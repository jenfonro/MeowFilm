using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> PlayerChromeCases()
    {
        yield return Case("footer rail touches top edge without a second rule", async () =>
        {
            await using var f = await Fixture.Create();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var progress = (SeekBar)footer.FindName("Progress");
            Equal(0d, progress.TranslatePoint(new Point(), footer).Y);
            True(progress.RailAtTop, "no inset track");
            var bar = new SeekBar { Maximum = 100, Value = 50, Height = 16, RailAtTop = true };
            var host = new Border { Background = Brushes.DarkSlateGray, Child = bar };
            byte[] Pixels()
            {
                host.Measure(new(400, 16)); host.Arrange(new(0, 0, 400, 16)); host.UpdateLayout();
                var bitmap = new RenderTargetBitmap(400, 16, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host); var bytes = new byte[400 * 16 * 4]; bitmap.CopyPixels(bytes, 1600, 0); return bytes;
            }
            bar.PlayedBrush = Brushes.CornflowerBlue;
            var first = Pixels();
            True(!first.AsSpan(300 * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 300) * 4, 4)), "ordinary faint track at top");
            True(!first.AsSpan(100 * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 100) * 4, 4)), "played part paints at y zero");
            bar.PlayedBrush = Brushes.SandyBrown;
            var second = Pixels();
            True(!first.AsSpan(100 * 4, 4).SequenceEqual(second.AsSpan(100 * 4, 4)), "cover-driven brush is consumed");
            True(first.AsSpan(300 * 4, 4).SequenceEqual(second.AsSpan(300 * 4, 4)), "unplayed stays neutral");
            True(first.AsSpan((4 * 400 + 300) * 4, 4).SequenceEqual(first.AsSpan((10 * 400 + 300) * 4, 4)), "no second lower line");
        });
        yield return Case("seek rail is continuous at rest active disabled and high contrast across DPI", async () =>
        {
            await using var f = await Fixture.Create();
            var bar = new SeekBar { Maximum = 180, Value = 90 };
            var host = new Border { Background = Brushes.White, Child = bar, Width = 400, Height = 8 };
            foreach (var highContrast in new[] { false, true })
            foreach (var enabled in new[] { false, true })
            foreach (double scale in new[] { 1d, 1.5, 2d })
            {
                DesktopTheme.SetIsHighContrast(host, highContrast); bar.IsEnabled = enabled;
                host.Background = Brushes.White;
                host.Measure(new(400, 8)); host.Arrange(new(0, 0, 400, 8)); host.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(400 * scale), (int)(8 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(host); int width = bitmap.PixelWidth, stride = width * 4;
                var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
                // A hidden Thumb formerly left 8 DIP uncovered. Both rail segments
                // must meet at exactly 50%, including their 0% and 100% endpoints.
                for (int x = 1; x < width - 1; x++)
                {
                    int i = (int)(4 * scale) * stride + x * 4, sample = (int)(4 * scale) * stride + (x < width / 2 ? 1 : width - 2) * 4;
                    True(pixels.AsSpan(i, 4).SequenceEqual(pixels.AsSpan(sample, 4)), "continuous rail without thumb hole or black seam");
                }
                True(!Visuals(bar).OfType<System.Windows.Controls.Primitives.Track>().Any(), "no system track paints another line");
                Equal(0d, bar.ValueAt(-20)); Equal(180d, bar.ValueAt(420)); Equal(90d, bar.ValueAt(200));
            }
            // Explicit active gesture drives a real animation clock; verify the
            // settled rail is 5 DIP rather than checking a style setter only.
            bar.IsEnabled = true; DesktopTheme.SetIsHighContrast(host, false);
            bar.BeginScrub(); await Task.Delay(170); await Drain();
            Render(host, 400, 8);
            bar.CancelScrub(); await Task.Delay(170); await Drain();
            Render(host, 400, 8);
        });
        yield return Case("seek gesture isolates live ticks commits once and rejects a retired track", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView; var bar = (SeekBar)footer.FindName("Progress");
            footer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Drain();
            bar.BeginScrub(); bar.UpdateScrub(74);
            await f.Playback.SeekAsync(12); await Drain();
            Equal(74d, bar.Value); Equal(12d, f.Playback.Seek);
            bar.EndScrub(true); await Until(() => !f.Module.Busy);
            Equal(74d, f.Playback.Seek); True(!bar.IsScrubbing, "released");
            bar.BeginScrub(); bar.UpdateScrub(95); bar.CancelScrub(); await Drain();
            Equal(74d, f.Playback.Seek); Equal(74d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(120);
            await f.Module.StepAsync(true); await Drain(); double replacementPosition = f.Playback.Seek;
            True(!bar.IsScrubbing, "track replacement immediately retires gesture");
            bar.EndScrub(true); await Drain(); Equal(replacementPosition, f.Playback.Seek);
            // UI Automation uses the same transaction and real media port.
            var peer = UIElementAutomationPeer.CreatePeerForElement(bar)!;
            ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)).SetValue(36);
            await Until(() => !f.Module.Busy); Equal(36d, f.Playback.Seek);
            using var inputSource = new HwndSource(new HwndSourceParameters("seek-input-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 80
            });
            inputSource.RootVisual = footer;
            footer.Measure(new(1110, 80)); footer.Arrange(new(0, 0, 1110, 80)); footer.UpdateLayout(); await Drain();
            void Key(Key key, RoutedEvent route) => bar.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, 0, key) { RoutedEvent = route });
            Key(System.Windows.Input.Key.Right, Keyboard.PreviewKeyDownEvent);
            Equal(41d, bar.Value);
            await f.Playback.SeekAsync(37); await Drain(); Equal(41d, bar.Value);
            Key(System.Windows.Input.Key.Right, Keyboard.PreviewKeyUpEvent);
            await Until(() => !f.Module.Busy); Equal(41d, f.Playback.Seek);
            Key(System.Windows.Input.Key.End, Keyboard.PreviewKeyDownEvent); Equal(180d, bar.Value);
            Key(System.Windows.Input.Key.Escape, Keyboard.PreviewKeyDownEvent);
            Key(System.Windows.Input.Key.End, Keyboard.PreviewKeyUpEvent);
            await Drain(); Equal(41d, f.Playback.Seek); Equal(41d, bar.Value);
            bar.BeginScrub(); bar.UpdateScrub(50); footer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            True(!bar.IsScrubbing, "unload cancels"); Equal(41d, f.Playback.Seek);
        });
        yield return Case("skip arrows animate direction without duplicate queue calls and clear on unload", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var next = (SkipButton)footer.FindName("NextButton");
            var previous = (SkipButton)footer.FindName("PreviousButton");
            True(next.Next && !previous.Next, "mirrored directions");
            next.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(70); await Drain();
            if (SystemParameters.ClientAreaAnimation) True(next.HasAnimatedProperties, "native hover motion active");
            Equal(0, f.Calls.Count("music.queue.next"));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.next"));
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !f.Module.Busy);
            Equal(1, f.Calls.Count("music.queue.previous"));
            next.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            True(!next.HasAnimatedProperties, "no retained clock after unload");
        });
        yield return Case("footer favorite toggles native membership for playing song not highlighted row", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            var song = f.Module.SelectedSong!; await f.Module.PlaySelectionAsync();
            f.Module.SelectedSong = f.Module.Songs.First(s => s.Id != song.Id);
            await f.Module.ToggleCurrentFavoriteAsync();
            True(f.Module.IsCurrentFavorite, "saved membership drives filled heart");
            Equal("取消收藏当前歌曲", f.Module.CurrentFavoriteLabel);
            await f.Module.NavigateAsync("favorites", default);
            Equal(1, f.Module.Songs.Count); Equal(song.Id, f.Module.Songs[0].Id);
            await f.Module.ToggleCurrentFavoriteAsync();
            True(!f.Module.IsCurrentFavorite, "remove updates heart without restarting playback");
            Equal(0, f.Module.Songs.Count);
            Equal(1, f.Playback.Opens.Count);
        });
        yield return Case("footer favorite chrome matches the optional 43 by 32 badge geometry without song data", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection();
            await f.Module.PlaySelectionAsync();
            await f.Module.ToggleCurrentFavoriteAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var button = (Button)footer.FindName("FavoriteButton");
            Equal(32d, button.ActualWidth);
            var badge = (TextBlock)button.Template.FindName("FavoriteBadge", button);
            Equal("", badge.Text);
            badge.Text = "10w+";
            Render(footer, 1110, 80);
            Equal(43d, button.ActualWidth); Equal(32d, button.ActualHeight);
            var surface = (Border)button.Template.FindName("FavoriteSurface", button);
            Equal(new CornerRadius(16), surface.CornerRadius);
            var heart = Visuals(button).OfType<System.Windows.Shapes.Path>().Single();
            Equal(Color.FromRgb(0xFC, 0x3D, 0x49), ((SolidColorBrush)heart.Fill).Color);
            Equal(Color.FromRgb(0xFC, 0x3D, 0x49), ((SolidColorBrush)badge.Foreground).Color);

            var bitmap = new RenderTargetBitmap(1110, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(footer);
            var origin = button.TranslatePoint(new Point(), footer);
            var crop = new CroppedBitmap(bitmap, new Int32Rect(
                (int)Math.Round(origin.X), (int)Math.Round(origin.Y), 43, 32));

            var pixels = new byte[43 * 32 * 4]; crop.CopyPixels(pixels, 43 * 4, 0);
            var points = Enumerable.Range(0, 43 * 32).Where(pixel =>
            {
                var offset = pixel * 4;
                var blue = pixels[offset]; var green = pixels[offset + 1];
                var red = pixels[offset + 2]; var alpha = pixels[offset + 3];
                return alpha > 0 && red > 240 && green < 220 && blue < 225 && red - green > 30;
            }).Select(pixel => new Point(pixel % 43, pixel / 43)).ToArray();
            True(points.Length > 250, "favorite reference paints the heart and badge");
            var bounds = new Rect(new Point(points.Min(point => point.X), points.Min(point => point.Y)),
                new Point(points.Max(point => point.X) + 1, points.Max(point => point.Y) + 1));
            True(Math.Abs(bounds.Left - 4) <= 1 && Math.Abs(bounds.Top - 6) <= 1 &&
                Math.Abs(bounds.Right - 40) <= 1 && Math.Abs(bounds.Bottom - 27) <= 1,
                $"favorite ink bounds match reference: {bounds}");
        });
        yield return Case("transport glyph painted centroid stays centered at 100 150 and 200 percent DPI", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var footer = f.Module.PlayerView;
            Render(footer, 1110, 80);
            var play = (Button)footer.FindName("PlayButton");
            foreach (var playing in new[] { false, true })
            {
                if (playing) f.Playback.Start();
                await Drain(); footer.UpdateLayout();
                foreach (double scale in new[] { 1d, 1.5, 2 })
                {
                    var size = (int)(40 * scale);
                    var full = new RenderTargetBitmap((int)(1110 * scale), (int)(80 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    full.Render(footer);
                    var origin = play.TranslatePoint(new Point(), footer);
                    var bitmap = new CroppedBitmap(full, new Int32Rect((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale), size, size));

                    var bytes = new byte[size * size * 4]; bitmap.CopyPixels(bytes, size * 4, 0);
                    double xSum = 0, ySum = 0, count = 0;
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            int i = (y * size + x) * 4;
                            // Only the glyph inside the disc; a cropped footer also includes
                            // white page corners outside the circular red button.
                            double dx = (x + .5) / scale - 20, dy = (y + .5) / scale - 20;
                            if (dx * dx + dy * dy >= 225) continue;
                            if (bytes[i] < 240 || bytes[i + 1] < 240 || bytes[i + 2] < 240 || bytes[i + 3] < 240) continue;
                            xSum += (x + .5) / scale; ySum += (y + .5) / scale; count++;
                        }
                    True(count > 50, "glyph actually paints white pixels");
                    True(Math.Abs(xSum / count - 20) < .6 && Math.Abs(ySum / count - 20) < .6,
                        $"painted {(playing ? "pause" : "play")} centroid at {scale}: {xSum / count},{ySum / count}");
                }
            }
        });
        yield return Case("independent player footer shares playback and retires gestures and popups on exit", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var ordinary = (MusicPlayerView)f.Module.PlayerView;
            ordinary.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Drain();
            var ordinaryProgress = (SeekBar)ordinary.FindName("Progress");
            ordinaryProgress.BeginScrub(); ordinaryProgress.UpdateScrub(70);
            var opens = f.Playback.Opens.Count;
            var subscribers = f.Playback.Subscribers;
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            True(!ordinaryProgress.IsScrubbing, "covered ordinary footer retires its gesture without seeking");
            Equal(0d, f.Playback.Seek); Equal(opens, f.Playback.Opens.Count);
            var page = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            var footer = (MusicPlayerView)((ContentControl)page.FindName("DetailPlayerHost")).Content;
            True(!ReferenceEquals(ordinary, footer), "player page owns its footer visual");
            True(ReferenceEquals(f.Module, footer.DataContext) && ReferenceEquals(ordinary.DataContext, footer.DataContext),
                "both visual controls consume the sole playback owner");
            using var source = new HwndSource(new HwndSourceParameters("player-page-controls-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 1110, Height = 800
            });
            source.RootVisual = page;
            Render(page, 1110, 800); await Drain();
            Equal(subscribers, f.Playback.Subscribers);
            f.Playback.Start(); await Drain();
            var play = (Button)footer.FindName("PlayButton");
            Click(play); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Pauses);
            Click(play); await Until(() => !f.Module.Busy);
            Equal(1, f.Playback.Resumes);
            var next = f.Calls.Count("music.queue.next");
            Click((Button)footer.FindName("NextButton")); await Until(() => !f.Module.Busy);
            Equal(next + 1, f.Calls.Count("music.queue.next")); Equal(opens + 1, f.Playback.Opens.Count);
            var progress = (SeekBar)footer.FindName("Progress");
            progress.BeginScrub(); progress.UpdateScrub(36); progress.EndScrub(true);
            await Until(() => !f.Module.Busy); Equal(36d, f.Playback.Seek);
            var requests = 0;
            f.Module.GlobalSurfaceRequested += (_, args) =>
            { if (args.Surface == DesktopGlobalSurface.PlayQueue) requests++; };
            Click(Visuals(footer).OfType<Button>().Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "播放队列"));
            Equal(1, requests);
            Click((Button)footer.FindName("RateButton"));
            var rate = (System.Windows.Controls.Primitives.Popup)footer.FindName("RatePopup");
            True(rate.IsOpen, "page footer popup opens");
            progress.BeginScrub(); progress.UpdateScrub(90);
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            True(!rate.IsOpen && !progress.IsScrubbing, "page exit closes its popup and cancels its own gesture");
            Equal(36d, f.Playback.Seek); Equal(opens + 1, f.Playback.Opens.Count);
            Equal(subscribers, f.Playback.Subscribers);
            source.RootVisual = null;
            ordinary.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
        yield return Case("complete player page preserves the underlying shell layout and lyrics at narrow bounds", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var shell = new ShellWindow([f.Module], () => Task.CompletedTask);
            try
            {
                shell.PrepareForRender();
                var root = (Border)shell.Content; shell.Content = null;
                root.Resources.MergedDictionaries.Add(shell.Resources);
                // The render fixture detaches WindowFrame from its Window. Keep
                // the production live backdrop binding instead of a stale brush snapshot.
                root.SetBinding(Border.BackgroundProperty, new Binding(nameof(Window.Background)) { Source = shell });
                root.DataContext = shell.DataContext;
                TextElement.SetFontFamily(root, shell.FontFamily); TextElement.SetFontSize(root, shell.FontSize);
                // Hidden native test presentation source admits real Loaded/visibility-dependent artwork.
                using var surface = new HwndSource(new HwndSourceParameters("player-chrome-code-fixture")
                {
                    WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                    PositionX = -32000, PositionY = -32000, Width = 1243, Height = 848
                });
                surface.RootVisual = root;
                root.Measure(new(1243, 848)); root.Arrange(new Rect(0, 0, 1243, 848)); root.UpdateLayout();
                var ordinaryFooter = f.Module.PlayerView;
                var ordinaryFooterHeight = ordinaryFooter.ActualHeight;
                await f.Module.ShowLyricsAsync();
                await Until(() => f.Module.IsLyrics);
                var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                var footer = (MusicPlayerView)((ContentControl)detail.FindName("DetailPlayerHost")).Content;
                True(!ReferenceEquals(ordinaryFooter, footer), "complete player page has its own footer");
                var record = (MusicRecordView)detail.FindName("DetailRecord");
                var cover = (ArtworkImage)record.FindName("Cover");
                ArtworkScope.SetLoader(record, new PlayerArtworkFixture());
                record.Address = "fixture://cover";
                f.Module.Lyrics.Clear();
                for (var i = 0; i < 16; i++) f.Module.Lyrics.Add(new(i * 5, "原生歌词排版测试 " + i, i == 2 ? "Translation test" : ""));
                var opens = f.Playback.Opens.Count;
                foreach (var size in new[] { new Size(1243, 848), new Size(860, 600) })
                {
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    cover.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await Drain(); root.UpdateLayout();
                    var grid = (Grid)shell.FindName("RootLayout");
                    True(grid.Background is null, "player overlay does not repaint the shell");
                    Equal(Visibility.Visible, ((ScrollViewer)shell.FindName("SidebarScroll")).Visibility);
                    True(Math.Abs(grid.ColumnDefinitions[0].ActualWidth - 203) < .5, "underlying sidebar keeps its layout");
                    Equal(new Thickness(0), root.BorderThickness);
                    True(ReferenceEquals(root.Background, shell.Background), "rounded edges do not expose normal white canvas");
                    var title = Visuals(detail).OfType<TextBlock>()
                        .First(block => block.Text == f.Module.CurrentTitle);
                    var lyrics = (ListBox)detail.FindName("LyricList");
                    True(title.TranslatePoint(new Point(), root).X >= size.Width * .5, "title on right");
                    True(record.ActualWidth > 200 && record.ActualHeight > 200, "full disc not a thumbnail");
                    True(lyrics.ActualHeight >= 160, "usable lyric viewport at minimum window");
                    True(lyrics.TranslatePoint(new Point(lyrics.ActualWidth, 0), root).X < size.Width, "lyric column fits");
                    Equal(size, detail.RenderSize);
                    Equal(ordinaryFooterHeight, ordinaryFooter.ActualHeight);
                    True(ReferenceEquals(ordinaryFooter, ((ContentControl)shell.FindName("PlayerHost")).Content),
                        "ordinary footer is not moved into the player page");
                    True(Math.Abs(footer.TranslatePoint(new Point(0, footer.ActualHeight), root).Y - size.Height) < .5,
                        "page footer reaches the bottom of the whole window");
                    var play = (Button)footer.FindName("PlayButton");
                    True(Math.Abs(play.TranslatePoint(new Point(20, 0), root).X - size.Width / 2) < 1, "play remains window-centered in detail");
                    True(root.InputHitTest(play.TranslatePoint(new Point(20, 20), root)) is DependencyObject hit &&
                        Visuals(play).Contains(hit), "window chrome does not intercept the page footer");
                    Render(root, (int)size.Width, (int)size.Height);
                    DesktopTheme.SetIsHighContrast(grid, false); root.UpdateLayout();
                    Equal(Color.FromRgb(252, 59, 90), ((SolidColorBrush)play.Background).Color);
                    True(f.Module.IsLyrics, "page footer consumes the existing active player state");
                    DesktopTheme.SetIsHighContrast(grid, true); root.UpdateLayout();
                }
                True(record.PaletteBrush != Brushes.DimGray, "cover is loaded through public loader and sampled");
                Equal(opens, f.Playback.Opens.Count);
                var lyricList = (ListBox)detail.FindName("LyricList");
                var lyricScroll = Visuals(lyricList).OfType<ScrollViewer>().First();
                await f.Playback.SeekAsync(40); await Task.Delay(550); await Drain();
                var centered = lyricScroll.VerticalOffset;
                True(centered > 100, "actual playback position smoothly centers a later lyric");
                var row = lyricList.ItemContainerGenerator.ContainerFromIndex(8) as FrameworkElement;
                if (row is null) throw new InvalidOperationException("active lyric row is not realized after automatic centering");
                var rowCenter = row.TranslatePoint(new Point(0, row.ActualHeight / 2), lyricScroll).Y;
                var readingBand = lyricScroll.ViewportHeight * .35;
                True(Math.Abs(rowCenter - readingBand) < 2,
                    $"active line reaches reference reading band: actual={rowCenter}, target={readingBand}, offset={centered}, " +
                    $"scrollable={lyricScroll.ScrollableHeight}, extent={lyricScroll.ExtentHeight}, viewport={lyricScroll.ViewportHeight}, row={row.ActualHeight}");
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                lyricList.RaiseEvent(wheel); await Task.Delay(250); await Drain();
                True(wheel.Handled && lyricScroll.VerticalOffset > centered, "wheel over actual lyrics scrolls");
                await f.Playback.SeekAsync(45); await Task.Delay(550); await Drain();
                True(Math.Abs(lyricScroll.VerticalOffset - centered) > 5, "manual scrolling is not immediately overwritten");
                // No new playback tick after this point: paused lyrics must still recover.
                // Synchronize with the timer plus animation settlement, not the
                // SelectedIndex bring-into-view behavior suppressed during manual scrolling.
                FrameworkElement? recoveredRow = null;
                await Until(() =>
                {
                    recoveredRow = lyricList.ItemContainerGenerator.ContainerFromIndex(9) as FrameworkElement;
                    return recoveredRow is not null &&
                        Math.Abs(recoveredRow.TranslatePoint(new Point(0, recoveredRow.ActualHeight / 2), lyricScroll).Y -
                            lyricScroll.ViewportHeight * .35) < 2;
                });
                row = recoveredRow ?? throw new InvalidOperationException(
                    "active lyric row is not realized after manual-scroll recovery");
                True(Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), lyricScroll).Y - lyricScroll.ViewportHeight * .35) < 2,
                    "idle timeout resumes lyric following without a new media tick");
                f.Module.PreviewLyric(f.Module.Lyrics[2]); await Drain();
                True(f.Module.HasLyricPreview, "preview remains available"); f.Module.CancelLyricPreview();
                lyricList.ScrollIntoView(f.Module.Lyrics[2]); lyricList.UpdateLayout();
                var previewRow = (FrameworkElement)lyricList.ItemContainerGenerator.ContainerFromIndex(2);
                var playLine = Visuals(previewRow).OfType<Button>().Single(b =>
                    System.Windows.Automation.AutomationProperties.GetName(b) == "播放此处" && ReferenceEquals(b.Tag, f.Module.Lyrics[2]));
                playLine.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                Equal(10d, f.Playback.PositionSeconds);
                True(f.Playback.IsPlaying, "inline lyric control seeks and resumes real playback port");
                Equal(opens, f.Playback.Opens.Count);
            }
            finally { shell.Close(); await shell.ShutdownCompletion; }
        });
    }
    // Explicit deterministic artwork fixture, never packaged into the product or fetched from a platform.
    private sealed class PlayerArtworkFixture : IArtworkLoader
    {
        public Task<IArtworkLease> AcquireAsync(string address, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Colors.SteelBlue, Colors.DarkSalmon, 45), null, new Rect(0, 0, 256, 256));
                dc.DrawEllipse(Brushes.LightGoldenrodYellow, null, new(168, 86), 38, 38);
                dc.DrawGeometry(Brushes.DarkSlateBlue, null, Geometry.Parse("M0,256 L0,200 86,105 175,210 256,135 256,256 Z"));
            }
            var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            return Task.FromResult<IArtworkLease>(new PlayerArtworkLease(bitmap));
        }
    }
    private sealed class PlayerArtworkLease(BitmapSource image) : IArtworkLease
    {
        public BitmapSource Image => image;
        public void Dispose() { }
    }
}
