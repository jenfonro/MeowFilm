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

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    internal static IEnumerable<TestCase> PlaybackSessionSourceCases()
    {
        foreach (string source in new[] { "ordinary", "fm", "similar", "heartbeat" })
            yield return Case("infinity offers recommendation only before entering a special source: " + source,
                () => PlaybackSessionSourceMenu(source));
    }

    internal static IEnumerable<TestCase> PlaybackSessionControlCases()
    {
        yield return Case("infinity enters recommendation and separates continuation from checkpoint return", PlaybackSessionActions);
        foreach (string retirement in new[] { "reopen", "catalog-action", "recommendation-command",
            "recommendation-snapshot", "account-generation", "unload" })
            yield return Case("infinity rejects a retained native menu action after " + retirement,
                () => PlaybackSessionRetirement(retirement));
        yield return Case("infinity retains loading and one explicit retry after a real FM failure", PlaybackSessionRetry);
        yield return Case("FM window placement resources focus and lifetime use the actual WPF popup", PlaybackFmWindow);
        foreach (string retirement in new[] { "catalog-action", "account-generation" })
            yield return Case("FM choices reject a retired " + retirement + " and accept the fresh popup",
                () => PlaybackFmAuthority(retirement));
    }

    private static HwndSource SessionSurface(MusicPlayerView footer, string name)
    {
        var source = new HwndSource(new HwndSourceParameters(name)
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 1260, Height = 100
        });
        source.RootVisual = footer; Layout(footer, 1260, 100);
        return source;
    }
    private static async Task<ContextMenu> OpenSessionMenu(MusicPlayerView footer)
    {
        var menu = (ContextMenu)footer.FindName("PlaybackSessionMenu");
        menu.IsOpen = false; await Drain();
        ((Button)footer.FindName("PlaybackSessionButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Drain();
        Check(menu.IsOpen, "infinity opens the real native menu");
        Check(menu.Items.OfType<MenuItem>().All(item => item.Items.Count == 0), "all source actions are same-level items");
        return menu;
    }
    private static MenuItem SessionAction(ContextMenu menu, string label) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label));
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
            InvokeSessionAction(SessionAction(menu, "频道切换"));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { menu.Closed -= OnClosed; }
        await Drain();
        Check(!menu.IsOpen && ((Popup)footer.FindName("FmPopup")).IsOpen,
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
            "ordinary" => ["推荐模式"],
            "fm" => ["关闭续播", "频道切换", "重试取歌"],
            "similar" => ["关闭续播", "重试取歌"],
            "heartbeat" => ["退出心动"],
            _ => throw new InvalidOperationException(source)
        };
        var actions = menu.Items.OfType<MenuItem>().Skip(1).ToArray();
        Check(actions.Select(item => item.Header).SequenceEqual(expected.Cast<object>()),
            "ordinary playback offers entry; active sources keep only their applicable controls and recovery");
        Check(actions.All(item => item.IsEnabled), "the expected source controls remain usable native actions");
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
        var button = (Button)footer.FindName("PlaybackSessionButton");
        var icon = (MusicPlaybackIcon)button.Content;
        var menu = await OpenSessionMenu(footer);
        Equal("fm-off", icon.Key);
        Check(!menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "退出推荐") ||
            Equals(item.Header, "开启续播") || Equals(item.Header, "关闭续播") || Equals(item.Header, "频道切换")),
            "ordinary playback has no invented session actions");
        InvokeSessionAction(SessionAction(menu, "推荐模式"));
        await Until(() => f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation && !f.Module.RecommendationLoading);
        Equal("queue", S(f.App.Last("music.recommendations.start"), "kind"));
        Check(f.App.Last("music.recommendations.start").GetProperty("restore").GetBoolean(),
            "explicit entry requests a real original-queue checkpoint");
        var recommended = await f.Queue();
        menu = await OpenSessionMenu(footer);
        Equal("fm", icon.Key);
        Check(!menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "推荐模式") || Equals(item.Header, "频道切换")),
            "queue recommendation has neither duplicate entry nor FM channels");
        Check(SessionAction(menu, "退出推荐").IsEnabled);
        var disable = SessionAction(menu, "关闭续播");
        Check(!disable.IsCheckable && !disable.IsChecked, "continuation is an explicit action, not a checked source state");
        InvokeSessionAction(disable);
        await Until(() => !f.Module.RecommendationContinue && !f.Module.RecommendationLoading);
        Check(f.Module.HasQueueRecommendations && f.Module.CanReturnRecommendation,
            "disabling continuation leaves the active session and checkpoint intact");
        Equal("fm", icon.Key);
        Equal(recommended.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
        Equal(S(recommended, "currentId"), S(await f.Queue(), "currentId"));
        Equal(opens, f.Output.Opens); Equal(resumes, f.Output.Resumes); Equal(1, f.Module.Mode);
        menu = await OpenSessionMenu(footer);
        Check(SessionAction(menu, "开启续播").IsEnabled);
        InvokeSessionAction(SessionAction(menu, "退出推荐"));
        await Until(() => !f.Module.HasActiveRecommendation && !f.Module.CanReturnRecommendation);
        Equal("fm-off", icon.Key);
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
            retained.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
            Equal(starts, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
            Equal(continues, f.App.Log.Count(call => call.Method == "music.recommendations.continue"));
            Equal(writes, f.Store.LibraryWrites); Equal(opens, f.Output.Opens);
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
            int continues = f.App.Log.Count(call => call.Method == "music.recommendations.continue");
            InvokeSessionAction(SessionAction(menu, "关闭续播"));
            await Until(() => !f.Module.RecommendationContinue);
            Equal(continues + 1, f.App.Log.Count(call => call.Method == "music.recommendations.continue"));
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
            Check(!SessionAction(menu, "正在加载").IsEnabled, "loading remains a real disabled status item in the infinity menu");
            menu.IsOpen = false;
        }
        finally { held.Fail("NETWORK_ERROR"); await starting; }
        await Until(() => !f.Module.RecommendationLoading && f.Module.CanRetryRecommendation);
        int requests = f.Platform.Requests.OfType<MusicFmRequest>().Count();
        int retries = f.App.Log.Count(call => call.Method == "music.recommendations.retry");
        var retryMenu = await OpenSessionMenu(footer);
        InvokeSessionAction(SessionAction(retryMenu, "重试取歌"));
        await Until(() => f.Module.HasPlaybackFm && !f.Module.RecommendationLoading);
        Equal(retries + 1, f.App.Log.Count(call => call.Method == "music.recommendations.retry"));
        Equal(requests + 1, f.Platform.Requests.OfType<MusicFmRequest>().Count());
        Equal("fm", ((MusicPlaybackIcon)((Button)footer.FindName("PlaybackSessionButton")).Content).Key);
        var fmMenu = await OpenSessionMenu(footer);
        Check(SessionAction(fmMenu, "频道切换").IsEnabled &&
            !fmMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "退出推荐")),
            "retried FM retains sibling channels without inventing a checkpoint");
        fmMenu.IsOpen = false;
        var changingHold = f.Platform.HoldNextFm();
        var changing = f.Module.ChangePlaybackFmAsync("familiar", "", f.Module.PlaybackFmActionKey);
        await changingHold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Until(() => f.Module.RecommendationLoading);
            var loading = await OpenSessionMenu(footer);
            Check(!SessionAction(loading, "正在加载").IsEnabled &&
                SessionAction(loading, "频道切换").IsEnabled,
                "a pending FM switch preserves the live source and its existing channel-change capability");
            InvokeSessionAction(SessionAction(loading, "频道切换")); await Drain();
            Check(((Popup)footer.FindName("FmPopup")).IsOpen, "loading does not add an unrequested FM-entry restriction");
        }
        finally { changingHold.Fail("NETWORK_ERROR"); await changing; }
        await Until(() => !f.Module.RecommendationLoading);
        Equal("default", f.Module.PlaybackFmMode);
        Check(!((Popup)footer.FindName("FmPopup")).IsOpen, "the changed receipt retires the pending popup");
        var failedMenu = await OpenSessionMenu(footer);
        Check(f.Module.HasPlaybackFm && SessionAction(failedMenu, "重试取歌").IsEnabled &&
            !failedMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "推荐模式")),
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
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = new GridLength(80) });
        root.Children.Add(f.Module.View); root.Children.Add(footer); Grid.SetRow(footer, 1);
        var window = new Window
        {
            Content = root, Width = 860, Height = 720, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        var popup = (Popup)footer.FindName("FmPopup");
        var panel = (Border)footer.FindName("FmPanel");
        var scroll = (ScrollViewer)footer.FindName("FmScroll");
        var modes = (StackPanel)footer.FindName("FmModeChoices");
        var scenes = (UniformGrid)footer.FindName("FmSceneChoices");
        var button = (Button)footer.FindName("PlaybackSessionButton");
        var close = (Button)footer.FindName("CloseFmButton");
        var callback = popup.CustomPopupPlacementCallback!;
        var placements = new List<(Size Size, Point Origin)>();
        // Observe WPF's actual placement calls. Offscreen native monitor clipping
        // is not a substitute for testing application-window coordinates.
        popup.CustomPopupPlacementCallback = (size, target, offset) =>
        {
            var result = callback(size, target, offset);
            placements.Add((size, result.Single().Point));
            return result;
        };
        void Centered()
        {
            var actual = placements.Last();
            var center = button.TranslatePoint(new(actual.Origin.X + actual.Size.Width / 2,
                actual.Origin.Y + actual.Size.Height / 2), root);
            Check(Math.Abs(center.X - root.ActualWidth / 2) < .5 && Math.Abs(center.Y - root.ActualHeight / 2) < .5,
                "the actual popup placement is centered in application content, not above the footer anchor");
        }
        void Closed(IDictionary sources)
        {
            Check(!popup.IsOpen && modes.Children.Count == 0 && scenes.Children.Count == 0,
                "close releases generated choices");
            Check(FocusManager.GetFocusedElement(panel) is null, "close releases the popup focus scope");
            Check(!OverlayScrollChrome.GetActive(scroll), "close stops the shared scrolling presentation");
            Equal(1, sources.Count);
            Check(ReferenceEquals(button, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(button))),
                "normal close returns logical focus to infinity without activating the offscreen window");
        }
        try
        {
            window.Show(); window.UpdateLayout(); await Drain();
            var sources = SessionInputSources(footer); Equal(1, sources.Count);
            await OpenSessionFm(footer);
            Check(PresentationSource.FromVisual(panel) is HwndSource && !popup.StaysOpen,
                "FM uses a real dismissible native popup");
            Equal(2, sources.Count); Check(placements.Count > 0); Centered();
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
            int before = placements.Count;
            window.Width = 1260; window.Height = 820; window.UpdateLayout(); await Drain();
            Check(placements.Count > before, "window resize repositions the open native popup"); Centered();
            before = placements.Count;
            window.Left -= 20; await Drain();
            Check(placements.Count > before, "window movement repositions the open native popup"); Centered();
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
            popup.CustomPopupPlacementCallback = callback;
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
        await OpenSessionFm(footer);
        var fresh = choices.Children.OfType<Button>().Single(choice => ((MusicCatalogChoice)choice.Tag).Key == "explore");
        ((IInvokeProvider)new ButtonAutomationPeer(fresh).GetPattern(PatternInterface.Invoke)).Invoke();
        await Until(() => f.Module.PlaybackFmMode == "explore" && !f.Module.RecommendationLoading);
        Equal(starts + 1, f.App.Log.Count(call => call.Method == "music.recommendations.start"));
        Equal(requests + 1, f.Platform.Requests.Count);
        surface.RootVisual = null;
    }
}
