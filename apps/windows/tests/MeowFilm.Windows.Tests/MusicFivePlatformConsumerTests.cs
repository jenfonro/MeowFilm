using System.Collections.Concurrent;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicFivePlatformConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PublicCatalogConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> PublicCatalogConsumerCases()
    {
        yield return Case("native KW and MG search preserves grouped versions through WPF and the next round", async () =>
        {
            await using var f = await PublicSearchScene.Create();
            await f.Module.SetCatalogSourceAsync("kg");
            await f.Search();
            // Provider completion precedes the asynchronous rank-fusion publication.
            await SearchUntil(() => f.Module.Songs.Count == 32 && f.Module.CanSearchMore);
            var state = await f.Snapshot();
            var mg = Platform(state, "mg"); var kw = Platform(state, "kw");
            Equal(31, mg.GetProperty("count").GetInt32()); Equal(1, mg.GetProperty("page").GetInt32());
            True(mg.GetProperty("more").GetBoolean(), "native group continuation retained");
            Equal(1, kw.GetProperty("count").GetInt32());
            Equal("", mg.GetProperty("error").GetString()); Equal("", kw.GetProperty("error").GetString());
            True(f.Module.CanSearchMore, "actual next-round control");
            await f.Module.MoreSearchAsync();
            await SearchUntil(() => f.Module.Songs.Count == 34 && !f.Module.CanSearchMore);
            mg = Platform(await f.Snapshot(), "mg");
            Equal(33, mg.GetProperty("count").GetInt32()); Equal(2, mg.GetProperty("page").GetInt32());
            True(!mg.GetProperty("more").GetBoolean(), "expanded count is not the group cursor");
            Equal(2, f.Http.MgPages.Count); Equal(0, f.Runtime.Calls.Count);
            Equal("kg", f.Module.CatalogSourcePlatform);
            Equal("kg", (await f.Call("music.catalogSource.settings.get")).GetProperty("settings").GetProperty("platform").GetString());
        });
        yield return Case("native public search retries failed KW in the next round and keeps MG recording metadata in queue", async () =>
        {
            await using var f = await PublicSearchScene.Create();
            f.Http.FailKw = true;
            await f.Search();
            // Completed providers may still have a rank-fusion publication in flight.
            await SearchUntil(() => f.Module.Songs.Count == 31 && f.Module.CanSearchMore);
            True(Platform(await f.Snapshot(), "kw").GetProperty("error").GetString()!.Length > 0, "failed provider is reported");
            await f.Module.MoreSearchAsync();
            await SearchUntil(() => f.Module.Songs.Count == 34 && !f.Module.CanSearchMore);
            var kw = Platform(await f.Snapshot(), "kw");
            Equal(1, kw.GetProperty("count").GetInt32()); Equal("", kw.GetProperty("error").GetString());
            Equal(2, f.Http.KwRequests); Equal(2, f.Http.MgPages.Count);
            var card = f.Module.CatalogSections.Single().Items.Single(c => c.Platform == "mg" &&
                c.Song is { } song && MusicSongFixture.Info(song.Raw).GetProperty("songId").GetString() == "S1001");
            await f.Module.QueueCatalogCardAsync(card);
            var queue = await f.Call("music.queue.get", new { offset = 0, limit = 100 });
            var source = MusicSongFixture.Info(queue.GetProperty("entries")[0].GetProperty("song"));
            Equal("mg", source.GetProperty("source").GetString());
            Equal("S1001", source.GetProperty("songId").GetString());
            Equal("C1001", source.GetProperty("copyrightId").GetString());
            Equal("CONTENT1001", source.GetProperty("contentId").GetString());
            True(!queue.GetRawText().Contains("DO_NOT_PERSIST", StringComparison.Ordinal), "media URL is not durable metadata");
            Equal(0, f.Runtime.Calls.Count);
        });
    }
    private static JsonElement Platform(JsonElement snapshot, string platform) =>
        snapshot.GetProperty("platforms").EnumerateArray().Single(p => p.GetProperty("platform").GetString() == platform);
    private sealed class PublicSearchScene : IAsyncDisposable
    {
        private readonly NativeDatabase store = ServiceStore();
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly PublicSearchHttp Http = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private string? songQueryId;
        private PublicSearchScene()
        {
            Runtime.Platform.OnRequest = Http.Read;
            App = new(store, MusicTestStores.For(store), Runtime);
            App.SearchChanged += snapshot =>
            {
                if (snapshot.GetProperty("kind").GetString() == "songs")
                    Volatile.Write(ref songQueryId, snapshot.GetProperty("id").GetString());
            };
            Module = new(App, store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<PublicSearchScene> Create()
        {
            var scene = new PublicSearchScene();
            try
            {
                await scene.Module.InitializeAsync(default); await Drain();
                Equal("wy", scene.Module.CatalogSourcePlatform);
                True(scene.Module.CanEditCatalogSource && !scene.Module.HasCatalogSourceError,
                    "five-platform fixture consumes native source settings without a script fallback");
                foreach (var row in scene.Module.SongSearch.Platforms.ToArray())
                    await scene.Module.SetSearchPlatformEnabledAsync("songs", row, row.Platform is "kw" or "mg");
                foreach (var row in scene.Module.PlaylistSearch.Platforms.ToArray())
                    await scene.Module.SetSearchPlatformEnabledAsync("playlists", row, false);
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> Call(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input ?? new { });
            True(result.GetProperty("ok").GetBoolean(), method + ":" + result.GetRawText());
            return result;
        }
        internal Task<JsonElement> Snapshot() => Call("music.search.get",
            new { id = Volatile.Read(ref songQueryId) ?? throw new InvalidOperationException("No admitted song search") });
        internal async Task Search()
        {
            await Module.NavigateAsync("catalog-search", default);
            await Module.SearchAsync("版本搜索", default);
            await Module.OpenSearchTabAsync("songs");
        }
        public async ValueTask DisposeAsync()
        {
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await store.DisposeAsync(); }
        }
    }
    private sealed class PublicSearchHttp
    {
        internal readonly ConcurrentQueue<int> MgPages = new();
        internal int KwRequests;
        internal bool FailKw;
        internal Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            True(call.Operation == "search", "canonical platform search");
            object result;
            if (call.Platform == "kw")
            {
                Interlocked.Increment(ref KwRequests);
                if (FailKw) { FailKw = false; throw new MeowFilm.Capabilities.Platforms.MusicPlatformException("UPSTREAM_ERROR"); }
                result = new { platform = "kw", kind = "songs", items = new[] { MusicSongFixture.Online("kw", "9001", "酷我合成歌曲", "合成歌手", "合成专辑") },
                    more = false, continuation = (object?)null };
            }
            else
            {
                Equal("mg", call.Platform); int page = call.Parameters.GetProperty("page").GetInt32();
                True(page is 1 or 2, "native page, not expanded-item offset"); MgPages.Enqueue(page);
                int[] ids = page == 1 ? Enumerable.Range(1, 30).SelectMany(i => i == 1 ? new[] { 1, 1001 } : new[] { i }).ToArray() : [31, 1002];
                result = new { platform = "mg", kind = "songs", items = ids.Select(Track).ToArray(), more = page == 1,
                    continuation = page == 1 ? new { page = 2 } : null };
            }
            return Task.FromResult(MusicPlatformRuntimeFixture.Json(result));
        }
        private static System.Text.Json.Nodes.JsonObject Track(int i) => MusicSongFixture.Online("mg", "S" + i,
            "咪咕版本 " + i, "咪咕合成歌手", "咪咕合成专辑", native: new() {
                ["songId"] = "S" + i, ["copyrightId"] = "C" + i, ["contentId"] = "CONTENT" + i, ["albumId"] = "A1" });
    }
}
