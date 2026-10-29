using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> VisualChromeCases()
    {
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
            Click(f.Element<Button>("PlayerDetailSettingsButton")); await DrainAsync();
            await UntilAsync(() => playerSurface.Visibility == Visibility.Collapsed &&
                Host(module.SettingsView).Visibility == Visibility.Visible, "player settings leaves the player page");
            Equal(1, module.InitializeCount, "presentation never creates another module");
        });
    }
    private sealed class ImmersiveProbe() : ProbeModule("immersive"), IDesktopGlobalSurfaceSource
    {
        private readonly Border detail = new() { Background = Brushes.DarkSlateGray };
        private readonly Border queue = new() { Background = Brushes.White };
        public int Deactivations { get; private set; }
        public event EventHandler<DesktopGlobalSurfaceRequestEventArgs>? GlobalSurfaceRequested;
        public FrameworkElement GetGlobalSurface(DesktopGlobalSurface surface) =>
            surface == DesktopGlobalSurface.PlayerDetail ? detail : queue;
        public Task ActivateGlobalSurfaceAsync(DesktopGlobalSurface surface, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void DeactivateGlobalSurface(DesktopGlobalSurface surface) => Deactivations++;
        public void Request(DesktopGlobalSurface surface) =>
            GlobalSurfaceRequested?.Invoke(this, new(surface));
    }
    private static Border DetachChrome(ShellWindow shell)
    {
        var root = (Border)shell.Content; shell.Content = null;
        root.Resources.MergedDictionaries.Add(shell.Resources); root.DataContext = shell.DataContext;
        root.Background = shell.Background; TextElement.SetForeground(root, shell.Foreground);
        TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
        return root;
    }
}
