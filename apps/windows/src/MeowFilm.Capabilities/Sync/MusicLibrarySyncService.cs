using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities.Sync;

/// <summary>One coordinator per library owner. Borrows owners/transport/runtime; no account writer or second library store.</summary>
public sealed class MusicLibrarySyncService : IAsyncDisposable
{
    public const string ContractVersion = "1.1.0";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly MusicLibraryOwner library;
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformHttp http;
    private readonly MusicPlatformClientContextOwner clients;
    private readonly MusicNeteaseTokenProvider neteaseTokens;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<byte[]>>? cloud;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly SemaphoreSlim controls = new(1, 1);
    private CancellationTokenSource? schedulerCancellation, roundCancellation;
    private Task? scheduler;
    private Task<JsonElement>? round;
    private long generation;
    private bool closed, stopping, started;
    public event Action? StateChanged;

    internal MusicLibrarySyncService(MusicLibraryOwner library, IMusicPlatformSessionSource sessions, MusicPlatformHttp http,
        MusicPlatformClientContextOwner clients, IRuntimeClient tokenRuntime)
        : this(library, sessions, http, clients, tokenRuntime, null) { }
    internal MusicLibrarySyncService(MusicLibraryOwner library, IMusicPlatformSessionSource sessions, MusicPlatformHttp http,
        MusicPlatformClientContextOwner clients, IRuntimeClient tokenRuntime,
        Func<HttpRequestMessage, CancellationToken, Task<byte[]>>? kugouCloudTransport, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(library); ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(http);
        this.library = library; this.sessions = sessions; this.http = http;
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients)); cloud = kugouCloudTransport; time = timeProvider ?? TimeProvider.System;
        neteaseTokens = new(tokenRuntime ?? throw new ArgumentNullException(nameof(tokenRuntime)));
        library.SynchronizeNetworkPlaylists = RunNetworkPlaylists;
    }
    private static JsonElement Failed(string code) => LJ.Element(LJ.Obj(("ok", false), ("code", code)));
    private void Changed() { foreach (Action listener in StateChanged?.GetInvocationList() ?? []) try { listener(); } catch { /* UI observers cannot change the durable outcome. */ } }
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            LJ.Require(!closed && !stopping, "CLOSED"); if (started) return Task.CompletedTask;
            started = true; schedulerCancellation = new(); var token = schedulerCancellation.Token;
            scheduler = RunSchedulerAsync(new PeriodicTimer(Interval, time), token);
        }
        return Task.CompletedTask;
    }
    private async Task RunSchedulerAsync(PeriodicTimer timer, CancellationToken token)
    {
        using (timer)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    if (!await RunScheduledOnce(token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { }
        }
    }
    private async Task<bool> RunScheduledOnce(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await library.ReadLibrarySyncAsync(cancellationToken).ConfigureAwait(false);
            if (!MusicLibrarySyncEngine.CanRunScheduled(snapshot)) return true;
            _ = await RunOnce(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A deterministic local-state failure retires this scheduler generation.
            // Explicit reads retain the real error; background work must not retry-loop.
            Changed();
            return false;
        }
    }
    public void Invalidate(string? platform = null)
    {
        if (platform is not null && !MusicLibraryOwner.SyncPlatforms.Contains(platform)) return;
        CancellationTokenSource? cts;
        lock (gate) { generation++; cts = roundCancellation; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }
    private async Task<JsonElement> RunNetworkPlaylists(string platform, CancellationToken cancellationToken)
    {
        try
        {
            LJ.Require(await controls.WaitAsync(0, cancellationToken).ConfigureAwait(false), "SYNC_BUSY");
            Task<JsonElement> work;
            try
            {
                Invalidate(); await DrainRound(cancellationToken).ConfigureAwait(false);
                work = RunOnce(cancellationToken, platform);
            }
            finally { controls.Release(); }
            return await work.ConfigureAwait(false);
        }
        catch (Exception error) { return Failed(MusicLibrarySyncEngine.Error(error)); }
    }
    private Task<JsonElement> RunOnce(CancellationToken cancellationToken, string? networkPlatform = null)
    {
        lock (gate)
        {
            if (closed || stopping) return Task.FromResult(Failed("CLOSED"));
            if (round is { IsCompleted: false }) return Task.FromResult(Failed("SYNC_BUSY"));
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            roundCancellation = source; long epoch = generation;
            round = Task.Run(async () =>
            {
                try
                {
                    void Check() { lock (gate) LJ.Require(!closed && !stopping && generation == epoch, "STALE_SESSION"); }
                    return await new MusicLibrarySyncEngine(library, sessions, http, clients, neteaseTokens, cloud, Check, source.Token)
                        .RunAsync(networkPlatform).ConfigureAwait(false);
                }
                catch (Exception e) { return Failed(MusicLibrarySyncEngine.Error(e)); }
                finally { lock (gate) if (ReferenceEquals(roundCancellation, source)) roundCancellation = null; source.Dispose(); Changed(); }
            }, CancellationToken.None);
            return round;
        }
    }
    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        bool entered = false;
        try
        {
            var p = LJ.Parameters(parameters, 8192);
            lock (gate) LJ.Require(!closed && !stopping, "CLOSED");
            if (method == "music.librarySync.get") { LJ.Fields(p, ""); return MusicLibrarySyncEngine.View(await library.ReadLibrarySyncAsync(cancellationToken)); }
            if (method == "music.librarySync.run") { LJ.Fields(p, ""); return await RunOnce(cancellationToken).ConfigureAwait(false); }
            LJ.Require(await controls.WaitAsync(0, cancellationToken), "SYNC_BUSY"); entered = true;
            if (method == "music.librarySync.cancel")
            {
                LJ.Fields(p, ""); Invalidate(); await DrainRound(cancellationToken);
                return MusicLibrarySyncEngine.View(await library.ReadLibrarySyncAsync(cancellationToken));
            }
            LJ.Require(method is "music.librarySync.set" or "music.librarySync.confirm" or "music.librarySync.resetAccount", "INVALID_REQUEST");
            if (method == "music.librarySync.set") LJ.Fields(p, "platform favorites playlists ifRevision confirm", "platform favorites playlists ifRevision confirm");
            if (method == "music.librarySync.resetAccount") LJ.Fields(p, "platform ifRevision confirm", "platform ifRevision confirm");
            if (method == "music.librarySync.confirm") LJ.Fields(p, "token ifRevision confirm", "token ifRevision confirm");
            LJ.Require(LJ.B(p["confirm"]), "CONFIRMATION_REQUIRED"); long revision = LJ.I(p["ifRevision"]);
            Invalidate(); await DrainRound(cancellationToken);
            var before = await library.ReadLibrarySyncAsync(cancellationToken); var state = JsonNode.Parse(before.State.GetRawText())!.AsObject(); var data = JsonNode.Parse(before.Library.GetRawText())!.AsObject();
            LJ.Require(LJ.I(state["revision"]) == revision, "REVISION_CONFLICT");
            if (method == "music.librarySync.confirm")
            {
                LJ.Require(state["confirmation"] is JsonObject && LJ.S(state["confirmation"]!["token"]) == LJ.Text(p["token"], 64), "CONFIRMATION_EXPIRED");
                state["approved"] = state["confirmation"]!.DeepClone(); state["confirmation"] = null;
            }
            else
            {
                string platform = LJ.Text(p["platform"], 2); LJ.Require(MusicLibraryOwner.SyncPlatforms.Contains(platform));
                var settings = LJ.Object(state["platforms"]![platform]);
                if (method == "music.librarySync.set")
                {
                    LJ.Require(LJ.IsBool(p["favorites"]) && LJ.IsBool(p["playlists"]));
                    bool enabled = LJ.B(p["favorites"]) || LJ.B(p["playlists"]);
                    if (enabled)
                    {
                        var lease = await sessions.AcquireAsync(platform, cancellationToken);
                        LJ.Require(lease is not null && lease.Platform == platform && sessions.IsCurrent(lease), "AUTH_REQUIRED");
                        string account = LJ.S(data["accounts"]![platform]); LJ.Require(account.Length == 0 || account == lease.UserId, "ACCOUNT_REBIND_REQUIRED");
                        LJ.Require(MusicLibraryOwner.NetworkPlaylists(data).OfType<JsonObject>().Where(r => LJ.S(r["platform"]) == platform)
                            .All(r => (r["intent"] ?? r["subscription"]) is not JsonObject relation ||
                                LJ.S(relation["accountId"]) == lease.UserId), "ACCOUNT_REBIND_REQUIRED");
                        settings["accountId"] = lease.UserId;
                    }
                    settings["favorites"] = LJ.B(p["favorites"]); settings["playlists"] = LJ.B(p["playlists"]);
                }
                else
                {
                    // Reset never deletes local songs/playlists or touches a remote account. Re-enable is a separate consent.
                    LJ.Require(state["pending"] is not JsonObject pending || LJ.S(pending["platform"]) != platform, "PENDING_WRITE");
                    settings["favorites"] = false; settings["playlists"] = false; settings["accountId"] = "";
                    LJ.Object(data["accounts"]).Remove(platform);
                    foreach (var reference in MusicLibraryOwner.NetworkPlaylists(data).OfType<JsonObject>().Where(r => LJ.S(r["platform"]) == platform))
                    {
                        reference["subscription"] = null; reference["intent"] = null;
                        reference["revision"] = LJ.I(reference["revision"]) + 1;
                    }
                    foreach (var c in LJ.Array(data["collections"]).OfType<JsonObject>())
                    {
                        var removed = LJ.Array(c["bindings"]).OfType<JsonObject>().Where(b => LJ.S(b["platform"]) == platform).ToArray();
                        var keys = removed.Select(b => LJ.Stringify(new JsonArray(platform, LJ.S(b["accountId"]), LJ.S(b["id"])))).ToHashSet();
                        foreach (var b in removed) LJ.Array(c["bindings"]).Remove(b);
                        foreach (var intent in LJ.Object(c["intents"]).Select(p => p.Value).OfType<JsonObject>())
                        { intent["pending"] = true; intent["acknowledged"] = LJ.Arr((intent["acknowledged"] as JsonArray ?? []).Where(n => !keys.Contains(LJ.S(n)))); }
                    }
                }
                state["approved"] = null; state["confirmation"] = null;
            }
            state["status"] = LJ.Obj(("phase", "idle"), ("code", ""), ("at", time.GetUtcNow().ToUnixTimeMilliseconds()), ("issues", new JsonArray()));
            var saved = await library.CommitLibrarySyncAsync(before, LJ.Element(data), LJ.Element(state), cancellationToken); Changed();
            // Authorization is durable; page cancellation does not undo it or create a second scheduler.
            return MusicLibrarySyncEngine.View(saved);
        }
        catch (Exception e) { return Failed(MusicLibrarySyncEngine.Error(e)); }
        finally { if (entered) controls.Release(); }
    }
    private async Task DrainRound(CancellationToken token)
    { Task? task; lock (gate) task = round; if (task is not null) await task.WaitAsync(token).ConfigureAwait(false); }
    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task? timer; CancellationTokenSource? cts;
        lock (gate) { if (closed) return; stopping = true; generation++; cts = schedulerCancellation; timer = scheduler; }
        Invalidate(); cts?.Cancel();
        if (timer is not null) await timer.WaitAsync(cancellationToken).ConfigureAwait(false);
        await DrainRound(cancellationToken).ConfigureAwait(false);
        await controls.WaitAsync(cancellationToken).ConfigureAwait(false); controls.Release();
    }
    public async Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        Task? timer;
        lock (gate)
        {
            LJ.Require(!closed, "CLOSED");
            if (!stopping) return;
            timer = scheduler;
        }
        await DrainRound(cancellationToken).ConfigureAwait(false);
        if (timer is not null) await timer.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool restart;
        lock (gate)
        {
            LJ.Require(!closed, "CLOSED");
            if (!stopping) return;
            restart = started; stopping = false; started = false;
            schedulerCancellation?.Dispose(); schedulerCancellation = null; scheduler = null; generation++;
        }
        if (restart) await StartAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await PrepareShutdownAsync(cancellationToken).ConfigureAwait(false);
        lock (gate) { closed = true; schedulerCancellation?.Dispose(); schedulerCancellation = null; StateChanged = null;
            library.SynchronizeNetworkPlaylists = null; }
    }
    public async ValueTask DisposeAsync() => await AbortAsync().ConfigureAwait(false);
}
