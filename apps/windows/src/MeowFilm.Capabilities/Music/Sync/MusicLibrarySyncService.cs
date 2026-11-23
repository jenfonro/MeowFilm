using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Sync;

/// <summary>One coordinator per library owner. Borrows owners/transport/runtime; no account writer or second library store.</summary>
public sealed class MusicLibrarySyncService : IAsyncDisposable
{
    public const string ContractVersion = "1.1.0";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly MusicLibraryOwner library;
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformClient client;
    private readonly object gate = new();
    private readonly SemaphoreSlim controls = new(1, 1);
    private CancellationTokenSource? schedulerCancellation, roundCancellation;
    private Task? scheduler;
    private Task<JsonElement>? round;
    private long generation;
    private bool closed, stopping, started, controlling;
    public event Action? StateChanged;

    internal MusicLibrarySyncService(MusicLibraryOwner library, IMusicPlatformSessionSource sessions,
        MusicPlatformClient client)
    {
        this.library = library; this.sessions = sessions; this.client = client;
        library.SynchronizeNetworkPlaylists = RunNetworkPlaylists;
    }

    private static JsonElement Failed(string code) => MusicWireCodec.Fail(code);
    private void Changed() { foreach (Action listener in StateChanged?.GetInvocationList() ?? []) try { listener(); } catch { /* UI observers cannot change the durable outcome. */ } }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            MusicRules.Require(!closed && !stopping, "CLOSED");
            if (started) return Task.CompletedTask;
            started = true; schedulerCancellation = new(); var token = schedulerCancellation.Token;
            scheduler = RunSchedulerAsync(new PeriodicTimer(Interval), token);
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
            MusicRules.Require(await controls.WaitAsync(0, cancellationToken).ConfigureAwait(false), "SYNC_BUSY");
            Task<JsonElement> work;
            try
            {
                lock (gate) { MusicRules.Require(!closed && !stopping, "CLOSED"); controlling = true; }
                Invalidate(); await DrainRound(cancellationToken).ConfigureAwait(false);
                work = RunOnce(cancellationToken, platform, hasControl: true);
            }
            finally { lock (gate) controlling = false; controls.Release(); }
            return await work.ConfigureAwait(false);
        }
        catch (Exception error) { return Failed(MusicLibrarySyncEngine.Error(error)); }
    }

    private Task<JsonElement> RunOnce(CancellationToken cancellationToken, string? networkPlatform = null, bool hasControl = false)
    {
        lock (gate)
        {
            if (closed || stopping) return Task.FromResult(Failed("CLOSED"));
            if (!hasControl && controlling || round is { IsCompleted: false }) return Task.FromResult(Failed("SYNC_BUSY"));
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            roundCancellation = source; long epoch = generation;
            round = Task.Run(async () =>
            {
                try
                {
                    void Check() { lock (gate) MusicRules.Require(!closed && !stopping && generation == epoch, "STALE_SESSION"); }
                    return await new MusicLibrarySyncEngine(library, sessions, client, Check, source.Token)
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
            var p = MusicWireCodec.ParseParameters(parameters);
            lock (gate) MusicRules.Require(!closed && !stopping, "CLOSED");
            if (method == "music.librarySync.get")
                return MusicLibrarySyncEngine.View(await library.ReadLibrarySyncAsync(cancellationToken));
            if (method == "music.librarySync.run")
                return await RunOnce(cancellationToken).ConfigureAwait(false);
            MusicRules.Require(await controls.WaitAsync(0, cancellationToken), "SYNC_BUSY"); entered = true;
            lock (gate) { MusicRules.Require(!closed && !stopping, "CLOSED"); controlling = true; }
            if (method == "music.librarySync.cancel")
            {
                Invalidate(); await DrainRound(cancellationToken);
                return MusicLibrarySyncEngine.View(await library.ReadLibrarySyncAsync(cancellationToken));
            }
            MusicRules.Require(method is "music.librarySync.set" or "music.librarySync.confirm" or "music.librarySync.resetAccount", "INVALID_REQUEST");
            if (method == "music.librarySync.set") MusicWireCodec.RequireFields(p, "platform favorites playlists ifRevision confirm");
            if (method == "music.librarySync.resetAccount") MusicWireCodec.RequireFields(p, "platform ifRevision confirm");
            if (method == "music.librarySync.confirm") MusicWireCodec.RequireFields(p, "token ifRevision confirm");
            MusicRules.Require(MusicWireCodec.RequireBoolProp(p, "confirm"), "CONFIRMATION_REQUIRED");
            long rev = MusicWireCodec.RequireIntProp(p, "ifRevision");
            Invalidate(); await DrainRound(cancellationToken);
            var before = await library.ReadLibrarySyncAsync(cancellationToken);
            var state = before.State;
            var data = before.Library;
            MusicRules.Require(state.Revision == rev, "REVISION_CONFLICT");

            if (method == "music.librarySync.confirm")
            {
                MusicRules.Require(state.Confirmation is { } conf && conf.Token == MusicWireCodec.RequireIdProp(p, "token", 64), "CONFIRMATION_EXPIRED");
                state = state with { Approved = state.Confirmation, Confirmation = null };
            }
            else
            {
                string platform = MusicWireCodec.RequireIdProp(p, "platform", 2);
                MusicRules.Require(MusicLibraryOwner.SyncPlatforms.Contains(platform));
                var platforms = new Dictionary<string, MusicSyncPlatformRecord>(state.Platforms, StringComparer.Ordinal);
                var currentSettings = platforms.TryGetValue(platform, out var ps) ? ps : new MusicSyncPlatformRecord(false, false, "");

                if (method == "music.librarySync.set")
                {
                    bool fav = MusicWireCodec.RequireBoolProp(p, "favorites");
                    bool pls = MusicWireCodec.RequireBoolProp(p, "playlists");
                    string accountId = currentSettings.AccountId;
                    if (fav || pls)
                    {
                        var lease = await sessions.AcquireAsync(platform, cancellationToken);
                        MusicRules.Require(lease is not null && lease.Platform == platform && sessions.IsCurrent(lease), "AUTH_REQUIRED");
                        string account = data.Accounts.TryGetValue(platform, out var acc) ? acc : "";
                        MusicRules.Require(account.Length == 0 || account == lease.UserId, "ACCOUNT_REBIND_REQUIRED");
                        MusicRules.Require(data.NetworkPlaylists.Where(r => r.Platform == platform).All(r =>
                        {
                            string? relAcc = r.IntentAccountId ?? r.SubAccountId;
                            return relAcc is null || relAcc == lease.UserId;
                        }), "ACCOUNT_REBIND_REQUIRED");
                        accountId = lease.UserId;
                    }
                    platforms[platform] = new MusicSyncPlatformRecord(fav, pls, accountId);
                }
                else
                {
                    // Reset never deletes local songs/playlists or touches a remote account. Re-enable is a separate consent.
                    MusicRules.Require(state.Pending is not { } pen || pen.Platform != platform, "PENDING_WRITE");
                    platforms[platform] = new MusicSyncPlatformRecord(false, false, "");
                    var nextAccounts = new Dictionary<string, string>(data.Accounts, StringComparer.Ordinal);
                    nextAccounts.Remove(platform);
                    var nextNetwork = data.NetworkPlaylists.Select(r => r.Platform == platform
                        ? r with
                        {
                            SubAccountId = null, SubPresent = null, SubListId = null, SubCreatorId = null, SubCreatorListId = null,
                            IntentAccountId = null, IntentPresent = null, Revision = checked(r.Revision + 1)
                        }
                        : r).ToArray();
                    var nextCollections = data.Collections.Select(c =>
                    {
                        var removed = c.Bindings.Where(b => b.Platform == platform).ToArray();
                        if (removed.Length == 0 && c.Intents.Count == 0) return c;
                        var keys = removed.Select(b => MusicLibraryOwner.BindingKey(platform, b.AccountId, b.RemoteId))
                            .ToHashSet(StringComparer.Ordinal);
                        var keptBindings = c.Bindings.Where(b => b.Platform != platform).ToArray();
                        var nextIntents = new Dictionary<string, MusicCollectionIntentRecord>(c.Intents.Count, StringComparer.Ordinal);
                        foreach (var (songId, intent) in c.Intents)
                        {
                            nextIntents[songId] = intent with
                            {
                                Pending = true,
                                Acknowledged = (intent.Acknowledged ?? []).Where(k => !keys.Contains(k)).ToArray()
                            };
                        }
                        return c with { Bindings = keptBindings, Intents = nextIntents };
                    }).ToArray();
                    data = data with { Accounts = nextAccounts, NetworkPlaylists = nextNetwork, Collections = nextCollections };
                }

                state = state with { Platforms = platforms, Approved = null, Confirmation = null };
            }

            state = state with
            {
                StatusPhase = "idle",
                StatusCode = "",
                StatusAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Issues = []
            };
            var saved = await library.CommitLibrarySyncAsync(before, data, state, cancellationToken);
            Changed();
            // Authorization is durable; page cancellation does not undo it or create a second scheduler.
            return MusicLibrarySyncEngine.View(saved);
        }
        catch (Exception e) { return Failed(MusicLibrarySyncEngine.Error(e)); }
        finally { if (entered) { lock (gate) controlling = false; controls.Release(); } }
    }

    private async Task DrainRound(CancellationToken token)
    { Task? task; lock (gate) task = round; if (task is not null) await task.WaitAsync(token).ConfigureAwait(false); }

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task? timer; CancellationTokenSource? cts;
        lock (gate) { if (closed) return; stopping = true; generation++; cts = schedulerCancellation; timer = scheduler; }
        Invalidate();
        try { cts?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        if (timer is not null) await timer.WaitAsync(cancellationToken).ConfigureAwait(false);
        await DrainRound(cancellationToken).ConfigureAwait(false);
        await controls.WaitAsync(cancellationToken).ConfigureAwait(false); controls.Release();
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        await PrepareShutdownAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            closed = true; schedulerCancellation?.Dispose(); schedulerCancellation = null; StateChanged = null;
            library.SynchronizeNetworkPlaylists = null;
        }
    }

    public async ValueTask DisposeAsync() => await AbortAsync().ConfigureAwait(false);
}
