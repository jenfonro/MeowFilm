using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;
using static MeowFilm.Capabilities.Recommendations.MusicRecommendationQueuePort;

namespace MeowFilm.Capabilities.Recommendations;

public sealed partial class MusicRecommendationCoordinator
{
    public Task OnPlaybackAsync(MusicRecommendationPlaybackState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (session is not { } s || !CurrentLocked(s) || s.Returning) return Task.CompletedTask;
            if (!state.IsLocal) { RetireLocked(s, "remote"); return s.Cleanup; }
            s.Playback = state;
            if (s.Queue?.CurrentId == state.EntryId && s.Play != state.IsPlaying)
            { s.Play = state.IsPlaying; s.MediaIntent++; PublishLocked(); }
            return MaybeBeginLocked(s) ?? Task.CompletedTask;
        }
    }
    private Task? MaybeBeginLocked(Session s, CancellationToken caller = default)
    {
        if (!CurrentLocked(s) || s.Loading || s.Returning || s.Pending is not null || !s.Continue ||
            s.InitialPending || s.Exhausted || s.Queue is not { Mode: 1 } q ||
            s.Playback is not { IsPlaying: true, HasStarted: true, MediaReady: true, IsLocal: true } state ||
            state.EntryId != q.CurrentId) return null;
        if (q.CurrentId != q.Tail) return null;
        if (s.Candidates is { } batch) return !q.HasPriority && batch.ModeIntent == s.ModeIntent
            ? BeginCandidatesLocked(s, batch, null, caller, automatic: true) : null;
        // Special mode may admit the preview at the playing tail, but another
        // provider page still requires an actual recommendation selection.
        if (s.Config.Kind == "queue" && !s.RecommendationStarted) return null;
        string attempt = "tail:" + s.Batch + ":" + s.Offset + ":" + q.Tail;
        if (!s.Attempted.Add(attempt)) return null;
        return BeginBatchLocked(s, false, true, caller);
    }
    public async Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (session is not { } active || !CurrentLocked(active)) return false;
        }
        Task? commit = null;
        Session? target;
        long epoch = queue.Epoch;
        // This read is local. Never wait for an in-flight provider request to select the next song.
        var snapshot = await queue.InLaneAsync(ReadAsync, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            target = session;
            if (target is not { } s || !CurrentLocked(s) || s.Returning || s.Pending is not null ||
                snapshot.CurrentId != entryId || queue.Epoch != epoch) return false;
            s.Queue = snapshot;
            if (s.Play != play) { s.Play = play; s.MediaIntent++; }
            if (s.Config.Kind == "queue" && s.Candidates is { } batch && !s.Loading &&
                !snapshot.HasPriority && snapshot.CurrentId == snapshot.Tail &&
                s.Continue && snapshot.Mode == 1 && batch.ModeIntent == s.ModeIntent)
                commit = BeginCandidatesLocked(s, batch, null, cancellationToken, automatic: true);
        }
        if (commit is not null) await commit.ConfigureAwait(false);
        // The sole queue owner applies the global loop/shuffle strategy, including explicit play-next priority.
        return queue.Epoch != epoch || target is not null && !ReferenceEquals(session, target);
    }
    private MusicRecommendationSelection SelectionLocked(Session s, string entryId, bool play, bool automatic,
        bool restoreOnly, MusicRecommendationPlaybackAction action = MusicRecommendationPlaybackAction.Select, bool inactive = false)
    {
        int navigation = s.Navigation, intent = s.MediaIntent; long epoch = queue.Epoch;
        bool Current()
        {
            lock (gate) return !closed && !stopping && ReferenceEquals(session, s) &&
                (s.Active || inactive) && s.Navigation == navigation && queue.Epoch == epoch &&
                (inactive || action == MusicRecommendationPlaybackAction.Stop || s.MediaIntent == intent) &&
                (action == MusicRecommendationPlaybackAction.Stop || s.Queue?.CurrentId == entryId);
        }
        return new(action, s.Id, entryId, s.Queue?.Version ?? 0, play, automatic, restoreOnly,
            !inactive && s.Active ? s.Feedback.GetValueOrDefault(entryId) : null, Current);
    }
    private async Task SelectAsync(Session s, MusicRecommendationSelection selection, CancellationToken token)
    {
        if (!selection.IsCurrent()) return;
        bool accepted;
        try { accepted = await selectAsync(selection, token).ConfigureAwait(false); }
        catch { accepted = false; }
        lock (gate)
        {
            if (!accepted && selection.IsCurrent())
            { s.Error = "PLAYBACK_FAILED"; s.Status = "error"; PublishLocked(); }
        }
    }
    public MusicRecommendationFeedback? GetFeedback(string entryId)
    {
        lock (gate)
        {
            if (session is not { } active || !CurrentLocked(active)) return null;
            var s = active.InitialPending ? active.PreviousSession : active;
            return s is not null && s.QueueEpoch == queue.Epoch &&
                s.Queue?.Entries.Any(e => e.Id == entryId) == true ? s.Feedback.GetValueOrDefault(entryId) : null;
        }
    }
    private MusicQueueSourceRecord? QueueSelectionSource(string entryId)
    {
        lock (gate)
        {
            if (session is not { } active || !CurrentLocked(active) || active.Returning) return null;
            var s = active.InitialPending ? active.PreviousSession : active;
            if (s is null || s.QueueEpoch != queue.Epoch || s.Config.Kind != "queue" ||
                !s.Feedback.ContainsKey(entryId)) return null;
            return BuildContext(s, s.Heartbeat).QueueSource;
        }
    }
    private void QueueSelected(string entryId)
    {
        lock (gate)
        {
            if (session is not { } active || !CurrentLocked(active) || active.Returning) return;
            var s = active.InitialPending ? active.PreviousSession : active;
            if (s is null || s.QueueEpoch != queue.Epoch || s.Config.Kind != "queue" ||
                !s.Feedback.ContainsKey(entryId) || s.RecommendationStarted) return;
            s.RecommendationStarted = true;
            if (!s.InitialPending) SchedulePersistSessionLocked(s);
            PublishLocked();
        }
    }
    private async Task ReturnAsync(Session s, Task previous, CancellationToken token)
    {
        long epoch = queue.Epoch; int navigation = s.Navigation;
        try
        {
            await previous.ConfigureAwait(false);
            MusicRecommendationSelection? selection = null;
            await queue.InLaneAsync(async (owner, ct) =>
            {
                var before = await ReadAsync(owner, ct).ConfigureAwait(false);
                lock (gate) Require(CurrentLocked(s) && s.Returning && s.Navigation == navigation && queue.Epoch == epoch, "STALE_SESSION");
                var operation = new PendingOperation(Guid.NewGuid().ToString("D"), "restore", [], null, "", false, false, epoch);
                try
                {
                    _ = await owner.RestoreRecommendationQueueAsync(operation.Nonce, before.Version, s.Checkpoint,
                        retainCurrent: true, ct).ConfigureAwait(false);
                }
                catch (Library.MusicLibraryException error)
                {
                    if (error.Code is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT") MarkUnknown(s, operation, error.Code);
                    throw new RecommendationException(error.Code);
                }
                var restored = await ReadAsync(owner, CancellationToken.None).ConfigureAwait(false);
                lock (gate)
                {
                    s.Checkpoint = "";
                    if (ReferenceEquals(session, s) && s.Navigation == navigation && queue.Epoch == epoch)
                    {
                        s.Active = false; s.Returning = false; s.Continue = false; s.Status = "returned";
                        s.Feedback.Clear(); s.Queue = restored;
                        DebugLog.Info(DebugCategories.MusicPlayback, "recommendation", "session_returned",
                            new Dictionary<string, object?> { ["sessionId"] = s.Id, ["kind"] = s.Config.Kind });
                        SchedulePersistSessionLocked(null);
                        if (before.CurrentId != restored.CurrentId)
                            selection = SelectionLocked(s, restored.CurrentId, false, false, true,
                                restored.CurrentId.Length == 0 ? MusicRecommendationPlaybackAction.Stop : MusicRecommendationPlaybackAction.Select, inactive: true);
                        PublishLocked();
                    }
                }
                return true;
            }, token).ConfigureAwait(false);
            if (selection is not null) await SelectAsync(s, selection, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (ReferenceEquals(session, s))
                { s.Returning = false; s.Error = ErrorCode(error); s.Status = s.Pending is null ? "error" : "commit-unknown"; PublishLocked(); }
            }
        }
    }
}
