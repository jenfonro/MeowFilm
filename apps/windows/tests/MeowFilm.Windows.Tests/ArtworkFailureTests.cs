using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class ArtworkFailureTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string code in new[] { "ARTWORK_FORMAT", "ARTWORK_LIMIT", "ARTWORK_ADDRESS",
            "ARTWORK_REDIRECT", "ARTWORK_DENIED", "ARTWORK_CLOSED" })
            yield return new("Native artwork deterministic failure keeps the note without retry " + code, () => Sta(async () =>
            {
                var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100 };
                var note = new TextBlock { Text = "♪", HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center };
                var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/failed" };
                host.Children.Add(note); host.Children.Add(view); ArtworkScope.SetLoader(host, loader);
                using var surface = Mount(host); Layout(host); await Tick();
                Equal(1, loader.Calls.Count);
                loader.Calls[0].Completion.SetException(new ArtworkException(code)); await Tick();

                var status = FailureStatus(view);
                Check(view.Source is null && view.Children.OfType<Image>().Single().Source is null);
                Check(note.IsVisible);
                Check(view.Background is null || view.Background is SolidColorBrush brush && brush.Color.A == 0,
                    "Failed artwork must keep the underlying note visible");
                Check(!view.Children.OfType<TextBlock>().Any(text => text.IsVisible),
                    "Failure status must not add text over the small cover");
                Check(view.InputHitTest(new Point(30, 30)) is not null, "The failure tooltip must be reachable");
                Equal(Visibility.Collapsed, Retry(view).Visibility);
                foreach (bool show in new[] { false, true, false, true })
                {
                    view.ShowRetry = show; view.Width = show ? 60 : 55;
                    Layout(host); await Tick();
                    Equal(Visibility.Collapsed, Retry(view).Visibility);
                    Equal(status, FailureStatus(view)); Equal(1, loader.Calls.Count);
                }
                host.Children.Remove(view); await Tick();
                CheckNoError(view); Check(view.Source is null);
                Equal(Visibility.Collapsed, Retry(view).Visibility); Equal(1, loader.Calls.Count);
            }));

        foreach (var (name, error) in new (string, Exception)[]
        {
            ("network", new ArtworkException("ARTWORK_NETWORK")),
            ("timeout", new ArtworkException("ARTWORK_TIMEOUT")),
            ("failed", new ArtworkException("ARTWORK_FAILED")),
            ("unknown code", new ArtworkException("ARTWORK_UNKNOWN")),
            ("unknown exception", new InvalidOperationException("Synthetic loader failure"))
        })
            yield return new("Native artwork transient failure recovers only on a retry click " + name, () => Sta(async () =>
            {
                var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100 };
                var view = new ArtworkImage { Width = 60, Height = 60,
                    Address = "https://art.example.test/retry", ShowRetry = false };
                host.Children.Add(view); ArtworkScope.SetLoader(host, loader);
                using var surface = Mount(host); Layout(host); await Tick();
                Equal(1, loader.Calls.Count);
                loader.Calls[0].Completion.SetException(error); await Tick();
                var status = FailureStatus(view);
                Equal(Visibility.Collapsed, Retry(view).Visibility); Check(view.Source is null);
                foreach (bool show in new[] { true, false, true })
                {
                    view.ShowRetry = show; view.Width = show ? 60 : 55;
                    Layout(host); await Tick();
                    Equal(show ? Visibility.Visible : Visibility.Collapsed, Retry(view).Visibility);
                    Equal(status, FailureStatus(view)); Equal(1, loader.Calls.Count);
                }

                var click = new RoutedEventArgs(Button.ClickEvent); Retry(view).RaiseEvent(click);
                Check(click.Handled); Equal(2, loader.Calls.Count);
                CheckNoError(view); Equal(Visibility.Collapsed, Retry(view).Visibility);
                var lease = new FakeLease(); loader.Calls[1].Completion.SetResult(lease); await Tick();
                Check(ReferenceEquals(lease.Image, view.Source));
                CheckNoError(view); Equal(Visibility.Collapsed, Retry(view).Visibility);
                Layout(host); await Tick(); Equal(2, loader.Calls.Count);
                host.Children.Remove(view); await Tick(); Check(lease.Disposed);
            }));

        foreach (bool cancelledTask in new[] { false, true })
            yield return new("Native artwork cancellation stays neutral and accepts a new address " + cancelledTask, () => Sta(async () =>
            {
                var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100 };
                var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/cancelled" };
                host.Children.Add(view); ArtworkScope.SetLoader(host, loader);
                using var surface = Mount(host); Layout(host); await Tick();
                Equal(1, loader.Calls.Count);
                if (cancelledTask) loader.Calls[0].Completion.SetCanceled();
                else loader.Calls[0].Completion.SetException(new OperationCanceledException());
                await Tick();
                foreach (bool show in new[] { false, true })
                {
                    view.ShowRetry = show; view.Width = show ? 60 : 55;
                    Layout(host); await Tick();
                    Check(view.Source is null); CheckNoError(view);
                    Equal(Visibility.Collapsed, Retry(view).Visibility); Equal(1, loader.Calls.Count);
                }
                view.Address = "https://art.example.test/recovered"; Equal(2, loader.Calls.Count);
                var lease = new FakeLease(); loader.Calls[1].Completion.SetResult(lease); await Tick();
                Check(ReferenceEquals(lease.Image, view.Source)); CheckNoError(view);
                Equal(Visibility.Collapsed, Retry(view).Visibility);
                host.Children.Remove(view); await Tick(); Check(lease.Disposed);
            }));

        foreach (bool replaceScope in new[] { false, true })
        {
            string replacement = replaceScope ? "scope" : "address";
            yield return new("Native artwork deterministic failure clears and recovers on new " + replacement, () => Sta(async () =>
            {
                var first = new HeldLoader(); var second = replaceScope ? new HeldLoader() : first;
                var host = new Grid { Width = 100, Height = 100 };
                var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/first" };
                host.Children.Add(view); ArtworkScope.SetLoader(host, first);
                using var surface = Mount(host); Layout(host); await Tick();
                Equal(1, first.Calls.Count);
                first.Calls[0].Completion.SetException(new ArtworkException("ARTWORK_FORMAT")); await Tick();
                _ = FailureStatus(view); Equal(Visibility.Collapsed, Retry(view).Visibility);

                if (replaceScope) ArtworkScope.SetLoader(host, second);
                else view.Address = "https://art.example.test/second";
                Equal(replaceScope ? 1 : 2, second.Calls.Count);
                CheckNoError(view); Equal(Visibility.Collapsed, Retry(view).Visibility);
                var lease = new FakeLease(); second.Calls[^1].Completion.SetResult(lease); await Tick();
                Check(ReferenceEquals(lease.Image, view.Source)); CheckNoError(view);
                Equal(Visibility.Collapsed, Retry(view).Visibility);
                host.Children.Remove(view); await Tick(); Check(lease.Disposed); CheckNoError(view);
            }));

            foreach (var (oldSucceeds, currentSucceeds) in new[] { (false, false), (true, false), (false, true) })
                yield return new($"Native artwork late receipt preserves new {replacement} state old={oldSucceeds} current={currentSucceeds}", () => Sta(async () =>
                {
                    var first = new HeldLoader(); var second = replaceScope ? new HeldLoader() : first;
                    var host = new Grid { Width = 100, Height = 100 };
                    var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/old" };
                    host.Children.Add(view); ArtworkScope.SetLoader(host, first);
                    using var surface = Mount(host); Layout(host); await Tick();
                    Equal(1, first.Calls.Count);
                    var old = first.Calls[0];
                    if (replaceScope) ArtworkScope.SetLoader(host, second);
                    else view.Address = "https://art.example.test/current";
                    Equal(replaceScope ? 1 : 2, second.Calls.Count); Check(old.Token.IsCancellationRequested);
                    CheckNoError(view);

                    FakeLease? current = null;
                    if (currentSucceeds)
                    {
                        current = new FakeLease(); second.Calls[^1].Completion.SetResult(current);
                    }
                    else second.Calls[^1].Completion.SetException(new ArtworkException("ARTWORK_LIMIT"));
                    await Tick();
                    string? status = currentSucceeds ? null : FailureStatus(view);
                    if (currentSucceeds) CheckNoError(view);
                    Check(ReferenceEquals(current?.Image, view.Source));

                    FakeLease? stale = null;
                    if (oldSucceeds) { stale = new FakeLease(); old.Completion.SetResult(stale); }
                    else old.Completion.SetException(new ArtworkException("ARTWORK_NETWORK"));
                    await Tick();
                    if (stale is not null) Check(stale.Disposed);
                    Check(ReferenceEquals(current?.Image, view.Source));
                    if (currentSucceeds) CheckNoError(view);
                    else Equal(status, FailureStatus(view));
                    Equal(Visibility.Collapsed, Retry(view).Visibility);
                    Layout(host); await Tick(); Equal(replaceScope ? 1 : 2, second.Calls.Count);
                    host.Children.Remove(view); await Tick();
                    Check(view.Source is null); CheckNoError(view);
                    if (current is not null) Check(current.Disposed);
                }));
        }

        foreach (bool oldSucceeds in new[] { false, true })
            yield return new("Native artwork retirement ignores a late receipt success=" + oldSucceeds, () => Sta(async () =>
            {
                var loader = new HeldLoader(); var host = new Grid { Width = 100, Height = 100 };
                var view = new ArtworkImage { Width = 60, Height = 60, Address = "https://art.example.test/retired" };
                host.Children.Add(view); ArtworkScope.SetLoader(host, loader);
                using var surface = Mount(host); Layout(host); await Tick();
                Equal(1, loader.Calls.Count);
                host.Children.Remove(view); await Tick();
                Check(loader.Calls[0].Token.IsCancellationRequested);
                CheckNoError(view); Equal(Visibility.Collapsed, Retry(view).Visibility);
                FakeLease? stale = null;
                if (oldSucceeds) { stale = new FakeLease(); loader.Calls[0].Completion.SetResult(stale); }
                else loader.Calls[0].Completion.SetException(new ArtworkException("ARTWORK_NETWORK"));
                await Tick();
                if (stale is not null) Check(stale.Disposed);
                Check(view.Source is null); CheckNoError(view);
                Equal(Visibility.Collapsed, Retry(view).Visibility);
                view.ShowRetry = false; view.ShowRetry = true; Layout(host); await Tick();
                Equal(1, loader.Calls.Count);
            }));
    }

    private sealed class HeldLoader : IArtworkLoader
    {
        internal readonly List<(TaskCompletionSource<IArtworkLease> Completion, CancellationToken Token)> Calls = [];
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            var next = new TaskCompletionSource<IArtworkLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((next, cancellationToken)); return next.Task;
        }
    }
    private sealed class FakeLease : IArtworkLease
    {
        internal bool Disposed;
        public BitmapSource Image { get; }
        internal FakeLease()
        {
            Image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 100, 200, 250, 255 }, 4);
            Image.Freeze();
        }
        public void Dispose() => Disposed = true;
    }
    private static Button Retry(ArtworkImage view) => view.Children.OfType<Button>().Single();
    private static string FailureStatus(ArtworkImage view)
    {
        if (view.ToolTip is not string status || string.IsNullOrWhiteSpace(status))
            throw new InvalidOperationException("Expected a brief artwork failure tooltip");
        Equal(status, AutomationProperties.GetHelpText(view));
        Equal(status, UIElementAutomationPeer.CreatePeerForElement(view)!.GetHelpText());
        return status;
    }
    private static void CheckNoError(ArtworkImage view)
    {
        Check(view.ToolTip is null, "The previous artwork tooltip must be cleared");
        Equal("", AutomationProperties.GetHelpText(view));
        Equal("", UIElementAutomationPeer.CreatePeerForElement(view)!.GetHelpText());
    }
    private static void Layout(FrameworkElement view)
    { view.Measure(new Size(view.Width, view.Height)); view.Arrange(new Rect(0, 0, view.Width, view.Height)); view.UpdateLayout(); }
    private static HwndSource Mount(FrameworkElement root)
    {
        var source = new HwndSource(new HwndSourceParameters("artwork-failure-fixture")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
            PositionX = -32000, PositionY = -32000, Width = (int)root.Width, Height = (int)root.Height
        });
        source.RootVisual = root; return source;
    }
    private static async Task Tick()
    { await Dispatcher.Yield(DispatcherPriority.Background); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Check(bool value, string message = "Artwork failure assertion failed")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Artwork failure expected {expected}, actual {actual}"); }
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            var current = Dispatcher.CurrentDispatcher; Volatile.Write(ref dispatcher, current);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(current));
            _ = current.BeginInvoke(async () =>
            {
                Exception? error = null;
                try { await action(); } catch (Exception e) { error = e; }
                finally { done.TrySetResult(error); current.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "artwork-failure-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? failure;
        try { failure = await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { Volatile.Read(ref dispatcher)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
