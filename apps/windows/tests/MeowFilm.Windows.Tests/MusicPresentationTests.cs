using System.Windows;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicPresentationTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PresentationCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> PresentationCases()
    {
        yield return Case("presentation continuous media ticks do not refresh page metadata or unchanged lyric selection", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start();
            f.Module.Lyrics.Add(new(0, "first", ""));
            f.Module.Lyrics.Add(new(10, "second", ""));
            await f.Playback.SeekAsync(1);
            var notifications = new List<string>();
            f.Module.PropertyChanged += (_, e) => notifications.Add(e.PropertyName ?? "");
            // Exercise both named native notifications and a port's all-properties event.
            for (int i = 0; i < 10; i++)
            {
                f.Playback.Tick(1.2 + i * .2);
                await f.Playback.SeekAsync(1.3 + i * .2);
            }
            Equal(20, notifications.Count(n => n == nameof(MusicCapability.PositionSeconds)));
            True(notifications.All(n => n is nameof(MusicCapability.PositionSeconds) or nameof(MusicCapability.PositionText)),
                "same-line ticks publish only changed time values: " + string.Join(",", notifications.Distinct()));
            notifications.Clear();
            f.Playback.Tick(10.1);
            Equal(1, notifications.Count(n => n == nameof(MusicCapability.ActiveLyricIndex)));
            Equal(1, notifications.Count(n => n == nameof(MusicCapability.LyricDisplayIndex)));
            Equal(1, f.Module.ActiveLyricIndex);
            notifications.Clear();
            f.Playback.Tick(10.3);
            True(!notifications.Contains(nameof(MusicCapability.LyricDisplayIndex)), "same target is not reselected");
            f.Playback.Tick(.5);
            Equal(0, f.Module.ActiveLyricIndex);
        });
        yield return Case("presentation lyric lookup handles duplicate timestamps seek and row replacement", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            f.Module.Lyrics.Add(new(1, "first", ""));
            f.Module.Lyrics.Add(new(1, "translation group", ""));
            f.Module.Lyrics.Add(new(2, "last", ""));
            f.Playback.Tick(0); Equal(-1, f.Module.ActiveLyricIndex);
            f.Playback.Tick(1); Equal(1, f.Module.ActiveLyricIndex);
            f.Playback.Tick(200); Equal(2, f.Module.ActiveLyricIndex);
            f.Module.PreviewLyric(f.Module.Lyrics[0]);
            Equal(0, f.Module.LyricDisplayIndex);
            f.Module.CancelLyricPreview(); Equal(2, f.Module.LyricDisplayIndex);
            f.Module.Lyrics.Clear();
            Equal(-1, f.Module.ActiveLyricIndex);
            f.Module.Lyrics.Add(new(0, "replacement", ""));
            Equal(0, f.Module.LyricDisplayIndex);
        });
        yield return Case("presentation navigation loading does not disable the current media seek", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync(); f.Playback.Start();
            var held = f.Calls.HoldNext("music.history.get");
            var navigation = f.Module.NavigateAsync("archives", default);
            await held.Arrived.Task;
            try
            {
                True(f.Module.Busy && !f.Module.CanEdit, "content loading retains its own editing gate");
                True(f.Module.CanSeek && f.Module.CanTogglePlayback, "unrelated navigation does not gate active media");
                await f.Module.SeekAsync(42);
                Equal(42d, f.Playback.PositionSeconds);
            }
            finally { held.Release(); await navigation; }
        });
        yield return Case("presentation mounts one content body and restores the same page instance", async () =>
        {
            await using var f = await Fixture.Create();
            var view = (MusicView)f.Module.View;
            void Layout()
            {
                view.Measure(new Size(1080, 680)); view.Arrange(new Rect(0, 0, 1080, 680)); view.UpdateLayout();
            }
            Layout();
            var bodies = (Grid)view.FindName("BodyPages");
            var presenter = (ContentControl)bodies.Children[0];
            var songs = (DataGrid)view.FindName("SongsGrid");
            True(ReferenceEquals(presenter.Content, songs), "songs own the active body");
            True(!Visuals(view).OfType<MusicCatalogView>().Any(), "catalog is not a hidden mounted page");
            True(!Visuals(view).OfType<MusicDiscoveryView>().Any(), "discovery is not a hidden mounted page");
            await f.Module.NavigateAsync("archives", default);
            Layout();
            True(ReferenceEquals(presenter.Content, view.FindName("ArchiveScroll")), "archive is the sole body");
            True(!Visuals(view).OfType<DataGrid>().Any(), "inactive song table leaves layout/input");
            True(ReferenceEquals(songs.DataContext, f.Module) &&
                ReferenceEquals(songs.ItemsSource, f.Module.CatalogSongsSource),
                "unmount keeps the same binding owner and song source");
            await f.Module.NavigateAsync("favorites", default);
            Layout();
            True(ReferenceEquals(presenter.Content, songs), "same page state and input owner return");
        });
        yield return Case("presentation seek rail animation never writes media state and stops on pause hide and scrub", async () =>
        {
            var bar = new SeekBar { Maximum = 30, Value = 1, IsPlaying = true, PlaybackRate = 2, PresentationKey = "test:1" };
            DesktopTheme.SetIsHighContrast(bar, false);
            var window = new Window { Content = bar, Width = 500, Height = 100,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.UpdateLayout(); await Drain();
                await Task.Delay(160); await Drain();
                Equal(1d, bar.Value);
                if (SystemParameters.ClientAreaAnimation)
                    True(bar.DisplayValue > 1.1, "framework advances the painted rail between samples");
                bar.IsPlaying = false;
                double paused = bar.DisplayValue;
                await Task.Delay(120); await Drain();
                Equal(paused, bar.DisplayValue);
                bar.Value = 10; Equal(10d, bar.DisplayValue);
                bar.IsPlaying = true;
                bar.BeginScrub(); bar.UpdateScrub(20);
                await Task.Delay(100); await Drain();
                Equal(20d, bar.DisplayValue);
                bar.CancelScrub(); bar.Visibility = Visibility.Collapsed;
                double hidden = bar.DisplayValue;
                await Task.Delay(100); await Drain(); Equal(hidden, bar.DisplayValue);
                bar.Visibility = Visibility.Visible; bar.PlaybackRate = null;
                await Task.Delay(100); await Drain(); Equal(bar.Value, bar.DisplayValue);
            }
            finally { window.Close(); }
        });
    }
}
