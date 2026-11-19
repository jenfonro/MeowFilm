using System.Reflection;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Desktop;
using MeowFilm.Media;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    internal static IEnumerable<TestCase> PlaybackSessionSourceCases()
    {
        foreach (string source in new[] { "ordinary", "fm", "similar", "heartbeat" })
            yield return Case("playback mode offers recommendation only before entering a special source: " + source,
                () => PlaybackSessionSourceMenu(source));
    }

    internal static IEnumerable<TestCase> PlaybackSessionControlCases()
    {
        yield return Case("playback mode enters recommendation and separates continuation from checkpoint return", PlaybackSessionActions);
        foreach (string retirement in new[] { "reopen", "catalog-action", "recommendation-command",
            "recommendation-snapshot", "account-generation", "unload" })
            yield return Case("playback mode rejects a retained native menu action after " + retirement,
                () => PlaybackSessionRetirement(retirement));
        yield return Case("playback mode retains loading and queue recovery after a real FM failure", PlaybackSessionRetry);
        yield return Case("FM window placement resources focus and lifetime use the in-content channel dialog", PlaybackFmWindow);
        yield return Case("channel native hit barrier covers transport seek and menus and survives deactivation", PlaybackFmPointerBarrier);
        yield return Case("footer native automation toggles menus switches queue and rejects late quality", PlaybackFooterPointer);
        foreach (string retirement in new[] { "catalog-action", "account-generation" })
            yield return Case("FM choices reject a retired " + retirement + " and accept the fresh dialog",
                () => PlaybackFmAuthority(retirement));
    }

    private sealed class SessionHost : IDisposable
    {
        internal Grid Root { get; } = new();
        internal PlayerDialogHost Dialog { get; } = new();
        internal DesktopWindow Window { get; }
        private bool closed;
        internal SessionHost(MusicPlayerView footer)
        {
            Root.RowDefinitions.Add(new()); Root.RowDefinitions.Add(new() { Height = new GridLength(80) });
            Root.UseResource(Grid.BackgroundProperty, "MeowFilm.Brush.AppBackground");
            Root.Children.Add(footer); Grid.SetRow(footer, 1);
            Root.Children.Add(Dialog); Grid.SetRowSpan(Dialog, 2);
            Window = MusicTestUi.Window(Root, 1260, 760); Window.Closed += (_, _) => closed = true;
            Dialog.Attach(Window); Window.Activate();
        }
        internal void Mount() { Window.Content = Root; Window.AttachRoot(); Layout(Root, 1260, 760); }
        internal void Unmount() { Window.Content = null; }
        public void Dispose() { if (closed) return; Dialog.Close(); Window.Content = null; Window.Close(); }
    }
    private static SessionHost SessionSurface(MusicPlayerView footer, string name) => new(footer);
    private static async Task SessionLoaded(MusicPlayerView footer)
    { await Until(() => footer.IsLoaded && footer.XamlRoot is not null); await Drain(); }
    private static async Task<MenuFlyout> OpenSessionMenu(MusicPlayerView footer)
    {
        var menu = (MenuFlyout)footer.FindName("ModeMenu");
        PlayerMenuInput.For(footer).CloseCurrent();
        await Until(() => !menu.IsOpen); await Drain();
        DesktopTestUi.Invoke((Button)footer.FindName("ModeButton"));
        await Until(() => menu.IsOpen && menu.Items.All(item => item.IsLoaded)); await Drain();
        Check(menu.IsOpen && ReferenceEquals(menu.XamlRoot, footer.XamlRoot), "mode menu is in the real native root");
        Check(menu.Items.All(item => item is ToggleMenuFlyoutItem), "source actions are flat native choices");
        return menu;
    }
    private static ToggleMenuFlyoutItem SessionAction(MenuFlyout menu, string label) =>
        menu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == label);
    private static void InvokeSessionAction(ToggleMenuFlyoutItem item)
    { Check(item.IsEnabled, "native action is enabled"); MusicTestUi.Invoke(item); }
    private static async Task OpenSessionFm(MusicPlayerView footer)
    {
        var menu = await OpenSessionMenu(footer);
        InvokeSessionAction(SessionAction(menu, "切换频道"));
        await Until(() => !menu.IsOpen && PlayerDialogHost.Find(footer)?.IsOpen == true && footer.FmPanel.IsLoaded); await Drain();
    }
    private static async Task OrdinarySessionSong(Fixture f)
    {
        f.Platform.Total = 3;
        await f.Module.NavigateAsync("catalog-playlist/wy/555", default);
        await f.Module.PlayCatalogPlaylistAsync(); await Drain();
        Check(f.Module.CanRecommendCurrent && !f.Module.HasActiveRecommendation,
            "a playing native WY occurrence is eligible for explicit recommendation");
    }
    private static async Task PlaybackSessionSourceMenu(string source)
    {
        await using var f = await Fixture.Create();
        switch (source)
        {
            case "ordinary":
                await OrdinarySessionSong(f);
                break;
            case "fm":
                await f.Module.NavigateAsync("catalog-fm", default);
                await f.Module.PlayCatalogPlaylistAsync();
                break;
            case "similar":
                await f.Module.NavigateAsync("catalog-daily", default);
                await f.Module.SimilarCatalogCardAsync(FirstSong(f));
                break;
            case "heartbeat":
                f.Platform.PlaylistChange = request => Edit(Page(request, 3), value => value["heartbeatAccountId"] = "42");
                await OrdinarySessionSong(f);
                f.Output.Start();
                await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey);
                break;
        }
        await Drain();
        Equal(source != "ordinary", f.Module.HasActiveRecommendation);
        Equal(source == "fm", f.Module.HasPlaybackFm);
        Equal(source == "heartbeat", f.Module.HasPlaybackHeartbeat);
        Check(f.Module.CanRecommendCurrent && !f.Module.HasQueueRecommendations,
            "the real native occurrence is eligible; absence of duplicate entry cannot pass for a missing identity");
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "playback-session-source-" + source);
        await Drain();
        var menu = await OpenSessionMenu(footer);
        string[] expected = source switch
        {
            "ordinary" => ["推荐模式", "列表循环", "单曲循环", "随机播放"],
            "fm" => ["私人漫游", "列表循环", "单曲循环", "随机播放", "切换频道"],
            "similar" => ["相似模式", "列表循环", "单曲循环", "随机播放"],
            "heartbeat" => ["退出心动模式", "心动模式", "列表循环", "单曲循环", "随机播放"],
            _ => throw new InvalidOperationException(source)
        };
        var actions = menu.Items.OfType<ToggleMenuFlyoutItem>().ToArray();
        Check(actions.Select(item => item.Text).SequenceEqual(expected), "source exposes one ordered playback-mode menu");
        Check(actions.All(item => item.IsEnabled), "the expected controls remain usable native actions");
        Equal(1, actions.Count(item => item.IsChecked));
        Equal(source == "ordinary" ? "列表循环" : source == "fm" ? "私人漫游" : source == "similar" ? "相似模式" : "心动模式",
            actions.Single(item => item.IsChecked).Text);
        menu.Hide();
        surface.Unmount();
    }
    private static async Task PlaybackSessionActions()
    {
        await using var f = await Fixture.Create(); await OrdinarySessionSong(f);
        var before = await f.Queue();
        int opens = f.Output.Opens, resumes = f.Output.Resumes;
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "playback-session-actions");
        await Drain();
        var button = (Button)footer.FindName("ModeButton");
        var icon = (MusicPlaybackIcon)button.Content;
        var menu = await OpenSessionMenu(footer);
        Equal("repeat", icon.Key);
        Check(!menu.Items.OfType<ToggleMenuFlyoutItem>().Any(item => item.Text is "退出推荐模式" or "切换频道"),
            "ordinary playback has no invented session actions");
        InvokeSessionAction(SessionAction(menu, "推荐模式"));
        await Until(() => f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation && !f.Module.RecommendationLoading);
        Equal("queue", S(f.App.Last("music.recommendations.start"), "kind"));
        Check(f.App.Last("music.recommendations.start").GetProperty("restore").GetBoolean(),
            "explicit entry requests a real original-queue checkpoint");
        var recommended = await f.Queue();
        menu = await OpenSessionMenu(footer);
        Equal("sparkles", icon.Key);
        Equal(1, menu.Items.OfType<ToggleMenuFlyoutItem>().Count(item => item.Text == "推荐模式"));
        Check(!menu.Items.OfType<ToggleMenuFlyoutItem>().Any(item => item.Text == "切换频道"), "queue recommendation has no FM channels");
        Check(SessionAction(menu, "退出推荐模式").IsEnabled);
        var disable = SessionAction(menu, "列表循环");
        Check(!disable.IsChecked, "the current special mode is selected instead of loop");
        InvokeSessionAction(disable);
        await Until(() => !f.Module.RecommendationContinue && !f.Module.RecommendationLoading);
        Check(f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation,
            "disabling continuation leaves the active session and checkpoint intact");
        Equal("repeat", icon.Key);
        Equal(recommended.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
        Equal(S(recommended, "currentId"), S(await f.Queue(), "currentId"));
        Equal(opens, f.Output.Opens); Equal(resumes, f.Output.Resumes); Equal(1, f.Module.Mode);
        menu = await OpenSessionMenu(footer);
        Check(SessionAction(menu, "推荐模式").IsEnabled && SessionAction(menu, "列表循环").IsChecked);
        InvokeSessionAction(SessionAction(menu, "退出推荐模式"));
        await Until(() => !f.Module.HasActiveRecommendation && !f.Module.CanReturnRecommendation);
        Equal("repeat", icon.Key);
        Equal(before.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
        Equal(S(before, "currentId"), S(await f.Queue(), "currentId"));
        Equal(opens, f.Output.Opens); Equal(resumes, f.Output.Resumes);
        surface.Unmount();
    }
    private static async Task PlaybackSessionRetirement(string retirement)
    {
        await using var f = await Fixture.Create(); await OrdinarySessionSong(f);
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "playback-session-retirement");
        await Drain();
        var menu = await OpenSessionMenu(footer);
        var retained = SessionAction(menu, "推荐模式");
        string oldKey = f.Module.PlaybackSourceActionKey;
        Task starting = Task.CompletedTask;
        Held? held = null;
        try
        {
            switch (retirement)
            {
                case "reopen":
                    menu = await OpenSessionMenu(footer);
                    Equal(oldKey, f.Module.PlaybackSourceActionKey);
                    break;
                case "catalog-action":
                    f.Module.InvalidateCatalogAction();
                    Check(menu.IsOpen && oldKey != f.Module.PlaybackSourceActionKey,
                        "synchronous intent invalidation rejects clicks even before a notification closes the menu");
                    break;
                case "recommendation-command":
                    held = f.App.HoldNext("music.recommendations.start", before: true);
                    starting = f.Module.RecommendCurrentAsync(f.Module.PlaybackPresentationKey);
                    await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                    Check(!menu.IsOpen, "a newly admitted command retires the old menu before its receipt");
                    break;
                case "recommendation-snapshot":
                    await f.App.ExecuteAsync("music.recommendations.start", new {
                        kind = "queue", platform = "wy", seedEntryId = S(await f.Queue(), "currentId"),
                        restore = true, @continue = true, local = true, play = false });
                    await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
                    Check(!menu.IsOpen && oldKey != f.Module.PlaybackSourceActionKey,
                        "the real owner's changed snapshot retires a menu without a view-issued command");
                    break;
                case "account-generation":
                    long generation = f.Module.Accounts.Single(account => account.Platform == "wy").Generation;
                    f.App.AccountGeneration++;
                    await f.Module.ReadAccountsAsync(); await Drain();
                    Equal(generation + 1, f.Module.Accounts.Single(account => account.Platform == "wy").Generation);
                    Check(oldKey != f.Module.PlaybackSourceActionKey, "same user with a new account generation has fresh authority");
                    break;
                case "unload":
                    surface.Unmount(); await Drain();
                    Check(!footer.IsLoaded && !menu.IsOpen, "unload retires the native menu");
                    break;
            }
            int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
            int continues = f.App.Log.Count(call => call.Method == "music.recommendations.continue");
            int writes = f.Store.LibraryWrites, opens = f.Output.Opens;
            double position = f.Output.PositionSeconds;
            MusicTestUi.Invoke(retained); await Drain();
            Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(continues, f.App.Log.Count(call => call.Method == "music.recommendations.continue"));
            Equal(writes, f.Store.LibraryWrites); Equal(opens, f.Output.Opens);
            Equal(position, f.Output.PositionSeconds);
        }
        finally { held?.Release(); await starting; }
        if (retirement == "unload")
        {
            surface.Mount(); await Drain();
        }
        menu = await OpenSessionMenu(footer);
        bool expectedQueue = retirement is "recommendation-command" or "recommendation-snapshot";
        Equal(expectedQueue, f.Module.HasQueueRecommendations);
        if (expectedQueue)
        {
            int modes = f.App.Log.Count(call => call.Method == "music.queue.mode");
            InvokeSessionAction(SessionAction(menu, "列表循环"));
            await Until(() => !f.Module.RecommendationContinue);
            Equal(modes + 1, f.App.Log.Count(call => call.Method == "music.queue.mode"));
        }
        else
        {
            int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
            InvokeSessionAction(SessionAction(menu, "推荐模式"));
            await Until(() => f.Module.HasQueueRecommendations && !f.Module.RecommendationLoading);
            Equal(starts + 1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        }
        surface.Unmount();
    }
    private static async Task PlaybackSessionRetry()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default);
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "playback-session-retry");
        var held = f.Platform.HoldNextFm();
        var starting = f.Module.PlayCatalogPlaylistAsync();
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Until(() => f.Module.RecommendationLoading);
            var menu = await OpenSessionMenu(footer);
            Check(f.Module.RecommendationLoading && menu.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.Text != "重试取歌"), "loading does not introduce a mode-menu retry");
            menu.Hide();
        }
        finally { held.Fail("NETWORK_ERROR"); await starting; }
        await Until(() => !f.Module.RecommendationLoading && f.Module.CanRetryRecommendation);
        int requests = f.Platform.Requests.OfType<MusicFmRequest>().Count();
        int retries = f.App.Log.Count(call => call.Method == "music.recommendations.retry");
        var retryMenu = await OpenSessionMenu(footer);
        Check(!retryMenu.Items.OfType<ToggleMenuFlyoutItem>().Any(item => item.Text == "重试取歌"));
        retryMenu.Hide();
        var queue = (MusicQueueView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
        var retry = (Button)queue.FindName("RetryRecommendationButton");
        Check(retry.IsEnabled && f.Module.HasRecommendationPanel, "real FM failures have a queue recovery action");
        DesktopTestUi.Invoke(retry);
        await Until(() => f.Module.HasPlaybackFm && !f.Module.RecommendationLoading);
        Equal(retries + 1, f.App.Log.Count(call => call.Method == "music.recommendations.retry"));
        Equal(requests + 1, f.Platform.Requests.OfType<MusicFmRequest>().Count());
        Equal("fm", ((MusicPlaybackIcon)((Button)footer.FindName("ModeButton")).Content).Key);
        var fmMenu = await OpenSessionMenu(footer);
        Check(SessionAction(fmMenu, "切换频道").IsEnabled &&
            !fmMenu.Items.OfType<ToggleMenuFlyoutItem>().Any(item => item.Text == "退出推荐模式"),
            "retried FM retains sibling channels without inventing a checkpoint");
        fmMenu.Hide();
        var changingHold = f.Platform.HoldNextFm();
        var changing = f.Module.ChangePlaybackFmAsync("familiar", "", f.Module.PlaybackFmActionKey);
        await changingHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Until(() => f.Module.RecommendationLoading);
            var loading = await OpenSessionMenu(footer);
            Check(f.Module.RecommendationLoading && SessionAction(loading, "切换频道").IsEnabled,
                "a pending FM switch preserves the live source and its existing channel-change capability");
            InvokeSessionAction(SessionAction(loading, "切换频道")); await Drain();
            Check(PlayerDialogHost.Find(footer)!.IsOpen, "loading does not add an unrequested FM-entry restriction");
        }
        finally { changingHold.Fail("NETWORK_ERROR"); await changing; }
        await Until(() => !f.Module.RecommendationLoading);
        Equal("default", f.Module.PlaybackFmMode);
        Check(!PlayerDialogHost.Find(footer)!.IsOpen, "the changed receipt retires the pending popup");
        var failedMenu = await OpenSessionMenu(footer);
        Check(f.Module.HasPlaybackFm && f.Module.CanRetryRecommendation && f.Module.HasRecommendationPanel &&
            !failedMenu.Items.OfType<ToggleMenuFlyoutItem>().Any(item => item.Text == "推荐模式"),
            "a failed switch preserves explicit recovery without offering duplicate entry into the still-active FM source");
        failedMenu.Hide();
        surface.Unmount();
    }
    private static PlayerSurfaceInput? SessionInput(MusicPlayerView footer) =>
        (PlayerSurfaceInput?)typeof(MusicPlayerView).GetField("surfaceInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(footer);
    private static async Task PlaybackFmWindow()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        await f.Module.ChangePlaybackFmAsync("scene", "YINGSHI", f.Module.PlaybackFmActionKey);
        await f.Module.NavigateAsync("catalog-home", default);
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "fm-window"); await SessionLoaded(footer);
        var panel = footer.FmPanel;
        var scroll = (ScrollViewer)footer.FmPanel.FindName("FmScroll");
        var modes = (StackPanel)footer.FmPanel.FindName("FmModeChoices");
        var scenes = (Grid)footer.FmPanel.FindName("FmSceneChoices");
        var button = (Button)footer.FindName("ModeButton");
        var close = (Button)footer.FmPanel.FindName("CloseFmButton");
        void Centered()
        {
            var center = panel.TransformToVisual(surface.Root).TransformPoint(new Point(panel.ActualWidth / 2, panel.ActualHeight / 2));
            Check(Math.Abs(center.X - surface.Root.ActualWidth / 2) < 1 && Math.Abs(center.Y - surface.Root.ActualHeight / 2) < 1,
                "the channel is centered by the same native root layout");
        }
        void Closed()
        {
            Check(!surface.Dialog.IsOpen && modes.Children.Count == 0 && scenes.Children.Count == 0, "close retires all generated choices");
            Check(!DesktopTestUi.HasFocus(panel), "closed choices retain no native focus");
            Check(ReferenceEquals(FocusManager.GetFocusedElement(surface.Root.XamlRoot), button), "normal close returns real focus to mode");
        }
        Check(SessionInput(footer) is not null, "mounted footer has one actual surface gesture owner");
        await OpenSessionFm(footer); Centered();
        Check(ReferenceEquals(panel.XamlRoot, surface.Root.XamlRoot) && DesktopVisual.Contains(surface.Dialog, panel), "channel is not a separate native window");
        Equal(4, modes.Children.Count); Equal(43, scenes.Children.Count);
        var selected = scenes.Children.OfType<Button>().Single(choice => AutomationProperties.GetItemStatus(choice) == "已选中");
        Equal("YINGSHI", ((MusicCatalogChoice)selected.Tag).Key);
        Check(ReferenceEquals(selected, FocusManager.GetFocusedElement(surface.Root.XamlRoot)), "committed scene receives actual native focus");
        Check(scroll.VerticalOffset > 0, "the committed lower scene is scrolled into view");
        var bounds = DesktopTestUi.Bounds(selected, scroll);
        Check(bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5, "the focused scene is inside the viewport");
        Check(!DesktopVisual.Contains(scroll, close) && DesktopVisual.IsVisible(close), "close stays reachable while scenes scroll");
        Check(OverlayScrollChrome.GetEnabled(scroll) && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "FM consumes the native shared overlay scrolling policy");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(scroll) ?? new ScrollViewerAutomationPeer(scroll);
        double offset = scroll.VerticalOffset;
        ((IScrollProvider)peer.GetPattern(PatternInterface.Scroll)).Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
        await Until(() => scroll.VerticalOffset < offset);
        foreach (var size in new[] { new Size(860, 720), new Size(1260, 820) })
        { surface.Window.Resize(size.Width, size.Height); await Drain(); Centered(); }
        var position = surface.Window.AppWindow.Position;
        surface.Window.AppWindow.Move(new global::Windows.Graphics.PointInt32(position.X + 20, position.Y + 20)); await Drain(); Centered();
        var retained = modes.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
        DesktopTestUi.Invoke(close); await Drain(); Closed();
        await OpenSessionFm(footer);
        int requests = f.Platform.Requests.Count;
        DesktopTestUi.Invoke(retained); await Drain(); Check(surface.Dialog.IsOpen); Equal(requests, f.Platform.Requests.Count);
        Check(surface.Dialog.HandleEscape(VirtualKey.Escape), "the real Escape admission owner closes its channel"); await Drain(); Closed();
        await OpenSessionFm(footer);
        surface.Window.Hide(); await Drain(); Check(!surface.Dialog.IsOpen && modes.Children.Count == 0 && scenes.Children.Count == 0);
        surface.Window.Show(); surface.Window.Activate(); await Drain(); await OpenSessionFm(footer);
        var retiredInput = SessionInput(footer)!;
        surface.Unmount(); await Drain();
        Check(!footer.IsLoaded && !surface.Dialog.IsOpen && SessionInput(footer) is null, "unload removes the actual input owner");
        retiredInput.Press(true); Check(!retiredInput.Release(true), "a retired owner cannot admit a blank gesture");
        surface.Mount(); await SessionLoaded(footer); await OpenSessionFm(footer);
        Check(!ReferenceEquals(retiredInput, SessionInput(footer)), "real remount creates the next view input lifetime");
        surface.Window.Close(); await Drain();
        Check(!surface.Dialog.IsOpen && !footer.IsLoaded && SessionInput(footer) is null, "closing the native owner retires the channel and input");
    }
    private static async Task PlaybackFmAuthority(string retirement)
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "playback-fm-authority");
        await Drain(); await OpenSessionFm(footer);
        var choices = (StackPanel)footer.FmPanel.FindName("FmModeChoices");
        var retained = choices.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
        string key = f.Module.PlaybackFmActionKey;
        if (retirement == "catalog-action") f.Module.InvalidateCatalogAction();
        else { f.App.AccountGeneration++; await f.Module.ReadAccountsAsync(); await Drain(); }
        Check(key != f.Module.PlaybackFmActionKey, "new intent or account authority retires the old FM key");
        int requests = f.Platform.Requests.Count, starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        DesktopTestUi.Invoke(retained); await Drain();
        Equal(requests, f.Platform.Requests.Count); Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        if (PlayerDialogHost.Find(footer)!.IsOpen)
        {
            // InvalidateCatalogAction changes authority synchronously but does not publish a view notification.
            // Dismiss the still-mounted channel through its own control before requesting a lower mode menu.
            ((IInvokeProvider)new ButtonAutomationPeer((Button)footer.FmPanel.FindName("CloseFmButton"))
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await Drain();
        }
        await OpenSessionFm(footer);
        var fresh = choices.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
        ((IInvokeProvider)new ButtonAutomationPeer(fresh).GetPattern(PatternInterface.Invoke)).Invoke();
        await Until(() => f.Module.PlaybackFmMode == "explore" && !f.Module.RecommendationLoading);
        Equal(starts + 1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(requests + 1, f.Platform.Requests.Count);
        surface.Unmount();
    }
    private static async Task PlaybackFmPointerBarrier()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "fm-hit-tree"); await SessionLoaded(footer); await OpenSessionFm(footer);
        int requests = f.Platform.Requests.Count, opens = f.Output.Opens;
        foreach (string name in new[] { "PlayButton", "ModeButton", "QualityButton", "MoreButton", "QueueButton", "Progress" })
        {
            var target = (FrameworkElement)footer.FindName(name);
            var center = target.TransformToVisual(surface.Root).TransformPoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            Check(DesktopTestUi.Hit(surface.Root, center) is { } hit && DesktopVisual.Contains(surface.Dialog, hit),
                "the same-tree modal barrier owns the actual hit before " + name);
        }
        using (var other = new DesktopTestUi.Host(new Grid(), 100, 100))
        { await Until(() => !surface.Window.IsActive); Check(surface.Dialog.IsOpen, "deactivation cancels a press but does not dismiss the channel"); }
        surface.Window.Activate(); await Until(() => surface.Window.IsActive);
        Check(surface.Dialog.IsOpen && ReferenceEquals(footer.FmPanel.Background, f.Module.PlayerBackground),
            "channel consumes the actual player palette and survives activation changes");
        await GeneralAppearanceTests.SavePreview(surface.Root, "playback-channel-root", 1260, 760);
        Check(surface.Dialog.HandleEscape(VirtualKey.Escape)); await Drain();
        Equal(requests, f.Platform.Requests.Count); Equal(opens, f.Output.Opens);
        await OpenSessionFm(footer);
        var retained = ((StackPanel)footer.FmPanel.FindName("FmModeChoices")).Children.OfType<Button>().First();
        surface.Root.Visibility = Visibility.Collapsed; await Drain(); Check(!surface.Dialog.IsOpen);
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        DesktopTestUi.Invoke(retained); await Drain(); Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
    }
    private static async Task PlaybackFooterPointer()
    {
        await using var f = await Fixture.Create(output: new SessionSourceOutput()); await OrdinarySessionSong(f);
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "footer-native-actions"); await SessionLoaded(footer);
        var input = PlayerMenuInput.For(footer);
        var mode = (Button)footer.FindName("ModeButton"); var volume = (Button)footer.FindName("VolumeButton");
        var quality = (Button)footer.FindName("QualityButton"); var queue = (Button)footer.FindName("QueueButton"); var more = (Button)footer.FindName("MoreButton");
        var modeMenu = (MenuFlyout)footer.FindName("ModeMenu"); var qualityMenu = (MenuFlyout)footer.FindName("QualityMenu");
        var volumePopup = (Flyout)footer.FindName("VolumePopup"); var moreMenu = (MenuFlyout)footer.FindName("MoreMenu");
        int queues = 0, closes = 0;
        f.Module.GlobalSurfaceRequested += (_, args) =>
        {
            if (args.Surface != DesktopGlobalSurface.PlayQueue) return;
            queues++; PlayerSurfaceInput.SetQueueOpen(surface.Root, true);
            input.QueueOpened(() => { closes++; PlayerSurfaceInput.SetQueueOpen(surface.Root, false); input.QueueClosed(); });
        };
        void Invoke(Button target) => DesktopTestUi.Invoke(target);
        async Task Open(FlyoutBase flyout, Action action) { action(); await Until(() => flyout.IsOpen); await Drain(); }
        void ReloadQuality() => typeof(MusicCapability).GetField("qualityLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Module, false);
        await Open(modeMenu, () => Invoke(mode)); Invoke(mode); await Until(() => !modeMenu.IsOpen);
        await Open(volumePopup, () => Invoke(volume)); Invoke(volume); await Until(() => !volumePopup.IsOpen);
        await Open(modeMenu, () => Invoke(mode)); await Open(volumePopup, () => Invoke(volume)); Check(!modeMenu.IsOpen);
        Invoke(queue); await Drain(); Equal(1, queues); Check(!volumePopup.IsOpen && PlayerSurfaceInput.GetQueueOpen(footer));
        Invoke(queue); await Drain(); Equal(1, queues); Equal(1, closes);
        Invoke(queue); await Drain(); await Open(moreMenu, () => Invoke(more)); Equal(2, closes); input.CloseCurrent(); await Drain();
        ReloadQuality(); var held = f.App.HoldNext("music.quality.settings.get", before: true);
        try { Invoke(quality); await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Open(volumePopup, () => Invoke(volume)); }
        finally { held.Release(); }
        await Until(() => !f.Module.Busy); await Drain();
        Check(volumePopup.IsOpen && !qualityMenu.IsOpen, "a late quality result cannot revive a retired menu"); input.CloseCurrent(); await Drain();
        await Open(qualityMenu, () => Invoke(quality)); ReloadQuality();
        var reopening = f.App.HoldNext("music.quality.settings.get", before: true);
        try
        {
            qualityMenu.Hide(); Invoke(quality); await reopening.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!reopening.Cancelled.Task.IsCompleted, "the old native Closed cannot retire a replacement load");
            new MusicPlayerView(f.Module, showArtwork: false).Deactivate();
            Check(!reopening.Cancelled.Task.IsCompleted, "an inactive footer cannot cancel the active request");
            reopening.Release(); await Until(() => qualityMenu.IsOpen); Check(qualityMenu.Items.Count > 2 && input.IsCurrent(quality));
        }
        finally { reopening.Release(); }
        input.CloseCurrent(); await Drain(); ReloadQuality();
        var draining = f.App.HoldNext("music.quality.settings.get", before: true); Held? next = null;
        try
        {
            Invoke(quality); await draining.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); Invoke(quality);
            await draining.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            next = f.App.HoldNext("music.quality.settings.get", before: true); Invoke(quality); await Drain();
            Check(!next.Arrived.Task.IsCompleted && !qualityMenu.IsOpen, "a new request waits for real cancelled-work drainage");
            draining.Release(); await next.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!next.Cancelled.Task.IsCompleted);
            next.Release(); await Until(() => qualityMenu.IsOpen); Check(qualityMenu.Items.Count > 2 && input.IsCurrent(quality));
        }
        finally { draining.Release(); next?.Release(); }
        var source = (Flyout)footer.FindName("SourcePopup"); Check(f.Module.CanSwitchSource);
        MusicTestUi.Invoke(qualityMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "查看更多"));
        await Until(() => source.IsOpen && !qualityMenu.IsOpen); Check(source.Placement == FlyoutPlacementMode.Full);
        source.Hide(); Invoke(quality); await Until(() => qualityMenu.IsOpen); await Drain();
        Check(input.IsCurrent(quality), "source Closed does not retire a new quality menu"); input.CloseCurrent(); await Drain();
        foreach (bool switchEntry in new[] { false, true })
        {
            await Open(modeMenu, () => Invoke(mode)); modeMenu.Hide(); Invoke(mode);
            if (switchEntry) Invoke(volume); else Invoke(mode);
            await Drain(); await Until(() => !modeMenu.IsOpen && volumePopup.IsOpen == switchEntry);
            Check(!modeMenu.IsOpen, "same-entry cancel and a different entrance both fence native-close callbacks");
            input.CloseCurrent(); await Drain();
        }
    }

    private sealed class SessionSourceOutput : Output, INativePlaybackHandoff
    {
        public double PreparedHandoffDurationSeconds => 0;
        public Task StopOutputForHandoffAsync() => throw new InvalidOperationException("opening a source list must not stop playback");
        public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
            throw new InvalidOperationException("opening a source list must not prepare playback");
        public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) =>
            throw new InvalidOperationException("opening a source list must not prepare playback");
        public Task CommitHandoffAsync(Action committed, CancellationToken token) =>
            throw new InvalidOperationException("opening a source list must not replace playback");
        public Task DiscardHandoffAsync() => Task.CompletedTask;
    }

}
