using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Desktop;
using IconPath = System.Windows.Shapes.Path;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> MediaNavigationCases()
    {
        yield return Case("media selector owns home and scopes both lower navigation groups across content search and settings", async () =>
        {
            var music = new ProbeModule("music");
            music.ReplaceNavigation(new ObservableCollection<NavigationItem>(
            [
                new("one", "音乐首页", ""),
                new("two", "音乐频道", ""),
                new("favorites", "收藏", "", "我的"),
                new("private", "内部页面", "", ShowInSidebar: false)
            ]));
            var film = new ProbeModule("film", hasPlayer: false);
            var novel = new ProbeModule("novel", hasPlayer: false);
            novel.ReplaceNavigation(new ObservableCollection<NavigationItem>(
            [
                new("one", "小说首页", "", ShowInSidebar: false),
                new("two", "分类", "")
            ]));
            var page = new ProbePage("public");
            await using var f = new Fixture([music, film, novel], pages: [page]); await f.LoadAsync();
            Sequence(["music", "film", "novel"], f.Modules.Items.OfType<ModuleSession>().Select(item => item.Module.Id),
                "media order follows the actual supplied owners");
            Same(music, ((ModuleSession)f.Modules.SelectedItem).Module, "media item selects the actual home owner");
            AssertSidebarOwner(f, music);
            Sequence(["two"], f.Navigation.Items.OfType<ShellNavigationEntry>().Select(item => item.Route),
                "home and non-sidebar routes stay outside lower content navigation");
            Sequence(["favorites"], f.Element<ListBox>("CollectionNavigationList").Items.OfType<ShellNavigationEntry>().Select(item => item.Route),
                "the current owner's collection group remains reachable");

            await f.NavigateAsync(music, "favorites");
            True(f.Modules.SelectedItem is null, "collection content does not highlight its media home");
            await f.SearchAsync("query");
            True(f.Modules.SelectedItem is null && f.SelectedRoute is null, "search cannot appear as a selected media home or lower route");
            AssertSidebarOwner(f, music);
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            True(f.Modules.SelectedItem is null && f.SelectedRoute is null, "settings clear both content selectors");
            AssertSidebarOwner(f, music);
            Click(f.Back); await DrainAsync();
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            await UntilAsync(() => page.Activations == 1 && f.Status == "", "public page");
            True(f.Modules.SelectedItem is null, "public page cannot highlight the owner's home");
            AssertSidebarOwner(f, music);
            Click(f.Back); await DrainAsync();

            await f.NavigateAsync(film, "two");
            Sequence(["route:one", "route:two"], film.Calls,
                "changing owner first visits media home then the selected lower destination");
            True(f.Modules.SelectedItem is null, "film subpage does not select the film home");
            AssertSidebarOwner(f, film);
            Equal(0, novel.InitializeCount + novel.Calls.Count, "unselected media remains dormant");
            await f.SwitchAsync(music);
            Same(music, ((ModuleSession)f.Modules.SelectedItem!).Module, "media reentry returns to its first declared route");
            Equal("route:one", music.Calls[^1], "media reentry is home navigation rather than restoring a subpage");
            AssertSidebarOwner(f, music);
            await f.SwitchAsync(novel);
            Same(novel, ((ModuleSession)f.Modules.SelectedItem).Module, "media home remains selectable when hidden from lower navigation");
            Sequence(["route:one"], novel.Calls, "media selection invokes the first declared home exactly once");
            AssertSidebarOwner(f, novel);
        });
        yield return Case("media and content templates render outline icons and retain glyph fallback", async () =>
        {
            const string homeIcon = "M3,18 L3,6 15,3 15,15";
            const string contentIcon = "M3,3 L18,3 18,18 3,18 Z";
            var music = new ProbeModule("music");
            music.ReplaceNavigation(new ObservableCollection<NavigationItem>(
            [
                new("one", "音乐", "\uE8D6") { IconData = homeIcon },
                new("two", "频道", "\uE8F1") { IconData = contentIcon }
            ]));
            var film = new ProbeModule("film", hasPlayer: false);
            film.ReplaceNavigation(new ObservableCollection<NavigationItem>(
            [
                new("one", "影视", "\uE714"),
                new("two", "收藏", "\uE734")
            ]));
            await using var f = new Fixture([music, film]); await f.LoadAsync();
            var root = DetachChrome(f.Shell);
            using var source = HeaderSource(root);
            root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout(); await DrainAsync();
            var mediaRow = (ListBoxItem)f.Modules.ItemContainerGenerator.ContainerFromIndex(0);
            var contentRow = (ListBoxItem)f.Navigation.ItemContainerGenerator.ContainerFromIndex(0);
            AssertOutline(mediaRow, homeIcon);
            AssertOutline(contentRow, contentIcon);
            Equal(music.Title, AutomationProperties.GetName(mediaRow), "media row retains its accessible owner title");
            var fallbackRow = (ListBoxItem)f.Modules.ItemContainerGenerator.ContainerFromIndex(1);
            Equal(Visibility.Collapsed, ChromeVisuals(fallbackRow).OfType<IconPath>().Single(item => item.Name == "Outline").Visibility,
                "a glyph-only owner does not render an empty outline");
            var glyph = ChromeVisuals(fallbackRow).OfType<TextBlock>().Single(item => item.Name == "Glyph");
            Equal(Visibility.Visible, glyph.Visibility, "glyph fallback remains visible");
            Equal("\uE714", glyph.Text, "glyph fallback uses the owner's declared icon");
            source.RootVisual = null;

            static void AssertOutline(ListBoxItem row, string expected)
            {
                var outline = ChromeVisuals(row).OfType<IconPath>().Single(item => item.Name == "Outline");
                Equal(Geometry.Parse(expected).ToString(CultureInfo.InvariantCulture),
                    outline.Data.ToString(CultureInfo.InvariantCulture), "the template renders the declared vector geometry");
                Equal(Visibility.Visible, outline.Visibility, "the vector is visible");
                Same(row.Foreground, outline.Stroke, "vector color follows the real row selection and contrast foreground");
                Equal(Visibility.Collapsed, ChromeVisuals(row).OfType<TextBlock>().Single(item => item.Name == "Glyph").Visibility,
                    "outline and legacy glyph do not render twice");
            }
        });
        yield return Case("forward restores exact opaque search and media locations without replay or reconstruction", async () =>
        {
            var music = new ProbeModule("music");
            var film = new ProbeModule("film", hasPlayer: false);
            await using var f = new Fixture([music, film]); await f.LoadAsync();
            False(f.Forward.IsEnabled, "initial home has no forward destination");
            f.Search.Text = "home draft";
            const string opaque = "opaque/source-one|query?kind=music";
            music.NextSearchLocation = opaque;
            await f.SearchAsync("original query");
            f.Search.Text = "unfinished search draft";
            var main = Host(music.View); var player = Host(music.PlayerView!);
            await f.SwitchAsync(film); f.Search.Text = "film draft";
            Click(f.Back); await UntilAsync(() => music.CurrentLocation == opaque && f.Status == "", "back to opaque search");
            Equal("original query", f.Search.Text, "restoring search retains its original query semantics");
            True(f.Forward.IsEnabled && f.Modules.SelectedItem is null, "restored search exposes forward without selecting home");
            Click(f.Back); await UntilAsync(() => f.SelectedOwner == music && f.SelectedRoute == "one" && f.Status == "", "back to music home");
            Equal("original query", f.Search.Text, "the query typed on home remains isolated from the later unfinished search draft");
            var captures = music.CaptureCount + film.CaptureCount;

            Click(f.Forward); await UntilAsync(() => music.CurrentLocation == opaque && f.Status == "", "forward to opaque search");
            Equal("original query", f.Search.Text, "forward restores the input query rather than the opaque location");
            True(f.Modules.SelectedItem is null && f.SelectedRoute is null, "forward search has no fabricated sidebar selection");
            AssertSidebarOwner(f, music);
            True(RaiseKey(f.Shell, Key.Right, ModifierKeys.Alt, true).Handled, "Alt+Right invokes forward history");
            await UntilAsync(() => f.SelectedOwner == film && f.SelectedRoute == "one" && f.Status == "", "forward to film home");
            Equal("film draft", f.Search.Text, "forward preserves the destination owner's route draft");
            AssertSidebarOwner(f, film);
            False(f.Forward.IsEnabled, "successful forward consumes the last future destination");
            Equal(captures, music.CaptureCount + film.CaptureCount, "forward reuses successful locations without recapturing");
            Sequence(["route:one", "search:original query", "restore-" + opaque, "restore-route:one", "restore-" + opaque],
                music.Calls, "back and forward restore opaque owner locations without another search");
            Sequence(["route:one", "restore-route:one"], film.Calls, "forward restores rather than navigates the media home");
            Equal(1, music.InitializeCount, "history retains the music instance");
            Equal(1, film.InitializeCount, "history retains the film instance");
            Same(main, Host(music.View), "history retains the visited content tree");
            Same(player, Host(music.PlayerView!), "history retains the global player host");
            Same(music.PlayerView, f.Element<ContentControl>("PlayerHost").Content, "playerless destination keeps the existing player");
        });
        foreach (var cancelled in new[] { false, true })
        yield return Case((cancelled ? "cancelled" : "failed") + " forward keeps its destination until a successful explicit retry", async () =>
        {
            var module = new ProbeModule("forward");
            await using var f = new Fixture([module]); await f.LoadAsync(); await f.NavigateAsync(module, "two");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one" && f.Status == "", "prepare future");
            using var pending = new HeldOperation(true);
            module.RestoreHook = (_, token) => cancelled ? pending.RunAsync(token) :
                Task.FromException(new InvalidOperationException("private-forward-detail"));
            var captures = module.CaptureCount;
            Click(f.Forward);
            if (cancelled)
            {
                await pending.Started.Task;
                Equal("one", f.SelectedRoute, "pending forward cannot commit its destination");
                Click(f.Button("取消当前操作"));
                await UntilAsync(() => module.ActiveCalls == 0 && f.Status == "已取消", "cancel forward");
                True(pending.Token.IsCancellationRequested, "forward receives the shell's cancellation");
            }
            else await UntilAsync(() => f.Status == "暂时无法完成操作", "forward failure");
            Equal("one", f.SelectedRoute, "unsuccessful forward keeps the committed route selected");
            True(f.Forward.IsEnabled, "unsuccessful forward retains the future destination");
            False(f.Status.Contains("private-forward-detail", StringComparison.Ordinal), "forward failure remains sanitized");
            module.RestoreHook = null;
            Click(cancelled ? f.Forward : f.Button("重试"));
            await UntilAsync(() => f.SelectedRoute == "two" && f.Status == "", "successful forward retry");
            False(f.Forward.IsEnabled, "only successful forward consumes the destination");
            Equal(captures, module.CaptureCount, "retry remains an opaque restore");
            Equal(1, module.MaximumConcurrentCalls, "forward and retry use the same operation gate");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one" && f.Status == "", "back after forward retry");
            False(f.Back.IsEnabled, "failed or cancelled forward never inserted a duplicate history entry");
            Sequence(["route:one", "route:two", "restore-route:one", "restore-route:two", "restore-route:two", "restore-route:one"],
                module.Calls, "both attempts restore the same future location and commit once");
        });
        yield return Case("only a successful new route replaces forward history", async () =>
        {
            var module = new ProbeModule("branch");
            await using var f = new Fixture([module]); await f.LoadAsync();
            await f.NavigateAsync(module, "two"); await f.NavigateAsync(module, "three");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "two" && f.Status == "", "back before branching");
            module.NavigateHook = (route, _) => route == "broken" ? Task.FromException(new Exception("private-branch")) : Task.CompletedTask;
            f.Select(module, "broken"); await UntilAsync(() => f.Status == "暂时无法完成操作", "failed branch");
            True(f.Forward.IsEnabled, "failed route does not erase a successful future location");
            await f.NavigateAsync(module, "slow");
            False(f.Forward.IsEnabled, "a successful new route replaces forward history");
            var calls = module.Calls.Count;
            Click(f.Forward); await DrainAsync();
            Equal(calls, module.Calls.Count, "disabled forward cannot replay the abandoned branch");
            Equal("slow", f.SelectedRoute, "the successful new branch remains selected");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "two" && f.Status == "", "back from new branch");
            Equal("restore-route:two", module.Calls[^1], "back skips the failed branch");
        });
        yield return Case("public pages settings and global overlays disable forward without consuming module history", async () =>
        {
            var module = new ImmersiveProbe();
            var page = new ProbePage("public");
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync(); await f.NavigateAsync(module, "two");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one" && f.Status == "", "prepare future");
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            await UntilAsync(() => page.Activations == 1 && f.Status == "", "public page");
            await AssertBlocked("public page");
            Click(f.Back); await DrainAsync();
            True(f.Forward.IsEnabled, "leaving a public page restores module forward");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            await AssertBlocked("settings");
            Click(f.Back); await DrainAsync();
            True(f.Forward.IsEnabled, "leaving settings restores module forward");
            foreach (var surface in new[] { DesktopGlobalSurface.PlayQueue, DesktopGlobalSurface.PlayerDetail })
            {
                module.Request(surface);
                FrameworkElement panel = surface == DesktopGlobalSurface.PlayQueue
                    ? f.Element<Border>("QueueSurface") : f.Element<Grid>("PlayerDetailSurface");
                await UntilAsync(() => panel.Visibility == Visibility.Visible, "open " + surface);
                await AssertBlocked(surface.ToString());
                True(RaiseKey(f.Shell, Key.Escape, ModifierKeys.None, true).Handled, "Escape dismisses " + surface);
                await UntilAsync(() => panel.Visibility == Visibility.Collapsed && f.Forward.IsEnabled, "dismiss " + surface);
            }
            Click(f.Forward); await UntilAsync(() => f.SelectedRoute == "two" && f.Status == "", "future survived temporary surfaces");
            Sequence(["route:one", "route:two", "restore-route:one", "restore-route:two"], module.Calls,
                "temporary surfaces neither replay nor consume module forward");

            async Task AssertBlocked(string boundary)
            {
                False(f.Forward.IsEnabled, boundary + " disables the real forward button");
                var calls = module.Calls.Count;
                Click(f.Forward); RaiseKey(f.Shell, Key.Right, ModifierKeys.Alt, true); await DrainAsync();
                Equal(calls, module.Calls.Count, boundary + " rejects both button and keyboard forward");
            }
        });
        yield return Case("notification caption renders only its accessible icon and has no navigation or notice business", async () =>
        {
            var module = new ProbeModule("caption");
            await using var f = new Fixture([module]); await f.LoadAsync(); await f.NavigateAsync(module, "two");
            var root = DetachChrome(f.Shell);
            using var source = HeaderSource(root);
            root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout(); await DrainAsync();
            var caption = f.Element<StackPanel>("CaptionBar");
            var bell = f.Element<Button>("NotificationButton");
            Equal(5, caption.Children.OfType<Button>().Count(), "caption contains notification settings and three window controls");
            Same(bell, caption.Children[0], "notification precedes settings");
            Same(f.Element<Button>("SettingsButton"), caption.Children[1], "settings remains immediately beside notification");
            Equal("通知", AutomationProperties.GetName(bell), "icon-only button keeps an accessible name");
            True(bell.Content is IconPath { Data: not null } && bell.Command is null, "bell contains a vector icon without a business command");
            False(ChromeVisuals(bell).OfType<TextBlock>().Any(), "bell has no visible label unread count or badge text");
            foreach (var button in caption.Children.OfType<Button>()) Equal(42d, button.ActualWidth, "caption button width");
            var notices = f.Element<NoticeStack>("Notices");
            notices.Show(new object(), "原有操作通知");
            var notice = notices.Children[0];
            var player = f.Element<ContentControl>("PlayerHost").Content;
            var view = Host(module.View); var settingsParent = LogicalTreeHelper.GetParent(module.SettingsView);
            f.Search.Text = "retained caption draft";
            var calls = module.Calls.Count; var status = f.Status;
            Click(bell); await DrainAsync();
            Equal(calls, module.Calls.Count, "bell invokes no module operation");
            Equal("two", f.SelectedRoute, "bell does not navigate or open settings");
            Same(settingsParent, LogicalTreeHelper.GetParent(module.SettingsView), "bell does not mount settings");
            Equal(Visibility.Visible, view.Visibility, "bell keeps the active content visible");
            Same(player, f.Element<ContentControl>("PlayerHost").Content, "bell retains the existing player");
            Equal("retained caption draft", f.Search.Text, "bell retains search editing state");
            Equal(status, f.Status, "bell publishes no operation result");
            Equal(1, notices.Children.Count, "existing operation notices remain intact");
            Same(notice, notices.Children[0], "bell cannot replace the existing operation notification");
            Equal(Visibility.Collapsed, f.Element<Grid>("PlayerDetailSurface").Visibility, "bell does not open a global page");
            Equal(Visibility.Collapsed, f.Element<Border>("QueueSurface").Visibility, "bell does not open a queue");
            source.RootVisual = null;
        });
    }

    private static void AssertSidebarOwner(Fixture fixture, ProbeModule owner)
    {
        var entries = fixture.Navigation.Items.OfType<ShellNavigationEntry>()
            .Concat(fixture.Element<ListBox>("CollectionNavigationList").Items.OfType<ShellNavigationEntry>()).ToArray();
        var expected = owner.Navigation.Skip(1).Where(item => item.ShowInSidebar).Select(item => item.Route).Order(StringComparer.Ordinal);
        Sequence(expected, entries.Select(item => item.Route).Order(StringComparer.Ordinal), "lower sidebar contains every current non-home destination exactly once");
        True(entries.All(item => ReferenceEquals(item.Owner.Module, owner)), "lower sidebar entries belong to the current owner");
        False(entries.Any(item => item.Route == owner.Navigation[0].Route), "media home has no duplicate lower sidebar entry");
    }
}
