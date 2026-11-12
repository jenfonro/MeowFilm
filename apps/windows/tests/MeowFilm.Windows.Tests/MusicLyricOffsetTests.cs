using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicLyricOffsetTests
{
    public static IEnumerable<TestCase> Cases() =>
        MusicUiTests.LyricOffsetCases().Concat(MusicLyricDisplayTests.LyricOffsetSourceCases())
            .Concat(PlaybackRecoveryTests.LyricOffsetQualityCases());
}

public static partial class PlaybackRecoveryTests
{
    internal static IEnumerable<TestCase> LyricOffsetQualityCases()
    {
        yield return Test("lyric correction survives an accepted same-recording audio quality handoff", async () =>
        {
            await using var f = await ControlledMusicFixture.Create(official: false,
                scripts: [new("lyric-standard", quality: "128k"), new("lyric-lossless", quality: "flac")],
                qualityRequest: "128k");
            var settings = await f.App.ExecuteAsync("music.settings.get");
            var enabled = await f.App.ExecuteAsync("music.settings.save", new
            {
                ifRevision = settings.GetProperty("settings").GetProperty("revision").GetInt64(), lyrics = true
            });
            Check(enabled.GetProperty("ok").GetBoolean(), "this lyric test enables the fixture's normally disabled lyrics");
            await f.Play();
            await Until(() => f.Module.CanSwitchSource, "initial source admitted");
            var queue = await f.App.ExecuteAsync("music.queue.get");
            var saved = await f.App.ExecuteAsync("music.lyrics.local.save", new
            {
                song = queue.GetProperty("current").GetProperty("song"), text = "[00:00]相同录音"
            });
            Check(saved.GetProperty("ok").GetBoolean(), "source lyrics saved through the native owner");
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            try
            {
                f.Module.AdjustLyricOffset(.5);
                Check(f.Module.LyricOffsetSeconds == .5 && f.Module.Lyrics.Count == 1, "initial lyric correction");
                f.Output.Plan();
                await f.Module.SwitchQualityAsync("lossless");
                Check(f.Output.Commits == 1 && f.Module.LyricOffsetSeconds == .5,
                    "a real accepted quality replacement is not a song or lyric source change");
            }
            finally { f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); }
        });
    }
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> LyricOffsetCases()
    {
        yield return Case("lyric adjustment buttons move the shared line and real word clock without seeking audio", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw,
                text = "[00:01]<00:01.000>甲<00:02.000>乙<00:03.000>\n[00:05]第二句" });
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            var detail = (MusicPlayerDetailView)f.Module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail);
            using var source = LyricInputSource(detail);
            try
            {
                f.Playback.Tick(1.25);
                Render(detail, 1110, 800); await Drain();
                GeneralAppearanceTests.SavePreview(detail, "player-lyric-timing-rail", 1110, 800);
                var list = (MusicLyricList)detail.FindName("LyricList");
                var delay = (Button)detail.FindName("DelayLyricButton");
                var advance = (Button)detail.FindName("AdvanceLyricButton");
                Equal(2, ((StackPanel)detail.FindName("LyricTimingRail")).Children.Count);
                await AssertLyricTimingIconAsync(delay); await AssertLyricTimingIconAsync(advance);
                Equal(0, f.Module.ActiveLyricIndex); Equal(1.25, list.Position);
                var firstWord = Visuals(list).OfType<MusicLyricWordView>().Single(word => word.Word?.Text == "甲");
                True(Math.Abs(firstWord.WordProgress - .25) < .01, "the initial sample uses real source word times");
                int seeks = f.Playback.SeekCalls;
                Click(delay); await Drain();
                Equal(.5, f.Module.LyricOffsetSeconds); Equal(.75, list.Position);
                Equal(-1, f.Module.ActiveLyricIndex); Equal(0d, firstWord.WordProgress);
                Equal(1.25, f.Playback.PositionSeconds); Equal(seeks, f.Playback.SeekCalls);
                Click(advance); Click(advance); await Drain();
                Equal(-.5, f.Module.LyricOffsetSeconds); Equal(1.75, list.Position);
                Equal(0, f.Module.ActiveLyricIndex);
                True(Math.Abs(firstWord.WordProgress - .75) < .01, "advance recalibrates the same retained word");
                Equal(1.25, f.Playback.PositionSeconds); Equal(seeks, f.Playback.SeekCalls);
                f.Playback.Tick(4.5); await Drain();
                Equal(1, f.Module.ActiveLyricIndex);
                Equal(f.Module.Lyrics[1], f.Module.DesktopLyricTopRow);
            }
            finally
            {
                f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail);
                source.RootVisual = null;
            }
        });

        yield return Case("lyric preview applies the inverse correction and clamps only the actual media seek", async () =>
        {
            await using var f = await Fixture.Create();
            await f.PlayableCollection(); await f.Module.PlaySelectionAsync();
            await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw,
                text = "[00:01]第一句\n[00:05]第二句" });
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            try
            {
                var first = f.Module.Lyrics[0]; var second = f.Module.Lyrics[1];
                f.Module.AdjustLyricOffset(.5); f.Module.AdjustLyricOffset(.5);
                f.Module.PreviewLyric(second);
                Equal("00:06", f.Module.LyricPreviewTime);
                await f.Module.ConfirmLyricPreviewAsync();
                Equal(6d, f.Playback.PositionSeconds); Equal(1, f.Playback.SeekCalls);
                True(f.Playback.IsPlaying && !f.Module.HasLyricPreview, "confirmation preserves existing resume intent");
                f.Module.AdjustLyricOffset(-2);
                await f.Playback.PauseAsync();
                f.Module.PreviewLyric(first); Equal("00:00", f.Module.LyricPreviewTime);
                await f.Module.ConfirmLyricPreviewAsync();
                Equal(0d, f.Playback.PositionSeconds); Equal(2, f.Playback.SeekCalls);
                f.Module.AdjustLyricOffset(500);
                Equal(499d, f.Module.LyricOffsetSeconds);
                f.Module.PreviewLyric(second); Equal("03:00", f.Module.LyricPreviewTime);
                await f.Module.ConfirmLyricPreviewAsync();
                Equal(180d, f.Playback.PositionSeconds);
            }
            finally { f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); }
        });

        yield return Case("lyric correction survives consumer reopening and refresh but not another playing occurrence", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await SaveDesktopLyricFixture(f, translated: false);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                f.Module.AdjustLyricOffset(.5);
                await f.Playback.PauseAsync(); await f.Playback.ResumeAsync();
                await f.Module.RefreshLyricsAsync();
                Equal(.5, f.Module.LyricOffsetSeconds);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                Equal(.5, f.Module.LyricOffsetSeconds);
                await f.Module.StepAsync(true);
                Equal(0d, f.Module.LyricOffsetSeconds);
                await f.Module.StepAsync(false);
                await f.Module.RestoreLyricsAsync();
                Equal(0d, f.Module.LyricOffsetSeconds);
                True(f.Module.CanAdjustLyricOffset, "the returning song does not restore a durable adjustment");
                await f.Module.PrepareShutdownAsync();
                True(!f.Module.CanAdjustLyricOffset, "shutdown seals view adjustments too");
            }));

        yield return Case("desktop double tracks alternate current and next source rows independently of lyric preview", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await f.Rpc("music.lyrics.local.save", new { song = f.Module.Songs[0].Raw,
                    text = "[00:01]上句\n[00:03]下句\n[00:05]末句" });
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
                await f.Module.SetDesktopLyricLayoutAsync("double");
                f.Playback.Tick(0);
                Equal("前奏", f.Module.DesktopLyricText);
                True(f.Module.DesktopLyricTopRow is null && f.Module.DesktopLyricBottomRow is null, "no invented current line");
                var rows = f.Module.Lyrics.ToArray();
                f.Playback.Tick(1);
                True(ReferenceEquals(rows[0], f.Module.DesktopLyricTopRow) &&
                    ReferenceEquals(rows[1], f.Module.DesktopLyricBottomRow), "first upper line and next lower line");
                f.Playback.Tick(3);
                True(ReferenceEquals(rows[2], f.Module.DesktopLyricTopRow) &&
                    ReferenceEquals(rows[1], f.Module.DesktopLyricBottomRow), "lower current and upcoming upper line");
                f.Module.PreviewLyric(rows[0]);
                Equal(rows[2], f.Module.DesktopLyricTopRow); Equal(rows[1], f.Module.DesktopLyricBottomRow);
                f.Playback.Tick(5);
                Equal(rows[2], f.Module.DesktopLyricTopRow);
                True(f.Module.DesktopLyricBottomRow is null, "there is no fabricated line after the end");
                await f.Module.SetDesktopLyricLayoutAsync("single");
                Equal(rows[2], f.Module.DesktopLyricTopRow);
                True(f.Module.DesktopLyricBottomRow is null, "single layout contains only the current original");
                f.Module.CancelLyricPreview();
            }));

        yield return Case("desktop music action invokes only the explicitly attached live main window callback", async () =>
        {
            await using var f = await Fixture.Create();
            int first = 0, second = 0;
            var owner = DesktopLyricOwner(); var replacement = DesktopLyricOwner();
            try
            {
                owner.Show(); replacement.Show();
                f.Module.AttachDesktopLyricsOwner(owner, () => first++);
                f.Module.ActivateDesktopLyricsOwner(); Equal(1, first);
                f.Module.AttachDesktopLyricsOwner(replacement, () => second++);
                owner.Close();
                f.Module.ActivateDesktopLyricsOwner();
                Equal(1, first); Equal(1, second);
                replacement.Close();
                f.Module.ActivateDesktopLyricsOwner(); Equal(1, second);
            }
            finally { owner.Close(); replacement.Close(); }
        });
    }

    private static async Task AssertLyricTimingIconAsync(Button button)
    {
        True(button.Content is System.Windows.FrameworkElement &&
            Visuals(button).OfType<System.Windows.Shapes.Path>().Any(path => path.Data is not null),
            "lyric timing uses directional vector artwork instead of a plain text button");
        True(Visuals(button).OfType<Border>().Any(border =>
            border.CornerRadius.TopLeft >= button.ActualHeight / 2 - 1),
            "the timing control has the reference's circular outline");
        Equal("5", Visuals(button).OfType<TextBlock>().Single().Text);
        True(System.Windows.Automation.AutomationProperties.GetName(button).Contains("0.5") &&
            button.ToolTip?.ToString()?.Contains("0.5") == true,
            "the icon retains the exact half-second accessible name and tooltip");
        bool highContrast = DesktopTheme.GetIsHighContrast(button);
        var admission = System.Windows.Data.BindingOperations.GetBindingExpression(button, System.Windows.UIElement.IsEnabledProperty)!;
        try
        {
            DesktopTheme.SetIsHighContrast(button, true); await Drain();
            Equal(System.Windows.SystemColors.WindowTextColor,
                ((System.Windows.Media.SolidColorBrush)button.Foreground).Color);
            True(Visuals(button).OfType<System.Windows.Shapes.Path>().All(path => Equals(path.Stroke, button.Foreground)),
                "the vector follows system high-contrast ink");
            button.SetCurrentValue(System.Windows.UIElement.IsEnabledProperty, false); await Drain();
            Equal(System.Windows.SystemColors.GrayTextColor,
                ((System.Windows.Media.SolidColorBrush)button.Foreground).Color);
            Equal(System.Windows.SystemColors.GrayTextColor,
                ((System.Windows.Media.SolidColorBrush)button.BorderBrush).Color);
        }
        finally
        {
            DesktopTheme.SetIsHighContrast(button, highContrast);
            admission.UpdateTarget(); await Drain();
        }
    }
}

public static partial class MusicLyricDisplayTests
{
    internal static IEnumerable<TestCase> LyricOffsetSourceCases()
    {
        yield return new("Native lyric explicit recording source change retires its prior user correction", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayerDetail, default);
            try
            {
                var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
                await f.Playback.OpenAsync("music", queue.GetProperty("currentId").GetString()!,
                    new Uri("https://synthetic.invalid/unused"), "", default);
                f.Module.AdjustLyricOffset(.5); Equal(.5, f.Module.LyricOffsetSeconds);
                f.Module.SelectedLyricSource = f.Module.LyricSources.First(row => row.SourceKey.Length > 0);
                await f.Module.RestoreLyricsAsync();
                Equal(0d, f.Module.LyricOffsetSeconds);
                Equal(2, f.Module.Lyrics.Count); Equal(0, f.Playback.Seeks);
            }
            finally { f.Module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayerDetail); }
        }));
    }
}
