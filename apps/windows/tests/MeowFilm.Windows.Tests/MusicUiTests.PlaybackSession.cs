using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            var detailFooter = new MusicPlayerView(f.Module, showArtwork: false);
            using var surface = new HwndSource(new HwndSourceParameters("playback-session-chrome")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 1260, Height = 100
            });
            try
            {
                foreach (bool ordinary in new[] { false, true })
                {
                    if (ordinary) { await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); }
                    foreach (var view in new[] { footer, detailFooter })
                    {
                        surface.RootVisual = view; await Drain();
                        var button = (Button)view.FindName("ModeButton");
                        var icon = (MusicPlaybackIcon)button.Content;
                        var transport = (StackPanel)view.FindName("TransportPanel");
                        True(transport.Children.OfType<Button>().Select(child => AutomationProperties.GetName(child))
                            .Take(5).SequenceEqual(new[] { f.Module.PlaybackModeText, "上一首", "播放或暂停", "下一首", "播放队列" }),
                            "one mode entry precedes the transport and queue controls");
                        True(button.IsVisible && button.IsEnabled && button.IsTabStop,
                            "empty and ordinary queues keep an accessible mode entry");
                        Equal("repeat", icon.Key);
                        Equal(SystemFonts.MessageFontFamily.Source, view.FontFamily.Source);
                        foreach (int width in new[] { 860, 1260 })
                        foreach (bool contrast in new[] { false, true })
                        {
                            DesktopTheme.SetIsHighContrast(view, contrast);
                            Render(view, width, 100); await Drain();
                            AssertPlayerBounds(view, width);
                            True(ReferenceEquals(button.Foreground, icon.Foreground),
                                "native vector consumes its button theme foreground");
                            if (contrast)
                                Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)icon.Foreground).Color);
                        }
                        DesktopTheme.SetIsHighContrast(view, false);
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        await Drain();
                        var menu = (ContextMenu)view.FindName("ModeMenu");
                        True(menu.IsOpen && menu.Items.OfType<MenuItem>().All(item => item.Items.Count == 0),
                            "native left-click path opens one flat menu");
                        True(ReferenceEquals(menu.Style, view.FindResource("MeowFilm.ContextMenu")), "shared native menu chrome");
                        True(menu.Items.OfType<MenuItem>().All(item => ReferenceEquals(item.Template,
                            ((Style)view.FindResource("MeowFilm.MenuItem")).Setters.OfType<Setter>()
                            .Single(setter => setter.Property == Control.TemplateProperty).Value)), "mode items retain the shared template");
                        var reopened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        void OnReopened(object? sender, RoutedEventArgs args) => reopened.TrySetResult();
                        menu.Opened += OnReopened;
                        try
                        {
                            // Do not wait for the old native HWND to finish closing: the real context request
                            // must survive that transition without being mistaken for a left-click toggle.
                            menu.IsOpen = false;
                            var request = RaiseSongMenu(button);
                            True(request.Handled, "the native context request is owned by the mode entry");
                            await reopened.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
                            True(menu.IsOpen, "native context request opens the same mode menu");
                            var items = menu.Items.OfType<MenuItem>().ToArray();
                            True(RaiseSongMenu(button).Handled && menu.IsOpen &&
                                items.SequenceEqual(menu.Items.OfType<MenuItem>()),
                                "a repeated context request shows the existing menu rather than toggling it or rebuilding actions");
                        }
                        finally { menu.Opened -= OnReopened; }
                        Equal("列表循环,单曲循环,随机播放", string.Join(',', menu.Items.OfType<MenuItem>().Select(AutomationProperties.GetName)));
                        True(menu.Items.OfType<MenuItem>().All(item => item.IsEnabled), "ordinary modes do not require a native source identity");
                        GeneralAppearanceTests.SavePreview(view, ordinary ? "playback-footer-ordinary" : "playback-footer-empty", 1260, 100);
                        menu.IsOpen = false;
                    }
                }
            }
            finally
            {
                footer.Deactivate(); detailFooter.Deactivate(); surface.RootVisual = null;
            }
        });
        yield return Case("playback glyphs render rounded loop infinity and recommendation across DPI", () =>
        {
            byte[] Paint(string key, double scale)
            {
                var icon = new MusicPlaybackIcon { Key = key, Width = 24, Height = 24, Foreground = Brushes.White };
                icon.Measure(new(24, 24)); icon.Arrange(new(0, 0, 24, 24)); icon.UpdateLayout();
                int size = (int)(24 * scale);
                var frame = new RenderTargetBitmap(size, size, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                frame.Render(icon);
                var pixels = new byte[size * size * 4]; frame.CopyPixels(pixels, size * 4, 0);
                return pixels;
            }
            foreach (double scale in new[] { 1d, 1.5, 2 })
            {
                int size = (int)(24 * scale);
                var active = Paint("fm", scale);
                int Ink(byte[] pixels, Rect region) => Enumerable.Range(0, size * size).Count(pixel =>
                    pixels[pixel * 4 + 3] > 32 &&
                    region.Contains(new Point((pixel % size + .5) / scale, (pixel / size + .5) / scale)));
                foreach (var lobe in new[] { new Rect(2, 8, 8, 8), new Rect(14, 8, 8, 8) })
                    True(Ink(active, lobe) > 15 * scale * scale, "infinity paints both recognizable loops");
                var loop = Paint("repeat", scale);
                True(Ink(loop, new(2, 3, 20, 20)) > 70 * scale * scale, "rounded loop occupies a full-height circular silhouette");
                True(Ink(loop, new(7, 20, 10, 3)) > 4 * scale * scale, "loop is no longer vertically compressed");
                True(Ink(Paint("sparkles", scale), new(0, 0, 24, 24)) > 35 * scale * scale, "recommendation has native sparkles ink");
            }
            return Task.CompletedTask;
        });
    }
}
