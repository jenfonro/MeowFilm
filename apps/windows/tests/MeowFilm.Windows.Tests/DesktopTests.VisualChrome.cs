using Colors = Microsoft.UI.Colors;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Text;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> VisualChromeCases()
    {
        yield return Case("sidebar samples only background and retains blur across resize", async () =>
        {
            await using var f = new Fixture([new ProbeModule("backdrop")]); await f.LoadAsync();
            var root = f.Shell.Root; var background = f.Element<Border>("ApplicationBackground");
            var material = f.Element<SidebarBackdrop>("SidebarMaterial");
            Same(background, material.Source, "only dedicated background sibling is sampled");
            False(DesktopVisual.Contains(background, material), "sample excludes sidebar foreground");
            var retainedMaterial = ElementCompositionPreview.GetElementChildVisual(material);
            True(retainedMaterial is not null, "loaded sidebar has a composition effect surface");
            var tint = f.Element<Border>("SidebarTint");
            Equal(Color.FromArgb(0x66, 255, 255, 255), ((SolidColorBrush)tint.Background).Color, "separate translucent white tint");
            foreach (var size in new[] { new Size(1110, 800), new Size(900, 640) })
            {
                f.Shell.Resize(size.Width, size.Height); await DrainAsync(); root.UpdateLayout();
                background.Background = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops =
                    {
                        new() { Color = Colors.Black, Offset = 0 }, new() { Color = Colors.Black, Offset = .5 },
                        new() { Color = Colors.White, Offset = .5 }, new() { Color = Colors.White, Offset = 1 }
                    }
                };
                var image = await NativeDesktopCapture.CaptureAsync(root);
                Color Pixel(int x, int y)
                {
                    int offset = ((int)(y * image.Scale) * image.Width + (int)(x * image.Scale)) * 4;
                    return Color.FromArgb(255, image.Pixels[offset + 2], image.Pixels[offset + 1], image.Pixels[offset]);
                }
                double Untinted(byte value) => (value - 102d) / .6;
                int boundary = (int)root.ActualHeight / 2;
                True(Untinted(Pixel(190, boundary - 2).R) is > 20 and < 235 &&
                    Untinted(Pixel(190, boundary + 2).R) is > 20 and < 235, "actual background edge remains blurred");
                True(Pixel(350, boundary - 2).R < 5 && Pixel(350, boundary + 2).R > 250,
                    "content background retains the sharp source edge");
                var foreground = new Border { Width = 12, Height = 40, Background = new SolidColorBrush(Colors.Red),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(180, boundary - 20, 0, 0) };
                var layout = f.Element<Grid>("RootLayout"); Grid.SetRowSpan(foreground, 3); Grid.SetColumnSpan(foreground, 2);
                layout.Children.Add(foreground); root.UpdateLayout();
                image = await NativeDesktopCapture.CaptureAsync(root);
                Equal(Colors.Red, Pixel(180, boundary), "foreground stays sharp and out of sampled background");
                True(Untinted(Pixel(178, boundary).G) > 20, "foreground does not bleed into background");
                layout.Children.Remove(foreground);
                Same(background, material.Source, "resize retains the source identity");
                Same(retainedMaterial, ElementCompositionPreview.GetElementChildVisual(material),
                    "resize updates the retained effect instead of recreating it");
                double edge = f.Element<Grid>("RootLayout").ColumnDefinitions[0].ActualWidth / background.ActualWidth;
                background.Background = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(1, 0), GradientStops =
                    {
                        new() { Color = Colors.Black, Offset = 0 }, new() { Color = Colors.Black, Offset = edge },
                        new() { Color = Colors.White, Offset = edge }, new() { Color = Colors.White, Offset = 1 }
                    }
                };
                image = await NativeDesktopCapture.CaptureAsync(root);
                True(Untinted(Pixel(208, boundary).R) is > 20 and < 235,
                    "blur samples background beyond the sidebar edge instead of cropping its input");
            }
            material.Source = null;
            True(ElementCompositionPreview.GetElementChildVisual(material) is null, "removing the source detaches the effect");
            material.Source = background;
            True(ElementCompositionPreview.GetElementChildVisual(material) is not null, "restoring the source recreates its effect");
            var parent = (Grid)material.Parent;
            parent.Children.Remove(material);
            await DrainAsync();
            True(ElementCompositionPreview.GetElementChildVisual(material) is null, "unloading releases the composition attachment");
            parent.Children.Insert(0, material);
            await DrainAsync();
            True(ElementCompositionPreview.GetElementChildVisual(material) is not null, "reloading restores the current background");
        });
        yield return Case("shell preserves regular typography compact layout and accessible focus", async () =>
        {
            var module = new ProbeModule("typography");
            await using var f = new Fixture([module], pages: [new SearchEntryProbe(module)]); await f.LoadAsync();
            Equal(new GridLength(64), f.Element<Grid>("RootLayout").RowDefinitions[0].Height, "compact header height");
            Equal(new Thickness(0), f.Element<Grid>("ContentRegion").Margin, "content begins below header");
            Equal(22d, f.Element<ScrollViewer>("SidebarScroll").Margin.Top, "sidebar top inset");
            Equal(76d, f.Element<Border>("QueueSurface").Margin.Top, "queue below header");
            Equal(76d, f.Element<StackPanel>("NotificationOverlay").Margin.Top, "notices below header");
            var navigation = f.Navigation; var item = (ListViewItem)navigation.ContainerFromIndex(0);
            True(item.IsTabStop && item.UseSystemFocusVisuals, "navigation rows retain visible keyboard focus");
            var search = f.Search; var picker = f.Element<ComboBox>("SearchTargetPicker");
            True(search.IsTabStop && picker.IsTabStop, "search and category are keyboard accessible");
            Equal("搜索", AutomationProperties.GetName(search), "search accessible name");
            Equal(FontWeights.Normal, search.FontWeight, "search stays regular");
            var labels = ChromeVisuals(navigation).OfType<TextBlock>().ToArray();
            True(labels.Length > 0 && labels.All(text => text.FontWeight == FontWeights.Normal), "realized navigation labels stay regular");
            var bounds = DesktopTestUi.Bounds(picker, search);
            True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= search.ActualWidth && bounds.Bottom <= search.ActualHeight,
                "whole category entry fits inside search field");
            True(search.Padding.Left >= bounds.Right + 2 && search.Padding.Left <= bounds.Right + 8,
                "search text keeps one intentional gap after category");
            True(search.Background is SolidColorBrush { Color.A: 255 }, "opaque search background");
            search.Focus(FocusState.Programmatic); await DrainAsync();
            Same(search, DesktopVisual.Focused(f.Shell.Root), "search receives native focus");
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
            var root = f.Shell.Root;
            root.Measure(new(860, 600)); root.Arrange(new Rect(0, 0, 860, 600)); root.UpdateLayout();
            await DrainAsync();

            var button = ChromeVisuals(root).OfType<Button>().Single(item =>
                AutomationProperties.GetName(item) == "创建歌单");
            True(button.IsEnabled && button.Command is not null, "group action is keyboard-command enabled");
            Equal("\uE710", button.Content as string, "visible add glyph");
            Click(button); await DrainAsync();
            Equal(1, executions, "bound action executes once");
            enabled = false; action.Refresh(); await DrainAsync();
            False(button.IsEnabled, "group action observes current admission");
        });
        yield return Case("sidebar native scroll reaches collections without changing layout width", async () =>
        {
            var module = new ProbeModule("scroll");
            module.ReplaceNavigation(new ObservableCollection<NavigationItem>(module.Navigation.Concat(
                Enumerable.Range(0, 45).Select(i => new NavigationItem("list-" + i, "测试歌单 " + i, "", "我的")))));
            await using var f = new Fixture([module]); f.Shell.Resize(860, 600); await f.LoadAsync();
            var scroll = f.Element<ScrollViewer>("SidebarScroll"); var list = f.Element<ListView>("CollectionNavigationList");
            True(scroll.IsLoaded && DesktopVisual.IsVisible(scroll) && scroll.ScrollableHeight > 0, "collections overflow native viewer");
            var width = list.ActualWidth;
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(scroll)!;
            var provider = (Microsoft.UI.Xaml.Automation.Provider.IScrollProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Scroll);
            provider.Scroll(Microsoft.UI.Xaml.Automation.ScrollAmount.NoAmount, Microsoft.UI.Xaml.Automation.ScrollAmount.LargeIncrement);
            await UntilAsync(() => scroll.VerticalOffset > 0, "accessible scroll moves the sidebar");
            Equal(width, list.ActualWidth, "scrolling does not relayout navigation width");
            var offset = scroll.VerticalOffset;
            provider.Scroll(Microsoft.UI.Xaml.Automation.ScrollAmount.NoAmount, Microsoft.UI.Xaml.Automation.ScrollAmount.LargeIncrement);
            await UntilAsync(() => scroll.VerticalOffset > offset, "next page remains reachable");
            scroll.ChangeView(null, 0, null, true); await UntilAsync(() => scroll.VerticalOffset == 0, "return to top");
        });
        yield return Case("global player detail overlays the unchanged shell and restores it on dismissal", async () =>
        {
            var module = new ImmersiveProbe();
            var passive = new ProbeModule("film", hasPlayer: false);
            var page = new ProbePage("downloads");
            await using var f = new Fixture([module, passive], pages: [page]); await f.LoadAsync();
            var root = f.Shell.Root;
            void Layout()
            {
                root.Measure(new(1110, 800)); root.Arrange(new Rect(0, 0, 1110, 800)); root.UpdateLayout();
            }
            Rect Bounds(FrameworkElement element) => DesktopTestUi.Bounds(element, root);
            Layout();
            var ordinaryPlayer = f.Element<ContentControl>("PlayerHost").Content;
            var ordinaryBackdrop = f.Element<SidebarBackdrop>("SidebarMaterial");
            Same(f.Element<Border>("ApplicationBackground"), ordinaryBackdrop.Source,
                "the ordinary sidebar samples the dedicated application background");
            True(f.Element<Border>("SidebarBackground").Background is null && f.Element<Border>("PlayerFrame").Background is null,
                "window background is the only ordinary page backdrop");
            var ordinaryPlayerBounds = Bounds(f.Element<Border>("PlayerFrame"));
            var ordinaryContentBounds = Bounds(f.Element<Grid>("ContentRegion"));
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "player detail opens");
            Layout();
            var playerSurface = f.Element<Grid>("PlayerDetailSurface");
            Equal(new Rect(0, 0, root.ActualWidth, root.ActualHeight), Bounds(playerSurface), "complete player page covers every shell row");
            Equal(KeyboardNavigationMode.Cycle, playerSurface.TabFocusNavigation,
                "keyboard navigation stays within the complete player page");
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content, "ordinary footer stays mounted");
            Equal(ordinaryPlayerBounds, Bounds(f.Element<Border>("PlayerFrame")), "ordinary footer layout unchanged");
            Equal(ordinaryContentBounds, Bounds(f.Element<Grid>("ContentRegion")), "ordinary content layout unchanged");
            Equal(Visibility.Visible, f.Element<ScrollViewer>("SidebarScroll").Visibility, "underlying sidebar remains");
            Equal(Visibility.Visible, f.Element<Grid>("SearchChrome").Visibility, "underlying search remains");
            Equal(Visibility.Visible, f.Element<Button>("PlayerDetailBackButton").Visibility, "collapse available");
            Equal(216d, f.Element<Grid>("RootLayout").ColumnDefinitions[0].Width.Value, "underlying layout unchanged");
            True(f.Element<Grid>("RootLayout").Background is null, "overlay does not repaint the shell");
            Same(ordinaryBackdrop, f.Element<SidebarBackdrop>("SidebarMaterial"),
                "player expansion retains the background material rather than rebuilding the shell");
            Same(module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail),
                f.Element<ContentControl>("PlayerDetailHost").Content, "detail is one retained opaque surface");
            var escape = f.Shell.TryExecuteShortcut(VirtualKey.Escape, VirtualKeyModifiers.None);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Collapsed,
                "escape closes player detail");
            Layout();
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content, "same ordinary footer restored");
            Equal(ordinaryPlayerBounds, Bounds(f.Element<Border>("PlayerFrame")), "no footer relayout on dismissal");
            Equal(ordinaryContentBounds, Bounds(f.Element<Grid>("ContentRegion")), "no content relayout on dismissal");
            True(escape, "escape handled");
            Equal(216d, f.Element<Grid>("RootLayout").ColumnDefinitions[0].Width.Value, "sidebar width restored");
            Equal(1, module.Deactivations, "detail owner deactivated once");

            await f.SwitchAsync(passive);
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content,
                "player remains mounted on a capability without its own player");
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "global player remains visible across capabilities");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "player detail opens from another capability");
            f.Shell.TryExecuteShortcut(VirtualKey.Escape, VirtualKeyModifiers.None);
            await UntilAsync(() => Host(passive.View).Visibility == Visibility.Visible,
                "closing detail restores the other capability");
            module.Request(DesktopGlobalSurface.PlayQueue);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Visible,
                "global queue opens from another capability");
            f.Shell.TryExecuteShortcut(VirtualKey.Escape, VirtualKeyModifiers.None);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Collapsed,
                "queue closes back to the other capability");

            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Equal(Visibility.Visible, f.Element<ScrollViewer>("SidebarScroll").Visibility, "settings restores shell");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility,
                "settings hide the global player");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await DrainAsync();
            Equal(Visibility.Collapsed, f.Element<Grid>("PlayerDetailSurface").Visibility,
                "player detail is unavailable while settings are current");
            module.Request(DesktopGlobalSurface.PlayQueue);
            await DrainAsync();
            Equal(Visibility.Collapsed, f.Element<Border>("QueueSurface").Visibility,
                "queue is unavailable while settings are current");
            Equal(Visibility.Visible, Host(passive.SettingsView).Visibility,
                "the current settings surface remains mounted");
            Click(f.Back); await DrainAsync();
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "leaving settings restores the global player");
            DesktopTestUi.Select(f.Element<ListView>("PublicNavigationList"), page); await DrainAsync();
            Equal(Visibility.Visible, f.Element<Grid>("SearchChrome").Visibility, "shared public pages restore shell");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "player detail opens while a public page is current");
            f.Shell.TryExecuteShortcut(VirtualKey.Escape, VirtualKeyModifiers.None);
            await UntilAsync(() => Host(page.View).Visibility == Visibility.Visible,
                "closing detail restores the same public page");
            module.Request(DesktopGlobalSurface.PlayQueue);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Visible,
                "global queue opens while a public page is current");
            Same(module.GetGlobalSurface(DesktopGlobalSurface.PlayQueue),
                f.Element<ContentControl>("QueueHost").Content, "one retained queue surface");
            f.Shell.TryExecuteShortcut(VirtualKey.Escape, VirtualKeyModifiers.None);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Collapsed,
                "queue closes back to the same public page");
            Equal(Visibility.Visible, Host(page.View).Visibility, "public page remains mounted after queue dismissal");
            Click(f.Back); await DrainAsync();
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "same retained detail opens from the restored app");
            Click(f.Element<Button>("PlayerDetailBackButton"));
            await UntilAsync(() => playerSurface.Visibility == Visibility.Collapsed &&
                Host(passive.View).Visibility == Visibility.Visible, "collapse restores the same app surface");
            Same(ordinaryPlayer, f.Element<ContentControl>("PlayerHost").Content, "same ordinary footer restored after collapse");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            await UntilAsync(() => playerSurface.Visibility == Visibility.Collapsed &&
                Host(passive.SettingsView).Visibility == Visibility.Visible, "ordinary settings opens after collapsing the player page");
            Equal(1, module.InitializeCount, "presentation never creates another module");
        });
        yield return Case("immersive reading retires global audio surfaces without releasing the retained player", async () =>
        {
            var player = new ImmersiveProbe();
            var reader = new ReadingProbe();
            await using var f = new Fixture([player, reader]); await f.LoadAsync();
            var retainedPlayer = f.Element<ContentControl>("PlayerHost").Content;
            player.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "detail opens before reading");
            player.Request(DesktopGlobalSurface.PlayQueue);
            await UntilAsync(() => f.Element<Border>("QueueSurface").Visibility == Visibility.Visible,
                "queue opens before reading");
            await f.SwitchAsync(reader);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Collapsed &&
                f.Element<Border>("QueueSurface").Visibility == Visibility.Collapsed, "reading retires both audio surfaces");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility, "reading hides the audio transport");
            Equal(0d, f.Element<Grid>("RootLayout").ColumnDefinitions[0].Width.Value, "reading hides ordinary navigation");
            Same(retainedPlayer, f.Element<ContentControl>("PlayerHost").Content, "reading does not detach the retained player");
            Equal(0, player.DisposeCount, "reading does not dispose the playing module");
            Equal(2, player.Deactivations, "each open audio surface is retired once");
            player.Request(DesktopGlobalSurface.PlayerDetail);
            player.Request(DesktopGlobalSurface.PlayQueue);
            await DrainAsync();
            Equal(Visibility.Collapsed, f.Element<Grid>("PlayerDetailSurface").Visibility, "late detail cannot cover the reader");
            Equal(Visibility.Collapsed, f.Element<Border>("QueueSurface").Visibility, "late queue cannot cover the reader");
            await f.SwitchAsync(player);
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility, "leaving reading restores the same transport");
            Same(retainedPlayer, f.Element<ContentControl>("PlayerHost").Content, "leaving reading keeps the same player instance");
            player.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => f.Element<Grid>("PlayerDetailSurface").Visibility == Visibility.Visible,
                "ordinary global player behavior resumes after reading");
        });
    }
    private sealed class ReadingProbe() : ProbeModule("reading", hasPlayer: false), IDesktopImmersiveSource
    {
        public bool IsImmersive => true;
    }
    private sealed class ImmersiveProbe() : ProbeModule("immersive"), IDesktopGlobalSurfaceSource
    {
        private readonly Border detail = new() { Background = new SolidColorBrush(Colors.DarkSlateGray) };
        private readonly Border queue = new() { Background = new SolidColorBrush(Colors.White) };
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
