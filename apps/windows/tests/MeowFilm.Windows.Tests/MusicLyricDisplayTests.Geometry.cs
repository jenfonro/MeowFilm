using Colors = Microsoft.UI.Colors;
using FontWeights = Microsoft.UI.Text.FontWeights;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using global::Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;

using Microsoft.UI.Xaml.Media;


using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop;

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
        var window = LyricWindow(view);
        try
        {
            window.Activate(); await MusicTestUi.Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
            var list = (MusicLyricList)view.FindName("LyricList");
            var viewport = (FrameworkElement)view.FindName("LyricViewport");
            var frame = (Border)view.FindName("LyricPreviewFrame");
            ScrollViewer Scroll() => LyricVisuals(list).OfType<ScrollViewer>().First();
            double Gap(int index) => list.ContainerFromIndex(index) is FrameworkElement row
                ? Math.Abs(row.TransformToVisual(Scroll()).TransformPoint(new Point(0, row.ActualHeight / 2)).Y - Scroll().ViewportHeight / 2)
                : double.PositiveInfinity;
            async Task Settle(Func<bool> done)
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                while (!done())
                {
                    if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Lyric alignment did not settle");
                    await MusicTestUi.Drain();
                    await Task.Delay(15);
                }
            }
            await Settle(() => Gap(0) < 1);
            Check(f.Module.ActiveLyricIndex < 0 && f.Module.LyricDisplayIndex < 0, "prelude centers without singing early");
            Check(Scroll().VerticalOffset < 1, "first row fits at the very top of the scrollable range");
            window.Resize(1000, 740); view.Width = 1000; view.Height = 740; DesktopTestUi.WindowRoot(window).UpdateLayout();
            await Settle(() => Gap(0) < 1);
            Check(view.BrowseLyric(VirtualKey.End), "the End command is accepted by the mounted lyric view");
            Check(f.Module.HasLyricPreview && frame.Visibility == Visibility.Visible, "the rounded frame appears on first input");
            await Settle(() => Gap(f.Module.Lyrics.Count - 1) < 1);
            Check(frame.CornerRadius.TopLeft > 0 && frame.BorderThickness.Top > 0 && frame.BorderThickness.Left > 0);
            var selected = (FrameworkElement)list.ContainerFromIndex(f.Module.LyricDisplayIndex);
            Check(Math.Abs(frame.ActualHeight - selected.ActualHeight) < 1, "frame contains the original row and translation");
            Equal(0, f.Playback.Seeks);
            window.Hide(); await MusicTestUi.Drain();
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
            window.Activate(); await MusicTestUi.Drain();
            var list = (MusicLyricList)view.FindName("LyricList");
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
                window.Resize(size.Width, size.Height); view.Width = size.Width; view.Height = size.Height; DesktopTestUi.WindowRoot(window).UpdateLayout();
                Equal(size.Width, view.ActualWidth); Equal(size.Height, view.ActualHeight);
                await WaitForLyric(() => CenterGap(list, scroll, 0) < 1);
                var row = (FrameworkElement)list.ContainerFromIndex(0);
                var text = LyricVisuals(row).OfType<TextBlock>().Single(t => t.Text == f.Module.Lyrics[0].Text);
                double before = text.TransformToVisual(view).TransformPoint(new Point()).X;
                Check(Math.Abs(before - title.TransformToVisual(view).TransformPoint(new Point()).X) < 1,
                    "title and lyric text share one left edge");
                var disc = DesktopTestUi.Bounds(record, view);
                Check(disc.Width <= 390 && disc.Left >= 0 && disc.Right < title.TransformToVisual(view).TransformPoint(new Point()).X,
                    "the smaller record stays inside its own column");
                if (size.Width >= 1298) Check(disc.Width >= 360, "desktop record retains the reference scale");
                Check(title.TransformToVisual(view).TransformPoint(new Point()).Y is >= 100 and <= 115,
                    "the title leaves the reference's top breathing room");
                var lyrics = DesktopTestUi.Bounds(viewport, view);
                var timing = DesktopTestUi.Bounds(rail, view);
                Check(timing.Left >= lyrics.Right + 10 && timing.Right <= view.ActualWidth &&
                    timing.Top >= lyrics.Top && timing.Bottom <= lyrics.Bottom,
                    "the timing controls remain beside the lyric viewport without clipping or covering its text");
                Check(view.BrowseLyric(VirtualKey.Home), "Home browse command");
                DesktopTestUi.WindowRoot(window).UpdateLayout();
                await WaitForLyric(() => DesktopVisual.IsVisible(frame) && action.ActualHeight > 0 && CenterGap(list, scroll, 0) < 1);
                Check(Math.Abs(before - text.TransformToVisual(view).TransformPoint(new Point()).X) < 1,
                    "showing the capsule does not move the original lyric or translation");
                var capsule = DesktopTestUi.Bounds(action, view);
                Check(capsule.Height is >= 20 and <= 26 && capsule.Width < 76 &&
                    capsule.Right + 3 < before, "one small capsule fits beside, not over, the lyric");
                var icon = LyricVisuals(action).OfType<FontIcon>().Single();
                var time = LyricVisuals(action).OfType<TextBlock>().Single(text => text.Text == "00:02");
                Check(icon.TransformToVisual(action).TransformPoint(new Point()).X < time.TransformToVisual(action).TransformPoint(new Point()).X,
                    "play icon precedes the time within one button");
                Equal(1, LyricVisuals(frame).OfType<Button>().Count());
                Check(LyricVisuals(action).OfType<Border>().First().CornerRadius.TopLeft >= capsule.Height / 2,
                    "the action has fully rounded ends");
                Equal(0, f.Playback.Seeks);
                await SaveLyricFrame(view, $"player-detail-{size.Width:0}x{size.Height:0}.png");
            }
            DesktopTestUi.Invoke(action);
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
        var list = (MusicLyricList)view.FindName("LyricList");
        // Native item entrance uses an independent layout-transition visual. Keep
        // its glyph motion out of the fade comparison; manual scrolling stays enabled.
        list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        var resumeTimer = (DispatcherTimer)typeof(MusicPlayerDetailView).GetField("lyricResumeTimer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        var window = LyricWindow(view);
        ScrollViewer? observedScroll = null;
        bool intermediate = false;
        void ScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args) => intermediate = args.IsIntermediate;
        try
        {
            window.Activate(); await MusicTestUi.Drain();
            var viewport = (FrameworkElement)view.FindName("LyricViewport");
            var fade = (Grid)view.FindName("LyricFadeViewport");
            var edge = (MusicArtworkFade)view.FindName("LyricEdgeFade");
            var backdrop = (Border)view.FindName("PageBackdrop");
            var preview = (FrameworkElement)view.FindName("LyricPreviewFrame");
            var scroll = LyricVisuals(list).OfType<ScrollViewer>().First();
            observedScroll = scroll; scroll.ViewChanged += ScrollChanged;
            Check(ReferenceEquals(list.Parent, fade) && !ReferenceEquals(edge.Source, list),
                "lyric text stays directly in XAML, outside the sampled background surface");
            var text = LyricVisuals(list).OfType<TextBlock>().ToArray();
            Check(text.Length > 0 && text.All(block => block.FontWeight.Weight == FontWeights.Normal.Weight),
                "detail lyric glyphs use native regular TextBlocks");
            Check(ReferenceEquals(edge.Source, backdrop), "the edge fade projects the page's own cover backdrop");
            Check(ReferenceEquals(backdrop.Background, f.Module.PlayerBackground),
                "the projected backdrop is the page's actual PlayerBackground brush");
            var edgeMask = edge.Mask ?? throw new InvalidOperationException("Native fade mask was not attached");
            Check(edgeMask.ColorStops.Select(stop => (stop.Color.A, Math.Round((double)stop.Offset, 2))).SequenceEqual(
                new[] { ((byte)255, 0d), ((byte)0, .10d),
                    ((byte)0, .90d), ((byte)255, 1d) }),
                "only the upper and lower edges fade; the central eighty percent stays clear");
            foreach (var size in new[] { new Size(1298, 848), new Size(1000, 740) })
            foreach (var key in new[] { VirtualKey.Home, VirtualKey.End, VirtualKey.PageUp })
            {
                window.Resize(size.Width, size.Height); view.Width = size.Width; view.Height = size.Height; DesktopTestUi.WindowRoot(window).UpdateLayout();
                Equal(size.Width, view.ActualWidth); Equal(size.Height, view.ActualHeight);
                await MusicTestUi.Drain();
                await WaitForLyric(() => !intermediate && !MusicLyricScroll.IsMoving(scroll) &&
                    CenterGap(list, scroll, Math.Max(0, f.Module.LyricDisplayIndex)) < 1);
                int previous = f.Module.LyricDisplayIndex;
                Check(view.BrowseLyric(key), "browse command");
                try
                {
                    await WaitForLyric(() => !intermediate && !MusicLyricScroll.IsMoving(scroll) && f.Module.HasLyricPreview && (key switch
                    {
                        VirtualKey.Home => f.Module.LyricDisplayIndex == 0,
                        VirtualKey.End => f.Module.LyricDisplayIndex == f.Module.Lyrics.Count - 1,
                        _ => f.Module.LyricDisplayIndex >= 0 && f.Module.LyricDisplayIndex < previous
                    }) && CenterGap(list, scroll, f.Module.LyricDisplayIndex) < 1);
                }
                catch (InvalidOperationException error)
                {
                    string rows = list.ItemsPanelRoot is { } panel ? string.Join(';', panel.Children.OfType<FrameworkElement>()
                        .Select(row => $"{list.IndexFromContainer(row)}:{DesktopTestUi.Bounds(row, scroll)}:context={row.DataContext?.GetType().Name}")) : "none";
                    throw new InvalidOperationException($"{error.Message}; key={key}, size={size}, preview={f.Module.HasLyricPreview}, " +
                        $"index={previous}->{f.Module.LyricDisplayIndex}, gap={CenterGap(list, scroll, f.Module.LyricDisplayIndex)}, " +
                        $"offset={scroll.VerticalOffset}, moving={MusicLyricScroll.IsMoving(scroll)}, intermediate={intermediate}, scrollable={scroll.ScrollableHeight}, viewport={scroll.ViewportHeight}, " +
                        $"extent={scroll.ExtentHeight}, list={list.ActualWidth}x{list.ActualHeight}, rows={rows}", error);
                }
                // Pixel capture must observe one preview, independently of its idle timeout.
                bool resumeEnabled = resumeTimer.IsEnabled;
                resumeTimer.Stop();
                try
                {
                    await MusicTestUi.Drain();
                    Console.WriteLine($"LYRIC {size} {key}: row={f.Module.LyricDisplayIndex}, centerGap={CenterGap(list, scroll, f.Module.LyricDisplayIndex):F2}");
                    string PixelState() => $"offset={scroll.VerticalOffset:F6}, moving={MusicLyricScroll.IsMoving(scroll)}, intermediate={intermediate}, row={f.Module.LyricDisplayIndex}, " +
                        $"gap={CenterGap(list, scroll, f.Module.LyricDisplayIndex):F6}, preview={f.Module.HasLyricPreview}, " +
                        $"edge={DesktopTestUi.Bounds(edge, fade)}, opacity={edge.Opacity}, " +
                        $"backdrop={string.Join(';', (backdrop.Background as RadialGradientBrush)?.GradientStops.Select(stop => $"{stop.Offset}:{stop.Color}") ?? [])}";
                    string beforePixels = PixelState();
                    var visible = await NativeDesktopCapture.CaptureAsync(fade);
                    var edgeOpacity = edge.Opacity;
                    NativeDesktopCapture.Capture clear;
                    try { edge.Opacity = 0; clear = await NativeDesktopCapture.CaptureAsync(fade); }
                    finally { edge.Opacity = edgeOpacity; }
                    long difference = 0, content = 0;
                    for (int y = (int)(visible.Height * .15); y < visible.Height * .85; y++)
                    for (int x = 0; x < visible.Width; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int at = (y * visible.Width + x) * 4 + c;
                        difference += Math.Abs(visible.Pixels[at] - clear.Pixels[at]); content += clear.Pixels[at];
                    }
                    if (difference > content * .005)
                    {
                        await NativeDesktopCapture.SaveAsync(visible, "lyric-edge-visible");
                        await NativeDesktopCapture.SaveAsync(clear, "lyric-edge-clear");
                    }
                    Check(content > 1000 && difference <= content * .005,
                        $"the native edge backdrop leaves central lyric pixels unchanged; difference={difference}, content={content}, " +
                        $"ratio={(double)difference / Math.Max(1, content):F6}, viewport={visible.Width}x{visible.Height}, " +
                        $"before=[{beforePixels}], after=[{PixelState()}]");
                    var row = list.ContainerFromIndex(f.Module.LyricDisplayIndex) as ListViewItem;
                    Check(row is not null && row.Opacity == 1, "active lyric rows do not use whole-row fading");
                    var originalBackdrop = backdrop.Background;
                    var originalBackground = fade.Background;
                    double originalLyrics = list.Opacity, originalPreview = preview.Opacity;
                    try
                    {
                        // An opaque white source over black lets desktop RGB measure the actual
                        // compositor mask, without relying on RenderTargetBitmap's child-visual support.
                        backdrop.Background = new SolidColorBrush(Colors.White);
                        fade.Background = new SolidColorBrush(Colors.Black); list.Opacity = preview.Opacity = 0;
                        var edgeFrame = await NativeDesktopCapture.CaptureAsync(fade);
                        byte Coverage(double y) => edgeFrame.Pixels[((int)(edgeFrame.Height * y) * edgeFrame.Width + edgeFrame.Width / 2) * 4];
                        string samples = string.Join(';', new[] { .05, .15, .3, .5, .7, .85, .95 }
                            .Select(y => $"({edgeFrame.Width / 2},{(int)(edgeFrame.Height * y)})={Coverage(y)}"));
                        if (Coverage(.05) is not (> 90 and < 170) || Coverage(.95) is not (> 90 and < 170) ||
                            new[] { .15, .3, .5, .7, .85 }.Any(y => Coverage(y) > 2))
                            await NativeDesktopCapture.SaveAsync(edgeFrame, "lyric-edge-mask");
                        Check(Coverage(.05) is > 90 and < 170 && Coverage(.95) is > 90 and < 170,
                            $"the upper and lower compositor fades have half coverage at five percent; pixels={samples}");
                        foreach (double y in new[] { .15, .3, .5, .7, .85 })
                            Check(Coverage(y) <= 2, $"central mask stays transparent; pixels={samples}, {PixelState()}");
                    }
                    finally
                    {
                        backdrop.Background = originalBackdrop; fade.Background = originalBackground;
                        list.Opacity = originalLyrics; preview.Opacity = originalPreview;
                    }
                    Equal(0, f.Playback.Seeks);
                    if (size.Width == 1298) await SaveLyricFrame(view, $"player-lyrics-{key}.png");
                }
                finally
                {
                    if (resumeEnabled) resumeTimer.Start();
                }
            }
        }
        finally
        {
            if (observedScroll is not null) observedScroll.ViewChanged -= ScrollChanged;
            window.Close(); f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }
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
            var font = text is TextBlock block ? block.FontFamily : ((Control)text).FontFamily;
            var weight = text is TextBlock label ? label.FontWeight : ((Control)text).FontWeight;
            double actualSize = text is TextBlock run ? run.FontSize : ((Control)text).FontSize;
            Equal(new FontFamily("XamlAutoFontFamily").Source, font.Source);
            Equal(FontWeights.Normal.Weight, weight.Weight); Equal(size, actualSize);
        }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Activate(); await MusicTestUi.Drain(); DesktopTestUi.WindowRoot(window).UpdateLayout();
            var state = (TextBlock)view.FindName("LyricState");
            Equal("加载中…", state.Text);
            Typography(state, 20);
            Check(DesktopVisual.IsVisible(state), "the real loading text is mounted");
            release.TrySetResult();
            await activation;
            var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
            await f.Playback.OpenAsync("music", queue.GetProperty("currentId").GetString()!,
                new Uri("https://synthetic.invalid/unused"), "", default);
            f.Module.Lyrics[0] = new(1, "And disappear inside", "然后隐去", "ran hou yin qu");
            f.Module.Lyrics[1] = new(20, "逐字歌词", "逐字译文", "zhu zi ge ci",
                [new("逐字", 20, 2), new("歌词", 22, 2)]);
            var list = (MusicLyricList)view.FindName("LyricList");
            foreach (var position in new[] { 0d, 21d, 23d, 1d })
            {
                f.Playback.Position = position;
                int selected = position < 1 ? -1 : position < 20 ? 0 : 1;
                await WaitForLyric(() => f.Module.LyricDisplayIndex == selected &&
                    list.ContainerFromIndex(0) is ListViewItem &&
                    LyricVisuals(list).OfType<MusicLyricWordView>().Count() == 2);
                DesktopTestUi.WindowRoot(window).UpdateLayout();
                await MusicTestUi.Drain();
                var plain = LyricVisuals(list).OfType<TextBlock>()
                    .Single(text => text.Text == "And disappear inside");
                Check(DesktopVisual.IsVisible(plain), "ordinary lyrics retain their native text control");
                Typography(plain, 20);
                Equal(selected == 0 ? Color.FromArgb(255, 235, 234, 237) : Color.FromArgb(255, 139, 137, 140),
                    ((SolidColorBrush)plain.Foreground).Color);
                foreach (var content in new[] { "然后隐去", "ran hou yin qu", "逐字译文", "zhu zi ge ci" })
                {
                    var optional = LyricVisuals(list).OfType<TextBlock>().Single(text => text.Text == content);
                    Check(DesktopVisual.IsVisible(optional), "translation and romanization are actually presented");
                    Typography(optional, 14);
                }
                var words = LyricVisuals(list).OfType<MusicLyricWordView>().ToArray();
                foreach (var word in words)
                {
                    Typography(word, 20);
                    Check(word.Stroke is null, "full player words do not acquire desktop outlines");
                    Equal<Color?>(selected == 1 ? Color.FromArgb(255, 244, 243, 246) : Color.FromArgb(255, 146, 143, 144),
                        (word.Highlight as SolidColorBrush)?.Color);
                    Equal(Math.Clamp((position - word.Word!.Time) / word.Word.Duration, 0, 1), word.WordProgress);
                }
            }
            await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
            DesktopTestUi.WindowRoot(window).UpdateLayout();
            Check(LyricVisuals(list).OfType<TextBlock>().Where(text => text.Name == "LyricTranslation")
                .All(text => text.Visibility == Visibility.Collapsed), "the normal translation toggle still hides translations");
            await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
            DesktopTheme.SetIsHighContrast(view, true);
            DesktopTestUi.WindowRoot(window).UpdateLayout();
            foreach (var text in LyricVisuals(list).OfType<TextBlock>())
                Typography(text, text.Name == "LyricTranslation" || text.Text is "ran hou yin qu" or "zhu zi ge ci" ? 14 : 20);
            foreach (var word in LyricVisuals(list).OfType<MusicLyricWordView>()) Typography(word, 20);
            var title = (TextBlock)view.FindName("DetailTitle");
            Typography(title, 28);
            var footer = (MusicPlayerView)((ContentControl)view.FindName("DetailPlayerHost")).Content;
            var footerText = LyricVisuals(footer).OfType<TextBlock>().ToArray();
            Check(footerText.Length > 0 && footerText.All(text => text.FontWeight.Weight == FontWeights.Normal.Weight),
                "the footer retains native regular text");
            var delay = (Button)view.FindName("DelayLyricButton");
            Typography(LyricVisuals(delay).OfType<TextBlock>().Single(text => text.Text == "5"), 10);
            f.Peer.Block = null;
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            DesktopTestUi.WindowRoot(window).UpdateLayout();
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

    private static DesktopWindow LyricWindow(FrameworkElement view)
    {
        view.Width = 1298; view.Height = 848;
        var host = new Canvas(); host.Children.Add(view);
        var window = DesktopTestUi.CreateWindow(host, 1440, 800); window.AttachRoot();
        NativeDesktopCapture.DisableWindowTransitions(window);
        ((OverlappedPresenter)window.AppWindow.Presenter).SetBorderAndTitleBar(false, false);
        window.Resize(1298, 848); return window;
    }
    private static double CenterGap(MusicLyricList list, ScrollViewer scroll, int index) =>
        list.ContainerFromIndex(index) is FrameworkElement row
            ? Math.Abs(row.TransformToVisual(scroll).TransformPoint(new Point(0, row.ActualHeight / 2)).Y - scroll.ViewportHeight / 2)
            : double.PositiveInfinity;
    private static async Task WaitForLyric(Func<bool> done)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Lyric presentation did not settle");
            await MusicTestUi.Drain();
            await Task.Delay(15);
        }
    }
    private static Task SaveLyricFrame(FrameworkElement element, string name) =>
        NativeDesktopCapture.SaveAsync(element, Path.GetFileNameWithoutExtension(name));

    private static async Task VerifyMultiplePresentations()
    {
        string directory = NewPresentationRoot();
        string[] songs = [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")];
        const string image = "https://images.example.test/shared.png?size=original";
        byte[] original = [1, 2, 3, 4]; // Cache stores opaque original bytes; loader tests cover successful decoding.
        await using (var cache = new MusicPresentationCache(directory))
        {
            foreach (string song in songs)
            {
                var raw = LJ.Obj(("songId", song), ("duration", 100));
                string key = MusicPresentationCache.SongKey(raw);
                await cache.SaveLyricsAsync(key, "source:" + song, new JsonArray(LJ.Obj(("time", 0), ("text", song))), cache.LyricEpoch, default);
                await cache.WriteAsync(image, original, cache.Epoch, default);
                raw["sources"] = new JsonArray(LJ.Obj(("sourceKey", "supplemented-recording")));
                Equal(key, MusicPresentationCache.SongKey(raw));
            }
            await cache.DrainAsync();
        }
        await using var cold = new MusicPresentationCache(directory);
        foreach (string song in songs.Reverse())
        {
            var saved = await cold.ReadLyricsAsync(song, default);
            Equal(song, saved!["lines"]![0]!["text"]!.GetValue<string>());
            Equal("source:" + song, saved["sourceKey"]!.GetValue<string>());
        }
        Check((await cold.ReadAsync(image, default))!.Content.SequenceEqual(original));
        Check(File.ReadAllBytes(PresentationResourceFile(directory, "image", image)).SequenceEqual(original));
        Equal(3, Directory.EnumerateFiles(directory, "*.resource").Count()); // Two song-referenced lyrics and one shared image, not per-song images.
        await cold.ClearImagesAsync(default);
        Check(await cold.ReadAsync(image, default) is null);
        foreach (string song in songs) Check(await cold.ReadLyricsAsync(song, default) is not null);
        await cold.ClearAllAsync(default);
        foreach (string song in songs) Check(await cold.ReadLyricsAsync(song, default) is null);
    }
}
