using System.Text.Json;

namespace MeowFilm.Capabilities.Search;

public sealed partial class MusicSearchCoordinator
{
    private void StopLocked(Query query)
    {
        if (query.Stopped) return;
        query.SettlementNoticePending = query.Computing || query.States.Any(s => s.Loading);
        query.Stopped = true; query.DataVersion++;
        PublishLocked(query);
    }
    private async Task DrainPhysicalAsync(Query query)
    {
        Task[] tasks;
        lock (gate) tasks = [.. query.States.Select(s => s.Flight), query.Fusion];
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    private async Task DrainAsync(Query query)
    {
        await DrainPhysicalAsync(query).ConfigureAwait(false);
        Task<JsonElement>? cancellation;
        lock (gate) cancellation = query.CancellationDrain;
        if (cancellation is not null) await cancellation.ConfigureAwait(false);
    }
    private async Task<JsonElement> CancelQueryAsync(Query query)
    {
        Cancel(query.Lifetime);
        await DrainPhysicalAsync(query).ConfigureAwait(false);
        lock (gate)
        {
            if (query.SettlementNoticePending)
            {
                query.SettlementNoticePending = false;
                if (!closed && !stopping && queries.TryGetValue(query.Kind, out var current) && ReferenceEquals(query, current))
                    PublishLocked(query);
            }
            return Snapshot(query);
        }
    }
    private static void ReleaseLocked(Query query)
    {
        if (query.Released) return;
        query.Released = true; query.Lifetime.Dispose();
        foreach (var state in query.States) state.Slots.Dispose();
    }

    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate) task = PrepareLocked();
        return WithNotificationBarrier(task).WaitAsync(cancellationToken);
    }
    private Task PrepareLocked()
    {
        if (resumption is { IsCompleted: false } resume) return PrepareAfterAsync(resume);
        stopping = true;
        foreach (var query in queries.Values) StopLocked(query);
        return preparation ??= Task.Run(PrepareCoreAsync);
    }
    private async Task PrepareAfterAsync(Task resume)
    {
        await resume.ConfigureAwait(false);
        Task task;
        lock (gate) task = PrepareLocked();
        await task.ConfigureAwait(false);
    }
    private async Task PrepareCoreAsync()
    {
        Query[] retiring; Task? start;
        lock (gate) { retiring = queries.Values.ToArray(); start = replacement?.Work; }
        foreach (var query in retiring)
        {
            Cancel(query.Lifetime);
            foreach (var state in query.States)
                if (state.Cancellation is { } physical) Cancel(physical);
        }
        foreach (var query in retiring) await DrainAsync(query).ConfigureAwait(false);
        if (start is not null) await start.ConfigureAwait(false);
    }
    public Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate)
        {
            if (closed) return Task.FromException(new InvalidOperationException("CLOSED"));
            if (preparation is null && resumption is null) return Task.CompletedTask;
            var prepared = preparation!;
            task = resumption ??= Task.Run(() => ResumeAsync(prepared));
        }
        return WithNotificationBarrier(task).WaitAsync(cancellationToken);
    }
    private async Task ResumeAsync(Task prepared)
    {
        await prepared.ConfigureAwait(false);
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("CLOSED");
            foreach (var query in queries.Values) ReleaseLocked(query);
            queries.Clear(); notices.Clear(); keyword = "";
            preparation = resumption = null; stopping = false;
        }
    }
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (gate)
        {
            closed = stopping = true;
            foreach (var query in queries.Values) StopLocked(query);
            var prepared = preparation ??= Task.Run(PrepareCoreAsync);
            var resume = resumption;
            task = disposal ??= Task.Run(() => DisposeCoreAsync(prepared, resume));
        }
        return new(WithNotificationBarrier(task));
    }
    private async Task DisposeCoreAsync(Task prepared, Task? resume)
    {
        await prepared.ConfigureAwait(false);
        if (resume is not null) { try { await resume.ConfigureAwait(false); } catch (InvalidOperationException) { } }
        lock (gate)
        {
            foreach (var query in queries.Values) ReleaseLocked(query);
            queries.Clear(); notices.Clear(); cache.Clear(); cacheBytes = 0;
        }
    }

    private Task WithNotificationBarrier(Task work)
    {
        // Work completion always means real request/fusion/resource settlement.
        // A callback cannot join its own stack; only that causally active callback
        // is excluded. Independent callers still join the publisher after work.
        return observerFrame.Value is { } frame && Volatile.Read(ref frame.Active) != 0
            ? work : WaitForNotificationsAsync(work);
    }
    private async Task WaitForNotificationsAsync(Task work)
    {
        await work.ConfigureAwait(false);
        Task notifications;
        lock (gate) notifications = publisher;
        await notifications.ConfigureAwait(false);
    }
}
