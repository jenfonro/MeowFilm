using MeowFilm.AppHost;

namespace MeowFilm.Windows.Tests;

// Controller-only tests: no Application, product Main, native input, real runtime or user data.
public static class StartupTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(4);

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("callback starts inline on caller thread and context", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup();
            var caller = Environment.CurrentManagedThreadId;
            var context = new UnpumpedContext();
            var previous = SynchronizationContext.Current;
            var invoked = false;
            Task<StartupOutcome> work;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                work = startup.StartAsync(_ =>
                {
                    Equal(caller, Environment.CurrentManagedThreadId, "callback must not be moved to Task.Run");
                    Same(context, SynchronizationContext.Current, "UI-affine startup keeps caller context");
                    invoked = true;
                    return pending.Task;
                });
                True(invoked, "callback invoked before StartAsync returns");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            pending.Complete(StartupOutcome.Started);
            Equal(StartupOutcome.Started, await Await(work), "successful outcome");
            Equal(0, context.PostCount, "wrapper completion must not depend on caller dispatcher");
        });
        yield return Case("duplicate pending Start returns identical task and ignores new delegate", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup();
            var calls = 0;
            var first = startup.StartAsync(_ => { calls++; return pending.Task; });
            var duplicate = startup.StartAsync(_ => throw new InvalidOperationException("must not run"));
            Same(first, duplicate, "pending task identity");
            Equal(1, calls, "one callback");
            False(first.IsCompleted, "no synthetic completion");
            pending.Complete(StartupOutcome.Started);
            Equal(StartupOutcome.Started, await Await(duplicate), "same outcome");
        });
        yield return Case("concurrent Start calls accept one attempt", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup();
            var calls = 0;
            var ready = NewCompletion();
            var callers = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            {
                await ready.Task.ConfigureAwait(false);
                return (Work: startup.StartAsync(token =>
                {
                    Interlocked.Increment(ref calls);
                    return pending.Task;
                }), Marker: true);
            })).ToArray();
            ready.SetResult();
            var accepted = await Await(Task.WhenAll(callers));
            foreach (var call in accepted) Same(accepted[0].Work, call.Work, "all callers share task");
            Equal(1, calls, "no concurrent duplicate callbacks");
            pending.Complete(StartupOutcome.Started);
            Equal(StartupOutcome.Started, await Await(accepted[0].Work), "accepted work completes");
        });
        foreach (var terminal in new[] { StartupOutcome.Started, StartupOutcome.CleanupFailed, StartupOutcome.Cancelled })
            yield return Case($"{terminal} outcome permanently prevents retry", async () =>
            {
                using var startup = new AppStartup();
                var calls = 0;
                var first = startup.StartAsync(_ => { calls++; return Task.FromResult(terminal); });
                Equal(terminal, await Await(first), "terminal outcome preserved");
                var next = startup.StartAsync(_ => { calls++; return Task.FromResult(StartupOutcome.Started); });
                Same(first, next, "terminal result task retained");
                Equal(1, calls, "terminal result cannot be retried");
            });
        yield return Case("explicit RetryableFailure permits a fresh token and exactly one next generation", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup();
            var firstToken = default(CancellationToken);
            var nextToken = default(CancellationToken);
            var calls = 0;
            var first = startup.StartAsync(token =>
            {
                firstToken = token; calls++;
                return Task.FromResult(StartupOutcome.RetryableFailure);
            });
            Equal(StartupOutcome.RetryableFailure, await Await(first), "caller explicitly certified safe retry");
            var second = startup.StartAsync(token => { nextToken = token; calls++; return pending.Task; });
            False(ReferenceEquals(first, second), "new generation has new completion");
            False(firstToken == nextToken, "cancellation registration scopes are distinct");
            Same(second, startup.StartAsync(_ => throw new InvalidOperationException("duplicate retry")), "retry still single-flight");
            Equal(2, calls, "exactly one second attempt");
            pending.Complete(StartupOutcome.Started);
            Equal(StartupOutcome.Started, await Await(second), "retry succeeds");
            Same(second, startup.StartAsync(_ => throw new InvalidOperationException("third attempt")), "success ends retry permission");
        });
        yield return Case("unclassified raw fault is CleanupFailed and cannot silently retry", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup();
            var work = startup.StartAsync(_ => pending.Task);
            pending.Fail();
            Equal(StartupOutcome.CleanupFailed, await Await(work), "exception is not evidence of completed cleanup");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("unsafe retry")), "fault blocks retries");
            True(await Await(startup.StopAsync()), "faulted action has settled, not necessarily cleaned up");
            Equal(StartupOutcome.CleanupFailed, await Await(work), "settled flag does not overwrite cleanup failure");
        });
        yield return Case("synchronous throw is contained without advertising safe retry", async () =>
        {
            using var startup = new AppStartup();
            var work = startup.StartAsync(_ => throw new InvalidOperationException("private-startup-detail"));
            Equal(StartupOutcome.CleanupFailed, await Await(work), "sync fault classified conservatively");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("unsafe retry")), "no second callback");
        });
        yield return Case("null and undefined callback results fail closed", async () =>
        {
            using var nullResult = new AppStartup();
            var first = nullResult.StartAsync(_ => null!);
            Equal(StartupOutcome.CleanupFailed, await Await(first), "null task is not retryable");
            Same(first, nullResult.StartAsync(_ => Task.FromResult(StartupOutcome.Started)), "null result cannot reopen startup");
            using var undefined = new AppStartup();
            var second = undefined.StartAsync(_ => Task.FromResult((StartupOutcome)99));
            Equal(StartupOutcome.CleanupFailed, await Await(second), "unknown enum is not permission");
            Same(second, undefined.StartAsync(_ => Task.FromResult(StartupOutcome.Started)), "unknown result terminal");
        });
        yield return Case("raw cancellation maps to terminal Cancelled rather than safe retry", async () =>
        {
            using var startup = new AppStartup();
            var work = startup.StartAsync(_ => Task.FromCanceled<StartupOutcome>(new CancellationToken(true)));
            Equal(StartupOutcome.Cancelled, await Await(work), "task cancellation is an explicit terminal outcome");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("unsafe retry")), "cancellation alone cannot certify retry");
        });
        yield return Case("Stop before Start is settled and permanently denies callbacks", async () =>
        {
            using var startup = new AppStartup();
            var stopped = startup.StopAsync();
            True(await Await(stopped), "no accepted startup remains");
            Same(stopped, startup.StopAsync(), "one Stop task");
            Equal(StartupOutcome.Cancelled, await Await(startup.StartAsync(_ => throw new InvalidOperationException("must not start"))),
                "stopped controller does not invoke action");
        });
        yield return Case("pending startup close cancels first but waits for actual settlement", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup(new ManualClock());
            var token = default(CancellationToken);
            var work = startup.StartAsync(value => { token = value; return pending.Task; });
            var stopped = startup.StopAsync();
            True(token.IsCancellationRequested, "cancellation requested before StopAsync returns");
            False(stopped.IsCompleted, "requesting cancellation is not observed settlement");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("restart after close")), "no new startup while stopping");
            pending.Complete(StartupOutcome.Started); // Deliberately noncooperative callback.
            True(await Await(stopped), "actual startup now settled");
            Equal(StartupOutcome.Cancelled, await Await(work), "late success must not mount a product after close");
        });
        yield return Case("default Stop deadline is exactly 25 seconds and repeated Stop does not extend it", async () =>
        {
            using var pending = new Pending();
            var clock = new ManualClock();
            using var startup = new AppStartup(clock);
            var work = startup.StartAsync(_ => pending.Task);
            var stopped = startup.StopAsync();
            Equal(1, clock.TimerCreations, "one observation timer");
            clock.Advance(TimeSpan.FromSeconds(25) - TimeSpan.FromTicks(1));
            False(stopped.IsCompleted, "no early timeout");
            Same(stopped, startup.StopAsync(), "deadline and task shared");
            Equal(1, clock.TimerCreations, "repeat stop must not restart timer");
            clock.Advance(TimeSpan.FromTicks(1));
            False(await Await(stopped), "timeout cannot be called cleanup completion");
            False(work.IsCompleted, "noncooperative startup remains pending");
            pending.Complete(StartupOutcome.Started);
            Equal(StartupOutcome.Cancelled, await Await(work), "late success remains fenced");
            False(await Await(startup.StopAsync()), "late completion cannot rewrite timeout result");
        });
        yield return Case("configured shorter deadline is respected once", async () =>
        {
            using var pending = new Pending();
            var clock = new ManualClock();
            using var startup = new AppStartup(clock, TimeSpan.FromMilliseconds(7));
            var work = startup.StartAsync(_ => pending.Task);
            var stopped = startup.StopAsync();
            clock.Advance(TimeSpan.FromMilliseconds(7) - TimeSpan.FromTicks(1));
            False(stopped.IsCompleted, "configured deadline not early");
            clock.Advance(TimeSpan.FromTicks(1));
            False(await Await(stopped), "configured deadline reached");
            pending.Complete(StartupOutcome.CleanupFailed);
            Equal(StartupOutcome.CleanupFailed, await Await(work), "stop does not conceal cleanup failure");
        });
        yield return Case("concurrent Stop callers share one deadline and one cancellation", async () =>
        {
            using var pending = new Pending();
            var clock = new ManualClock();
            using var startup = new AppStartup(clock);
            var token = default(CancellationToken);
            var calls = 0;
            var cancelled = NewCompletion();
            var work = startup.StartAsync(value => { token = value; return pending.Task; });
            using var registration = token.Register(() => { Interlocked.Increment(ref calls); cancelled.TrySetResult(); });
            var callers = await Await(Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
                Task.Run(() => (Work: startup.StopAsync(), Marker: true)))));
            foreach (var call in callers) Same(callers[0].Work, call.Work, "Stop task shared across threads");
            await Await(cancelled.Task);
            Equal(1, calls, "one cancellation notification");
            Equal(1, clock.TimerCreations, "one stop deadline");
            pending.Complete(StartupOutcome.Cancelled);
            True(await Await(callers[0].Work), "settled before deadline");
            Equal(StartupOutcome.Cancelled, await Await(work), "cancelled outcome");
        });
        yield return Case("Stop preserves CleanupFailed and never treats settled as cleanup success", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup(new ManualClock());
            var work = startup.StartAsync(_ => pending.Task);
            var stopped = startup.StopAsync();
            pending.Complete(StartupOutcome.CleanupFailed);
            True(await Await(stopped), "settled bool only");
            Equal(StartupOutcome.CleanupFailed, await Await(work), "failure preserved after stop cancellation");
            Same(work, startup.StartAsync(_ => Task.FromResult(StartupOutcome.Started)), "cleanup failure never retried");
        });
        yield return Case("late raw fault settles without a pumping SynchronizationContext", async () =>
        {
            using var pending = new Pending();
            var clock = new ManualClock();
            using var startup = new AppStartup(clock);
            var context = new UnpumpedContext();
            var previous = SynchronizationContext.Current;
            Task<StartupOutcome> work;
            Task<bool> stopped;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                work = startup.StartAsync(_ => pending.Task);
                stopped = startup.StopAsync();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            clock.Advance(TimeSpan.FromSeconds(25));
            False(await Await(stopped), "timeout observed without dispatcher");
            pending.Fail();
            Equal(StartupOutcome.CleanupFailed, await Await(work), "raw late exception observed without dispatcher");
            False(await Await(startup.StopAsync()), "late fault cannot alter prior timeout");
            Equal(0, context.PostCount, "no continuation posted to dead dispatcher");
        });
        yield return Case("Stop prevents retry even after previously certified RetryableFailure", async () =>
        {
            using var startup = new AppStartup();
            var work = startup.StartAsync(_ => Task.FromResult(StartupOutcome.RetryableFailure));
            Equal(StartupOutcome.RetryableFailure, await Await(work), "safe cleanup before close");
            True(await Await(startup.StopAsync()), "previous attempt settled");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("restart after Stop")), "Stop revokes retry permission");
        });
        yield return Case("Stop cancels current retry generation not previous attempt token", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup(new ManualClock());
            var first = default(CancellationToken);
            var second = default(CancellationToken);
            await Await(startup.StartAsync(token => { first = token; return Task.FromResult(StartupOutcome.RetryableFailure); }));
            var work = startup.StartAsync(token => { second = token; return pending.Task; });
            var stopped = startup.StopAsync();
            True(second.IsCancellationRequested, "current generation cancelled");
            False(first.IsCancellationRequested, "retired attempt does not share lifetime token");
            pending.Complete(StartupOutcome.RetryableFailure);
            Equal(StartupOutcome.Cancelled, await Await(work), "close beats late retry permission");
            True(await Await(stopped), "current attempt settled");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("third attempt")), "no next generation after Stop");
        });
        yield return Case("throwing cancellation registration cannot escape Stop", async () =>
        {
            using var pending = new Pending();
            using var startup = new AppStartup(new ManualClock());
            var token = default(CancellationToken);
            var invoked = NewCompletion();
            var work = startup.StartAsync(value => { token = value; return pending.Task; });
            using var registration = token.Register(() =>
            {
                invoked.TrySetResult();
                throw new InvalidOperationException("private-cancellation-detail");
            });
            var stopped = startup.StopAsync();
            await Await(invoked.Task);
            False(stopped.IsCompleted, "registration fault cannot masquerade as startup settlement");
            pending.Complete(StartupOutcome.Cancelled);
            True(await Await(stopped), "startup later settles independently of registration fault");
            Equal(StartupOutcome.Cancelled, await Await(work), "no unhandled registration exception");
        });
        yield return Case("hung cancellation registration cannot extend startup stop deadline", async () =>
        {
            using var pending = new Pending();
            using var release = new ManualResetEventSlim();
            var entered = NewCompletion();
            var clock = new ManualClock();
            using var startup = new AppStartup(clock);
            var token = default(CancellationToken);
            var work = startup.StartAsync(value => { token = value; return pending.Task; });
            using var registration = token.Register(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(Guard)) throw new InvalidOperationException("cancellation test guard");
            });
            try
            {
                var stopped = startup.StopAsync();
                await Await(entered.Task);
                clock.Advance(TimeSpan.FromSeconds(25));
                False(await Await(stopped), "same bounded observation despite blocked registration");
                False(work.IsCompleted, "startup has not settled");
                pending.Complete(StartupOutcome.Cancelled);
                Equal(StartupOutcome.Cancelled, await Await(work), "startup completion independent of registration");
            }
            finally { release.Set(); }
        });
        yield return Case("synchronous noncooperative callback is not claimed preempted", SynchronousCallbackAsync);
        yield return Case("external startup callback runs outside controller lock", async () =>
        {
            using var startup = new AppStartup(new ManualClock());
            Task<bool>? stopped = null;
            var work = startup.StartAsync(token =>
            {
                var crossThread = Task.Run(() => { stopped = startup.StopAsync(); });
                True(crossThread.Wait(Guard), "another thread must enter Stop while callback is on stack");
                True(token.IsCancellationRequested, "reentrant external close cancels accepted attempt");
                return Task.FromResult(StartupOutcome.Started);
            });
            Equal(StartupOutcome.Cancelled, await Await(work), "reentrant close fences success");
            True(stopped is not null && await Await(stopped), "reentrant stop waits for callback return");
        });
        yield return Case("Dispose cancels pending startup once and permanently rejects another action", async () =>
        {
            using var pending = new Pending();
            var startup = new AppStartup(new ManualClock());
            var token = default(CancellationToken);
            var work = startup.StartAsync(value => { token = value; return pending.Task; });
            startup.Dispose(); startup.Dispose();
            True(token.IsCancellationRequested, "Dispose initiates Stop without blocking");
            Same(work, startup.StartAsync(_ => throw new InvalidOperationException("start after Dispose")), "no action after disposal");
            var stopped = startup.StopAsync();
            False(stopped.IsCompleted, "Dispose does not pretend pending startup finished");
            pending.Complete(StartupOutcome.Started);
            Equal(StartupOutcome.Cancelled, await Await(work), "disposed controller cannot advertise new product");
            True(await Await(stopped), "actual completion observed");
        });
        yield return Case("Dispose before Start prevents startup and Stop remains idempotent", async () =>
        {
            var startup = new AppStartup();
            startup.Dispose(); startup.Dispose();
            Equal(StartupOutcome.Cancelled, await Await(startup.StartAsync(_ => throw new InvalidOperationException("disposed"))),
                "unused disposed controller is terminal");
            var stopped = startup.StopAsync();
            Same(stopped, startup.StopAsync(), "same Stop after Dispose");
            True(await Await(stopped), "nothing in flight");
        });
        yield return Case("invalid stop deadlines and null action do not admit startup", async () =>
        {
            foreach (var invalid in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(25) + TimeSpan.FromTicks(1) })
                Throws<ArgumentOutOfRangeException>(() => new AppStartup(stopTimeout: invalid), "positive bounded stop policy");
            using var startup = new AppStartup();
            Throws<ArgumentNullException>(() => startup.StartAsync(null!), "null delegate rejected without consuming initial slot");
            Equal(StartupOutcome.Started, await Await(startup.StartAsync(_ => Task.FromResult(StartupOutcome.Started))),
                "valid initial attempt still allowed");
        });
    }

    private static async Task SynchronousCallbackAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = NewCompletion();
        var returned = new TaskCompletionSource<Task<StartupOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = NewCompletion();
        var clock = new ManualClock();
        using var startup = new AppStartup(clock);
        var thread = new Thread(() =>
        {
            try
            {
                var work = startup.StartAsync(_ =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(Guard)) throw new InvalidOperationException("synchronous test guard");
                    return Task.FromResult(StartupOutcome.Started);
                });
                returned.TrySetResult(work);
            }
            catch (Exception error) { returned.TrySetException(error); }
            finally { exited.TrySetResult(); }
        }) { IsBackground = true, Name = "meowfilm-startup-code-test" };
        thread.Start();
        try
        {
            await Await(entered.Task);
            var shared = startup.StartAsync(_ => throw new InvalidOperationException("second callback"));
            False(shared.IsCompleted, "published promise tracks blocked invocation");
            var stopped = startup.StopAsync();
            clock.Advance(TimeSpan.FromSeconds(25));
            False(await Await(stopped), "deadline does not claim synchronous callback was killed");
            False(returned.Task.IsCompleted, "original StartAsync caller is still blocked");
            release.Set();
            Same(shared, await Await(returned.Task), "original caller gets same accepted promise");
            Equal(StartupOutcome.Cancelled, await Await(shared), "late callback return remains cancelled");
        }
        finally { release.Set(); await Await(exited.Task); }
    }

    private static TestCase Case(string name, Func<Task> body) => new("startup: " + name,
        () => body().WaitAsync(TimeSpan.FromSeconds(8)));
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(Guard);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(Guard);
    private sealed class Pending : IDisposable
    {
        private readonly TaskCompletionSource<StartupOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StartupOutcome> Task => completion.Task;
        public void Complete(StartupOutcome value) => completion.TrySetResult(value);
        public void Fail() => completion.TrySetException(new InvalidOperationException("private-startup-detail"));
        public void Dispose() => Complete(StartupOutcome.Cancelled);
    }
    private sealed class UnpumpedContext : SynchronizationContext
    {
        private int posts;
        public int PostCount => Volatile.Read(ref posts);
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref posts);
    }

    // No Test SDK/packages: real TimeProvider timers, deterministically advanced by each case.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ClockTimer> timers = [];
        private long ticks;
        private int timerCreations;
        public int TimerCreations => Volatile.Read(ref timerCreations);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (gate)
            {
                Interlocked.Increment(ref timerCreations);
                var timer = new ClockTimer(this, callback, state);
                timers.Add(timer); timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            long target; lock (gate) target = checked(ticks + amount.Ticks);
            for (var safety = 0; safety < 1000; safety++)
            {
                ClockTimer? next;
                lock (gate)
                {
                    next = timers.Where(timer => !timer.Disposed && timer.Due <= target).MinBy(timer => timer.Due);
                    if (next is null) { ticks = target; return; }
                    ticks = next.Due;
                    next.Due = next.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(ticks + next.Period.Ticks);
                }
                next.Callback(next.State);
            }
            throw new InvalidOperationException("Unexpected repeating test timer");
        }
        private sealed class ClockTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; } = long.MaxValue;
            public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner.ticks + dueTime.Ticks);
                    Period = period <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : period;
                    return true;
                }
            }
            public void Dispose() { lock (owner.gate) { Disposed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void False(bool condition, string message) => True(!condition, message);
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'");
    }
    private static void Same(object? expected, object? actual, string message) => True(ReferenceEquals(expected, actual), message);
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
