using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities.Sync;

public sealed record MusicLibrarySyncResult(
    bool Ok,
    string Code,
    string Phase,
    long Revision,
    IReadOnlyDictionary<string, MusicSyncPlatformRecord>? Platforms,
    MusicSyncConfirmationRecord? Confirmation,
    IReadOnlyList<MusicSyncIssueRecord> Issues,
    bool LocalOnly = false)
{
    internal static MusicLibrarySyncResult FromSnapshot(MusicLibrarySyncSnapshot snapshot)
    {
        var s = snapshot.State;
        return new(true, s.StatusCode, s.StatusPhase, s.Revision, s.Platforms, s.Confirmation, s.Issues);
    }

    internal static MusicLibrarySyncResult Failed(string code, string phase = "", MusicSyncConfirmationRecord? confirmation = null) =>
        new(false, code, phase, 0, null, confirmation, []);
}

/// <summary>One coordinator per library owner. Borrows owners/transport/runtime; no account writer or second library store.</summary>
public sealed class MusicLibrarySyncService : IAsyncDisposable
{
    public const string ContractVersion = "1.1.0";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan LocalEditDebounce = TimeSpan.FromMilliseconds(1500);
    private readonly MusicLibraryOwner library;
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformClient client;
    private readonly object gate = new();
    private readonly SemaphoreSlim controls = new(1, 1);
    private readonly HashSet<Task> localEditTasks = [];
    private CancellationTokenSource? schedulerCancellation, roundCancellation, localEditCancellation;
    private Task? scheduler, abortion;
    private Task<MusicLibrarySyncResult>? round;
    private long generation;
    private bool closed, stopping, started, controlling, roundActive, localEditPending;
    public event Action? StateChanged;

    internal MusicLibrarySyncService(MusicLibraryOwner library, IMusicPlatformSessionSource sessions,
        MusicPlatformClient client)
    {
        this.library = library; this.sessions = sessions; this.client = client;
        library.SynchronizeNetworkPlaylists = RunNetworkPlaylists;
        library.LocalCollectionsChanged += RequestLocalEditSync;
    }

