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
        yield return Case("TMDB save cancels old details without allowing late data to seed a new query cache", MetadataLateConsumer);
        yield return Case("TMDB admitted settings save drains before close and never restores a revoked search", MetadataSaveDrain);
    }
    private static async Task MetadataSaveConsumer()
    {
        var runtime = new MetadataRuntime();
        var metadata = new FilmMetadataService(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        var oldSession = scene.Film.Search.Current!;
        var oldCard = scene.Page.Cards.Single(card => card.SearchContext!.DisplayKey.StartsWith("tmdb:", StringComparison.Ordinal));
        await scene.Page.OpenCardAsync(oldCard);
        var oldPlayback = scene.Film.Playback!;
        var oldContext = oldPlayback.SearchContext!;
        runtime.FailSave = true;
        await BridgeTests.Expect("STORAGE_ERROR", () => metadata.SaveSettingsAsync(new Dictionary<string, object?>()));
        True(ReferenceEquals(scene.Film.Search.Current, oldSession), "failed save cannot revoke an effective search");
        runtime.FailSave = false;
        await metadata.SaveSettingsAsync(new Dictionary<string, object?>());
        True(scene.Film.Search.Current is null && scene.Page.Cards.Count == 0 && runtime.Searches == 1,
            "success revokes the current lease and visible old cards without requesting anything");
        Equal("详情1", oldPlayback.Overview);
        scene.Click("BackButton");
        await SearchFixture.Until(() => scene.SearchHost is not null && scene.Status == "");
        await SearchFixture.Settle(scene.Film.Search.Current!);
        var current = scene.Film.Search.Current!;
        True(!ReferenceEquals(current, oldSession) && current.Query == oldSession.Query &&
            runtime.Searches == 2 && runtime.Details == 2, "return explicitly enters the same word with a fresh query and detail cache");
        await scene.Page.OpenCardAsync(scene.Page.Cards.Single(card => card.SearchContext!.DisplayKey.StartsWith("tmdb:", StringComparison.Ordinal)));
        True(!ReferenceEquals(oldPlayback, scene.Film.Playback) && !ReferenceEquals(oldContext.Session, scene.Film.Playback!.SearchContext!.Session),
            "the actual page and FilmCapability receiver accept the new session without re-identifying the old choice");
        Equal("详情2", scene.Film.Playback!.Overview);
        Equal("详情1", oldPlayback.Overview);
        True(ReferenceEquals(oldContext.Session, oldSession), "old opened detail retains its original session and metadata");
    }
    private static async Task MetadataLateConsumer()
    {
        var held = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new MetadataRuntime { HeldDetail = held.Task };
        var metadata = new FilmMetadataService(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata);
        try
        {
            await scene.Submit("目标", "film");
            var previous = scene.Film.Search.Current!;
            await SearchFixture.Until(() => runtime.Details == 1);
            var old = new FilmSearchContext(previous, previous.Items.Single(item => item.GetProperty("sourceKind").GetString() == "tmdb"));
            await metadata.SaveSettingsAsync(new Dictionary<string, object?>());
            True(runtime.DetailToken.IsCancellationRequested && scene.Film.Search.Current is null &&
                runtime.Searches == 1, "save cancels in-flight old detail and does not search automatically");
            runtime.HeldDetail = null;
            await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
            held.SetResult(SearchFixture.Json(new { id = 7, name = "目标", overview = "迟到旧详情", seasons = Array.Empty<object>() }));
            await SearchFixture.Settle(previous); await Drain();
            await scene.Submit("另一个词", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
            Equal(2, runtime.Details);
            var next = scene.Page.Cards.Single(card => card.SearchContext!.DisplayKey.StartsWith("tmdb:", StringComparison.Ordinal));
            Equal("详情2", next.SearchContext!.Metadata!.Value.GetProperty("overview").GetString());
            Equal("已得搜索简介", old.Metadata!.Value.GetProperty("overview").GetString());
            True(scene.Film.Search.TrackedSessions == 1, "retired old query drains and releases the service tracking");
        }
        finally { held.TrySetResult(SearchFixture.Json(new { })); }
    }
    private static async Task MetadataSaveDrain()
    {
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new MetadataRuntime { HeldSave = receipt.Task };
        var metadata = new FilmMetadataService(runtime);
        await using var scene = await UnifiedScene.Create(metadata: metadata);
        await scene.Submit("目标", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
        using var caller = new CancellationTokenSource();
        var save = metadata.SaveSettingsAsync(new Dictionary<string, object?>(), caller.Token);
        caller.Cancel();
        var close = scene.Film.PrepareShutdownAsync();
        try
        {
            True(!save.IsCompleted && !close.IsCompleted && !runtime.SaveToken.IsCancellationRequested,
                "an admitted write cannot be abandoned by caller cancel or close preparation");
            receipt.SetResult(); await save; await close;
            True(scene.Film.Search.Current is null && scene.Page.Cards.Count == 0,
                "the successful receipt still revokes its old search before shutdown drain acknowledges completion");
            await scene.Film.CancelShutdownPreparationAsync();
            True(scene.Film.Search.Current is null && runtime.Searches == 1,
                "cancelled shutdown does not auto-request the revoked metadata session");
            await scene.Submit("最终关闭", "film"); await SearchFixture.Settle(scene.Film.Search.Current!);
            var retained = scene.Page.Cards.Single(card => card.SearchContext!.DisplayKey.StartsWith("tmdb:", StringComparison.Ordinal)).SearchContext!;
            string snapshot = retained.Metadata!.Value.GetRawText();
            var finalReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.HeldSave = finalReceipt.Task;
            var finalSave = metadata.SaveSettingsAsync(new Dictionary<string, object?>());
            var abort = scene.Film.AbortAsync();
            try
            {
                await SearchFixture.Until(() => scene.Search.Engine.Disposals == 1 && scene.Film.Search.TrackedSessions == 0);
                True(!abort.IsCompleted && !finalSave.IsCompleted,
                    "forced release still waits for an admitted settings receipt after the search owner physically closes");
                finalReceipt.SetResult(); await finalSave; await abort;
                Equal(snapshot, retained.Metadata!.Value.GetRawText());
                True(retained.Query == "最终关闭", "final disposal cannot dispatch saved notifications to a released search or discard its retained data");
            }
            finally { finalReceipt.TrySetResult(); }
        }
        finally { receipt.TrySetResult(); }
    }
    private sealed class MetadataRuntime : IRuntimeClient
    {
        internal int Searches, Details;
        internal bool FailSave;
        internal Task<JsonElement>? HeldDetail;
        internal CancellationToken DetailToken;
        internal Task? HeldSave;
        internal CancellationToken SaveToken;
        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "tmdb.data.settings.update" && FailSave)
                return JsonSerializer.SerializeToElement(new { ok = false, contract = "tmdb.data.v1", code = "STORAGE_ERROR" });
            if (method == "tmdb.data.settings.update")
            {
                SaveToken = cancellationToken;
                if (HeldSave is { } save) await save;
            }
            if (method is "tmdb.data.settings.get" or "tmdb.data.settings.update")
                return Success(new { apiBase = "https://api.fixture.invalid", imageBase = "https://image.fixture.invalid/t/p",
                    apiKeyConfigured = false, accessTokenConfigured = false });
            var request = JsonSerializer.SerializeToElement(parameters);
            if (request.GetProperty("path").GetString() == "/3/search/multi")
            {
                Searches++;
                return Success(SearchFixture.Tmdb("tv", 7, "目标", "已得搜索简介"));
            }
            Details++; DetailToken = cancellationToken;
            return Success(HeldDetail is { } held ? await held : SearchFixture.Json(new
            { id = 7, name = "目标", overview = "详情" + Details, seasons = Array.Empty<object>() }));
        }
        private static JsonElement Success(object value) => JsonSerializer.SerializeToElement(new { ok = true, contract = "tmdb.data.v1", value });
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Metadata does not upload source.");
    }
}
