using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.AppHost;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicLyricDisplayTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Native lyric first last resize and manual rounded preview share viewport center", () => Sta(VerifyCenteredLyrics));
        yield return new("Native complete player proportions align lyrics and preview capsule without shifting text", () => Sta(VerifyDetailLayout));
        yield return new("Native lyric viewport fades only its edges across first last scrolling and resize", () => Sta(VerifyViewportFade));
        yield return new("Native full player lyric text uses automatic rendering through loading selection and content changes", () => Sta(VerifyLyricRendering));
        yield return new("Native recording presentation preserves multiple songs independent artwork and cold lyrics", VerifyMultiplePresentations);
        yield return new("Native long lyric source persists and restores from the current cold display cache", async () =>
        {
            await using var store = new MemoryStore();
            await using var disk = new DisplayDisk(store.Storage);
            JsonElement song;
            string key;
            var lines = MusicLyricTiming.Parse("[00:01]long locator");
            await using (var cache = new NativeTestDatabase(disk))
            await using (var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: cache))
            {
                Ok(await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("canonical/" + new string('x', 9000)) }, index = 0 }));
                song = Ok(await owner.ExecuteAsync("music.queue.get")).GetProperty("current").GetProperty("song").Clone();
                key = song.GetProperty("sources")[0].GetProperty("sourceKey").GetString()!;
                Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
                await owner.PresentationCache.DrainAsync();
            }
            int writes = store.Writes;
            await using var coldCache = new NativeTestDatabase(disk);
            await using var cold = new MusicLibraryOwner(MusicTestStores.For(store), database: coldCache);
            var restored = Ok(await cold.ExecuteAsync("music.lyrics.display.get", new { song, sourceKey = key }));
            Equal("restored", restored.GetProperty("state").GetString());
            Equal(key, restored.GetProperty("sourceKey").GetString());
            Check(JsonNode.DeepEquals(JsonNode.Parse(lines.GetRawText()), JsonNode.Parse(restored.GetProperty("lines").GetRawText())));
            Equal(writes, store.Writes);
        });
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
                Equal(0, f.Runtime.Calls); Equal(0, f.Peer.Calls);
            }
            await using var restored = await Fixture.Create(root, seed: false);
            Equal("", restored.Module.CurrentArtwork);
            await restored.Module.SetMetadataEnabledAsync(
                restored.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), true);
            Equal("https://art.example.test/recording42.png", restored.Module.CurrentArtwork);
            Equal(0, restored.Runtime.Calls); Equal(0, restored.Peer.Calls);
        }));
        yield return new("Native current artwork reconciles an unknown metadata receipt internally", () => Sta(() => WithArtworkFixture(async f =>
        {
            f.Access.FailAfterLibraryWrite = true;
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Equal("", f.Module.CurrentArtwork); Equal("", f.Module.Status);
            Equal(0, f.Peer.Calls); Equal(0, f.Runtime.Calls);
        })));
        yield return new("Native current artwork unknown save remains internally reconciled when actual Sync overlaps its receipt",
            () => Sta(() => WithArtworkFixture(f => SyncPreservesUnknownSave(f, duringSave: true), holdSync: true)));
        yield return new("Native current artwork unknown save remains internally reconciled through actual Sync refresh",
            () => Sta(() => WithArtworkFixture(f => SyncPreservesUnknownSave(f, duringSave: false), holdSync: true)));
        yield return new("Native lyric display exact snapshot survives cold resource restart without Node network or autoplay", () => Sta(async () =>
        {
            string root = NewRoot();
            await using (var f = await Fixture.Create(root))
            {
                await f.Module.ActivateGlobalSurfaceAsync(
                    MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
                Equal(2, f.Module.Lyrics.Count); Equal(1, f.Peer.Calls); Equal(0, f.Runtime.Calls);
                Equal("", f.Module.LyricStatus);
                var document = f.Access.Records.Root;
                Check(!document.ToJsonString().Contains("SYNTHETIC_LYRIC", StringComparison.Ordinal));
                await f.Application.PresentationCache.DrainAsync();
                var queue = Ok(await f.Application.ExecuteAsync("music.queue.get"));
                string songId = queue.GetProperty("current").GetProperty("song").GetProperty("songId").GetString()!;
                var display = await f.Application.PresentationCache.ReadLyricsAsync(songId, default);
                Check(display!.ToJsonString().Contains("SYNTHETIC_LYRIC", StringComparison.Ordinal));
                f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
            }
            await using var restored = await Fixture.Create(root, seed: false);
            restored.Peer.Fail = true;
            await restored.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(2, restored.Module.Lyrics.Count); Equal("", restored.Module.LyricStatus);
            Equal(0, restored.Peer.Calls); Equal(0, restored.Runtime.Calls); Equal(0, restored.Playback.Opens);
            await restored.Module.RefreshLyricsAsync();
            Equal(1, restored.Peer.Calls); Equal(0, restored.Module.Lyrics.Count);
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
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control)!;
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
            Equal("替换歌词 العربية", FrameworkElementAutomationPeer.CreatePeerForElement(control)!.GetName());
            control.Position = 3.5; Layout(); Equal(.5, control.WordProgress);
            return Task.CompletedTask;
        }));
        yield return new("Native lyric display switches and closes cancel late metadata before publication", () => Sta(async () =>
        {
            LyricLookupApplication? application = null;
            await using var f = await Fixture.Create(decorate: inner => application = new(inner));
            var lookups = application!;
            var arrived = Signal(); var release = Signal();
            var published = new List<string>();
            f.Module.Lyrics.CollectionChanged += (_, _) => published.AddRange(f.Module.Lyrics.Select(row => row.Text));
            f.Peer.Block = async () => { arrived.TrySetResult(); await release.Task; };
            var read = f.Module.RefreshLyricsAsync();
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
                f.Module.CancelLyrics();
                await read.WaitAsync(TimeSpan.FromSeconds(3));
                Equal(0, f.Module.Lyrics.Count); Equal(0, published.Count);
                Check(!f.Module.CanCancelLyrics);
                // The current application contract cancels this subscription, not
                // its shared physical read. A new same-recording subscriber joins it.
                var retry = f.Module.RefreshLyricsAsync();
                await lookups.SecondLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!retry.IsCompleted && !release.Task.IsCompleted);
                Equal(1, f.Peer.Calls);
                release.TrySetResult();
                await retry;
                Equal(1, f.Peer.Calls);
                Equal(2, f.Module.Lyrics.Count); Equal(0, f.Runtime.Calls);
                Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
                Equal("second", f.Module.Lyrics[1].Text);
            }
            finally { release.TrySetResult(); await read; }
        }));
        yield return new("Native lyric display owns one request across player activation and duplicate restore", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            var arrived = Signal(); var release = Signal();
            f.Peer.Block = async () => { arrived.TrySetResult(); await release.Task; };
            var refresh = f.Module.RefreshLyricsAsync();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var detail = f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            var repeated = f.Module.RestoreLyricsAsync();
            await Task.Delay(30);
            Equal(1, f.Peer.Calls);
            release.TrySetResult();
            await Task.WhenAll(refresh, detail, repeated);
            Equal(1, f.Peer.Calls); Equal(2, f.Module.Lyrics.Count);
            var published = f.Module.Lyrics[0];
            await f.Module.RestoreLyricsAsync();
            Equal(1, f.Peer.Calls); Check(ReferenceEquals(published, f.Module.Lyrics[0]));
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
                f.Peer.Text = "OLD_SOURCE";
                f.Peer.Block = async () => { arrived.TrySetResult(); await release.Task; };
                var read = f.Module.RefreshLyricsAsync();
                try
                {
                    await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    f.Peer.Block = null; f.Peer.Text = "CURRENT_SOURCE";
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
                    await read.WaitAsync(TimeSpan.FromSeconds(3));
                    Equal(0, f.Module.Lyrics.Count);
                    Check(!published.Contains("OLD_SOURCE"));
                    var retry = f.Module.RestoreLyricsAsync();
                    await retry.WaitAsync(TimeSpan.FromSeconds(5));
                    // Invalidating a source creates a new generation without
                    // waiting for the old provider that ignores cancellation.
                    Check(!release.Task.IsCompleted);
                    Equal(2, f.Peer.Calls);
                    release.TrySetResult();
                    Equal(2, f.Module.Lyrics.Count);
                    Equal("CURRENT_SOURCE", f.Module.Lyrics[0].Text);
                    Equal("second", f.Module.Lyrics[1].Text);
                    Check(!published.Contains("OLD_SOURCE"));
                    Equal(2, f.Peer.Calls); Equal(0, f.Runtime.Invocations); Equal(0, f.Playback.Opens);
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
            Equal(2, f.Module.Lyrics.Count); Equal(1, f.Peer.Calls);
            f.Peer.Fail = true;
            await f.InstallScript();
            await f.Module.RestoreLyricsAsync();
            Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
            Equal(1, f.Peer.Calls); Equal(0, f.Runtime.Invocations); Equal(0, f.Playback.Opens);
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
            f.Peer.Empty = true;
            await f.Module.RefreshLyricsAsync();
            Equal("SCRIPT_LYRIC", f.Module.Lyrics.Single().Text);
            Check(f.Runtime.Loaded.SequenceEqual([firstId]) && !f.Runtime.Loaded.Contains(unusedId));
            Equal(1, f.Runtime.Invocations); Equal(1, f.Peer.Calls); Equal(0, f.Playback.Opens);
        }));
        yield return new("Native lyric display Unicode text obeys the existing UTF8 resource limit across cold restart", async () =>
        {
            await using var store = new MemoryStore();
            string text = string.Concat(Enumerable.Repeat("歌词", 20000));
            var lines = Enumerable.Range(1, 4).Select(time => new { time, text }).ToArray();
            JsonElement song;
            string key;
            await using (var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage))
            {
                (song, key) = await Seed(owner);
                int writes = store.Writes;
                Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
                Equal(writes, store.Writes);
                Check(await store.Storage.ReadAsync(db => db.Scalar<long>("SELECT storedBytes FROM windows_lyrics")) <= 512 * 1024);
            }
            await using var cold = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
            int before = store.Writes;
            var restored = Ok(await cold.ExecuteAsync("music.lyrics.display.get", new { song, sourceKey = key }));
            Equal("restored", restored.GetProperty("state").GetString());
            Equal(4, restored.GetProperty("lines").GetArrayLength());
            Equal(text, restored.GetProperty("lines")[0].GetProperty("text").GetString());
            Equal(before, store.Writes);
        });
        yield return new("Native lyric display source scope cannot restore another recording and ignores unused input fields", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
            var (song, key) = await Seed(owner);
            var lines = MusicLyricTiming.Parse("[00:01]text", "", 120);
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
            Equal("restored", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetProperty("state").GetString());
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song = Song("43") })).GetProperty("state").GetString());
            int writes = store.Writes;
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines, cookie = "SYNTHETIC" }));
            Check(!Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetRawText().Contains("SYNTHETIC", StringComparison.Ordinal));
            Code(await owner.ExecuteAsync("music.lyrics.display.save", new { song = Song("43"), sourceKey = key, lines }), "TARGET_CHANGED");
            Equal(writes, store.Writes);
        });
        yield return new("Native lyric display malformed or unsorted timed rows cannot enter durable snapshot", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
            var (song, key) = await Seed(owner);
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key,
                lines = new[] { new { time = 1, text = "a", url = "https://synthetic.invalid/" } } }));
            var restored = Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song }));
            Check(!restored.GetRawText().Contains("synthetic.invalid", StringComparison.Ordinal));
            Equal("a", restored.GetProperty("lines")[0].GetProperty("text").GetString());
            var defects = new object[] {
                new[] { new { time = 2, text = "a" }, new { time = 1, text = "b" } },
                new[] { new { time = -1, text = "negative seek" } },
                new[] { new { time = 1, text = "null\0" } },
                Array.Empty<object>()
            };
            int writes = store.Writes;
            foreach (var lines in defects)
                Check(!(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines })).GetProperty("ok").GetBoolean());
            Equal(writes, store.Writes);
            Equal("a", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song }))
                .GetProperty("lines")[0].GetProperty("text").GetString());
        });
        yield return new("Native lyric display failed persistence never enters business pending", async () =>
        {
            await using var store = new MemoryStore();
            await using var disk = new DisplayDisk(store.Storage) { FailWrite = true };
            await using var cache = new NativeTestDatabase(disk);
            await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: cache);
            var (song, key) = await Seed(owner);
            var lines = MusicLyricTiming.Parse("[00:01]text");
            int writes = store.Writes;
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
            await cache.DrainAsync();
            Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetProperty("state").GetString());
            disk.FailWrite = false;
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines }));
            Equal("restored", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetProperty("state").GetString());
            Ok(await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = true }));
            await owner.PrepareShutdownAsync();
        });
        yield return new("Native lyric display business gate wait observes latest saved resource and current disabled policy", () => Task.Run(async () =>
        {
            foreach (bool disable in new[] { false, true })
            {
                await using var store = new MemoryStore();
                await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
                var (song, key) = await Seed(owner);
                Ok(await owner.ExecuteAsync("music.lyrics.display.save",
                    new { song, sourceKey = key, lines = MusicLyricTiming.Parse("[00:01]old") }));
                store.HoldCommit();
                var business = disable
                    ? owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false })
                    : owner.ExecuteAsync("music.library.create", new { name = "held lyric read" });
                Task<JsonElement>? read = null;
                try
                {
                    await store.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    read = owner.ExecuteAsync("music.lyrics.display.get", new { song, sourceKey = key });
                    await owner.PresentationCache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!read.IsCompleted && !business.IsCompleted);
                    await owner.PresentationCache.SaveLyricsAsync(song.GetProperty("songId").GetString()!, key,
                        JsonNode.Parse(MusicLyricTiming.Parse("[00:01]latest").GetRawText())!.AsArray(),
                        owner.PresentationCache.LyricEpoch, default);
                }
                finally
                {
                    store.Release!.TrySetResult();
                    Ok(await business.WaitAsync(TimeSpan.FromSeconds(5)));
                    if (read is not null) await read.WaitAsync(TimeSpan.FromSeconds(5));
                }
                var settled = Ok(await read!);
                Equal(disable ? "disabled" : "restored", settled.GetProperty("state").GetString());
                if (disable) Equal(0, settled.GetProperty("lines").GetArrayLength());
                else Equal("latest", settled.GetProperty("lines")[0].GetProperty("text").GetString());
            }
        }));
        yield return new("Native lyric display cancelled gate wait does not publish and borrowed generic cache survives library close", () => Task.Run(async () =>
        {
            await using var store = new MemoryStore();
            await using var cache = new NativeTestDatabase(store.Storage);
            await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: cache);
            var (song, key) = await Seed(owner);
            Ok(await owner.ExecuteAsync("music.lyrics.display.save",
                new { song, sourceKey = key, lines = MusicLyricTiming.Parse("[00:01]retained") }));
            store.HoldCommit();
            var business = owner.ExecuteAsync("music.library.create", new { name = "held cancellation" });
            using var cancellation = new CancellationTokenSource();
            Task<JsonElement>? read = null;
            try
            {
                await store.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
                read = owner.ExecuteAsync("music.lyrics.display.get", new { song, sourceKey = key }, cancellation.Token);
                await owner.PresentationCache.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                Code(await read.WaitAsync(TimeSpan.FromSeconds(5)), "CANCELLED");
                Check(!business.IsCompleted);
            }
            finally
            {
                store.Release!.TrySetResult();
                Ok(await business.WaitAsync(TimeSpan.FromSeconds(5)));
                if (read is not null) await read.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Equal("retained", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song }))
                .GetProperty("lines")[0].GetProperty("text").GetString());
            Ok(await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false }));
            await owner.DisposeAsync();
            Check(await cache.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_lyrics")) >= 0);
            Code(await owner.ExecuteAsync("music.lyrics.display.get", new { song }), "CLOSED");
        }));
        yield return new("Native lyric display closed borrowed cache adapter cannot poison the shared database", async () =>
        {
            await using var store = new MemoryStore();
            await using var cache = new DisplayDisk(store.Storage);
            await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: cache);
            var (song, key) = await Seed(owner);
            await cache.DisposeAsync();
            Ok(await owner.ExecuteAsync("music.lyrics.display.save", new { song, sourceKey = key, lines = MusicLyricTiming.Parse("[00:01]text") }));
            Equal("restored", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song })).GetProperty("state").GetString());
            Ok(await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false }));
            await owner.PrepareShutdownAsync();
        });
        yield return new("Native lyric display unreadable or malformed cache is a miss without business mutation", async () =>
        {
            foreach (bool unreadable in new[] { true, false })
            {
                await using var store = new MemoryStore();
                await using var disk = new DisplayDisk(store.Storage);
                await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: disk);
                var (song, key) = await Seed(owner);
                Ok(await owner.ExecuteAsync("music.lyrics.display.save",
                    new { song, sourceKey = key, lines = MusicLyricTiming.Parse("[00:01]valid") }));
                if (unreadable) disk.FailRead = true;
                else await store.Storage.WriteAsync(db =>
                {
                    db.Execute("UPDATE windows_lyrics_lines SET time=-1");
                    return true;
                });
                int writes = store.Writes;
                Equal("none", Ok(await owner.ExecuteAsync("music.lyrics.display.get", new { song, sourceKey = key })).GetProperty("state").GetString());
                Equal(writes, store.Writes);
                Ok(await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = true }));
            }
        });
        yield return new("Native lyric display disabled preference prevents restoring previously saved text", async () =>
        {
            await using var store = new MemoryStore();
            await using var owner = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
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
            Equal(0, f.Peer.Calls); Equal(0, f.Runtime.Calls); Equal("暂无歌词", f.Module.LyricStatus);
            Check(!f.Module.CanCancelLyrics && f.Module.CanEditMetadata);
        }));
        yield return new("Native metadata changes restore visible player lyrics once without manual refresh", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(2, f.Module.Lyrics.Count); Equal(1, f.Peer.Calls);
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), false);
            Equal(0, f.Module.Lyrics.Count); Equal(1, f.Peer.Calls);
            await f.Module.SetMetadataEnabledAsync(
                f.Module.MetadataPlatforms.Single(row => row.Platform == "wy"), true);
            Equal(2, f.Module.Lyrics.Count); Equal("SYNTHETIC_LYRIC", f.Module.Lyrics[0].Text);
            Equal(1, f.Peer.Calls); // Re-enable restores the exact saved display; it is not a refresh request.
            f.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native quality changes retain exact visible lyrics without another lookup", () => Sta(async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal(2, f.Module.Lyrics.Count); Equal(1, f.Peer.Calls);
            var published = f.Module.Lyrics[0];
            await f.Module.SetPlaybackQualityRequestAsync("128k");
            Equal(1, f.Peer.Calls);
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
                Check(f.Module.CanRemoveLocalLyrics); Equal(0, f.Runtime.Calls); Equal(0, f.Peer.Calls);
                Equal(0, f.Playback.Opens); Check((await File.ReadAllBytesAsync(selected)).SequenceEqual(original));
                var document = f.Access.Records.Root;
                string text = document.ToJsonString();
                Check(text.Contains("native-local-lyrics", StringComparison.Ordinal));
                Check(!text.Contains(selected, StringComparison.Ordinal) && !text.Contains("chosen.lrc", StringComparison.Ordinal));
            }
            await using var reopened = await Fixture.Create(root, seed: false);
            await reopened.Module.ActivateGlobalSurfaceAsync(
                MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail, default);
            Equal("", reopened.Module.LyricStatus); Equal("用户选择", reopened.Module.Lyrics[0].Text);
            Equal(0, reopened.Runtime.Calls); Equal(0, reopened.Peer.Calls); Equal(0, reopened.Playback.Opens);
            reopened.Dialogs.Accept = true;
            await DisableAllMetadata(reopened.Module);
            await reopened.Module.RestoreLyricsAsync(); // Manual local text grants no platform lookup.
            Equal("", reopened.Module.LyricStatus);
            await reopened.Module.RemoveLocalLyricsAsync();
            Check(!reopened.Module.CanRemoveLocalLyrics); Equal(0, reopened.Module.Lyrics.Count);
            Equal(0, reopened.Peer.Calls); Check((await File.ReadAllBytesAsync(selected)).SequenceEqual(original));
            reopened.Module.DeactivateGlobalSurface(MeowFilm.Desktop.DesktopGlobalSurface.PlayerDetail);
        }));
        yield return new("Native local lyric declined confirmation makes no durable mutation or metadata request", () => Sta(async () =>
        {
            string root = NewRoot(), selected = Path.Combine(root, "cancel.lrc");
            await File.WriteAllTextAsync(selected, "[00:01]not admitted");
            await using var f = await Fixture.Create(root);
            var before = f.Access.Records.Snapshot().Revision;
            f.Dialogs.LyricFile = selected; f.Dialogs.Accept = false;
            await f.Module.ImportLocalLyricsAsync();
            var after = f.Access.Records.Snapshot().Revision;
            Equal(before, after);
            Equal(0, f.Peer.Calls); Equal(0, f.Runtime.Calls); Check(!f.Module.CanRemoveLocalLyrics);
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
            Equal(0, next.Module.Lyrics.Count); Equal("暂无歌词", next.Module.LyricStatus);
            Equal(0, next.Peer.Calls); Equal(0, next.Runtime.Calls);
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
            Equal("暂无歌词", f.Module.LyricStatus); Equal(0, f.Peer.Calls); Equal(0, f.Runtime.Calls);
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
        string originalArtwork = f.Module.CurrentArtwork;
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
            Equal(duringSave ? originalArtwork : "", f.Module.CurrentArtwork);
            f.ReleaseSyncNotice();
            await f.SyncNoticeDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Ok(await f.SyncRound!);
            // The Module posts its real notification at Normal priority. This
            // barrier admits that refresh while the first receipt is still held.
            await MusicTestUi.Drain();
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
            Equal(0, f.Peer.Calls); Equal(0, f.Runtime.Calls); Equal(0, f.Playback.Opens);
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
    private static void Check(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    { if (!value) throw new InvalidOperationException("Lyric display assertion failed: " + expression); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static JsonElement Ok(JsonElement result)
    { if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetProperty("code").GetString()); return result; }
    private static void Code(JsonElement result, string code)
    { Check(!result.GetProperty("ok").GetBoolean()); Equal(code, result.GetProperty("code").GetString()); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string NewRoot()
    {
        string path = TestWorkspace.DataPath("lyric-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static async Task Sta(Func<Task> body)
    { await TestUiHost.InvokeAsync(body); }
    private sealed class LyricLookupApplication(IMusicApplication inner) : MusicApplicationForwarder(inner)
    {
        private readonly TaskCompletionSource releaseLookups = Signal();
        private int lookups;
        internal bool HoldLookups;
        internal readonly TaskCompletionSource SecondLookupEntered = Signal();
        internal void ReleaseLookups() => releaseLookups.TrySetResult();
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (method != "music.lyrics.lookup")
                return await Inner.ExecuteAsync(method, parameters, cancellationToken);
            if (HoldLookups) await releaseLookups.Task.WaitAsync(cancellationToken);
            var read = Inner.ExecuteAsync(method, parameters, cancellationToken);
            if (Interlocked.Increment(ref lookups) == 2) SecondLookupEntered.TrySetResult();
            return await read;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly NativeDatabase Store;
        internal readonly WriteFaultStore Access;
        internal readonly NativeTestDatabase Cache;
        internal readonly LyricRuntime Runtime;
        internal readonly LyricPeer Peer = new();
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
        private Fixture(string root, bool holdSync, Func<IMusicApplication, IMusicApplication>? decorate = null)
        {
            Runtime = new(root);
            Runtime.Platform.OnRequest = Peer.Read;
            ScriptPath = Path.Combine(root, "lyric-source.js");
            Store = new(root);
            Access = new(Store, root);
            Cache = new(Access);
            Application = new(Access, MusicTestStores.For(Access), Runtime);
            if (holdSync) Application.LibrarySyncChanged += HoldSyncNotice;
            Module = new(decorate?.Invoke(Application) ?? Application, Access, Playback, Dialogs);
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
            bool allowScripts = false, Func<IMusicApplication, IMusicApplication>? decorate = null)
        {
            var value = new Fixture(root ?? NewRoot(), holdSync, decorate);
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
            finally
            {
                try { await Module.DisposeAsync(); }
                finally
                {
                    try { await Runtime.DisposeAsync(); }
                    finally { await Cache.DisposeAsync(); await Access.Records.DisposeAsync(); await Store.DisposeAsync(); }
                }
            }
        }
    }
    private sealed class WriteFaultStore : NativeTestDatabase
    {
        internal readonly MusicRecordTestStore Records;
        internal bool FailAfterLibraryWrite;
        internal int LibraryWrites;
        internal Func<Task>? BeforeLibraryReceipt;
        internal WriteFaultStore(IStorageDatabase store, string recordsRoot) : base(store)
        {
            Records = MusicTestStores.Use(this, store.ProductRoot!);
            MusicTestStores.Attach(store, Records);
            Records.AfterCommit = async (_, _) =>
            {
                LibraryWrites++;
                if (BeforeLibraryReceipt is { } beforeReceipt) await beforeReceipt().ConfigureAwait(false);
                if (FailAfterLibraryWrite)
                { FailAfterLibraryWrite = false; throw new DocumentStoreException(DocumentStoreError.Unavailable); }
            };
        }
    }
    private sealed class LyricPeer
    {
        internal int Calls;
        internal bool Fail, Empty;
        internal string Text = "SYNTHETIC_LYRIC";
        internal Func<Task>? Block;
        internal async Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            Calls++;
            Check(call.Platform == "wy" && call.Operation == "lyrics");
            string text = Text;
            if (Block is { } block) await block(); // Deliberately ignore cancellation until actual completion.
            if (Fail) throw new MusicPlatformException("NETWORK_ERROR");
            return MusicPlatformRuntimeFixture.Json(new
            {
                text = Empty ? "" : "[00:01]" + text + "\n[00:20]second",
                translation = Empty ? "" : "[00:01]合成译文",
                romanization = ""
            });
        }
    }
    private sealed class LyricRuntime(string root) : IRuntimeClient, IAsyncDisposable
    {
        internal readonly MusicPlatformRuntimeFixture Platform = new();
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
            if (method.StartsWith("music.platform.", StringComparison.Ordinal))
                return Platform.CallAsync(method, parameters, cancellationToken);
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
                        sites = new { wy = new { actions = new[] { "lyric" }, qualitys = Array.Empty<string>() } },
                        capabilities = new { wy = new { actions = new[] { "lyric" }, qualitys = Array.Empty<string>() } },
                        check = (object?)null })!.AsObject();
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
        public ValueTask DisposeAsync() => Platform.DisposeAsync();
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
    private sealed class DisplayDisk : NativeTestDatabase
    {
        internal bool FailRead, FailWrite;
        internal DisplayDisk(IStorageDatabase storage) : base(storage) { }
        public override async Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default)
        {
            if (Disposals != 0) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (FailRead) throw new DocumentStoreException(DocumentStoreError.Storage);
            return await base.ReadAsync(read, token);
        }
        public override async Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
            int retainedBytes = 0, Action<Action>? commit = null)
        {
            if (Disposals != 0) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (FailWrite) throw new DocumentStoreException(DocumentStoreError.Storage);
            return await base.WriteAsync(write, token, retainedBytes, commit);
        }
    }
    private sealed class MemoryStore : MusicRecordTestStore
    {
    }
}
