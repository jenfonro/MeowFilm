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
using MeowFilm.Desktop;
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
        var window = new DesktopWindow { Content = view, Width = 1200, Height = 850,
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
        var view = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
        var window = LyricWindow(view);
        try
        {
            window.Show();
            var list = (ListBox)view.FindName("LyricList");
            var viewport = (FrameworkElement)view.FindName("LyricViewport");
            var fade = (Grid)view.FindName("LyricFadeViewport");
            var edge = (SidebarBackdrop)view.FindName("LyricEdgeFade");
            var backdrop = (Border)view.FindName("PageBackdrop");
            var scroll = LyricVisuals(list).OfType<ScrollViewer>().First();
            Check(fade.OpacityMask is null,
                "the lyric text layer is not composited through a whole-viewport opacity mask");
            var text = LyricVisuals(list).OfType<TextBlock>().ToArray();
            Check(text.Length > 0 && text.All(block =>
                TextOptions.GetTextRenderingMode(block) == TextRenderingMode.Auto),
                "detail lyric glyphs use native automatic rendering");
            Check(ReferenceEquals(edge.Source, backdrop), "the edge fade projects the page's own cover backdrop");
            Check(ReferenceEquals(backdrop.Background, view.Background),
                "the projected backdrop is the same PlayerBackground brush as the page");
            var edgeMask = (LinearGradientBrush)edge.OpacityMask;
            Check(edgeMask.GradientStops.Select(stop => (stop.Color, stop.Offset)).SequenceEqual(
                new[] { (Colors.Black, 0d), (Colors.Transparent, .10d),
                    (Colors.Transparent, .90d), (Colors.Black, 1d) }),
                "only the upper and lower edges fade; the central eighty percent stays clear");
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
                var visible = CaptureLyricFrame(fade);
                var edgeOpacity = edge.Opacity;
                RenderTargetBitmap clear;
                try { edge.Opacity = 0; clear = CaptureLyricFrame(fade); }
                finally { edge.Opacity = edgeOpacity; }
                var central = new Int32Rect(0, (int)(visible.PixelHeight * .15), visible.PixelWidth,
                    (int)(visible.PixelHeight * .7));
                long withEdge = LyricAlpha(visible, central), withoutEdge = LyricAlpha(clear, central);
                Check(withoutEdge > 1000 && Math.Abs(withEdge - withoutEdge) <= withoutEdge * .005,
                    "the edge backdrop does not alter central lyric text");
                var row = list.ItemContainerGenerator.ContainerFromIndex(f.Module.LyricDisplayIndex) as ListBoxItem;
                Check(row is not null && row.Opacity == 1,
                    "active lyric rows are not weakened through whole-row opacity compositing");
                var edgeFrame = CaptureLyricFrame(edge);
                byte Alpha(double y)
                {
                    var pixel = new byte[4];
                    edgeFrame.CopyPixels(new Int32Rect(edgeFrame.PixelWidth / 2,
                        (int)(edgeFrame.PixelHeight * y), 1, 1), pixel, 4, 0);
                    return pixel[3];
                }
                Check(Alpha(.05) is > 90 and < 170 && Alpha(.95) is > 90 and < 170,
                    "the upper and lower edge fades remain visible");
                foreach (double y in new[] { .15, .3, .5, .7, .85 }) Equal((byte)0, Alpha(y));
                Equal(0, f.Playback.Seeks);
                if (size.Width == 1298) SaveLyricFrame(view, $"player-lyrics-{key}.png");
            }
        }
        finally { window.Close(); f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail); }
    }

    private static async Task VerifyLyricRendering()
    {
        await using var f = await Fixture.Create();
        var entered = Signal();
        var release = Signal();
        f.Peer.Block = () => { entered.TrySetResult(); return release.Task; };
        var activation = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
        var view = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
        var window = LyricWindow(view);
        void Typography(FrameworkElement text, double size)
        {
            Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(text));
            Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(text));
            Equal(SystemFonts.MessageFontFamily, (FontFamily)text.GetValue(TextBlock.FontFamilyProperty));
            Equal(FontWeights.Normal, (FontWeight)text.GetValue(TextBlock.FontWeightProperty));
            Equal(size, (double)text.GetValue(TextBlock.FontSizeProperty));
        }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Show(); window.UpdateLayout();
            var state = (TextBlock)view.FindName("LyricState");
            Equal("加载中…", state.Text);
            Typography(state, 20);
            Check(state.IsVisible, "the real loading text is mounted");
            release.TrySetResult();
            await activation;
            var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
            await f.Playback.OpenAsync("music", queue.GetProperty("currentId").GetString()!,
                new Uri("https://synthetic.invalid/unused"), "", default);
            f.Module.Lyrics[0] = new(1, "And disappear inside", "然后隐去", "ran hou yin qu");
            f.Module.Lyrics[1] = new(20, "逐字歌词", "逐字译文", "zhu zi ge ci",
                [new("逐字", 20, 2), new("歌词", 22, 2)]);
            var list = (ListBox)view.FindName("LyricList");
            foreach (var position in new[] { 0d, 21d, 23d, 1d })
            {
                f.Playback.Position = position;
                int selected = position < 1 ? -1 : position < 20 ? 0 : 1;
                await WaitForLyric(() => list.SelectedIndex == selected &&
                    list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem &&
                    LyricVisuals(list).OfType<MusicLyricWordView>().Count() == 2);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Render);
                var plain = LyricVisuals(list).OfType<TextBlock>()
                    .Single(text => text.Text == "And disappear inside");
                Check(plain.IsVisible, "ordinary lyrics retain their native text control");
                Typography(plain, 20);
                Equal(selected == 0 ? Color.FromRgb(235, 234, 237) : Color.FromRgb(139, 137, 140),
                    ((SolidColorBrush)plain.Foreground).Color);
                foreach (var content in new[] { "然后隐去", "ran hou yin qu", "逐字译文", "zhu zi ge ci" })
                {
                    var optional = LyricVisuals(list).OfType<TextBlock>().Single(text => text.Text == content);
                    Check(optional.IsVisible, "translation and romanization are actually presented");
                    Typography(optional, 14);
                }
                var words = LyricVisuals(list).OfType<MusicLyricWordView>().ToArray();
                foreach (var word in words)
                {
                    Typography(word, 20);
                    Check(word.Stroke is null, "full player words do not acquire desktop outlines");
                    Equal(selected == 1 ? Color.FromRgb(244, 243, 246) : Color.FromRgb(146, 143, 144),
                        ((SolidColorBrush)word.Highlight).Color);
                    Equal(Math.Clamp((position - word.Word!.Time) / word.Word.Duration, 0, 1), word.WordProgress);
                }
            }
            await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
            window.UpdateLayout();
            Check(LyricVisuals(list).OfType<TextBlock>().Where(text => text.Name == "LyricTranslation")
                .All(text => text.Visibility == Visibility.Collapsed), "the normal translation toggle still hides translations");
            await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
            DesktopTheme.SetIsHighContrast(view, true);
            window.UpdateLayout();
            foreach (var text in LyricVisuals(list).OfType<TextBlock>())
                Typography(text, text.Name == "LyricTranslation" || text.Text is "ran hou yin qu" or "zhu zi ge ci" ? 14 : 20);
            foreach (var word in LyricVisuals(list).OfType<MusicLyricWordView>()) Typography(word, 20);
            var title = (TextBlock)view.FindName("DetailTitle");
            Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(title));
            var footer = (MusicPlayerView)((ContentControl)view.FindName("DetailPlayerHost")).Content;
            var footerText = LyricVisuals(footer).OfType<TextBlock>().ToArray();
            Check(footerText.Length > 0 && footerText.All(text =>
                TextOptions.GetTextRenderingMode(text) == TextRenderingMode.Auto), "the footer keeps native automatic rendering");
            var delay = (Button)view.FindName("DelayLyricButton");
            Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(LyricVisuals(delay).OfType<TextBlock>().Single()));
            f.Peer.Block = null;
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            window.UpdateLayout();
            Equal(0, f.Module.Lyrics.Count);
            Equal("暂无歌词", state.Text);
            Typography(state, 20);
            Equal(0, f.Playback.Seeks);
        }
        finally
        {
            release.TrySetResult();
            window.Close();
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            await activation;
        }
    }

    private static Window LyricWindow(FrameworkElement view)
    {
        // An unclipped canvas keeps component layout independent of the host
        // display's native window size.
        view.Width = 1298; view.Height = 848;
        var host = new Canvas();
        host.Children.Add(view);
        return new DesktopWindow()
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
        await using var documents = new NativeTestDatabase(disk);
        var cache = new MusicPresentationCache(documents);
        string[] songs = [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")];
        const string image = "https://images.example.test/shared.png?size=original";
        byte[] original = [1, 2, 3, 4]; // Cache stores opaque original bytes; loader tests cover successful decoding.
        foreach (string song in songs)
        {
            var raw = LJ.Obj(("songId", song), ("duration", 100));
            string key = MusicPresentationCache.SongKey(raw);
            await cache.SaveLyricsAsync(key, "source:" + song, new JsonArray(LJ.Obj(("text", song))), cache.LyricEpoch, default);
            await cache.WriteAsync(image, original, cache.Epoch, default);
            raw["sources"] = new JsonArray(LJ.Obj(("sourceKey", "supplemented-recording")));
            Equal(key, MusicPresentationCache.SongKey(raw));
        }
        await documents.DrainAsync();
        await using var restarted = new NativeTestDatabase(disk);
        var cold = new MusicPresentationCache(restarted);
        await cold.LoadAsync(default);
        foreach (string song in songs.Reverse())
        {
            var saved = cold.ReadLyricsAsync(song, default).GetAwaiter().GetResult();
            Equal(song, saved!["lines"]![0]!["text"]!.GetValue<string>());
        }
        Check((await cold.ReadAsync(image, default))!.Content.SequenceEqual(original));
        var entries = await restarted.ReadPresentationEntriesAsync();
        Equal(3, entries.Count); // Two song-referenced lyrics and one shared image, not per-song images.
        await cold.ClearImagesAsync(default);
        Check(await cold.ReadAsync(image, default) is null && songs.All(song => cold.ReadLyricsAsync(song, default).GetAwaiter().GetResult() is not null));
        await cold.ClearAllAsync(default);
        Check(songs.All(song => cold.ReadLyricsAsync(song, default).GetAwaiter().GetResult() is null));
    }
}
