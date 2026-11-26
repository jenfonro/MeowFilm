using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Debug;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;
using static MeowFilm.Capabilities.Recommendations.MusicRecommendationQueuePort;

namespace MeowFilm.Capabilities.Recommendations;

public sealed partial class MusicRecommendationCoordinator
{
    private readonly AsyncLocal<ObserverFrame?> observerFrame = new();
    private sealed class ObserverFrame { internal int Active = 1; }
    private Task publisher = Task.CompletedTask;
    private Task persistTail = Task.CompletedTask;
    private bool publishing;
    private MusicRecommendationSnapshot? notice;
    private Task? abortion, release;

    private static MusicRecommendationSessionRecord? ToSessionRecord(Session? s)
    {
        if (s is null || !s.Active || s.InitialPending || s.Config.Kind == "fm") return null;
        return new MusicRecommendationSessionRecord(
            SessionId: s.Id,
            Kind: s.Config.Kind,
            Platform: s.Config.Platform,
            SourceType: s.Config.SourceType,
            Seed: s.Config.Seed,
            FixedSeed: s.FixedSeed,
            Restore: s.Config.Restore,
            ContinueEnabled: s.Continue,
            Exhausted: s.Exhausted,
            OffsetVal: Math.Max(0, s.Offset),
            Batch: Math.Max(0, s.Batch),
            RecommendationStarted: s.RecommendationStarted,
            CheckpointId: s.Checkpoint,
            HeartbeatEntry: s.Config.Heartbeat is { } hb
                ? (hb.Entry == MusicHeartbeatEntry.Favorites ? "favorites" : "current")
                : null,
            HeartbeatPlaylistId: s.Heartbeat?.PlaylistId ?? s.Config.Heartbeat?.PlaylistId,
            HeartbeatStartMusicId: s.Heartbeat?.StartMusicId,
            HeartbeatSongId: s.Heartbeat?.SongId ?? s.Config.Heartbeat?.SongId,
            HeartbeatFromHome: s.Heartbeat?.FromHome,
            HeartbeatCount: s.Heartbeat?.Count,
            HeartbeatAccountId: s.Heartbeat?.AccountId,
            SourceIds: s.Config.SourceIds,
            Attempted: s.Attempted.Order(StringComparer.Ordinal).ToArray());
    }

