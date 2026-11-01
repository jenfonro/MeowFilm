using System.Text.Json;
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
            // Old media events cannot change the intent of a newly selected occurrence.
            if (s.Queue?.CurrentId != state.EntryId) return Task.CompletedTask;
            s.Playback = state;
            if (s.Play != state.IsPlaying) { s.Play = state.IsPlaying; s.MediaIntent++; PublishLocked(); }
            return MaybeBeginLocked(s) ?? Task.CompletedTask;
        }
    }
    private Task? MaybeBeginLocked(Session s)
    {
        if (!CurrentLocked(s) || s.Loading || s.Returning || s.Pending is not null || !s.Continue || s.InitialPending ||
            s.Failures >= 2 || s.RetryAt > clock.GetUtcNow() || s.Queue is not { } q ||
            s.Playback is not { IsPlaying: true, HasStarted: true, MediaReady: true, IsLocal: true } state ||
            state.EntryId != q.CurrentId || q.Tail.Length == 0 || q.CurrentId != q.Tail) return null;
        if (s.Config.Kind != "fm" && !s.Attempted.Add(q.Tail)) return null;
        return BeginBatchLocked(s, false, true);
    }
    public async Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
        CancellationToken cancellationToken = default)
    {
        Task<bool> next;
        lock (gate)
        {
            if (session is not { } s || !CurrentLocked(s) || s.Returning || s.Pending is not null ||
                s.Queue?.CurrentId != entryId && !(s.Config.Kind == "fm" && s.Batch == 0 && s.Loading) ||
                s.Config.Kind != "fm" && !s.Continue) return false;
            if (s.Play != play) { s.Play = play; s.MediaIntent++; }
            if (s.Advance is Task<bool> running && !running.IsCompleted) next = running;
            else
            {
                int navigation = ++s.Navigation; long epoch = queue.Epoch;
                next = LaunchLocked(() => AdvanceAsync(s, entryId, naturalEnd, navigation, epoch, cancellationToken));
                s.Advance = next;
            }
        }
        return await next.ConfigureAwait(false);
    }
    private async Task<bool> AdvanceAsync(Session s, string entryId, bool naturalEnd, int navigation,
        long epoch, CancellationToken token)
    {
        try
        {
            var before = await queue.InLaneAsync(ReadAsync, token).ConfigureAwait(false);
            Task? flight;
            lock (gate)
            {
                if (!NavigationCurrentLocked(s, navigation, epoch) || before.CurrentId != entryId) return true;
                s.Queue = before;
                if (s.Config.Kind != "fm" && before.CurrentId != before.Tail) return false;
                if (s.Batch == 0) flight = s.Flight;
                else if (before.CurrentId == before.Tail)
                {
                    s.Waiting = true; PublishLocked();
                    flight = s.Loading ? s.Flight :
                        naturalEnd && (s.RetryAt > clock.GetUtcNow() || s.Failures >= 2 ||
                            s.Config.Kind != "fm" && s.Attempted.Contains(before.Tail)) ? null :
                        BeginBatchLocked(s, false, naturalEnd, token);
                    if (s.Config.Kind != "fm") s.Attempted.Add(before.Tail);
                }
                else flight = null;
            }
            if (flight is not null) await flight.ConfigureAwait(false);
            MusicRecommendationSelection? selection = null;
            await queue.InLaneAsync(async (owner, ct) =>
            {
                var current = await ReadAsync(owner, ct).ConfigureAwait(false);
                lock (gate)
                    if (!NavigationCurrentLocked(s, navigation, epoch) || current.CurrentId != entryId || s.Pending is not null)
                        return false;
                string next = current.Index >= 0 && current.Index + 1 < current.Entries.Length ? current.Entries[current.Index + 1].Id : "";
                if (next.Length > 0)
                {
                    var operation = new PendingOperation(Guid.NewGuid().ToString("D"), "select", [], null, "", false, naturalEnd, epoch);
                    var result = await owner.ExecuteAsync("music.queue.recommendation.select",
                        new { nonce = operation.Nonce, expectedVersion = current.Version, id = next }, ct).ConfigureAwait(false);
                    if (!result.GetProperty("ok").GetBoolean())
                    {
                        string code = Text(result, "code");
                        if (code is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT") MarkUnknown(s, operation, code);
                        throw new RecommendationException(code);
                    }
                    try
                    {
                        current = await ReadAsync(owner, CancellationToken.None).ConfigureAwait(false);
                        await owner.AcknowledgeRecommendationAsync(operation.Nonce).ConfigureAwait(false);
                    }
                    catch
                    {
                        MarkUnknown(s, operation, "COMMIT_UNKNOWN");
                        throw;
                    }
                }
                lock (gate)
                {
                    if (!NavigationCurrentLocked(s, navigation, epoch)) return false;
                    s.Queue = current; s.Waiting = false;
                    selection = next.Length > 0
                        ? SelectionLocked(s, next, s.Play, naturalEnd, false)
                        : SelectionLocked(s, entryId, false, naturalEnd, false, MusicRecommendationPlaybackAction.Stop);
                    PublishLocked();
                }
                return true;
            }, token).ConfigureAwait(false);
            if (selection is not null) await SelectAsync(s, selection, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (NavigationCurrentLocked(s, navigation, epoch))
                {
                    s.Waiting = false; s.Error = ErrorCode(error); s.Status = s.Pending is null ? "error" : "commit-unknown";
                    PublishLocked();
                }
            }
        }
        return true;
    }
    private bool NavigationCurrentLocked(Session s, int navigation, long epoch) =>
        CurrentLocked(s) && !s.Returning && s.Navigation == navigation && queue.Epoch == epoch;
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
        lock (gate) return session is { } s && CurrentLocked(s) && s.QueueEpoch == queue.Epoch &&
            s.Queue?.Entries.Any(e => e.Id == entryId) == true ? s.Feedback.GetValueOrDefault(entryId) : null;
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
                var result = await owner.ExecuteAsync("music.queue.recommendation.restore",
                    new { nonce = operation.Nonce, expectedVersion = before.Version, checkpoint = s.Checkpoint }, ct).ConfigureAwait(false);
                if (!result.GetProperty("ok").GetBoolean())
                {
                    string code = Text(result, "code");
                    if (code is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT") MarkUnknown(s, operation, code);
                    throw new RecommendationException(code);
                }
                try
                {
                    var restored = await ReadAsync(owner, CancellationToken.None).ConfigureAwait(false);
                    lock (gate)
                    {
                        s.Checkpoint = "";
                        if (ReferenceEquals(session, s) && s.Navigation == navigation && queue.Epoch == epoch)
                        {
                            s.Active = false; s.Returning = false; s.Continue = false; s.Waiting = false; s.Status = "returned";
                            s.Feedback.Clear(); s.Queue = restored;
                            if (before.CurrentId != restored.CurrentId)
                                selection = SelectionLocked(s, restored.CurrentId, false, false, true,
                                    restored.CurrentId.Length == 0 ? MusicRecommendationPlaybackAction.Stop : MusicRecommendationPlaybackAction.Select, inactive: true);
                            PublishLocked();
                        }
                    }
                    await owner.AcknowledgeRecommendationAsync(operation.Nonce).ConfigureAwait(false);
                }
                catch
                {
                    MarkUnknown(s, operation, "COMMIT_UNKNOWN");
                    throw;
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
