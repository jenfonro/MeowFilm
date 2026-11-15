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
        var settings = new MetadataRuntime();
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
        var settings = new MetadataRuntime();
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
        var settings = new MetadataRuntime { HeldSave = receipt.Task };
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
    private sealed class MetadataRuntime : IRuntimeClient
    {
        internal bool FailSave;
        internal Task? HeldSave;
        internal CancellationToken SaveToken;
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            True(method is "tmdb.data.settings.get" or "tmdb.data.settings.update",
                "the metadata settings fixture accepts only settings reads and writes");
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
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Metadata does not upload source.");
    }
}
