using System.Text.Json;
using System.IO;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed class NovelSourceProjectionChange(IReadOnlySet<string> retired) : EventArgs
{
    internal IReadOnlySet<string> Retired => retired;
}

/// <summary>Native source configuration and revision-bound execution through the runtime.</summary>
public sealed class NovelSourceService(IRuntimeClient runtime, IDocumentStore store)
{
    private readonly NovelSourceConfiguration configuration = new(store);
    private readonly NovelWork work = new();
    private readonly NovelWork visuals = new();
    private readonly NovelWork applications = new();
    private readonly SemaphoreSlim applying = new(1, 1);
    private IRuntimeClient? appliedClient;
    private CancellationToken appliedRevoked;
    private long appliedRevision;
    private readonly HashSet<string> imageReferences = [];
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> leases = [];
    private readonly Dictionary<string, (CancellationToken Token, NovelSource Source)> invoking = [];
    private IReadOnlyList<NovelSource> sources = [];
    private bool initialized;
    private int activeCalls;
    private Task? webPump;
    private CancellationTokenSource? pumpCancellation;
    private sealed record ActiveWeb(string Id, string InstallationId, CancellationTokenSource Cancellation, Task<object?> Completion);
    private ActiveWeb? activeWeb;
    public IReadOnlyList<NovelSource> Sources => sources;
    public event EventHandler? Changed;
    public event EventHandler<string>? ApplyFailed;
    internal event EventHandler? Resumed;
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
            var previous = sources.FirstOrDefault(s => s.InstallationId == item.InstallationId && s.Revision == item.Revision);
            return previous is null ? source : source with { Actions = previous.Actions, ErrorCode = previous.ErrorCode };
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
            catch (Exception) { if (reportFailure) ApplyFailed?.Invoke(this, "书源配置已保存，运行器暂未生效"); }
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
                var projected = NovelJson.Decode<NovelSource[]>(value.GetProperty("sources"));
                if (projected.Length != next.Sources.Length || projected.Any(source =>
                    !next.Sources.Any(item => item.InstallationId == source.InstallationId && item.Revision == source.Revision)))
                    throw new BridgeException("INVALID_RESPONSE");
                appliedClient = client; appliedRevoked = revoked; appliedRevision = next.Revision;
                Publish(next.Sources.Select(item =>
                {
                    var applied = projected.Single(source => source.InstallationId == item.InstallationId);
                    return item.Source() with { Actions = applied.Actions, ErrorCode = applied.ErrorCode };
                }).ToArray());
                if (projected.Any(source => source.Enabled && source.ErrorCode is not null))
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
        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prior in sources)
        {
            var next = values.FirstOrDefault(s => s.InstallationId == prior.InstallationId);
            if (next is null || !next.Enabled || next.Revision != prior.Revision)
            { retired.Add(prior.InstallationId); Retire(prior.InstallationId); }
        }
        sources = values;
        Changed?.Invoke(this, new NovelSourceProjectionChange(retired));
    }
    private void Retire(string id)
    {
        if (!leases.Remove(id, out var lease)) return;
        lease.Cancel(); lease.Dispose();
    }
    private CancellationToken SourceToken(NovelSource source)
    {
        if (!leases.TryGetValue(source.InstallationId, out var lease)) leases[source.InstallationId] = lease = new();
        return lease.Token;
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SourceToken(source));
        var invocation = Guid.NewGuid().ToString("N");
        invoking[invocation] = (linked.Token, source);
        activeCalls++;
        if (webPump is null || webPump.IsCompleted || pumpCancellation!.IsCancellationRequested)
        {
            var prior = webPump; var priorCancellation = pumpCancellation;
            pumpCancellation = new();
            webPump = RestartPumpAsync(prior, priorCancellation, pumpCancellation.Token);
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
            invoking.Remove(invocation);
            if (--activeCalls == 0) { var retiring = webPump; pumpCancellation?.Cancel(); if (retiring is not null) await retiring; }
            else await DrainRetiredWebAsync(source.InstallationId);
        }
    }, token);
    private async Task RestartPumpAsync(Task? prior, CancellationTokenSource? priorCancellation, CancellationToken token)
    {
        try { if (prior is not null) await prior; }
        finally { priorCancellation?.Dispose(); }
        // A replacement may invoke its source immediately, but its Web surface
        // cannot overlap a still-closing interaction from the retired pump.
        await PumpWebAsync(token);
    }
    private async Task DrainRetiredWebAsync(string installationId)
    {
        var current = activeWeb;
        if (current is null || current.InstallationId != installationId) return;
        bool pending;
        try
        {
            var result = await CallAsync("web.pending", new { }, CancellationToken.None);
            pending = result.GetProperty("requests").EnumerateArray().Any(r => NovelJson.Text(r, "id") == current.Id);
        }
        catch (BridgeException) { pending = false; }
        if (pending || !ReferenceEquals(activeWeb, current)) return;
        current.Cancellation.Cancel();
        // Another unrelated source call must not let this caller acknowledge
        // cancellation before its retired browser has actually closed.
        try { await current.Completion; } catch { /* PumpWebAsync owns the response/error. */ }
    }
    private async Task PumpWebAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var value = await CallAsync("web.pending", new { }, token);
                foreach (var request in value.GetProperty("requests").EnumerateArray())
                {
                    string id = NovelJson.Text(request, "id"), installation = NovelJson.Text(request, "installationId");
                    // Earlier interactions can keep this snapshot pending for a long time.
                    // Revalidate admission before presenting another source's surface.
                    var current = await CallAsync("web.pending", new { }, token);
                    if (!current.GetProperty("requests").EnumerateArray().Any(r => NovelJson.Text(r, "id") == id)) continue;
                    var call = invoking.Values.FirstOrDefault(v => v.Source.InstallationId == installation && !v.Token.IsCancellationRequested);
                    var sourceToken = leases.TryGetValue(installation, out var lease) ? lease.Token : token;
                    using var interaction = CancellationTokenSource.CreateLinkedTokenSource(token, sourceToken);
                    object? result = null; bool cancelled = call.Source is null || InteractAsync is null;
                    try
                    {
                        if (!cancelled)
                        {
                            var ui = InteractAsync!(request.Clone(), interaction.Token);
                            activeWeb = new(id, installation, interaction, ui);
                            try
                            {
                                while (!ui.IsCompleted)
                                {
                                    await Task.WhenAny(ui, Task.Delay(250, interaction.Token));
                                    if (ui.IsCompleted) break;
                                    var pending = await CallAsync("web.pending", new { }, interaction.Token);
                                    if (!pending.GetProperty("requests").EnumerateArray().Any(r => NovelJson.Text(r, "id") == id))
                                    { interaction.Cancel(); break; }
                                }
                            }
                            finally
                            {
                                if (!ui.IsCompleted) interaction.Cancel();
                                // A retired request includes the browser's physical cleanup.
                                try { result = await ui; }
                                finally { activeWeb = null; }
                            }
                        }
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    catch { cancelled = true; }
                    cancelled |= interaction.IsCancellationRequested || call.Source is null || !IsCurrent(call.Source);
                    // The runtime also fences the request against its retired source generation.
                    try { await CallAsync("web.respond", cancelled ? new { id, cancelled = true } : (object)new { id, result }, CancellationToken.None); }
                    catch (BridgeException) { }
                }
                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (BridgeException) { /* The invocation delivers the fixed runtime error to its caller. */ }
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
    private static async Task<string> ImportTextAsync(string? text, string? url, CancellationToken token)
    {
        if ((text is null) == (url is null)) throw new BridgeException("INVALID_REQUEST");
        if (text is not null) return string.IsNullOrWhiteSpace(text) ? throw new BridgeException("INVALID_REQUEST") : text;
        static Uri Address(string value, Uri? origin = null)
        {
            if (!(origin is null ? Uri.TryCreate(value, UriKind.Absolute, out var result) :
                    Uri.TryCreate(origin, value, out result)) || result.Scheme is not ("http" or "https") || result.UserInfo.Length != 0)
                throw new BridgeException("INVALID_REQUEST");
            return result;
        }
        var uri = Address(url!); var visited = new HashSet<Uri>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            while (visited.Add(uri))
            {
                using var response = await client.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    uri = Address(response.Headers.Location?.OriginalString ?? throw new BridgeException("UPSTREAM_RESPONSE"), uri);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new BridgeException("UPSTREAM_RESPONSE");
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var output = new MemoryStream();
                byte[] buffer = new byte[65536];
                int count;
                while ((count = await body.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    if (output.Length + count > LocalDocumentStore.MaximumSourceBytes) throw new BridgeException("LIMIT");
                    output.Write(buffer, 0, count);
                }
                try { return new System.Text.UTF8Encoding(false, true).GetString(output.ToArray()).TrimStart('\uFEFF'); }
                catch (System.Text.DecoderFallbackException) { throw new BridgeException("SOURCE_ENCODING"); }
            }
            throw new BridgeException("UPSTREAM_RESPONSE");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new BridgeException("TIMEOUT"); }
    }
    public Task<JsonElement> GetAsync(NovelSource source, CancellationToken token = default) => work.Run(async ct =>
    {
        await configuration.LoadAsync(ct); var item = configuration.Require(source);
        return NovelJson.Value(new { source = item.Source(), definition = item.Definition });
    }, token);
    public Task<JsonElement> SaveAsync(NovelSource source, string definition, CancellationToken token = default) => work.Run(async ct =>
    {
        await changes.WaitAsync(ct);
        try
        {
            await configuration.LoadAsync(ct);
            var saved = await configuration.SaveAsync(source, source.Protocol, definition, ct);
            PublishConfiguration(); ScheduleApplication(true);
            return NovelJson.Value(new { source = saved.Source() });
        }
        finally { changes.Release(); }
    }, token);
    public Task<JsonElement> SaveDraftAsync(string protocol, string definition, CancellationToken token = default) =>
        EditAsync(async ct =>
        {
            var saved = await configuration.SaveAsync(null, protocol, definition, ct);
            return NovelJson.Value(new { source = saved.Source() });
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
        await ApplyLatestAsync(ct);
        if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SourceToken(source));
        var value = await CallAsync("library.image", new { installationId = source.InstallationId, revision = source.Revision,
            url, bookUrl, kind }, linked.Token, acceptReceipt: true);
        string reference = NovelJson.Text(value, "reference");
        imageReferences.Add(reference);
        if (!IsCurrent(source) || linked.IsCancellationRequested)
        {
            await ReleaseImageAsync(reference); throw new OperationCanceledException(linked.Token);
        }
        return reference;
    }, token);
    public async Task ReleaseImageAsync(string reference)
    {
        await CallAsync("library.releaseImage", new { reference }, CancellationToken.None);
        imageReferences.Remove(reference);
    }
    internal Task VisualAsync(Func<CancellationToken, Task> action, CancellationToken token = default) => visuals.Run(async ct =>
    { await action(ct); return true; }, token);
    internal Task VisualAsync(NovelBook book, Func<CancellationToken, Task> action, CancellationToken token) => visuals.Run(async ct =>
    {
        var source = await RequireAsync(book, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, SourceToken(source));
        await action(linked.Token); return true;
    }, token);
    public Task<byte[]> ReadImageAsync(string reference, CancellationToken token) =>
        runtime is IRuntimeImageClient images ? images.ReadImageAsync(reference, token) :
            throw new BridgeException("CAPABILITY_UNAVAILABLE");
    public async Task PrepareShutdownAsync()
    {
        foreach (var id in leases.Keys.ToArray()) Retire(id);
        await visuals.PauseAsync();
        await applications.PauseAsync();
        await work.PauseAsync();
        try { await Task.WhenAll(imageReferences.ToArray().Select(ReleaseImageAsync)); }
        finally
        {
            pumpCancellation?.Cancel();
            try { if (webPump is not null) await webPump; }
            finally { pumpCancellation?.Dispose(); pumpCancellation = null; webPump = null; }
        }
    }
    public void Resume() { work.Resume(); visuals.Resume(); applications.Resume(); appliedRevision = 0; Resumed?.Invoke(this, EventArgs.Empty); }
}
