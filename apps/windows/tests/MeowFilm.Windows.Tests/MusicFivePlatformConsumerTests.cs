using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
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
        yield return Case("native KW and MG aggregate preserves grouped versions through WPF and native next page", async () =>
        {
            await using var f = await PublicSearchScene.Create();
            await f.Search();
            // Provider completion precedes the asynchronous rank-fusion publication.
            await SearchUntil(() => f.Module.SearchProgress.Count == 2 && f.Module.SearchProgress.All(p => !p.Loading) &&
                f.Module.CanAggregateMore);
            var mg = f.Module.SearchProgress.Single(p => p.Platform == "mg");
            var kw = f.Module.SearchProgress.Single(p => p.Platform == "kw");
            Equal(31, mg.Count); Equal(1, mg.Page); True(mg.More, "native group continuation retained");
            Equal(1, kw.Count); Equal("", mg.Error); Equal("", kw.Error);
            Equal(32, f.Module.Songs.Count); True(f.Module.CanAggregateMore, "actual next-page control");
            await f.Module.MoreAggregateSearchAsync();
            await SearchUntil(() => f.Module.SearchProgress.Single(p => p.Platform == "mg") is { Page: 2, Loading: false } &&
                f.Module.Songs.Count == 34);
            mg = f.Module.SearchProgress.Single(p => p.Platform == "mg");
            Equal(33, mg.Count); True(!mg.More, "expanded count is not the group cursor");
            Equal(34, f.Module.Songs.Count);
            var state = await f.Call("music.search.start",
                new { keyword = "版本搜索", kind = "songs", platforms = new[] { "kw", "mg" } });
            var native = state.GetProperty("platforms").EnumerateArray().Single(p => p.GetProperty("platform").GetString() == "mg");
            Equal("groups", native.GetProperty("totalUnit").GetString());
            Equal(31L, native.GetProperty("total").GetInt64());
            Equal(1, native.GetProperty("groupCount").GetInt32());
            Equal(2, native.GetProperty("rawItemCount").GetInt32());
            Equal(2, f.Http.MgPages.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("native public search retries only failed KW and keeps MG recording metadata in queue", async () =>
        {
            await using var f = await PublicSearchScene.Create();
            f.Http.FailKw = true;
            await f.Search();
            // Completed providers may still have a rank-fusion publication in flight.
            await SearchUntil(() => f.Module.SearchProgress.Count == 2 && f.Module.SearchProgress.All(p => !p.Loading) &&
                f.Module.CanAggregateMore);
            var failed = f.Module.SearchProgress.Single(p => p.Platform == "kw");
            True(failed.CanRetry, "failed provider remains retryable");
            Equal(31, f.Module.Songs.Count);
            await f.Module.RetryAggregateSearchAsync(failed);
            await SearchUntil(() => f.Module.SearchProgress.Single(p => p.Platform == "kw") is { Count: 1, Loading: false } &&
                f.Module.CanAggregateMore);
            Equal(32, f.Module.Songs.Count);
            Equal(2, f.Http.KwRequests); Equal(1, f.Http.MgPages.Count);
            var card = f.Module.CatalogSections.Single().Items.Single(c => c.Platform == "mg" &&
                c.Song?.Raw.GetProperty("source_data").GetProperty("songId").GetString() == "S1001");
            await f.Module.QueueCatalogCardAsync(card);
            var queue = await f.Call("music.queue.get", new { offset = 0, limit = 100 });
            var source = queue.GetProperty("entries")[0].GetProperty("song").GetProperty("source_data");
            Equal("mg", source.GetProperty("source").GetString());
            Equal("S1001", source.GetProperty("songId").GetString());
            Equal("C1001", source.GetProperty("copyrightId").GetString());
            Equal("CONTENT1001", source.GetProperty("contentId").GetString());
            True(!queue.GetRawText().Contains("DO_NOT_PERSIST", StringComparison.Ordinal), "media URL is not durable metadata");
            Equal(0, f.Runtime.Calls.Count);
        });
    }
    private sealed class PublicSearchScene : IAsyncDisposable
    {
        private readonly DpapiDocumentStore store = ServiceStore();
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly PublicSearchHttp Http = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private PublicSearchScene()
        {
            App = new(store, Runtime, Http); Runtime.Register(App);
            Module = new(App, store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<PublicSearchScene> Create()
        {
            var scene = new PublicSearchScene();
            try
            {
                await scene.Module.InitializeAsync(default); await Drain();
                foreach (var row in scene.Module.SongSearch.Platforms.ToArray())
                    await scene.Module.SetSearchPlatformEnabledAsync("songs", row, row.Platform is "kw" or "mg");
                foreach (var row in scene.Module.PlaylistSearch.Platforms.ToArray())
                    await scene.Module.SetSearchPlatformEnabledAsync("playlists", row, false);
                await scene.Module.SetSearchModeAsync("songs", "aggregate");
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
        internal async Task Search()
        {
            await Module.NavigateAsync("catalog-search", default);
            await Module.SearchAsync("版本搜索", default);
            await Module.OpenAggregateSearchTabAsync("songs");
        }
        public async ValueTask DisposeAsync()
        {
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await store.DisposeAsync(); }
        }
    }
    private sealed class PublicSearchHttp : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<int> MgPages = new();
        internal int KwRequests;
        internal bool FailKw;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            True(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"), "anonymous public read");
            var uri = request.RequestUri!;
            object result;
            if (uri.Host == "search.kuwo.cn" && uri.AbsolutePath == "/r.s")
            {
                Interlocked.Increment(ref KwRequests);
                if (FailKw)
                {
                    FailKw = false;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
                }
                result = new
                {
                    TOTAL = "1", SHOW = "1", abslist = new[]
                    {
                        new { id = "9001", name = "酷我合成歌曲", artist = "合成歌手", album = "合成专辑", albumid = "2",
                            duration = 180, N_MINFO = "level:p,bitrate:128,format:mp3,size:3.2Mb" }
                    }
                };
            }
            else
            {
                True(uri.Host == "jadeite.migu.cn" && uri.AbsolutePath == "/music_search/v3/search/searchAll", "exact MG search");
                var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
                Equal("30", query["pageSize"]);
                int page = int.Parse(query["pageNo"]); True(page is 1 or 2, "native page, not expanded-item offset");
                MgPages.Enqueue(page);
                var groups = page == 1 ? Enumerable.Range(1, 30)
                    .Select(i => i == 1 ? new[] { Track(i), Track(1001) } : new[] { Track(i) }).ToArray()
                    : new[] { new[] { Track(31), Track(1002) } };
                result = new { code = "000000", songResultData = new { totalCount = 31, resultList = groups } };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(result), System.Text.Encoding.UTF8, "application/json") });
        }
        private static object Track(int i) => new
        {
            songId = "S" + i, copyrightId = "C" + i, songName = "咪咕版本 " + i, album = "咪咕合成专辑",
            singerList = new[] { new { name = "咪咕合成歌手" } }, albumId = "A1", duration = 180, contentId = "CONTENT" + i,
            audioFormats = new[] { new { formatType = "HQ", asize = 1048576, hash = "synthetic-only",
                url = "https://media.invalid/audio?token=DO_NOT_PERSIST" } }
        };
    }
}