    private void Changed()
    {
        foreach (Action listener in StateChanged?.GetInvocationList() ?? [])
        {
            try { listener(); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicLibrary, "sync", "sync_state_listener_failed",
                    new { error = error.Message });
            }
        }
    }

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

    private void TrackLocalEditLocked(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        localEditTasks.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch { /* Background debounce/follow-up failures are logged inside RunScheduledOnceCore. */ }
            finally
            {
                lock (gate) localEditTasks.Remove(completion.Task);
                completion.TrySetResult();
            }
        });
    }

    private void RequestLocalEditSync()
    {
        CancellationTokenSource? previous;
        lock (gate)
        {
            if (closed || stopping || !started || schedulerCancellation is null) return;
            previous = localEditCancellation;
            var next = CancellationTokenSource.CreateLinkedTokenSource(schedulerCancellation.Token);
            localEditCancellation = next;
            TrackLocalEditLocked(() => RunLocalEditDebouncedAsync(next));
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }

    private async Task RunLocalEditDebouncedAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(LocalEditDebounce, cts.Token).ConfigureAwait(false);
            if (cts.Token.IsCancellationRequested) return;
            lock (gate)
            {
                if (closed || stopping) return;
                if (controlling || roundActive)
                {
                    localEditPending = true;
                    return;
                }
            }
            var (_, result) = await RunScheduledOnceCore(cts.Token).ConfigureAwait(false);
            if (result is { Ok: false, Code: "SYNC_BUSY" })
            {
                lock (gate)
                {
                    if (!closed && !stopping)
                    {
                        localEditPending = true;
                        MaybeSchedulePendingLocalEditLocked();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(localEditCancellation, cts)) localEditCancellation = null;
            }
            cts.Dispose();
        }
    }

    private void MaybeSchedulePendingLocalEditLocked()
    {
        if (!localEditPending || closed || stopping || !started || schedulerCancellation is null) return;
        if (controlling || roundActive) return;
        localEditPending = false;
        var token = schedulerCancellation.Token;
        TrackLocalEditLocked(async () =>
        {
            try
            {
                var (_, result) = await RunScheduledOnceCore(token).ConfigureAwait(false);
                if (result is { Ok: false, Code: "SYNC_BUSY" })
                {
                    lock (gate)
                    {
                        if (!closed && !stopping)
                        {
                            localEditPending = true;
                            MaybeSchedulePendingLocalEditLocked();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task RunSchedulerAsync(PeriodicTimer timer, CancellationToken token)
    {
        using (timer)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    if (!(await RunScheduledOnceCore(token).ConfigureAwait(false)).Continue) break;
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<(bool Continue, MusicLibrarySyncResult? Result)> RunScheduledOnceCore(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await library.ReadLibrarySyncAsync(cancellationToken).ConfigureAwait(false);
            if (!MusicLibrarySyncEngine.CanRunScheduled(snapshot)) return (true, null);
            var result = await RunOnce(cancellationToken).ConfigureAwait(false);
            return (true, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // A deterministic local-state failure retires this scheduler generation.
            // Explicit reads retain the real error; background work must not retry-loop.
            DebugLog.Warn(DebugCategories.MusicLibrary, "sync", "sync_scheduler_retired",
                new { code = MusicLibrarySyncEngine.Error(error), error = error.Message });
            Changed();
            return (false, null);
        }
    }

    public void Invalidate(string? platform = null)
    {
        if (platform is not null && !MusicLibraryOwner.SyncPlatforms.Contains(platform)) return;
        CancellationTokenSource? cts;
        lock (gate) { generation++; cts = roundCancellation; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }

    private async Task<MusicNetworkPlaylistSyncStatus> RunNetworkPlaylists(string platform, CancellationToken cancellationToken)
    {
        try
        {
            lock (gate) MusicRules.Require(!closed && !stopping, "CLOSED");
            MusicRules.Require(await controls.WaitAsync(0, cancellationToken).ConfigureAwait(false), "SYNC_BUSY");
            Task<MusicLibrarySyncResult> work;
            try
            {
                lock (gate) { MusicRules.Require(!closed && !stopping, "CLOSED"); controlling = true; }
                Invalidate(); await DrainRound(cancellationToken).ConfigureAwait(false);
                work = RunOnce(cancellationToken, platform, hasControl: true);
            }
            finally
            {
                lock (gate) { controlling = false; MaybeSchedulePendingLocalEditLocked(); }
                controls.Release();
            }
            var result = await work.ConfigureAwait(false);
            if (!result.Ok) return new MusicNetworkPlaylistSyncStatus(false, Code: result.Code);
            string? firstIssue = result.Issues.Count > 0 ? result.Issues[0].Code : null;
            return new MusicNetworkPlaylistSyncStatus(true, State: result.LocalOnly ? "local-only" : "synced",
                Phase: result.Phase, FirstIssueCode: firstIssue);
        }
        catch (Exception error) { return new MusicNetworkPlaylistSyncStatus(false, Code: MusicLibrarySyncEngine.Error(error)); }
    }

    private Task<MusicLibrarySyncResult> RunOnce(CancellationToken cancellationToken, string? networkPlatform = null, bool hasControl = false)
    {
        lock (gate)
        {
            if (closed || stopping) return Task.FromResult(MusicLibrarySyncResult.Failed("CLOSED"));
            if (!hasControl && controlling || roundActive)
                return Task.FromResult(MusicLibrarySyncResult.Failed("SYNC_BUSY"));
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            roundCancellation = source; roundActive = true; long epoch = generation;
            round = Task.Run(async () =>
            {
                try
                {
                    void Check() { lock (gate) MusicRules.Require(!closed && !stopping && generation == epoch, "STALE_SESSION"); }
                    return await new MusicLibrarySyncEngine(library, sessions, client, Check, source.Token)
                        .RunAsync(networkPlatform).ConfigureAwait(false);
                }
                catch (Exception e) { return MusicLibrarySyncResult.Failed(MusicLibrarySyncEngine.Error(e)); }
                finally
                {
                    lock (gate) if (ReferenceEquals(roundCancellation, source)) roundCancellation = null;
                    source.Dispose();
                    Changed();
                    lock (gate)
                    {
                        roundActive = false;
                        MaybeSchedulePendingLocalEditLocked();
                    }
                }
            }, CancellationToken.None);
            return round;
        }
    }

    public async Task<MusicLibrarySyncResult> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            lock (gate) MusicRules.Require(!closed && !stopping, "CLOSED");
            return MusicLibrarySyncResult.FromSnapshot(await library.ReadLibrarySyncAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) { return MusicLibrarySyncResult.Failed(MusicLibrarySyncEngine.Error(e)); }
    }

    public Task<MusicLibrarySyncResult> RunAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) if (closed || stopping) return Task.FromResult(MusicLibrarySyncResult.Failed("CLOSED"));
        return RunOnce(cancellationToken);
    }

    public Task<MusicLibrarySyncResult> SetPlatformAsync(string platform, bool favorites, bool playlists,
        long ifRevision, bool confirm, CancellationToken cancellationToken = default) =>
        MutateControlAsync(ifRevision, confirm, async (data, state, token) =>
        {
            MusicRules.Require(MusicLibraryOwner.SyncPlatforms.Contains(platform), "INVALID_REQUEST");
            var platforms = new Dictionary<string, MusicSyncPlatformRecord>(state.Platforms, StringComparer.Ordinal);
            var currentSettings = platforms.TryGetValue(platform, out var ps) ? ps : new MusicSyncPlatformRecord(false, false, "");
            string accountId = currentSettings.AccountId;
            if (favorites || playlists)
            {
                var lease = await sessions.AcquireAsync(platform, token).ConfigureAwait(false);
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
            platforms[platform] = new MusicSyncPlatformRecord(favorites, playlists, accountId);
            return (data, state with { Platforms = platforms, Approved = null, Confirmation = null });
        }, cancellationToken);

    public Task<MusicLibrarySyncResult> ConfirmAsync(string confirmationToken, long ifRevision, bool confirm,
        CancellationToken cancellationToken = default) =>
        MutateControlAsync(ifRevision, confirm, (data, state, _) =>
        {
            MusicRules.Require(MusicRules.IsValidId(confirmationToken, 64), "INVALID_REQUEST");
            MusicRules.Require(state.Confirmation is { } conf && conf.Token == confirmationToken, "CONFIRMATION_EXPIRED");
            return Task.FromResult((data, state with { Approved = state.Confirmation, Confirmation = null }));
        }, cancellationToken);

    public Task<MusicLibrarySyncResult> ResetAccountAsync(string platform, long ifRevision, bool confirm,
        CancellationToken cancellationToken = default) =>
        MutateControlAsync(ifRevision, confirm, (data, state, _) =>
        {
            MusicRules.Require(MusicLibraryOwner.SyncPlatforms.Contains(platform), "INVALID_REQUEST");
            // Reset never deletes local songs/playlists or touches a remote account. Re-enable is a separate consent.
            var platforms = new Dictionary<string, MusicSyncPlatformRecord>(state.Platforms, StringComparer.Ordinal)
            {
                [platform] = new MusicSyncPlatformRecord(false, false, "")
            };
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
                if (removed.Length == 0) return c;
                var keys = removed.Select(b => MusicLibraryOwner.BindingKey(platform, b.AccountId, b.RemoteId))
                    .ToHashSet(StringComparer.Ordinal);
                var keptBindings = c.Bindings.Where(b => b.Platform != platform).ToArray();
                var nextIntents = new Dictionary<string, MusicCollectionIntentRecord>(c.Intents.Count, StringComparer.Ordinal);
                foreach (var (songId, intent) in c.Intents)
                {
                    nextIntents[songId] = intent with
                    {
                        Acknowledged = (intent.Acknowledged ?? []).Where(k => !keys.Contains(k)).ToArray()
                    };
                }
                return c with { Bindings = keptBindings, Intents = nextIntents };
            }).ToArray();
            var nextPending = state.Pending is { } pen && pen.Platform == platform ? null : state.Pending;
            var nextData = data with { Accounts = nextAccounts, NetworkPlaylists = nextNetwork, Collections = nextCollections };
            var nextState = state with { Platforms = platforms, Pending = nextPending, Approved = null, Confirmation = null };
            return Task.FromResult((nextData, nextState));
        }, cancellationToken);

    private async Task<MusicLibrarySyncResult> MutateControlAsync(
        long ifRevision,
        bool confirm,
        Func<MusicLibraryAreaRecord, MusicLibrarySyncStateRecord, CancellationToken, Task<(MusicLibraryAreaRecord Data, MusicLibrarySyncStateRecord State)>> transform,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            lock (gate) MusicRules.Require(!closed && !stopping, "CLOSED");
            MusicRules.Require(await controls.WaitAsync(0, cancellationToken).ConfigureAwait(false), "SYNC_BUSY");
            entered = true;
            lock (gate) { MusicRules.Require(!closed && !stopping, "CLOSED"); controlling = true; }
            MusicRules.Require(confirm, "CONFIRMATION_REQUIRED");
            MusicRules.Require(ifRevision is >= 0 and <= MusicRules.MaxInteger, "INVALID_REQUEST");
            Invalidate();
            await DrainRound(cancellationToken).ConfigureAwait(false);
            var before = await library.ReadLibrarySyncAsync(cancellationToken).ConfigureAwait(false);
            var state = before.State;
            var data = before.Library;
            MusicRules.Require(state.Revision == ifRevision, "REVISION_CONFLICT");

            (data, state) = await transform(data, state, cancellationToken).ConfigureAwait(false);

            state = state with
            {
                StatusPhase = "idle",
                StatusCode = "",
                StatusAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Issues = []
            };
            var saved = await library.CommitLibrarySyncAsync(before, data, state, cancellationToken).ConfigureAwait(false);
            Changed();
            // Authorization is durable; page cancellation does not undo it or create a second scheduler.
            return MusicLibrarySyncResult.FromSnapshot(saved);
        }
        catch (Exception e) { return MusicLibrarySyncResult.Failed(MusicLibrarySyncEngine.Error(e)); }
        finally
        {
            if (entered)
            {
                lock (gate) { controlling = false; MaybeSchedulePendingLocalEditLocked(); }
                controls.Release();
            }
        }
    }

    private async Task DrainRound(CancellationToken token)
    { Task? task; lock (gate) task = round; if (task is not null) await task.WaitAsync(token).ConfigureAwait(false); }

    private async Task DrainLocalEdits(CancellationToken token)
    {
        while (true)
        {
            Task[] pending;
            lock (gate) pending = localEditTasks.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task? timer; CancellationTokenSource? cts, editCts;
        lock (gate)
        {
            if (closed) return;
            stopping = true; generation++; localEditPending = false;
            cts = schedulerCancellation; timer = scheduler;
            editCts = localEditCancellation;
        }
        Invalidate();
        try { editCts?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        await DrainLocalEdits(cancellationToken).ConfigureAwait(false);
        if (timer is not null) await timer.WaitAsync(cancellationToken).ConfigureAwait(false);
        await DrainRound(cancellationToken).ConfigureAwait(false);
        await controls.WaitAsync(cancellationToken).ConfigureAwait(false); controls.Release();
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate) task = abortion ??= AbortCoreAsync();
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task AbortCoreAsync()
    {
        await PrepareShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        lock (gate)
        {
            closed = true; schedulerCancellation?.Dispose(); schedulerCancellation = null; StateChanged = null;
            library.LocalCollectionsChanged -= RequestLocalEditSync;
            library.SynchronizeNetworkPlaylists = null;
        }
        controls.Dispose();
    }

    public async ValueTask DisposeAsync() => await AbortAsync().ConfigureAwait(false);
}
