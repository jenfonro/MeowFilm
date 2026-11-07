using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.AppHost;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicLyricDisplayTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native lyric source selection loads exact recording immediately without a refresh button", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            var source = f.Module.LyricSources.First(row => row.SourceKey.Length > 0);
            var done = Signal();
            PropertyChangedEventHandler changed = (_, e) =>
            {
                if (e.PropertyName == nameof(MusicCapability.CanCancelLyrics) && !f.Module.CanCancelLyrics && f.Module.Lyrics.Count == 2)
                    done.TrySetResult();
            };
            f.Module.PropertyChanged += changed;
            try
            {
                f.Module.SelectedLyricSource = source;
                await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(source, f.Module.SelectedLyricSource);
                Equal("", f.Module.LyricStatus);
                Equal(0, f.Runtime.Calls);
                Equal(0, f.Playback.Opens);
            }
            finally
            {
                f.Module.PropertyChanged -= changed;
                f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
            }
        }));
        yield return new("Native current artwork follows each immediately persisted metadata policy change", () => Sta(async () =>
        {
            string root = NewRoot();
            await using (var f = await Fixture.Create(root, cover: "https://art.example.test/recording42.png"))
            {
                Equal("https://art.example.test/recording42.png", f.Module.CurrentArtwork);
                await f.Module.SetMetadataEnabledAsync(
                    f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
                Equal("", f.Module.CurrentArtwork);
                Equal(0, f.Runtime.Calls); Equal(0, f.Http.Calls);
            }
            await using var restored = await Fixture.Create(root, seed: false);
            Equal("", restored.Module.CurrentArtwork);
            await restored.Module.SetMetadataEnabledAsync(
                restored.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), true);
            Equal("https://art.example.test/recording42.png", restored.Module.CurrentArtwork);
            Equal(0, restored.Runtime.Calls); Equal(0, restored.Http.Calls);
        }));
        yield return new("Native current artwork reconciles an unknown metadata receipt internally", () => Sta(() => WithArtworkFixture(async f =>
        {
            f.Access.FailAfterLibraryWrite = true;
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Equal("", f.Module.CurrentArtwork); Equal("", f.Module.Status);
            Equal(0, f.Http.Calls); Equal(0, f.Runtime.Calls);
        })));
        yield return new("Native current artwork unknown save remains internally reconciled when actual Sync overlaps its receipt",
            () => Sta(() => WithArtworkFixture(f => SyncPreservesUnknownSave(f, duringSave: true), holdSync: true)));
        yield return new("Native current artwork unknown save remains internally reconciled through actual Sync refresh",
            () => Sta(() => WithArtworkFixture(f => SyncPreservesUnknownSave(f, duringSave: false), holdSync: true)));
        yield return new("Native lyric display exact snapshot survives local document restart without Node network or autoplay", () => Sta(async () =>
        {
            string root = NewRoot();
            await using (var f = await Fixture.Create(root))
            {
                await f.Module.ActivateGlobalSurfaceAsync(
                    MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
                Equal(2, f.Module.Lyrics.Count); Equal(1, f.Http.Calls); Equal(0, f.Runtime.Calls);
                Equal("", f.Module.LyricStatus);
                var document = await f.Store.ReadAsync(MusicLibraryOwner.DocumentNamespace);
                Check(Encoding.UTF8.GetString(document.Data!).Contains("native-lyric-display", StringComparison.Ordinal));
                // music.library.v1 is plaintext JSON; the snapshot text may live only in that owner document.
                byte[] library = document.Data!;
                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(path);
                    Check(!Encoding.UTF8.GetString(bytes).Contains("SYNTHETIC_LYRIC", StringComparison.Ordinal) ||
                        bytes.SequenceEqual(library));
                }
                f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
            }
            await using var restored = await Fixture.Create(root, seed: false);
            restored.Http.Fail = true;
            await restored.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(2, restored.Module.Lyrics.Count); Equal("", restored.Module.LyricStatus);
            Equal(0, restored.Http.Calls); Equal(0, restored.Runtime.Calls); Equal(0, restored.Playback.Opens);
            await restored.Module.RefreshLyricsAsync();
            Equal(1, restored.Http.Calls); Equal(0, restored.Module.Lyrics.Count);
            Check(restored.Module.LyricStatus.Length > 0); // Explicit refresh does not hide auth/network failure with saved text.
            restored.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native lyric display preview cancel and confirm use exact current playback ownership", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
            await f.Playback.OpenAsync("music", queue.GetProperty("currentId").GetString()!, new Uri("https://synthetic.invalid/unused"), "", default);
            Check(f.Module.CanSeek);
            var row = f.Module.Lyrics[1];
            f.Module.PreviewLyric(row);
            Check(f.Module.HasLyricPreview && f.Module.CanConfirmLyricPreview);
            Equal(0, f.Playback.Seeks); Equal(0, f.Playback.Resumes);
            f.Playback.Position = 1.25;
            Equal(1, f.Module.LyricDisplayIndex); // Preview is not overwritten by the clock.
            f.Module.CancelLyricPreview(); Check(!f.Module.HasLyricPreview);
            Equal(0, f.Playback.Seeks);
            f.Module.PreviewLyric(row); await f.Module.ConfirmLyricPreviewAsync();
            Equal(1, f.Playback.Seeks); Equal(1, f.Playback.Resumes); Equal(row.Time, f.Playback.PositionSeconds);
            Check(!f.Module.HasLyricPreview);
            await f.Playback.PauseAsync(); await f.Module.SeekAsync(1);
            Equal(1, f.Playback.Resumes); // Ordinary seek still preserves pause.
            f.Module.PreviewLyric(row);
            await f.Playback.OpenAsync("film", "other-owner", new Uri("https://synthetic.invalid/unused"), "", default);
            await f.Module.ConfirmLyricPreviewAsync();
            Equal(2, f.Playback.Seeks); Equal(1, f.Playback.Resumes);
            f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
            Check(!f.Module.HasLyricPreview);
        }));
        yield return new("Native lyric display word shape exposes text and a bounded backward-seek clock", () => Sta(() =>
        {
            var control = new MusicLyricWordView { Word = new("中🙂", 1, 2), Position = 2, FontSize = 24 };
            control.Measure(new Size(500, 80)); control.Arrange(new Rect(0, 0, 500, 80));
            Equal(.5, control.WordProgress);
            control.Position = 0; Equal(0d, control.WordProgress);
            control.Position = 4; Equal(1d, control.WordProgress);
            control.Position = double.NaN; Equal(0d, control.WordProgress);
            var peer = UIElementAutomationPeer.CreatePeerForElement(control)!;
            Equal("中🙂", peer.GetName()); Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            return Task.CompletedTask;
        }));
        yield return new("Native lyric display cached shape responds to text font and direction without changing timing", () => Sta(() =>
        {
            var control = new MusicLyricWordView { Word = new("中🙂 e\u0301", 1, 2), Position = 2, FontSize = 20 };
            void Layout()
            { control.Measure(new Size(500, 100)); control.Arrange(new Rect(0, 0, 500, 100)); }
            Layout();
            var original = control.DesiredSize;
            control.Position = 2.5; Layout();
            Equal(original, control.DesiredSize); Equal(.75, control.WordProgress);
            control.FontSize = 32; Layout();
            Check(control.DesiredSize.Width > original.Width && control.DesiredSize.Height > original.Height);
            Equal(.75, control.WordProgress);
            control.FlowDirection = FlowDirection.RightToLeft; Layout();
            Equal(.75, control.WordProgress);
            control.Word = new("替换歌词 العربية", 3, 1); Layout();
            Equal(0d, control.WordProgress);
            Equal("替换歌词 العربية", UIElementAutomationPeer.CreatePeerForElement(control)!.GetName());
            control.Position = 3.5; Layout(); Equal(.5, control.WordProgress);
            return Task.CompletedTask;
        }));
        yield return new("Native lyric display switches and closes cancel late metadata before publication", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            var arrived = Signal(); var release = Signal();
            f.Http.Block = async () => { arrived.TrySetResult(); await release.Task; };
            var read = f.Module.RefreshLyricsAsync();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Module.CancelLyrics();
            var retry = f.Module.RefreshLyricsAsync();
            await Task.Delay(30);
            Check(!read.IsCompleted && !retry.IsCompleted);
            Equal(1, f.Http.Calls);
            release.TrySetResult();
            await Task.WhenAll(read, retry);
            Equal(2, f.Http.Calls);
            Equal(2, f.Module.Lyrics.Count); Equal(0, f.Runtime.Calls);
        }));
        yield return new("Native lyric display owns one request across player activation and duplicate restore", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            var arrived = Signal(); var release = Signal();
            f.Http.Block = async () => { arrived.TrySetResult(); await release.Task; };
            var refresh = f.Module.RefreshLyricsAsync();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var detail = f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            var repeated = f.Module.RestoreLyricsAsync();
            await Task.Delay(30);
            Equal(1, f.Http.Calls);
            release.TrySetResult();
            await Task.WhenAll(refresh, detail, repeated);
            Equal(1, f.Http.Calls); Equal(2, f.Module.Lyrics.Count);
            var published = f.Module.Lyrics[0];
            await f.Module.RestoreLyricsAsync();
            Equal(1, f.Http.Calls); Check(ReferenceEquals(published, f.Module.Lyrics[0]));
            f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        foreach (var mutation in new[] { "disable", "update" })
            yield return new("Native lyric display source " + mutation + " cancels old lyrics before retry publication", () => Sta(async () =>
            {
                await using var f = await Fixture.Create(allowScripts: true);
                var source = await f.InstallScript();
                await f.Module.SetSourceEnabledAsync(source, true);
                Check(f.Module.Sources.Single(row => !row.IsBuiltIn).Enabled);
                var arrived = Signal(); var release = Signal();
                var published = new List<string>();
                f.Module.Lyrics.CollectionChanged += (_, _) => published.AddRange(f.Module.Lyrics.Select(row => row.Text));
                f.Http.Text = "OLD_SOURCE";
                f.Http.Block = async () => { arrived.TrySetResult(); await release.Task; };
                var read = f.Module.RefreshLyricsAsync();
                try
                {
                    await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    f.Http.Block = null; f.Http.Text = "CURRENT_SOURCE";
                    if (mutation == "disable")
                    {
                        await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(row => !row.IsBuiltIn), false);
                        Check(!f.Module.Sources.Single(row => !row.IsBuiltIn).Enabled);
                    }
                    else
                    {
                        string previousHash = f.Module.Sources.Single(row => !row.IsBuiltIn).ExecutionHash;
                        await File.WriteAllTextAsync(f.ScriptPath, "// updated lyric source");
                        await f.Module.UpdateSourceAsync(f.Module.Sources.Single(row => !row.IsBuiltIn));
                        Check(f.Module.Sources.Single(row => !row.IsBuiltIn).ExecutionHash != previousHash);
                    }
                    var retry = f.Module.RestoreLyricsAsync();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Check(!read.IsCompleted && !retry.IsCompleted);
                    Equal(1, f.Http.Calls);
                    release.TrySetResult();
                    await Task.WhenAll(read, retry);
                    Equal("CURRENT_SOURCE", f.Module.Lyrics[0].Text);
                    Check(!published.Contains("OLD_SOURCE"));
                    Equal(2, f.Http.Calls); Equal(0, f.Runtime.Invocations); Equal(0, f.Playback.Opens);
                    var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
                    var saved = Ok(await f.Application.ExecuteAsync("music.lyrics.display.get",
                        new { song = queue.GetProperty("current").GetProperty("song") }));
                    Equal("CURRENT_SOURCE", saved.GetProperty("lines")[0].GetProperty("text").GetString());
                }
                finally { release.TrySetResult(); await read; }
            }));
        yield return new("Native lyric display source changes preserve saved text restoration without runtime or network", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.RefreshLyricsAsync();
            Equal(2, f.Module.Lyrics.Count); Equal(1, f.Http.Calls);
            f.Http.Fail = true;
            await f.InstallScript();
            await f.Module.RestoreLyricsAsync();
            Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
            Equal(1, f.Http.Calls); Equal(0, f.Runtime.Invocations); Equal(0, f.Playback.Opens);
        }));
        yield return new("Native lyric lookup after restart activates only the first usable enabled script", () => Sta(async () =>
        {
            string root = NewRoot(), firstId, unusedId;
            await using (var saved = await Fixture.Create(root, allowScripts: true))
            {
                var first = await saved.InstallScript(); firstId = first.InstallationId!;
                await saved.Module.SetSourceEnabledAsync(first, true);
                var second = await saved.InstallScript(); unusedId = second.InstallationId!;
                Check(firstId != unusedId);
                await saved.Module.SetSourceEnabledAsync(second, true);
            }
            await using var f = await Fixture.Create(root, seed: false, allowScripts: true);
            var listed = Ok(await f.Application.ExecuteAsync("music.sources.list"));
            Equal(2, listed.GetProperty("items").GetArrayLength());
            foreach (var source in listed.GetProperty("items").EnumerateArray())
            {
                Check(source.GetProperty("enabled").GetBoolean());
                var detail = Ok(await f.Application.ExecuteAsync("music.sources.detail",
                    new { installationId = source.GetProperty("installationId").GetString() }));
                Equal("idle", detail.GetProperty("item").GetProperty("state").GetString());
            }
            Equal(0, f.Runtime.Invocations); Check(f.Runtime.Loaded.Count == 0);
            f.Http.Empty = true;
            await f.Module.RefreshLyricsAsync();
            Equal("SCRIPT_LYRIC", f.Module.Lyrics.Single().Text);
            Check(f.Runtime.Loaded.SequenceEqual([firstId]) && !f.Runtime.Loaded.Contains(unusedId));
            Equal(1, f.Runtime.Invocations); Equal(1, f.Http.Calls); Equal(0, f.Playback.Opens);
        }));
        yield return new("Native lyric display source scope cannot restore another recording or accept an unknown field", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(store);
            var (song, key) = await Seed(owner);
            var lines = MusicLyricTiming.Parse("[00:01]text", "", 120);
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
            Equal("restored", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetProperty("state").GetString());
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song = Song("43") })).GetProperty("state").GetString());
            int writes = store.Writes;
            Code(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines, cookie = "SYNTHETIC" }), "INVALID_REQUEST");
            Code(await owner.ExecuteAsync("music.lyrics.display.save", new { song = Song("43"), sourceKey = key, lines }), "TARGET_CHANGED");
            Equal(writes, store.Writes);
        });
        yield return new("Native lyric display malformed or unsorted timed rows cannot enter durable snapshot", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(store);
            var (song, key) = await Seed(owner);
            var defects = new object[] {
                new[] { new { time = 2, text = "a" }, new { time = 1, text = "b" } },
                new[] { new { time = -1, text = "negative seek" } },
                new[] { new { time = 1, text = "a", url = "https://synthetic.invalid/" } },
                new[] { new { time = 1, text = "null\0" } },
                Array.Empty<object>()
            };
            int writes = store.Writes;
            foreach (var lines in defects)
                Check(!(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines })).GetProperty("ok").GetBoolean());
            Equal(writes, store.Writes);
        });
        yield return new("Native lyric display unknown CAS is reconciled by read without replaying save", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(store);
            var (song, key) = await Seed(owner);
            var lines = MusicLyricTiming.Parse("[00:01]text");
            store.FailAfterWrite = true;
            Code(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }), "COMMIT_UNKNOWN");
            int writes = store.Writes;
            Code(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }), "PENDING_WRITE");
            await owner.ReconcileAsync();
            Equal("restored", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetProperty("state").GetString());
            Equal(writes, store.Writes);
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
            Equal(writes, store.Writes); // Identical confirmed display is read-only.
        });
        yield return new("Native lyric display disabled preference prevents restoring previously saved text", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(store);
            var (song, key) = await Seed(owner);
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new {
                song, sourceKey = key, lines = MusicLyricTiming.Parse("[offset:-500]\n[00:01]text")
            }));
            Ok(await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false }));
            var restored = Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song }));
            Equal("disabled", restored.GetProperty("state").GetString()); Equal(0, restored.GetProperty("lines").GetArrayLength());
        });
        yield return new("Native metadata settings page loads saves ordering and disables lookup without starting Node", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            Equal(5, f.Module.MetadataPlatforms.Count); Check(f.Module.CanEditMetadata);
            var qq = f.Module.MetadataPlatforms.Single(row => row.Platform == "tx");
            await f.Module.MoveMetadataPlatformAsync(qq,
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"));
            await DisableAllMetadata(f.Module);
            Equal("tx", f.Module.MetadataPlatforms[0].Platform);
            Check(f.Module.MetadataPlatforms.All(row => !row.Enabled));
            await f.Module.RefreshLyricsAsync();
            Equal(0, f.Http.Calls); Equal(0, f.Runtime.Calls); Equal("", f.Module.LyricStatus);
            Check(!f.Module.CanCancelLyrics && f.Module.CanEditMetadata);
        }));
        yield return new("Native metadata changes restore visible player lyrics once without manual refresh", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(2, f.Module.Lyrics.Count); Equal(1, f.Http.Calls);
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Equal(0, f.Module.Lyrics.Count); Equal(1, f.Http.Calls);
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), true);
            Equal(2, f.Module.Lyrics.Count); Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
            Equal(1, f.Http.Calls); // Re-enable restores the exact saved display; it is not a refresh request.
            f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native quality changes retain exact visible lyrics without another lookup", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(2, f.Module.Lyrics.Count); Equal(1, f.Http.Calls);
            var published = f.Module.Lyrics[0];
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            Equal(1, f.Http.Calls);
            Check(ReferenceEquals(published, f.Module.Lyrics[0]));
            f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native local lyric selected file confirm DPAPI reopen and remove form one complete no-network chain", () => Sta(async () =>
        {
            string root = NewRoot(), selected = Path.Combine(root, "chosen.lrc");
            byte[] original = Encoding.UTF8.GetBytes("[00:01]用户选择\n[00:22]合成末行");
            await File.WriteAllBytesAsync(selected, original);
            await using (var f = await Fixture.Create(root))
            {
                f.Dialogs.LyricFile = selected; f.Dialogs.Accept = true;
                await f.Module.ImportLocalLyricsAsync();
                Equal("", f.Module.LyricStatus); Equal(2, f.Module.Lyrics.Count);
                Check(f.Module.CanRemoveLocalLyrics); Equal(0, f.Runtime.Calls); Equal(0, f.Http.Calls);
                Equal(0, f.Playback.Opens); Check((await File.ReadAllBytesAsync(selected)).SequenceEqual(original));
                var document = await f.Store.ReadAsync(MusicLibraryOwner.DocumentNamespace);
                string text = Encoding.UTF8.GetString(document.Data!);
                Check(text.Contains("native-local-lyrics", StringComparison.Ordinal));
                Check(!text.Contains(selected, StringComparison.Ordinal) && !text.Contains("chosen.lrc", StringComparison.Ordinal));
            }
            await using var reopened = await Fixture.Create(root, seed: false);
            await reopened.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal("", reopened.Module.LyricStatus); Equal("用户选择", reopened.Module.Lyrics[0].Text);
            Equal(0, reopened.Runtime.Calls); Equal(0, reopened.Http.Calls); Equal(0, reopened.Playback.Opens);
            reopened.Dialogs.Accept = true;
            await DisableAllMetadata(reopened.Module);
            await reopened.Module.RestoreLyricsAsync(); // Manual local text grants no platform lookup.
            Equal("", reopened.Module.LyricStatus);
            await reopened.Module.RemoveLocalLyricsAsync();
            Check(!reopened.Module.CanRemoveLocalLyrics); Equal(0, reopened.Module.Lyrics.Count);
            Equal(0, reopened.Http.Calls); Check((await File.ReadAllBytesAsync(selected)).SequenceEqual(original));
            reopened.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native local lyric declined confirmation makes no durable mutation or metadata request", () => Sta(async () =>
        {
            string root = NewRoot(), selected = Path.Combine(root, "cancel.lrc");
            await File.WriteAllTextAsync(selected, "[00:01]not admitted");
            await using var f = await Fixture.Create(root);
            var before = await f.Store.ReadAsync(MusicLibraryOwner.DocumentNamespace);
            f.Dialogs.LyricFile = selected; f.Dialogs.Accept = false;
            await f.Module.ImportLocalLyricsAsync();
            var after = await f.Store.ReadAsync(MusicLibraryOwner.DocumentNamespace);
            Equal(before.Fingerprint, after.Fingerprint);
            Equal(0, f.Http.Calls); Equal(0, f.Runtime.Calls); Check(!f.Module.CanRemoveLocalLyrics);
        }));
        yield return new("Native metadata disabled platform suppresses the previously saved display on restart", () => Sta(async () =>
        {
            string root = NewRoot();
            await using (var f = await Fixture.Create(root))
            {
                await f.Module.ActivateGlobalSurfaceAsync(
                    MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
                Equal(2, f.Module.Lyrics.Count);
                await f.Module.SetMetadataEnabledAsync(
                    f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
                f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
            }
            await using var next = await Fixture.Create(root, seed: false);
            await next.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(0, next.Module.Lyrics.Count); Equal("", next.Module.LyricStatus);
            Equal(0, next.Http.Calls); Equal(0, next.Runtime.Calls);
            next.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native metadata unknown save reconciles inside the immediate setting operation", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            f.Access.FailAfterLibraryWrite = true;
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Equal("", f.Module.Status);
            Check(!f.Module.MetadataPlatforms.Single(row => row.Platform == "wy").Enabled);
            int writes = f.Access.LibraryWrites;
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Equal(writes, f.Access.LibraryWrites);
            await f.Module.RefreshLyricsAsync();
            Equal("", f.Module.LyricStatus); Equal(0, f.Http.Calls); Equal(0, f.Runtime.Calls);
            Equal("", f.Module.Status);
        }));
    }

    private static async Task DisableAllMetadata(MusicCapability module)
    {
        foreach (string platform in module.MetadataPlatforms.Where(row => row.Enabled)
                     .Select(row => row.Platform).ToArray())
            await module.SetMetadataEnabledAsync(
                module.MetadataPlatforms.Single(row => row.Platform == platform), false);
    }

    private static async Task WithArtworkFixture(Func<Fixture, Task> body, bool holdSync = false)
    {
        var fixture = await Fixture.Create(cover: "https://art.example.test/recording42.png", holdSync: holdSync);
        Exception? failure = null;
        try { await body(fixture); }
        catch (Exception error) { failure = error; }
        try { await fixture.DisposeAsync(); }
        catch (Exception cleanup)
        {
            if (failure is null) failure = cleanup;
            else failure.Data["FixtureCleanupFailure"] = cleanup;
        }
        // An unknown receipt must still fail cleanup, but cannot hide an earlier assertion.
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task SyncPreservesUnknownSave(Fixture f, bool duringSave)
    {
        // Hold one explicitly requested real NativeApplication Sync round before
        // the actual Module listener. Startup itself must not perform this request.
        await f.Module.NavigateAsync("favorites", default);
        await f.SyncNoticeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var committed = Signal(); var releaseReceipt = Signal();
        try
        {
            f.Access.FailAfterLibraryWrite = true;
            if (duringSave) f.Access.BeforeLibraryReceipt = async () =>
            {
                committed.TrySetResult();
                await releaseReceipt.Task;
            };
            var save = f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            if (duringSave)
            {
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(!save.IsCompleted);
            }
            else
            {
                await save;
                Equal("", f.Module.Status);
            }
            Equal("", f.Module.CurrentArtwork);
            f.ReleaseSyncNotice();
            await f.SyncNoticeDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ok(await f.SyncRound!);
            // The Module posts its real notification at Normal priority. This
            // barrier admits that refresh while the first receipt is still held.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (duringSave)
            {
                Check(!save.IsCompleted);
                releaseReceipt.TrySetResult();
                await save;
            }
            await WaitIdle(f.Module);
            Check(f.SyncNoticeFailure is null);
            Equal("", f.Module.CurrentArtwork);
            Equal("", f.Module.Status);
            int writes = f.Access.LibraryWrites;
            f.Access.BeforeLibraryReceipt = null;
            Equal("", f.Module.Status); Equal("", f.Module.CurrentArtwork);
            Check(!f.Module.MetadataPlatforms.Single(row => row.Platform == "wy").Enabled);
            Equal(writes, f.Access.LibraryWrites); // Internal reconciliation reads, never replays CAS.
            // Ordinary user commands still own and replace their status.
            await f.Module.CreatePlaylistAsync(""); Equal("请输入歌单名称", f.Module.Status);
            await f.Module.RefreshAsync(); Equal("", f.Module.Status);
            Equal(writes, f.Access.LibraryWrites);
            Equal(0, f.Http.Calls); Equal(0, f.Runtime.Calls); Equal(0, f.Playback.Opens);
        }
        finally
        {
            f.Access.BeforeLibraryReceipt = null;
            releaseReceipt.TrySetResult();
            f.ReleaseSyncNotice(); // Fixture cleanup can physically drain even after a failed assertion.
        }
    }
    private static async Task WaitIdle(MusicCapability module)
    {
        var idle = Signal();
        PropertyChangedEventHandler changed = (_, _) => { if (!module.Busy) idle.TrySetResult(); };
        module.PropertyChanged += changed;
        try
        {
            if (!module.Busy) idle.TrySetResult();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { module.PropertyChanged -= changed; }
    }
    private static JsonElement Song(string id = "42", string cover = "")
    {
        var native = new JsonObject();
        if (cover.Length > 0) native["img"] = cover;
        return MusicSongFixture.Element(MusicSongFixture.Online("wy", id, "Synthetic lyric song",
            "Synthetic artist", "Fixture", 120, cover, native));
    }
    private static async Task<(JsonElement Song, string Key)> Seed(MusicLibraryOwner owner)
    {
        Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { Song() }, index = 0 }));
        var queue = Ok(await owner.ExecuteAsync("music.queue.get"));
        var song = queue.GetProperty("current").GetProperty("song");
        return (song, song.GetProperty("sources")[0].GetProperty("sourceKey").GetString()!);
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Lyric display assertion failed"); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static JsonElement Ok(JsonElement result)
    { if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetProperty("code").GetString()); return result; }
    private static void Code(JsonElement result, string code)
    { Check(!result.GetProperty("ok").GetBoolean()); Equal(code, result.GetProperty("code").GetString()); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string NewRoot()
    {
        var parent = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Missing isolated output");
        string path = Path.Combine(parent, "lyric-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static async Task Sta(Func<Task> body)
    {
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = Signal(); Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            var current = Dispatcher.CurrentDispatcher; Volatile.Write(ref dispatcher, current);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(current));
            current.BeginInvoke(async () =>
            {
                Exception? failure = null;
                try { await body(); } catch (Exception error) { failure = error; }
                finally { done.TrySetResult(failure); current.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); } finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "native-lyric-display-code-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? error;
        try { error = await done.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
        catch { Volatile.Read(ref dispatcher)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly LocalDocumentStore Store;
        internal readonly WriteFaultStore Access;
        internal readonly LyricRuntime Runtime;
        internal readonly Handler Http = new();
        internal readonly MusicNativeApplication Application;
        internal readonly Playback Playback = new();
        internal readonly Dialogs Dialogs = new();
        internal readonly MusicCapability Module;
        internal readonly string ScriptPath;
        internal readonly TaskCompletionSource SyncNoticeEntered = Signal(), SyncNoticeDelivered = Signal();
        internal Task<JsonElement>? SyncRound;
        internal Exception? SyncNoticeFailure;
        private readonly TaskCompletionSource releaseSyncNotice = Signal();
        private int syncNoticeHeld;
        private Fixture(string root, bool holdSync)
        {
            Runtime = new(root);
            ScriptPath = Path.Combine(root, "lyric-source.js");
            Store = new(root, AppIdentity.NativeNamespaces, AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            Access = new(Store);
            Application = new(Access, Runtime, Http);
            if (holdSync) Application.LibrarySyncChanged += HoldSyncNotice;
            Module = new(Application, Access, Playback, Dialogs);
            if (holdSync) Application.LibrarySyncChanged += () => SyncNoticeDelivered.TrySetResult();
        }
        private void HoldSyncNotice()
        {
            if (Interlocked.Exchange(ref syncNoticeHeld, 1) != 0) return;
            SyncNoticeEntered.TrySetResult();
            try { releaseSyncNotice.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (Exception error) { SyncNoticeFailure = error; }
        }
        internal void ReleaseSyncNotice() => releaseSyncNotice.TrySetResult();
        internal static async Task<Fixture> Create(string? root = null, bool seed = true, string cover = "", bool holdSync = false,
            bool allowScripts = false)
        {
            var value = new Fixture(root ?? NewRoot(), holdSync);
            value.Runtime.AllowScripts = allowScripts;
            try
            {
                if (seed) Ok(await value.Application.ExecuteAsync("music.queue.replace", new { songs = new[] { Song(cover: cover) }, index = 0 }));
                await value.Module.InitializeAsync(default);
                if (holdSync) value.SyncRound = value.Application.ExecuteAsync("music.librarySync.run", new { });
                return value;
            }
            catch (Exception failure)
            {
                try { await value.DisposeAsync(); }
                catch (Exception cleanup) { failure.Data["FixtureCleanupFailure"] = cleanup; }
                throw;
            }
        }
        internal async Task<MusicSourceRow> InstallScript()
        {
            await File.WriteAllTextAsync(ScriptPath, "// synthetic lyric source");
            Dialogs.ScriptFile = ScriptPath;
            int previousCount = Module.Sources.Count(row => !row.IsBuiltIn);
            await Module.ImportSourceAsync();
            Equal("", Module.Status);
            Equal(previousCount + 1, Module.Sources.Count(row => !row.IsBuiltIn));
            return Module.Sources.Last();
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseSyncNotice();
            try
            {
                if (SyncRound is not null) Ok(await SyncRound);
            }
            finally { try { await Module.DisposeAsync(); } finally { await Store.DisposeAsync(); } }
        }
    }
    private sealed class WriteFaultStore(IDocumentStore store) : IDocumentStore
    {
        internal bool FailAfterLibraryWrite;
        internal int LibraryWrites;
        internal Func<Task>? BeforeLibraryReceipt;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) => store.ReadAsync(ns, token);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            var result = await store.WriteAsync(ns, bytes, expected, token);
            if (ns == MusicLibraryOwner.DocumentNamespace)
            {
                LibraryWrites++;
                if (BeforeLibraryReceipt is { } beforeReceipt) await beforeReceipt().ConfigureAwait(false);
                if (FailAfterLibraryWrite)
                { FailAfterLibraryWrite = false; throw new DocumentStoreException(DocumentStoreError.Unavailable); }
            }
            return result;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Fixture owns the real DPAPI store.
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        internal bool Fail, Empty;
        internal string Text = "SYNTHETIC_LYRIC";
        internal Func<Task>? Block;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.RequestUri!.Host is "music.163.com" or "interfacepc.music.163.com");
            string text = Text;
            if (Block is { } block) await block();
            if (Fail) throw new HttpRequestException("Synthetic unavailable");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                code = 200, id = "42", lrc = new { lyric = Empty ? "" : "[00:01]" + text + "\n[00:20]second" },
                tlyric = new { lyric = Empty ? "" : "[00:01]合成译文" }
            }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class LyricRuntime(string root) : IRuntimeClient
    {
        // Durable state of the fake shared module, shared only by fixture reopen.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, JsonObject>> documents = new();
        private readonly Dictionary<string, JsonObject> items = documents.GetOrAdd(root, _ => []);
        private readonly HashSet<string> ready = [];
        internal int Calls, Invocations;
        internal bool AllowScripts;
        internal readonly List<string> Loaded = [];
        private readonly HashSet<(string Id, long Revision)> candidates = [];
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(module == "music" && !source.IsEmpty && Guid.TryParseExact(installationId, "D", out _) && sourceRevision > 0);
            candidates.Add((installationId, sourceRevision));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3",
                value = new { installationId, sourceRevision } }));
        }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            var input = JsonSerializer.SerializeToElement(parameters); object result;
            string Id() => input.GetProperty("installationId").GetString()!;
            JsonObject Row(string id)
            {
                var row = items[id].DeepClone().AsObject();
                row["state"] = !row["enabled"]!.GetValue<bool>() ? "disabled" : ready.Contains(id) ? "ready" : "idle";
                return row;
            }
            switch (method)
            {
                case "music.script.sources.add":
                    string id = Id();
                    Check(candidates.Remove((id, input.GetProperty("sourceRevision").GetInt64())));
                    items[id] = JsonSerializer.SerializeToNode(new { installationId = id, name = "Lyric fixture", version = "1",
                        origin = new { kind = "file" }, enabled = false, executionHash = new string('a', 64), rollbackAvailable = false,
                        sites = new { wy = new { actions = new[] { "lyric" }, qualitys = Array.Empty<string>() } }, check = (object?)null })!.AsObject();
                    result = new { item = Row(id) }; break;
                case "music.script.sources.list": result = new { items = items.Keys.Select(Row).ToArray() }; break;
                case "music.script.sources.detail": result = new { item = Row(Id()) }; break;
                case "music.script.sources.enable":
                    items[Id()]["enabled"] = true; ready.Add(Id()); result = new { item = Row(Id()) }; break;
                case "music.script.sources.disable":
                    items[Id()]["enabled"] = false; ready.Remove(Id()); result = new { item = Row(Id()) }; break;
                case "music.script.sources.update":
                    Check(candidates.Remove((Id(), input.GetProperty("sourceRevision").GetInt64())));
                    items[Id()]["executionHash"] = new string('b', 64); result = new { item = Row(Id()) }; break;
                case "music.script.invoke":
                    Check(AllowScripts);
                    if (ready.Add(Id())) Loaded.Add(Id());
                    Invocations++; result = "[00:01]SCRIPT_LYRIC"; break;
                default: throw new InvalidOperationException("Unexpected script method: " + method);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3", value = result }));
        }
    }
    private sealed class Dialogs : IMusicDialogs
    {
        internal bool Accept;
        internal string? LyricFile, ScriptFile;
        public bool Confirm(string message, string title) => Accept;
        public string[]? SelectAudioFiles() => null;
        public string? SelectLyricFile() => LyricFile;
        public string? SelectScriptFile() => ScriptFile;
    }
    private sealed class Playback : IMusicPlayback
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }
        private double position;
        internal double Position { get => position; set { position = value; Changed(); } }
        public double PositionSeconds => position;
        public double DurationSeconds => 120;
        public int Volume { get; set; }
        internal int Opens, Seeks, Resumes;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        private void Changed() => PropertyChanged?.Invoke(this, new(""));
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { OwnerId = owner; Identity = identity; HasMedia = true; IsPlaying = false; Opens++; Changed(); return Task.CompletedTask; }
        public Task PauseAsync() { IsPlaying = false; Changed(); return Task.CompletedTask; }
        public Task ResumeAsync() { IsPlaying = true; Resumes++; Changed(); return Task.CompletedTask; }
        public Task StopAsync() { HasMedia = false; Changed(); return Task.CompletedTask; }
        public Task SeekAsync(double seconds) { Position = seconds; Seeks++; return Task.CompletedTask; }
    }
    private sealed class MemoryStore : IDocumentStore
    {
        private byte[]? data;
        internal int Writes;
        internal bool FailAfterWrite;
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Equal(MusicLibraryOwner.DocumentNamespace, documentNamespace);
            return Task.FromResult(new DocumentSnapshot(data?.ToArray(), data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data)));
        }
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            Equal(MusicLibraryOwner.DocumentNamespace, documentNamespace); token.ThrowIfCancellationRequested();
            Equal(data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(data), expected);
            Writes++; data = bytes.ToArray();
            if (FailAfterWrite) { FailAfterWrite = false; throw new DocumentStoreException(DocumentStoreError.Unavailable); }
            return Task.FromResult(DocumentFingerprint.FromBytes(data));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
