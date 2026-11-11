using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed class NovelSourceProjectionChange(bool initial) : EventArgs
{
    internal bool Initial => initial;
}

/// <summary>Projection of the runtime-owned source repository, with revision-bound invocation leases.</summary>
public sealed class NovelSourceService(IRuntimeClient runtime)
{
    private readonly NovelWork work = new();
    private readonly NovelWork visuals = new();
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
                var result = await CallAsync("library.list", new { }, ct);
                Publish(NovelJson.Decode<NovelSource[]>(result.GetProperty("sources")));
            }
            return true;
        }
        finally { changes.Release(); }
    }, token);
    private void Publish(IReadOnlyList<NovelSource> values)
    {
        bool initial = !initialized; initialized = true;
        values = values.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.CurrentCulture).ThenBy(s => s.InstallationId, StringComparer.Ordinal).ToArray();
        if (sources.Count == values.Count && sources.Zip(values).All(pair =>
            (pair.First with { Actions = pair.Second.Actions }) == pair.Second && pair.First.Actions.SequenceEqual(pair.Second.Actions))) return;
        foreach (var prior in sources)
        {
            var next = values.FirstOrDefault(s => s.InstallationId == prior.InstallationId);
            if (next is null || !next.Enabled || next.Revision != prior.Revision) Retire(prior.InstallationId);
        }
        sources = values;
        Changed?.Invoke(this, new NovelSourceProjectionChange(initial));
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
        if (!IsCurrent(source)) throw new BridgeException("STALE_SOURCE");
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
        CancellationToken token = default)
    {
        var payload = new Dictionary<string, object>();
        if (text is not null) payload["text"] = text;
        if (url is not null) payload["url"] = url;
        if (fileName is not null) payload["fileName"] = fileName;
        return EditAsync("library.import", payload, token);
    }
    public Task<JsonElement> GetAsync(NovelSource source, CancellationToken token = default) => work.Run(ct =>
        CallAsync("library.get", new { installationId = source.InstallationId }, ct), token);
    public Task<JsonElement> SaveAsync(NovelSource source, string definition, CancellationToken token = default) =>
        EditAsync("library.save", new { installationId = source.InstallationId, expectedRevision = source.Revision, definition }, token);
    public Task<JsonElement> UpdateAsync(NovelSource source, object patch, CancellationToken token = default) =>
        EditAsync("library.update", new { installationId = source.InstallationId, expectedRevision = source.Revision, patch }, token);
    public Task<JsonElement> RemoveAsync(NovelSource source, CancellationToken token = default) =>
        EditAsync("library.remove", new { installationId = source.InstallationId, expectedRevision = source.Revision }, token);
    public Task<JsonElement> ExportAsync(string[] ids, CancellationToken token = default) => work.Run(ct =>
        CallAsync("library.export", new { installationIds = ids }, ct), token);
    private Task<JsonElement> EditAsync(string operation, object payload, CancellationToken token) => work.Run(async ct =>
    {
        await changes.WaitAsync(ct);
        try
        {
            var value = await CallAsync(operation, payload, ct);
            var list = await CallAsync("library.list", new { }, ct);
            Publish(NovelJson.Decode<NovelSource[]>(list.GetProperty("sources")));
            return value;
        }
        finally { changes.Release(); }
    }, token);
    public Task<string> ImageAsync(NovelSource source, string url, string bookUrl, string kind, CancellationToken token) => work.Run(async ct =>
    {
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
        await work.PauseAsync();
        try { await Task.WhenAll(imageReferences.ToArray().Select(ReleaseImageAsync)); }
        finally
        {
            pumpCancellation?.Cancel();
            try { if (webPump is not null) await webPump; }
            finally { pumpCancellation?.Dispose(); pumpCancellation = null; webPump = null; }
        }
    }
    public void Resume() { work.Resume(); visuals.Resume(); Resumed?.Invoke(this, EventArgs.Empty); }
}
