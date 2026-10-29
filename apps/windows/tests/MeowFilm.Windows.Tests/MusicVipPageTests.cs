using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicVipPageTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("VIP presentation is dormant off route and publishes successful branches independently", async () =>
        {
            var source = new Source
            {
                Snapshot = Snapshot(sign: null, signError: "UPSTREAM_ERROR")
            };
            await using var page = new MusicVipPage(source, Dispatcher.CurrentDispatcher);
            await page.SetSurfaceAsync(false, true);
            await page.SetSurfaceAsync(true, false);
            Equal(0, source.Reads);

            await page.SetSurfaceAsync(true, true);
            Equal(1, source.Reads);
            True(page.ShowAccount && !page.ShowSign && page.ShowBanner && page.HasCards,
                "one failed branch must not clear independent successful cards");
            Equal("NinonZQ", page.Account?.DisplayName);
            Equal("PLAYLIST", page.Banner?.ResourceType);

            await page.SetSurfaceAsync(false, true);
            True(!page.HasCards && page.Account is null && page.Banner is null,
                "unloaded VIP presentation clears synchronously");
        });

        yield return Case("VIP invalidation cancels one physical read and coalesces to one serial reread", async () =>
        {
            var source = new Source();
            await using var page = new MusicVipPage(source, Dispatcher.CurrentDispatcher);
            await page.SetSurfaceAsync(true, true);
            var hold = source.HoldNextRead();
            source.Invalidate();
            await Drain();
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            True(!page.HasCards, "invalidation clears before the old request physically settles");

            source.Invalidate();
            source.Invalidate();
            await Drain();
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            hold.Release.TrySetResult();
            await Until(() => source.Reads == 3 && page.HasCards);
            Equal(3, source.Reads);
        });

        yield return Case("VIP sign receipt disables a stale unsigned refresh without replacing independent branches", async () =>
        {
            var source = new Source
            {
                SignReceipt = new(7, Sign(false))
            };
            await using var page = new MusicVipPage(source, Dispatcher.CurrentDispatcher);
            await page.SetSurfaceAsync(true, true);
            var account = page.Account;
            var banner = page.Banner;
            await page.SignAsync();
            Equal(1, source.Signs);
            Equal(7L, source.LastSignGeneration);
            True(page.Sign?.SignedToday == false && page.SignedToday && !page.CanSign,
                "successful receipt, not a stale reread, owns sign admission");
            Equal("已打卡", page.SignButtonText);
            True(ReferenceEquals(account, page.Account) && ReferenceEquals(banner, page.Banner),
                "signing does not reread or replace independent branches");
            await page.SignAsync();
            Equal(1, source.Signs);
            Equal(1, source.Reads);
        });

        yield return Case("VIP unconfirmed sign admission stays disabled and is never replayed", async () =>
        {
            var source = new Source { SignError = "COMMIT_UNKNOWN" };
            await using var page = new MusicVipPage(source, Dispatcher.CurrentDispatcher);
            await page.SetSurfaceAsync(true, true);
            await page.SignAsync();
            Equal(1, source.Signs);
            True(!page.SignedToday && !page.CanSign && page.SignButtonText == "打卡",
                "an unknown remote receipt must neither fake success nor permit a replay");
            await page.SignAsync();
            Equal(1, source.Signs);
        });

        yield return Case("VIP close preparation and disposal wait for physical source settlement", async () =>
        {
            var source = new Source();
            var page = new MusicVipPage(source, Dispatcher.CurrentDispatcher);
            var hold = source.HoldNextRead();
            var loading = page.SetSurfaceAsync(true, true);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var preparing = page.PrepareShutdownAsync();
            True(!page.HasCards && !preparing.IsCompleted,
                "preparation clears and seals before physical settlement");
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            hold.Release.TrySetResult();
            await Task.WhenAll(loading, preparing);

            await page.CancelShutdownPreparationAsync();
            Equal(2, source.Reads);
            var closingHold = source.HoldNextRead();
            source.Invalidate();
            await Drain();
            await closingHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var first = page.DisposeAsync().AsTask();
            var second = page.DisposeAsync().AsTask();
            True(ReferenceEquals(first, second) && !first.IsCompleted,
                "one disposal receipt owns the active physical read");
            await closingHold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            closingHold.Release.TrySetResult();
            await Task.WhenAll(first, second);
        });
    }

    private static MusicVipPageSnapshot Snapshot(MusicVipSignCardData? sign = null,
        string signError = "") => new(7,
        new(7, "NinonZQ", "https://img.invalid/avatar.jpg", "SVIP · 2026.11.27"),
        sign ?? Sign(false),
        new("https://img.invalid/banner.jpg", "PLAYLIST", "500"),
        SignError: signError);

    private static MusicVipSignCardData Sign(bool signed) => new(
        "10月黑胶乐签", "第2天，得3成长值", false, false,
        signed ? "已打卡" : "打卡", "https://img.invalid/sign-bg.jpg",
        [new("今天", signed, "", signed ? 1 : 0, true)]);

    private static TestCase Case(string name, Func<Task> run) => new(name, () => OnSta(run));
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}");
    }
    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static async Task Drain()
    {
        for (var index = 0; index < 3; index++)
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("VIP condition did not settle");
            await Drain();
            if (!predicate()) await Task.Delay(1);
        }
    }

    private sealed class Source : IMusicVipPageSource
    {
        private readonly Queue<Hold> holds = [];
        internal int Reads, Signs;
        internal long LastSignGeneration;
        internal MusicVipPageSnapshot Snapshot = MusicVipPageTests.Snapshot();
        internal MusicVipSignReceipt SignReceipt = new(7, Sign(true));
        internal string SignError = "";
        public event Action? VipPageInvalidated;

        internal Hold HoldNextRead()
        {
            var hold = new Hold();
            holds.Enqueue(hold);
            return hold;
        }

        internal void Invalidate() => VipPageInvalidated?.Invoke();

        public async Task<MusicVipPageSnapshot> ReadVipPageAsync(
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            if (holds.TryDequeue(out var hold))
            {
                using var registration = token.Register(() => hold.Cancelled.TrySetResult());
                hold.Arrived.TrySetResult();
                await hold.Release.Task;
                token.ThrowIfCancellationRequested();
            }
            return Snapshot;
        }

        public Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Signs++;
            LastSignGeneration = expectedGeneration;
            if (SignError.Length > 0) throw new MusicOperationException(SignError);
            return Task.FromResult(SignReceipt);
        }
    }

    private sealed class Hold
    {
        internal TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task OnSta(Func<Task> action)
    {
        var completed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                Exception? error = null;
                try
                {
                    await action();
                    await Drain();
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    completed.TrySetResult(error);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            try
            {
                Dispatcher.Run();
            }
            finally
            {
                stopped.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "music-vip-page-test"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var failure = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
