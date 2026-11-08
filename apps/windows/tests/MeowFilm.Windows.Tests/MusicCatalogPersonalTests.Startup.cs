using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    private static async Task StartupPassiveHomeActivation()
    {
        await using var f = await Fixture.Create(initialize: false);
        using var caller = new CancellationTokenSource();
        await f.Module.PrepareStartupAsync(caller.Token).WaitAsync(HomeGuard);
        var held = f.App.HoldNext("music.library.list", before: false);
        try
        {
            var initializing = f.Module.InitializeAsync(caller.Token);
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await f.Module.ActivateCatalogLandingAsync().WaitAsync(HomeGuard);
            await Drain();
            AssertStartupReadHeld(f, held);
            Check(!initializing.IsCompleted && !caller.IsCancellationRequested,
                "one passive activation leaves the caller's initialization pending");

            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Module.ActivateCatalogLandingAsync()))
                .WaitAsync(HomeGuard);
            await Drain();
            AssertStartupReadHeld(f, held);
            Check(!initializing.IsCompleted && !caller.IsCancellationRequested,
                "repeated passive activation cannot replace initialization");

            held.Release();
            await initializing.WaitAsync(HomeGuard);
            Check(held.Delivered.Task.IsCompleted && !held.Cancelled.Task.IsCompleted,
                "the original local library receipt completes without cancellation");
            AssertStartupInitialized(f);
            Equal(1, Calls(f, "music.library.list"));
            Equal(0, Calls(f, "music.catalog.home"));

            await f.Module.NavigateAsync("catalog-home", caller.Token).WaitAsync(HomeGuard);
            await f.Module.HomeRefresh.WaitAsync(HomeGuard);
            await f.Module.HomeInitialization.WaitAsync(HomeGuard);
            AssertStartupHomeChain(f);
            AssertNoHomeCommit(f);
        }
        finally { held.Release(); }
    }

    private static async Task StartupShellPassiveHomeActivation()
    {
        await using var f = await Fixture.Create(initialize: false);
        await f.Module.PrepareStartupAsync().WaitAsync(HomeGuard);
        var held = f.App.HoldNext("music.library.list", before: false);
        var shell = new ShellWindow([f.Module], [], () => f.Module.DisposeAsync().AsTask(), f.Module.AbortAsync);
        var status = (TextBlock)shell.FindName("StatusText");
        var panel = (Border)shell.FindName("StatusPanel");
        var navigation = (ListBox)shell.FindName("NavigationList");
        try
        {
            shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Module.ActivateCatalogLandingAsync()))
                .WaitAsync(HomeGuard);
            await Drain();
            AssertStartupReadHeld(f, held);
            status.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            Equal("正在加载…", status.Text);
            Check(!shell.IsVisible && new WindowInteropHelper(shell).Handle == IntPtr.Zero,
                "synthetic Loaded exercises the real shell without showing or creating a window handle");

            held.Release();
            await held.Delivered.Task.WaitAsync(HomeGuard);
            await Until(() => navigation.SelectedItem is ShellNavigationEntry { Route: "catalog-home" } &&
                f.Module.HomeSections.Count > 0 && !f.Module.Busy && status.Text.Length == 0).WaitAsync(HomeGuard);
            await f.Module.HomeRefresh.WaitAsync(HomeGuard);
            await f.Module.HomeInitialization.WaitAsync(HomeGuard);
            await Drain();
            status.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            AssertStartupInitialized(f);
            Equal(1, Calls(f, "music.library.list"));
            Check(!held.Cancelled.Task.IsCompleted, "shell initialization keeps its original local read");
            AssertStartupHomeChain(f);
            Equal("", status.Text);
            Equal(Visibility.Collapsed, panel.Visibility);
            Check(navigation.SelectedItem is ShellNavigationEntry { Route: "catalog-home" },
                "the shell commits its normal first destination after full initialization");
            Check(!shell.IsVisible && new WindowInteropHelper(shell).Handle == IntPtr.Zero,
                "successful startup remains an offscreen component operation");
            AssertNoHomeCommit(f);
        }
        finally
        {
            held.Release();
            await shell.AbortAsync().WaitAsync(HomeGuard);
        }
    }

    private static async Task StartupCallerCancellation()
    {
        await using var f = await Fixture.Create(initialize: false);
        using var caller = new CancellationTokenSource();
        await f.Module.PrepareStartupAsync(caller.Token).WaitAsync(HomeGuard);
        var held = f.App.HoldNext("music.library.list", before: false);
        try
        {
            var initializing = f.Module.InitializeAsync(caller.Token);
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await f.Module.ActivateCatalogLandingAsync().WaitAsync(HomeGuard);
            await Drain();
            AssertStartupReadHeld(f, held);
            Check(!initializing.IsCompleted, "passive activation leaves the explicit caller in control");

            caller.Cancel();
            await held.Cancelled.Task.WaitAsync(HomeGuard);
            await CancelledHomeWait(initializing, caller.Token);
            Check(!held.Delivered.Task.IsCompleted,
                "caller cancellation is observable while the noncooperative local receipt remains held");
            Equal(0, f.App.InitializationCalls);
            Equal(0, Calls(f, "music.catalog.home"));
            Check(!f.Module.CanEditFeedback, "cancelled initialization cannot report feedback preferences as ready");
            held.Release();
            await held.Delivered.Task.WaitAsync(HomeGuard);
            await Drain();

            using var retry = new CancellationTokenSource();
            await f.Module.InitializeAsync(retry.Token).WaitAsync(HomeGuard);
            AssertStartupInitialized(f);
            Equal(2, Calls(f, "music.library.list"));
            Equal(0, Calls(f, "music.catalog.home"));
            await f.Module.NavigateAsync("catalog-home", retry.Token).WaitAsync(HomeGuard);
            await f.Module.HomeRefresh.WaitAsync(HomeGuard);
            await f.Module.HomeInitialization.WaitAsync(HomeGuard);
            AssertStartupHomeChain(f);
            AssertNoHomeCommit(f);
        }
        finally { held.Release(); }
    }

    private static async Task HomeRefreshDuringPassiveActivation()
    {
        await using var f = await Fixture.Create();
        Equal(0, Calls(f, "music.catalog.home"));
        var held = f.App.HoldNext("music.catalog.home", before: false);
        try
        {
            // Refresh is the first explicit home operation; no navigation has consumed startup's refresh intent.
            var refreshing = f.Module.RefreshCatalogAsync();
            await held.Arrived.Task.WaitAsync(HomeGuard);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Module.ActivateCatalogLandingAsync()))
                .WaitAsync(HomeGuard);
            await Drain();
            Check(!refreshing.IsCompleted && !held.Cancelled.Task.IsCompleted && !held.Delivered.Task.IsCompleted,
                "passive callbacks return while the original explicit refresh remains held and uncancelled");
            Equal(1, Calls(f, "music.catalog.home"));
            Equal(1, f.Platform.Requests.Count);
            Equal(0, f.Module.HomeSections.Count);

            held.Release();
            await refreshing.WaitAsync(HomeGuard);
            await f.Module.HomeInitialization.WaitAsync(HomeGuard);
            Check(!held.Cancelled.Task.IsCompleted, "the explicit refresh completes through its own receipt");
            AssertStartupHomeChain(f);
            AssertNoHomeCommit(f);
        }
        finally { held.Release(); }
    }

    private static void AssertStartupReadHeld(Fixture f, Held held)
    {
        Check(f.Module.Busy && !held.Cancelled.Task.IsCompleted && !held.Delivered.Task.IsCompleted,
            "passive visibility cannot cancel or release the initialization read");
        Equal(1, Calls(f, "music.library.list"));
        Equal(0, Calls(f, "music.catalog.home"));
        Equal(0, f.Platform.Requests.Count);
        Equal(0, f.App.InitializationCalls);
    }

    private static void AssertStartupInitialized(Fixture f)
    {
        foreach (string method in new[] { "music.accounts.list", "music.catalogSource.settings.get",
            "music.metadata.settings.get", "music.queue.get", "music.appearance.get",
            "music.device.settings.get", "music.search.settings.get" })
            Equal(1, Calls(f, method));
        Equal(1, f.App.InitializationCalls);
        Check(f.Module.CanEditMetadata && f.Module.CanEditAppearance &&
            f.Module.CanEditFeedback && f.Module.CanEditSearchPreferences,
            "full initialization publishes metadata, appearance, feedback and search preferences");
        Equal(100, f.Output.Volume);
        Equal("", f.Module.Status);
    }

    private static void AssertStartupHomeChain(Fixture f)
    {
        var reads = f.App.Log.Where(call => call.Method == "music.catalog.home").ToArray();
        Equal(1, reads.Count(read => !read.Parameters.TryGetProperty("continuation", out _)));
        Check(reads.Where(read => read.Parameters.TryGetProperty("continuation", out _))
            .Select(read => N(read.Parameters.GetProperty("continuation"), "page")).SequenceEqual(new[] { 2L }),
            "the fixture's first home read has one normal continuation and no replacement chain");
        Check(reads.All(read => S(read.Parameters, "platform") == "wy" &&
            read.Parameters.GetProperty("refresh").GetBoolean()), "the complete home chain uses its original source and refresh");
        Check(f.Module.IsCatalogHome && f.Module.HomeSections.SelectMany(section => section.Items)
            .Select(card => card.ActionId).SequenceEqual(new[] { "500", "501" }),
            "both fixture home pages publish after startup without replay or loss");
        Equal("", f.Module.Status);
    }
}
