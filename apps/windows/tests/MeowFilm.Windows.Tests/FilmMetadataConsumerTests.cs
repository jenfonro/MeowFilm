using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

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
        yield return Case("metadata content captures its exact producer and never invents native detail cache receipts", MetadataProducerConsumer);
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
        var metadata = new FilmMetadataService(settings);
        using var runtime = new FilmSearchConsumerRuntime(); MetadataSearch(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata, runtime: runtime);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        var oldSession = scene.Film.Search.Current!;
        await scene.Page.OpenCardAsync(scene.Page.Cards.Single());
        var oldPlayback = scene.Film.Playback!;
        settings.FailSave = true;
        await BridgeTests.Expect("STORAGE_ERROR", () => metadata.SaveSettingsAsync(new Dictionary<string, object?>()));
        True(ReferenceEquals(scene.Film.Search.Current, oldSession), "failed settings receipt cannot revoke an effective search");
        settings.FailSave = false;
        await metadata.SaveSettingsAsync(new Dictionary<string, object?>());
        True(scene.Film.Search.Current is null && scene.Page.Cards.Count == 0 && runtime.SearchOpens == 1,
            "successful save revokes display without starting another search");
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
        var metadata = new FilmMetadataService(settings);
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
        using var settings = new MetadataRuntime { HeldSave = receipt.Task };
        var metadata = new FilmMetadataService(settings);
        using var runtime = new FilmSearchConsumerRuntime(); MetadataSearch(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata, runtime: runtime);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        using var caller = new CancellationTokenSource();
        var save = metadata.SaveSettingsAsync(new Dictionary<string, object?>(), caller.Token);
        caller.Cancel();
        var close = scene.Film.PrepareShutdownAsync();
        try
        {
            True(!save.IsCompleted && !close.IsCompleted && !settings.SaveToken.IsCancellationRequested,
                "caller cancellation and close drain an admitted settings write without abandoning its receipt");
            receipt.SetResult(); await save; await close;
            True(scene.Film.Search.Current is null && scene.Page.Cards.Count == 0, "successful save revokes the old search during shutdown");
            await scene.Film.CancelShutdownPreparationAsync();
            True(scene.Film.Search.Current is null && runtime.SearchOpens == 1, "shutdown cancellation does not restore a revoked search");
            await scene.Submit("最终关闭", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
            await scene.Page.OpenCardAsync(scene.Page.Cards.Single()); var retained = scene.Film.Playback!;
            string snapshot = retained.Request.Metadata!.Value.GetRawText();
            var finalReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settings.HeldSave = finalReceipt.Task;
            var finalSave = metadata.SaveSettingsAsync(new Dictionary<string, object?>());
            var abort = scene.Film.AbortAsync();
            try
            {
                True(!abort.IsCompleted && !finalSave.IsCompleted, "forced close still waits for its admitted settings write");
                finalReceipt.SetResult(); await finalSave; await abort;
                Equal(snapshot, retained.Request.Metadata!.Value.GetRawText());
                True(scene.Film.Search.TrackedSessions == 0 && retained.SearchContext!.Query == "最终关闭",
                    "final disposal releases search ownership but preserves retained display data");
            }
            finally { finalReceipt.TrySetResult(); }
        }
        finally { receipt.TrySetResult(); }
    }
    private static async Task MetadataProducerConsumer()
    {
        using var runtime = new MetadataRuntime();
        await using var metadata = new FilmMetadataService(runtime);
        IFilmSearchMetadata consumer = metadata;
        var search = await consumer.SearchAsync("原查询 2 ", default);
        var detail = await consumer.DetailAsync("tv", 7, default);
        True(search.Owner is { } searchOwner && detail.Owner is { } detailOwner &&
            ReferenceEquals(searchOwner.Client, runtime.Producer) && ReferenceEquals(detailOwner.Client, runtime.Producer) &&
            !ReferenceEquals(runtime.Producer, runtime) && searchOwner.Revoked.CanBeCanceled &&
            searchOwner.Revoked == detailOwner.Revoked && !searchOwner.Revoked.IsCancellationRequested &&
            search.CacheEntry is null && detail.CacheEntry is null,
            "content and optional receipt use the actual captured producer, not the settings facade or fabricated native freshness");
        Equal("/v1/tmdb/images/w500/poster.png", search.Value.GetProperty("results")[0].GetProperty("poster_path").GetString());
        Equal("/v1/tmdb/images/original/poster.png", detail.Value.GetProperty("poster_path").GetString());
        Equal("  原始搜索字段  ", search.Value.GetProperty("raw").GetProperty("unknown").GetString());
        Equal("  原始详情字段  ", detail.Value.GetProperty("raw").GetProperty("unknown").GetString());
        True(detail.Value.GetProperty("genres").GetArrayLength() == 1 && detail.Value.GetProperty("seasons").GetArrayLength() == 1 &&
            detail.Value.GetProperty("credits").GetProperty("cast").GetArrayLength() == 1 &&
            detail.Value.GetProperty("similar").GetProperty("results").GetArrayLength() == 1 &&
            detail.Value.GetProperty("images").GetProperty("posters").GetArrayLength() == 1,
            "the native adapter preserves complete original detail and every requested appended object");
        var repeated = await consumer.DetailAsync("tv", 7, default);
        Equal("详情2", repeated.Value.GetProperty("overview").GetString());
        True(runtime.Searches == 1 && runtime.Details == 2 && runtime.CacheReads == 3 && repeated.CacheEntry is null,
            "each new native read reaches the captured Node interface; only the real runtime tests assert upstream cache reuse");
        var held = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.HeldDetail = held.Task;
        try
        {
            var first = consumer.DetailAsync("tv", 7, default);
            var second = consumer.DetailAsync("tv", 7, default);
            await SearchFixture.Until(() => runtime.Details == 4);
            True(!first.IsCompleted && !second.IsCompleted && runtime.CacheReads == 3,
                "both same-key native calls reach the Node producer before either body or receipt is available; no second native flight owner");
            held.SetResult(MetadataRuntime.Detail("同一producer并发详情"));
            var completed = await Task.WhenAll(first, second);
            True(completed.All(value => value.CacheEntry is null && ReferenceEquals(value.Owner?.Client, runtime.Producer)) &&
                runtime.CacheReads == 5, "both native waiters retain the same captured producer without inventing a shared cache receipt");
        }
        finally { held.TrySetResult(MetadataRuntime.Detail("同一producer并发详情")); }
    }
    private sealed class MetadataRuntime : IRuntimeClient, IRuntimeConnectionSource, IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly RuntimeConnection connection;
        internal readonly IRuntimeClient Producer;
        internal int Searches, Details, CacheReads;
        internal bool FailSave;
        internal Task<JsonElement>? HeldDetail;
        internal CancellationToken DetailToken;
        internal Task? HeldSave;
        internal CancellationToken SaveToken;
        internal MetadataRuntime()
        {
            Producer = new MetadataProducer(this);
            connection = new(Producer, new Uri("http://127.0.0.1:1/"), lifetime.Token);
        }
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            return Task.FromResult(connection);
        }
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            True(method is "tmdb.data.settings.get" or "tmdb.data.settings.update",
                "the App facade is only for settings; content must use its captured producer");
            if (method == "tmdb.data.settings.update" && FailSave)
                return JsonSerializer.SerializeToElement(new { ok = false, contract = "tmdb.data.v1", code = "STORAGE_ERROR" });
            if (method == "tmdb.data.settings.update")
            {
                SaveToken = cancellationToken;
                if (HeldSave is { } save) await save;
            }
            return JsonSerializer.SerializeToElement(new { ok = true, contract = "tmdb.data.v1", value = new
            { apiBase = "https://api.fixture.invalid", imageBase = "https://image.fixture.invalid/t/p",
                apiKey = "", accessToken = "", language = "zh-CN", region = "CN", includeAdult = false } });
        }
        private async Task<JsonElement> ContentAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            True(method is "film.data.metadata" or "film.data.cache.entry", "metadata content uses only the formal film operations");
            var request = JsonSerializer.SerializeToElement(parameters);
            bool receipt = method == "film.data.cache.entry";
            True(request.ValueKind == JsonValueKind.Object && request.EnumerateObject().Count() == (receipt ? 5 : 4) &&
                request.GetProperty("provider").GetString() == "tmdb" &&
                (!receipt || request.GetProperty("kind").GetString() == "metadata"),
                "content and cache identity retain the exact formal provider/shape");
            string? path = request.GetProperty("path").GetString();
            var query = request.GetProperty("query");
            if (path == "/3/search/multi")
            {
                True(query.EnumerateObject().Count() == 2 && query.GetProperty("query").ValueKind == JsonValueKind.String &&
                    query.GetProperty("page").GetInt32() == 1 &&
                    request.GetProperty("imageSize").GetString() == "w500", "search uses its original query, first page and w500 identity; configured language belongs to Node");
                if (receipt) { Interlocked.Increment(ref CacheReads); return Success(null); }
                Interlocked.Increment(ref Searches);
                return Success(new
                {
                    marker = "完整搜索原响应", raw = new { unknown = "  原始搜索字段  " }, results = new[]
                    {
                        new { media_type = "tv", id = 7, name = "目标", title = "目标", overview = "已得搜索简介",
                            first_air_date = "2023-01-01", release_date = "2023-01-01", poster_path = "/v1/tmdb/images/w500/poster.png" }
                    }
                });
            }
            True(path == "/3/tv/7" && query.EnumerateObject().Count() == 1 &&
                query.GetProperty("append_to_response").GetString() == "credits,similar,images" &&
                request.GetProperty("imageSize").GetString() == "original", "full detail uses its exact type/ID, append set and original image identity");
            if (receipt) { Interlocked.Increment(ref CacheReads); return Success(null); }
            int number = Interlocked.Increment(ref Details); DetailToken = cancellationToken;
            // Deliberately return an already admitted late body even after its
            // UI waiter leaves; the production consumer must reject publication.
            return Success(HeldDetail is { } held ? await held : Detail("详情" + number));
        }
        internal static JsonElement Detail(string overview) => SearchFixture.Json(new
        {
            id = 7, name = "目标", overview, poster_path = "/v1/tmdb/images/original/poster.png",
            marker = "完整详情原响应", raw = new { unknown = "  原始详情字段  " },
            genres = new[] { new { id = 18, name = "剧情" } },
            seasons = new[] { new { season_number = 1, episode_count = 3, air_date = "2023-01-01" } },
            credits = new { cast = new[] { new { id = 19, name = "演员" } }, crew = Array.Empty<object>() },
            similar = new { results = new[] { new { id = 8, name = "相关作品" } } },
            images = new { posters = new[] { new { file_path = "/v1/tmdb/images/original/poster.png" } } }
        });
        private static JsonElement Success(object? value) => JsonSerializer.SerializeToElement(new { ok = true, contract = "film.playback.v1", value });
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Metadata does not upload source.");
        public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }

        private sealed class MetadataProducer(MetadataRuntime runtime) : IRuntimeClient
        {
            public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
                runtime.ContentAsync(method, parameters, cancellationToken);
            public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
                ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Metadata does not upload source.");
        }
    }
}
