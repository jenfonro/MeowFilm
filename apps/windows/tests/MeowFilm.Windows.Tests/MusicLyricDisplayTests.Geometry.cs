using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicLyricDisplayTests
{
    private static IEnumerable<DependencyObject> LyricVisuals(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var item in LyricVisuals(VisualTreeHelper.GetChild(parent, i))) yield return item;
    }
    private static async Task VerifyCenteredLyrics()
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateGlobalSurfaceAsync(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
        var view = new MusicPlayerDetailView(f.Module);
        var window = new Window { Content = view, Width = 1200, Height = 850,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var list = (ListBox)view.FindName("LyricList");
            var viewport = (FrameworkElement)view.FindName("LyricViewport");
            var frame = (Border)view.FindName("LyricPreviewFrame");
            ScrollViewer Scroll() => LyricVisuals(list).OfType<ScrollViewer>().First();
            double Gap(int index) => list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row
                ? Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), Scroll()).Y - Scroll().ViewportHeight / 2)
                : double.PositiveInfinity;
            async Task Settle(Func<bool> done)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                while (!done())
                {
                    if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Lyric alignment did not settle");
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    await Task.Delay(15);
                }
            }
            await Settle(() => Gap(0) < 1);
            Check(f.Module.ActiveLyricIndex < 0 && list.SelectedIndex < 0, "prelude centers without singing early");
            Check(Scroll().VerticalOffset < 1, "first row fits at the very top of the scrollable range");
            window.Width = 1000; window.Height = 740; window.UpdateLayout();
            await Settle(() => Gap(0) < 1);
            viewport.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.End)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(f.Module.HasLyricPreview && frame.Visibility == Visibility.Visible, "the rounded frame appears on first input");
            await Settle(() => Gap(f.Module.Lyrics.Count - 1) < 1);
            Check(frame.CornerRadius.TopLeft > 0 && frame.BorderThickness.Top > 0 && frame.BorderThickness.Left > 0);
            var selected = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(f.Module.LyricDisplayIndex);
            Check(Math.Abs(frame.ActualHeight - selected.ActualHeight) < 1, "frame contains the original row and translation");
            Equal(0, f.Playback.Seeks);
            window.Visibility = Visibility.Hidden;
            Check(!f.Module.HasLyricPreview, "hiding cannot leave a preview with no resume timer");
        }
        finally { window.Close(); f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail); }
    }

    private static async Task VerifyDetailLayout()
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateGlobalSurfaceAsync(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
        var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
        await f.Playback.OpenAsync("music", queue.GetProperty("currentId").GetString()!,
            new Uri("https://synthetic.invalid/unused"), "", default);
        f.Module.Lyrics.Clear();
        f.Module.Lyrics.Add(new(2, "You spread your wings and rise from the ashes",
            "你展开双翼 从灰烬中重生"));
        for (int i = 1; i < 20; i++) f.Module.Lyrics.Add(new(i * 5, $"歌词行 {i} · Keep moving forward", "继续向前"));
        var view = new MusicPlayerDetailView(f.Module);
        var window = LyricWindow(view);
        try
        {
            window.Show();
            var list = (ListBox)view.FindName("LyricList");
            var viewport = (FrameworkElement)view.FindName("LyricViewport");
            var title = (TextBlock)view.FindName("DetailTitle");
            var action = (Button)view.FindName("ConfirmLyric");
            var frame = (Border)view.FindName("LyricPreviewFrame");
            var record = (FrameworkElement)view.FindName("DetailRecord");
            var rail = (FrameworkElement)view.FindName("LyricTimingRail");
            var scroll = LyricVisuals(list).OfType<ScrollViewer>().First();
            foreach (var size in new[] { new Size(1000, 740), new Size(1298, 848), new Size(1440, 900) })
            {
                f.Module.CancelLyricPreview();
                window.Width = view.Width = size.Width; window.Height = view.Height = size.Height; window.UpdateLayout();
                Equal(size.Width, view.ActualWidth); Equal(size.Height, view.ActualHeight);
                await WaitForLyric(() => CenterGap(list, scroll, 0) < 1);
                var row = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0);
                var text = LyricVisuals(row).OfType<TextBlock>().Single(t => t.Text == f.Module.Lyrics[0].Text);
                double before = text.TranslatePoint(new Point(), view).X;
                Check(Math.Abs(before - title.TranslatePoint(new Point(), view).X) < 1,
                    "title and lyric text share one left edge");
                var disc = record.TransformToAncestor(view).TransformBounds(new Rect(record.RenderSize));
                Check(disc.Width <= 390 && disc.Left >= 0 && disc.Right < title.TranslatePoint(new Point(), view).X,
                    "the smaller record stays inside its own column");
                if (size.Width >= 1298) Check(disc.Width >= 360, "desktop record retains the reference scale");
                Check(title.TranslatePoint(new Point(), view).Y is >= 100 and <= 115,
                    "the title leaves the reference's top breathing room");
                var lyrics = viewport.TransformToAncestor(view).TransformBounds(new Rect(viewport.RenderSize));
                var timing = rail.TransformToAncestor(view).TransformBounds(new Rect(rail.RenderSize));
                Check(timing.Left >= lyrics.Right + 10 && timing.Right <= view.ActualWidth &&
                    timing.Top >= lyrics.Top && timing.Bottom <= lyrics.Bottom,
                    "the timing controls remain beside the lyric viewport without clipping or covering its text");
                BrowseLyricKey(viewport, window, Key.Home);
                window.UpdateLayout();
                await WaitForLyric(() => frame.IsVisible && action.ActualHeight > 0 && CenterGap(list, scroll, 0) < 1);
                Check(Math.Abs(before - text.TranslatePoint(new Point(), view).X) < 1,
                    "showing the capsule does not move the original lyric or translation");
                var capsule = action.TransformToAncestor(view).TransformBounds(new Rect(action.RenderSize));
                Check(capsule.Height is >= 20 and <= 26 && capsule.Width < 76 &&
                    capsule.Right + 3 < before, "one small capsule fits beside, not over, the lyric");
                var pieces = LyricVisuals(action).OfType<TextBlock>().ToArray();
                Equal(2, pieces.Length); Equal("00:02", pieces[1].Text);
                Check(pieces[0].TranslatePoint(new Point(), action).X < pieces[1].TranslatePoint(new Point(), action).X,
                    "play icon precedes the time within one button");
                Equal(1, LyricVisuals(frame).OfType<Button>().Count());
                Check(LyricVisuals(action).OfType<Border>().First().CornerRadius.TopLeft >= capsule.Height / 2,
                    "the action has fully rounded ends");
                Equal(0, f.Playback.Seeks);
                SaveLyricFrame(view, $"player-detail-{size.Width:0}x{size.Height:0}.png");
            }
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForLyric(() => !f.Module.HasLyricPreview);
            Equal(1, f.Playback.Seeks); Equal(1, f.Playback.Resumes);
            Equal(2d, f.Playback.PositionSeconds); Equal(1, f.Playback.Opens);
        }
        finally { window.Close(); f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail); }
    }

    private static async Task VerifyViewportFade()
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateGlobalSurfaceAsync(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
        f.Module.Lyrics.Clear();
        for (int i = 0; i < 40; i++)
            f.Module.Lyrics.Add(new(i * 2, $"第 {i} 句 · You spread your wings and rise from the ashes",
                "你展开双翼 从灰烬中重生 · Translation remains fully visible"));
        var view = new MusicPlayerDetailView(f.Module);
        var window = LyricWindow(view);
        try
        {
            window.Show();
            var list = (ListBox)view.FindName("LyricList");
            var viewport = (FrameworkElement)view.FindName("LyricViewport");
            var fade = (Grid)view.FindName("LyricFadeViewport");
            var scroll = LyricVisuals(list).OfType<ScrollViewer>().First();
            foreach (var size in new[] { new Size(1298, 848), new Size(1000, 740) })
            foreach (var key in new[] { Key.Home, Key.End, Key.PageUp })
            {
                window.Width = view.Width = size.Width; window.Height = view.Height = size.Height; window.UpdateLayout();
                Equal(size.Width, view.ActualWidth); Equal(size.Height, view.ActualHeight);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await WaitForLyric(() => CenterGap(list, scroll, Math.Max(0, f.Module.LyricDisplayIndex)) < 1);
                int previous = f.Module.LyricDisplayIndex;
                BrowseLyricKey(viewport, window, key);
                await WaitForLyric(() => f.Module.HasLyricPreview && (key switch
                {
                    Key.Home => f.Module.LyricDisplayIndex == 0,
                    Key.End => f.Module.LyricDisplayIndex == f.Module.Lyrics.Count - 1,
                    _ => f.Module.LyricDisplayIndex >= 0 && f.Module.LyricDisplayIndex < previous
                }) && CenterGap(list, scroll, f.Module.LyricDisplayIndex) < 1);
                await Dispatcher.Yield(DispatcherPriority.Render);
                Console.WriteLine($"LYRIC {size} {key}: row={f.Module.LyricDisplayIndex}, centerGap={CenterGap(list, scroll, f.Module.LyricDisplayIndex):F2}");
                var mask = fade.OpacityMask;
                var masked = CaptureLyricFrame(fade);
                RenderTargetBitmap clear;
                try { fade.OpacityMask = null; clear = CaptureLyricFrame(fade); }
                finally { fade.OpacityMask = mask; }
                var central = new Int32Rect(0, (int)(masked.PixelHeight * .15), masked.PixelWidth,
                    (int)(masked.PixelHeight * .7));
                long original = LyricAlpha(clear, central), visible = LyricAlpha(masked, central);
                Check(original > 1000 && Math.Abs(visible - original) <= original * .005,
                    "actual first/last multiline lyric ink is not faded in the viewport middle");
                // A uniform probe makes the edge opacity observable even when a gap
                // between real lyric lines happens to land at the sample coordinate.
                var probe = new Border { Background = Brushes.White, IsHitTestVisible = false };
                fade.Children.Add(probe); fade.UpdateLayout();
                try
                {
                    var image = CaptureLyricFrame(fade);
                    byte Alpha(double y)
                    {
                        var pixel = new byte[4];
                        image.CopyPixels(new Int32Rect(image.PixelWidth / 2, (int)(image.PixelHeight * y), 1, 1), pixel, 4, 0);
                        return pixel[3];
                    }
                    Check(Alpha(.05) is > 90 and < 170 && Alpha(.95) is > 90 and < 170,
                        "only viewport edges fade, independent of the scrolled song boundary");
                    foreach (double y in new[] { .15, .3, .5, .7, .85 }) Equal((byte)255, Alpha(y));
                }
                finally { fade.Children.Remove(probe); }
                Equal(0, f.Playback.Seeks);
                if (size.Width == 1298) SaveLyricFrame(view, $"player-lyrics-{key}.png");
            }
        }
        finally { window.Close(); f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail); }
    }

    private static Window LyricWindow(FrameworkElement view)
    {
        // Session 0 limits native windows to its small monitor; an unclipped canvas
        // lets the real component still lay out at each requested desktop size.
        view.Width = 1298; view.Height = 848;
        var host = new Canvas();
        host.Children.Add(view);
        return new()
        {
            Content = host, Width = 1298, Height = 848, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false
        };
    }
    private static void BrowseLyricKey(FrameworkElement viewport, Window window, Key key) =>
        viewport.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static double CenterGap(ListBox list, ScrollViewer scroll, int index) =>
        list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row
            ? Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y - scroll.ViewportHeight / 2)
            : double.PositiveInfinity;
    private static async Task WaitForLyric(Func<bool> done)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Lyric presentation did not settle");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            await Task.Delay(15);
        }
    }
    private static RenderTargetBitmap CaptureLyricFrame(FrameworkElement element)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(element); return image;
    }
    private static long LyricAlpha(BitmapSource image, Int32Rect area)
    {
        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        image.CopyPixels(area, pixels, stride, 0);
        long sum = 0;
        for (int i = 3; i < pixels.Length; i += 4) sum += pixels[i];
        return sum;
    }
    private static void SaveLyricFrame(FrameworkElement element, string name)
    {
        string path = TestWorkspace.ValidatePath(TestWorkspace.ResultsRoot,
            Path.Combine(TestWorkspace.ResultsRoot, "windows-native", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(CaptureLyricFrame(element)));
        using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine("VISUAL " + path);
    }

    private static async Task VerifyMultiplePresentations()
    {
        await using var disk = new MusicOfficialAudioTests.MemoryStore();
        await using var documents = new CacheDocumentStore(disk);
        var cache = new MusicRecordingPresentationCache(documents);
        foreach (string song in new[] { "A", "B" })
        {
            var raw = LJ.Obj(("songId", song), ("duration", 100));
            string key = MusicRecordingPresentationCache.Key(raw);
            await cache.PatchAsync(key, song, row => row["lines"] = JsonNode.Parse("[{\"text\":\"" + song + "\"}]")!, default);
            await cache.PatchAsync(key, song, row => row["artwork"] = "synthetic-" + song, default);
            raw["sources"] = new JsonArray(LJ.Obj(("sourceKey", "supplemented-recording")));
            Equal(key, MusicRecordingPresentationCache.Key(raw));
        }
        await documents.DrainAsync();
        await using var restarted = new CacheDocumentStore(disk);
        var cold = new MusicRecordingPresentationCache(restarted);
        foreach (string song in new[] { "B", "A" })
        {
            string key = MusicRecordingPresentationCache.Key(LJ.Obj(("songId", song), ("duration", 100)));
            var saved = await cold.ReadAsync(key, default);
            Equal(song, saved!["lines"]![0]!["text"]!.GetValue<string>());
            Equal("synthetic-" + song, saved["artwork"]!.GetValue<string>());
        }
        await cold.RemoveAsync(new HashSet<string> { "A" }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), default);
        string a = MusicRecordingPresentationCache.Key(LJ.Obj(("songId", "A"), ("duration", 100)));
        string b = MusicRecordingPresentationCache.Key(LJ.Obj(("songId", "B"), ("duration", 100)));
        Check(await cold.ReadAsync(a, default) is null && await cold.ReadAsync(b, default) is not null);
    }
}
