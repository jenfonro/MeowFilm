using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> VisualChromeCases()
    {
        yield return Case("shell text follows system typography and search and navigation retain accessible focus", async () =>
        {
            await using var f = new Fixture([new ProbeModule("typography")]); await f.LoadAsync();
            Equal(SystemFonts.MessageFontFamily, f.Shell.FontFamily, "Windows system body font");
            Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(f.Shell), "display text metrics");
            Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(f.Shell), "grayscale text rendering");
            True(f.Shell.UseLayoutRounding && f.Shell.SnapsToDevicePixels, "native display pixels own text layout");
            var root = DetachChrome(f.Shell);
            using var source = new HwndSource(new HwndSourceParameters("shell-headless-hit-test-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            });
            source.RootVisual = root;
            root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout(); await DrainAsync();
            var navigation = f.Element<ListBox>("NavigationList");
            True(!navigation.Focusable && !f.Element<ScrollViewer>("SidebarScroll").Focusable,
                "scroll and list containers do not take focus away from navigation rows");
            var item = (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(0);
            True(item.Focusable && item.FocusVisualStyle is not null, "navigation rows keep a visible keyboard focus");
            Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(navigation), "one navigation tab scope");
            var search = f.Element<TextBox>("SearchBox");
            var action = f.Element<Button>("SearchButton");
            Equal(f.Shell.FontFamily, search.FontFamily, "search follows the body font");
            True(search.Focusable && action.Focusable, "both search entry and submit remain keyboard accessible");
            var origin = action.TranslatePoint(new Point(), search);
            True(origin.X >= 0 && origin.Y >= 0 &&
                origin.X + action.ActualWidth <= search.ActualWidth &&
                origin.Y + action.ActualHeight <= search.ActualHeight,
                "the real search action is inside the rounded search field");
            bool Hits(FrameworkElement target, Point point) =>
                target.IsEnabled && root.InputHitTest(target.TranslatePoint(point, root)) is DependencyObject hit &&
                (ReferenceEquals(target, hit) || target.IsAncestorOf(hit));
            True(Hits(action, new Point(action.ActualWidth / 2, action.ActualHeight / 2)),
                "the embedded search action receives pointer hits");
            True(Hits(search, new Point((search.Padding.Left + search.ActualWidth - search.Padding.Right) / 2, search.ActualHeight / 2)),
                "the text area keeps independent pointer hits");
            double TextOrigin(TextBox box)
            {
                box.ApplyTemplate(); box.UpdateLayout();
                var textView = VisualDescendants<FrameworkElement>(box)
                    .Single(element => element.GetType().Name == "TextBoxView");
                return textView.TranslatePoint(new Point(), box).X;
            }
            search.Text = "搜索内容"; root.UpdateLayout(); await DrainAsync();
            var searchTextOrigin = TextOrigin(search);
            var textChromeInset = searchTextOrigin - search.Padding.Left;
            True(textChromeInset is >= 0 and <= 4,
                $"search padding is applied once before the native text chrome ({searchTextOrigin:0.##} vs {search.Padding.Left:0.##})");
            True(searchTextOrigin - (origin.X + action.ActualWidth) is >= 2 and <= 8,
                "the search glyph and input text have only their intentional gap");
            var layout = (Grid)root.Child;
            var ordinary = new TextBox
            {
                Width = 220, Height = 38, Text = "普通输入", HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Panel.SetZIndex(ordinary, 100);
            layout.Children.Add(ordinary); root.UpdateLayout(); await DrainAsync();
            True(Math.Abs((TextOrigin(ordinary) - ordinary.Padding.Left) - textChromeInset) <= 1 &&
                Math.Abs(ordinary.Padding.Left - 12) < .01,
                "the shared text-box template preserves one ordinary padding application");
            layout.Children.Remove(ordinary);
            True(search.Background is SolidColorBrush { Color.A: 255 }, "search has an opaque flat background");
            DesktopTheme.SetIsHighContrast(root, true); root.UpdateLayout(); await DrainAsync();
            Equal(SystemColors.WindowColor, ((SolidColorBrush)search.Background).Color, "system search background");
            Equal(SystemColors.WindowTextColor, ((SolidColorBrush)search.Foreground).Color, "system search text");
            Equal(SystemColors.WindowTextColor, ((SolidColorBrush)action.Foreground).Color, "system search action");
            Equal(SystemColors.WindowTextColor, ((SolidColorBrush)f.Back.BorderBrush).Color, "system back button edge");
            True(f.Element<Border>("SidebarBackground").BorderThickness.Right > 0,
                "high contrast restores the structural sidebar edge");
            var alternate = new FontFamily("Arial");
            f.Shell.Resources[SystemFonts.MessageFontFamilyKey] = alternate; await DrainAsync();
            Equal(alternate, f.Shell.FontFamily, "shell follows dynamic system font resources");
            Equal(alternate, search.FontFamily, "search follows dynamic system font resources");
            f.Shell.Resources.Remove(SystemFonts.MessageFontFamilyKey); await DrainAsync();
            Equal(SystemFonts.MessageFontFamily, search.FontFamily, "system font restored without changing Windows settings");
            source.RootVisual = null;
        });
        yield return Case("sidebar collection group exposes one accessible action", async () =>
        {
            var enabled = true;
            var executions = 0;
            var action = new DesktopNavigationGroupAction("创建歌单", () => executions++, () => enabled);
            var module = new ProbeModule("collections");
            module.ReplaceNavigation(new ObservableCollection<NavigationItem>(
            new NavigationItem[]
            {
                new("one", "首页", ""),
                new("favorites", "我的收藏", "", "我的", GroupAction: action),
                new("playlist/1", "本地歌单", "", "我的")
            }));
            await using var f = new Fixture([module]);
            await f.LoadAsync();
            var root = DetachChrome(f.Shell);
            root.Measure(new(860, 600)); root.Arrange(new Rect(0, 0, 860, 600)); root.UpdateLayout();
            await DrainAsync();

            var button = ChromeVisuals(root).OfType<Button>().Single(item =>
                AutomationProperties.GetName(item) == "创建歌单");
            True(button.IsEnabled && button.Command is not null, "group action is keyboard-command enabled");
            Equal("\uE710", button.Content as string, "visible add glyph");
            button.Command!.Execute(button.CommandParameter);
            Equal(1, executions, "bound action executes once");
            enabled = false; action.Refresh(); await DrainAsync();
            False(button.IsEnabled, "group action observes current admission");
        });
        yield return Case("sidebar overlay scroll hides at rest and scrolls over collection items", async () =>
        {
            var module = new ProbeModule("scroll");
            module.ReplaceNavigation(new ObservableCollection<NavigationItem>(module.Navigation.Concat(
                Enumerable.Range(0, 45).Select(i => new NavigationItem("list-" + i, "测试歌单 " + i, "", "我的")))));
            await using var f = new Fixture([module]); await f.LoadAsync();
            var root = DetachChrome(f.Shell);
            root.Measure(new(860, 600)); root.Arrange(new Rect(0, 0, 860, 600)); root.UpdateLayout(); await DrainAsync();
            var scroll = f.Element<ScrollViewer>("SidebarScroll");
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            True(OverlayScrollChrome.GetEnabled(scroll), "shared behavior mounted");
            True(scroll.ScrollableHeight > 0, "long collections overflow");
            Equal(0d, bar.Opacity, "hidden at rest");
            var list = f.Element<ListBox>("CollectionNavigationList");
            var width = list.ActualWidth;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            list.RaiseEvent(wheel); await DrainAsync(); root.UpdateLayout();
            True(wheel.Handled && scroll.VerticalOffset > 0, "wheel on a nested list reaches sidebar, no blank-space requirement");
            True(OverlayScrollChrome.GetActive(scroll) && bar.Opacity > 0, "scroll exposes thumb");
            Equal(width, list.ActualWidth, "overlay never changes navigation width");
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            True(track.Thumb.ActualHeight >= 28, "draggable thumb minimum");
            var offset = scroll.VerticalOffset;
            ((RoutedCommand)track.IncreaseRepeatButton.Command).Execute(null, track.IncreaseRepeatButton); await DrainAsync();
            True(scroll.VerticalOffset > offset, "track page click works");
            await Task.Delay(1100); await DrainAsync();
            Equal(0d, bar.Opacity, "900ms idle hides scrollbar");
            DesktopTheme.SetIsHighContrast(root, true); root.UpdateLayout(); Equal(1d, bar.Opacity, "high contrast remains visible");
            scroll.ScrollToTop(); await DrainAsync();
            scroll.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            True(!OverlayScrollChrome.GetActive(scroll), "unload cancels active timer");
        });
        yield return Case("global player detail overlays the unchanged shell and restores it on dismissal", async () =>
        {
            var module = new ImmersiveProbe();
            var page = new ProbePage("downloads");
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync();
            var root = DetachChrome(f.Shell);
            void Layout()
            {
                root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout();
            }
            Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), root), element.RenderSize);
            Layout();
            var ordinaryPlayer = f.Element<ContentControl>("PlayerHost").Content;
            True(f.Element<Border>("SidebarBackground").Background is null && f.Element<Border>("PlayerFrame").Background is null,
                "window background is the only ordinary page backdrop");
            var ordinaryPlayerBounds = Bounds(f.Element<Border>("PlayerFrame"));
            var ordinaryContentBounds = Bounds(f.Element<Grid>("ContentRegion"));
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "player detail opens");
            Layout();
            var playerSurface = f.Element<Grid>("PlayerDetailSurface");
            Equal(new Rect(root.RenderSize), Bounds(playerSurface), "complete player page covers every shell row");
            Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(playerSurface),
                "keyboard navigation stays within the complete player page");
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content, "ordinary footer stays mounted");
            Equal(ordinaryPlayerBounds, Bounds(f.Element<Border>("PlayerFrame")), "ordinary footer layout unchanged");
            Equal(ordinaryContentBounds, Bounds(f.Element<Grid>("ContentRegion")), "ordinary content layout unchanged");
            Equal(Visibility.Visible, f.Element<ScrollViewer>("SidebarScroll").Visibility, "underlying sidebar remains");
            Equal(Visibility.Visible, f.Element<Grid>("SearchChrome").Visibility, "underlying search remains");
            Equal(Visibility.Visible, f.Element<Button>("PlayerDetailBackButton").Visibility, "collapse available");
            Equal(203d, f.Element<Grid>("RootLayout").ColumnDefinitions[0].Width.Value, "underlying layout unchanged");
            True(f.Element<Grid>("RootLayout").Background is null, "overlay does not repaint the shell");
            Same(module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail),
                f.Element<ContentControl>("PlayerDetailHost").Content, "detail is one retained opaque surface");
            var escape = RaiseKey(f.Shell, Key.Escape, ModifierKeys.None, preview: true);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Collapsed,
                "escape closes player detail");
            Layout();
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content, "same ordinary footer restored");
            Equal(ordinaryPlayerBounds, Bounds(f.Element<Border>("PlayerFrame")), "no footer relayout on dismissal");
            Equal(ordinaryContentBounds, Bounds(f.Element<Grid>("ContentRegion")), "no content relayout on dismissal");
            True(escape.Handled, "escape handled");
            Equal(203d, f.Element<Grid>("RootLayout").ColumnDefinitions[0].Width.Value, "sidebar width restored");
            Equal(1, module.Deactivations, "detail owner deactivated once");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Equal(Visibility.Visible, f.Element<ScrollViewer>("SidebarScroll").Visibility, "settings restores shell");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "player detail opens while settings is current");
            RaiseKey(f.Shell, Key.Escape, ModifierKeys.None, preview: true);
            await UntilAsync(() => Host(module.SettingsView).Visibility == Visibility.Visible,
                "closing detail restores the same settings surface");
            module.Request(DesktopGlobalSurface.PlayQueue);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Visible,
                "global queue opens while settings is current");
            RaiseKey(f.Shell, Key.Escape, ModifierKeys.None, preview: true);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Collapsed,
                "queue closes back to settings");
            Equal(Visibility.Visible, Host(module.SettingsView).Visibility,
                "settings surface remains mounted after queue dismissal");
            Click(f.Back); await DrainAsync();
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page; await DrainAsync();
            Equal(Visibility.Visible, f.Element<Grid>("SearchChrome").Visibility, "shared public pages restore shell");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "player detail opens while a public page is current");
            RaiseKey(f.Shell, Key.Escape, ModifierKeys.None, preview: true);
            await UntilAsync(() => Host(page.View).Visibility == Visibility.Visible,
                "closing detail restores the same public page");
            module.Request(DesktopGlobalSurface.PlayQueue);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Visible,
                "global queue opens while a public page is current");
            Same(module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue),
                f.Element<ContentControl>("QueueHost").Content, "one retained queue surface");
            RaiseKey(f.Shell, Key.Escape, ModifierKeys.None, preview: true);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Collapsed,
                "queue closes back to the same public page");
            Equal(Visibility.Visible, Host(page.View).Visibility, "public page remains mounted after queue dismissal");
            Click(f.Back); await DrainAsync();
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "same retained detail opens from the restored app");
            Click(f.Element<Button>("PlayerDetailBackButton"));
            await UntilAsync(() => playerSurface.Visibility == Visibility.Collapsed &&
                Host(module.View).Visibility == Visibility.Visible, "collapse restores the same app surface");
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content, "same ordinary footer restored after collapse");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            await UntilAsync(() => playerSurface.Visibility == Visibility.Collapsed &&
                Host(module.SettingsView).Visibility == Visibility.Visible, "ordinary settings opens after collapsing the player page");
            Equal(1, module.InitializeCount, "presentation never creates another module");
        });
    }
    private sealed class ImmersiveProbe() : ProbeModule("immersive"), IDesktopGlobalSurfaceSource
    {
        private readonly Border detail = new() { Background = Brushes.DarkSlateGray };
        private readonly Border queue = new() { Background = Brushes.White };
        public int Deactivations { get; private set; }
        public Task? PlayerReady { get; set; }
        public List<CancellationToken> PlayerActivations { get; } = [];
        public event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;
        public FrameworkElement GetGlobalSurface(DesktopGlobalSurface surface) =>
            surface == DesktopGlobalSurface.PlayerDetail ? detail : queue;
        public Task ActivateGlobalSurfaceAsync(DesktopGlobalSurface surface, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (surface == DesktopGlobalSurface.PlayerDetail)
            {
                PlayerActivations.Add(cancellationToken);
                return PlayerReady ?? Task.CompletedTask;
            }
            return Task.CompletedTask;
        }
        public void DeactivateGlobalSurface(DesktopGlobalSurface surface) => Deactivations++;
        public void Request(DesktopGlobalSurface surface) =>
            GlobalSurfaceRequested?.Invoke(this, new(surface));
    }
    private static IEnumerable<DependencyObject> ChromeVisuals(DependencyObject current)
    {
        yield return current;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            foreach (var child in ChromeVisuals(VisualTreeHelper.GetChild(current, index))) yield return child;
    }
    private static Border DetachChrome(ShellWindow shell)
    {
        var root = (Border)shell.Content; shell.Content = null;
        root.Resources.MergedDictionaries.Add(shell.Resources); root.DataContext = shell.DataContext;
        root.Background = shell.Background; TextElement.SetForeground(root, shell.Foreground);
        TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
        return root;
    }
    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }
}
