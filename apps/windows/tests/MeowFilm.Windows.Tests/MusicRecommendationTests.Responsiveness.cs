using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.RecommendationTest;

namespace MeowFilm.Windows.Tests;

public static partial class MusicRecommendationTests
{
    private static async Task InactiveAdvanceResponsiveness()
    {
        await using var f = new Fixture();
        int reads = 0;
        f.Store.AfterRead = () => Interlocked.Increment(ref reads);
        foreach (bool naturalEnd in new[] { false, true })
            Check(!await f.Rec.TryAdvanceAsync("ordinary-entry", naturalEnd, true),
                "no recommendation session leaves ordinary queue navigation to its owner");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RecommendationCancelled(f.Rec.TryAdvanceAsync("ordinary-entry", false, true, cancelled.Token), cancelled.Token);
        await RecommendationCancelled(f.Port.ExecuteAsync("music.queue.get", cancellationToken: cancelled.Token), cancelled.Token);
        Check(reads == 0 && f.Store.Writes == 0,
            "inactive and pre-cancelled requests must not load the cold document or write");
        Check(f.Catalog.Calls.IsEmpty && f.Selections.IsEmpty, "inactive advance must not request recommendations or media");

        var result = await f.Port.ExecuteAsync("music.queue.replace",
            new { songs = new[] { Song("1"), Song("2") }, index = 0 }).WaitAsync(TimeSpan.FromSeconds(10));
        Check(result.GetProperty("ok").GetBoolean(), "a cancelled lane acquisition must not leak the lane");
        var queue = await f.Q().WaitAsync(TimeSpan.FromSeconds(10));
        Check(N(queue, "total") == 2 && Current(queue) == S(queue.GetProperty("entries")[0], "id") &&
            SongId(queue.GetProperty("current").GetProperty("song")) == "1");
        Check(f.Catalog.Calls.IsEmpty && f.Selections.IsEmpty);
    }

