using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
