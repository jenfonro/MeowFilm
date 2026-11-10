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
        yield return Case("infinity stays before loop in empty and ordinary footers without clipping or a new theme", async () =>
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
                        var button = (Button)view.FindName("PlaybackSessionButton");
                        var icon = (MusicPlaybackIcon)button.Content;
                        var transport = (StackPanel)view.FindName("TransportPanel");
                        True(transport.Children.OfType<Button>().Select(child => AutomationProperties.GetName(child))
                            .Take(5).SequenceEqual(new[] { "推荐与续播", f.Module.PlaybackModeText, "上一首", "播放或暂停", "下一首" }),
                            "infinity, loop and transport retain their public order");
                        True(button.IsVisible && button.IsEnabled && button.IsTabStop,
                            "empty and ordinary queues keep an accessible enabled infinity entry");
                        Equal("fm-off", icon.Key);
                        Equal(f.Module.PlaybackQueueSourceText, AutomationProperties.GetItemStatus(button));
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
                            True(button.TranslatePoint(new Point(), view).X <
                                ((Button)view.FindName("ModeButton")).TranslatePoint(new Point(), view).X,
                                "rendered infinity is left of the independent loop button");
                        }
                        DesktopTheme.SetIsHighContrast(view, false);
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        await Drain();
                        var menu = (ContextMenu)view.FindName("PlaybackSessionMenu");
                        True(menu.IsOpen && menu.Items.OfType<MenuItem>().All(item => item.Items.Count == 0),
                            "native left-click path opens one flat menu");
                        await AssertOpenedSongMenuStyles(menu, view);
                        menu.IsOpen = false; await Drain();
                        var request = RaiseSongMenu(button); await Drain();
                        True(request.Handled && menu.IsOpen, "native context request opens the same infinity menu");
                        True(menu.Items.OfType<MenuItem>().All(item => !item.IsEnabled),
                            "recordings without a supported native identity do not invent entry actions");
                        menu.IsOpen = false;
                    }
                }
            }
            finally
            {
                footer.Deactivate(); detailFooter.Deactivate(); surface.RootVisual = null;
            }
        });
        yield return Case("native infinity renders both loops and only ordinary state adds a diagonal across DPI", () =>
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
                var active = Paint("fm", scale); var ordinary = Paint("fm-off", scale);
                int Ink(byte[] pixels, Rect region) => Enumerable.Range(0, size * size).Count(pixel =>
                    pixels[pixel * 4 + 3] > 32 &&
                    region.Contains(new Point((pixel % size + .5) / scale, (pixel / size + .5) / scale)));
                foreach (var lobe in new[] { new Rect(2, 8, 8, 8), new Rect(14, 8, 8, 8) })
                {
                    True(Ink(active, lobe) > 15 * scale * scale, "active infinity paints both recognizable loops");
                    True(Ink(ordinary, lobe) >= Ink(active, lobe), "ordinary infinity retains both loops");
                }
                foreach (var end in new[] { new Rect(16, 2, 6, 4), new Rect(2, 18, 6, 4) })
                {
                    Equal(0, Ink(active, end));
                    True(Ink(ordinary, end) > 2 * scale * scale,
                        "ordinary diagonal extends above the upper-right and below the lower-left loop");
                }
                for (int pixel = 0; pixel < size * size; pixel++)
                    True(ordinary[pixel * 4 + 3] >= active[pixel * 4 + 3],
                        "adding the slash never erases the original native infinity ink");
            }
            return Task.CompletedTask;
        });
    }
}
