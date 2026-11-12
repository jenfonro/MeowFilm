using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        yield return Case("channel root pointer barrier consumes transport seek and menu clicks and survives deactivation", PlaybackFmPointerBarrier);
        yield return Case("footer pointer routes toggle captured native menus switch queue and reject late quality", PlaybackFooterPointer);
        foreach (string retirement in new[] { "catalog-action", "account-generation" })
            yield return Case("FM choices reject a retired " + retirement + " and accept the fresh dialog",
                () => PlaybackFmAuthority(retirement));
    }

    private sealed class SessionHost : IDisposable
    {
        private readonly HwndSource source;
        private readonly Window window;
        private readonly Grid root;
        internal PlayerDialogHost Dialog { get; }
        internal FrameworkElement Root => root;
        internal HwndSource Source => source;
        internal Window Window => window;
        internal SessionHost(MusicPlayerView footer, string name)
        {
            root = new Grid(); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = new GridLength(80) });
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
            root.SetResourceReference(Panel.BackgroundProperty, "MeowFilm.Brush.AppBackground");
            root.Children.Add(footer); Grid.SetRow(footer, 1);
            Dialog = new PlayerDialogHost(); root.Children.Add(Dialog); Grid.SetRowSpan(Dialog, 2);
            window = new Window { Content = root };
            Dialog.Attach(window);
            source = new HwndSource(new HwndSourceParameters(name) {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1260, Height = 760 });
            source.RootVisual = root; Layout(root, 1260, 760);
        }
        internal System.Windows.Media.Visual? RootVisual
        {
            set { source.RootVisual = value is null ? null : root; if (value is not null) Layout(root, 1260, 760); }
        }
        public void Dispose() { Dialog.Close(); source.RootVisual = null; window.Content = null; source.Dispose(); window.Close(); }
    }
    private static SessionHost SessionSurface(MusicPlayerView footer, string name) => new(footer, name);
    private static async Task SessionLoaded(MusicPlayerView footer)
    {
        if (footer.IsLoaded) return;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        footer.Loaded += OnLoaded;
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { footer.Loaded -= OnLoaded; }
    }
    private static async Task<ContextMenu> OpenSessionMenu(MusicPlayerView footer)
    {
        var menu = (ContextMenu)footer.FindName("ModeMenu");
        menu.IsOpen = false;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, RoutedEventArgs args) => opened.TrySetResult();
        menu.Opened += OnOpened;
        try
        {
            ((Button)footer.FindName("ModeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { menu.Opened -= OnOpened; }
        await Drain();
        Check(menu.IsOpen, "playback mode opens the real native menu");
        Check(menu.Items.OfType<MenuItem>().All(item => item.Items.Count == 0), "all source actions are same-level items");
        return menu;
    }
    private static MenuItem SessionAction(ContextMenu menu, string label) =>
        menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetName(item) == label);
    private static void InvokeSessionAction(MenuItem item)
    {
        Check(item.IsEnabled && item.Items.Count == 0, "native menu action is enabled and flat");
        ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private static async Task OpenSessionFm(MusicPlayerView footer)
    {
        var menu = await OpenSessionMenu(footer);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, RoutedEventArgs args) => closed.TrySetResult();
        menu.Closed += OnClosed;
        try
        {
            InvokeSessionAction(SessionAction(menu, "切换频道"));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { menu.Closed -= OnClosed; }
        await Drain();
        Check(!menu.IsOpen && PlayerDialogHost.Find(footer)?.IsOpen == true,
            "the sibling channel action transitions from menu to the real FM popup");
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
        var actions = menu.Items.OfType<MenuItem>().ToArray();
        Check(actions.Select(AutomationProperties.GetName).SequenceEqual(expected), "source exposes one ordered playback-mode menu");
        Check(actions.All(item => item.IsEnabled), "the expected controls remain usable native actions");
        Equal(1, actions.Count(item => item.IsChecked));
        Equal(source == "ordinary" ? "列表循环" : source == "fm" ? "私人漫游" : source == "similar" ? "相似模式" : "心动模式",
            AutomationProperties.GetName(actions.Single(item => item.IsChecked)));
        menu.IsOpen = false;
        surface.RootVisual = null;
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
        Check(!menu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetName(item) is "退出推荐模式" or "切换频道"),
            "ordinary playback has no invented session actions");
        InvokeSessionAction(SessionAction(menu, "推荐模式"));
        await Until(() => f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation && !f.Module.RecommendationLoading);
        Equal("queue", S(f.App.Last("music.recommendations.start"), "kind"));
        Check(f.App.Last("music.recommendations.start").GetProperty("restore").GetBoolean(),
            "explicit entry requests a real original-queue checkpoint");
        var recommended = await f.Queue();
        menu = await OpenSessionMenu(footer);
        Equal("sparkles", icon.Key);
        Equal(1, menu.Items.OfType<MenuItem>().Count(item => AutomationProperties.GetName(item) == "推荐模式"));
        Check(!menu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetName(item) == "切换频道"), "queue recommendation has no FM channels");
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
        surface.RootVisual = null;
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
                    surface.RootVisual = null; await Drain();
                    Check(!footer.IsLoaded && !menu.IsOpen, "unload retires the native menu");
                    break;
            }
            int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
            int continues = f.App.Log.Count(call => call.Method == "music.recommendations.continue");
            int writes = f.Store.LibraryWrites, opens = f.Output.Opens;
            double position = f.Output.PositionSeconds;
            retained.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
            Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(continues, f.App.Log.Count(call => call.Method == "music.recommendations.continue"));
            Equal(writes, f.Store.LibraryWrites); Equal(opens, f.Output.Opens);
            Equal(position, f.Output.PositionSeconds);
        }
        finally { held?.Release(); await starting; }
        if (retirement == "unload")
        {
            surface.RootVisual = footer; Layout(footer, 1260, 100); await Drain();
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
        surface.RootVisual = null;
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
            Check(f.Module.RecommendationLoading && menu.Items.OfType<MenuItem>().All(item => AutomationProperties.GetName(item) != "重试取歌"), "loading does not introduce a mode-menu retry");
            menu.IsOpen = false;
        }
        finally { held.Fail("NETWORK_ERROR"); await starting; }
        await Until(() => !f.Module.RecommendationLoading && f.Module.CanRetryRecommendation);
        int requests = f.Platform.Requests.OfType<MusicFmRequest>().Count();
        int retries = f.App.Log.Count(call => call.Method == "music.recommendations.retry");
        var retryMenu = await OpenSessionMenu(footer);
        Check(!retryMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetName(item) == "重试取歌"));
        retryMenu.IsOpen = false;
        var queue = (MusicQueueView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue);
        var retry = (Button)queue.FindName("RetryRecommendationButton");
        Check(retry.IsEnabled && f.Module.HasRecommendationPanel, "real FM failures have a queue recovery action");
        retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => f.Module.HasPlaybackFm && !f.Module.RecommendationLoading);
        Equal(retries + 1, f.App.Log.Count(call => call.Method == "music.recommendations.retry"));
        Equal(requests + 1, f.Platform.Requests.OfType<MusicFmRequest>().Count());
        Equal("fm", ((MusicPlaybackIcon)((Button)footer.FindName("ModeButton")).Content).Key);
        var fmMenu = await OpenSessionMenu(footer);
        Check(SessionAction(fmMenu, "切换频道").IsEnabled &&
            !fmMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetName(item) == "退出推荐模式"),
            "retried FM retains sibling channels without inventing a checkpoint");
        fmMenu.IsOpen = false;
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
            !failedMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetName(item) == "推荐模式"),
            "a failed switch preserves explicit recovery without offering duplicate entry into the still-active FM source");
        failedMenu.IsOpen = false;
        surface.RootVisual = null;
    }
    private static IDictionary SessionInputSources(MusicPlayerView footer)
    {
        var input = (PlayerSurfaceInput)typeof(MusicPlayerView).GetField("surfaceInput",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(footer)!;
        return (IDictionary)typeof(PlayerSurfaceInput).GetField("sources",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!;
    }
    private static async Task PlaybackFmWindow()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        await f.Module.ChangePlaybackFmAsync("scene", "YINGSHI", f.Module.PlaybackFmActionKey);
        await f.Module.NavigateAsync("catalog-home", default);
        var footer = (MusicPlayerView)f.Module.PlayerView;
        var root = new Grid();
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
        root.SetResourceReference(Panel.BackgroundProperty, "MeowFilm.Brush.AppBackground");
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = new GridLength(80) });
        root.Children.Add(f.Module.View); root.Children.Add(footer); Grid.SetRow(footer, 1);
        var window = new Window
        {
            Content = root, Width = 860, Height = 720, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        var popup = new PlayerDialogHost(); root.Children.Add(popup); Grid.SetRowSpan(popup, 2); popup.Attach(window);
        window.PreviewKeyDown += (_, e) => popup.HandleEscape(e);
        var panel = (Border)footer.FindName("FmPanel");
        var scroll = (ScrollViewer)footer.FindName("FmScroll");
        var modes = (StackPanel)footer.FindName("FmModeChoices");
        var scenes = (UniformGrid)footer.FindName("FmSceneChoices");
        var button = (Button)footer.FindName("ModeButton");
        var close = (Button)footer.FindName("CloseFmButton");
        void Centered()
        {
            var center = panel.TranslatePoint(new(panel.ActualWidth / 2, panel.ActualHeight / 2), root);
            Check(Math.Abs(center.X - root.ActualWidth / 2) < .5 && Math.Abs(center.Y - root.ActualHeight / 2) < .5,
                "the channel panel stays centered in application content");
        }
        void Closed(IDictionary sources)
        {
            Check(!popup.IsOpen && modes.Children.Count == 0 && scenes.Children.Count == 0,
                "close releases generated choices");
            Check(FocusManager.GetFocusedElement(panel) is null, "close releases the popup focus scope");
            Check(!OverlayScrollChrome.GetActive(scroll), "close stops the shared scrolling presentation");
            Equal(1, sources.Count);
            Check(ReferenceEquals(button, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(button))),
                "normal close returns logical focus to playback mode without activating the offscreen window");
        }
        try
        {
            window.Show(); window.UpdateLayout(); await Drain();
            var sources = SessionInputSources(footer); Equal(1, sources.Count);
            await OpenSessionFm(footer);
            Check(ReferenceEquals(PresentationSource.FromVisual(panel), PresentationSource.FromVisual(root)) && popup.IsAncestorOf(panel),
                "FM shares the application content HWND and in-app hit-test tree");
            Equal(1, sources.Count); Centered();
            Equal(4, modes.Children.Count); Equal(43, scenes.Children.Count);
            var selected = scenes.Children.OfType<Button>().Single(choice => AutomationProperties.GetItemStatus(choice) == "已选中");
            Equal("YINGSHI", ((MusicCatalogChoice)selected.Tag).Key);
            Check(ReferenceEquals(selected, FocusManager.GetFocusedElement(panel)), "committed scene receives logical focus on open");
            Check(scroll.VerticalOffset > 0, "the selected lower scene is brought into the viewport");
            var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            var selectedRect = new Rect(selected.TranslatePoint(new Point(), viewport), selected.RenderSize);
            Check(new Rect(viewport.RenderSize).Contains(selectedRect), "the focused scene is actually visible");
            Check(!scroll.IsAncestorOf(close) && close.IsVisible, "closing stays reachable while scenes scroll");
            var catalog = (MusicCatalogView)f.Module.View.FindName("CatalogView");
            var home = (ScrollViewer)catalog.FindName("HomeScroll");
            static IEnumerable<ResourceDictionary> Dictionaries(ResourceDictionary root)
            {
                yield return root;
                foreach (var merged in root.MergedDictionaries)
                    foreach (var dictionary in Dictionaries(merged)) yield return dictionary;
            }
            static ResourceDictionary OverlayResources(FrameworkElement owner) => Dictionaries(owner.Resources)
                .Single(dictionary => dictionary.Source?.OriginalString ==
                    "/MeowFilm.Capabilities;component/Presentation/Controls/OverlayScrollChrome.xaml");
            foreach (var (consumer, owner) in new[] { (home, (FrameworkElement)catalog), (scroll, footer) })
            {
                consumer.ApplyTemplate();
                // Resolve from the declaring dictionary, not a different copy
                // of the desktop resources also merged by SettingsTheme.
                var resources = OverlayResources(owner);
                Check(ReferenceEquals(consumer.Style, resources["Capability.OverlayScrollViewer"]) &&
                    ReferenceEquals(consumer.Style.BasedOn, resources["MeowFilm.OverlayScrollViewer"]) &&
                    OverlayScrollChrome.GetEnabled(consumer), "home and FM resolve the same shared overlay implementation");
            }
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            Check(ReferenceEquals(bar.Style, OverlayResources(footer)["MeowFilm.OverlayScrollBar"]), "FM consumes the actual shared scrollbar");
            Check(ReferenceEquals(VisualTreeHelper.GetParent(bar), VisualTreeHelper.GetParent(viewport)) &&
                Grid.GetColumn(bar) == Grid.GetColumn(viewport), "scrollbar overlays rather than shrinking the content viewport");
            if (!DesktopTheme.GetIsHighContrast(scroll))
                Check(!OverlayScrollChrome.GetActive(scroll) && bar.Opacity == 0, "programmatic initial selection does not reveal idle chrome");
            bar.ApplyTemplate();
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            double offset = scroll.VerticalOffset;
            ((RoutedCommand)track.DecreaseRepeatButton.Command).Execute(null, track.DecreaseRepeatButton);
            await Drain();
            Check(scroll.VerticalOffset < offset && OverlayScrollChrome.GetActive(scroll) && bar.Opacity > 0,
                "real FM page scrolling reveals the shared overlay chrome");
            window.Width = 1260; window.Height = 820; window.UpdateLayout(); await Drain();
            Centered();
            window.Left -= 20; await Drain();
            Centered();
            DesktopTheme.SetIsHighContrast(panel, true); await Drain(); Equal(1d, bar.Opacity);
            var retained = modes.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
            ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
            await Drain(); Closed(sources);
            DesktopTheme.SetIsHighContrast(panel, false);
            await OpenSessionFm(footer);
            int requests = f.Platform.Requests.Count;
            retained.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
            Check(popup.IsOpen, "old choices cannot close a newer popup even with unchanged committed settings");
            Equal(requests, f.Platform.Requests.Count);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            panel.RaiseEvent(escape); await Drain();
            Check(escape.Handled, "Escape is handled by the actual popup route"); Closed(sources);
            await OpenSessionFm(footer);
            window.Hide(); await Drain();
            Check(!popup.IsOpen && modes.Children.Count == 0 && scenes.Children.Count == 0, "hiding the app ends its FM layer");
            window.Show(); window.UpdateLayout(); await Drain();
            await OpenSessionFm(footer);
            sources = SessionInputSources(footer);
            root.Children.Remove(footer); await Drain();
            Check(!footer.IsLoaded && !popup.IsOpen); Equal(0, sources.Count);
            root.Children.Add(footer); Grid.SetRow(footer, 1); window.UpdateLayout(); await Drain();
            await OpenSessionFm(footer);
            sources = SessionInputSources(footer);
            window.Close(); await Drain();
            Check(!popup.IsOpen && !footer.IsLoaded); Equal(0, sources.Count);
        }
        finally
        {
            footer.Deactivate(); window.Content = null; window.Close();
        }
    }
    private static async Task PlaybackFmAuthority(string retirement)
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "playback-fm-authority");
        await Drain(); await OpenSessionFm(footer);
        var choices = (StackPanel)footer.FindName("FmModeChoices");
        var retained = choices.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
        string key = f.Module.PlaybackFmActionKey;
        if (retirement == "catalog-action") f.Module.InvalidateCatalogAction();
        else { f.App.AccountGeneration++; await f.Module.ReadAccountsAsync(); await Drain(); }
        Check(key != f.Module.PlaybackFmActionKey, "new intent or account authority retires the old FM key");
        int requests = f.Platform.Requests.Count, starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        retained.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Equal(requests, f.Platform.Requests.Count); Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        if (PlayerDialogHost.Find(footer)!.IsOpen)
        {
            // InvalidateCatalogAction changes authority synchronously but does not publish a view notification.
            // Dismiss the still-mounted channel through its own control before requesting a lower mode menu.
            ((IInvokeProvider)new ButtonAutomationPeer((Button)footer.FindName("CloseFmButton"))
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await Drain();
        }
        await OpenSessionFm(footer);
        var fresh = choices.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
        ((IInvokeProvider)new ButtonAutomationPeer(fresh).GetPattern(PatternInterface.Invoke)).Invoke();
        await Until(() => f.Module.PlaybackFmMode == "explore" && !f.Module.RecommendationLoading);
        Equal(starts + 1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(requests + 1, f.Platform.Requests.Count);
        surface.RootVisual = null;
    }
    private static async Task PlaybackFmPointerBarrier()
    {
        await using var f = await Fixture.Create();
        await f.Module.NavigateAsync("catalog-fm", default); await f.Module.PlayCatalogPlaylistAsync();
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "channel-pointer-barrier");
        await SessionLoaded(footer);
        using var pointer = new PlaybackPointer(surface.Root, surface.Source);
        var mode = (Button)footer.FindName("ModeButton");
        var next = (Button)footer.FindName("NextButton");
        var seek = (SeekBar)footer.FindName("Progress");
        int down = 0, up = 0;
        next.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => down++), true);
        next.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => up++), true);
        foreach (var target in new FrameworkElement[] { next, seek, mode, (Button)footer.FindName("VolumeButton"), (Button)footer.FindName("QueueButton") })
        {
            await OpenSessionFm(footer);
            var point = pointer.Center(target);
            var hit = pointer.Hit(point) as DependencyObject;
            Check(hit is not null && (ReferenceEquals(hit, surface.Dialog) || surface.Dialog.IsAncestorOf(hit)), "root hit-test stops on the channel barrier");
            int writes = f.Store.LibraryWrites, opens = f.Output.Opens;
            double position = f.Output.PositionSeconds;
            Check(pointer.Button(point, true) && surface.Dialog.IsOpen, "outside Down is consumed while the barrier remains mounted");
            Check(pointer.Button(point, false), "matching outside Up is consumed");
            await Drain();
            Check(!surface.Dialog.IsOpen && !seek.IsScrubbing, "only the channel closes; seek cannot begin");
            Check(!((Popup)footer.FindName("VolumePopup")).IsOpen && !((ContextMenu)footer.FindName("ModeMenu")).IsOpen,
                "a lower menu entry does not switch menus while the channel is open");
            Equal(writes, f.Store.LibraryWrites); Equal(opens, f.Output.Opens);
            Equal(position, f.Output.PositionSeconds);
        }
        Equal(0, down); Equal(0, up);
        await OpenSessionFm(footer);
        typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(surface.Window, [EventArgs.Empty]);
        Check(surface.Dialog.IsOpen, "deactivation does not dismiss a channel or steal focus back");
        var panel = (Border)footer.FindName("FmPanel");
        Check(ReferenceEquals(panel.Background, f.Module.PlayerBackground), "channel consumes the actual full-player palette brush");
        Check(ReferenceEquals(PresentationSource.FromVisual(panel), surface.Source) && surface.Dialog.IsAncestorOf(panel),
            "the preview renders the channel inside its actual root HWND");
        Equal(((FontFamily)surface.Root.FindResource("MeowFilm.Font.Icons")).Source,
            ((Button)footer.FindName("CloseFmButton")).FontFamily.Source);
        GeneralAppearanceTests.SavePreview(surface.Root, "playback-channel-root", 1260, 760);
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, surface.Source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        surface.Dialog.HandleEscape(escape); await Drain();
        Check(escape.Handled && !surface.Dialog.IsOpen, "the standalone owner explicitly routes Escape to its channel layer");
        await OpenSessionFm(footer);
        var old = ((StackPanel)footer.FindName("FmModeChoices")).Children.OfType<Button>().First();
        surface.Root.Visibility = Visibility.Hidden; await Drain();
        Check(!surface.Dialog.IsOpen, "hiding the owner removes the channel");
        int starts = f.App.Log.Count(call => call.Method == "music.recommendations.start");
        old.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Drain();
        Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
    }
    private static async Task PlaybackFooterPointer()
    {
        await using var f = await Fixture.Create(output: new SessionSourceOutput()); await OrdinarySessionSong(f);
        var footer = (MusicPlayerView)f.Module.PlayerView;
        using var surface = SessionSurface(footer, "footer-pointer-sequence");
        await SessionLoaded(footer);
        using var pointer = new PlaybackPointer(surface.Root, surface.Source);
        var input = PlayerMenuInput.For(surface.Window);
        var mode = (Button)footer.FindName("ModeButton");
        var volume = (Button)footer.FindName("VolumeButton");
        var quality = (Button)footer.FindName("QualityButton");
        var queue = (Button)footer.FindName("QueueButton");
        var more = (Button)footer.FindName("MoreButton");
        var modeMenu = (ContextMenu)footer.FindName("ModeMenu");
        var qualityMenu = (ContextMenu)footer.FindName("QualityMenu");
        var volumePopup = (Popup)footer.FindName("VolumePopup");
        int queues = 0, queueCloses = 0;
        f.Module.GlobalSurfaceRequested += (_, args) =>
        {
            if (args.Surface != DesktopGlobalSurface.PlayQueue) return;
            queues++;
            PlayerSurfaceInput.SetQueueOpen(surface.Window, true);
            input.QueueOpened(() => { queueCloses++; PlayerSurfaceInput.SetQueueOpen(surface.Window, false); input.QueueClosed(); });
        };
        void Click(FrameworkElement target) => pointer.Click(pointer.Center(target));
        async Task MenuOpens(ContextMenu menu, Action trigger)
        {
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnOpened(object? sender, RoutedEventArgs args) => opened.TrySetResult();
            menu.Opened += OnOpened;
            try
            {
                trigger();
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { menu.Opened -= OnOpened; }
            await Drain();
        }
        Task ModeOpens(Action trigger) => MenuOpens(modeMenu, trigger);
        void ReloadQuality() =>
            typeof(MusicCapability).GetField("qualityLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Module, false);
        var point = pointer.Center(mode);
        await ModeOpens(() =>
        {
            Check(pointer.Button(point, true), "the mode entrance consumes Down");
            Check(pointer.Button(point, false), "the same gesture consumes Up before native capture starts");
        });
        Check(modeMenu.IsOpen, "root-routed Down/Up opens the native context menu");
        // Model the real ordering: captured HWND Down records intent, WPF closes the menu, then preview routing reaches the footer.
        var modePoint = pointer.Center(mode);
        input.NativeDown(surface.Root.PointToScreen(modePoint));
        modeMenu.IsOpen = false; await Drain();
        pointer.Button(modePoint, true, sourceReady: false);
        pointer.Button(modePoint, false);
        await Drain(); Check(!modeMenu.IsOpen, "same entry does not reopen after native pre-close before WPF establishes Source");
        Click(volume); await Drain(); Check(volumePopup.IsOpen, "native volume popup opens");
        var volumePoint = pointer.Center(volume);
        input.NativeDown(surface.Root.PointToScreen(volumePoint));
        volumePopup.IsOpen = false; await Drain();
        pointer.Click(volumePoint); await Drain(); Check(!volumePopup.IsOpen, "same volume entry closes across native pre-close");
        await ModeOpens(() => Click(mode));
        input.NativeDown(surface.Root.PointToScreen(volumePoint)); modeMenu.IsOpen = false; await Drain();
        Click(volume); await Drain(); Check(volumePopup.IsOpen && !modeMenu.IsOpen, "different footer entry switches on one gesture");
        Click(queue); await Drain(); Equal(1, queues); Check(!volumePopup.IsOpen && PlayerSurfaceInput.GetQueueOpen(footer));
        Click(queue); await Drain(); Equal(1, queues); Equal(1, queueCloses);
        Click(queue); await Drain(); Click(more); await Drain(); Equal(2, queueCloses);
        Check(more.ContextMenu?.IsOpen == true, "queue switches directly to another footer menu");
        input.CloseCurrent();
        // Force a fresh quality load without changing the already accepted playback identity.
        ReloadQuality();
        var held = f.App.HoldNext("music.quality.settings.get", before: true);
        try
        {
            Click(quality);
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Click(volume); await Drain(); Check(volumePopup.IsOpen);
        }
        finally { held.Release(); }
        await Until(() => !f.Module.Busy); await Drain();
        Check(volumePopup.IsOpen && !qualityMenu.IsOpen,
            "a late quality result cannot reopen the retired menu over the newer one");
        input.CloseCurrent();
        await MenuOpens(qualityMenu, () => Click(quality));
        ReloadQuality();
        var reopeningQuality = f.App.HoldNext("music.quality.settings.get", before: true);
        try
        {
            var reopened = MenuOpens(qualityMenu, () =>
            {
                qualityMenu.IsOpen = false;
                quality.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            });
            await reopeningQuality.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!reopeningQuality.Cancelled.Task.IsCompleted,
                "the old native Quality Closed cannot cancel the new load");
            new MusicPlayerView(f.Module, showArtwork: false).Deactivate();
            Check(!reopeningQuality.Cancelled.Task.IsCompleted,
                "deactivating an inactive footer cannot cancel the active footer's settings load");
            reopeningQuality.Release();
            await reopened;
            Check(qualityMenu.Items.Count > 2 && input.IsCurrent(quality),
                "quality reopens with usable choices after the old native menu has retired");
        }
        finally { reopeningQuality.Release(); }
        // A cancelled settings operation may still be draining. Reopen must wait, not skip loading while busy.
        qualityMenu.IsOpen = false;
        ReloadQuality();
        var drainingQuality = f.App.HoldNext("music.quality.settings.get", before: true);
        Held? nextQuality = null;
        try
        {
            quality.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await drainingQuality.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Click(quality);
            await drainingQuality.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            nextQuality = f.App.HoldNext("music.quality.settings.get", before: true);
            var reopened = MenuOpens(qualityMenu, () => Click(quality));
            await Drain();
            Check(!nextQuality.Arrived.Task.IsCompleted && !qualityMenu.IsOpen,
                "a replacement quality request waits for cancelled work to finish");
            drainingQuality.Release();
            await nextQuality.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!nextQuality.Cancelled.Task.IsCompleted, "only the retired load is cancelled");
            nextQuality.Release();
            await reopened;
            Check(qualityMenu.Items.Count > 2 && input.IsCurrent(quality));
        }
        finally { drainingQuality.Release(); nextQuality?.Release(); }
        var sourcePopup = (Popup)footer.FindName("SourcePopup");
        Check(f.Module.CanSwitchSource, "the playback fixture supports the source-list entrance");
        var sourceOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSourceOpened(object? sender, EventArgs args) => sourceOpened.TrySetResult();
        sourcePopup.Opened += OnSourceOpened;
        try
        {
            ((MenuItem)footer.FindName("SourceListItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await sourceOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(sourcePopup.IsOpen, "quality hands its entrance to the source panel");
        }
        finally { sourcePopup.Opened -= OnSourceOpened; }
        var sourceClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSourceClosed(object? sender, EventArgs args) => sourceClosed.TrySetResult();
        sourcePopup.Closed += OnSourceClosed;
        try
        {
            await MenuOpens(qualityMenu, () =>
            {
                sourcePopup.IsOpen = false;
                quality.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            });
            await sourceClosed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            Check(qualityMenu.IsOpen && input.IsCurrent(quality),
                "the source panel's late native Closed cannot retire the replacement quality menu");
        }
        finally { sourcePopup.Closed -= OnSourceClosed; input.CloseCurrent(); }
        foreach (bool switchEntry in new[] { false, true })
        {
            await ModeOpens(() => Click(mode));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClosed(object? sender, RoutedEventArgs args) => closed.TrySetResult();
            modeMenu.Closed += OnClosed;
            try
            {
                modeMenu.IsOpen = false;
                mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (switchEntry) Click(volume);
                else mode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                Check(!modeMenu.IsOpen && volumePopup.IsOpen == switchEntry,
                    switchEntry ? "switching entries retires the old native reopen callback" :
                    "the same entry cancels a reopen requested during native teardown");
            }
            finally { modeMenu.Closed -= OnClosed; input.CloseCurrent(); }
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
