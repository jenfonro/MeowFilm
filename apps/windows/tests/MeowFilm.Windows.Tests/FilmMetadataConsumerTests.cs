using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmMetadataConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.MetadataConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> MetadataConsumerCases()
    {
        yield return Case("TMDB save retires search only after success and same-word playback uses a fresh session", MetadataSaveConsumer);
        yield return Case("TMDB save cancels old details without allowing late data to replace new query metadata", MetadataLateConsumer);
        yield return Case("TMDB admitted settings save drains before close and never restores a revoked search", MetadataSaveDrain);
        yield return Case("native TMDB and Douban settings work offline with CAS failures and block notifications", MetadataNativeOwner);
        yield return Case("native metadata saves preserve canonical text endpoints and proxy validation offline", MetadataNativeNormalization);
        yield return Case("native metadata patches replace invalid old fields and validate the complete final DTO before commit", MetadataPatchConsumption);
    }
    private static void MetadataSearch(FilmSearchConsumerRuntime runtime)
    {
        var details = new Dictionary<Guid, JsonElement>();
        runtime.SearchOpen = (id, input) =>
        {
            details[id] = MetadataRuntime.Detail("详情" + runtime.SearchOpens);
            return SearchFixture.Snapshot(id, FilmJson.Text(input, "query"), [SearchFixture.Card("tmdb:tv:7", "tmdb", "目标", [])], []);
        };
        runtime.SearchHandoff = input =>
        {
            var id = Guid.Parse(FilmJson.Text(input, "sessionId"));
            return SearchFixture.Handoff(id, FilmJson.Text(input, "selectionId"), FilmJson.Text(runtime.SearchValue(id), "query"),
                new("tmdb:tv:7", "tv", "目标", "tmdb", "7"), [], metadata: details[id]);
        };
    }
    private static async Task MetadataSaveConsumer()
    {
        using var settings = new MetadataRuntime();
        await using var store = new FilmNativeSettingsFixture();
        var metadata = new FilmMetadataService(settings, store);
        using var runtime = new FilmSearchConsumerRuntime(); MetadataSearch(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata, runtime: runtime);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        var oldSession = scene.Film.Search.Current!;
        var oldCard = scene.Page.Cards.Single();
        var cardSnapshot = oldCard.SearchContext.Item.Clone();
        await scene.Page.OpenCardAsync(scene.Page.Cards.Single());
        var oldPlayback = scene.Film.Playback!;
        store.WriteError = DocumentStoreError.Storage;
        await MetadataStoreError(DocumentStoreError.Storage, () => metadata.SaveSettingsAsync(new Dictionary<string, object?>()));
        True(ReferenceEquals(scene.Film.Search.Current, oldSession), "failed settings receipt cannot revoke an effective search");
        store.WriteError = null;
        await metadata.SaveSettingsAsync(new Dictionary<string, object?>());
        True(scene.Film.Search.Current is null && oldSession.Retired &&
            ReferenceEquals(scene.Page.Cards.Single(), oldCard) && oldCard.Id == "tmdb:tv:7" && oldCard.Title == "目标" &&
            ReferenceEquals(oldCard.SearchContext.Session, oldSession) &&
            JsonElement.DeepEquals(oldCard.SearchContext.Item, cardSnapshot) &&
            oldCard.SearchContext.PlaybackRequest.Target == new FilmPlaybackTarget("tmdb:tv:7", "tv", "目标", "tmdb", "7") &&
            runtime.SearchOpens == 1,
            "successful save retires execution while retaining the exact successful card and target without another search");
        Equal("详情1", oldPlayback.Overview);
        scene.Click("BackButton"); await SearchFixture.Until(() => scene.SearchHost is not null && scene.Status == "");
        await SearchFixture.Settle(scene.Film.Search.Current!);
        var current = scene.Film.Search.Current!;
        True(!ReferenceEquals(current, oldSession) && current.Query == oldSession.Query && runtime.SearchOpens == 2,
            "explicit same-word return asks Node for a new session after invalidation");
        await scene.Page.OpenCardAsync(scene.Page.Cards.Single());
        True(!ReferenceEquals(oldPlayback, scene.Film.Playback), "the new opaque session creates a different playback selection");
        Equal("详情2", scene.Film.Playback!.Overview); Equal("详情1", oldPlayback.Overview);
    }
    private static async Task MetadataLateConsumer()
    {
        using var settings = new MetadataRuntime();
        await using var store = new FilmNativeSettingsFixture();
        var metadata = new FilmMetadataService(settings, store);
        using var runtime = new FilmSearchConsumerRuntime(); MetadataSearch(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata, runtime: runtime);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        var previous = scene.Film.Search.Current!;
        await scene.Page.OpenCardAsync(scene.Page.Cards.Single()); var retained = scene.Film.Playback!;
        await metadata.SaveSettingsAsync(new Dictionary<string, object?>());
        True(scene.Film.Search.Current is null && previous.Retired && runtime.SearchOpens == 1,
            "save cancels old UI publication without automatically requesting new content");
        scene.Click("BackButton"); await SearchFixture.Until(() => scene.SearchHost is not null && scene.Status == "");
        await SearchFixture.Settle(scene.Film.Search.Current!);
        var refreshed = scene.Film.Search.Current!;
        runtime.PublishSearch(SearchFixture.Snapshot(Guid.Parse(previous.Id), previous.Query,
            [SearchFixture.Card("tmdb:tv:7", "tmdb", "迟到旧详情", [])], [], revision: 99));
        await Drain();
        True(scene.Page.Cards.Single().Title == "目标" && refreshed.Revision < 99 && previous.Revision < 99,
            "late revoked metadata cannot replace the new query or the retained old snapshot");
        Equal("详情1", retained.Overview);
        await scene.Page.OpenCardAsync(scene.Page.Cards.Single()); Equal("详情2", scene.Film.Playback!.Overview);
    }
    private static async Task MetadataSaveDrain()
    {
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var settings = new MetadataRuntime();
        await using var store = new FilmNativeSettingsFixture();
        var metadata = new FilmMetadataService(settings, store);
        store.Writing = (_) => receipt.Task;
        using var runtime = new FilmSearchConsumerRuntime(); MetadataSearch(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata, runtime: runtime);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        var oldSession = scene.Film.Search.Current!;
        var oldCard = scene.Page.Cards.Single();
        var cardSnapshot = oldCard.SearchContext.Item.Clone();
        using var caller = new CancellationTokenSource();
        var save = metadata.SaveSettingsAsync(new Dictionary<string, object?>(), caller.Token);
        caller.Cancel();
        var close = scene.Film.PrepareShutdownAsync();
        try
        {
            True(!save.IsCompleted && !close.IsCompleted && !store.WriteToken.CanBeCanceled,
                "caller cancellation and close drain an admitted settings write without abandoning its receipt");
            receipt.SetResult(); await save; await close;
            True(scene.Film.Search.Current is null && oldSession.Retired &&
                ReferenceEquals(scene.Page.Cards.Single(), oldCard) && oldCard.Id == "tmdb:tv:7" && oldCard.Title == "目标" &&
                ReferenceEquals(oldCard.SearchContext.Session, oldSession) &&
                JsonElement.DeepEquals(oldCard.SearchContext.Item, cardSnapshot) &&
                oldCard.SearchContext.PlaybackRequest.Target == new FilmPlaybackTarget("tmdb:tv:7", "tv", "目标", "tmdb", "7"),
                "successful save retires the old search during shutdown without clearing its exact successful card and target");
            await scene.Film.DisposeAsync();
            True(scene.Film.Search.TrackedSessions == 0 && runtime.SearchOpens == 1, "release retires search without reopening it");
        }
        finally { receipt.TrySetResult(); }
    }
    private static async Task MetadataNativeOwner()
    {
        using var runtime = new MetadataRuntime { Offline = true };
        await using var store = new FilmNativeSettingsFixture();
        await using var metadata = new FilmMetadataService(runtime, store);
        var original = await metadata.GetSettingsAsync();
        var douban = await metadata.GetDoubanSettingsAsync();
        await metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["language"] = "ja-JP", ["includeAdult"] = true });
        await metadata.SaveDoubanSettingsAsync(new Dictionary<string, object?> { ["apiBase"] = "https://douban.fixture.invalid" });
        True((await metadata.GetSettingsAsync()).Language == "ja-JP" && (await metadata.GetDoubanSettingsAsync()).ApiBase == "https://douban.fixture.invalid" &&
            runtime.Notifications.SequenceEqual(["tmdb", "douban"]) && store.Value("film.tmdb.v1").GetProperty("version").GetInt32() == 1 &&
            store.Value("film.douban.v1").GetProperty("settings").GetProperty("cookie").GetString() == douban.Cookie,
            "ordinary settings commit independent version-one documents and notify only the changed block while offline");
        foreach (var error in new[] { DocumentStoreError.Conflict, DocumentStoreError.Storage })
        {
            store.WriteError = error;
            await MetadataStoreError(error, () => metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["language"] = "en-US" }));
            True(metadata.Settings!.Language == "ja-JP" && runtime.Notifications.Count == 2,
                "failed native CAS neither publishes settings nor raises a notification");
        }
        store.WriteError = null;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int writes = store.WritesFor("film.tmdb.v1");
        try { await metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["language"] = "en-US" }, cancelled.Token); throw new Exception("Cancelled edit wrote"); }
        catch (OperationCanceledException) { }
        await BridgeTests.Expect("INVALID_REQUEST", () => metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["includeAdult"] = "true" }));
        True(store.WritesFor("film.tmdb.v1") == writes && metadata.Settings!.Language != original.Language, "cancelled and invalid edits do not replace the committed snapshot");
        await BridgeTests.Expect("CLOSED", () => Read(new
        { provider = "tmdb", path = "/3/search/multi", query = new { query = "离线", page = 1 }, imageSize = "w500" }));
        await BridgeTests.Expect("CLOSED", () => Read(new
        { provider = "tmdb", path = "/3/tv/7", query = new { append_to_response = "credits,similar,images" }, imageSize = "original" }));
        True(runtime.Captures == 2, "explicit metadata content fails at the offline connection without changing native settings");
        await using var reopened = new FilmMetadataService(runtime, store);
        Equal("ja-JP", (await reopened.GetSettingsAsync()).Language);
        Equal("https://douban.fixture.invalid", (await reopened.GetDoubanSettingsAsync()).ApiBase);
        store.Seed("film.tmdb.v1", new { version = 2 });
        await MetadataStoreError(DocumentStoreError.Corrupt, () => metadata.GetSettingsAsync());
        await MetadataStoreError(DocumentStoreError.Corrupt, () => metadata.SaveSettingsAsync(new Dictionary<string, object?>()));
        True(store.WritesFor("film.tmdb.v1") == writes && runtime.Notifications.Count == 2, "a corrupt document is never repaired by blind overwrite or runtime fallback");
        async Task Read(object input)
        {
            var owner = await runtime.CaptureConnectionAsync();
            await new FilmPlaybackClient(owner.Client).CallAsync("data.metadata", input, owner.Revoked);
        }
    }
    private static async Task MetadataNativeNormalization()
    {
        using var runtime = new MetadataRuntime { Offline = true };
        await using var store = new FilmNativeSettingsFixture();
        await using var metadata = new FilmMetadataService(runtime, store);
        var saved = await metadata.SaveSettingsAsync(new Dictionary<string, object?>
        {
            ["apiBase"] = "", ["imageBase"] = "HTTPS://IMAGE.FIXTURE.INVALID:443/root///",
            ["apiKey"] = "   ", ["accessToken"] = "\ufeff  synthetic-token \u00a0", ["language"] = " ja-JP ", ["region"] = " JP "
        });
        True(saved.ApiBase == "https://api.themoviedb.org/3" && saved.ImageBase == "https://image.fixture.invalid/root" &&
            saved.ApiKey == "" && saved.AccessToken == "synthetic-token" && saved.Language == "ja-JP" && saved.Region == "JP",
            "the native owner stores the same canonical values as the runtime without calling Node");
        var douban = await metadata.SaveDoubanSettingsAsync(new Dictionary<string, object?>
        {
            ["apiBase"] = "https://douban.fixture.invalid/", ["cookie"] = "  \u0085synthetic=fixture\u0085  ",
            ["apiProxy"] = "HTTPS://relay.fixture.invalid/?url={encodedUrl}", ["imageProxy"] = ""
        });
        True(douban.ApiBase == "https://douban.fixture.invalid" && douban.Cookie == "\u0085synthetic=fixture\u0085" &&
            douban.ApiProxy == "https://relay.fixture.invalid/?url={encodedUrl}" && douban.ImageProxy == "" &&
            (await metadata.GetSettingsAsync()) == saved && (await metadata.GetDoubanSettingsAsync()) == douban,
            "text and ordinary endpoints normalize while the validated proxy template keeps its exact replacement slot");
        int writes = store.WritesFor("film.douban.v1");
        foreach (var patch in new Dictionary<string, object?>[]
        {
            new() { ["apiBase"] = "" },
            new() { ["apiBase"] = "https://fixture.invalid/?" },
            new() { ["imageBase"] = "https://fixture.invalid/#" },
            new() { ["apiProxy"] = "https://{url}/" },
            new() { ["apiProxy"] = "https://fixture.invalid/?a={url}&b={encodedUrl}" },
            new() { ["imageProxy"] = "https://fixture.invalid/{unknown}" },
            new() { ["cookie"] = "synthetic\r\ninvalid" }
        })
            await BridgeTests.Expect("INVALID_REQUEST", () => metadata.SaveDoubanSettingsAsync(patch));
        True(store.WritesFor("film.douban.v1") == writes && (await metadata.GetDoubanSettingsAsync()) == douban &&
            runtime.Notifications.SequenceEqual(["tmdb", "douban"]) && runtime.Captures == 0,
            "invalid drafts cannot write notify or replace accepted local settings while offline");
    }
    private static async Task MetadataPatchConsumption()
    {
        using var runtime = new MetadataRuntime { Offline = true };
        await using var store = new FilmNativeSettingsFixture();
        await using var metadata = new FilmMetadataService(runtime, store);
        await metadata.SaveSettingsAsync(new Dictionary<string, object?>());
        await metadata.SaveDoubanSettingsAsync(new Dictionary<string, object?>());
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_tmdb_settings SET language=NULL,null_mask=null_mask | 16"));
        var saved = await metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["language"] = " ja-JP " });
        True(saved.Language == "ja-JP", "a valid new language replaces the discarded null field");
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_douban_settings SET cookie=NULL,null_mask=null_mask | 32"));
        var douban = await metadata.SaveDoubanSettingsAsync(new Dictionary<string, object?> { ["cookie"] = "" });
        True(douban.Cookie == "", "a valid cookie clear does not require the old cookie to be consumable");
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_tmdb_settings SET region=NULL,null_mask=null_mask | 32"));
        int notifications = runtime.Notifications.Count;
        var before = store.Value("film.tmdb.v1");
        await MetadataStoreError(DocumentStoreError.Corrupt, () => metadata.SaveSettingsAsync(
            new Dictionary<string, object?> { ["language"] = "en-US" }));
        True(JsonElement.DeepEquals(before, store.Value("film.tmdb.v1")) && metadata.Settings == saved &&
            runtime.Notifications.Count == notifications,
            "an unpatched final DTO field fails before committing or publishing the otherwise valid edit");
        saved = await metadata.SaveSettingsAsync(new Dictionary<string, object?> { ["region"] = "JP", ["unused"] = new { ignored = true } });
        True(saved.Region == "JP" && saved.Language == "ja-JP" && runtime.Captures == 0,
            "the complete final projection is returned after repairing its selected field and ignoring unused input");
    }
    private static async Task MetadataStoreError(DocumentStoreError code, Func<Task> operation)
    {
        try { await operation(); throw new Exception("Expected native storage error"); }
        catch (DocumentStoreException error) { True(error.Error == code, "native failure code is preserved"); }
    }
    private sealed class MetadataRuntime : IRuntimeClient, IRuntimeConnectionSource, IRuntimeSettingsNotifications, IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly RuntimeConnection connection;
        internal bool Offline;
        internal int Captures;
        internal readonly List<string> Notifications = [];
        internal MetadataRuntime() => connection = new(this, new Uri("http://127.0.0.1:1/"), lifetime.Token);
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            Captures++;
            if (Offline) throw new BridgeException("CLOSED");
            return Task.FromResult(connection);
        }
        public void SettingsChanged(string block) => Notifications.Add(block);
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Settings must use native documents; content belongs to Node search/playback sessions.");
        internal static JsonElement Detail(string overview) => SearchFixture.Json(new
        {
            id = 7, name = "目标", overview, poster_path = "/v1/tmdb/images/original/poster.png",
            businessId = "tmdb:tv:7", playbackTarget = new FilmPlaybackTarget("tmdb:tv:7", "tv", "目标", "tmdb", "7").Wire(),
            marker = "完整详情原响应", raw = new { unknown = "  原始详情字段  " },
            genres = new[] { new { id = 18, name = "剧情" } },
            seasons = new[] { new { season_number = 1, episode_count = 3, air_date = "2023-01-01",
                businessId = "tmdb:tv:7:season:1", playbackTarget = new FilmPlaybackTarget("tmdb:tv:7", "tv", "目标", "tmdb", "7", Season: 1).Wire() } },
            credits = new { cast = new[] { new { id = 19, name = "演员" } }, crew = Array.Empty<object>() },
            similar = new { results = new[] { new { id = 8, name = "相关作品", businessId = "tmdb:tv:8",
                playbackTarget = new FilmPlaybackTarget("tmdb:tv:8", "tv", "相关作品", "tmdb", "8").Wire() } } },
            images = new { posters = new[] { new { file_path = "/v1/tmdb/images/original/poster.png" } } }
        });
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Metadata does not upload source.");
        public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
    }
}