    private void SchedulePersistSessionLocked(Session? s)
    {
        if (closed || stopping) return;
        var record = ToSessionRecord(s);
        var previous = persistTail;
        persistTail = LaunchLocked(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch { /* Previous persistence error already logged. */ }
            try { await queue.SaveRecommendationSessionAsync(record, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "recommendation", "persist_session_failed",
                    new Dictionary<string, object?> { ["error"] = error.GetType().Name, ["message"] = error.Message });
            }
        });
    }

    public async Task RestorePersistedSessionAsync(CancellationToken cancellationToken = default)
    {
        var saved = await queue.ReadRecommendationSessionAsync(cancellationToken).ConfigureAwait(false);
        if (saved is null) return;
        if (saved.Kind is not ("similar" or "heartbeat" or "queue") || saved.Platform != "wy")
        {
            await queue.SaveRecommendationSessionAsync(null, cancellationToken).ConfigureAwait(false);
            return;
        }
        var snapshot = await queue.InLaneAsync(ReadAsync, cancellationToken).ConfigureAwait(false);
        if (snapshot.Entries.Length == 0)
        {
            await queue.SaveRecommendationSessionAsync(null, cancellationToken).ConfigureAwait(false);
            return;
        }
        string checkpoint = saved.CheckpointId;
        if (checkpoint.Length > 0 && !await queue.HasRecommendationCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false))
            checkpoint = "";
        MusicHeartbeatRequest? heartbeatReq = null;
        HeartbeatContext? heartbeatCtx = null;
        if (saved.Kind == "heartbeat")
        {
            var entry = saved.HeartbeatEntry == "current"
                ? MusicHeartbeatEntry.CurrentSong
                : MusicHeartbeatEntry.Favorites;
            heartbeatReq = new MusicHeartbeatRequest(saved.Platform, entry, saved.HeartbeatSongId, saved.HeartbeatPlaylistId);
            if (!string.IsNullOrEmpty(saved.HeartbeatPlaylistId) &&
                !string.IsNullOrEmpty(saved.HeartbeatStartMusicId) &&
                !string.IsNullOrEmpty(saved.HeartbeatSongId) &&
                !string.IsNullOrEmpty(saved.HeartbeatAccountId))
            {
                heartbeatCtx = new HeartbeatContext(
                    saved.HeartbeatPlaylistId,
                    saved.HeartbeatStartMusicId,
                    saved.HeartbeatSongId,
                    saved.HeartbeatFromHome ?? false,
                    saved.HeartbeatCount ?? 0,
                    saved.HeartbeatAccountId);
            }
        }
        var config = new Configuration(
            saved.Kind,
            saved.Platform,
            Fm: null,
            saved.SourceIds,
            saved.SourceType,
            heartbeatReq,
            saved.Seed,
            saved.Restore && checkpoint.Length > 0,
            saved.ContinueEnabled,
            Play: false);
        lock (gate)
        {
            if (closed || stopping || session is { Active: true }) return;
            var restored = new Session(config, saved.SessionId)
            {
                Active = true,
                InitialPending = false,
                Continue = saved.ContinueEnabled,
                Exhausted = saved.Exhausted,
                Offset = saved.OffsetVal,
                Batch = Math.Max(0, saved.Batch),
                Checkpoint = checkpoint,
                FixedSeed = saved.FixedSeed,
                Heartbeat = heartbeatCtx,
                Queue = snapshot,
                QueueEpoch = queue.Epoch,
                Anchor = snapshot.Tail,
                Status = "ready",
                RecommendationStarted = saved.RecommendationStarted
            };
            foreach (string attempted in saved.Attempted) restored.Attempted.Add(attempted);
            session = restored;
            DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "session_restored",
                new Dictionary<string, object?>
                {
                    ["sessionId"] = restored.Id, ["kind"] = restored.Config.Kind,
                    ["checkpoint"] = restored.Checkpoint, ["continue"] = restored.Continue,
                    ["queueCount"] = snapshot.Entries.Length
                });
            PublishLocked();
        }
    }

    private Task<T> LaunchLocked<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(completion.Task);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await action().ConfigureAwait(false);
                lock (gate) { work.Remove(completion.Task); completion.TrySetResult(result); }
            }
            catch (Exception error)
            {
                lock (gate) { work.Remove(completion.Task); completion.TrySetException(error); }
            }
        });
        return completion.Task;
    }
    private Task LaunchLocked(Func<Task> action) =>
        LaunchLocked(async () => { await action().ConfigureAwait(false); return true; });
    private void CancelLocked(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        // Cancellation callbacks are foreign code: never run them under our state lock.
        _ = LaunchLocked(() =>
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
            return Task.CompletedTask;
        });
    }
    private void RetireLocked(Session s, string reason, bool stopPlayback = false, bool publish = true, bool preservePersisted = false)
    {
        if (!s.Active) return;
        var source = s.InitialPending ? s.PreviousSession : s;
        string ownedEntry = source?.Queue is { } current && source.Feedback.ContainsKey(current.CurrentId)
            ? current.CurrentId : "";
        if (s.PreviousSession is { } previous)
        {
            s.PreviousSession = null;
            if (previous.Checkpoint == s.Checkpoint) s.Checkpoint = "";
            RetireLocked(previous, reason, publish: false, preservePersisted: preservePersisted);
        }
        s.Active = false; s.Continue = false; s.Status = reason;
        s.Generation++; s.Navigation++; s.Feedback.Clear(); s.Candidates = null; CancelLocked(s.Cancellation);
        DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "session_retired",
            new Dictionary<string, object?>
            {
                ["sessionId"] = s.Id, ["kind"] = s.Config.Kind, ["reason"] = reason,
                ["preservePersisted"] = preservePersisted
            });
        var flight = s.Flight;
        if (!preservePersisted)
        {
            if (ReferenceEquals(s, session)) SchedulePersistSessionLocked(null);
            s.Cleanup = LaunchLocked(async () =>
            {
                await flight.ConfigureAwait(false);
                await DiscardAsync(s).ConfigureAwait(false);
            });
        }
        else
        {
            s.Cleanup = flight;
        }
        if (stopPlayback && ownedEntry.Length > 0)
        {
            var selection = SelectionLocked(s, ownedEntry, false, false, false, MusicRecommendationPlaybackAction.Stop, inactive: true);
            _ = LaunchLocked(() => SelectAsync(s, selection, CancellationToken.None));
        }
        if (publish && ReferenceEquals(s, session)) PublishLocked();
    }
    private void SuspendForAccountInvalidationLocked(Session s)
    {
        string ownedEntry = s.Queue is { } current && s.Feedback.ContainsKey(current.CurrentId)
            ? current.CurrentId : "";
        s.Continue = false;
        s.ModeIntent++;
        s.Generation++;
        s.Navigation++;
        s.Candidates = null;
        s.Feedback.Clear();
        s.Status = "account-invalidated";
        CancelLocked(s.Cancellation);
        DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "session_suspended_account_invalidated",
            new Dictionary<string, object?>
            {
                ["sessionId"] = s.Id, ["kind"] = s.Config.Kind, ["checkpoint"] = s.Checkpoint
            });
        if (ReferenceEquals(s, session)) SchedulePersistSessionLocked(s);
        if (ownedEntry.Length > 0)
        {
            var selection = SelectionLocked(s, ownedEntry, false, false, false, MusicRecommendationPlaybackAction.Stop, inactive: true);
            _ = LaunchLocked(() => SelectAsync(s, selection, CancellationToken.None));
        }
        if (ReferenceEquals(s, session)) PublishLocked();
    }
    public void Invalidate(string? platform = null)
    {
        if (platform is not null && platform is not ("wy" or "tx" or "kw" or "kg" or "mg"))
            throw new ArgumentException("INVALID_PLATFORM", nameof(platform));
        lock (gate)
        {
            if (closed || session is not { Active: true } s || platform is not null && s.Config.Platform != platform) return;
            if (s.InitialPending && s.PreviousSession is { Active: true, InitialPending: false } previous &&
                previous.Config.Restore && previous.Checkpoint.Length > 0 &&
                (platform is null || previous.Config.Platform == platform))
            {
                s.PreviousSession = null;
                if (s.Checkpoint == previous.Checkpoint) s.Checkpoint = "";
                RetireLocked(s, "account-invalidated", stopPlayback: true, publish: false);
                session = previous;
                s = previous;
            }
            bool hasRestorableCheckpoint = !s.InitialPending && s.Config.Restore && s.Checkpoint.Length > 0;
            if (hasRestorableCheckpoint)
            {
                if (platform is not null)
                    SuspendForAccountInvalidationLocked(s);
                else
                    RetireLocked(s, "account-invalidated", stopPlayback: true, preservePersisted: true);
                return;
            }
            RetireLocked(s, "account-invalidated", stopPlayback: true);
        }
    }
    private void QueueChanging(MusicRecommendationQueueChange change)
    {
        lock (gate)
        {
            if (session is not { } s || !CurrentLocked(s)) return;
            if (change == MusicRecommendationQueueChange.Mode)
                DisableContinuationLocked(s, publish: false);
            if (s.Returning || change is MusicRecommendationQueueChange.Replace or MusicRecommendationQueueChange.Clear or
                MusicRecommendationQueueChange.Restore or MusicRecommendationQueueChange.Reset ||
                s.Config.Kind == "queue" && !s.RecommendationStarted && change == MusicRecommendationQueueChange.Append)
            { RetireLocked(s, "queue-replaced"); return; }
            if (change != MusicRecommendationQueueChange.Mode) s.Navigation++;
            if (change == MusicRecommendationQueueChange.Selection) s.Playback = null;
            PublishLocked();
        }
    }
    private void DisableContinuationLocked(Session s, bool publish = true)
    {
        if (s.PreviousSession is { } committed) DisableContinuationLocked(committed, publish: false);
        s.ModeIntent++;
        s.Continue = false;
        if (s.InitialPending) s.Navigation++;
        // A pending source entry cannot replace an ordinary queue after closure.
        // Provider continuations may finish as preview candidates, never as an
        // automatic admission for an earlier mode intent.
        if (s.Loading && s.InitialPending && !s.CommittingInitial && s.Pending is null &&
            !(s.Config.Kind == "queue" && !s.Config.Restore))
        {
            s.Generation++;
            CancelLocked(s.Cancellation);
            if (s.PreviousSession is { } previous)
            {
                s.PreviousSession = null;
                if (s.Checkpoint == previous.Checkpoint) s.Checkpoint = "";
                RetireLocked(s, "mode-changed", publish: false);
                session = previous;
            }
            else
                RetireLocked(s, "mode-changed", publish: false);
        }
        if (s.Active && !s.InitialPending) SchedulePersistSessionLocked(s);
        if (publish) PublishLocked();
    }
    private async Task EnableContinuationAsync(Session s, int intent, CancellationToken token)
    {
        await queue.InLaneAsync(async (owner, ct) =>
        {
            lock (gate) Require(CurrentLocked(s) && !s.Returning && s.ModeIntent == intent, "STALE_SESSION");
            var before = await ReadAsync(owner, ct).ConfigureAwait(false);
            if (before.Mode != 1)
            {
                Task<MusicQueuePositionResult> admission;
                lock (gate)
                {
                    Require(CurrentLocked(s) && !s.Returning && s.ModeIntent == intent, "STALE_SESSION");
                    admission = owner.SetPlaybackModeAsync(1, ct);
                }
                _ = await admission.ConfigureAwait(false);
                before = await ReadAsync(owner, ct).ConfigureAwait(false);
            }
            lock (gate)
            {
                Require(CurrentLocked(s) && !s.Returning && s.ModeIntent == intent, "STALE_SESSION");
                s.Queue = before; s.QueueEpoch = queue.Epoch;
                if (s.Candidates is { } batch) s.Candidates = batch with { ModeIntent = intent };
                s.Continue = true; s.Attempted.Clear();
                DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "continue_enabled",
                    new Dictionary<string, object?> { ["sessionId"] = s.Id, ["kind"] = s.Config.Kind, ["loading"] = s.Loading });
                SchedulePersistSessionLocked(s);
                PublishLocked();
            }
            return true;
        }, token).ConfigureAwait(false);
        Task? next;
        lock (gate) next = CurrentLocked(s) && s.ModeIntent == intent ? MaybeBeginLocked(s, token) : null;
        if (next is not null) await next.ConfigureAwait(false);
    }
    private void QueueChanged(MusicRecommendationQueueChange change)
    {
        lock (gate)
        {
            if (session is not { } s || !CurrentLocked(s)) return;
            long epoch = queue.Epoch;
            _ = LaunchLocked(async () =>
            {
                try
                {
                    await queue.InLaneAsync(async (owner, token) =>
                    {
                        var snapshot = await ReadAsync(owner, token).ConfigureAwait(false);
                        lock (gate)
                        {
                            if (!CurrentLocked(s) || queue.Epoch != epoch) return false;
                            s.Queue = snapshot; s.QueueEpoch = epoch;
                            if (s.PreviousSession is { } previous) previous.Queue = snapshot;
                            if (s.Config.Kind == "queue" && !s.RecommendationStarted &&
                                !snapshot.Entries.Any(e => e.Id == s.Config.Seed))
                            { RetireLocked(s, "queue-replaced"); return false; }
                            var ids = snapshot.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                            foreach (string old in s.Feedback.Keys.Where(id => !ids.Contains(id)).ToArray()) s.Feedback.Remove(old);
                            if (s.Config.Kind == "queue" && !s.RecommendationStarted && s.Feedback.ContainsKey(snapshot.CurrentId))
                            {
                                s.RecommendationStarted = true;
                                if (!s.InitialPending) SchedulePersistSessionLocked(s);
                            }
                            s.Anchor = snapshot.Tail;
                            if (snapshot.Entries.Length == 0) RetireLocked(s, "queue-empty");
                            else { PublishLocked(); MaybeBeginLocked(s); }
                        }
                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    lock (gate) if (CurrentLocked(s) && queue.Epoch == epoch)
                        { s.Error = ErrorCode(error); s.Status = "error"; PublishLocked(); }
                }
            });
        }
    }
    public Task PrepareCloseAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate)
        {
            stopping = true;
            if (session is { } s) RetireLocked(s, "closing", preservePersisted: true);
            if (preparation is null || preparation.IsFaulted) preparation = Task.Run(PrepareCoreAsync);
            task = preparation;
        }
        // Cancelling a waiter never marks a non-cooperative HTTP/commit as settled.
        return NotificationBarrier(task).WaitAsync(cancellationToken);
    }
    private async Task DrainWorkAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (gate) pending = work.ToArray();
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Individual caller/background tasks retain their own outcomes; drain waits for physical settlement. */ }
        }
    }
    private async Task PrepareCoreAsync()
    {
        await DrainWorkAsync().ConfigureAwait(false);
        lock (gate) Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
    }
    public ValueTask DisposeAsync()
        => new(FinalizeAsync(discardUnsaved: false));

    /// <summary>Discard unsaved state only after all actual work and notifications have settled.</summary>
    public Task AbortAsync() => FinalizeAsync(discardUnsaved: true);

    private Task FinalizeAsync(bool discardUnsaved)
    {
        Task task;
        lock (gate)
        {
            if (closed) return NotificationBarrier(release ?? Task.CompletedTask);
            stopping = true;
            if (session is { } s) RetireLocked(s, "closed", preservePersisted: true);
            if (discardUnsaved)
            {
                if (abortion is null || abortion.IsFaulted)
                    abortion = Task.Run(() => FinalizeCoreAsync(true));
                task = abortion;
            }
            else
            {
                if (disposal is null || disposal.IsFaulted)
                    disposal = Task.Run(() => FinalizeCoreAsync(false));
                task = disposal;
            }
        }
        return NotificationBarrier(task);
    }
    private async Task FinalizeCoreAsync(bool discardUnsaved)
    {
        Task cleanup;
        while (true)
        {
            await DrainWorkAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (work.Count > 0) continue;
                if (!discardUnsaved) Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
                closed = true; notice = null; session = null;
                cleanup = release ??= Task.Run(() =>
                {
                    queue.Changing -= QueueChanging; queue.Changed -= QueueChanged; queue.Selected -= QueueSelected;
                    queue.SelectionSource = null;
                    physical.Dispose();
                });
                break;
            }
        }
        await cleanup.ConfigureAwait(false);
    }
    private Task NotificationBarrier(Task task) => observerFrame.Value is { } frame && Volatile.Read(ref frame.Active) != 0
        ? task : WaitNotificationsAsync(task);
    private async Task WaitNotificationsAsync(Task task)
    {
        await task.ConfigureAwait(false);
        Task notifications; lock (gate) notifications = publisher;
        await notifications.ConfigureAwait(false);
    }
    private void PublishLocked()
    {
        revision++; notice = SnapshotLocked(session);
        if (publishing || closed || stopping) return;
        publishing = true; publisher = Task.Run(Publish);
    }
    private void Publish()
    {
        while (true)
        {
            MusicRecommendationSnapshot value; long generation;
            lock (gate)
            {
                if (notice is null || closed || stopping) { publishing = false; notice = null; return; }
                value = notice; notice = null; generation = revision;
            }
            foreach (var callback in Changed?.GetInvocationList() ?? [])
            {
                lock (gate) if (closed || stopping || generation != revision) break;
                var previous = observerFrame.Value; var frame = new ObserverFrame(); observerFrame.Value = frame;
                try { ((Action<MusicRecommendationSnapshot>)callback)(value); } catch { /* A UI observer cannot break the native drain. */ }
                finally { Volatile.Write(ref frame.Active, 0); observerFrame.Value = previous; }
            }
        }
    }
}
