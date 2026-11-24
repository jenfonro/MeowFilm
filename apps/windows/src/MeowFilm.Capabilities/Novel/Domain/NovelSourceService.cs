using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class NovelSourceProjectionChange(IReadOnlySet<string> retired) : EventArgs
{
    internal IReadOnlySet<string> Retired => retired;
}

/// <summary>Native source configuration and revision-bound execution through the runtime.</summary>
public sealed class NovelSourceService(IRuntimeClient runtime, IStorageDatabase store)
{
    private const int CoverCacheMaxEntries = 128;
    private const long CoverCacheMaxBytes = 24L * 1024 * 1024;

    private readonly record struct CoverCacheKey(string InstallationId, long Revision, string BookUrl, string Url, string Slot);
    private sealed class CoverCacheNode(CoverCacheKey key, NovelImagePixels pixels)
    {
        internal CoverCacheKey Key => key;
        internal NovelImagePixels Pixels => pixels;
        internal long ByteSize => pixels.Pixels.Length;
    }
    private sealed class CoverInFlight(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation => cancellation;
        internal Task<NovelImagePixels> Task { get; set; } = null!;
        internal int Waiters;
        internal int ActiveCancels;
        internal bool Completed;
        internal bool Disposed;

        internal void TryDisposeLocked(out CancellationTokenSource? toDispose)
        {
            if (Completed && Waiters == 0 && ActiveCancels == 0 && !Disposed)
            {
                Disposed = true;
                toDispose = cancellation;
                return;
            }
            toDispose = null;
        }
    }

    private sealed class SourceLease
    {
        private readonly CancellationTokenSource cts = new();
        private int refs = 1;
        private bool disposed;