    private static async Task HotQueueNextResponsiveness()
    {
        await using var store = new RecommendationWriteStore();
        await using var library = new MusicLibraryOwner(MusicTestStores.For(store));
        var port = new MusicRecommendationQueuePort(library);
        int reads = 0;
        store.Inner.AfterRead = () => Interlocked.Increment(ref reads);
        var replaced = await port.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("1"), Song("2") }, index = 0 });
        Check(replaced.GetProperty("ok").GetBoolean());
        var before = await Ok(library, "music.queue.get");
        string first = S(before.GetProperty("entries")[0], "id"), next = S(before.GetProperty("entries")[1], "id");
        Check(Current(before) == first);
        int writes = store.Inner.Writes, warmReads = reads, uiThread = Environment.CurrentManagedThreadId;

        using var hold = new SynchronousRecommendationHold();
        store.NextWrite = hold;
        var observation = hold.ObserveDispatcher(Dispatcher.CurrentDispatcher);
        Task<JsonElement> step = Task.FromResult(default(JsonElement));
        try
        {
            step = port.ExecuteAsync("music.queue.next");
            bool responsive = await observation;
            var result = await step.WaitAsync(TimeSpan.FromSeconds(10));
            Check(responsive, "Dispatcher must execute before the synchronous queue write is released");
            Check(hold.WorkerThreadId != uiThread, "the real store write must execute outside the UI thread");
            Check(result.GetProperty("ok").GetBoolean() && S(result, "targetId") == next &&
                Current(result) == next, "completed queue next must select the exact second occurrence");
            Check(store.Inner.Writes == writes + 1 && reads == warmReads, "the hot transaction performs one actual write without a cold load");
            var after = await port.ExecuteAsync("music.queue.get").WaitAsync(TimeSpan.FromSeconds(10));
            Check(after.GetProperty("ok").GetBoolean() && N(after, "total") == 2 && Current(after) == next &&
                S(after.GetProperty("current"), "id") == next && SongId(after.GetProperty("current").GetProperty("song")) == "2");
            Check(after.GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText());
            await using var reopened = new MusicLibraryOwner(MusicTestStores.For(store));
            var durable = await Ok(reopened, "music.queue.get");
            Check(Current(durable) == next && N(durable, "total") == 2 &&
                S(durable.GetProperty("current"), "id") == next && SongId(durable.GetProperty("current").GetProperty("song")) == "2",
                "a fresh owner must read the committed next occurrence from the actual document store");
        }
        finally { hold.Release(); await observation; await step.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task ActiveAdvanceResponsiveness()
    {
        await using var f = new Fixture();
        await f.Replace("1");
        f.Catalog.Handle = (_, _) => Task.FromResult(QueueBatch(false, "2", "3"));
        var preview = await f.Start("queue");
        Check(preview.GetProperty("active").GetBoolean() && preview.GetProperty("continue").GetBoolean() &&
            !preview.GetProperty("loading").GetBoolean() && preview.GetProperty("candidates").GetArrayLength() == 2);
        var before = await f.Q();
        string seed = Current(before), session = S(preview, "id");
        int calls = f.Catalog.Calls.Count, writes = f.Store.Writes, observedWrites = 0, uiThread = Environment.CurrentManagedThreadId;
        Check(N(before, "total") == 1 && f.Selections.IsEmpty);
        using var hold = new SynchronousRecommendationHold();
        f.Store.BeforeWrite = () =>
        {
            if (Interlocked.Increment(ref observedWrites) == 1) hold.Wait();
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var observation = hold.ObserveDispatcher(Dispatcher.CurrentDispatcher);
        Task<bool> advance = Task.FromResult(false);
        try
        {
            await RecommendationCancelled(f.Rec.TryAdvanceAsync(seed, true, true, cancelled.Token), cancelled.Token);
            Check(observedWrites == 0 && f.Store.Writes == writes && f.Catalog.Calls.Count == calls && f.Selections.IsEmpty);
            advance = f.Rec.TryAdvanceAsync(seed, true, true);
            bool responsive = await observation;
            bool handled = await advance.WaitAsync(TimeSpan.FromSeconds(10));
            Check(responsive && hold.WorkerThreadId != uiThread && observedWrites > 0,
                "active TryAdvance must admit its actual owner transaction outside Dispatcher even when dependencies complete synchronously");
            Check(!handled, "preview admission leaves ordinary next selection to the queue owner");
            f.Store.BeforeWrite = null;
            var admitted = await f.Q();
            Check(N(admitted, "total") == 3 && Current(admitted) == seed && f.Store.Writes == writes + 1 &&
                f.Selections.IsEmpty && f.Catalog.Calls.Count == calls);
            Check(admitted.GetProperty("entries").EnumerateArray().Select(e => SongId(e.GetProperty("song")))
                .SequenceEqual(["1", "2", "3"]), "the complete recommendation preview is admitted once in provider order");
            string next = S(admitted.GetProperty("entries")[1], "id");
            var result = await f.Port.ExecuteAsync("music.queue.next").WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.GetProperty("ok").GetBoolean() && S(result, "targetId") == next && Current(result) == next);
            var state = await f.Wait(value => S(value, "currentId") == next);
            var selected = await f.Q();
            Check(S(state, "id") == session && state.GetProperty("active").GetBoolean() &&
                state.GetProperty("started").GetBoolean() && state.GetProperty("candidates").GetArrayLength() == 0 &&
                Current(selected) == next && SongId(selected.GetProperty("current").GetProperty("song")) == "2");
            Check(f.Catalog.Calls.Count == calls && f.Selections.IsEmpty,
                "ordinary queue selection must not duplicate provider requests or coordinator media selections");
        }
        finally
        {
            hold.Release(); await observation;
            try { await advance.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { f.Store.BeforeWrite = null; }
        }
    }

    private static async Task RecommendationCancelled(Task request, CancellationToken token)
    {
        try { await request; throw new InvalidOperationException("Expected recommendation cancellation"); }
        catch (OperationCanceledException error) { Check(error.CancellationToken == token, "caller cancellation must be retained"); }
    }

    private sealed class RecommendationWriteStore : IAsyncDisposable
    {
        internal readonly Store Inner = new();
        internal SynchronousRecommendationHold? NextWrite;
        internal RecommendationWriteStore()
        {
            MusicTestStores.Attach(this, Inner);
            Inner.BeforeWrite = () => Interlocked.Exchange(ref NextWrite, null)?.Wait();
        }
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class SynchronousRecommendationHold : IDisposable
    {
        private readonly ManualResetEventSlim entered = new(), release = new();
        private int released;
        internal int WorkerThreadId { get; private set; }
        internal void Wait()
        {
            WorkerThreadId = Environment.CurrentManagedThreadId;
            entered.Set(); release.Wait();
        }
        internal Task<bool> ObserveDispatcher(Dispatcher dispatcher) => Task.Factory.StartNew(() =>
        {
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(10))) return false;
                // Hang guards only: success requires actual dispatch while the synchronous work is still held.
                return dispatcher.InvokeAsync(() => Volatile.Read(ref released) == 0, DispatcherPriority.Normal).Task
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (TimeoutException) { return false; }
            finally { Release(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        internal void Release() { if (Interlocked.Exchange(ref released, 1) == 0) release.Set(); }
        public void Dispose() { Release(); entered.Dispose(); release.Dispose(); }
    }

    private static async Task OnRecommendationSta(Func<Task> body)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = NewSignal(); Dispatcher? owner = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Volatile.Write(ref owner, dispatcher);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? failure = null;
                try { await body(); } catch (Exception error) { failure = error; }
                finally { done.TrySetResult(failure); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "recommendation-responsiveness-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? result;
        try { result = await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { Volatile.Read(ref owner)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (result is not null) ExceptionDispatchInfo.Capture(result).Throw();
    }
}
