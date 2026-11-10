using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicMediaActionTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.MediaActionCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> MediaActionCases()
    {
        yield return Case("media actions pause resume and change the current occurrence through the existing queue", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                int opens = f.Playback.Opens.Count;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.TogglePlayback);
                Equal(1, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.TogglePlayback);
                Equal(1, f.Playback.Pauses); Equal(1, f.Playback.Resumes);
                Equal(opens, f.Playback.Opens.Count);
                string first = f.Playback.Identity!;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.Next);
                True(first != f.Playback.Identity, "Next changes the real queue occurrence");
                Equal(opens + 1, f.Playback.Opens.Count);
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.Previous);
                Equal(first, f.Playback.Identity); Equal(opens + 2, f.Playback.Opens.Count);
                Equal(1, f.Calls.Count("music.queue.next")); Equal(1, f.Calls.Count("music.queue.previous"));
            }));

        yield return Case("media actions adjust volume by five clamp at the media boundary and save through device settings", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                f.Module.Volume = 50;
                await f.Module.SaveVolumeAsync();
                int writes = f.Calls.Count("music.device.settings.save");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.VolumeUp);
                Equal(55, f.Module.Volume);
                Equal(55, f.Calls.Last("music.device.settings.save").GetProperty("volume").GetInt32());
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.VolumeDown);
                Equal(50, f.Module.Volume);
                Equal(writes + 2, f.Calls.Count("music.device.settings.save"));
                f.Module.Volume = 99;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.VolumeUp);
                Equal(100, f.Module.Volume);
                Equal(100, (await f.Rpc("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32());
                f.Module.Volume = 1;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.VolumeDown);
                Equal(0, f.Module.Volume);
                Equal(0, (await f.Rpc("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32());
            }));

        yield return Case("media actions favorite targets the playing song rather than selection without reopening media", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                f.Module.SelectedSong = f.Module.Songs[2];
                int opens = f.Playback.Opens.Count;
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleFavorite);
                True(f.Module.IsCurrentFavorite, "the current song is added to the native favorites owner");
                True((await f.Rpc("music.library.membership", new { id = "favorites", song = f.Module.Songs[0].Raw }))
                    .GetProperty("present").GetBoolean(), "current membership is durable");
                True(!(await f.Rpc("music.library.membership", new { id = "favorites", song = f.Module.Songs[2].Raw }))
                    .GetProperty("present").GetBoolean(), "selection is not the favorite target");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleFavorite);
                True(!f.Module.IsCurrentFavorite, "the same action removes current membership");
                Equal(opens, f.Playback.Opens.Count); Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
            }));

        yield return Case("media actions report unavailable playback and business failures through existing status", async () =>
        {
            await WithDesktopLyrics(async (f, _) =>
            {
                foreach (var action in new[] { MediaShortcutAction.TogglePlayback, MediaShortcutAction.Previous,
                    MediaShortcutAction.Next, MediaShortcutAction.ToggleFavorite })
                {
                    await f.Module.RunAsync(() => Task.CompletedTask);
                    Equal("", f.Module.Status);
                    await f.Module.ExecuteMediaShortcutAsync(action);
                    True(f.Module.Status.Length > 0, "unavailable actions are not silently reported as successful");
                }
                Equal(0, f.Playback.Opens.Count);
            }, withCurrent: false);
            await WithDesktopLyrics(async (f, _) =>
            {
                f.Calls.FailNext("music.library.membership", "STORAGE_UNAVAILABLE");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleFavorite);
                True(!f.Module.IsCurrentFavorite && f.Module.Status.Length > 0, "failed membership leaves actual favorite state");
                int saved = (await f.Rpc("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32();
                f.Module.Volume = 45;
                await f.Module.RunAsync(() => Task.CompletedTask);
                Equal("", f.Module.Status);
                int writes = f.Calls.Count("music.device.settings.save");
                f.Calls.FailNext("music.device.settings.save", "STORAGE_UNAVAILABLE");
                await f.Module.ExecuteMediaShortcutAsync(MediaShortcutAction.VolumeUp);
                Equal(writes + 1, f.Calls.Count("music.device.settings.save"));
                True(f.Module.Status.Length > 0, "a failed durable volume save publishes the existing error");
                Equal(saved, (await f.Rpc("music.device.settings.get")).GetProperty("settings").GetProperty("volume").GetInt32());
            });
        });

        yield return Case("media actions seal all eight routes after shutdown preparation", () =>
            WithDesktopLyrics(async (f, _) =>
            {
                await f.Module.PrepareShutdownAsync();
                int calls = f.Calls.Log.Count, opens = f.Playback.Opens.Count, volume = f.Module.Volume;
                bool translation = f.Module.ShowLyricTranslation;
                foreach (var action in Enum.GetValues<MediaShortcutAction>())
                    await f.Module.ExecuteMediaShortcutAsync(action);
                Equal(calls, f.Calls.Log.Count); Equal(opens, f.Playback.Opens.Count); Equal(volume, f.Module.Volume);
                Equal(translation, f.Module.ShowLyricTranslation); Equal(0, f.Playback.Pauses); Equal(0, f.Playback.Resumes);
                True(!f.Module.IsDesktopLyricsOpen, "sealed input cannot create a new native window");
            }));
    }
}