        internal CancellationTokenSource CreateLinkedLocked(CancellationToken outer)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, cts.Token);
            refs++;
            return linked;
        }

        internal void Release(object gate)
        {
            bool shouldDispose = false;
            lock (gate)
            {
                if (--refs > 0 || disposed) return;
                disposed = true;
                shouldDispose = true;
            }
            if (shouldDispose) cts.Dispose();
        }

        internal void CancelAndReleaseOwner(object gate)
        {
            try { cts.Cancel(); }
            finally { Release(gate); }
        }
    }

    private readonly struct LinkedLease(CancellationTokenSource cts, SourceLease? lease, object gate) : IDisposable
    {
        internal CancellationToken Token => cts.Token;
        internal bool IsCancellationRequested => cts.IsCancellationRequested;
        internal void Cancel() => cts.Cancel();
        public void Dispose()
        {
            cts.Dispose();
            lease?.Release(gate);
        }
    }

    private sealed class ActiveWeb(string id, string installationId, LinkedLease lease)
    {
        private int refs = 1;
        private bool disposed;
        internal string Id => id;
        internal string InstallationId => installationId;
        internal LinkedLease Lease => lease;
        internal Task<object?> Completion { get; set; } = Task.FromResult<object?>(null);

        internal bool TryAcquireLocked()
        {
            if (disposed) return false;
            refs++;
            return true;
        }

        internal void Cancel() => lease.Cancel();

        internal void Release(object gate)
        {
            bool shouldDispose = false;
            lock (gate)
            {
                if (--refs > 0 || disposed) return;
                disposed = true;
                shouldDispose = true;
            }
            if (shouldDispose) lease.Dispose();
        }
    }

    private readonly NovelSourceConfiguration configuration = new(store);
    private readonly NovelWork work = new();
    private readonly NovelWork visuals = new();
    private readonly NovelWork applications = new();
    private readonly SemaphoreSlim applying = new(1, 1);
    private IRuntimeClient? appliedClient;
    private CancellationToken appliedRevoked;
    private long appliedRevision;
    private readonly object gate = new();
    private readonly object coverCacheGate = new();
    private readonly Dictionary<CoverCacheKey, LinkedListNode<CoverCacheNode>> coverCache = [];
    private readonly LinkedList<CoverCacheNode> coverCacheLru = [];
    private readonly Dictionary<CoverCacheKey, CoverInFlight> coverInFlight = [];
    private long coverCacheBytes;
    private readonly HashSet<string> imageReferences = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly Dictionary<string, SourceLease> leases = [];
    private readonly Dictionary<string, (CancellationToken Token, NovelSource Source)> invoking = [];
    private IReadOnlyList<NovelSource> sources = [];
    private bool initialized, shuttingDown;
    private int activeCalls;
    private Task? webPump;
    private Task? retiringPump;
    private CancellationTokenSource? pumpCancellation;
    private ActiveWeb? activeWeb;
    public IReadOnlyList<NovelSource> Sources => sources;
    public event EventHandler? Changed;
    public event EventHandler<string>? ApplyFailed;
    public Func<JsonElement, CancellationToken, Task<object?>>? InteractAsync { get; set; }
    private async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken token, bool acceptReceipt = false)
    {
        var envelope = await runtime.CallAsync("source.runtime." + operation, payload, token);
        if (!acceptReceipt) token.ThrowIfCancellationRequested();
        if (!envelope.GetProperty("ok").GetBoolean()) throw new BridgeException(envelope.GetProperty("code").GetString()!);
        return envelope.GetProperty("value").Clone();
    }
    public Task RefreshAsync(CancellationToken token = default) => RefreshAsync(false, token);
    internal Task EnsureReadyAsync(CancellationToken token = default) =>
        initialized ? Task.CompletedTask : RefreshAsync(true, token);
    internal Task EnsureExecutionReadyAsync(CancellationToken token) => work.Run(async ct =>
    {
        await EnsureReadyAsync(ct);
        await ApplyLatestAsync(ct);
        return true;
    }, token);
    private Task RefreshAsync(bool onlyMissing, CancellationToken token) => work.Run(async ct =>
    {
        await changes.WaitAsync(ct);
        try
        {
            if (!onlyMissing || !initialized)
            {
                await configuration.LoadAsync(ct, reload: !onlyMissing);
                PublishConfiguration();
            }
            return true;
        }
        finally { changes.Release(); }
    }, token);
    private void PublishConfiguration()
    {
        Publish(configuration.Snapshot.Sources.Select(item =>
        {
            var source = item.Source();
            var previousSameRev = sources.FirstOrDefault(s => s.InstallationId == item.InstallationId && s.Revision == item.Revision);
            if (previousSameRev is not null)
            {
                return source with
                {
                    Actions = previousSameRev.Actions,
                    ErrorCode = previousSameRev.ErrorCode,
                    Weight = previousSameRev.Weight,
                    LastUpdateTime = previousSameRev.LastUpdateTime,
                    RespondTime = previousSameRev.RespondTime,
                    HasExplore = previousSameRev.HasExplore
                };
            }
            if (item.Enabled)
            {
                var previousById = sources.FirstOrDefault(s => s.InstallationId == item.InstallationId);
                if (previousById is not null)
                {
                    return source with
                    {
                        Actions = previousById.Actions,
                        Weight = previousById.Weight,
                        LastUpdateTime = previousById.LastUpdateTime,
                        RespondTime = previousById.RespondTime,
                        HasExplore = previousById.HasExplore
                    };
                }
            }
            return source;
        }).ToArray());
    }
    public async Task StartAsync(CancellationToken token = default)
    {
        await EnsureReadyAsync(token); ScheduleApplication(false);
    }
    private void ScheduleApplication(bool reportFailure)
    {
        _ = Apply();
        async Task Apply()
        {
            try
            {
                await applications.Run(async ct => { await ApplyLatestAsync(ct); return true; });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.Novel, "sources", "apply.error", new { reportFailure }, error: error);
                if (reportFailure) ApplyFailed?.Invoke(this, "书源配置已保存，运行器暂未生效");
            }
        }
    }
    private async Task ApplyLatestAsync(CancellationToken token, bool force = false)
    {
        await applying.WaitAsync(token);
        try
        {
            while (true)
            {
                var next = configuration.Snapshot;
                var connection = runtime is IRuntimeConnectionSource capture ? await capture.CaptureConnectionAsync(token) : null;
                var client = connection?.Client ?? runtime;
                var revoked = connection?.Revoked ?? default;
                if (!force && ReferenceEquals(appliedClient, client) && !appliedRevoked.IsCancellationRequested &&
                    appliedRevision == next.Revision) return;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, revoked);
                var envelope = await client.CallAsync("source.runtime.library.apply",
                    NovelJson.Value(new { revision = next.Revision, sources = next.Sources }), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (!envelope.GetProperty("ok").GetBoolean()) throw new BridgeException(envelope.GetProperty("code").GetString()!);
                var value = envelope.GetProperty("value");
                if (value.GetProperty("revision").GetInt64() != next.Revision) throw new BridgeException("INVALID_RESPONSE");
                if (!ReferenceEquals(configuration.Snapshot, next)) { force = true; continue; }
                var projected = value.GetProperty("sources").EnumerateArray().Select(source => new
                {
                    InstallationId = source.GetProperty("installationId").GetString(),
                    Revision = source.GetProperty("revision").GetInt64(),
                    Actions = NovelJson.Decode<string[]>(source.GetProperty("actions")),
                    ErrorCode = source.TryGetProperty("errorCode", out var error) && error.ValueKind != JsonValueKind.Null
                        ? error.GetString() : null
                }).ToArray();
                if (projected.Length != next.Sources.Length || projected.Any(source =>
                    !next.Sources.Any(item => item.InstallationId == source.InstallationId && item.Revision == source.Revision)))
                    throw new BridgeException("INVALID_RESPONSE");
                appliedClient = client; appliedRevoked = revoked; appliedRevision = next.Revision;
                Publish(next.Sources.Select(item =>
                {
                    var applied = projected.Single(source => source.InstallationId == item.InstallationId);
                    var source = item.Enabled && applied.ErrorCode is null ? item.SourceFromDefinition() : item.Source();
                    return source with { Actions = applied.Actions, ErrorCode = applied.ErrorCode };
                }).ToArray());
                if (sources.Any(source => source.Enabled && source.ErrorCode is not null))
                    ApplyFailed?.Invoke(this, "书源配置已保存，部分书源未能生效");
                return;
            }
        }
        finally { applying.Release(); }
    }
    private void Publish(IReadOnlyList<NovelSource> values)
    {
        bool initial = !initialized; initialized = true;
        values = values.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.CurrentCulture).ThenBy(s => s.InstallationId, StringComparer.Ordinal).ToArray();
        if (!initial && sources.Count == values.Count && sources.Zip(values).All(pair =>
            (pair.First with { Actions = pair.Second.Actions }) == pair.Second && pair.First.Actions.SequenceEqual(pair.Second.Actions))) return;
        var previous = sources;
        sources = values;
        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prior in previous)
        {
            var next = values.FirstOrDefault(s => s.InstallationId == prior.InstallationId);
            if (next is null || !next.Enabled || next.Revision != prior.Revision)
            { retired.Add(prior.InstallationId); Retire(prior.InstallationId); }
        }
        InvalidateCoverCache(values);
        Changed?.Invoke(this, new NovelSourceProjectionChange(retired));
    }
    private void Retire(string id)
    {
        SourceLease? lease;
        lock (gate)
        {
            if (!leases.Remove(id, out lease)) return;
        }
        lease.CancelAndReleaseOwner(gate);
    }
    private LinkedLease LinkSourceToken(string installationId, CancellationToken outer)
    {
        lock (gate)
        {
            if (shuttingDown) throw new OperationCanceledException(outer);
            if (!leases.TryGetValue(installationId, out var lease))
                leases[installationId] = lease = new();
            return new LinkedLease(lease.CreateLinkedLocked(outer), lease, gate);
        }
    }
    public NovelSource Require(NovelBook book) => sources.FirstOrDefault(s => s.InstallationId == book.InstallationId && s.Enabled)
        ?? throw new BridgeException("SOURCE_DISABLED");
    internal async Task<NovelSource> RequireAsync(NovelBook book, CancellationToken token)
    {
        await EnsureReadyAsync(token); return Require(book);
    }
    public bool IsCurrent(NovelSource source) => sources.Any(s => s.InstallationId == source.InstallationId &&
        s.Revision == source.Revision && s.Enabled);
    public Task<JsonElement> InvokeAsync(NovelSource source, string action, object payload, CancellationToken token = default) => work.Run(async ct =>
    {
        await EnsureReadyAsync(ct);
        await ApplyLatestAsync(ct, force: sources.Any(s => s.InstallationId == source.InstallationId && s.ErrorCode is not null));
        if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
        if (sources.First(s => s.InstallationId == source.InstallationId).ErrorCode is { } code) throw new BridgeException(code);
        using var linked = LinkSourceToken(source.InstallationId, ct);
        if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
        var invocation = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            invoking[invocation] = (linked.Token, source);
            activeCalls++;
            if (webPump is null || webPump.IsCompleted)
            {
                var prior = webPump ?? retiringPump;
                var priorCancellation = pumpCancellation;
                var nextCancellation = new CancellationTokenSource();
                pumpCancellation = nextCancellation;
                webPump = RestartPumpAsync(prior, priorCancellation, nextCancellation.Token);
                retiringPump = null;
            }
        }
        try
        {
            var value = await CallAsync("library.invoke", new { installationId = source.InstallationId,
                revision = source.Revision, action, payload }, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
            return value;
        }
        finally
        {
            Task? retiring = null;
            CancellationTokenSource? retiringCancel = null;
            bool shouldDrainWeb = false;
            lock (gate)
            {
                invoking.Remove(invocation);
                if (--activeCalls == 0)
                {
                    retiring = webPump;
                    retiringCancel = pumpCancellation;
                    webPump = null;
                    pumpCancellation = null;
                    if (retiring is not null) retiringPump = retiring;
                }
                else
                {
                    shouldDrainWeb = true;
                }
            }
            if (retiringCancel is not null)
            {
                try
                {
                    retiringCancel.Cancel();
                    if (retiring is not null) await retiring;
                }
                finally
                {
                    retiringCancel.Dispose();
                    lock (gate)
                    {
                        if (ReferenceEquals(retiringPump, retiring)) retiringPump = null;
                    }
                }
            }
            else if (shouldDrainWeb)
            {
                await DrainRetiredWebAsync(source.InstallationId);
            }
        }
    }, token);
    private async Task RestartPumpAsync(Task? prior, CancellationTokenSource? priorCancellation, CancellationToken token)
    {
        try
        {
            if (prior is not null) await prior;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Novel, "web.pump", "restart.prior_error", error: error);
        }
        finally
        {
            priorCancellation?.Dispose();
        }
        // A replacement may invoke its source immediately, but its Web surface
        // cannot overlap a still-closing interaction from the retired pump.
        await PumpWebAsync(token);
    }
    private async Task DrainRetiredWebAsync(string installationId)
    {
        ActiveWeb? current;
        lock (gate) current = activeWeb;
        if (current is null || current.InstallationId != installationId) return;
        bool pending;
        try
        {
            var result = await CallAsync("web.pending", new { }, CancellationToken.None);
            pending = result.GetProperty("requests").EnumerateArray().Any(r => NovelJson.Text(r, "id") == current.Id);
        }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Novel, "web.pump", "drain.pending_error", new { installationId }, error: error);
            pending = false;
        }
        if (pending) return;
        bool acquired;
        lock (gate) acquired = ReferenceEquals(activeWeb, current) && current.TryAcquireLocked();
        if (!acquired) return;
        try
        {
            current.Cancel();
            // Another unrelated source call must not let this caller acknowledge
            // cancellation before its retired browser has actually closed.
            await current.Completion;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Novel, "web.pump", "drain.completion_error", new { installationId, requestId = current.Id }, error: error);
        }
        finally
        {
            current.Release(gate);
        }
    }
    private async Task PumpWebAsync(CancellationToken token)
    {
        int idleDelayMs = 200;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var value = await CallAsync("web.pending", new { }, token);
                bool sawRequest = false;
                bool first = true;
                foreach (var request in value.GetProperty("requests").EnumerateArray())
                {
                    sawRequest = true;
                    idleDelayMs = 200;
                    string id = NovelJson.Text(request, "id"), installation = NovelJson.Text(request, "installationId");
                    // Earlier interactions can keep this snapshot pending for a long time.
                    // Revalidate admission before presenting another source's surface.
                    if (!first)
                    {
                        var current = await CallAsync("web.pending", new { }, token);
                        if (!current.GetProperty("requests").EnumerateArray().Any(r => NovelJson.Text(r, "id") == id)) continue;
                    }
                    first = false;
                    (CancellationToken Token, NovelSource Source) call;
                    LinkedLease interaction;
                    lock (gate)
                    {
                        call = invoking.Values.FirstOrDefault(v => v.Source.InstallationId == installation && !v.Token.IsCancellationRequested);
                        interaction = leases.TryGetValue(installation, out var lease)
                            ? new LinkedLease(lease.CreateLinkedLocked(token), lease, gate)
                            : new LinkedLease(CancellationTokenSource.CreateLinkedTokenSource(token), null, gate);
                    }
                    var webGate = new ActiveWeb(id, installation, interaction);
                    object? result = null; bool cancelled = call.Source is null || InteractAsync is null;
                    try
                    {
                        try
                        {
                            if (!cancelled)
                            {
                                var ui = InteractAsync!(request.Clone(), interaction.Token);
                                webGate.Completion = ui;
                                lock (gate) activeWeb = webGate;
                                try
                                {
                                    while (!ui.IsCompleted)
                                    {
                                        await Task.WhenAny(ui, Task.Delay(400, interaction.Token));
                                        if (ui.IsCompleted) break;
                                        var pending = await CallAsync("web.pending", new { }, interaction.Token);
                                        if (!pending.GetProperty("requests").EnumerateArray().Any(r => NovelJson.Text(r, "id") == id))
                                        { webGate.Cancel(); break; }
                                    }
                                }
                                finally
                                {
                                    if (!ui.IsCompleted) webGate.Cancel();
                                    // A retired request includes the browser's physical cleanup.
                                    try { result = await ui; }
                                    finally
                                    {
                                        lock (gate)
                                        {
                                            if (ReferenceEquals(activeWeb, webGate)) activeWeb = null;
                                        }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { cancelled = true; }
                        catch (Exception error)
                        {
                            cancelled = true;
                            DebugLog.Warn(DebugCategories.Novel, "web.pump", "interaction.error", new { id, installationId = installation }, error: error);
                        }
                        cancelled |= interaction.IsCancellationRequested || call.Source is null || !IsCurrent(call.Source);
                    }
                    finally
                    {
                        webGate.Release(gate);
                    }
                    // The runtime also fences the request against its retired source generation.
                    try { await CallAsync("web.respond", cancelled ? new { id, cancelled = true } : (object)new { id, result }, CancellationToken.None); }
                    catch (BridgeException error) when (error.Code == "STALE_SOURCE") { }
                    catch (Exception error)
                    {
                        DebugLog.Warn(DebugCategories.Novel, "web.pump", "respond.error", new { id, installationId = installation, cancelled }, error: error);
                    }
                }
                await Task.Delay(idleDelayMs, token);
                if (!sawRequest) idleDelayMs = Math.Min(800, idleDelayMs + 150);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Novel, "web.pump", "loop.stopped", new { code = error is BridgeException b ? b.Code : error.GetType().Name }, error: error);
        }
    }
    public Task<JsonElement> ImportAsync(string? text = null, string? url = null, string? fileName = null,
        CancellationToken token = default) => work.Run(async ct =>
    {
        var input = await ImportTextAsync(text, url, ct);
        await changes.WaitAsync(ct);
        try
        {
            await configuration.LoadAsync(ct);
            var value = await configuration.ImportAsync(input, fileName, url, ct);
            PublishConfiguration(); ScheduleApplication(true); return value;
        }
        finally { changes.Release(); }
    }, token);
    public Task<JsonElement> PreviewImportAsync(string? text = null, string? url = null, string? fileName = null,
        CancellationToken token = default) => work.Run(async ct =>
    {
        await configuration.LoadAsync(ct);
        return configuration.Preview(await ImportTextAsync(text, url, ct), fileName, url);
    }, token);
    private static Task<string> ImportTextAsync(string? text, string? url, CancellationToken token)
    {
        if ((text is null) == (url is null)) throw new BridgeException("INVALID_REQUEST");
        if (text is not null) return Task.FromResult(string.IsNullOrWhiteSpace(text) ? throw new BridgeException("INVALID_REQUEST") : text);
        return FetchTextUrlAsync(url!, token);
    }
    private static readonly HttpClient TextHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true, MaxAutomaticRedirections = 10,
        UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<string> FetchTextUrlAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new BridgeException("INVALID_REQUEST");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await TextHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new BridgeException("UPSTREAM_RESPONSE");
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            try { return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (System.Text.DecoderFallbackException) { throw new BridgeException("SOURCE_ENCODING"); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new BridgeException("TIMEOUT"); }
    }
    public Task<JsonElement> GetAsync(NovelSource source, CancellationToken token = default) => work.Run(async ct =>
    {
        await configuration.LoadAsync(ct); var item = configuration.Require(source);
        return NovelJson.Value(new { source = sources.FirstOrDefault(value => value.InstallationId == item.InstallationId && value.Revision == item.Revision) ?? item.Source(), definition = item.Definition });
    }, token);
    public Task<JsonElement> SaveAsync(NovelSource source, string definition, CancellationToken token = default) =>
        EditAsync(async ct =>
        {
            var saved = await configuration.SaveAsync(source, source.Protocol, definition, ct);
            return NovelJson.Value(new { source = saved.SourceFromDefinition() });
        }, token);
    public Task<JsonElement> SaveDraftAsync(string protocol, string definition, CancellationToken token = default) =>
        EditAsync(async ct =>
        {
            var saved = await configuration.SaveAsync(null, protocol, definition, ct);
            return NovelJson.Value(new { source = saved.SourceFromDefinition() });
        }, token);
    public Task<JsonElement> UpdateAsync(NovelSource source, object patch, CancellationToken token = default) =>
        EditAsync(async ct => { await configuration.UpdateAsync(source, NovelJson.Value(patch), ct);
            return NovelJson.Value(new { source = configuration.Snapshot.Sources.Single(s => s.InstallationId == source.InstallationId).Source() }); }, token);
    public Task<JsonElement> RemoveAsync(NovelSource source, CancellationToken token = default) =>
        EditAsync(async ct => { await configuration.RemoveAsync(source, ct); return NovelJson.Value(new { removed = true }); }, token);
    public Task<JsonElement> ExportAsync(string[] ids, CancellationToken token = default) => work.Run(async ct =>
    {
        await configuration.LoadAsync(ct);
        string text = "[" + string.Join(',', ids.Select(id => configuration.Snapshot.Sources.FirstOrDefault(s =>
            s.InstallationId == id)?.Definition ?? throw new BridgeException("SOURCE_NOT_FOUND"))) + "]";
        return NovelJson.Value(new { text });
    }, token);
    private Task<JsonElement> EditAsync(Func<CancellationToken, Task<JsonElement>> update, CancellationToken token) => work.Run(async ct =>
    {
        await changes.WaitAsync(ct);
        try
        {
            await configuration.LoadAsync(ct);
            var value = await update(ct);
            PublishConfiguration(); ScheduleApplication(true);
            return value;
        }
        finally { changes.Release(); }
    }, token);
    public Task<string> ImageAsync(NovelSource source, string url, string bookUrl, string kind, CancellationToken token) => work.Run(async ct =>
    {
        await EnsureReadyAsync(ct);
        await ApplyLatestAsync(ct, force: sources.Any(s => s.InstallationId == source.InstallationId && s.ErrorCode is not null));
        if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
        if (sources.First(s => s.InstallationId == source.InstallationId).ErrorCode is { } code) throw new BridgeException(code);
        using var linked = LinkSourceToken(source.InstallationId, ct);
        if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
        var value = await CallAsync("library.image", new { installationId = source.InstallationId, revision = source.Revision,
            url, bookUrl, kind }, linked.Token, acceptReceipt: true);
        string reference = NovelJson.Text(value, "reference");
        lock (gate) imageReferences.Add(reference);
        if (!IsCurrent(source) || linked.IsCancellationRequested)
        {
            await ReleaseImageAsync(reference); throw new OperationCanceledException(linked.Token);
        }
        return reference;
    }, token);
    internal bool TryGetCachedCover(NovelBook book, string slot, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NovelImagePixels? pixels)
    {
        var source = sources.FirstOrDefault(s => s.InstallationId == book.InstallationId && s.Enabled);
        if (source is null)
        {
            pixels = null;
            return false;
        }
        var key = new CoverCacheKey(source.InstallationId, source.Revision, book.BookUrl, book.CoverUrl, slot);
        lock (coverCacheGate)
        {
            if (coverCache.TryGetValue(key, out var node))
            {
                coverCacheLru.Remove(node);
                coverCacheLru.AddFirst(node);
                pixels = node.Value.Pixels;
                return true;
            }
        }
        pixels = null;
        return false;
    }
    internal async Task<NovelImagePixels> LoadCoverAsync(NovelBook book, string slot, CancellationToken token)
    {
        var source = await RequireAsync(book, token);
        var key = new CoverCacheKey(source.InstallationId, source.Revision, book.BookUrl, book.CoverUrl, slot);
        CoverInFlight inFlight;
        lock (coverCacheGate)
        {
            if (coverCache.TryGetValue(key, out var cached))
            {
                coverCacheLru.Remove(cached);
                coverCacheLru.AddFirst(cached);
                return cached.Value.Pixels;
            }
            if (coverInFlight.TryGetValue(key, out var existing))
            {
                inFlight = existing;
                inFlight.Waiters++;
            }
            else
            {
                inFlight = new CoverInFlight(new CancellationTokenSource()) { Waiters = 1 };
                coverInFlight[key] = inFlight;
                inFlight.Task = LoadCoverSingleFlightAsync(book, source, key, inFlight);
            }
        }
        try
        {
            return await inFlight.Task.WaitAsync(token);
        }
        finally
        {
            bool cancelShared = false;
            CancellationTokenSource? toDispose = null;
            lock (coverCacheGate)
            {
                inFlight.Waiters--;
                if (inFlight.Waiters == 0 && !inFlight.Completed)
                {
                    cancelShared = true;
                    inFlight.ActiveCancels++;
                    if (coverInFlight.TryGetValue(key, out var current) && ReferenceEquals(current, inFlight))
                        coverInFlight.Remove(key);
                }
                else
                {
                    inFlight.TryDisposeLocked(out toDispose);
                }
            }
            if (cancelShared)
            {
                try { inFlight.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                finally
                {
                    lock (coverCacheGate)
                    {
                        inFlight.ActiveCancels--;
                        inFlight.TryDisposeLocked(out toDispose);
                    }
                }
            }
            toDispose?.Dispose();
        }
    }
    private async Task<NovelImagePixels> LoadCoverSingleFlightAsync(
        NovelBook book,
        NovelSource source,
        CoverCacheKey key,
        CoverInFlight entry)
    {
        NovelImagePixels? decoded = null;
        try
        {
            await Task.Yield();
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            await VisualAsync(book, async ct =>
            {
                byte[] bytes;
                string? reference = null;
                try
                {
                    reference = await ImageAsync(source, book.CoverUrl, book.BookUrl, key.Slot, ct);
                    bytes = await ReadImageAsync(reference, ct);
                }
                finally
                {
                    if (reference is not null)
                    {
                        try { await ReleaseImageAsync(reference); }
                        catch (Exception releaseError)
                        {
                            DebugLog.Warn(DebugCategories.Novel, "cover.image", "release.failed",
                                new { reference, url = book.CoverUrl, slot = key.Slot }, error: releaseError);
                        }
                    }
                }
                decoded = await Task.Run(() => NovelImagePixels.Decode(bytes, 240), ct);
                ct.ThrowIfCancellationRequested();
            }, entry.Cancellation.Token);
            if (decoded is null) throw new OperationCanceledException(entry.Cancellation.Token);
            if (IsCurrent(source)) PutCoverCache(key, decoded);
            return decoded;
        }
        finally
        {
            CancellationTokenSource? toDispose;
            lock (coverCacheGate)
            {
                entry.Completed = true;
                if (coverInFlight.TryGetValue(key, out var existing) && ReferenceEquals(existing, entry))
                    coverInFlight.Remove(key);
                entry.TryDisposeLocked(out toDispose);
            }
            toDispose?.Dispose();
        }
    }
    private void PutCoverCache(CoverCacheKey key, NovelImagePixels pixels)
    {
        lock (coverCacheGate)
        {
            if (coverCache.TryGetValue(key, out var existing))
            {
                coverCacheBytes -= existing.Value.ByteSize;
                coverCacheLru.Remove(existing);
                coverCache.Remove(key);
            }
            var node = new LinkedListNode<CoverCacheNode>(new(key, pixels));
            coverCacheLru.AddFirst(node);
            coverCache[key] = node;
            coverCacheBytes += node.Value.ByteSize;
            while (coverCache.Count > CoverCacheMaxEntries || (coverCacheBytes > CoverCacheMaxBytes && coverCache.Count > 1))
            {
                var tail = coverCacheLru.Last;
                if (tail is null) break;
                coverCacheLru.RemoveLast();
                coverCache.Remove(tail.Value.Key);
                coverCacheBytes -= tail.Value.ByteSize;
            }
        }
    }
    private void InvalidateCoverCache(IReadOnlyList<NovelSource> activeSources)
    {
        var activePairs = new HashSet<(string InstallationId, long Revision)>();
        foreach (var source in activeSources)
        {
            if (source.Enabled) activePairs.Add((source.InstallationId, source.Revision));
        }
        List<CoverInFlight>? toCancel = null;
        lock (coverCacheGate)
        {
            foreach (var pair in coverCache.ToArray())
            {
                if (activePairs.Contains((pair.Key.InstallationId, pair.Key.Revision))) continue;
                coverCacheBytes -= pair.Value.Value.ByteSize;
                coverCacheLru.Remove(pair.Value);
                coverCache.Remove(pair.Key);
            }
            foreach (var pair in coverInFlight.ToArray())
            {
                if (activePairs.Contains((pair.Key.InstallationId, pair.Key.Revision))) continue;
                coverInFlight.Remove(pair.Key);
                pair.Value.ActiveCancels++;
                (toCancel ??= []).Add(pair.Value);
            }
        }
        CancelCoverInFlights(toCancel);
    }
    private void ClearCoverCache()
    {
        List<CoverInFlight>? toCancel = null;
        lock (coverCacheGate)
        {
            coverCache.Clear();
            coverCacheLru.Clear();
            coverCacheBytes = 0;
            foreach (var entry in coverInFlight.Values)
            {
                entry.ActiveCancels++;
                (toCancel ??= []).Add(entry);
            }
            coverInFlight.Clear();
        }
        CancelCoverInFlights(toCancel);
    }
    private void CancelCoverInFlights(List<CoverInFlight>? entries)
    {
        if (entries is null) return;
        foreach (var entry in entries)
        {
            try
            {
                try { entry.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            finally
            {
                CancellationTokenSource? toDispose;
                lock (coverCacheGate)
                {
                    entry.ActiveCancels--;
                    entry.TryDisposeLocked(out toDispose);
                }
                toDispose?.Dispose();
            }
        }
    }
    public async Task ReleaseImageAsync(string reference)
    {
        if (runtime is IRuntimeLifetime { ShutdownCompletion: { } shutdown })
            await shutdown;
        else
        {
            try { await CallAsync("library.releaseImage", new { reference }, CancellationToken.None); }
            catch (BridgeException error) when (error.Code == "CLOSED" &&
                runtime is IRuntimeLifetime { ShutdownCompletion: not null })
            {
                await ((IRuntimeLifetime)runtime).ShutdownCompletion!;
            }
        }
        lock (gate) imageReferences.Remove(reference);
    }
    internal Task VisualAsync(Func<CancellationToken, Task> action, CancellationToken token = default) => visuals.Run(async ct =>
    { await action(ct); return true; }, token);
    internal Task VisualAsync(NovelBook book, Func<CancellationToken, Task> action, CancellationToken token) => visuals.Run(async ct =>
    {
        var source = await RequireAsync(book, ct);
        using var linked = LinkSourceToken(source.InstallationId, ct);
        if (!IsCurrent(source)) throw new OperationCanceledException(linked.Token);
        await action(linked.Token); return true;
    }, token);
    public Task<byte[]> ReadImageAsync(string reference, CancellationToken token) =>
        runtime is IRuntimeImageClient images ? images.ReadImageAsync(reference, token) :
            throw new BridgeException("CAPABILITY_UNAVAILABLE");
    public async Task PrepareShutdownAsync()
    {
        string[] initialLeaseIds;
        lock (gate)
        {
            shuttingDown = true;
            initialLeaseIds = leases.Keys.ToArray();
        }
        ClearCoverCache();
        foreach (var id in initialLeaseIds) Retire(id);
        await Task.WhenAll(visuals.PauseAsync(), applications.PauseAsync(), work.PauseAsync());
        string[] remainingLeaseIds;
        string[] remainingImages;
        lock (gate)
        {
            remainingLeaseIds = leases.Keys.ToArray();
            remainingImages = imageReferences.ToArray();
        }
        foreach (var id in remainingLeaseIds) Retire(id);
        try { await Task.WhenAll(remainingImages.Select(ReleaseImageAsync)); }
        finally
        {
            Task? pump;
            Task? retiring;
            CancellationTokenSource? cancel;
            lock (gate)
            {
                pump = webPump;
                cancel = pumpCancellation;
                retiring = retiringPump;
                webPump = null;
                pumpCancellation = null;
                retiringPump = null;
            }
            try
            {
                cancel?.Cancel();
                if (pump is not null) await pump;
                if (retiring is not null && !ReferenceEquals(retiring, pump)) await retiring;
            }
            finally { cancel?.Dispose(); }
        }
    }
}
