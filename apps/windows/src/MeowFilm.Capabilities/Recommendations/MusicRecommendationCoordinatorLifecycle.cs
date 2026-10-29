using System.Text.Json;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;
using static MeowFilm.Capabilities.Recommendations.MusicRecommendationQueuePort;

namespace MeowFilm.Capabilities.Recommendations;

public sealed partial class MusicRecommendationCoordinator
{
    private readonly AsyncLocal<ObserverFrame?> observerFrame = new();
    private sealed class ObserverFrame { internal int Active = 1; }
    private Task publisher = Task.CompletedTask;
    private bool publishing;
    private JsonElement? notice;

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
    private void RetireLocked(Session s, string reason, bool stopPlayback = false)
    {
        if (!s.Active) return;
        s.Active = false; s.Waiting = false; s.Continue = false; s.Status = reason;
        s.Generation++; s.Navigation++; s.Feedback.Clear(); CancelLocked(s.Cancellation);
        var flight = s.Flight; var advance = s.Advance;
        s.Cleanup = LaunchLocked(async () =>
        {
            await flight.ConfigureAwait(false);
            await advance.ConfigureAwait(false);
            lock (gate)
            {
                if (s.Pending is null && s.TransferTo is { } next && CurrentLocked(next) && next.Checkpoint.Length == 0)
                { next.Checkpoint = s.Checkpoint; s.Checkpoint = ""; }
            }
            await DiscardAsync(s).ConfigureAwait(false);
        });
        if (stopPlayback && s.Queue is { } q)
        {
            var selection = SelectionLocked(s, q.CurrentId, false, false, false, MusicRecommendationPlaybackAction.Stop, inactive: true);
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
            RetireLocked(s, "account-invalidated", stopPlayback: s.Batch > 0);
        }
    }
    private void QueueChanging(MusicRecommendationQueueChange change)
    {
        lock (gate)
        {
            if (session is not { } s || !CurrentLocked(s)) return;
            if (s.Batch == 0 || s.Returning || change is MusicRecommendationQueueChange.Replace or
                MusicRecommendationQueueChange.Clear or MusicRecommendationQueueChange.Restore or MusicRecommendationQueueChange.Reset)
            { RetireLocked(s, "queue-replaced"); return; }
            s.Generation++; s.Navigation++; s.Waiting = false;
            CancelLocked(s.Cancellation); s.Status = "ready";
            if (s.Pending is null && s.PreviousConfiguration is { } previousConfig)
            { s.Config = previousConfig; s.PreviousConfiguration = null; s.InitialPending = false; }
            // An edit changes the actual tail/request input. It is not permission
            // for the prior whole-queue response to append later.
            s.Attempted.Clear(); PublishLocked();
        }
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
                    var snapshot = await queue.InLaneAsync(ReadAsync, CancellationToken.None).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!CurrentLocked(s) || queue.Epoch != epoch) return;
                        s.Queue = snapshot; s.QueueEpoch = epoch;
                        var ids = snapshot.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                        foreach (string old in s.Feedback.Keys.Where(id => !ids.Contains(id)).ToArray()) s.Feedback.Remove(old);
                        s.Anchor = snapshot.Tail;
                        if (snapshot.Entries.Length == 0) RetireLocked(s, "queue-empty");
                        else { PublishLocked(); MaybeBeginLocked(s); }
                    }
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
            if (session is { } s) RetireLocked(s, "closing");
            if (preparation is null || preparation.IsFaulted) preparation = Task.Run(DrainWorkAsync);
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
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        lock (gate) Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
    }
    public void Resume()
    {
        lock (gate)
        {
            // Dispose admission is irreversible. A completed prior preparation
            // must not let Resume reopen the owner while disposal is draining.
            Require(!closed && disposal is null, "CLOSED");
            if (!stopping) return;
            Require(preparation is { IsCompletedSuccessfully: true } && work.Count == 0, "NOT_DRAINED");
            Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            session = null; preparation = null; stopping = false; notice = null; PublishLocked();
        }
    }
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (gate)
        {
            if (closed) return new(NotificationBarrier(Task.CompletedTask));
            stopping = true;
            if (session is { } s) RetireLocked(s, "closed");
            if (disposal is null || disposal.IsFaulted)
                disposal = Task.Run(async () =>
                {
                    await DrainWorkAsync().ConfigureAwait(false);
                    lock (gate) { closed = true; notice = null; session = null; }
                    queue.Changing -= QueueChanging; queue.Changed -= QueueChanged;
                    physical.Dispose();
                });
            task = disposal;
        }
        return new(NotificationBarrier(task));
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
            JsonElement value; long generation;
            lock (gate)
            {
                if (notice is null || closed || stopping) { publishing = false; notice = null; return; }
                value = notice.Value; notice = null; generation = revision;
            }
            foreach (var callback in Changed?.GetInvocationList() ?? [])
            {
                lock (gate) if (closed || stopping || generation != revision) break;
                var previous = observerFrame.Value; var frame = new ObserverFrame(); observerFrame.Value = frame;
                try { ((Action<JsonElement>)callback)(value); } catch { /* A UI observer cannot break the native drain. */ }
                finally { Volatile.Write(ref frame.Active, 0); observerFrame.Value = previous; }
            }
        }
    }
}
