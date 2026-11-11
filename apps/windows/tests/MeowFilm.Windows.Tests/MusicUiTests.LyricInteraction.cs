using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class MusicUiTests
{
    private static IEnumerable<TestCase> LyricInteractionCases()
    {
        yield return Case("hidden and unloaded lyric previews retire their hold and follow without a media tick", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            f.Module.Lyrics.Clear();
            for (int i = 0; i < 80; i++) f.Module.Lyrics.Add(new(i * 2, $"歌词 {i}", ""));
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = LyricInputSource(detail);
            Render(detail, 860, 600); await Drain();
            var list = (MusicLyricList)detail.FindName("LyricList");
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            double CenterError() => list.ItemContainerGenerator.ContainerFromIndex(20) is FrameworkElement row
                ? Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y - scroll.ViewportHeight * .24)
                : double.PositiveInfinity;
            await f.Playback.SeekAsync(40); await f.Playback.PauseAsync();
            await Until(() => CenterError() < 2);
            foreach (bool unload in new[] { false, true })
            {
                list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                await Task.Delay(250); await Drain();
                True(f.Module.HasLyricPreview, "manual browsing starts a four-second preview");
                if (unload) source.RootVisual = null;
                else detail.Visibility = Visibility.Hidden;
                await Drain();
                True(!f.Module.HasLyricPreview, "leaving the visible detail retires its preview");
                if (unload) source.RootVisual = detail;
                else detail.Visibility = Visibility.Visible;
                Render(detail, 860, 600); await Drain();
                await Until(() => CenterError() < 2);
                True(!f.Playback.IsPlaying && !f.Module.HasLyricPreview,
                    "restoring the detail follows the paused position without another media tick");
            }
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            source.RootVisual = null;
        });
        yield return Case("lyric loading empty failure and late cancellation stay inside the lyric region", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = LyricInputSource(detail);
            Render(detail, 860, 600); await Drain();
            var held = f.Calls.HoldNext("music.lyrics.local.get");
            var loading = f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Drain();
            var state = (TextBlock)detail.FindName("LyricState");
            Equal("加载中…", state.Text); Equal(0, f.Module.Lyrics.Count);
            True(!Visuals(detail).OfType<NoticeOutlet>().Any(), "lyric content states do not create notices");
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            held.Release(); await loading;
            Equal(0, f.Module.Lyrics.Count);
            True(f.Module.LyricStatus != "加载中…", "cancellation terminates loading");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default); await Drain();
            Equal("暂无歌词", state.Text);
            f.Calls.FailNext("music.lyrics.local.get", "STORAGE_UNAVAILABLE");
            await f.Module.RefreshLyricsAsync(); await Drain();
            True(state.Text.Length > 0 && state.Text != "暂无歌词" && state.Text != "加载中…",
                "a real failure is not reported as an empty lyric");
            await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw, text = "[00:00]当前歌词\n[00:10]第二句" });
            await f.Module.RefreshLyricsAsync(); await Drain();
            Equal(2, f.Module.Lyrics.Count); Equal("", state.Text);
            True(f.Module.Lyrics.All(row => row.Words is { Count: 0 }), "plain lyrics remain whole lines in the actual view model");
            f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            source.RootVisual = null;
        });
        yield return Case("lyric middle browsing accumulates wheel input pause stays fixed and automatic return avoids frame layouts", async () =>
        {
            await using var f = await Fixture.Create(); await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            f.Module.Lyrics.Clear();
            for (int i = 0; i < 1000; i++)
                f.Module.Lyrics.Add(new(i * .2, $"第{i}句 多词排版", "", Words:
                    [new($"第{i}句", i * .2, .1), new(" 多词排版", i * .2 + .1, .1)]));
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = LyricInputSource(detail);
            Render(detail, 860, 600); await Drain();
            var list = (MusicLyricList)detail.FindName("LyricList");
            var scroll = Visuals(list).OfType<ScrollViewer>().First();
            double CenterError(int index) => list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row
                ? Math.Abs(row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y - scroll.ViewportHeight * .24)
                : double.PositiveInfinity;
            await f.Playback.SeekAsync(80); await Until(() => CenterError(400) < 2);
            await Task.Delay(480); await Drain();
            var offset = scroll.VerticalOffset;
            var firstSpace = ((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(400)).Margin;
            await f.Playback.ResumeAsync(); await Drain();
            await f.Playback.PauseAsync(); await Drain();
            True(Math.Abs(scroll.VerticalOffset - offset) < .5, "pause and resume leave the reading offset unchanged");
            Equal(firstSpace, ((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(400)).Margin);
            True(!Visuals(list).OfType<Button>().Any(), "lyric rows have no per-line action");
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            True(!f.Module.HasLyricPreview, "clicking the lyric body does not select a preview");
            var beforeSeeks = f.Playback.SeekCalls;
            for (int i = 0; i < 3; i++)
                list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Task.Delay(260); await Drain();
            var distance = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 24d;
            True(Math.Abs(scroll.VerticalOffset - offset - distance * 3) < 2, "successive wheel notches retain their full distance");
            Equal(beforeSeeks, f.Playback.SeekCalls);
            var rowAtMiddle = Visuals(list).OfType<ListBoxItem>()
                .Where(row => list.ItemContainerGenerator.IndexFromContainer(row) >= 0)
                .OrderBy(row =>
                {
                    var y = row.TranslatePoint(new Point(), scroll).Y;
                    return Math.Max(y - scroll.ViewportHeight / 2, Math.Max(0, scroll.ViewportHeight / 2 - y - row.ActualHeight));
                }).First();
            Equal(list.ItemContainerGenerator.IndexFromContainer(rowAtMiddle), f.Module.LyricDisplayIndex);
            var preview = f.Module.Lyrics[f.Module.LyricDisplayIndex];
            f.Playback.Tick(81); await Drain();
            Equal(preview.Time, f.Module.Lyrics[f.Module.LyricDisplayIndex].Time);
            ((Button)detail.FindName("ConfirmLyric")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !f.Module.HasLyricPreview);
            Equal(beforeSeeks + 1, f.Playback.SeekCalls); Equal(preview.Time, f.Playback.PositionSeconds);
            True(f.Playback.IsPlaying, "explicit middle confirmation resumes a paused recording");
            await Task.Delay(480); await Drain();

            int layouts = 0, offsetChanges = 0;
            var frames = new List<double>(); var clock = Stopwatch.StartNew();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            EventHandler onLayout = (_, _) => layouts++;
            EventHandler onFrame = (_, _) => frames.Add(clock.Elapsed.TotalMilliseconds);
            ScrollChangedEventHandler onScroll = (_, e) => { if (e.VerticalChange != 0) offsetChanges++; };
            list.LayoutUpdated += onLayout; CompositionTarget.Rendering += onFrame; scroll.ScrollChanged += onScroll;
            try
            {
                await f.Playback.SeekAsync(82);
                await Task.Delay(550); await Drain();
                True(CenterError(410) < 2, "automatic follow finishes on the real wrapped row");
                True(offsetChanges < 10, "automatic movement does not drive a scroll-layout on every animation frame");
                var gaps = frames.Zip(frames.Skip(1), (a, b) => b - a).ToArray();
                Console.WriteLine($"LYRIC_PERF rows=1000 realized={Visuals(list).OfType<ListBoxItem>().Count()} " +
                    $"layouts={layouts} scrollChanges={offsetChanges} allocated={GC.GetAllocatedBytesForCurrentThread() - allocated} " +
                    $"renderCallbacks={frames.Count} maxCallbackGapMs={(gaps.Length == 0 ? 0 : gaps.Max()):F2} environment=Session0-not-GPU-FPS");
            }
            finally
            {
                list.LayoutUpdated -= onLayout; CompositionTarget.Rendering -= onFrame; scroll.ScrollChanged -= onScroll;
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); source.RootVisual = null;
            }
        });
    }

    private static HwndSource LyricInputSource(FrameworkElement content)
    {
        var source = new HwndSource(new HwndSourceParameters("lyric-interaction-code-fixture")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 860, Height = 600
        });
        source.RootVisual = content;
        return source;
    }
}
