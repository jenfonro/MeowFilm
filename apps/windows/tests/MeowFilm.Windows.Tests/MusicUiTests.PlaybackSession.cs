using Colors = Microsoft.UI.Colors;
using Color = global::Windows.UI.Color;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Window = MeowFilm.Desktop.DesktopWindow;
using WindowState = MeowFilm.Desktop.DesktopWindowState;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

// Program discovers this focused group through its public Cases method.
public static class MusicPlaybackSessionTests
{
    public static IEnumerable<TestCase> Cases() =>
        MusicUiTests.PlaybackSessionChromeCases()
            .Concat(MusicCatalogPersonalTests.PlaybackSessionSourceCases())
            .Concat(MusicCatalogPersonalTests.PlaybackSessionControlCases());
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> PlaybackSessionChromeCases()
    {
        yield return Case("unified playback mode keeps both footers accessible and unclipped across themes", async () =>
        {
            await using var f = await Fixture.Create();
            var footer = (MusicPlayerView)f.Module.PlayerView;
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var detailHost = (ContentControl)detail.FindName("DetailPlayerHost");
            var detailFooter = (MusicPlayerView)detailHost.Content;
            detailHost.Content = null;
            var root = new Grid();
            using var surface = new DesktopTestUi.Host(root, 1260, 100);
            ((Microsoft.UI.Windowing.OverlappedPresenter)surface.Window.AppWindow.Presenter).SetBorderAndTitleBar(false, false);
            try
            {
                foreach (bool ordinary in new[] { false, true })
                {
                    if (ordinary) { await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); }
                    foreach (var view in new[] { footer, detailFooter })
                    {
                        root.Children.Clear();
                        root.Children.Add(view);
                        await Until(() => view.IsLoaded && view.XamlRoot is not null);
                        var button = (Button)view.FindName("ModeButton");
                        var icon = (MusicPlaybackIcon)button.Content;
                        var transport = (StackPanel)button.Parent;
                        True(transport.Children.OfType<Button>().Select(child => AutomationProperties.GetName(child))
                            .Take(5).SequenceEqual(new[] { f.Module.PlaybackModeText, "上一首", "播放或暂停", "下一首", "播放队列" }),
                            "one mode entry precedes the transport and queue controls");
                        True(DesktopVisual.IsVisible(button) && button.IsEnabled && button.IsTabStop,
                            "empty and ordinary queues keep an accessible mode entry");
                        Equal("repeat", icon.Key);
                        var metadata = ((StackPanel)view.FindName("MetadataText")).Children.OfType<TextBlock>().ToArray();
                        Equal(metadata[0].FontFamily.Source, view.FontFamily.Source);
                        Equal(Microsoft.UI.Text.FontWeights.Normal.Weight, view.FontWeight.Weight);
                        foreach (int width in new[] { 860, 1260 })
                        foreach (bool contrast in new[] { false, true })
                        {
                            DesktopTheme.SetIsHighContrast(view, contrast);
                            double scale = view.XamlRoot.RasterizationScale;
                            surface.Window.AppWindow.ResizeClient(new((int)Math.Round(width * scale), (int)Math.Round(100 * scale)));
                            try
                            {
                                await Until(() => Math.Abs(root.ActualWidth - width) < .5 && Math.Abs(view.ActualWidth - width) < .5);
                            }
                            catch (TimeoutException error)
                            {
                                throw new InvalidOperationException($"footer native client did not settle (ordinary={ordinary}, detail={ReferenceEquals(view, detailFooter)}, contrast={contrast}, width={width}, root={root.ActualWidth}x{root.ActualHeight}, view={view.ActualWidth}x{view.ActualHeight}, desired={view.DesiredSize}, slot={Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(view)}, xamlRoot={view.XamlRoot.Size}, scale={view.XamlRoot.RasterizationScale}, client={surface.Window.AppWindow.ClientSize}, window={surface.Window.AppWindow.Size})", error);
                            }
                            root.UpdateLayout();
                            AssertPlayerBounds(view, width);
                            True(ReferenceEquals(button.Foreground, icon.Foreground),
                                "native vector consumes its button theme foreground");
                            if (contrast)
                                Equal(new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)).Color, ((SolidColorBrush)icon.Foreground).Color);
                            else
                            {
                                True(ReferenceEquals(DesktopVisual.Resource(view, "Capability.Player.Foreground"), button.Foreground), "mode uses the footer owner's palette");
                                True(ReferenceEquals(DesktopVisual.Resource(view, "Capability.Player.Text"), metadata[0].Foreground), "title uses the footer owner's palette");
                                True(ReferenceEquals(DesktopVisual.Resource(view, "Capability.Player.Muted"), metadata[1].Foreground), "artist uses the footer owner's palette");
                            }
                        }
                        DesktopTheme.SetIsHighContrast(view, false);
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        var menu = (MenuFlyout)view.FindName("ModeMenu");
                        await Until(() => menu.IsOpen && menu.Items.All(item => item.IsLoaded));
                        True(menu.IsOpen && !menu.Items.OfType<MenuFlyoutSubItem>().Any(),
                            "native left-click path opens one flat menu");
                        var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                            .SelectMany(popup => Visuals(popup.Child)).OfType<MenuFlyoutPresenter>()
                            .Single(panel => menu.Items.All(item => DesktopVisual.Contains(panel, item)));
                        var menuStyle = menu.MenuFlyoutPresenterStyle ?? presenter.Style;
                        True(ReferenceEquals(menuStyle, DesktopVisual.Resource(view, "MeowFilm.ContextMenu")) ||
                            ReferenceEquals(menuStyle?.BasedOn, DesktopVisual.Resource(view, "MeowFilm.ContextMenu")), "shared native menu chrome");
                        True(menu.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.Template is not null && item.FontWeight.Weight == 400),
                            "mode items retain their native template and regular typography");
                        var reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        void OnReopened(object? sender, object args) => reopened.TrySetResult();
                        menu.Opened += OnReopened;
                        try
                        {
                            menu.Hide();
                            PlayerMenuInput.For(view).Show(button);
                            await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                            True(menu.IsOpen && PlayerMenuInput.For(view).IsCurrent(button),
                                "the context-show owner reopens the same mode menu after real retirement");
                            var items = menu.Items.OfType<ToggleMenuFlyoutItem>().ToArray();
                            PlayerMenuInput.For(view).Show(button); await Drain();
                            True(menu.IsOpen && items.SequenceEqual(menu.Items.OfType<ToggleMenuFlyoutItem>()),
                                "a repeated context-show retains the open actions without toggling or rebuilding");
                        }
                        finally { menu.Opened -= OnReopened; }
                        Equal("列表循环,单曲循环,随机播放", string.Join(',', menu.Items.OfType<ToggleMenuFlyoutItem>()
                            .Select(item => FrameworkElementAutomationPeer.CreatePeerForElement(item)!.GetName())));
                        True(menu.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.IsEnabled), "ordinary modes do not require a native source identity");
                        await GeneralAppearanceTests.SavePreview(view, ordinary ? "playback-footer-ordinary" : "playback-footer-empty", 1260, 100);
                        menu.Hide();
                    }
                }
            }
            finally
            {
                footer.Deactivate(); detailFooter.Deactivate(); root.Children.Clear();
                detailHost.Content = detailFooter;
            }
        });
        yield return Case("playback glyphs render rounded loop infinity and recommendation across DPI", async () =>
        {
            async Task<byte[]> Paint(string key, double scale)
            {
                var icon = new MusicPlaybackIcon { Key = key, Width = 24, Height = 24, Foreground = (new SolidColorBrush(Microsoft.UI.Colors.White)) };
                using var host = new DesktopTestUi.Host(icon, 24, 24); await Drain();
                return (await DesktopTestUi.Render(icon, scale)).Pixels;
            }
            foreach (double scale in new[] { 1d, 1.5, 2 })
            {
                int size = (int)(24 * scale);
                var active = await Paint("fm", scale);
                int Ink(byte[] pixels, Rect region) => Enumerable.Range(0, size * size).Count(pixel =>
                    pixels[pixel * 4 + 3] > 32 &&
                    region.Contains(new Point((pixel % size + .5) / scale, (pixel / size + .5) / scale)));
                foreach (var lobe in new[] { new Rect(2, 8, 8, 8), new Rect(14, 8, 8, 8) })
                    True(Ink(active, lobe) > 15 * scale * scale, "infinity paints both recognizable loops");
                var loop = await Paint("repeat", scale);
                True(Ink(loop, new(2, 3, 20, 20)) > 70 * scale * scale, "rounded loop occupies a full-height circular silhouette");
                True(Ink(loop, new(7, 20, 10, 3)) > 4 * scale * scale, "loop is no longer vertically compressed");
                True(Ink(await Paint("sparkles", scale), new(0, 0, 24, 24)) > 35 * scale * scale, "recommendation has native sparkles ink");
            }
        });
    }
}
