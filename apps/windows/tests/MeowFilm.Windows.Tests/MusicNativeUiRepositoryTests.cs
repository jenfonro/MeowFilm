using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Windows.Tests;

// Keep the real database's UI-thread read boundary and the native application's
// repository notifications active; application forwarders bypass that path.
public static class MusicNativeUiRepositoryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("shell opens and revisits real music settings with SQLite without changing the queue", async () =>
        {
            await using var scene = await Scene.Create();
            var respond = scene.Runtime.OnRequest;
            scene.Runtime.OnRequest = (call, token) => call.Operation switch
            {
                "home" => Task.FromResult(MusicPlatformRuntimeFixture.Json(new
                    { platform = "wy", sections = Array.Empty<object>(), more = false, continuation = (object?)null })),
                "channels" => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { items = Array.Empty<object>() })),
                _ => respond(call, token)
            };
            await using var appearance = new AppAppearance(scene.Store);
            await appearance.LoadAsync();
            var shell = new ShellWindow([scene.Module], [], () => Task.CompletedTask,
                appearance: appearance);
            var root = DesktopTestUi.WindowRoot(shell);
            try
            {
                shell.Show();
                await Until(() => scene.Module.View.IsLoaded && scene.Module.CanEdit,
                    "the real music page must be ready before entering settings");
                var queue = await scene.Ok("music.queue.get");
                DesktopTestUi.Invoke((Button)root.FindName("SettingsButton"));
                var header = (DesktopPageHeader)root.FindName("PageHeaderHost");
                var slot = (ContentControl)header.FindName("LeadingSlot");
                await Until(() => slot.Content is Panel, "the shell must expose its settings categories");
                var categories = ((Panel)slot.Content).Children.OfType<RadioButton>().ToArray();
                var music = categories.Single(tab => Equals(tab.Content, "音乐"));
                var general = categories.Single(tab => Equals(tab.Content, "通用"));
                var view = (MusicSettingsView)scene.Module.SettingsView;
                Check(general.IsChecked == true && !DesktopVisual.IsVisible(view),
                    "opening general settings must not mount the music settings page");
                for (int visit = 0; visit < 2; visit++)
                {
                    DesktopTestUi.Invoke(music);
                    await Until(() => view.IsLoaded && DesktopVisual.IsVisible(view) && scene.Module.CanEditQuality &&
                        scene.Module.CanEditSearchPreferences && scene.Module.CanEditMetadata,
                        "the music category must mount and activate its real controls");
                    var anchors = ((Panel)view.FindName("SettingsNavigation")).Children.OfType<RadioButton>().ToArray();
                    Check(anchors.Select(anchor => anchor.Content).SequenceEqual(new[]
                        { "账号与平台", "搜索", "音质", "歌词与封面", "音源" }),
                        "the real music settings sections must be available");
                    Check(scene.Module.PlatformSettings.Count == 3 && scene.Module.PlatformSettings.All(row => !row.IsLoggedIn),
                        "the anonymous platform account rows must load without logging in");
                    foreach (var anchor in anchors)
                    {
                        DesktopTestUi.Invoke(anchor);
                        await MusicTestUi.Drain();
                        Check(DesktopVisual.IsVisible(view), "changing a music settings section must retain the page");
                    }
                    Check(DesktopVisual.IsVisible((FrameworkElement)view.FindName("SourcesPage")),
                        "source management must mount inside music settings");
                    DesktopTestUi.Invoke(anchors[0]);
                    await MusicTestUi.Drain();
                    Check(DesktopVisual.IsVisible((FrameworkElement)view.FindName("SettingsScroller")),
                        "returning to the account section must restore the continuous settings page");
                    Check(scene.Module.Status.Length == 0 && scene.Module.OwnedNotices.Count == 0 && scene.Playback.Opens == 0,
                        "opening and navigating settings must not report errors or start playback");
                    DesktopTestUi.Invoke(general);
                    await Until(() => !DesktopVisual.IsVisible(view), "general settings must retire the music page");
                }
                DesktopTestUi.Invoke((Button)root.FindName("BackButton"));
                await Until(() => DesktopVisual.IsVisible(scene.Module.View) && !DesktopVisual.IsVisible(view),
                    "Back must restore the original music page");
                CheckQueueUnchanged(queue, await scene.Ok("music.queue.get"));
                Check(scene.Runtime.Requests.All(call => call.Operation is "home" or "channels") && scene.Playback.Opens == 0,
                    "settings navigation must not resolve media or request account data");
            }
            finally
            {
                shell.Close();
                await shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(5));
                await scene.Module.DeactivateSettingsAsync();
                await MusicTestUi.Drain();
            }
        });
        yield return Case("real SQLite publishes catalog songs and previews on the UI dispatcher", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);

            Check(scene.Module.Status.Length == 0 && scene.Module.OwnedNotices.Count == 0,
                "a successful catalog response must not publish an error: " + scene.Module.Status);
            Check(scene.Module.CatalogSections.Count == 2 && scene.Module.Songs.Count == 1,
                "the successful catalog must publish both sections and its native song row");
            var card = scene.Module.CatalogSections.Single(section => section.Id == "songs").Items.Single();
            var ranking = scene.Module.CatalogSections.Single(section => section.Id == "rankings").Items.Single();
            Check(card.Song is { } song && Guid.TryParse(song.Id, out _) &&
                song.Title == "Original title" && song.Cover == Scene.OriginalCover,
                "catalog songs keep their registered identity and metadata");
            Check(ranking.PreviewSongs.Single().Song?.Id == card.Song!.Id &&
                ranking.FirstPreviewCover == Scene.OriginalCover,
                "ranking previews publish the same registered song");
            Check(scene.Runtime.Requests.Count == 1 && scene.Runtime.Requests[0].Operation == "channel",
                "publishing a complete catalog does not request more metadata");
            Check(SynchronizationContext.Current is not null,
                "the regression must run with the real UI synchronization context");
        });
        yield return Case("background metadata refresh updates current and cached catalog songs and previews", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            var cached = scene.Module.CatalogSections.ToArray();
            var cachedSong = cached.Single(section => section.Id == "songs").Items.Single();
            var cachedRanking = cached.Single(section => section.Id == "rankings").Items.Single();
            string id = cachedSong.Song!.Id;
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-b", default);
            var currentSong = scene.Module.CatalogSections.Single(section => section.Id == "songs").Items.Single();
            var currentRanking = scene.Module.CatalogSections.Single(section => section.Id == "rankings").Items.Single();
            Check(!ReferenceEquals(currentSong, cachedSong), "the second page owns a separate display snapshot");

            scene.Song = Scene.UpdatedSong();
            var refresh = await scene.Ok("music.catalog.song", new { platform = "wy", id = "repository-song", refresh = true });
            Check(refresh.GetProperty("song").GetProperty("songId").GetString() == id,
                "a background metadata read retains the permanent song identity");
            await Until(() => currentSong.Song!.Title == "Updated title" && currentRanking.FirstPreviewCover == Scene.UpdatedCover &&
                cachedSong.Song!.Title == "Updated title" && cachedRanking.FirstPreviewCover == Scene.UpdatedCover,
                "the visible and cached songs and previews did not receive the background repository update");
            CheckUpdated(currentSong, currentRanking, id);
            CheckUpdated(cachedSong, cachedRanking, id);
            int requests = scene.Runtime.Requests.Count;
            var receipt = (await scene.Library.ReadAsync("local")).Revision;

            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            Check(scene.Module.CatalogSections.SequenceEqual(cached, ReferenceEqualityComparer.Instance),
                "returning to a successful page restores its retained section and card identities");
            CheckUpdated(cachedSong, cachedRanking, id);
            Check(scene.Runtime.Requests.Count == requests && (await scene.Library.ReadAsync("local")).Revision == receipt,
                "cache restoration projects repository references without another request or metadata write");
            Check(scene.Module.Status.Length == 0 && scene.Module.OwnedNotices.Count == 0 && scene.Playback.Opens == 0,
                "background display metadata neither reports a format error nor starts playback");
        });
        yield return Case("superseded cached navigation keeps the visible page cache and queue intact", async () =>
        {
            await using var scene = await Scene.Create(seedQueue: true);
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            var original = scene.Module.CatalogSections.ToArray();
            var originalCard = original.Single(section => section.Id == "songs").Items.Single();
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-b", default);
            var otherCard = scene.Module.CatalogSections.Single(section => section.Id == "songs").Items.Single();
            Check(!ReferenceEquals(originalCard, otherCard), "the two cached pages must own different display cards");
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            var queue = await scene.Ok("music.queue.get");
            string track = scene.Module.CurrentTrackIdentity;
            int requests = scene.Runtime.Requests.Count;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var blocker = scene.Store.ReadAsync(_ =>
            {
                entered.TrySetResult();
                release.Wait();
                return true;
            });
            Task restore = Task.CompletedTask, navigate = Task.CompletedTask;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                restore = scene.Module.NavigateAsync("catalog-channel/wy/repository-b", default);
                await MusicTestUi.Drain();
                Check(!restore.IsCompleted && scene.Module.CurrentChannelKey == "wy/repository-a" &&
                    scene.Module.CatalogSections.SequenceEqual(original, ReferenceEqualityComparer.Instance) &&
                    scene.Runtime.Requests.Count == requests,
                    "cached navigation waits for its real repository read while the previous page remains visible");
                navigate = scene.Module.NavigateAsync("catalog-channel/wy/repository-c", default);
                release.Set();
                await Task.WhenAll(blocker, restore, navigate).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                release.Set();
                await Task.WhenAll(blocker, restore, navigate);
            }
            Check(scene.Module.CurrentChannelKey == "wy/repository-c" && scene.Runtime.Requests.Count == requests + 1,
                "the later navigation alone publishes and reads its uncached page");

            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            var returned = scene.Module.CatalogSections.Single(section => section.Id == "songs").Items.Single();
            Check(scene.Module.CurrentChannelKey == "wy/repository-a" &&
                scene.Module.CatalogSections.SequenceEqual(original, ReferenceEqualityComparer.Instance) &&
                ReferenceEquals(returned, originalCard) && !ReferenceEquals(returned, otherCard),
                "a superseded cache restore cannot save the destination page under the still-visible page's route");
            Check(scene.Runtime.Requests.Count == requests + 1 && scene.Module.CurrentTrackIdentity == track &&
                scene.Module.Status.Length == 0 && scene.Module.OwnedNotices.Count == 0 && scene.Playback.Opens == 0,
                "returning to the intact cache makes no extra request, error, playback or queue selection");
            CheckQueueUnchanged(queue, await scene.Ok("music.queue.get"));
        });
        yield return Case("player getters and source refresh use published rows on the UI dispatcher", async () =>
        {
            await using var scene = await Scene.Create(seedQueue: true);
            Check(scene.Module.HasCurrent && scene.Module.CurrentTitle == "Original title" &&
                scene.Module.CurrentArtist == "Original artist" && scene.Module.CurrentAlbum == "Original album" &&
                scene.Module.CurrentArtwork == Scene.OriginalCover && scene.Module.CurrentArtworkIdentity == scene.Module.CurrentSongId,
                "restoring a saved queue publishes all player display fields on the UI dispatcher");
            string track = scene.Module.CurrentTrackIdentity;
            var queue = await scene.Ok("music.queue.get");
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            scene.Module.SelectedSong = scene.Module.Songs.Single();
            Check(scene.Module.RecordingCandidates.Count == 1 && scene.Module.LyricSources.Count == 2,
                "recording and lyric source projections use the already published song");

            await scene.Module.RefreshSourceManagementAsync();
            await scene.Module.SetSourceEnabledAsync(scene.Module.Sources.Single(row => row.Platform == "tx"), false);
            await scene.Module.RefreshSourceManagementAsync();
            scene.Module.OpenSourceChoices();
            Check(scene.Module.Status.Length == 0 && scene.Module.OwnedNotices.Count == 0 &&
                !scene.Module.Sources.Single(row => row.Platform == "tx").Enabled &&
                scene.Module.CurrentTrackIdentity == track && scene.Module.CurrentTitle == "Original title" &&
                scene.Module.CurrentArtwork == Scene.OriginalCover && !scene.Module.IsPlaying &&
                !scene.Module.CanLoadMoreSources && scene.Module.SourceChoices.Count == 0 && scene.Playback.Opens == 0,
                "source refresh and player getters do not synchronously read SQLite or restart the restored queue");
            CheckQueueUnchanged(queue, await scene.Ok("music.queue.get"));
            Check(scene.Runtime.Requests.All(call => call.Operation == "channel"),
                "the preloaded player and source settings do not resolve audio or supplementary metadata");
        });
        yield return Case("a late older metadata response cannot roll back published catalog snapshots", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            var card = scene.Module.CatalogSections.Single(section => section.Id == "songs").Items.Single();
            var ranking = scene.Module.CatalogSections.Single(section => section.Id == "rankings").Items.Single();
            string id = card.Song!.Id;
            var older = new HeldReply(MusicSongFixture.Element(scene.Song));
            var respond = scene.Runtime.OnRequest;
            scene.Runtime.OnRequest = (call, token) => call.Operation == "song" &&
                call.Parameters.GetProperty("id").GetString() == "repository-older" ? older.Read(token) : respond(call, token);
            var pending = scene.App.ExecuteAsync("music.catalog.song",
                new { platform = "wy", id = "repository-older", refresh = true });
            try
            {
                await older.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                scene.Song = Scene.UpdatedSong();
                await scene.Ok("music.catalog.song", new { platform = "wy", id = "repository-song", refresh = true });
                await Until(() => card.Song!.Title == "Updated title" && ranking.FirstPreviewCover == Scene.UpdatedCover,
                    "the newer metadata response did not publish");
                var observed = new List<(string Title, string Cover)>();
                var song = card.Song!;
                var preview = ranking.PreviewSongs.Single().Song!;
                song.PropertyChanged += (_, _) => observed.Add((song.Title, song.Cover));
                preview.PropertyChanged += (_, _) => observed.Add((preview.Title, preview.Cover));
                older.Release.TrySetResult();
                var late = await pending;
                Check(late.GetProperty("ok").GetBoolean() &&
                    late.GetProperty("song").GetProperty("cover_url").GetString() == Scene.UpdatedCover,
                    "the older accepted request resolves against the newer committed song facts");
                await MusicTestUi.Drain();
                CheckUpdated(card, ranking, id);
                Check(observed.All(value => value.Title == "Updated title" && value.Cover == Scene.UpdatedCover),
                    "a delayed completion never republishes older song or preview metadata");
                var durable = await Task.Run(() => scene.Library.ReadSong(id));
                Check(durable!["cover_url"]!.GetValue<string>() == Scene.UpdatedCover &&
                    scene.Module.Status.Length == 0 && scene.Module.OwnedNotices.Count == 0,
                    "the durable repository and displayed snapshot retain the newest observation");
            }
            finally { older.Release.TrySetResult(); await pending; }
        });
        yield return Case("shutdown drains admitted metadata work without publishing its late response", async () =>
        {
            await using var scene = await Scene.Create();
            await scene.Module.NavigateAsync("catalog-channel/wy/repository-a", default);
            var card = scene.Module.CatalogSections.Single(section => section.Id == "songs").Items.Single();
            var ranking = scene.Module.CatalogSections.Single(section => section.Id == "rankings").Items.Single();
            var originalSong = card.Song!.Raw.Clone();
            var originalPreview = ranking.PreviewSongs.Single().Song!.Raw.Clone();
            var held = new HeldReply(MusicSongFixture.Element(Scene.UpdatedSong()));
            var respond = scene.Runtime.OnRequest;
            scene.Runtime.OnRequest = (call, token) => call.Operation == "song" ? held.Read(token) : respond(call, token);
            var pending = scene.App.ExecuteAsync("music.catalog.song",
                new { platform = "wy", id = "repository-song", refresh = true });
            try
            {
                await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var close = scene.Module.PrepareShutdownAsync();
                await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!pending.IsCompleted && !close.IsCompleted && scene.Runtime.Disposals == 0,
                    "shutdown retains a cancelled request until its physical peer has settled");
                var rejected = await scene.App.ExecuteAsync("music.queue.get");
                Check(!rejected.GetProperty("ok").GetBoolean() && rejected.GetProperty("code").GetString() == "CLOSED",
                    "shutdown seals new application requests while admitted work drains");
                held.Release.TrySetResult();
                var result = await pending;
                Check(!result.GetProperty("ok").GetBoolean() &&
                    result.GetProperty("code").GetString() is "CANCELLED" or "STALE_SESSION",
                    "a response arriving after shutdown cannot be admitted as fresh metadata");
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                await scene.Module.DisposeAsync();
                await MusicTestUi.Drain();
                Check(card.Title == "Catalog title" && card.Subtitle == "Catalog artist" && card.Cover == Scene.OriginalCover &&
                    JsonElement.DeepEquals(card.Song!.Raw, originalSong) &&
                    JsonElement.DeepEquals(ranking.PreviewSongs.Single().Song!.Raw, originalPreview) &&
                    card.Song!.Title == "Original title" && ranking.PreviewSongs.Single().Title == "Original title" &&
                    ranking.FirstPreviewCover == Scene.OriginalCover && scene.Playback.Opens == 0,
                    "the late response cannot mutate retired display snapshots or start playback");
                Check(scene.Runtime.Disposals == 0 &&
                    await scene.Store.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM music_songs")) == 1,
                    "the module releases its owners without disposing borrowed runtime or App database");
            }
            finally { held.Release.TrySetResult(); await pending; }
        });
    }

    private static TestCase Case(string name, Func<Task> run) =>
        new("music-native-ui-repository: " + name, () => TestUiHost.InvokeAsync(run));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    private static void CheckUpdated(MusicCatalogCard card, MusicCatalogCard ranking, string id)
    {
        var song = card.Song!;
        var preview = ranking.PreviewSongs.Single();
        var previewSong = preview.Song!;
        Check(song.Id == id && song.Title == "Updated title" && song.Artist == "Updated artist" &&
            song.Album == "Updated album" && song.Duration == "1:43" && song.Cover == Scene.UpdatedCover &&
            previewSong.Id == id && preview.Title == song.Title && preview.Artist == song.Artist &&
            ranking.FirstPreviewCover == song.Cover && JsonElement.DeepEquals(previewSong.Raw, song.Raw),
            "catalog song and ranking preview must project the same current permanent metadata");
        Check(card.Title == "Catalog title" && card.Subtitle == "Catalog artist" && card.Cover == Scene.OriginalCover,
            "repository updates must preserve the catalog card's independent display text and artwork");
    }

    private static void CheckQueueUnchanged(JsonElement before, JsonElement after) =>
        Check(after.GetProperty("version").GetInt64() == before.GetProperty("version").GetInt64() &&
            after.GetProperty("currentId").GetString() == before.GetProperty("currentId").GetString() &&
            after.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString())
                .SequenceEqual(before.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString())),
            "display and source refreshes retain queue occurrences, selected entry and version");

    private sealed class HeldReply(JsonElement value)
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<JsonElement> Read(CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            return value;
        }
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal const string OriginalCover = "https://images.example.test/repository-original.png";
        internal const string UpdatedCover = "https://images.example.test/repository-updated.png";
        internal readonly NativeDatabase Store;
        internal readonly MusicLibraryDatabase Library;
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        internal readonly Output Playback = new();
        internal JsonObject Song = MusicSongFixture.Online("wy", "repository-song", "Original title",
            "Original artist", "Original album", 100, OriginalCover);

        private Scene()
        {
            string root = TestWorkspace.DataPath("music-native-ui-repository-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Store = new(root);
            Library = new(Store);
            Runtime.OnRequest = Read;
            Runtime.OnOtherCall = (call, _) => call.Method == "music.script.sources.list"
                ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { ok = true, contract = "music.script.v3",
                    value = new { items = Array.Empty<object>() } }))
                : throw new InvalidOperationException("Unexpected script operation: " + call.Method);
            App = new(Store, Library, Runtime);
            Module = new(App, Store, Playback);
        }

        internal static JsonObject UpdatedSong() => MusicSongFixture.Online("wy", "repository-song", "Updated title",
            "Updated artist", "Updated album", 103, UpdatedCover);

        internal async Task<JsonElement> Ok(string method, object? parameters = null)
        {
            var reply = await App.ExecuteAsync(method, parameters);
            Check(reply.GetProperty("ok").GetBoolean(), method + ": " + reply.GetRawText());
            return reply;
        }

        internal static async Task<Scene> Create(bool seedQueue = false)
        {
            var result = new Scene();
            try
            {
                await result.Store.Ready;
                if (seedQueue)
                {
                    var song = await MusicSongFixture.AdoptAsync(result.App, result.Song);
                    await result.Ok("music.queue.replace", new { songs = new[] { song, song }, index = 1 });
                }
                await result.Module.InitializeAsync(default);
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }

        private Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (call.Operation == "song") return Task.FromResult(MusicPlatformRuntimeFixture.Json(Song));
            if (call.Operation != "channel")
                throw new InvalidOperationException("Unexpected platform operation: " + call.Operation);
            var sections = new object[]
            {
                new { id = "songs", title = "Songs", layout = "song-list", items = new[]
                {
                    new { id = "song-card", kind = "song", title = "Catalog title", subtitle = "Catalog artist",
                        cover = OriginalCover, song = Song, action = new { type = "song", id = "repository-song" } }
                } },
                new { id = "rankings", title = "Rankings", layout = "ranked-songs", items = new[]
                {
                    new { id = "ranking", kind = "playlist", title = "Ranking", subtitle = "", cover = OriginalCover,
                        preview = new[] { new { song = Song, rank = 1 } },
                        action = new { type = "playlist", id = "ranking" } }
                } }
            };
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(new
                { platform = "wy", sections, more = false, continuation = (object?)null }));
        }

        public async ValueTask DisposeAsync()
        {
            try { await Module.DisposeAsync(); }
            finally
            {
                try { await Runtime.DisposeAsync(); }
                finally { try { await Library.DisposeAsync(); } finally { await Store.DisposeAsync(); } }
            }
        }
    }

    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => HasMedia;
        public double PositionSeconds => 0;
        public double DurationSeconds => 100;
        public int Volume { get; set; } = 80;
        public int Opens { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OwnerId = owner; Identity = identity; HasMedia = true; Opens++;
            return Task.CompletedTask;
        }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { HasMedia = false; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
}
