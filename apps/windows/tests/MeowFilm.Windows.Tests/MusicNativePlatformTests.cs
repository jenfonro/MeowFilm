using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Production C# services/codecs/parsers with trusted synthetic HTTP, no Node, real accounts or upstream.
public static partial class MusicNativePlatformTests
{
    private const string Huge = "9007199254740993123";
    private const string KgHash = "ABCDEF0123456789ABCDEF0123456789";
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in ChartCases()) yield return test;
        yield return Case("platform response uses the last JSON field at every object without stripping provider metadata", async () =>
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """{"code":500,"code":200,"alg":null,"alg":"root","data":{"id":1,"id":2,"rows":[{"alg":null,"alg":"track","meta":{"name":"old","name":"new"}}]}}""",
                    Encoding.UTF8, "application/json") })));
            var result = await http.SendAsync("wy", new(HttpMethod.Post,
                "https://interfacepc.music.163.com/eapi/v6/playlist/detail"), default);
            Equal("200", S(result.Body["code"])); Equal("root", S(result.Body["alg"]));
            Equal("2", S(result.Body["data"]!["id"]));
            Equal("track", S(result.Body["data"]!["rows"]![0]!["alg"]));
            Equal("new", S(result.Body["data"]!["rows"]![0]!["meta"]!["name"]));
        });
        yield return Case("official WY audio uses XEAPI and separates requested from delivered quality", async () =>
        {
            foreach (bool trial in new[] { false, true })
            {
                using var wire = new XeapiFixture();
                using var transport = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) =>
                {
                    if (request.RequestUri!.AbsolutePath == "/api/bsr/sk/get")
                        return Task.FromResult(wire.PublicKey(request));
                    if (request.RequestUri.AbsolutePath == "/xeapi/song/enhance/player/url/v1")
                    {
                        var content = S(wire.Open(request)["content"]);
                        True(content.Contains("level=exhigh", StringComparison.Ordinal) &&
                            Uri.UnescapeDataString(content).Contains("ids=[123]", StringComparison.Ordinal));
                        return Task.FromResult(XeapiFixture.Response(J(new { code = 200, data = new[] { new {
                            id = 123, code = 200, url = "https://m7.music.126.net/fixture.mp3", level = "standard",
                            size = 123456, time = 100000, expi = 1800, freeTrialInfo = trial ? new { start = 0, end = 30 } : null
                        } } })));
                    }
                    var (path, body) = Eapi(request);
                    Equal("/api/song/music/detail/get", path); Equal("123", S(body["songId"]));
                    return Task.FromResult(Json(J(new { code = 200, data = new { h = new { size = 123456 } } })));
                }));
                var rights = new MeowFilm.Capabilities.Accounts.MusicAccountEntitlements(new(true, "vip",
                    DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()), 1, 0, ["exhigh"]);
                await using var clients = new MusicClientContextFixture(transport);
                if (trial)
                    await Error("PLAYBACK_RESTRICTED", () => new OfficialMusicAudio(transport, clients.Owner).ResolveAsync(DeviceSession(),
                        new() { ["songmid"] = "123" }, ["320k"], rights, default, () => { }));
                else
                {
                    var result = await new OfficialMusicAudio(transport, clients.Owner).ResolveAsync(DeviceSession(),
                        new() { ["songmid"] = "123" }, ["320k"], rights, default, () => { });
                    Equal("320k", result.RequestQuality); Equal("128k", result.ActualQuality); True(result.QualityVerified);
                }
            }
        });
        yield return Case("official chorus uses exact recording milliseconds and no QQ trial fallback", async () =>
        {
            using var f = new Fixture(req => {
                var (path, body) = Eapi(req); Equal("/api/song/chorus", path);
                Equal("[\"123\"]", S(body["ids"]));
                return Json(J(new { code = 200, chorus = new[] { new { id = 123, startTime = 71200, endTime = 82000 } } }));
            });
            var point = await f.Catalog.ChorusAsync("wy", "123");
            Equal(71.2, point.GetProperty("point").GetProperty("start").GetDouble());
            await f.Catalog.ChorusAsync("wy", "123"); Equal(1, f.Calls);
            var qq = await f.Catalog.ChorusAsync("tx", "FixtureMID");
            Equal(JsonValueKind.Null, qq.GetProperty("point").ValueKind); Equal(1, f.Calls);
            await Error("INVALID_REQUEST", () => f.Catalog.ChorusAsync("wy", "9007199254740992"));
            using var atStart = new Fixture(_ => Json(J(new { code = 200,
                chorus = new[] { new { id = 123, startTime = 0, endTime = 10000 } } })));
            Equal(0d, (await atStart.Catalog.ChorusAsync("wy", "123")).GetProperty("point").GetProperty("start").GetDouble());
        });
        yield return Case("KG chorus CDN carries no credentials and refuses mismatched length", async () =>
        {
            using var f = new Fixture(req => {
                Equal("expendablekmrcdn.kugou.com", req.RequestUri!.Host);
                Equal("/v1/audio_climax/audio", req.RequestUri.AbsolutePath);
                True(!req.Headers.Contains("Cookie") && !req.Headers.Contains("Authorization"));
                True(Query(req.RequestUri)["data"].Contains(KgHash, StringComparison.Ordinal));
                return Json(J(new { status = 1, data = new[] { new { hash = KgHash, start_time = 71200, end_time = 82000, timelength = 1 } } }));
            });
            var result = await f.Catalog.ChorusAsync("kg", KgHash);
            Equal(JsonValueKind.Null, result.GetProperty("point").ValueKind);
        });
        yield return Case("chorus refuses another recording and duplicate matches", async () =>
        {
            foreach (var ids in new[] { new[] { 124 }, new[] { 123, 123 } })
            {
                using var f = new Fixture(_ => Json(J(new { code = 200, chorus = ids.Select(id => new { id, startTime = 10000, endTime = 20000 }).ToArray() })));
                Equal(JsonValueKind.Null, (await f.Catalog.ChorusAsync("wy", "123")).GetProperty("point").ValueKind);
            }
        });
        yield return Case("session cannot stringify or serialize credentials", () =>
        {
            var session = Session("wy");
            Equal("MusicPlatformSession (redacted)", session.ToString());
            Equal("{}", JsonSerializer.Serialize(session));
            Equal(Huge, session.UserId);
            return Task.CompletedTask;
        });
        yield return Case("factory and capability manifest perform no IO and expose real gaps", () =>
        {
            using var f = new Fixture(_ => throw new Exception("unexpected"));
            var d = f.Catalog.Describe();
            Equal("native", d.GetProperty("execution").GetString());
            var wy = d.GetProperty("platforms").GetProperty("wy");
            True(wy.GetProperty("daily").GetBoolean());
            True(wy.GetProperty("dailyRequiresDeviceContext").GetBoolean());
            True(wy.GetProperty("artist").GetBoolean());
            Equal("featured,playlists,rankings,new-songs,cube,artists,vip",
                string.Join(",", wy.GetProperty("channelKinds").EnumerateArray().Select(kind => kind.GetString())));
            True(!d.GetProperty("platforms").GetProperty("tx").GetProperty("home").GetBoolean());
            True(d.GetProperty("platforms").GetProperty("kg").GetProperty("home").GetBoolean());
            True(d.GetProperty("platforms").GetProperty("kg").GetProperty("channels").GetBoolean());
            var limits = d.GetProperty("limits");
            Equal(100, limits.GetProperty("maxPageSize").GetInt32());
            Equal(4 * 1024 * 1024, limits.GetProperty("maxResponseBytes").GetInt32());
            Equal(2, limits.EnumerateObject().Count());
            Equal(0, f.Calls); return Task.CompletedTask;
        });
        yield return Case("wy search real eapi envelope and lossless song identity", async () =>
        {
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req);
                Equal("/api/cloudsearch/pc", path); Equal("关键词", S(body["s"])); Equal("2", S(body["offset"]));
                True(body["header"]!["MUSIC_U"] is null);
                return WySearch([Wy(Huge)], 3);
            });
            var page = await f.Catalog.SearchAsync(new("wy", " 关键词 ", Page: 2, Limit: 2));
            Song(page.GetProperty("items")[0], "wy", Huge);
            Equal(185d, page.GetProperty("items")[0].GetProperty("duration").GetDouble());
            Equal(3L, page.GetProperty("total").GetInt64()); Equal(1, f.Calls);
        });
        yield return Case("native short search pages without an explicit cursor are terminal", async () =>
        {
            foreach (string platform in new[] { "wy", "kg" }) await ShortNativeSearch(platform, false);
        });
        yield return Case("qq short nonterminal search page follows nextpage", async () =>
        {
            await ShortNativeSearch("tx", false);
        });
        yield return Case("native empty search pages against a larger total are terminal", async () =>
        {
            foreach (string platform in new[] { "wy", "tx", "kg" }) await ShortNativeSearch(platform, true);
        });
        yield return Case("qq search uses raw native mid numeric id and no signature fallback", async () =>
        {
            using var f = new Fixture(req =>
            {
                var body = RequestJson(req); var call = body["req_0"]!;
                Equal("music.search.SearchCgiService", S(call["module"])); Equal("DoSearchForQQMusicMobile", S(call["method"]));
                Equal("0", S(call["param"]!["search_type"])); True(body["comm"]!["authst"] is null);
                Equal("SYNTHETIC_Q36", S(body["comm"]!["QIMEI36"]));
                Equal("SYNTHETIC_UID", S(body["comm"]!["uid"])); Equal("SYNTHETIC_SID", S(body["comm"]!["sid"]));
                True(long.TryParse(S(call["param"]!["searchid"]), out var searchId) &&
                    searchId >= 18014398509481984L && searchId <= 378302368785521663L);
                Equal("QQMusic 20090008(android 15)", req.Headers.GetValues("User-Agent").Single());
                return Tx(J(new { meta = new { sum = 1, nextpage = -1 }, body = new { item_song = new[] { Qq() } } }));
            });
            var r = await f.Catalog.SearchAsync(new("tx", "q"));
            Song(r.GetProperty("items")[0], "tx", "midA001");
            Equal(Huge, MusicSongFixture.Info(r.GetProperty("items")[0]).GetProperty("qqSongId").GetString());
        });
        yield return Case("qq songs and playlists share one anonymous bootstrap and reuse the current session", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture(async (request, _) =>
            {
                if (request.RequestUri!.Host == "api.tencentmusic.com")
                { entered.TrySetResult(); await release.Task; }
                return QqClientResponse(request) ?? QqSearchResponse(request);
            }, readyQq: false);
            Equal(0, f.Calls);
            var songs = f.Catalog.SearchAsync(new("tx", "词"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var playlists = f.Catalog.SearchAsync(new("tx", "词", MusicSearchKind.Playlists));
            var deviceWaiter = f.Clients.Owner.EnsureQqAsync(f.Clients.Protocol, default);
            try { Equal(1, f.Calls); True(!songs.IsCompleted && !playlists.IsCompleted); }
            finally { release.TrySetResult(); }
            var pages = await Task.WhenAll(songs, playlists).WaitAsync(TimeSpan.FromSeconds(5));
            Song(pages[0].GetProperty("items")[0], "tx", "midA001");
            Equal("77", pages[1].GetProperty("items")[0].GetProperty("id").GetString());
            Equal(4, f.Calls);
            var device = await f.Clients.Owner.AcquireQqAsync();
            True(device.Qimei is not null && device.SessionCurrent(DateTimeOffset.UtcNow));
            True(ReferenceEquals(device, await deviceWaiter));
            await f.Catalog.SearchAsync(new("tx", "词"));
            await f.Catalog.SearchAsync(new("tx", "词", MusicSearchKind.Playlists));
            Equal(6, f.Calls);
            True(ReferenceEquals(device, await f.Clients.Owner.AcquireQqAsync()));
        });
        foreach (var phase in new[] { "qimei", "session" })
            yield return Case("qq " + phase + " bootstrap failure never searches or falls back", async () =>
            {
                using var f = new Fixture(request =>
                {
                    bool qimei = request.RequestUri!.Host == "api.tencentmusic.com";
                    if ((phase == "qimei") == qimei)
                        return Json(J(new { code = 1, req_0 = new { code = 1 } }));
                    return QqClientResponse(request) ?? throw new InvalidOperationException("Unexpected search after failed bootstrap");
                }, readyQq: false);
                await Error("UPSTREAM_ERROR", () => f.Catalog.SearchAsync(new("tx", "词")));
                Equal(phase == "qimei" ? 1 : 2, f.Calls);
                var device = await f.Clients.Owner.AcquireQqAsync();
                True(device.Session is null && (device.Qimei is not null) == (phase == "session"));
            });
        yield return Case("qq cancelling one search waiter retains the other anonymous bootstrap consumer", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture(async (request, _) =>
            {
                if (request.RequestUri!.Host == "api.tencentmusic.com")
                { entered.TrySetResult(); await release.Task; }
                return QqClientResponse(request) ?? QqSearchResponse(request);
            }, readyQq: false);
            using var cancel = new CancellationTokenSource();
            var songs = f.Catalog.SearchAsync(new("tx", "词"), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var playlists = f.Catalog.SearchAsync(new("tx", "词", MusicSearchKind.Playlists));
            try
            {
                cancel.Cancel();
                await Error("CANCELLED", () => songs);
                True(!playlists.IsCompleted); Equal(1, f.Calls);
            }
            finally { release.TrySetResult(); }
            Equal(1, (await playlists.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("items").GetArrayLength());
            Equal(3, f.Calls);
        });
        yield return Case("qq search account authorization remains distinct from the anonymous device session", async () =>
        {
            using var f = new Fixture(request =>
            {
                var comm = RequestJson(request)["comm"]!;
                Equal(Huge, S(comm["qq"])); Equal("SYNTHETIC_ONLY", S(comm["authst"]));
                Equal("SYNTHETIC_UID", S(comm["uid"])); Equal("SYNTHETIC_SID", S(comm["sid"]));
                True(S(comm["traceid"]).StartsWith("10002_" + Huge + "_", StringComparison.Ordinal));
                True(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"));
                return QqSearchResponse(request);
            }, Session("tx"));
            Song((await f.Catalog.SearchAsync(new("tx", "词"))).GetProperty("items")[0], "tx", "midA001");
            Equal(1, f.Calls);
        });
        yield return Case("qq authoritative client reload rejects an old search response without replay", async () =>
        {
            Fixture? f = null;
            using (f = new Fixture(async (request, _) =>
            {
                await f!.Clients.Owner.ReloadAsync();
                return QqSearchResponse(request);
            }))
            {
                await Error("STORAGE_CONFLICT", () => f.Catalog.SearchAsync(new("tx", "词")));
                Equal(1, f.Calls);
            }
        });
        yield return Case("qq signature-required search remains an explicit error with no alternate request", async () =>
        {
            using var f = new Fixture(_ => Json(J(new { code = 0, req_0 = new { code = 2000 } })));
            await Error("SIGNATURE_REQUIRED", () => f.Catalog.SearchAsync(new("tx", "词")));
            Equal(1, f.Calls);
        });
        yield return Case("qq search uses the shared client protocol clock", async () =>
        {
            var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) =>
            {
                True(S(RequestJson(request)["comm"]!["traceid"]).EndsWith("_" + now.ToUnixTimeSeconds(), StringComparison.Ordinal));
                return Task.FromResult(QqSearchResponse(request));
            }));
            await using var clients = new MusicClientContextFixture(http, clock: new FixedClock(now));
            await clients.SeedQqAsync();
            using var catalog = new MusicPlatformCatalog(new Source(), http, clients.Owner, clientProtocol: clients.Protocol);
            Equal(1, (await catalog.SearchAsync(new("tx", "词"))).GetProperty("items").GetArrayLength());
        });
        yield return Case("qq search refreshes expired session and QIMEI through the same client owner", async () =>
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
            List<string> steps = [];
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) =>
            {
                steps.Add(request.RequestUri!.Host == "api.tencentmusic.com" ? "QIMEI" : S(RequestJson(request)["req_0"]!["method"]));
                return Task.FromResult(QqClientResponse(request) ?? QqSearchResponse(request));
            }));
            await using var clients = new MusicClientContextFixture(http, clock: clock);
            await clients.SeedQqAsync();
            using var catalog = new MusicPlatformCatalog(new Source(), http, clients.Owner, clientProtocol: clients.Protocol);
            await catalog.SearchAsync(new("tx", "词"));
            var first = await clients.Owner.AcquireQqAsync();
            clock.Advance(TimeSpan.FromHours(12));
            await catalog.SearchAsync(new("tx", "词", MusicSearchKind.Playlists));
            var midnight = await clients.Owner.AcquireQqAsync();
            Equal(first.Qimei, midnight.Qimei);
            Equal(first.Session!.Generation + 1, midnight.Session!.Generation);
            clock.Advance(TimeSpan.FromHours(25));
            await catalog.SearchAsync(new("tx", "词"));
            var next = await clients.Owner.AcquireQqAsync();
            Equal(first.Qimei!.Generation + 1, next.Qimei!.Generation);
            Equal(next.Qimei.Generation, next.Session!.QimeiGeneration);
            Equal("DoSearchForQQMusicMobile,GetSession,DoSearchForQQMusicMobile,QIMEI,GetSession,DoSearchForQQMusicMobile",
                string.Join(",", steps));
        });
        yield return Case("qq exact song and playlist reads do not require anonymous bootstrap", async () =>
        {
            using var f = new Fixture(request => S(RequestJson(request)["req_0"]!["method"]) switch
            {
                "CgiGetTrackInfo" => Tx(J(new { tracks = new[] { Qq() } })),
                "CgiGetDiss" => Tx(J(new { total_song_num = 1, hasmore = 0, songlist_size = 1,
                    dirinfo = new { id = 77, title = "歌单" }, songlist = new[] { Qq() } })),
                _ => throw new InvalidOperationException("Unexpected anonymous bootstrap")
            }, readyQq: false);
            Song(await f.Catalog.SongAsync(new("tx", "midA001")), "tx", "midA001");
            Song((await f.Catalog.PlaylistAsync(new("tx", "77"))).GetProperty("items")[0], "tx", "midA001");
            Equal(2, f.Calls);
            True((await f.Clients.Owner.AcquireQqAsync()).Qimei is null);
        });
        yield return Case("qq application shutdown drains a noncooperative anonymous bootstrap before release", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            await using var store = new MusicClientContextFixture.MemoryStore();
            await using var application = new MusicNativeApplication(store, new NoRuntime(), new Handler(async (request, token) =>
            {
                Interlocked.Increment(ref calls);
                Equal("api.tencentmusic.com", request.RequestUri!.Host);
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task;
                return QqClientResponse(request)!;
            }));
            await application.InitializeAsync();
            var settings = (await application.ExecuteAsync("music.search.settings.get")).GetProperty("settings");
            var songSettings = JsonNode.Parse(settings.GetProperty("songs").GetRawText())!.AsObject();
            songSettings["enabled"] = new JsonArray("tx");
            var saved = await application.ExecuteAsync("music.search.settings.save", new
            {
                ifRevision = settings.GetProperty("revision").GetInt64(),
                songs = songSettings, playlists = settings.GetProperty("playlists")
            });
            True(saved.GetProperty("ok").GetBoolean());
            var start = await application.ExecuteAsync("music.search.start", new { keyword = "词", kind = "songs", platforms = new[] { "tx" } });
            True(start.GetProperty("ok").GetBoolean());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var preparation = application.PrepareShutdownAsync();
            try
            {
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!preparation.IsCompleted); Equal(1, calls);
            }
            finally { release.TrySetResult(); }
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, calls);
        });
        yield return Case("kg search signs actual query and maps hash and units", async () =>
        {
            using var f = new Fixture(req =>
            {
                VerifyKg(req); Equal("/v2/search/song", req.RequestUri!.AbsolutePath);
                Equal("complexsearch.kugou.com", req.Headers.GetValues("x-router").Single());
                return Kg(J(new { total = 1, lists = new[] { KgSong() } }));
            });
            var r = await f.Catalog.SearchAsync(new("kg", "曲"));
            Song(r.GetProperty("items")[0], "kg", KgHash);
            Equal(Huge, MusicSongFixture.Info(r.GetProperty("items")[0]).GetProperty("albumAudioId").GetString());
        });
        yield return Case("kg search gid maps card metadata and reads the same paged global playlist", async () =>
        {
            const string globalId = "collection_3_42_287_0";
            using var f = new Fixture(request =>
            {
                VerifyKg(request);
                if (request.RequestUri!.AbsolutePath == "/v1/search/special")
                    return Kg(J(new { total = 1, lists = new[] { new { specialid = 99, gid = globalId, specialname = "搜索歌单",
                        img = "https://images.invalid/{size}/list.jpg", song_count = 2, nickname = "创建者" } } }));
                Equal("/pubsongs/v2/get_other_list_file_nofilt", request.RequestUri.AbsolutePath);
                var query = Query(request.RequestUri);
                Equal(globalId, query["global_collection_id"]); Equal("1", query["pagesize"]);
                var song = KgSong(); if (query["begin_idx"] == "1") song["FileHash"] = new string('B', 32);
                return Kg(J(new { list_info = new { global_collection_id = globalId, count = 2,
                    name = "搜索歌单", pic = "https://images.invalid/{size}/detail.jpg", version = 1 }, songs = new[] { song } }));
            });
            var card = (await f.Catalog.SearchAsync(new("kg", "词", MusicSearchKind.Playlists))).GetProperty("items")[0];
            Equal(globalId, card.GetProperty("id").GetString()); Equal("搜索歌单", card.GetProperty("name").GetString());
            Equal("https://images.invalid/480/list.jpg", card.GetProperty("cover").GetString());
            Equal(2L, card.GetProperty("count").GetInt64()); Equal("创建者", card.GetProperty("creator").GetString());
            var first = await f.Catalog.PlaylistAsync(new("kg", card.GetProperty("id").GetString()!, Limit: 1));
            Song(first.GetProperty("items")[0], "kg", KgHash); True(first.GetProperty("more").GetBoolean());
            Equal("https://images.invalid/480/detail.jpg", first.GetProperty("cover").GetString());
            var next = await f.Catalog.PlaylistAsync(new("kg", globalId, Page: 2, Limit: 1, Snapshot: first.GetProperty("snapshot").GetString()));
            Song(next.GetProperty("items")[0], "kg", new string('B', 32));
            True(!next.GetProperty("more").GetBoolean()); Equal(globalId, next.GetProperty("id").GetString());
            Equal(first.GetProperty("snapshot").GetString(), next.GetProperty("snapshot").GetString());
            Equal(3, f.Calls);
        });
        yield return Case("kg playlist page without list info keeps total and snapshot while leaving metadata empty", async () =>
        {
            const string globalId = "collection_3_42_287_0";
            using var f = new Fixture(request =>
            {
                VerifyKg(request); Equal("/pubsongs/v2/get_other_list_file_nofilt", request.RequestUri!.AbsolutePath);
                var query = Query(request.RequestUri); Equal(globalId, query["global_collection_id"]);
                var song = KgSong(); if (query["begin_idx"] == "1") song["FileHash"] = new string('B', 32);
                return query["begin_idx"] == "0"
                    ? Kg(J(new { list_info = new { global_collection_id = globalId, name = "搜索歌单", version = 1 },
                        count = 2, songs = new[] { song } }))
                    : Kg(J(new { list_info = (object?)null, count = 2, songs = new[] { song } }));
            });
            var first = await f.Catalog.PlaylistAsync(new("kg", globalId, Limit: 1));
            Equal("搜索歌单", first.GetProperty("name").GetString());
            Equal(2L, first.GetProperty("total").GetInt64());
            var next = await f.Catalog.PlaylistAsync(new("kg", globalId, Page: 2, Limit: 1,
                Snapshot: first.GetProperty("snapshot").GetString()));
            Song(next.GetProperty("items")[0], "kg", new string('B', 32));
            Equal(2L, next.GetProperty("total").GetInt64()); Equal("", next.GetProperty("name").GetString());
            Equal("", next.GetProperty("cover").GetString());
            Equal(first.GetProperty("snapshot").GetString(), next.GetProperty("snapshot").GetString());
            Equal(2, f.Calls);
        });
        foreach (var (identity, code) in new[]
        {
            (new JsonObject { ["specialid"] = 99 }, "INVALID_RESPONSE"),
            (new JsonObject { ["gid"] = "../99" }, "INVALID_RESPONSE"),
            (new JsonObject { ["gid"] = "id_fake" }, "IDENTITY_MISMATCH"),
            (new JsonObject { ["gid"] = "collection-A", ["global_collection_id"] = "collection-B" }, "IDENTITY_MISMATCH")
        })
            yield return Case("kg search rejects unconfirmed global identity " + identity.ToJsonString(), async () =>
            {
                using var f = new Fixture(_ => Kg(J(new { total = 1, lists = new[] { identity } })));
                await Error(code, () => f.Catalog.SearchAsync(new("kg", "词", MusicSearchKind.Playlists)));
                Equal(1, f.Calls);
            });
        yield return Case("kg agreeing search and detail global fields preserve one identity", async () =>
        {
            using var f = new Fixture(_ => Kg(J(new { total = 1, lists = new[] {
                new { specialid = 99, gid = "collection-A", global_collection_id = "collection-A", specialname = "搜索歌单" } } })));
            Equal("collection-A", (await f.Catalog.SearchAsync(new("kg", "词", MusicSearchKind.Playlists)))
                .GetProperty("items")[0].GetProperty("id").GetString());
            Equal(1, f.Calls);
        });
        yield return Case("kg search playlist detail rejects a different declared global collection", async () =>
        {
            using var f = new Fixture(_ => Kg(J(new { list_info = new { global_collection_id = "collection-B", count = 0, name = "另一歌单" } })));
            await Error("IDENTITY_MISMATCH", () => f.Catalog.PlaylistAsync(new("kg", "collection-A")));
            Equal(1, f.Calls);
        });
        yield return Case("kg search playlist detail rejects disagreeing total counts", async () =>
        {
            using var f = new Fixture(_ => Kg(J(new { list_info = new { global_collection_id = "collection-A", count = 2, name = "搜索歌单" },
                count = 3, songs = new[] { KgSong() } })));
            await Error("INVALID_RESPONSE", () => f.Catalog.PlaylistAsync(new("kg", "collection-A")));
            Equal(1, f.Calls);
        });
        foreach (string platform in new[] { "wy", "tx", "kg" })
            foreach (var kind in new[] { MusicSearchKind.Songs, MusicSearchKind.Playlists })
                yield return Case(platform + " " + kind + " native search continues beyond one hundred pages", async () =>
                {
                    bool songs = kind == MusicSearchKind.Songs;
                    using var f = new Fixture(req =>
                    {
                        if (platform == "wy")
                        {
                            var (_, body) = Eapi(req); Equal("100", S(body["offset"]));
                            return songs ? WySearch([Wy("1")], 102) :
                                Json(J(new { code = 200, result = new { playlistCount = 102,
                                    playlists = new[] { new { id = 1, name = "Synthetic list" } } } }));
                        }
                        if (platform == "tx")
                        {
                            Equal("101", S(RequestJson(req)["req_0"]!["param"]!["page_num"]));
                            return Tx(J(new { meta = new { sum = 102, nextpage = 102 },
                                body = new JsonObject { [songs ? "item_song" : "item_songlist"] =
                                    new JsonArray(songs ? Qq() : J(new { id = 1, title = "Synthetic list" })) } }));
                        }
                        VerifyKg(req); Equal("101", Query(req.RequestUri!)["page"]);
                        return Kg(new JsonObject { ["total"] = 102, [songs ? "lists" : "info"] =
                            new JsonArray(songs ? KgSong() : J(new { global_collection_id = "fixture", name = "Synthetic list" })) });
                    });
                    var page = await f.Catalog.SearchAsync(new(platform, "fixture", kind, Page: 101, Limit: 1));
                    Equal(1, page.GetProperty("items").GetArrayLength());
                    True(page.GetProperty("more").GetBoolean());
                    Equal(102, page.GetProperty("continuation").GetProperty("page").GetInt32());
                });
        yield return Case("catalog operation owns deadline and ignores unused provider field quotas", async () =>
        {
            using var f = new Fixture((_, _) => Task.FromResult(Json(J(new
            {
                code = 200, result = new { songCount = 1, songs = new[] { Wy("1") } },
                unusedText = new string('x', 60000), unusedRows = Enumerable.Range(0, 12000).ToArray()
            }))), timeout: TimeSpan.FromMinutes(2));
            var page = await f.Catalog.SearchAsync(new("wy", "fixture", Limit: 1));
            Equal(1, page.GetProperty("items").GetArrayLength());
        });
        yield return Case("kg public search tolerates absent album but not malformed nonempty identity", async () =>
        {
            using var f = new Fixture(_ => {
                var row = KgSong(); row["album_id"] = ""; row["AlbumID"] = "";
                return Kg(J(new { total = 1, lists = new[] { row } }));
            });
            var reply = await f.Catalog.SearchAsync(new("kg", "曲"));
            Equal("0", MusicSongFixture.Info(reply.GetProperty("items")[0]).GetProperty("albumId").GetString());
            Song(reply.GetProperty("items")[0], "kg", KgHash);
            using var bad = new Fixture(_ => {
                var row = KgSong(); row["album_id"] = "-1";
                return Kg(J(new { total = 1, lists = new[] { row } }));
            });
            await Error("INVALID_RESPONSE", () => bad.Catalog.SearchAsync(new("kg", "曲")));
        });
        foreach (var platform in new[] { "wy", "tx", "kg" })
            yield return Case(platform + " songs/playlists distinct search and explicit empty", async () =>
            {
                using var f = new Fixture(req => platform switch {
                    "wy" => Json(J(new { code = 200, result = new { playlistCount = 0, playlists = Array.Empty<object>() } })),
                    "tx" => Tx(J(new { meta = new { sum = 0, nextpage = -1 }, body = new { item_songlist = Array.Empty<object>() } })),
                    _ => Kg(J(new { total = 0, info = Array.Empty<object>() })) });
                var r = await f.Catalog.SearchAsync(new(platform, "x", MusicSearchKind.Playlists));
                Equal(0, r.GetProperty("items").GetArrayLength()); True(!r.GetProperty("more").GetBoolean());
            });
        yield return Case("wy detail checks raw weapi and exact returned id", async () =>
        {
            using var f = new Fixture(req =>
            {
                Weapi(req, "/weapi/v3/song/detail");
                return Json(J(new { code = 200, songs = new[] { Wy(Huge) } }));
            });
            Song(await f.Catalog.SongAsync(new("wy", Huge)), "wy", Huge);
        });
        yield return Case("qq detail retains mid numeric id type and media mid", async () =>
        {
            using var f = new Fixture(req =>
            {
                Equal("CgiGetTrackInfo", S(RequestJson(req)["req_0"]!["method"]));
                return Tx(J(new { tracks = new[] { Qq() } }));
            });
            Song(await f.Catalog.SongAsync(new("tx", "midA001")), "tx", "midA001");
        });
        yield return Case("kg detail signed body verifies every declared hash", async () =>
        {
            using var f = new Fixture(req => { VerifyKg(req); return Json(J(new { status = 1, error_code = 0, data = new[] { KgSong() } })); });
            Song(await f.Catalog.SongAsync(new("kg", KgHash.ToLowerInvariant())), "kg", KgHash);
            using var bad = new Fixture(_ =>
            {
                var row = KgSong(); row["hash"] = new string('A', 32);
                return Json(J(new { status = 1, error_code = 0, data = new[] { row } }));
            });
            await Error("IDENTITY_MISMATCH", () => bad.Catalog.SongAsync(new("kg", KgHash)));
        });
        yield return Case("wy playlist without embedded tracks batches the selected page and restores order", async () =>
        {
            using var f = new Fixture(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal))
                {
                    var (path, body) = Eapi(req);
                    Equal("/api/v6/playlist/detail", path);
                    Equal("2", S(body["n"]));
                    return WyList(["1", Huge, "3"]);
                }
                Weapi(req, "/weapi/v3/song/detail");
                return Json(J(new { code = 200, songs = new[] { Wy(Huge), Wy("1") } }));
            });
            var r = await f.Catalog.PlaylistAsync(new("wy", "500", Limit: 2));
            Equal("1", MusicSongFixture.NativeId(r.GetProperty("items")[0]));
            Equal(Huge, MusicSongFixture.NativeId(r.GetProperty("items")[1]));
            True(r.GetProperty("more").GetBoolean()); Equal("ordered-membership", r.GetProperty("snapshotStrength").GetString());
            Equal(2, f.Calls);
        });
        yield return Case("wy playlist reuses embedded display tracks across pages without detail reads", async () =>
        {
            var firstSong = Wy(Huge); firstSong.Remove("dt"); firstSong.Remove("ar");
            firstSong["al"]!.AsObject().Remove("id"); firstSong["al"]!.AsObject().Remove("name");
            var secondSong = Wy("1"); secondSong.Remove("dt"); secondSong.Remove("al");
            var playlists = 0;
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req); Equal("/api/v6/playlist/detail", path);
                Equal(++playlists == 1 ? "2" : "0", S(body["n"]));
                return WyList([Huge, "1", "3"], [Wy("3"), secondSong, firstSong]);
            });
            var first = await f.Catalog.PlaylistAsync(new("wy", "500", Limit: 2));
            Song(first.GetProperty("items")[0], "wy", Huge);
            Song(first.GetProperty("items")[1], "wy", "1");
            Equal(0d, first.GetProperty("items")[0].GetProperty("duration").GetDouble());
            Equal("https://images.invalid/a.jpg", first.GetProperty("items")[0].GetProperty("cover_url").GetString());
            Equal("", first.GetProperty("items")[1].GetProperty("cover_url").GetString());
            Equal(1, f.Calls);
            var snapshot = first.GetProperty("snapshot").GetString();
            var next = await f.Catalog.PlaylistAsync(new("wy", "500", 2, 2, snapshot));
            Equal(1, next.GetProperty("items").GetArrayLength()); Song(next.GetProperty("items")[0], "wy", "3");
            Equal(snapshot, next.GetProperty("snapshot").GetString()); True(!next.GetProperty("more").GetBoolean());
            Equal(2, f.Calls);
            var beyond = await f.Catalog.PlaylistAsync(new("wy", "500", 3, 2, snapshot));
            Equal(0, beyond.GetProperty("items").GetArrayLength()); Equal(3, f.Calls);
        });
        yield return Case("wy recommended playlist accepts repeated unused track alg and retains preview songs", async () =>
        {
            var tracks = Enumerable.Range(1, 3).Select(i =>
                Wy(i.ToString()).ToJsonString().Insert(1, "\"alg\":null,\"alg\":\"recommendation\",")).ToArray();
            var json = "{\"code\":200,\"playlist\":{\"id\":\"500\",\"name\":\"推荐歌单\",\"trackCount\":3," +
                "\"trackIds\":[{\"id\":\"1\"},{\"id\":\"2\"},{\"id\":\"3\"}],\"tracks\":[" + string.Join(",", tracks) + "]}}";
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req);
                Equal("/api/v6/playlist/detail", path); Equal("3", S(body["n"]));
                return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });
            var reply = await f.Catalog.PlaylistAsync(new("wy", "500", Limit: 3));
            var cards = MusicCatalogDto.Read(reply, "wy", "playlist", "").SelectMany(section => section.Items).ToArray();
            Equal(3, cards.Length);
            for (int i = 0; i < cards.Length; i++)
            {
                Song(reply.GetProperty("items")[i], "wy", (i + 1).ToString());
                Equal("测试曲", cards[i].Title);
            }
            Equal(1, f.Calls);
        });
        yield return Case("wy final response fields still enforce playlist and recording identity", async () =>
        {
            foreach (var (json, error) in new[]
            {
                ("""{"code":200,"playlist":{"id":"500","id":"501"}}""", "IDENTITY_MISMATCH"),
                ("""{"code":200,"playlist":{"id":"500","trackIds":[{"id":"1"}],"trackCount":1,"tracks":[{"id":"1","id":"2","name":"other"}]}}""", "IDENTITY_MISMATCH"),
                ("""{"code":200,"code":500,"playlist":{}}""", "UPSTREAM_ERROR")
            })
            {
                using var f = new Fixture(_ => new(HttpStatusCode.OK)
                    { Content = new StringContent(json, Encoding.UTF8, "application/json") });
                await Error(error, () => f.Catalog.PlaylistAsync(new("wy", "500", Limit: 3)));
                Equal(1, f.Calls);
            }
            using var other = new Fixture(_ => new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200,"alg":null,"alg":"x","data":{"blocks":[]}}""", Encoding.UTF8, "application/json")
            });
            var home = await other.Catalog.HomeAsync(new("wy"));
            Equal(0, home.GetProperty("sections").GetArrayLength());
            Equal(1, other.Calls);
        });
        yield return Case("wy playlist batches only missing current page display members once", async () =>
        {
            var embedded = Wy(Huge); embedded["name"] = "歌单原曲"; embedded.Remove("dt");
            using var f = new Fixture(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal))
                {
                    var (path, body) = Eapi(req); Equal("/api/v6/playlist/detail", path); Equal("0", S(body["n"]));
                    return WyList(["1", "2", "3", "4", Huge, "6", "7", "8", "9"],
                        [Wy("1"), J(new { id = "2" }), Wy("9"), embedded, J(new { id = "6" }), J(new { id = "7", name = " " })]);
                }
                Weapi(req, "/weapi/v3/song/detail");
                return Json(J(new { code = 200, songs = new[] { Wy("8"), Wy("7"), Wy("6") } }));
            });
            var result = await f.Catalog.PlaylistAsync(new("wy", "500", Page: 2, Limit: 4));
            var items = result.GetProperty("items"); Equal(4, items.GetArrayLength());
            Song(items[0], "wy", Huge); Equal("歌单原曲", items[0].GetProperty("title").GetString());
            Song(items[1], "wy", "6"); Song(items[2], "wy", "7"); Song(items[3], "wy", "8");
            Equal(9L, result.GetProperty("total").GetInt64()); True(result.GetProperty("more").GetBoolean());
            Equal(2, f.Calls);
        });
        yield return Case("wy playlist complete embedded membership fallback needs no detail read", async () =>
        {
            using var f = new Fixture(req =>
            {
                var (path, _) = Eapi(req); Equal("/api/v6/playlist/detail", path);
                return Json(J(new { code = 200, playlist = new { id = "500", name = "列表", trackCount = 2,
                    tracks = new[] { Wy(Huge), Wy("1") } } }));
            });
            var result = await f.Catalog.PlaylistAsync(new("wy", "500", Limit: 1));
            Song(result.GetProperty("items")[0], "wy", Huge);
            Equal(2L, result.GetProperty("total").GetInt64()); Equal(1, f.Calls);
        });
        yield return Case("wy playlist invalid embedded identities fail before any detail request", async () =>
        {
            var conflicting = Wy("1"); conflicting["songInfo"] = Wy("2");
            foreach (var tracks in new[] { new[] { Wy("1"), Wy("1") }, new[] { Wy("4") }, new[] { conflicting } })
            {
                using var f = new Fixture(req =>
                {
                    var (path, _) = Eapi(req); Equal("/api/v6/playlist/detail", path);
                    return WyList(["1", "2", "3"], tracks);
                });
                await Error("IDENTITY_MISMATCH", () => f.Catalog.PlaylistAsync(new("wy", "500", Limit: 2)));
                Equal(1, f.Calls);
            }
            foreach (var id in new[] { "0", "01" })
            {
                using var f = new Fixture(_ => WyList([id], [J(new { id, name = "测试曲" })]));
                await Error("INVALID_REQUEST", () => f.Catalog.PlaylistAsync(new("wy", "500")));
                Equal(1, f.Calls);
            }
        });
        yield return Case("wy playlist missing member detail failures never retry or publish partial songs", async () =>
        {
            foreach (var songs in new[] { Array.Empty<JsonObject>(), new[] { Wy("1") },
                new[] { Wy("2"), Wy("2") }, new[] { Wy("2"), Wy("3") } })
            {
                using var f = new Fixture(req =>
                {
                    if (req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal))
                    {
                        var (path, _) = Eapi(req); Equal("/api/v6/playlist/detail", path);
                        return WyList(["1", "2", "3"], [Wy("1")]);
                    }
                    Weapi(req, "/weapi/v3/song/detail");
                    return Json(J(new { code = 200, songs }));
                });
                await Error("IDENTITY_MISMATCH", () => f.Catalog.PlaylistAsync(new("wy", "500", Limit: 2)));
                Equal(2, f.Calls);
            }
        });
        yield return Case("wy playlist page 100 uses one detail batch without prefix prefetch", async () =>
        {
            var ids = Enumerable.Range(1, 10000).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            using var f = new Fixture(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal))
                {
                    var (path, body) = Eapi(req); Equal("/api/v6/playlist/detail", path); Equal("0", S(body["n"]));
                    return WyList(ids);
                }
                Weapi(req, "/weapi/v3/song/detail");
                return Json(J(new { code = 200, songs = ids.Skip(9900).Reverse().Select(Wy).ToArray() }));
            });
            var result = await f.Catalog.PlaylistAsync(new("wy", "500", Page: 100, Limit: 100));
            Equal(100, result.GetProperty("items").GetArrayLength());
            Song(result.GetProperty("items")[0], "wy", "9901"); Song(result.GetProperty("items")[99], "wy", "10000");
            True(!result.GetProperty("more").GetBoolean()); Equal(2, f.Calls);
        });
        yield return Case("wy playlist cancellation drains the physical read without extra member requests", async () =>
        {
            foreach (var holdDetail in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var f = new Fixture(async (req, token) =>
                {
                    if (req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal))
                    {
                        var (path, _) = Eapi(req); Equal("/api/v6/playlist/detail", path);
                        if (holdDetail) return WyList(["1", "2"], [Wy("1")]);
                    }
                    else Weapi(req, "/weapi/v3/song/detail");
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult(); return await delayed.Task;
                });
                using var cancel = new CancellationTokenSource();
                var task = f.Catalog.PlaylistAsync(new("wy", "500", Limit: 2), cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
                try
                {
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); True(!task.IsCompleted);
                    Equal(holdDetail ? 2 : 1, f.Calls);
                }
                finally { delayed.TrySetResult(holdDetail ? Json(J(new { code = 200, songs = new[] { Wy("2") } })) : WyList(["1", "2"], [Wy("1")])); }
                await Error("CANCELLED", () => task); Equal(holdDetail ? 2 : 1, f.Calls);
            }
        });
        yield return Case("wy incomplete playlist never succeeds with partial tracks", async () =>
        {
            using var f = new Fixture(_ => Json(J(new { code = 200, playlist = new { id = "500", name = "列表", trackCount = 2, trackIds = new[] { new { id = "1" } } } })));
            await Error("INCOMPLETE_PLAYLIST", () => f.Catalog.PlaylistAsync(new("wy", "500")));
            Equal(1, f.Calls);
        });
        yield return Case("wy snapshot changed refuses fetching next songs", async () =>
        {
            var changed = false;
            using var f = new Fixture(req => req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal)
                ? WyList(changed ? ["2", "1"] : ["1", "2"]) : Json(J(new { code = 200, songs = new[] { Wy("1") } })));
            var first = await f.Catalog.PlaylistAsync(new("wy", "500", Limit: 1)); changed = true;
            await Error("SNAPSHOT_CHANGED", () => f.Catalog.PlaylistAsync(new("wy", "500", 2, 1, first.GetProperty("snapshot").GetString())));
            Equal(3, f.Calls);
        });
        yield return Case("qq playlist integer payload remains exact above double precision", async () =>
        {
            using var f = new Fixture(req =>
            {
                var body = RequestJson(req); Equal(Huge, body["req_0"]!["param"]!["disstid"]!.ToJsonString());
                return Tx(J(new { total_song_num = 1, hasmore = 0, songlist_size = 1,
                    dirinfo = new { id = Huge, title = "列表" }, songlist = new[] { Qq() } }));
            });
            var r = await f.Catalog.PlaylistAsync(new("tx", Huge)); Equal(Huge, r.GetProperty("id").GetString());
            Equal("metadata-only", r.GetProperty("snapshotStrength").GetString());
        });
        yield return Case("kg playlist targets real global id with strict page counts", async () =>
        {
            using var f = new Fixture(req =>
            {
                VerifyKg(req); Equal("global-A", Query(req.RequestUri!)["global_collection_id"]);
                return Kg(J(new { list_info = new { global_collection_id = "global-A", count = 1, name = "列表" }, songs = new[] { KgSong() } }));
            });
            var r = await f.Catalog.PlaylistAsync(new("kg", "global-A"));
            Song(r.GetProperty("items")[0], "kg", KgHash);
            await Error("INVALID_REQUEST", () => f.Catalog.PlaylistAsync(new("kg", "id_fake")));
            Equal(1, f.Calls);
        });
        foreach (var platform in new[] { "wy", "tx", "kg" })
            yield return Case(platform + " empty playlist retains complete native owner page contract", async () =>
            {
                using var f = new Fixture(_ => platform switch {
                    "wy" => Json(J(new { code = 200, playlist = new { id = "500", name = "空列表", trackCount = 0 } })),
                    "tx" => Tx(J(new { total_song_num = 0, hasmore = 0, songlist_size = 0, dirinfo = new { id = "500", title = "空列表" } })),
                    _ => Kg(J(new { list_info = new { global_collection_id = "500", count = 0, name = "空列表" } })) });
                var result = await f.Catalog.PlaylistAsync(new(platform, "500"));
                Equal(platform, result.GetProperty("platform").GetString()); Equal("500", result.GetProperty("id").GetString());
                Equal(1, result.GetProperty("page").GetInt32()); Equal(100, result.GetProperty("limit").GetInt32());
                Equal(0, result.GetProperty("items").GetArrayLength()); Equal(0L, result.GetProperty("total").GetInt64());
                True(!result.GetProperty("more").GetBoolean()); Equal(JsonValueKind.Null, result.GetProperty("continuation").ValueKind);
                Equal(64, result.GetProperty("snapshot").GetString()!.Length); Equal(1, f.Calls);
            });
        yield return Case("qq short nonterminal playlist page fails rather than losing members", async () =>
        {
            using var f = new Fixture(_ => Tx(J(new { total_song_num = 3, hasmore = 1, songlist_size = 1,
                dirinfo = new { id = "500", title = "列表" }, songlist = new[] { Qq() } })));
            await Error("INCOMPLETE_PAGE", () => f.Catalog.PlaylistAsync(new("tx", "500", Limit: 2)));
        });
        foreach (var (title, artist, duration, expected) in new[] {
            ("Test (Live)", "B; A", 185000, "song:18c66609b3825777"),
            ("星😀 (Live)", "Ｂ; A", 185000, "song:738530dd4024f5df"),
            ("歌曲live", "艺人", 185000, "song:b1d5fc2b8f357441"),
            ("A\u2028B\u2029C", "A\u0085B", 185000, "song:aab6a0c14f208ccb"),
            ("星😀", "艺人", 0, "song:ee628a1de1e9aa73") })
            yield return Case("identity old JS golden " + expected, async () =>
            {
                var raw = Wy(Huge); raw["name"] = title; raw["ar"] = new JsonArray(J(new { name = artist }));
                raw["dt"] = duration;
                using var f = new Fixture(_ => WySearch([raw], 1));
                var result = (await f.Catalog.SearchAsync(new("wy", "x"))).GetProperty("items")[0];
                Equal(expected, result.GetProperty("songId").GetString());
                Equal("[\"wy\",\"" + Huge + "\",\"\"]", result.GetProperty("sources")[0].GetProperty("sourceKey").GetString());
            });
        yield return Case("home modern DSL retains mixed sections and recognized unknown actions", async () =>
        {
            using var f = new Fixture(req =>
            {
                Equal("/api/link/page/rcmd/resource/show", Eapi(req).Path);
                return Home(false, "1", J(new { resourceType = "playlist", resourceId = Huge, name = "歌单" }),
                    J(new { resourceType = "song", resourceExtInfo = new { songData = Wy("1") } }),
                    J(new { resourceType = "album", resourceId = "30", name = "专辑" }));
            });
            var r = await f.Catalog.HomeAsync(new("wy"));
            Equal(3, r.GetProperty("sections").GetArrayLength());
            Equal(Huge, r.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetString());
            Song(r.GetProperty("sections")[1].GetProperty("items")[0], "wy", "1");
        });
        yield return Case("home daily requires account and no H5 execution", async () =>
        {
            using var f = new Fixture(_ => Home(false, "1", J(new { resourceType = "dailysongs", name = "每日推荐" }),
                J(new { resourceType = "mv", resourceId = "31", name = "MV", action = "https://evil.invalid/run" })));
            var r = await f.Catalog.HomeAsync(new("wy"));
            Equal(1, r.GetProperty("entries").GetArrayLength());
            True(!r.GetProperty("entries")[0].GetProperty("available").GetBoolean());
            Equal("AUTH_REQUIRED", r.GetProperty("entries")[0].GetProperty("errorCode").GetString());
            True(r.GetProperty("entries")[0].GetProperty("requiresAccount").GetBoolean());
            True(!r.GetProperty("entries")[0].GetProperty("display").GetBoolean());
            Equal("preview", r.GetProperty("entries")[0].GetProperty("presentation").GetString());
            Equal(1, f.Calls);

            using var signed = new Fixture(_ => Home(false, "1",
                J(new { resourceType = "dailysongs", name = "每日推荐" })), Session("wy"));
            var signedEntry = (await signed.Catalog.HomeAsync(new("wy"))).GetProperty("entries")[0];
            True(signedEntry.GetProperty("available").GetBoolean() &&
                signedEntry.GetProperty("requiresAccount").GetBoolean() &&
                signedEntry.GetProperty("display").GetBoolean());
            Equal(1, signed.Calls);
        });
        yield return Case("home daily preserves provider calendar and permanent full caption without inventing songs", async () =>
        {
            foreach (var day in new[] { "29", "0", "32", "" })
            {
                using var f = new Fixture(_ => Json(J(new { code = 200, data = new { hasMore = false,
                    blocks = new[] { new { positionCode = "ENTRY", dslData = new { dslShowTitle = false,
                        blockResource = new { resources = new[] { new { resourceType = "dailySongs",
                            title = "每日推荐 | 从「合成歌曲」听起", subTitle = "10-29", dayOfMonth = day,
                            singleLineTitle = "符合你口味的新鲜好歌" } } } } } } } })));
                var reply = await f.Catalog.HomeAsync(new("wy"));
                var entry = reply.GetProperty("entries")[0];
                Equal("每日推荐", entry.GetProperty("name").GetString());
                Equal("从「合成歌曲」听起", entry.GetProperty("subtitle").GetString());
                var card = MusicCatalogDto.Read(reply, "wy", "home", "").Single().Items.Single();
                Equal(day == "29" ? 29 : (int?)null, card.DayOfMonth);
                True(!card.HasPreview && !card.CanOpen);
                Equal(1, f.Calls);
            }
        });
        yield return Case("home entries publish explicit lowercase presentation for every entry behavior", async () =>
        {
            using var f = new Fixture(_ => Home(false, "1",
                J(new { resourceType = "playlist", resourceId = "500", name = "歌单" }),
                J(new { resourceType = "dailysongs", name = "每日推荐" }),
                J(new { resourceType = "album", resourceId = "30", name = "专辑" }),
                J(new { resourceType = "similarsong", name = "相似歌曲",
                    action = "orpheus://nm/play/similarFM?sourceType=song&sourceId=%5B%221%22%5D" }),
                J(new { resourceType = "fm", name = "私人漫游", mode = "DEFAULT" }),
                J(new { resourceType = "star", name = "心动模式" }),
                J(new { resourceType = "similarartist", name = "相似艺人" })));
            var entries = (await f.Catalog.HomeAsync(new("wy"))).GetProperty("entries").EnumerateArray().ToArray();
            Equal(7, entries.Length);
            var presentations = entries.ToDictionary(
                entry => entry.GetProperty("action").GetProperty("type").GetString()!,
                entry => entry.GetProperty("presentation").GetString()!,
                StringComparer.Ordinal);
            Equal("preview", presentations["playlist"]);
            Equal("preview", presentations["daily"]);
            Equal("preview", presentations["album"]);
            Equal("overlay", presentations["similar"]);
            Equal("animated", presentations["fm"]);
            Equal("animated", presentations["heartbeat"]);
            Equal("static", presentations["unsupported"]);
        });
        yield return Case("home repeated cursor and cross-session continuation rejected", async () =>
        {
            using var f = new Fixture(_ => Home(true, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" })));
            var r = await f.Catalog.HomeAsync(new("wy"));
            await Error("PAGINATION_STALLED", () => f.Catalog.HomeAsync(new("wy", r.GetProperty("continuation"))));
            f.Sessions.Current = Session("wy");
            await Error("STALE_SESSION", () => f.Catalog.HomeAsync(new("wy", r.GetProperty("continuation"))));
            Equal(2, f.Calls);
        });
        yield return Case("channels uses directory actions no arbitrary pageCode", async () =>
        {
            using var f = new Fixture(_ => Directory());
            var r = await f.Catalog.ChannelsAsync(new("wy"));
            var items = r.GetProperty("items").EnumerateArray().ToArray();
            var rock = r.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "rock");
            Equal("channel", rock.GetProperty("kind").GetString()); Equal("MUSIC_ROCK", rock.GetProperty("pageCode").GetString());
            Equal("more", rock.GetProperty("placement").GetString()); True(rock.GetProperty("available").GetBoolean());
            Equal("歌单广场", items.Single(c => c.GetProperty("id").GetString() == "playlist").GetProperty("name").GetString());
            var artist = items.Single(c => c.GetProperty("id").GetString() == "artist");
            Equal("channel", artist.GetProperty("kind").GetString()); Equal("primary", artist.GetProperty("placement").GetString());
            True(artist.GetProperty("available").GetBoolean());
            Equal("MUSIC_ACG", items.Single(c => c.GetProperty("id").GetString() == "acg").GetProperty("pageCode").GetString());
            True(!items.Any(c => c.GetProperty("id").GetString() == "music_top_tab_acg"));
            await Error("IDENTITY_MISMATCH", () => f.Catalog.ChannelAsync(new("wy", "rock", PageCode: "OTHER_PAGE")));
        });
        yield return Case("playlist channel exposes primary filters and binds continuation to the selected category", async () =>
        {
            using var f = new Fixture(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal)) return Directory();
                Weapi(req, "/weapi/playlist/square/block/page");
                return Home(true, "opaque-cursor", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
            });
            var page = await f.Catalog.ChannelAsync(new("wy", "playlist", Tag: "摇滚"));
            Equal("推荐,官方,华语,摇滚,民谣,电子,轻音乐", string.Join(",", page.GetProperty("tags")
                .EnumerateArray().Select(tag => tag.GetProperty("id").GetString())));
            Equal("摇滚", page.GetProperty("selectedTag").GetString());
            var continuation = page.GetProperty("continuation");
            Equal("摇滚", continuation.GetProperty("tag").GetString());
            await Error("SNAPSHOT_CHANGED", () => f.Catalog.ChannelAsync(new("wy", "playlist",
                Continuation: continuation, Tag: "民谣")));
            await Error("INVALID_REQUEST", () => f.Catalog.ChannelAsync(new("wy", "playlist", Tag: "不存在")));
        });
        yield return Case("rankings channel and new songs channel return actual typed sections", async () =>
        {
            static object Group(string name, int count, string layout, int seed) => new
            {
                name,
                categoryCode = "group-" + seed,
                frontDisplayType = layout,
                list = Enumerable.Range(0, count).Select(index => new
                {
                    targetType = "PLAYLIST",
                    resourceId = (seed + index).ToString(),
                    name = name + " " + (index + 1),
                    rankItems = new[] { new { trackId = "1", songName = "测试曲", artistName = "测试艺人", rank = 1, lastRank = 2 } }
                }).ToArray()
            };
            var groups = new[]
            {
                Group("榜单推荐", 6, "ONLY_COVER", 100),
                Group("官方榜", 4, "ranked-songs", 200),
                Group("精选榜", 5, "ONLY_COVER", 300),
                Group("曲风榜", 7, "ONLY_COVER", 400),
                Group("全球榜", 6, "ONLY_COVER", 500),
                Group("语种榜", 7, "ONLY_COVER", 600),
                Group("特色榜", 3, "ONLY_COVER", 700)
            };
            using var f = new Fixture(req => req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal) ? Directory()
                : req.RequestUri.AbsolutePath.Contains("toplist", StringComparison.Ordinal)
                ? Json(J(new { code = 200, data = groups }))
                : Json(J(new { code = 200, data = new[] { Wy("1") } })));
            var ranking = await f.Catalog.ChannelAsync(new("wy", "chart"));
            var sections = ranking.GetProperty("sections").EnumerateArray().ToArray();
            var expected = new[]
            {
                ("榜单推荐", 6, "ONLY_COVER"),
                ("官方榜", 4, "ranked-songs"),
                ("精选榜", 5, "ONLY_COVER"),
                ("曲风榜", 7, "ONLY_COVER"),
                ("全球榜", 6, "ONLY_COVER"),
                ("语种榜", 7, "ONLY_COVER"),
                ("特色榜", 3, "ONLY_COVER")
            };
            Equal(expected.Length, sections.Length);
            for (var index = 0; index < expected.Length; index++)
            {
                Equal(expected[index].Item1, sections[index].GetProperty("title").GetString());
                Equal("rankings", sections[index].GetProperty("type").GetString());
                Equal(expected[index].Item2, sections[index].GetProperty("items").GetArrayLength());
                Equal(expected[index].Item3, sections[index].GetProperty("layout").GetString());
            }
            Equal(1, sections[1].GetProperty("items")[0].GetProperty("preview").GetArrayLength());
            var fresh = await f.Catalog.ChannelAsync(new("wy", "newsong"));
            Song(fresh.GetProperty("sections")[0].GetProperty("items")[0], "wy", "1");
        });
        yield return Case("artist channel keeps grouped selection token and advancing continuation", async () =>
        {
            int reads = 0;
            using var f = new Fixture(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal)) return Directory();
                Weapi(req, "/weapi/v1/artist/list");
                int page = reads++;
                return Json(J(new
                {
                    code = 200,
                    artists = Enumerable.Range(1 + page * 2, 2).Select(id => new
                    {
                        id,
                        name = "歌手 " + id,
                        picUrl = "https://images.invalid/artist-" + id + ".jpg",
                        musicSize = 100 + id
                    }),
                    more = page == 0
                }));
            });
            var first = await f.Catalog.ChannelAsync(new("wy", "artist", Tag: "96:2:65"));
            Equal("歌手", first.GetProperty("title").GetString());
            var section = first.GetProperty("sections")[0];
            Equal("artists", section.GetProperty("type").GetString());
            Equal("artist", section.GetProperty("items")[0].GetProperty("kind").GetString());
            Equal(38, first.GetProperty("tags").GetArrayLength());
            var selected = first.GetProperty("tags").EnumerateArray()
                .Where(tag => tag.GetProperty("selected").GetBoolean()).ToArray();
            Equal(3, selected.Length);
            Equal("area,initial,type", string.Join(",", selected.Select(tag => tag.GetProperty("group").GetString())
                .OrderBy(value => value, StringComparer.Ordinal)));
            True(selected.All(tag => tag.GetProperty("id").GetString() == "96:2:65"));
            var continuation = first.GetProperty("continuation");
            Equal(2, continuation.GetProperty("offset").GetInt32());
            Equal("96:2:65", continuation.GetProperty("tag").GetString());
            var second = await f.Catalog.ChannelAsync(new("wy", "artist",
                Continuation: continuation, Tag: "96:2:65"));
            True(!second.GetProperty("more").GetBoolean());
            Equal("3", second.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetString());
            await Error("SNAPSHOT_CHANGED", () => f.Catalog.ChannelAsync(new("wy", "artist",
                Continuation: continuation, Tag: "96:1:65")));
            await Error("INVALID_REQUEST", () => f.Catalog.ChannelAsync(new("wy", "artist", Tag: "96:x:65")));
        });
        yield return Case("artist list and details prefer square artwork and do not lose a fallback behind empty metadata", async () =>
        {
            foreach (var square in new[] { "", "http://p1.music.126.net/avatar/109951100000001.jpg" })
            {
                var row = J(new { id = "30", name = "歌手", picUrl = "https://images.invalid/portrait.jpg", img1v1Url = square });
                using var f = new Fixture(req =>
                    req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal) ? Directory() :
                    req.RequestUri.AbsolutePath.EndsWith("/list", StringComparison.Ordinal)
                        ? Json(J(new { code = 200, artists = new[] { row }, more = false }))
                        : Json(J(new { code = 200, artist = row, hotSongs = Array.Empty<object>() })));
                var expected = square.Length == 0 ? "https://images.invalid/portrait.jpg" :
                    "https://p1.music.126.net/avatar/109951100000001.jpg";
                var channel = await f.Catalog.ChannelAsync(new("wy", "artist"));
                Equal(expected, channel.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("cover").GetString());
                Equal(expected, (await f.Catalog.ArtistAsync(new("wy", "30"))).GetProperty("cover").GetString());
            }
        });
        yield return Case("wy home and search use later real display fields when optional image and title fields are empty", async () =>
        {
            using var f = new Fixture(_ => Home(false, "1", J(new
            {
                resourceType = "playlist", resourceId = "500", name = "", title = "真实歌单",
                cover = "", coverImg = "", picUrl = "https://images.invalid/real.jpg"
            })));
            var card = MusicCatalogDto.Read(await f.Catalog.HomeAsync(new("wy")), "wy", "home", "")
                .SelectMany(section => section.Items).Single();
            Equal("真实歌单", card.Title); Equal("https://images.invalid/real.jpg", card.Cover);

            using var search = new Fixture(_ => Json(J(new { code = 200, result = new
            {
                playlistCount = 1,
                playlists = new[] { new { id = "500", name = "", title = "真实歌单", picUrl = "", coverImgUrl = "https://images.invalid/real.jpg" } }
            } })));
            var item = (await search.Catalog.SearchAsync(new("wy", "x", Kind: MusicSearchKind.Playlists))).GetProperty("items")[0];
            Equal("真实歌单", item.GetProperty("name").GetString());
            Equal("https://images.invalid/real.jpg", item.GetProperty("cover").GetString());
        });
        yield return Case("wy song search and embedded playlist use real title and cover behind empty optional fields", async () =>
        {
            var raw = Wy("1"); raw["name"] = ""; raw["title"] = "真实歌曲";
            raw["al"]!["picUrl"] = ""; raw["picUrl"] = ""; raw["coverUrl"] = "https://images.invalid/real.jpg";
            using var search = new Fixture(_ => WySearch([raw], 1));
            var song = (await search.Catalog.SearchAsync(new("wy", "x"))).GetProperty("items")[0];
            Equal("真实歌曲", song.GetProperty("title").GetString());
            Equal("https://images.invalid/real.jpg", song.GetProperty("cover_url").GetString());
            using var playlist = new Fixture(req =>
            {
                True(req.RequestUri!.AbsolutePath.Contains("/playlist/", StringComparison.Ordinal));
                return WyList(["1"], [raw]);
            });
            var member = (await playlist.Catalog.PlaylistAsync(new("wy", "500"))).GetProperty("items")[0];
            Equal("真实歌曲", member.GetProperty("title").GetString());
            Equal("https://images.invalid/real.jpg", member.GetProperty("cover_url").GetString());
            Equal(1, playlist.Calls);
        });
        yield return Case("wy home consumes final fields in embedded provider JSON without executing templates", async () =>
        {
            using var f = new Fixture(_ => Json(J(new { code = 200, data = new { blocks = new[] {
                new { positionCode = "PLAYLIST", dslData =
                    """
                    {"blockResource":{"header":{"title":"旧栏目","title":"推荐"},"items":[
                      {"resourceType":"playlist","resourceId":"500","name":"旧歌单","name":"真实歌单",
                       "cover":"https://images.invalid/real.jpg",
                       "playBtnData":"{\"detailUrl\":\"orpheus://playlist/499\",\"detailUrl\":\"orpheus://playlist/500\"}"}
                    ]}}
                    """ }
            } } })));
            var section = MusicCatalogDto.Read(await f.Catalog.HomeAsync(new("wy")), "wy", "home", "").Single();
            var card = section.Items.Single();
            Equal("推荐", section.Title); Equal("真实歌单", card.Title);
            Equal("500", card.ActionId); Equal("https://images.invalid/real.jpg", card.Cover);
            Equal(1, f.Calls);
        });
        yield return Case("artist continuation supports large offsets without truncation or integer overflow", async () =>
        {
            using var f = new Fixture(req => req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal)
                ? Directory() : Json(J(new { code = 200, artists = new[] { new { id = "1", name = "歌手" } }, more = true })));
            var first = await f.Catalog.ChannelAsync(new("wy", "artist"));
            var cursor = JsonNode.Parse(first.GetProperty("continuation").GetRawText())!.AsObject();
            cursor["offset"] = (long)int.MaxValue + 40;
            var next = await f.Catalog.ChannelAsync(new("wy", "artist",
                Continuation: JsonSerializer.SerializeToElement(cursor)));
            Equal((long)int.MaxValue + 41, next.GetProperty("continuation").GetProperty("offset").GetInt64());
        });
        yield return Case("VIP channel uses native recommendation and filters explicit member rankings", async () =>
        {
            using var f = new Fixture(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                if (path.Contains("vipnewcenter", StringComparison.Ordinal))
                {
                    var (api, _) = Eapi(req);
                    Equal("/api/vipnewcenter/app/viptab/recommend/song/list", api);
                    return Json(J(new { code = 200, data = new { title = "黑胶VIP 每日推荐",
                        songInfoList = new[] { Wy("1"), Wy("2") } } }));
                }
                Weapi(req, "/weapi/toplist/detail/v2");
                return Json(J(new { code = 200, data = new[] { new { name = "排行榜", categoryCode = "vip",
                    frontDisplayType = "ranked-songs", list = new object[] {
                        new { targetType = "PLAYLIST", resourceId = "500", name = "黑胶VIP 热歌榜", canPlay = true,
                            rankItems = new[] { new { trackId = "1", songName = "会员歌曲", artistName = "会员歌手", rank = 1, lastRank = 2 } } },
                        new { targetType = "PLAYLIST", resourceId = "501", name = "黑胶SVIP 飙升榜", canPlay = true,
                            rankItems = new[] { new { trackId = "2", songName = "会员新歌", artistName = "会员歌手", rank = 1, lastRank = 0 } } },
                        new { targetType = "PLAYLIST", resourceId = "502", name = "普通热歌榜", canPlay = true }
                    } } } }));
            });
            var page = await f.Catalog.ChannelAsync(new("wy", "vip"));
            Equal(2, page.GetProperty("sections").GetArrayLength());
            var recommendations = page.GetProperty("sections")[0];
            Equal("songs", recommendations.GetProperty("type").GetString());
            Equal(2, recommendations.GetProperty("items").GetArrayLength());
            Song(recommendations.GetProperty("items")[0], "wy", "1");
            var rankings = page.GetProperty("sections")[1];
            Equal("vip-ranked-songs", rankings.GetProperty("layout").GetString());
            Equal("黑胶VIP 热歌榜,黑胶SVIP 飙升榜", string.Join(",", rankings.GetProperty("items")
                .EnumerateArray().Select(item => item.GetProperty("name").GetString())));
            Equal(1, rankings.GetProperty("items")[0].GetProperty("preview").GetArrayLength());
        });
        yield return Case("VIP channel preserves a successful section and rejects two failed branches", async () =>
        {
            static Fixture Branches(string recommendations, string rankings) => new(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                if (path.Contains("vipnewcenter", StringComparison.Ordinal))
                    return recommendations switch
                    {
                        "failed" => new(HttpStatusCode.ServiceUnavailable),
                        "invalid" => Json(J(new { code = 200, data = new { } })),
                        "success" => Json(J(new { code = 200, data = new { songInfoList = new[] { Wy("1") } } })),
                        _ => throw new InvalidOperationException("Unknown recommendation branch.")
                    };
                return rankings switch
                {
                    "failed" => new(HttpStatusCode.ServiceUnavailable),
                    "invalid" => Json(J(new { code = 200, data = new { } })),
                    "success" => Json(J(new { code = 200, data = new[] { new { name = "排行榜", categoryCode = "vip",
                        frontDisplayType = "ranked-songs", list = new[] {
                            new { targetType = "PLAYLIST", resourceId = "500", name = "黑胶VIP 热歌榜", canPlay = true } } } } })),
                    _ => throw new InvalidOperationException("Unknown ranking branch.")
                };
            });

            using (var f = Branches("failed", "success"))
            {
                var page = await f.Catalog.ChannelAsync(new("wy", "vip"));
                Equal(1, page.GetProperty("sections").GetArrayLength());
                Equal("rankings", page.GetProperty("sections")[0].GetProperty("type").GetString());
            }
            using (var f = Branches("success", "failed"))
            {
                var page = await f.Catalog.ChannelAsync(new("wy", "vip"));
                Equal(1, page.GetProperty("sections").GetArrayLength());
                Equal("songs", page.GetProperty("sections")[0].GetProperty("type").GetString());
            }
            using (var f = Branches("invalid", "success"))
                Equal("rankings", (await f.Catalog.ChannelAsync(new("wy", "vip")))
                    .GetProperty("sections")[0].GetProperty("type").GetString());
            using (var f = Branches("success", "invalid"))
                Equal("songs", (await f.Catalog.ChannelAsync(new("wy", "vip")))
                    .GetProperty("sections")[0].GetProperty("type").GetString());
            using (var f = Branches("failed", "failed"))
                await Error("UPSTREAM_ERROR", () => f.Catalog.ChannelAsync(new("wy", "vip")));
            using (var f = Branches("invalid", "invalid"))
                await Error("INVALID_RESPONSE", () => f.Catalog.ChannelAsync(new("wy", "vip")));
        });
        yield return Case("cube protocol feed tags and next page keep full cursor", async () =>
        {
            using var f = new Fixture(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                if (path.Contains("cube/render", StringComparison.Ordinal)) { Weapi(req, "/weapi/cube/render/page/protocol"); return Cube(); }
                if (path.Contains("tab/tags", StringComparison.Ordinal)) return Json(J(new { code = 200, data = new { algTagInfos = new[] { new { algTagId = "30", algTagName = "摇滚" } } } }));
                var body = Eapi(req).Body; Equal("30", S(body["algTagIds"]));
                return Json(J(new { code = 200, data = new { songs = new[] { Wy(S(body["offset"]) == "0" ? "1" : "2") }, hasMore = S(body["offset"]) == "0" } }));
            });
            var r = await f.Catalog.ChannelAsync(new("wy", "rock", Tag: "30"));
            var continuation = r.GetProperty("continuation"); Equal(30L, continuation.GetProperty("offset").GetInt64());
            Equal("123", continuation.GetProperty("ruleId").GetString());
            Equal("30", r.GetProperty("feed").GetProperty("selectedTag").GetString());
            var next = await f.Catalog.ChannelAsync(new("wy", "rock", Continuation: continuation, Tag: "30"));
            True(!next.GetProperty("more").GetBoolean());
            await Error("SNAPSHOT_CHANGED", () => f.Catalog.ChannelAsync(new("wy", "rock", Continuation: continuation, Tag: "31")));
        });
        yield return Case("cube feed accepts the current null algorithm-tag collection", async () =>
        {
            using var f = new Fixture(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                if (path.Contains("cube/render", StringComparison.Ordinal))
                {
                    Weapi(req, "/weapi/cube/render/page/protocol");
                    return Cube();
                }
                if (path.Contains("tab/tags", StringComparison.Ordinal))
                    return Json(J(new { code = 200, data = new { tabTagId = "", tagShowType = (object?)null, algTagInfos = (object?)null } }));
                return Json(J(new { code = 200, songs = new[] { Wy("1") }, hasMore = false }));
            });
            var page = await f.Catalog.ChannelAsync(new("wy", "euro"));
            Equal(1, page.GetProperty("sections").GetArrayLength());
            Equal(1, page.GetProperty("sections")[0].GetProperty("items").GetArrayLength());
            True(!page.GetProperty("more").GetBoolean());
        });
        yield return Case("cube recommendation entries complete visible playlist summaries and preserve typed actions", async () =>
        {
            const string cover = "http://p1.music.126.net/synthetic/cube.jpg?imageView=1&thumbnail=800y800&enlarge=1%7CimageView%3D1&watermark=&type=1&image=fixture&dx=0&dy=0%7CimageView%3D1";
            var recommendation = J(new { id = "recommend", componentName = "RecommendHorizontal", props = new
            {
                title = "电音旗舰", tab = "electronic", recommendModule = new[] {
                    new { title = "默认", isDefaultTab = true, modules = new object[] {
                        new { type = "fm", mode = "SCENE_RCMD", subMode = "ELECTRONIC", title = "电音漫游", fmLogoTextContent = "电音漫游" },
                        new { type = "song_list", resource = 500, title = "栏目自己的标题", songCount = 7,
                            creator = new { nickname = "原创建者" },
                            rankItems = new[] { new { trackId = "1", songName = "已供预览", artistName = "合成艺人" } } },
                        new { type = "song_list", resource = 501, title = "", cover = "http://p2.music.126.net/synthetic/inline.jpg" },
                        new { type = "album", resource = 30, title = "专辑", cover = "https://images.invalid/album.jpg" },
                        new { type = "song", resource = 1, resourceExtInfo = new { songData = Wy("1") } },
                        new { type = "similarsong", title = "相似歌曲",
                            action = "orpheus://nm/play/similarFM?sourceType=song&sourceId=%5B%221%22%5D" },
                        new { type = "fm", mode = "UNKNOWN", title = "不支持的漫游" }
                    } },
                    new { title = "其他", isDefaultTab = false, modules = new object[] {
                        new { type = "song_list", resource = 500, title = "同一歌单的另一入口" },
                        new { type = "song_list", resource = 599, title = "未选择的歌单" }
                    } }
                }
            } });
            var grid = J(new { id = "grid", componentName = "Grid", children = new[] {
                new { componentName = "Playlist", props = new { id = 700, title = "普通歌单", coverUrl = "https://images.invalid/grid.jpg" } }
            } });
            using var f = new Fixture(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                if (path.Contains("cube/render", StringComparison.Ordinal)) return CubePage(recommendation, grid);
                Equal("/eapi/tag/tab/playlists", path);
                Equal("500,501", S(Eapi(req).Body["ids"]));
                return Json(J(new { code = 200, data = new[] {
                    new { id = 501, name = "补齐的歌单名称", cover = "https://images.invalid/replacement.jpg", songCount = 44, userName = "合成创建者" },
                    new { id = 500, name = "服务端歌单名称", cover, songCount = 100, userName = "另一创建者" }
                } }));
            }, Session("wy"));
            var reply = await f.Catalog.ChannelAsync(new("wy", "rock"));
            var sections = reply.GetProperty("sections");
            Equal(2, sections.GetArrayLength());
            Equal("entries", sections[0].GetProperty("type").GetString());
            var items = sections[0].GetProperty("items").EnumerateArray().ToArray();
            Equal("animated,preview,preview,preview,static,overlay,static",
                string.Join(",", items.Select(item => item.GetProperty("presentation").GetString())));
            Equal("fm,playlist,playlist,album,song,similar,unsupported",
                string.Join(",", items.Select(item => item.GetProperty("action").GetProperty("type").GetString())));
            Equal("SCENE_RCMD", items[0].GetProperty("action").GetProperty("mode").GetString());
            Equal("ELECTRONIC", items[0].GetProperty("action").GetProperty("submode").GetString());
            Equal("电音漫游", items[0].GetProperty("coverLabel").GetString());
            Equal("栏目自己的标题", items[1].GetProperty("name").GetString());
            Equal(cover.Replace("http://", "https://", StringComparison.Ordinal), items[1].GetProperty("cover").GetString());
            Equal(7, items[1].GetProperty("count").GetInt32());
            Equal("原创建者", items[1].GetProperty("creator").GetString());
            Equal("已供预览", items[1].GetProperty("preview")[0].GetProperty("title").GetString());
            Equal("补齐的歌单名称", items[2].GetProperty("name").GetString());
            Equal("https://p2.music.126.net/synthetic/inline.jpg", items[2].GetProperty("cover").GetString());
            Equal(44, items[2].GetProperty("count").GetInt32());
            Equal("合成创建者", items[2].GetProperty("creator").GetString());
            Equal(0, items[2].GetProperty("preview").GetArrayLength());
            Song(items[4].GetProperty("song"), "wy", "1");
            True(!items[6].GetProperty("available").GetBoolean());
            var tabs = sections[0].GetProperty("tabs");
            Equal("recommend:group:0", sections[0].GetProperty("selectedTab").GetString());
            Equal(items[1].GetProperty("cover").GetString(), tabs[0].GetProperty("items")[1].GetProperty("cover").GetString());
            Equal(items[1].GetProperty("key").GetString(), tabs[0].GetProperty("items")[1].GetProperty("key").GetString());
            Equal(items[1].GetProperty("cover").GetString(), tabs[1].GetProperty("items")[0].GetProperty("cover").GetString());
            True(items[1].GetProperty("key").GetString() != tabs[1].GetProperty("items")[0].GetProperty("key").GetString());
            Equal("同一歌单的另一入口", tabs[1].GetProperty("items")[0].GetProperty("name").GetString());
            Equal("", tabs[1].GetProperty("items")[1].GetProperty("cover").GetString());
            True(tabs.EnumerateArray().SelectMany(tab => tab.GetProperty("items").EnumerateArray())
                .All(item => item.TryGetProperty("presentation", out _)));
            Equal("playlists", sections[1].GetProperty("type").GetString());
            True(!sections[1].GetProperty("items")[0].TryGetProperty("presentation", out _));
            Equal(3, f.Calls);
        });
        yield return Case("cube supplied entry artwork and preview need no playlist metadata or members", async () =>
        {
            const string cover = "https://p1.music.126.net/synthetic/cube.jpg?imageView=1&watermark=&type=1&image=fixture";
            using var f = new Fixture(req =>
            {
                if (req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal)) return Directory();
                Weapi(req, "/weapi/cube/render/page/protocol");
                return CubePage(J(new { id = "recommend", componentName = "RecommendHorizontal", props = new {
                    title = "官方栏目", recommendModule = new[] { new { modules = new[] {
                        new { type = "song_list", resource = Huge, title = "现有歌单", cover,
                            rankItems = new[] { new { trackId = "1", songName = "现有歌曲", artistName = "现有艺人" } } }
                    } } }
                } }));
            });
            var reply = await f.Catalog.ChannelAsync(new("wy", "rock"));
            var card = MusicCatalogDto.Read(reply, "wy", "channel", "").Single().Items.Single();
            Equal(MusicCatalogEntryPresentation.Preview, card.Presentation);
            Equal(Huge, card.ActionId); Equal(cover, card.Cover);
            True(card.HasPreview && card.CanRequestPreview);
            Equal("现有歌曲", card.PreviewSongs.Single().Title);
            Equal(2, f.Calls);
        });
        yield return Case("cube playlist summary batches do not truncate the visible collection", async () =>
        {
            var modules = Enumerable.Range(500, 101).Select(id => new { type = "song_list", resource = id, title = "入口 " + id }).ToArray();
            var batches = new List<int>();
            using var f = new Fixture(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                if (path.Contains("cube/render", StringComparison.Ordinal))
                    return CubePage(J(new { id = "recommend", componentName = "RecommendHorizontal",
                        props = new { title = "栏目", recommendModule = new[] { new { modules } } } }));
                Equal("/eapi/tag/tab/playlists", path);
                var ids = S(Eapi(req).Body["ids"]).Split(',');
                batches.Add(ids.Length);
                return Json(J(new { code = 200, data = ids.Select(id => new {
                    id, name = "服务端 " + id, cover = "https://images.invalid/" + id + ".jpg", songCount = 1, userName = ""
                }).ToArray() }));
            });
            var items = (await f.Catalog.ChannelAsync(new("wy", "rock"))).GetProperty("sections")[0].GetProperty("items");
            Equal(101, items.GetArrayLength());
            Equal("100,1", string.Join(",", batches));
            Equal("600", items[100].GetProperty("action").GetProperty("id").GetString());
            True(items.EnumerateArray().All(item => item.GetProperty("cover").GetString()!.Length > 0));
            Equal(4, f.Calls);
        });
        foreach (var failure in new[] { "missing", "foreign", "duplicate", "upstream" })
            yield return Case("cube playlist summary rejects " + failure + " results", async () =>
            {
                using var f = new Fixture(req =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    if (path.Contains("position/show", StringComparison.Ordinal)) return Directory();
                    if (path.Contains("cube/render", StringComparison.Ordinal))
                        return CubePage(J(new { id = "recommend", componentName = "RecommendHorizontal", props = new {
                            title = "栏目", recommendModule = new[] { new { modules = new[] {
                                new { type = "song_list", resource = 500, title = "入口一" },
                                new { type = "song_list", resource = 501, title = "入口二" }
                            } } }
                        } }));
                    Equal("/eapi/tag/tab/playlists", path);
                    var ids = failure switch { "missing" => Array.Empty<int>(), "foreign" => new[] { 500, 502 }, _ => new[] { 500, 500 } };
                    return Json(J(new { code = failure == "upstream" ? 500 : 200, data = ids.Select(id => new {
                        id, name = "歌单", cover = "https://images.invalid/list.jpg", songCount = 1, userName = ""
                    }).ToArray() }));
                });
                await Error(failure switch {
                    "missing" => "NOT_FOUND", "upstream" => "UPSTREAM_ERROR", _ => "IDENTITY_MISMATCH"
                }, () => f.Catalog.ChannelAsync(new("wy", "rock")));
                Equal(3, f.Calls);
            });
        yield return Case("personal wy uid from native owner with paginated identity", async () =>
        {
            var page = 0;
            using var f = new Fixture(req =>
            {
                Weapi(req, "/weapi/user/playlist");
                page++;
                return Json(J(new { code = 200, more = page == 1, playlist = new[] {
                    new { id = page == 1 ? Huge : "502", name = "个人列表", creator = new { userId = Huge, nickname = "合成用户" }, specialType = 5, trackCount = 0 } } }));
            }, Session("wy"));
            var first = await f.Catalog.PersonalPlaylistsAsync(new("wy", Limit: 1));
            Equal(Huge, first.GetProperty("accountId").GetString());
            Equal("favorites", first.GetProperty("items")[0].GetProperty("kind").GetString());
            var next = await f.Catalog.PersonalPlaylistsAsync(new("wy", Page: 2, Limit: 1, Continuation: first.GetProperty("continuation")));
            True(!next.GetProperty("more").GetBoolean()); Equal(JsonValueKind.Null, next.GetProperty("total").ValueKind);
        });
        yield return Case("personal repeated identity not silently deduplicated", async () =>
        {
            using var f = new Fixture(_ => Json(J(new { code = 200, more = true,
                playlist = new[] { new { id = "1", name = "列表", creator = new { userId = Huge } } } })), Session("wy"));
            var first = await f.Catalog.PersonalPlaylistsAsync(new("wy", Limit: 1));
            await Error("PAGINATION_STALLED", () => f.Catalog.PersonalPlaylistsAsync(new("wy", Page: 2, Limit: 1, Continuation: first.GetProperty("continuation"))));
        });
        yield return Case("personal directory retains a large identity cursor across later pages", async () =>
        {
            var calls = 0;
            using var f = new Fixture(_ => Json(J(new { code = 200, more = true,
                playlist = new[] { new { id = (++calls + 40000).ToString(), name = "列表", creator = new { userId = Huge } } } })), Session("wy"));
            var first = await f.Catalog.PersonalPlaylistsAsync(new("wy", Limit: 1));
            var cursor = JsonNode.Parse(first.GetProperty("continuation").GetRawText())!.AsObject();
            cursor["page"] = 30001;
            cursor["seen"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 30000).Select(i => i.ToString("D20")).ToArray());
            var next = await f.Catalog.PersonalPlaylistsAsync(new("wy", Page: 30001, Limit: 1,
                Continuation: JsonSerializer.SerializeToElement(cursor)));
            True(Encoding.UTF8.GetByteCount(next.GetRawText()) > 512 * 1024);
            Equal(30001, next.GetProperty("continuation").GetProperty("seen").GetArrayLength());
            var tail = await f.Catalog.PersonalPlaylistsAsync(new("wy", Page: 30002, Limit: 1,
                Continuation: next.GetProperty("continuation")));
            Equal(30002, tail.GetProperty("continuation").GetProperty("seen").GetArrayLength());
            Equal(3, calls);
        });
        yield return Case("catalog returns a complete large normalized page without requiring cache admission", async () =>
        {
            var songs = Enumerable.Range(1, 280).Select(i =>
            {
                var song = Wy(i.ToString()); song["name"] = new string('x', 4096); return song;
            }).ToArray();
            using var f = new Fixture(_ => Json(J(new { code = 200, album = new { id = "1", name = "专辑", size = songs.Length }, songs })));
            var first = await f.Catalog.AlbumAsync(new("wy", "1"));
            True(Encoding.UTF8.GetByteCount(first.GetRawText()) > 2 * 1024 * 1024);
            Equal(songs.Length, first.GetProperty("items").GetArrayLength());
            var second = await f.Catalog.AlbumAsync(new("wy", "1"));
            Equal(songs.Length, second.GetProperty("items").GetArrayLength());
            Equal(2, f.Calls);
        });
        yield return Case("personal continuation revoked before sending on account change", async () =>
        {
            using var f = new Fixture(_ => Json(J(new { code = 200, more = true,
                playlist = new[] { new { id = "1", name = "列表", creator = new { userId = Huge } } } })), Session("wy"));
            var first = await f.Catalog.PersonalPlaylistsAsync(new("wy", Limit: 1));
            f.Sessions.Current = Session("wy", 2);
            await Error("STALE_SESSION", () => f.Catalog.PersonalPlaylistsAsync(new("wy", Page: 2, Limit: 1, Continuation: first.GetProperty("continuation"))));
            Equal(1, f.Calls);
        });
        yield return Case("qq personal native created and subscribed methods and ownership", async () =>
        {
            using var f = new Fixture(req =>
            {
                var call = RequestJson(req)["req_0"]!; var method = S(call["method"]);
                if (method == "GetPlaylistByUin")
                {
                    Equal(Huge, S(call["param"]!["uin"]));
                    return Tx(J(new { total = 1, bFinish = true, v_delTid = Array.Empty<object>(), v_playlist = new[] { QqList("100", Huge, true) } }));
                }
                Equal("CgiGetPlaylistFavInfo", method); Equal("SYNTHETIC_EUIN", S(call["param"]!["uin"]));
                return Tx(J(new { total = 1, hasmore = 0, hide = false, v_failTids = Array.Empty<object>(), v_list = new[] { QqList("200", "99", false) } }));
            }, Session("tx"));
            var result = await f.Catalog.PersonalPlaylistsAsync(new("tx"));
            Equal(3, result.GetProperty("items").GetArrayLength()); Equal(3L, result.GetProperty("total").GetInt64());
            Equal("owned", result.GetProperty("items")[0].GetProperty("relationship").GetString());
            Equal("subscribed", result.GetProperty("items")[2].GetProperty("relationship").GetString());
        });
        yield return Case("qq hidden subscriptions and incomplete created directory are errors", async () =>
        {
            using var hidden = new Fixture(_ => Tx(J(new { hide = true, total = 0, hasmore = 0, v_list = Array.Empty<object>(), v_failTids = Array.Empty<object>() })), Session("tx"));
            await Error("FORBIDDEN", () => hidden.Catalog.PersonalPlaylistsAsync(new("tx", MusicPersonalListKind.Subscribed)));
            using var partial = new Fixture(_ => Tx(J(new { bFinish = false, total = 0, v_playlist = Array.Empty<object>() })), Session("tx"));
            await Error("INCOMPLETE_PAGE", () => partial.Catalog.PersonalPlaylistsAsync(new("tx", MusicPersonalListKind.Created)));
        });
        yield return Case("kg personal body signature, listId distinct from global and no guessed favorites", async () =>
        {
            using var f = new Fixture(req =>
            {
                VerifyKg(req); Equal("/v7/get_all_list", req.RequestUri!.AbsolutePath);
                Equal("cloudlist.service.kugou.com", req.Headers.GetValues("x-router").Single());
                var body = RequestJson(req); Equal(Huge, S(body["userid"])); Equal("979", S(body["total_ver"]));
                return Kg(J(new { count = 1, total_ver = "9", info = new[] { new { listid = Huge, type = 0, source = 1,
                    list_create_userid = Huge, count = 0, name = "我喜欢", is_def = 0 } } }));
            }, Session("kg"));
            var r = await f.Catalog.PersonalPlaylistsAsync(new("kg"));
            var item = r.GetProperty("items")[0]; Equal("0:" + Huge, item.GetProperty("id").GetString());
            Equal("playlist", item.GetProperty("kind").GetString()); True(item.GetProperty("available").GetBoolean());
            Equal(JsonValueKind.Null, item.GetProperty("locator").GetProperty("globalCollectionId").ValueKind);
        });
        yield return Case("fm authenticates native, fixed modes, no caching or write", async () =>
        {
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req); Equal("/api/v1/radio/get", path);
                Equal("FAMILIAR", S(body["mode"])); Equal("3", S(body["limit"]));
                Equal("SYNTHETIC_ONLY", S(body["header"]!["MUSIC_U"]));
                return Json(J(new { code = 200, data = new[] { Wy("1"), Wy(Huge) } }));
            }, Session("wy"));
            var first = await f.Catalog.FmAsync(new(Mode: MusicFmMode.Familiar));
            var second = await f.Catalog.FmAsync(new(Mode: MusicFmMode.Familiar));
            Equal(2, f.Calls); Equal(2, first.GetProperty("items").GetArrayLength()); True(!second.GetProperty("empty").GetBoolean());
            True(!second.TryGetProperty("more", out _));
        });
        yield return Case("fm empty result differs from malformed and business error", async () =>
        {
            using var empty = new Fixture(_ => Json(J(new { code = 200, data = Array.Empty<object>() })), Session("wy"));
            True((await empty.Catalog.FmAsync(new())).GetProperty("empty").GetBoolean());
            using var missing = new Fixture(_ => Json(J(new { code = 200, data = new { } })), Session("wy"));
            await Error("INVALID_RESPONSE", () => missing.Catalog.FmAsync(new()));
            using var bad = new Fixture(_ => Json(J(new { code = 500, data = Array.Empty<object>() })), Session("wy"));
            await Error("UPSTREAM_ERROR", () => bad.Catalog.FmAsync(new()));
        });
        yield return Case("fm preview and rule variants target distinct verified recipes", async () =>
        {
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req);
                if (path == "/api/tag/tab/fm/cover") { Equal("rap", S(body["tabTagId"])); return Json(J(new { code = 200, data = new { cover = "https://images.invalid/a.jpg" } })); }
                Equal("/api/v1/radio/get", path); Equal("1", S(body["limit"]));
                return Json(J(new { code = 200, data = new[] { Wy("1") } }));
            }, Session("wy"));
            Equal("fm-preview", (await f.Catalog.FmAsync(new(Preview: true, TabTagId: "rap"))).GetProperty("kind").GetString());
            Equal(1, (await f.Catalog.FmAsync(new(Preview: true))).GetProperty("count").GetInt32());
            await Error("INVALID_REQUEST", () => f.Catalog.FmAsync(new(Mode: MusicFmMode.Scene, Submode: "invented")));
            Equal(2, f.Calls);
        });
        yield return Case("similar is distinct WEAPI algorithm with precise seed DTO", async () =>
        {
            using var f = new Fixture(req => { Weapi(req, "/weapi/v1/radio/get"); return Json(J(new { code = 200, data = new[] { Wy("1") } })); }, Session("wy"));
            var result = await f.Catalog.SimilarAsync(new("wy", [Huge]));
            Equal(Huge, result.GetProperty("context").GetProperty("sourceIds")[0].GetString());
            Equal("similar", result.GetProperty("kind").GetString());
            await Error("INVALID_REQUEST", () => f.Catalog.SimilarAsync(new("wy", ["song:canonical"])));
        });
        yield return Case("daily uses local client without device cookie and still requires real account", async () =>
        {
            using var wire = new XeapiFixture();
            string device = "";
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/bsr/sk/get")
                {
                    var key = wire.PublicKey(request, expectedDevice: device);
                    key.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_DAILY_KEY; Domain=.music.163.com");
                    return key;
                }
                True(request.RequestUri.AbsolutePath is "/xeapi/v3/discovery/recommend/songs" or "/xeapi/homepage/category/daily/song/list");
                _ = wire.Open(request, expectedDevice: device);
                True(request.Headers.GetValues("Cookie").Single().Contains("NMTID=SYNTHETIC_DAILY_KEY", StringComparison.Ordinal));
                var response = XeapiFixture.Response(J(new { code = 200, data = new { dailySongs = new[] { Wy("1") } } }));
                response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_DAILY_BATCH; Domain=.music.163.com");
                return response;
            }, Session("wy"));
            var before = await f.Clients.Owner.AcquireWyAsync();
            device = before.DeviceId;
            Equal(1, (await f.Catalog.DailyAsync(new())).GetProperty("items").GetArrayLength());
            var current = await f.Clients.Owner.AcquireWyAsync();
            True(current.Generation == before.Generation + 2 && current.Nmtid == "SYNTHETIC_DAILY_BATCH");
            Equal(1, (await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style))).GetProperty("items").GetArrayLength());
            current = await f.Clients.Owner.AcquireWyAsync();
            True(current.Generation == before.Generation + 4 && current.Nmtid == "SYNTHETIC_DAILY_BATCH");
            f.Sessions.Current = null;
            await Error("AUTH_REQUIRED", () => f.Catalog.DailyAsync(new()));
            Equal(4, f.Calls);
        });
        yield return Case("heartbeat keeps play-all versus play-one and no fabricated metadata", async () =>
        {
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req); Equal("/api/playmode/intelligence/list", path);
                Equal("fromPlayAll", S(body["type"])); Equal(Huge, S(body["playlistId"]));
                return Json(J(new { code = 200, data = new[] { new { songInfo = Wy("1"), alg = "fixture" } } }));
            }, Session("wy"));
            Equal("heartbeat", (await f.Catalog.HeartbeatAsync(new("wy", Huge, "1", "1", true))).GetProperty("kind").GetString());
        });
        yield return Case("queue recommendations numeric array lossless and current must be in queue", async () =>
        {
            using var f = new Fixture(req =>
            {
                var (path, body) = Eapi(req); Equal("/api/homepage/more/rcmd/song", path);
                Equal("[" + Huge + "]", S(body["songIds"])); Equal(Huge, S(body["currentPlaySongId"]));
                return Json(J(new { code = 200, data = new { songList = new[] { Wy("1") } } }));
            }, Session("wy"));
            Equal(1, (await f.Catalog.QueueRecommendationsAsync(new("wy", [Huge], Huge))).GetProperty("count").GetInt32());
            await Error("INVALID_REQUEST", () => f.Catalog.QueueRecommendationsAsync(new("wy", ["1"], "2")));
        });
        yield return Case("album verified endpoint and exact identity", async () =>
        {
            using var f = new Fixture(req => { Weapi(req, "/weapi/v1/album/30"); return Json(J(new { code = 200, album = new { id = "30", name = "专辑", size = 1 }, songs = new[] { Wy("1") } })); });
            var r = await f.Catalog.AlbumAsync(new("wy", "30")); Equal("NeteaseAlbum", r.GetProperty("engine").GetString());
            Song(r.GetProperty("items")[0], "wy", "1");
        });
        yield return Case("artist detail uses one verified WEAPI request and normalizes hot songs", async () =>
        {
            using var f = new Fixture(req =>
            {
                Weapi(req, "/weapi/v1/artist/30");
                return Json(J(new
                {
                    code = 200,
                    artist = new { id = "30", name = "测试歌手", picUrl = "https://images.invalid/artist.jpg" },
                    hotSongs = new[] { Wy("1"), Wy("2") }
                }));
            });
            var r = await f.Catalog.ArtistAsync(new("wy", "30"));
            Equal("wy", r.GetProperty("platform").GetString()); Equal("30", r.GetProperty("id").GetString());
            Equal("测试歌手", r.GetProperty("name").GetString());
            Equal("https://images.invalid/artist.jpg", r.GetProperty("cover").GetString());
            Equal("NeteaseArtist", r.GetProperty("engine").GetString());
            Equal(2, r.GetProperty("items").GetArrayLength()); Equal(2, r.GetProperty("total").GetInt32());
            Song(r.GetProperty("items")[0], "wy", "1"); Song(r.GetProperty("items")[1], "wy", "2");
            Equal(1, f.Calls);
        });
        yield return Case("artist detail rejects invalid input mismatched identity and malformed metadata", async () =>
        {
            using var unused = new Fixture(_ => throw new InvalidOperationException("unexpected request"));
            await Error("UNSUPPORTED", () => unused.Catalog.ArtistAsync(new("tx", "30")));
            await Error("INVALID_REQUEST", () => unused.Catalog.ArtistAsync(new("wy", "x")));
            Equal(0, unused.Calls);

            using var mismatch = new Fixture(_ => Json(J(new
            {
                code = 200,
                artist = new { id = "31", name = "错误歌手", picUrl = "https://images.invalid/artist.jpg" },
                hotSongs = Array.Empty<object>()
            })));
            await Error("IDENTITY_MISMATCH", () => mismatch.Catalog.ArtistAsync(new("wy", "30")));

            using var malformed = new Fixture(_ => Json(J(new
            {
                code = 200,
                artist = new { id = "30", name = "测试歌手", picUrl = "not-an-image" },
                hotSongs = Array.Empty<object>()
            })));
            await Error("INVALID_RESPONSE", () => malformed.Catalog.ArtistAsync(new("wy", "30")));

            using var missingSongs = new Fixture(_ => Json(J(new
            {
                code = 200,
                artist = new { id = "30", name = "测试歌手", picUrl = "https://images.invalid/artist.jpg" }
            })));
            await Error("INVALID_RESPONSE", () => missingSongs.Catalog.ArtistAsync(new("wy", "30")));

            using var duplicateSongs = new Fixture(_ => Json(J(new
            {
                code = 200,
                artist = new { id = "30", name = "测试歌手", picUrl = "https://images.invalid/artist.jpg" },
                hotSongs = new[] { Wy("1"), Wy("1") }
            })));
            await Error("IDENTITY_MISMATCH", () => duplicateSongs.Catalog.ArtistAsync(new("wy", "30")));
        });
        foreach (var (status, code) in new[] { (301, "REDIRECT_DENIED"), (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"), (429, "RATE_LIMITED"), (500, "UPSTREAM_ERROR") })
            yield return Case("http classification " + status, async () =>
            {
                using var f = new Fixture(_ => new((HttpStatusCode)status) { Content = new StringContent("private response must never escape") });
                await Error(code, () => f.Catalog.SearchAsync(new("wy", "x")));
                Equal(1, f.Calls);
            });
        foreach (var (platform, code, expected) in new[] { ("wy", 301, "AUTH_REQUIRED"), ("wy", 403, "FORBIDDEN"), ("wy", 460, "VERIFICATION_REQUIRED"),
            ("wy", 509, "RATE_LIMITED"), ("tx", 1000, "AUTH_REQUIRED"), ("tx", 2000, "SIGNATURE_REQUIRED"), ("tx", 2001, "RATE_LIMITED"),
            ("kg", 20018, "AUTH_REQUIRED"), ("kg", 20028, "VERIFICATION_REQUIRED") })
            yield return Case(platform + " business code " + code, async () =>
            {
                using var f = new Fixture(_ => Json(platform == "wy" ? J(new { code })
                    : platform == "tx" ? J(new { code = 0, req_0 = new { code, data = new { } } }) : J(new { status = 0, error_code = code })));
                await Error(expected, () => f.Catalog.SearchAsync(new(platform, "x")));
                Equal(1, f.Calls);
            });
        foreach (var uri in new[] { "http://music.163.com/api/x", "https://music.163.com.evil.invalid/x", "https://u.y.qq.com/x",
            "https://user:secret@music.163.com/x", "https://music.163.com:444/x", "https://127.0.0.1/x", "https://music.163.com/x#fragment" })
            yield return Case("transport rejects origin " + Array.IndexOf(new[] { "http://music.163.com/api/x", "https://music.163.com.evil.invalid/x", "https://u.y.qq.com/x",
                "https://user:secret@music.163.com/x", "https://music.163.com:444/x", "https://127.0.0.1/x", "https://music.163.com/x#fragment" }, uri), async () =>
            {
                var sent = 0; using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { sent++; return Task.FromResult(Json(J(new { }))); }));
                await Error("ORIGIN_DENIED", () => http.SendAsync("wy", new(HttpMethod.Get, uri), default));
                Equal(0, sent);
            });
        yield return Case("shared native transport preserves split Set-Cookie without jar", async () =>
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) =>
            {
                True(!request.Headers.Contains("Cookie"));
                var result = Json(J(new { code = 200 }));
                result.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "MUSIC_U=SYNTHETIC_ONLY; Path=/", "__csrf=SYNTHETIC_CSRF; Path=/" });
                return Task.FromResult(result);
            }));
            var response = await http.SendAsync("wy", new(HttpMethod.Post, "https://music.163.com/api/x"), default);
            Equal(2, response.SetCookieHeaders.Count); Equal("{}", JsonSerializer.Serialize(response));
            _ = await http.SendAsync("wy", new(HttpMethod.Post, "https://music.163.com/api/x"), default);
        });
        yield return Case("stream budget enforced even without Content-Length", async () =>
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StreamContent(new System.IO.MemoryStream(Encoding.UTF8.GetBytes("{\"tooLong\":\"1234567890\"}"))) })));
            await Error("LIMIT", () => http.SendAsync("wy", new(HttpMethod.Get, "https://music.163.com/api/x"), default, maxResponseBytes: 8));
        });
        foreach (var (name, body, error) in new[] {
            ("final-business-status", "{\"code\":200,\"code\":500}", "UPSTREAM_ERROR"),
            ("missing-result", "{\"code\":200,\"result\":null}", "INVALID_RESPONSE"),
            ("invalid-json", "not JSON", "INVALID_RESPONSE") })
            yield return Case("raw response preserves final business or schema error " + name, async () =>
            {
                using var f = new Fixture(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
                await Error(error, () => f.Catalog.SearchAsync(new("wy", "x")));
            });
        yield return Case("no secret signed artwork enters durable song metadata", async () =>
        {
            var raw = Wy("1"); raw["al"]!["picUrl"] = "https://images.invalid/a?token=SYNTHETIC_ONLY";
            using var f = new Fixture(_ => WySearch([raw], 1));
            var r = (await f.Catalog.SearchAsync(new("wy", "x"))).GetProperty("items")[0];
            Equal("", r.GetProperty("cover_url").GetString()); True(!MusicSongFixture.Info(r).TryGetProperty("img", out _));
            True(!r.GetRawText().Contains("SYNTHETIC_ONLY", StringComparison.Ordinal));
        });
        yield return Case("wy public thumbnail artwork survives home normalization without storing queries", async () =>
        {
            const string original = "https://p1.music.126.net/synthetic-cover/artwork.jpg";
            const string thumbnail = original + "?imageView=1&type=webp&thumbnail=150y150";
            var nested = Wy("2"); nested["al"]!.AsObject().Remove("picUrl");
            using var f = new Fixture(_ => Home(false, "1",
                J(new { resourceType = "song", resourceId = "1", title = "测试歌曲", artistName = "测试歌手", coverUrl = thumbnail }),
                J(new { resourceType = "song", coverUrl = thumbnail.Replace("imageView=1", "imageView", StringComparison.Ordinal),
                    resourceExtInfo = new { songData = nested } })));
            var reply = await f.Catalog.HomeAsync(new("wy"));
            var cards = MeowFilm.Capabilities.MusicCatalogDto.Read(reply, "wy", "home", "").SelectMany(s => s.Items).ToArray();
            Equal(2, cards.Length);
            foreach (var card in cards)
            {
                Equal(original, card.Cover);
                Equal(original, MusicSongFixture.Info(card.Song!.Raw).GetProperty("img").GetString());
                True(!card.Song.Raw.GetRawText().Contains("imageView", StringComparison.Ordinal));
            }
            Equal("1", cards[0].ActionId); Equal("2", cards[1].ActionId);
        });
        yield return Case("wy album-logo entries preserve official decoration and upgrade trusted artwork to TLS", async () =>
        {
            const string decoration = "http://p1.music.126.net/synthetic-gradient/decoration.png";
            const string cover = "http://p2.music.163.net/synthetic-cover/actual.jpg";
            var action = "orpheus://playlist/500?coverUrl=" + Uri.EscapeDataString(cover);
            using var f = new Fixture(_ => Home(false, "1", J(new
            {
                resourceType = "playlist",
                resourceId = "500",
                title = "私人雷达",
                coverImg = decoration,
                coverType = "ALBUM_LOGO",
                action
            })));
            var reply = await f.Catalog.HomeAsync(new("wy"));
            var card = MusicCatalogDto.Read(reply, "wy", "home", "")
                .SelectMany(section => section.Items).Single();
            Equal("https://p2.music.163.net/synthetic-cover/actual.jpg", card.Cover);
            Equal("https://p1.music.126.net/synthetic-gradient/decoration.png", card.Decoration);
            Equal("500", card.ActionId);
            Equal(1, f.Calls);

            using var same = new Fixture(_ => Home(false, "1", J(new
            {
                resourceType = "playlist",
                resourceId = "500",
                title = "私人雷达",
                coverImg = decoration,
                coverType = "ALBUM_LOGO",
                action = "orpheus://playlist/500?coverUrl=" + Uri.EscapeDataString(decoration)
            })));
            var unchanged = MusicCatalogDto.Read(await same.Catalog.HomeAsync(new("wy")), "wy", "home", "")
                .SelectMany(section => section.Items).Single();
            Equal("https://p1.music.126.net/synthetic-gradient/decoration.png", unchanged.Cover);
            Equal("", unchanged.Decoration);
            Equal(1, same.Calls);

            using var nested = new Fixture(_ => Home(false, "1", J(new
            {
                resourceType = "playlist",
                resourceId = "500",
                title = "私人雷达",
                coverImg = decoration,
                coverType = "ALBUM_LOGO",
                action = new { type = "resource", @params = new { resourceType = "playlist", query = "500" } },
                playBtnData = new { detailUrl = action }
            })));
            var nestedCard = MusicCatalogDto.Read(await nested.Catalog.HomeAsync(new("wy")), "wy", "home", "")
                .SelectMany(section => section.Items).Single();
            Equal("https://p2.music.163.net/synthetic-cover/actual.jpg", nestedCard.Cover);
            Equal("https://p1.music.126.net/synthetic-gradient/decoration.png", nestedCard.Decoration);
            Equal(1, nested.Calls);

            using var invalid = new Fixture(_ => Home(false, "1", J(new
            {
                resourceType = "playlist",
                resourceId = "500",
                title = "私人雷达",
                coverImg = decoration,
                coverType = "ALBUM_LOGO",
                action = "orpheus://playlist:99/500?coverUrl=" + Uri.EscapeDataString(cover)
            })));
            var rejected = MusicCatalogDto.Read(await invalid.Catalog.HomeAsync(new("wy")), "wy", "home", "")
                .SelectMany(section => section.Items).Single();
            Equal("https://p1.music.126.net/synthetic-gradient/decoration.png", rejected.Cover);
            Equal("", rejected.Decoration);
            Equal(1, invalid.Calls);
        });
        yield return Case("wy discovery artwork removes only validated CDN rendering parameters", async () =>
        {
            foreach (var (input, expected) in new[]
            {
                ("http://p1.music.126.net/synthetic/cover.jpg?imageView&type=webp&thumbnail=300y300",
                    "https://p1.music.126.net/synthetic/cover.jpg"),
                ("http://p2.music.163.net/synthetic/cover.jpg?imageView=1&type=png",
                    "https://p2.music.163.net/synthetic/cover.jpg"),
                ("http://p3.music.126.net/synthetic/cover.png?param=200y200",
                    "https://p3.music.126.net/synthetic/cover.png")
            })
            {
                using var f = new Fixture(_ => Home(false, "1", J(new
                {
                    resourceType = "playlist", resourceId = "500", name = "歌单", coverImg = input
                })));
                var card = MusicCatalogDto.Read(await f.Catalog.HomeAsync(new("wy")), "wy", "home", "")
                    .SelectMany(section => section.Items).Single();
                Equal(expected, card.Cover);
                Equal(1, f.Calls);
            }

            using var unsafeQuery = new Fixture(_ => Home(false, "1", J(new
            {
                resourceType = "playlist", resourceId = "500", name = "歌单",
                coverImg = "https://p1.music.126.net/synthetic/cover.jpg?imageView&type=webp&token=SYNTHETIC_ONLY"
            })));
            var unchanged = MusicCatalogDto.Read(await unsafeQuery.Catalog.HomeAsync(new("wy")), "wy", "home", "")
                .SelectMany(section => section.Items).Single();
            Equal("https://p1.music.126.net/synthetic/cover.jpg?imageView&type=webp&token=SYNTHETIC_ONLY",
                unchanged.Cover);
            Equal(1, unsafeQuery.Calls);
        });
        yield return Case("wy thumbnail canonicalization rejects unknown signed and duplicate parameters", async () =>
        {
            foreach (var url in new[] {
                "https://p1.music.126.net/a.jpg?imageView&type=webp&token=SYNTHETIC_ONLY",
                "https://p1.music.126.net/a.jpg?imageView&type=webp&type=png",
                "https://p1.music.126.net/a.jpg?imageView&thumbnail=bad",
                "https://p1.music.126.net/a.jpg?imageView=2&type=webp",
                "https://p1.music.126.net/a.jpg?type=webp",
                "https://p1.music.126.net.evil.invalid/a.jpg?imageView&type=webp",
                "https://images.invalid/a.jpg?imageView&type=webp",
                "https://p1.music.126.net/a.jpg?imageView&type=webp#fragment" })
            {
                var raw = Wy("1"); raw["al"]!["picUrl"] = url;
                using var f = new Fixture(_ => WySearch([raw], 1));
                var song = (await f.Catalog.SearchAsync(new("wy", "x"))).GetProperty("items")[0];
                Equal("", song.GetProperty("cover_url").GetString());
                True(!MusicSongFixture.Info(song).TryGetProperty("img", out _));
                True(!song.GetRawText().Contains("SYNTHETIC_ONLY", StringComparison.Ordinal));
            }
        });
        yield return Case("catalog never caches search so a failure is never hidden by an earlier success", async () =>
        {
            var fail = false;
            using var f = new Fixture(_ => fail ? Json(J(new { code = 500 })) : WySearch([Wy("1")], 1));
            var first = await f.Catalog.SearchAsync(new("wy", "x")); fail = true;
            await Error("UPSTREAM_ERROR", () => f.Catalog.SearchAsync(new("wy", "x"))); Equal(2, f.Calls);
            await Error("UPSTREAM_ERROR", () => f.Catalog.SearchAsync(new("wy", "x", Refresh: true)));
            Equal("1", MusicSongFixture.NativeId(first.GetProperty("items")[0]));
        });
        yield return Case("catalog search always reaches the platform; the search owner caches once", async () =>
        {
            var id = "1";
            using var f = new Fixture(_ => WySearch([Wy(id)], 1));
            _ = await f.Catalog.SearchAsync(new("wy", "x")); id = "2";
            var again = await f.Catalog.SearchAsync(new("wy", "x"));
            var refreshed = await f.Catalog.SearchAsync(new("wy", "x", Refresh: true));
            Equal("2", MusicSongFixture.NativeId(again.GetProperty("items")[0]));
            Equal("2", MusicSongFixture.NativeId(refreshed.GetProperty("items")[0])); Equal(3, f.Calls);
        });
        yield return Case("logout during first request blocks second request and old result", async () =>
        {
            Fixture? f = null;
            using (f = new Fixture(_ => { f!.Sessions.Current = null; return WyList(["1"]); }, Session("wy")))
            {
                await Error("STALE_SESSION", () => f.Catalog.PlaylistAsync(new("wy", "500")));
                Equal(1, f.Calls);
            }
        });
        yield return Case("cancel old callback while physical handler ignores cancellation", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture(async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); return await delayed.Task;
            });
            using var cancel = new CancellationTokenSource();
            var task = f.Catalog.SearchAsync(new("wy", "x"), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
            try { await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); True(!task.IsCompleted); }
            finally { delayed.TrySetResult(WySearch([Wy("1")], 1)); }
            await Error("CANCELLED", () => task);
        });
        yield return Case("invalidation old callback and close are distinct typed results", async () =>
        {
            foreach (var close in new[] { false, true })
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var f = new Fixture(async (_, token) =>
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult(); return await delayed.Task;
                });
                var task = f.Catalog.SearchAsync(new("wy", "x"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (close) f.Catalog.Dispose(); else f.Catalog.Invalidate("wy");
                try { await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); True(!task.IsCompleted); }
                finally { delayed.TrySetResult(WySearch([Wy("1")], 1)); }
                await Error(close ? "CLOSED" : "STALE_SESSION", () => task);
            }
        });
        yield return Case("cancelled physical requests never block a new request", async () =>
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, _) =>
            {
                int call = Interlocked.Increment(ref calls);
                (call == 1 ? first : call == 2 ? second : third).TrySetResult();
                await release.Task; return Json(J(new { code = 200 }));
            }));
            using var cancelled = new CancellationTokenSource();
            var a = http.SendAsync("wy", new(HttpMethod.Get, "https://music.163.com/api/x"), cancelled.Token);
            var b = http.SendAsync("wy", new(HttpMethod.Get, "https://music.163.com/api/y"), cancelled.Token);
            await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(2)); cancelled.Cancel();
            try
            {
                True(!a.IsCompleted && !b.IsCompleted);
                var c = http.SendAsync("wy", new(HttpMethod.Get, "https://music.163.com/api/z"), default);
                await third.Task.WaitAsync(TimeSpan.FromSeconds(2)); Equal(3, calls);
                release.TrySetResult(); await c;
                try { await Task.WhenAll(a, b); throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { }
            }
            finally { release.TrySetResult(); }
        });
        yield return Case("timeout is bounded without network retries", async () =>
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var f = new Fixture(async (_, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                return await delayed.Task;
            }, timeout: TimeSpan.FromMilliseconds(30));
            var task = f.Catalog.SearchAsync(new("wy", "x"));
            try { await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); True(!task.IsCompleted); Equal(1, f.Calls); }
            finally { delayed.TrySetResult(WySearch([Wy("1")], 1)); }
            await Error("TIMEOUT", () => task);
        });
        yield return Case("credentials unavailable is sanitized and never downgraded to anonymous", async () =>
        {
            using var f = new Fixture(_ => throw new Exception("must not send"));
            f.Sessions.Throw = true;
            await Error("CREDENTIALS_UNAVAILABLE", () => f.Catalog.SearchAsync(new("wy", "x")));
            Equal(0, f.Calls);
        });
        yield return Case("KG account without legacy mid uses current local client without registration", async () =>
        {
            using var f = new Fixture(request =>
            {
                VerifyKg(request);
                True(Query(request.RequestUri!)["mid"].Length > 0);
                return Kg(J(new { count = 0, total_ver = 0, info = Array.Empty<object>() }));
            },
                new("kg", "fixture-scope", 1, Huge, "userid=" + Huge + "; token=SYNTHETIC_ONLY"));
            Equal(0, (await f.Catalog.PersonalPlaylistsAsync(new("kg"))).GetProperty("items").GetArrayLength());
            Equal(1, f.Calls);
        });
        yield return Case("invalid inputs pre-cancel and closed perform no request", async () =>
        {
            using var f = new Fixture(_ => throw new Exception("must not send"));
            await Error("INVALID_REQUEST", () => f.Catalog.SearchAsync(new("wy", "", Page: 0)));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Error("CANCELLED", () => f.Catalog.SearchAsync(new("wy", "x"), cancelled.Token));
            f.Catalog.Dispose();
            await Error("CLOSED", () => f.Catalog.SearchAsync(new("wy", "x")));
            Equal(0, f.Calls);
        });
        foreach (var test in ExtensionCases()) yield return test;
    }
    private static IEnumerable<TestCase> ExtensionCases()
    {
        yield return Case("extension RFC7748 CNG X25519 vector", () =>
        {
            using var alice = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.CreateFromFriendlyName("curve25519"),
                D = Clamp(Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")) });
            using var bob = Peer(Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"));
            Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a",
                Convert.ToHexStringLower(alice.ExportParameters(false).Q.X!));
            Equal("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742",
                Convert.ToHexStringLower(alice.DeriveRawSecretAgreement(bob.PublicKey)));
            return Task.CompletedTask;
        });
        yield return Case("extension daily full XEAPI authenticated envelope decrypted response cache expiry and style", async () =>
        {
            using var wire = new XeapiFixture();
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/bsr/sk/get") return wire.PublicKey(request);
                var plaintext = wire.Open(request);
                if (request.RequestUri.AbsolutePath == "/xeapi/v3/discovery/recommend/songs")
                    Equal("ispush=false", S(plaintext["content"]));
                else { Equal("/xeapi/homepage/category/daily/song/list", request.RequestUri.AbsolutePath); True(plaintext["content"] is null); }
                Equal("e_r=true", S(plaintext["queryString"]));
                return XeapiFixture.Response(J(new { code = 200, data = new { dailySongs = new[] { Wy(Huge) },
                    tags = new { categoryId = "3000", tagVOList = new[] { new { tagId = "11", tagName = "合成风格" } } } } }), gzip: true);
            }, DeviceSession());
            var daily = await f.Catalog.DailyAsync(new());
            Song(daily.GetProperty("items")[0], "wy", Huge); Equal("daily:default", daily.GetProperty("id").GetString());
            Equal(6, DateTimeOffset.FromUnixTimeMilliseconds(daily.GetProperty("expiresAt").GetInt64()).ToOffset(TimeSpan.FromHours(8)).Hour);
            True(!daily.GetRawText().Contains("SYNTHETIC", StringComparison.Ordinal));
            _ = await f.Catalog.DailyAsync(new()); Equal(2, f.Calls);
            _ = await f.Catalog.DailyAsync(new(Refresh: true)); Equal(4, f.Calls);
            var style = await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
            Equal("3000", style.GetProperty("category").GetString()); Equal("11", style.GetProperty("tags")[0].GetProperty("id").GetString());
            var remaining = style.GetProperty("expiresAt").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            True(remaining is > 45000 and <= 60000); Equal(6, f.Calls);
            f.Catalog.Invalidate("wy"); _ = await f.Catalog.DailyAsync(new()); Equal(8, f.Calls);
        });
        foreach (var failure in new[] { "signature", "sk", "key", "cipher", "shape", "identity", "business", "gzip-budget" })
            yield return Case("extension XEAPI " + failure + " is not empty success", async () =>
            {
                using var wire = new XeapiFixture();
                string device = "";
                using var f = new Fixture(request =>
                {
                    if (request.RequestUri!.AbsolutePath == "/api/bsr/sk/get")
                    {
                        var key = wire.PublicKey(request, failure, device);
                        if (failure is "signature" or "sk" or "key")
                            key.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_REJECTED; Domain=.music.163.com");
                        return key;
                    }
                    _ = wire.Open(request, device);
                    HttpResponseMessage response = failure switch {
                        "cipher" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
                        "shape" => XeapiFixture.Response(J(new { code = 200, data = new { } })),
                        "identity" => XeapiFixture.Response(J(new { code = 200, data = new { dailySongs = new[] { Wy("1"), Wy("1") } } })),
                        "business" => XeapiFixture.Response(J(new { code = 301, data = new { dailySongs = Array.Empty<object>() } })),
                        "gzip-budget" => XeapiFixture.Response(J(new { code = 200, padding = new string('x', 5 * 1024 * 1024) }), true),
                        _ => throw new InvalidOperationException("Must not send after invalid key")
                    };
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_REJECTED; Domain=.music.163.com");
                    return response;
                }, Session("wy"));
                var before = await f.Clients.Owner.AcquireWyAsync();
                device = before.DeviceId;
                await Error(failure == "business" ? "AUTH_REQUIRED" : failure == "gzip-budget" ? "LIMIT" :
                    failure == "identity" ? "IDENTITY_MISMATCH" : "INVALID_RESPONSE",
                    () => f.Catalog.DailyAsync(new()));
                Equal(failure is "signature" or "sk" or "key" ? 1 : 2, f.Calls);
                True(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()));
            });
        yield return Case("extension daily explicit encrypted empty is successful", async () =>
        {
            using var wire = new XeapiFixture();
            using var f = new Fixture(r => r.RequestUri!.Host == "interface.music.163.com" ? wire.PublicKey(r)
                : XeapiFixture.Response(J(new { code = 200, data = new { dailySongs = Array.Empty<object>() } })), DeviceSession());
            var r = await f.Catalog.DailyAsync(new()); True(r.GetProperty("empty").GetBoolean());
            Equal(0, r.GetProperty("count").GetInt32()); Equal(2, f.Calls);
        });
        yield return Case("extension logout after public key prevents daily HTTP and stale cache", async () =>
        {
            using var wire = new XeapiFixture(); Fixture? f = null;
            using (f = new Fixture(r => { f!.Sessions.Current = null; return wire.PublicKey(r); }, DeviceSession()))
            {
                await Error("STALE_SESSION", () => f.Catalog.DailyAsync(new())); Equal(1, f.Calls);
                await Error("AUTH_REQUIRED", () => f.Catalog.DailyAsync(new())); Equal(1, f.Calls);
            }
        });
        yield return Case("extension XEAPI origins only allow audited paths", () =>
        {
            True(MusicPlatformHttp.IsAllowedOrigin("wy", new("https://interface.music.163.com/api/bsr/sk/get")));
            True(MusicPlatformHttp.IsAllowedOrigin("wy", new("https://interface3.music.163.com/xeapi/v3/discovery/recommend/songs")));
            True(!MusicPlatformHttp.IsAllowedOrigin("wy", new("https://interface.music.163.com/api/arbitrary")));
            True(!MusicPlatformHttp.IsAllowedOrigin("wy", new("https://interface3.music.163.com/xeapi/arbitrary")));
            True(!MusicPlatformHttp.IsAllowedOrigin("tx", new("https://interface3.music.163.com/xeapi/v3/discovery/recommend/songs")));
            return Task.CompletedTask;
        });
        foreach (string branch in new[] { "sign-card", "banner", "check-in" })
        foreach (bool valid in new[] { true, false })
        {
            var b = branch; var v = valid;
            yield return Case("VIP " + b + (v ? " validates before client update" : " rejects invalid result without client update"), async () =>
            {
                using var transport = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) =>
                {
                    if (b != "check-in")
                    {
                        var wire = VipEapi(request);
                        Equal("", wire.Body["header"]!["__csrf"]!.GetValue<string>());
                    }
                    var body = b switch
                    {
                        "sign-card" => J(new { code = 200, bgTexture = "https://img.invalid/sign.jpg",
                            signInfoList = v ? (object)Array.Empty<object>() : false }),
                        "banner" => J(new { code = 200, data = new { contentList = v ? (object)Array.Empty<string>() : false } }),
                        _ => J(new { code = 200, data = v })
                    };
                    var response = b == "check-in" ? Json(body) : XeapiFixture.Response(body);
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_VIP_CLIENT; Domain=.music.163.com");
                    return Task.FromResult(response);
                }));
                await using var clients = new MusicClientContextFixture(transport);
                var before = await clients.Owner.AcquireWyAsync();
                var protocol = new MusicVipPageProtocol(transport, clients.Owner);
                var session = new MusicPlatformSession("wy", "synthetic-vip-client", 1, "42", "MUSIC_U=SYNTHETIC_VIP_ACCOUNT");
                Task Read() => b switch
                {
                    "sign-card" => protocol.ReadSignAsync(session, default, () => { }),
                    "banner" => protocol.ReadBannerAsync(session, default, () => { }),
                    _ => protocol.SignAsync(session, default, () => { })
                };
                if (v)
                {
                    await Read();
                    var current = await clients.Owner.AcquireWyAsync();
                    True(current.Generation == before.Generation + 1 && current.Nmtid == "SYNTHETIC_VIP_CLIENT");
                }
                else
                {
                    await Error(b == "check-in" ? "UPSTREAM_ERROR" : "INVALID_RESPONSE", Read);
                    True(ReferenceEquals(before, await clients.Owner.AcquireWyAsync()));
                }
            });
        }
        yield return Case("VIP page uses fixed EAPI and WEAPI recipes with strict native data", async () =>
        {
            var calls = 0;
            using var transport = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) =>
            {
                calls++;
                if (request.RequestUri!.AbsolutePath == "/eapi/vipnewcenter/app/minidesk/music/sign/pc")
                {
                    var (path, body) = VipEapi(request);
                    Equal("/api/vipnewcenter/app/minidesk/music/sign/pc", path);
                    Equal("1", S(body["type"])); Equal("true", S(body["e_r"]));
                    Equal("SYNTHETIC_ONLY", S(body["header"]!["MUSIC_U"]));
                    return Task.FromResult(XeapiFixture.Response(J(new
                    {
                        code = 200,
                        text = "10月黑胶乐签",
                        subText = "第2天，得3成长值",
                        redPrize = false,
                        redDay = true,
                        btnText = "打卡",
                        bgTexture = "https://img.invalid/sign.jpg",
                        signInfoList = new[]
                        {
                            new { dayText = "25日", sign = true, songCoverUrl = "", signTime = 1, today = false },
                            new { dayText = "今天", sign = false, songCoverUrl = "https://img.invalid/song.jpg", signTime = 0, today = true }
                        }
                    })));
                }
                if (request.RequestUri.AbsolutePath == "/eapi/vipnewcenter/app/resource/common/list")
                {
                    var (path, body) = VipEapi(request);
                    Equal("/api/vipnewcenter/app/resource/common/list", path);
                    Equal("16", S(body["positionType"])); Equal("true", S(body["e_r"]));
                    return Task.FromResult(XeapiFixture.Response(J(new
                    {
                        code = 200,
                        data = new
                        {
                            contentList = new[]
                            {
                                J(new { algFlag = 1, imgUrl = "https://img.invalid/hidden.jpg", resourceType = "SONG", resourceId = "1" }).ToJsonString(),
                                J(new { algFlag = 0, imgUrl = "https://img.invalid/banner.jpg", resourceType = "PLAYLIST", resourceId = Huge }).ToJsonString()
                            }
                        }
                    })));
                }
                Weapi(request, "/weapi/vip-center-bff/task/sign");
                return Task.FromResult(Json(J(new { code = 200, data = true })));
            }));
            await using var clients = new MusicClientContextFixture(transport);
            var protocol = new MusicVipPageProtocol(transport, clients.Owner);
            var session = Session("wy");
            var sign = await protocol.ReadSignAsync(session, default, () => { });
            Equal("10月黑胶乐签", sign?.Text);
            Equal(2, sign!.Days.Count);
            True(sign is { SignedToday: false, RedDay: true });
            var banner = await protocol.ReadBannerAsync(session, default, () => { });
            Equal("PLAYLIST", banner?.ResourceType); Equal(Huge, banner?.ResourceId);
            await protocol.SignAsync(session, default, () => { });
            Equal(3, calls);
        });
        yield return Case("VIP owner keeps an accepted sign receipt across a stale unsigned reread", async () =>
        {
            await using var fixture = await VipApplicationFixture.CreateAsync();
            var snapshot = await fixture.Application.ReadVipPageAsync();
            True(snapshot is { Generation: > 0, SignedToday: false });

            var receipt = await fixture.Application.SignVipPageAsync(snapshot.Generation);
            Equal(snapshot.Generation, receipt.Generation);
            True(receipt.RefreshedSign is { SignedToday: false });
            Equal(1, fixture.Transport.SignWrites);
            Equal(2, fixture.Transport.SignReads);

            await OperationError("ALREADY_SIGNED",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            var reread = await fixture.Application.ReadVipPageAsync();
            True(reread.Sign is { SignedToday: false } && reread.SignConfirmed &&
                reread.SignedToday);
            await OperationError("ALREADY_SIGNED",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            Equal(1, fixture.Transport.SignWrites);
            Equal(3, fixture.Transport.SignReads);
        });
        yield return Case("VIP admitted sign outlives caller cancellation and is never replayed", async () =>
        {
            await using var fixture = await VipApplicationFixture.CreateAsync();
            var snapshot = await fixture.Application.ReadVipPageAsync();
            var hold = fixture.Transport.HoldNextSignWrite();
            using var cancellation = new CancellationTokenSource();
            var signing = fixture.Application.SignVipPageAsync(snapshot.Generation, cancellation.Token);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();
            True(!hold.Cancelled.Task.IsCompleted);
            hold.Release.TrySetResult();
            var receipt = await signing;
            Equal(snapshot.Generation, receipt.Generation);
            await OperationError("ALREADY_SIGNED",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            Equal(1, fixture.Transport.SignWrites);
        });
        yield return Case("VIP unknown sign receipt remains blocked across reread without faking success", async () =>
        {
            await using var fixture = await VipApplicationFixture.CreateAsync();
            var snapshot = await fixture.Application.ReadVipPageAsync();
            fixture.Transport.FailNextSignWrite();

            await OperationError("COMMIT_UNKNOWN",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            var reread = await fixture.Application.ReadVipPageAsync();
            True(reread.Sign is { SignedToday: false } && reread.SignUnconfirmed &&
                !reread.SignedToday);
            await OperationError("COMMIT_UNKNOWN",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            Equal(1, fixture.Transport.SignWrites);
        });
        yield return Case("VIP unknown sign receipt survives temporary account document unavailability", async () =>
        {
            await using var fixture = await VipApplicationFixture.CreateAsync();
            var snapshot = await fixture.Application.ReadVipPageAsync();
            fixture.Transport.FailNextSignWrite();

            await OperationError("COMMIT_UNKNOWN",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            fixture.FailNextAccountReads();
            var unavailable = await fixture.Application.ReadVipPageAsync();
            Equal(0L, unavailable.Generation);
            True(!unavailable.SignedToday && !unavailable.SignConfirmed &&
                !unavailable.SignUnconfirmed);

            var recovered = await fixture.Application.ReadVipPageAsync();
            Equal(snapshot.Generation, recovered.Generation);
            True(recovered.Sign is { SignedToday: false } && recovered.SignUnconfirmed &&
                !recovered.SignedToday);
            await OperationError("COMMIT_UNKNOWN",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            Equal(1, fixture.Transport.SignWrites);
        });
        yield return Case("VIP shutdown drains an admitted sign and resume never replays it", async () =>
        {
            await using var fixture = await VipApplicationFixture.CreateAsync();
            var snapshot = await fixture.Application.ReadVipPageAsync();
            var hold = fixture.Transport.HoldNextSignWrite();
            var signing = fixture.Application.SignVipPageAsync(snapshot.Generation);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var preparing = fixture.Application.PrepareShutdownAsync();
            True(!hold.Cancelled.Task.IsCompleted && !preparing.IsCompleted);
            hold.Release.TrySetResult();
            bool confirmed = true;
            try { await signing; }
            catch (MusicOperationException error) when (error.Code == "CLOSED") { confirmed = false; }
            await preparing;

            await fixture.Application.CancelShutdownPreparationAsync();
            var resumed = await fixture.Application.ReadVipPageAsync();
            True(resumed.SignUnconfirmed == !confirmed && resumed.SignedToday == confirmed);
            await OperationError(confirmed ? "ALREADY_SIGNED" : "COMMIT_UNKNOWN",
                () => fixture.Application.SignVipPageAsync(snapshot.Generation));
            Equal(1, fixture.Transport.SignWrites);
        });
        yield return Case("VIP typed calls share App shutdown admission and physical drain", async () =>
        {
            await using var fixture = await VipApplicationFixture.CreateAsync();
            var hold = fixture.Transport.HoldNextSignRead();
            var reading = fixture.Application.ReadVipPageAsync();
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var preparing = fixture.Application.PrepareShutdownAsync();
            await OperationError("CLOSED", () => fixture.Application.ReadVipPageAsync());
            await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!reading.IsCompleted && !preparing.IsCompleted);
            hold.Release.TrySetResult();
            try { await reading; }
            catch (Exception error) when (error is OperationCanceledException or
                MusicOperationException { Code: "CLOSED" }) { }
            await preparing;

            await fixture.Application.CancelShutdownPreparationAsync();
            var resumed = await fixture.Application.ReadVipPageAsync();
            True(resumed.Generation > 0);

            var disposal = fixture.Application.DisposeAsync().AsTask();
            await OperationError("CLOSED", () => fixture.Application.ReadVipPageAsync());
            await disposal;
        });
        yield return Case("VIP native policy rejects malformed or duplicate form fields before transport", async () =>
        {
            var calls = 0;
            using var transport = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(XeapiFixture.Response(J(new { code = 200 })));
            }));
            foreach (var body in new[] { "params", "=value", "params=a&params=b", "par%ZZams=a" })
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://interface.music.163.com/eapi/vipnewcenter/app/minidesk/music/sign/pc")
                {
                    Content = new StringContent(body, Encoding.ASCII,
                        "application/x-www-form-urlencoded")
                };
                await Error("INVALID_REQUEST",
                    () => transport.SendVipPageEapiAsync(request, default, 65536));
            }
            Equal(0, calls);
        });
        yield return Case("extension QQ favorites fixed locator actual wire and owner binding", async () =>
        {
            using var f = new Fixture(r =>
            {
                var q = RequestJson(r)["req_0"]!;
                if (S(q["method"]) == "GetPlaylistByUin") return QqCreated();
                var p = q["param"]!; Equal("201", S(p["dirid"])); Equal("0", S(p["disstid"]));
                Equal("SYNTHETIC_EUIN", S(p["enc_host_uin"])); Equal("true", S(p["tag"]));
                return QqFavorites();
            }, Session("tx"));
            var directory = await f.Catalog.PersonalPlaylistsAsync(new("tx", MusicPersonalListKind.Created));
            Equal("favorites", directory.GetProperty("items")[0].GetProperty("id").GetString());
            var locator = Locator(directory.GetProperty("items")[0].GetProperty("locator"));
            var page = await f.Catalog.PlaylistAsync(new("tx", "favorites", Personal: locator));
            Song(page.GetProperty("items")[0], "tx", "midA001"); Equal("midA001", page.GetProperty("memberIds")[0].GetString());
            f.Sessions.Current = Session("tx", 2);
            await Error("STALE_SESSION", () => f.Catalog.PlaylistAsync(new("tx", "favorites", Personal: locator)));
            Equal(2, f.Calls);
        });
        yield return Case("extension QQ favorites wrong owner is never accepted", async () =>
        {
            using var f = new Fixture(r => S(RequestJson(r)["req_0"]!["method"]) == "GetPlaylistByUin" ? QqCreated() : QqFavorites("99"), Session("tx"));
            var dir = await f.Catalog.PersonalPlaylistsAsync(new("tx", MusicPersonalListKind.Created));
            await Error("IDENTITY_MISMATCH", () => f.Catalog.PlaylistAsync(new("tx", "favorites", Personal: Locator(dir.GetProperty("items")[0].GetProperty("locator")))));
        });
        yield return Case("extension QQ All streams one backend page and does not prefetch entire directory", async () =>
        {
            var favoriteCalls = 0;
            using var f = new Fixture(r =>
            {
                var call = RequestJson(r)["req_0"]!;
                if (S(call["method"]) == "GetPlaylistByUin") return QqCreated(QqList("100", Huge, true));
                var offset = S(call["param"]!["offset"]); favoriteCalls++;
                return Tx(J(new { total = 2, hasmore = offset == "0" ? 1 : 0, hide = false, v_failTids = Array.Empty<object>(),
                    v_list = new[] { offset == "0" ? QqList("100", Huge, false) : QqList("200", "99", false) } }));
            }, Session("tx"));
            var page = await f.Catalog.PersonalPlaylistsAsync(new("tx", Limit: 1)); Equal(1, f.Calls); Equal(0, favoriteCalls);
            Equal(JsonValueKind.Null, page.GetProperty("total").ValueKind);
            page = await f.Catalog.PersonalPlaylistsAsync(new("tx", Page: 2, Limit: 1, Continuation: page.GetProperty("continuation"))); Equal(0, favoriteCalls);
            page = await f.Catalog.PersonalPlaylistsAsync(new("tx", Page: 3, Limit: 1, Continuation: page.GetProperty("continuation")));
            Equal(1, favoriteCalls); Equal(0, page.GetProperty("items").GetArrayLength()); True(page.GetProperty("more").GetBoolean());
            page = await f.Catalog.PersonalPlaylistsAsync(new("tx", Page: 4, Limit: 1, Continuation: page.GetProperty("continuation")));
            Equal(2, favoriteCalls); Equal(3L, page.GetProperty("total").GetInt64()); True(!page.GetProperty("more").GetBoolean());
        });
        yield return Case("extension QQ subscriptions short nonterminal page cannot skip offsets", async () =>
        {
            using var f = new Fixture(_ => Tx(J(new { total = 3, hasmore = 1, hide = false, v_failTids = Array.Empty<object>(),
                v_list = new[] { QqList("100", "99", false) } })), Session("tx"));
            await Error("INCOMPLETE_PAGE", () => f.Catalog.PersonalPlaylistsAsync(new("tx", MusicPersonalListKind.Subscribed, Limit: 2)));
        });
        yield return Case("extension KG owned raw member occurrences sort counts and signed body", async () =>
        {
            using var f = new Fixture(r =>
            {
                VerifyKg(r);
                if (r.RequestUri!.AbsolutePath == "/v7/get_all_list") return KgDirectory(2);
                Equal("/v4/get_list_all_file_v3", r.RequestUri.AbsolutePath);
                var body = RequestJson(r); Equal(Huge, S(body["listid"])); Equal(Huge, S(body["userid"]));
                Equal("0", S(body["type"])); Equal("30", S(body["pagesize"]));
                return KgMembers(2, "9", [KgMember("1001", 10), KgMember("1002", 1)]);
            }, Session("kg"));
            var dir = await f.Catalog.PersonalPlaylistsAsync(new("kg"));
            var locator = Locator(dir.GetProperty("items")[0].GetProperty("locator"));
            var result = await f.Catalog.PlaylistAsync(new("kg", "0:" + Huge, Limit: 30, Personal: locator));
            Equal(2, result.GetProperty("items").GetArrayLength());
            Equal(KgHash, MusicSongFixture.NativeId(result.GetProperty("items")[0]));
            Equal(KgHash, MusicSongFixture.NativeId(result.GetProperty("items")[1]));
            Equal("1001", result.GetProperty("memberIds")[0].GetString()); Equal("1002", result.GetProperty("memberIds")[1].GetString());
            Equal(10, result.GetProperty("memberSorts")[0].GetInt32()); Equal(1, result.GetProperty("memberSorts")[1].GetInt32());
            await Error("INVALID_REQUEST", () => f.Catalog.PlaylistAsync(new("kg", "0:" + Huge, Limit: 100, Personal: locator)));
            f.Sessions.Current = Session("kg", 2);
            await Error("STALE_SESSION", () => f.Catalog.PlaylistAsync(new("kg", "0:" + Huge, Limit: 30, Personal: locator)));
            Equal(2, f.Calls);
        });
        foreach (var failure in new[] { "version", "count", "fileid" })
            yield return Case("extension KG member " + failure + " mismatch fails", async () =>
            {
                using var f = new Fixture(r => r.RequestUri!.AbsolutePath == "/v7/get_all_list" ? KgDirectory(2)
                    : KgMembers(failure == "count" ? 3 : 2, failure == "version" ? "10" : "9",
                        [KgMember("1", 1), KgMember(failure == "fileid" ? "1" : "2", 2)]), Session("kg"));
                var dir = await f.Catalog.PersonalPlaylistsAsync(new("kg"));
                var locator = Locator(dir.GetProperty("items")[0].GetProperty("locator"));
                await Error(failure == "fileid" ? "PAGINATION_STALLED" : "SNAPSHOT_CHANGED",
                    () => f.Catalog.PlaylistAsync(new("kg", "0:" + Huge, Limit: 30, Personal: locator)));
            });
        yield return Case("extension KG private pages beyond 100 and verified empty", async () =>
        {
            using var f = new Fixture(r =>
            {
                if (r.RequestUri!.AbsolutePath == "/v7/get_all_list") return KgDirectory(3031);
                Equal("101", S(RequestJson(r)["page"]));
                return KgMembers(3031, "9", Enumerable.Range(3001, 30).Select(i => KgMember(i.ToString(), i)).ToArray());
            }, Session("kg"));
            var dir = await f.Catalog.PersonalPlaylistsAsync(new("kg"));
            var result = await f.Catalog.PlaylistAsync(new("kg", "0:" + Huge, Page: 101, Limit: 30, Personal: Locator(dir.GetProperty("items")[0].GetProperty("locator"))));
            True(result.GetProperty("more").GetBoolean()); Equal(102, result.GetProperty("continuation").GetProperty("page").GetInt32());
            using var empty = new Fixture(r => r.RequestUri!.AbsolutePath == "/v7/get_all_list" ? KgDirectory(0)
                : Kg(J(new { count = 0, list_ver = 9, info = (object?)null })), Session("kg"));
            var d = await empty.Catalog.PersonalPlaylistsAsync(new("kg"));
            var page = await empty.Catalog.PlaylistAsync(new("kg", "0:" + Huge, Limit: 30, Personal: Locator(d.GetProperty("items")[0].GetProperty("locator"))));
            Equal(0, page.GetProperty("memberIds").GetArrayLength()); True(!page.GetProperty("more").GetBoolean());
        });
        yield return Case("extension KG classified directory counts accept only explicit zero empty", async () =>
        {
            using var f = new Fixture(_ => Kg(J(new { list_count = 0, collect_count = 0, album_count = 0, info = (object?)null })), Session("kg"));
            var page = await f.Catalog.PersonalPlaylistsAsync(new("kg")); Equal(0L, page.GetProperty("total").GetInt64());
            using var bad = new Fixture(_ => Kg(J(new { list_count = 0, collect_count = 0, info = (object?)null })), Session("kg"));
            await Error("INVALID_RESPONSE", () => bad.Catalog.PersonalPlaylistsAsync(new("kg")));
        });
        foreach (var (title, artist, expected) in new[] { ("İSTANBUL", "ΟΣ", "song:b50a579dd05f16a3"), ("ΟΣ́", "İ", "song:d8a203994447b8df") })
            yield return Case("extension casing identity golden " + expected, async () =>
            {
                var row = Wy("1"); row["name"] = title; row["ar"] = new JsonArray(J(new { name = artist }));
                using var f = new Fixture(_ => WySearch([row], 1));
                Equal(expected, (await f.Catalog.SearchAsync(new("wy", "x"))).GetProperty("items")[0].GetProperty("songId").GetString());
            });
        yield return Case("extension invalid optional qualities do not erase valid recording", async () =>
        {
            var row = KgSong(); row["HQFileHash"] = new string('0', 32); row["HQFileSize"] = 100;
            row["SQFileHash"] = KgHash; row["SQFileSize"] = "not-a-size";
            using var f = new Fixture(_ => Kg(J(new { total = 1, lists = new[] { row } })));
            var r = (await f.Catalog.SearchAsync(new("kg", "x"))).GetProperty("items")[0];
            Song(r, "kg", KgHash); Equal(1, MusicSongFixture.Info(r).GetProperty("types").GetArrayLength());
        });
        foreach (var branch in new[] { "search", "search-playlists", "song", "playlist", "album", "artist", "home",
            "directory", "fm", "fm-rule", "fm-preview", "similar", "heartbeat", "queue", "personal", "rankings",
            "new-songs", "square", "artists", "cube-layout", "cube-tags", "cube-feed", "vip-songs", "vip-rankings", "chorus" })
            foreach (bool valid in new[] { false, true })
            {
                var b = branch; var v = valid;
                yield return Case("WY catalog " + b + (v ? " validates before client handoff" : " rejects client handoff for invalid content"),
                    () => CatalogClientHandoff(b, v));
            }
    }
    private static async Task CatalogClientHandoff(string branch, bool valid)
    {
        const string directory = "/eapi/link/position/show/resource", cube = "/weapi/cube/render/page/protocol",
            tags = "/eapi/tag/tab/tags", feed = "/eapi/tag/tab/alg/recommends",
            vipSongs = "/eapi/vipnewcenter/app/viptab/recommend/song/list", rankings = "/weapi/toplist/detail/v2";
        string route = branch switch
        {
            "search" or "search-playlists" => "/eapi/cloudsearch/pc", "song" => "/weapi/v3/song/detail",
            "playlist" => "/eapi/v6/playlist/detail", "album" => "/weapi/v1/album/1", "artist" => "/weapi/v1/artist/1",
            "home" => "/eapi/link/page/rcmd/resource/show", "directory" => directory,
            "fm" => "/eapi/v1/radio/get", "fm-rule" => "/eapi/tag/tab/fm/recommends",
            "fm-preview" => "/eapi/tag/tab/fm/cover", "similar" => "/weapi/v1/radio/get",
            "heartbeat" => "/eapi/playmode/intelligence/list", "queue" => "/eapi/homepage/more/rcmd/song",
            "personal" => "/weapi/user/playlist", "rankings" or "vip-rankings" => rankings,
            "new-songs" => "/weapi/v1/discovery/new/songs", "square" => "/weapi/playlist/square/block/page",
            "artists" => "/weapi/v1/artist/list", "cube-layout" => cube, "cube-tags" => tags, "cube-feed" => feed,
            "vip-songs" => vipSongs, "chorus" => "/eapi/song/chorus",
            _ => throw new InvalidOperationException("Unknown catalog client fixture")
        };
        JsonObject Playlist() => J(new { id = "500", name = "合成歌单", trackCount = 1,
            creator = new { userId = Huge, nickname = "合成账号" } });
        JsonObject Rankings() => J(new { code = 200, data = new[] { new { name = "排行榜", categoryCode = "vip",
            list = new[] { new { targetType = "PLAYLIST", resourceId = "500", name = "黑胶VIP 热歌榜", canPlay = true } } } } });
        using var f = new Fixture(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path != route)
                return path switch
                {
                    directory => Directory(), cube => Cube(),
                    tags => Json(J(new { code = 200, data = new { algTagInfos = Array.Empty<object>() } })),
                    feed => Json(J(new { code = 200, data = new { songs = new[] { Wy("1") }, hasMore = false } })),
                    vipSongs => Json(J(new { code = 200, data = new { songInfoList = Array.Empty<object>() } })),
                    rankings => Json(J(new { code = 200, data = Array.Empty<object>() })),
                    _ => throw new InvalidOperationException("Unexpected catalog client fixture route")
                };
            JsonObject body;
            if (valid)
                body = branch switch
                {
                    "search" => J(new { code = 200, result = new { songs = new[] { Wy("1") }, songCount = 1 } }),
                    "search-playlists" => J(new { code = 200, result = new { playlists = new[] { Playlist() }, playlistCount = 1 } }),
                    "song" => J(new { code = 200, songs = new[] { Wy("1") } }),
                    "playlist" => J(new { code = 200, playlist = new { id = "500", name = "合成歌单", trackCount = 1,
                        trackIds = new[] { new { id = "1" } }, tracks = new[] { Wy("1") } } }),
                    "album" => J(new { code = 200, album = new { id = "1", name = "合成专辑", size = 1 }, songs = new[] { Wy("1") } }),
                    "artist" => J(new { code = 200, artist = new { id = "1", name = "合成歌手",
                        picUrl = "https://images.invalid/artist.jpg" }, hotSongs = new[] { Wy("1") } }),
                    "home" or "square" => J(new { code = 200, data = new { cursor = "0", hasMore = false, blocks = Array.Empty<object>() } }),
                    "directory" => J(new { code = 200, data = new { channels = Array.Empty<object>() } }),
                    "fm" or "fm-rule" or "similar" or "new-songs" => J(new { code = 200, data = new[] { Wy("1") } }),
                    "fm-preview" => J(new { code = 200, data = new { cover = "https://images.invalid/fm.jpg" } }),
                    "heartbeat" => J(new { code = 200, data = new[] { new { songInfo = Wy("1") } } }),
                    "queue" => J(new { code = 200, data = new { songList = new[] { Wy("1") } } }),
                    "personal" => J(new { code = 200, playlist = new[] { Playlist() }, more = false }),
                    "rankings" or "vip-rankings" => Rankings(),
                    "artists" => J(new { code = 200, artists = new[] { new { id = "1", name = "合成歌手" } }, more = false }),
                    "cube-tags" => J(new { code = 200, data = new { algTagInfos = Array.Empty<object>() } }),
                    "cube-feed" => J(new { code = 200, data = new { songs = new[] { Wy("1") }, hasMore = false } }),
                    "vip-songs" => J(new { code = 200, data = new { songInfoList = new[] { Wy("1") } } }),
                    "chorus" => J(new { code = 200, chorus = new[] { new { id = "1", startTime = 1000, endTime = 2000 } } }),
                    "cube-layout" => J(new { code = 200 }),
                    _ => throw new InvalidOperationException("Unknown valid catalog client body")
                };
            else
                body = branch switch
                {
                    "song" => J(new { code = 200, songs = new[] { Wy("2") } }),
                    "personal" => J(new { code = 200, playlist = new[] { Playlist(), Playlist() }, more = false }),
                    "chorus" => J(new { code = 200, chorus = new[] { new { id = "2", startTime = 1000, endTime = 2000 } } }),
                    "cube-tags" => J(new { code = 200, data = new { algTagInfos = false } }),
                    _ => J(new { code = 200 })
                };
            var response = valid && branch == "cube-layout" ? Cube() : Json(body);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_CATALOG_HANDOFF; Domain=music.163.com");
            return response;
        }, Session("wy"));
        var before = await f.Clients.Owner.AcquireWyAsync();
        Task<JsonElement> Read() => branch switch
        {
            "search" or "search-playlists" => f.Catalog.SearchAsync(new("wy", "x",
                branch == "search" ? MusicSearchKind.Songs : MusicSearchKind.Playlists)),
            "song" => f.Catalog.SongAsync(new("wy", "1")), "playlist" => f.Catalog.PlaylistAsync(new("wy", "500")),
            "album" => f.Catalog.AlbumAsync(new("wy", "1")), "artist" => f.Catalog.ArtistAsync(new("wy", "1")),
            "home" => f.Catalog.HomeAsync(new("wy")), "directory" => f.Catalog.ChannelsAsync(new("wy")),
            "fm" => f.Catalog.FmAsync(new()), "fm-rule" => f.Catalog.FmAsync(new(RuleId: "123")),
            "fm-preview" => f.Catalog.FmAsync(new(Preview: true, RuleId: "123")),
            "similar" => f.Catalog.SimilarAsync(new("wy", ["1"])), "heartbeat" => f.Catalog.HeartbeatAsync(new("wy", "500", "1", "1")),
            "queue" => f.Catalog.QueueRecommendationsAsync(new("wy", ["1"], "1")), "personal" => f.Catalog.PersonalPlaylistsAsync(new("wy")),
            "rankings" => f.Catalog.ChannelAsync(new("wy", "chart")), "new-songs" => f.Catalog.ChannelAsync(new("wy", "newsong")),
            "square" => f.Catalog.ChannelAsync(new("wy", "playlist")), "artists" => f.Catalog.ChannelAsync(new("wy", "artist")),
            "cube-layout" or "cube-tags" or "cube-feed" => f.Catalog.ChannelAsync(new("wy", "rock")),
            "vip-songs" or "vip-rankings" => f.Catalog.ChannelAsync(new("wy", "vip")),
            "chorus" => f.Catalog.ChorusAsync("wy", "1"),
            _ => throw new InvalidOperationException("Unknown catalog client consumer")
        };
        if (valid || branch is "chorus" or "vip-songs" or "vip-rankings")
        {
            var result = await Read();
            True(!result.GetRawText().Contains("SYNTHETIC_CATALOG_HANDOFF", StringComparison.Ordinal));
            if (!valid && branch == "chorus") Equal(JsonValueKind.Null, result.GetProperty("point").ValueKind);
            if (branch is "vip-songs" or "vip-rankings") Equal(valid ? 1 : 0, result.GetProperty("sections").GetArrayLength());
        }
        else
            await Error(branch switch
            {
                "song" or "playlist" or "album" or "artist" => "IDENTITY_MISMATCH",
                "personal" => "PAGINATION_STALLED", _ => "INVALID_RESPONSE"
            }, () => Read());
        var after = await f.Clients.Owner.AcquireWyAsync();
        Equal(before.Generation + (valid ? 1 : 0), after.Generation);
        if (valid) Equal("SYNTHETIC_CATALOG_HANDOFF", after.Nmtid);
        else True(ReferenceEquals(before, after) && f.Clients.Owner.IsCurrent(before));
        Equal(branch switch
        {
            "rankings" or "new-songs" or "square" or "artists" => 2,
            "cube-layout" => valid ? 4 : 2, "cube-tags" => valid ? 4 : 3, "cube-feed" => 4,
            "vip-songs" or "vip-rankings" => 3, _ => 1
        }, f.Calls);
    }
    private static MusicPlatformSession DeviceSession() => new("wy", "synthetic-scope", 1, Huge,
        "MUSIC_U=SYNTHETIC_ONLY; __csrf=SYNTHETIC_CSRF; deviceId=SYNTHETIC_DEVICE");
    private static MusicPersonalPlaylistLocator Locator(JsonElement value) => new(value.GetProperty("scope").GetString()!,
        value.GetProperty("kind").GetString() == "qq-favorites" ? MusicPersonalPlaylistKind.QqFavorites : MusicPersonalPlaylistKind.KugouOwned,
        value.GetProperty("listId").GetString()!, value.GetProperty("total").ValueKind == JsonValueKind.Null ? null : value.GetProperty("total").GetInt64(),
        value.GetProperty("version").ValueKind == JsonValueKind.Null ? null : value.GetProperty("version").GetString(), value.GetProperty("name").GetString());
    private static HttpResponseMessage QqCreated(params JsonObject[] rows) => Tx(J(new { total = rows.Length, bFinish = true,
        v_delTid = Array.Empty<object>(), v_playlist = rows }));
    private static HttpResponseMessage QqFavorites(string owner = Huge) => Tx(J(new { total_song_num = 1, hasmore = 0, songlist_size = 1,
        dirinfo = new { id = "0", dirid = 201, title = "我喜欢", creator = new { musicid = JsonNode.Parse(owner) } }, songlist = new[] { Qq() } }));
    private static HttpResponseMessage KgDirectory(int count) => Kg(J(new { count = 1, total_ver = 9, info = new[] {
        new { listid = Huge, type = 0, source = 1, list_create_userid = Huge, count, name = "合成歌单", list_ver = "9" } } }));
    private static JsonObject KgMember(string fileId, int sort) { var row = KgSong(); row["fileid"] = JsonNode.Parse(fileId); row["sort"] = sort; return row; }
    private static HttpResponseMessage KgMembers(int count, string version, JsonObject[] rows) =>
        Kg(J(new { count, list_info = new { listid = Huge, type = 0, list_create_userid = Huge, list_ver = version }, info = rows }));
    private static byte[] Clamp(byte[] key) { key[0] &= 248; key[31] = (byte)((key[31] & 127) | 64); return key; }
    private static ECDiffieHellman Peer(byte[] key) => ECDiffieHellman.Create(new ECParameters {
        Curve = ECCurve.CreateFromFriendlyName("curve25519"), Q = new ECPoint { X = key, Y = new byte[32] } });
    private sealed class XeapiFixture : IDisposable
    {
        private const string Signing = "mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g==";
        private static readonly byte[] StaticKey = Convert.FromHexString("ab1d5a430f6bb04a3f01e81ddd72bd916d5ce591248ac128714806d7f8fb1b84");
        private readonly ECDiffieHellman server = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("curve25519"));
        internal HttpResponseMessage PublicKey(HttpRequestMessage request, string failure = "", string expectedDevice = "SYNTHETIC_DEVICE")
        {
            Equal("interface.music.163.com", request.RequestUri!.Host); var p = Form(request);
            Equal("9.5.61", p["appVersion"]); Equal(expectedDevice, p["deviceId"]); Equal(16, p["nonce"].Length);
            Equal(Convert.ToBase64String(HMACSHA256.HashData(Encoding.ASCII.GetBytes(Signing), Encoding.UTF8.GetBytes(p["timestamp"] + p["nonce"]))), p["signature"]);
            var state = J(new { version = "fixture-v1", publicKey = Convert.ToBase64String(failure == "key" ? new byte[32] : server.ExportParameters(false).Q.X!),
                sk = failure == "sk" ? "" : "SYNTHETIC_KEY_STATE" });
            return Json(J(new { code = 200, data = new {
                encryptedData = Convert.ToBase64String(AesBytes(StaticKey, Encoding.UTF8.GetBytes(state.ToJsonString()), true)),
                timestamp = p["timestamp"], signature = failure == "signature" ? Convert.ToBase64String(new byte[32]) : p["signature"] } }));
        }
        internal JsonObject Open(HttpRequestMessage request, string expectedDevice = "SYNTHETIC_DEVICE")
        {
            Equal("interface3.music.163.com", request.RequestUri!.Host);
            Equal("ENCRYPTED", request.Headers.GetValues("X-Client-Enc-State").Single());
            Equal(expectedDevice, request.Headers.GetValues("x-deviceid").Single());
            var form = Form(request); var sealedKey = Convert.FromBase64String(form["S"]); var ephemeral = sealedKey[..32];
            using var peer = Peer(ephemeral);
            var secret = server.DeriveRawSecretAgreement(peer.PublicKey);
            var prk = HMACSHA256.HashData(new byte[32], secret); byte[] info = [.. ephemeral, 1];
            var key = HMACSHA256.HashData(prk, info)[..16];
            var plainKey = new byte[sealedKey.Length - 32 - 12 - 16];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(sealedKey.AsSpan(32, 12), sealedKey.AsSpan(44, plainKey.Length), sealedKey.AsSpan(^16), plainKey);
            var parts = Encoding.UTF8.GetString(plainKey).Split('|'); Equal(3, parts.Length);
            Equal("android", parts[1]); Equal("SYNTHETIC_KEY_STATE", parts[2]);
            Equal("fixture-v1|", Encoding.UTF8.GetString(AesBytes(StaticKey, Convert.FromBase64String(form["R"]), false)));
            var content = form["C"].Replace('-', '+').Replace('_', '/'); content = content.PadRight((content.Length + 3) / 4 * 4, '=');
            var transformed = AesBytes(Convert.FromBase64String(parts[0]), Convert.FromBase64String(content), false);
            var mask = transformed[..16]; var rotated = transformed[16..]; var rotate = (mask[0] & 15) % rotated.Length;
            byte[] original = rotate == 0 ? rotated : [.. rotated[^rotate..], .. rotated[..^rotate]];
            for (var i = 0; i < original.Length; i++) original[i] ^= mask[i & 15];
            return (JsonObject)JsonNode.Parse(AesBytes(StaticKey, original, false))!;
        }
        private static byte[] AesBytes(byte[] key, byte[] bytes, bool encrypt)
        {
            using var aes = Aes.Create(); aes.Key = key;
            return encrypt ? aes.EncryptEcb(bytes, PaddingMode.PKCS7) : aes.DecryptEcb(bytes, PaddingMode.PKCS7);
        }
        internal static HttpResponseMessage Response(JsonObject value, bool gzip = false)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
            if (gzip)
            {
                using var stream = new System.IO.MemoryStream();
                using (var compressor = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionLevel.Fastest, true)) compressor.Write(bytes);
                bytes = stream.ToArray();
            }
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AesBytes("e82ckenh8dichen8"u8.ToArray(), bytes, true)) };
            result.Headers.TryAddWithoutValidation("x-encr-ssid", "SYNTHETIC_UNUSED_SESSION");
            result.Headers.TryAddWithoutValidation("x-encr-sskey", "SYNTHETIC_UNUSED_KEY");
            return result;
        }
        public void Dispose() => server.Dispose();
    }
    private static TestCase Case(string name, Func<Task> run) => new("MusicNativePlatformTests." + name, run);
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static async Task Error(string expected, Func<Task> run)
    {
        try { await run(); }
        catch (MusicPlatformException e)
        {
            Equal(expected, e.Code); Equal(expected, e.Message); True(e.InnerException is null);
            True(!e.ToString().Contains("SYNTHETIC", StringComparison.Ordinal)); return;
        }
        throw new InvalidOperationException("Expected " + expected);
    }
    private static async Task OperationError(string expected, Func<Task> run)
    {
        try { await run(); }
        catch (MusicOperationException error)
        {
            Equal(expected, error.Code);
            Equal(expected, error.Message);
            return;
        }
        throw new InvalidOperationException("Expected " + expected);
    }
    private static JsonObject J(object value) => (JsonObject)JsonSerializer.SerializeToNode(value)!;
    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";
    private static HttpResponseMessage Json(JsonObject value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Tx(JsonObject data) => Json(J(new { code = 0, req_0 = new { code = 0, data } }));
    private static HttpResponseMessage? QqClientResponse(HttpRequestMessage request)
    {
        if (request.RequestUri!.Host == "api.tencentmusic.com")
            return Json(J(new { data = JsonSerializer.Serialize(new { code = 0,
                data = new { q16 = "SYNTHETIC_Q16", q36 = "SYNTHETIC_Q36" } }) }));
        var body = RequestJson(request);
        if (S(body["req_0"]!["method"]) != "GetSession") return null;
        var comm = body["comm"]!.AsObject();
        Equal("SYNTHETIC_Q36", S(comm["QIMEI36"]));
        True(!comm.ContainsKey("qq") && !comm.ContainsKey("authst") &&
            !comm.ContainsKey("tmeLoginType") && !comm.ContainsKey("uid") && !comm.ContainsKey("sid"));
        True(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"));
        return Tx(J(new { session = new { uid = "SYNTHETIC_UID", sid = "SYNTHETIC_SID" } }));
    }
    private static HttpResponseMessage QqSearchResponse(HttpRequestMessage request)
    {
        var body = RequestJson(request);
        Equal("DoSearchForQQMusicMobile", S(body["req_0"]!["method"]));
        Equal("SYNTHETIC_Q36", S(body["comm"]!["QIMEI36"]));
        Equal("SYNTHETIC_UID", S(body["comm"]!["uid"])); Equal("SYNTHETIC_SID", S(body["comm"]!["sid"]));
        bool songs = S(body["req_0"]!["param"]!["search_type"]) == "0";
        return Tx(J(new { meta = new { sum = 1, nextpage = -1 }, body = new JsonObject
        {
            [songs ? "item_song" : "item_songlist"] = new JsonArray(songs ? Qq() : J(new { id = 77, title = "搜索歌单" }))
        } }));
    }
    private static HttpResponseMessage Kg(JsonObject data) => Json(J(new { status = 1, error_code = 0, data }));
    private static async Task ShortNativeSearch(string platform, bool empty)
    {
        using var f = new Fixture(request =>
        {
            if (platform == "wy") return WySearch(empty ? Array.Empty<JsonObject>() : new[] { Wy("1") }, 100);
            if (platform == "tx")
                return Tx(J(new { meta = new { sum = 100, nextpage = 2 }, body = new JsonObject
                {
                    ["item_song"] = empty ? new JsonArray() : new JsonArray(Qq())
                } }));
            VerifyKg(request);
            return Kg(J(new { total = 100, lists = empty ? Array.Empty<object>() : new object[] { KgSong() } }));
        });
        var page = await f.Catalog.SearchAsync(new(platform, "x", Limit: 30, Pages: platform == "wy" ? 3 : 1));
        Equal(empty ? 0 : 1, page.GetProperty("items").GetArrayLength());
        bool more = !empty && platform == "tx";
        True(page.GetProperty("more").GetBoolean() == more);
        if (more) Equal(2, page.GetProperty("continuation").GetProperty("page").GetInt32());
        else Equal(JsonValueKind.Null, page.GetProperty("continuation").ValueKind);
        Equal(1, f.Calls);
    }
    private static JsonObject Wy(string id) => J(new { id = JsonNode.Parse(id), name = "测试曲", ar = new[] { new { name = "测试艺人" } },
        al = new { id = "31", name = "测试专辑", picUrl = "https://images.invalid/a.jpg" }, dt = 185000 });
    private static JsonObject Qq() => J(new { id = JsonNode.Parse(Huge), mid = "midA001", type = 0, title = "测试曲", singer = new[] { new { name = "测试艺人" } },
        album = new { mid = "albumM1", name = "测试专辑" }, interval = 185, file = new { media_mid = "mediaM1", size_128mp3 = 1000 } });
    private static JsonObject KgSong() => J(new { FileHash = KgHash, FileName = "测试艺人 - 测试曲.flac", SingerName = "测试艺人",
        AlbumName = "测试专辑", AlbumID = "31", MixSongID = JsonNode.Parse(Huge), Duration = 185, FileSize = 1000, Image = "https://images.invalid/{size}/a.jpg" });
    private static JsonObject QqList(string id, string owner, bool owned) => J(new { id, title = "列表", dirid = "202", uin = owner,
        invalid = false, songnum = 0, nickname = owned ? "本人" : "其他" });
    private static HttpResponseMessage WySearch(JsonObject[] songs, int total) => Json(J(new { code = 200, result = new { songs, songCount = total } }));
    private static HttpResponseMessage WyList(string[] ids) => Json(J(new { code = 200, playlist = new { id = "500", name = "列表", trackCount = ids.Length,
        trackIds = ids.Select(id => new { id }).ToArray() } }));
    private static HttpResponseMessage WyList(string[] ids, JsonObject[] tracks) => Json(J(new { code = 200, playlist = new { id = "500", name = "列表", trackCount = ids.Length,
        trackIds = ids.Select(id => new { id }).ToArray(), tracks } }));
    private static HttpResponseMessage Home(bool more, string cursor, params JsonObject[] resources) => Json(J(new { code = 200, data = new { cursor, hasMore = more,
        blocks = new[] { new { positionCode = "PLAYLIST", dslData = new { blockResource = new { header = new { title = "推荐" }, items = resources } } } } } }));
    private static HttpResponseMessage Directory() => Json(J(new { code = 200, data = new { channels = new[] {
        new { code = "feature", name = "精选", url = "" }, new { code = "playlist", name = "歌单", url = "" },
        new { code = "chart", name = "排行榜", url = "" }, new { code = "vip", name = "VIP", url = "" },
        new { code = "euro", name = "欧美", url = "orpheus://rnpage?component=cube-renderer-rn&page=MUSIC_EURO" },
        new { code = "music_top_tab_acg", name = "二次元", url = "orpheus://rnpage?component=cube-renderer-rn&page=MUSIC_ACG" },
        new { code = "newsong", name = "新歌", url = "" }, new { code = "rock", name = "摇滚", url = "orpheus://rnpage?component=cube-renderer-rn&page=MUSIC_ROCK" } } } }));
    private static HttpResponseMessage Cube() => Json(J(new { code = 200, data = new { pageProtocol = new { title = "摇滚", pages = new[] {
        new { id = "root", componentName = "Root", props = new { songList = new { ruleId = "123", title = "推荐曲", pointId = new[] { new { id = "4" } } } } } } } } }));
    private static HttpResponseMessage CubePage(params JsonObject[] pages) => Json(J(new {
        code = 200, data = new { pageProtocol = new { title = "频道", pages } }
    }));
    private static JsonObject RequestJson(HttpRequestMessage request) => (JsonObject)JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace("+", " ", StringComparison.Ordinal)));
    private static Dictionary<string, string> Form(HttpRequestMessage request) => Query(new Uri("https://fixture.invalid/?" + request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
    private static void Weapi(HttpRequestMessage request, string path)
    {
        Equal("music.163.com", request.RequestUri!.Host); Equal(path, request.RequestUri.AbsolutePath);
        var form = Form(request); Equal(256, form["encSecKey"].Length); True(Convert.FromBase64String(form["params"]).Length > 0);
    }
    private static (string Path, JsonObject Body) Eapi(HttpRequestMessage request) =>
        Eapi(request, "interfacepc.music.163.com");
    private static (string Path, JsonObject Body) VipEapi(HttpRequestMessage request) =>
        Eapi(request, "interface.music.163.com");
    private static (string Path, JsonObject Body) Eapi(HttpRequestMessage request, string host)
    {
        Equal(host, request.RequestUri!.Host);
        using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
        var plain = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(Form(request)["params"]), PaddingMode.PKCS7));
        var parts = plain.Split("-36cd479b6b5-"); Equal(3, parts.Length);
        Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{parts[0]}use{parts[1]}md5forencrypt"))), parts[2]);
        var body = (JsonObject)JsonNode.Parse(parts[1])!;
        var header = body["header"]!.AsObject();
        var cookies = request.Headers.GetValues("Cookie").Single().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Split('=', 2)).ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        True(cookies.Count == header.Count && header.All(pair =>
            pair.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
            cookies.TryGetValue(pair.Key, out var cookie) && cookie == text));
        return (parts[0], body);
    }
    private static void VerifyKg(HttpRequestMessage request)
    {
        Equal("gateway.kugou.com", request.RequestUri!.Host);
        var q = Query(request.RequestUri); var signature = q["signature"]; q.Remove("signature");
        var text = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
        const string salt = "OIlwieks28dk2k092lksi2UIkp";
        var expected = salt + string.Concat(q.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + text + salt;
        Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(expected))), signature);
    }
    private static void Song(JsonElement s, string platform, string id)
    {
        True(!s.TryGetProperty("id", out _));
        True(s.GetProperty("songId").GetString()!.StartsWith("song:", StringComparison.Ordinal));
        var native = MusicSongFixture.Info(s); Equal(platform, native.GetProperty("source").GetString());
        Equal(id, native.GetProperty("songmid").GetString());
        Equal(1, s.GetProperty("sources").GetArrayLength()); True(s.GetProperty("songId").GetString() != id);
    }
    private static MusicPlatformSession Session(string platform, long generation = 1) => new(platform, "synthetic-scope", generation, Huge,
        platform == "wy" ? "MUSIC_U=SYNTHETIC_ONLY; __csrf=SYNTHETIC_CSRF"
        : platform == "tx" ? $"musicid={Huge}; musickey=SYNTHETIC_ONLY; encrypt_uin=SYNTHETIC_EUIN"
        : $"userid={Huge}; token=SYNTHETIC_ONLY; KUGOU_API_MID=123456789");
    private sealed class Source : IMusicPlatformSessionSource
    {
        public MusicPlatformSession? Current;
        public bool Throw;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("SYNTHETIC_SECRET");
            cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Current?.Platform == platform ? Current : null);
        }
        public bool IsCurrent(MusicPlatformSession session) => ReferenceEquals(Current, session);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan by) => current += by;
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Source Sessions = new();
        public readonly MusicPlatformCatalog Catalog;
        public readonly MusicClientContextFixture Clients = new();
        public int Calls;
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> send, MusicPlatformSession? session = null, bool readyQq = true)
            : this((r, _) => Task.FromResult(send(r)), session, readyQq: readyQq) { }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            MusicPlatformSession? session = null, TimeSpan? timeout = null, bool readyQq = true)
        {
            Sessions.Current = session;
            if (readyQq) Clients.SeedQqAsync().GetAwaiter().GetResult();
            Catalog = MusicPlatformCatalog.CreateWithHandler(Sessions, new Handler((r, ct) => { Interlocked.Increment(ref Calls); return send(r, ct); }), Clients.Owner, timeout);
        }
        public void Dispose() { Catalog.Dispose(); Clients.Dispose(); }
    }

    private sealed class VipApplicationFixture : IAsyncDisposable
    {
        private readonly VipApplicationStore store;
        internal VipApplicationTransport Transport { get; }
        internal MusicNativeApplication Application { get; }

        private VipApplicationFixture(VipApplicationStore store,
            VipApplicationTransport transport)
        {
            this.store = store;
            Transport = transport;
            Application = new(store, new NoRuntime(), transport);
        }

        internal static async Task<VipApplicationFixture> CreateAsync()
        {
            var root = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ??
                throw new InvalidOperationException("Isolated test data required");
            var path = Path.Combine(root, "vip-native-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(path);
            var store = new VipApplicationStore(new LocalDocumentStore(path, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            var fixture = new VipApplicationFixture(store, new());
            try
            {
                var result = await fixture.Application.ExecuteAsync("music.accounts.import",
                    new
                    {
                        platform = "wy",
                        cookie = "MUSIC_U=SYNTHETIC_VIP; __csrf=SYNTHETIC_CSRF"
                    });
                True(result.GetProperty("ok").GetBoolean());
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal void FailNextAccountReads() => store.FailNextAccountReads();

        public async ValueTask DisposeAsync()
        {
            try { await Application.DisposeAsync(); }
            finally { await store.DisposeAsync(); }
        }
    }

    private sealed class VipApplicationStore(LocalDocumentStore inner) : IDocumentStore
    {
        private readonly object gate = new();
        private int accountReadsToFail;

        internal void FailNextAccountReads()
        {
            lock (gate)
            {
                if (accountReadsToFail != 0)
                    throw new InvalidOperationException("VIP account read failure already armed");
                accountReadsToFail = 2;
            }
        }

        public Task<DocumentSnapshot> ReadAsync(string documentNamespace,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (documentNamespace == "music.accounts.v1" && accountReadsToFail > 0)
                {
                    accountReadsToFail--;
                    return Task.FromException<DocumentSnapshot>(
                        new DocumentStoreException(DocumentStoreError.Unavailable));
                }
            }
            return inner.ReadAsync(documentNamespace, cancellationToken);
        }

        public Task<DocumentFingerprint> WriteAsync(string documentNamespace,
            ReadOnlyMemory<byte> data, DocumentFingerprint expected,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(documentNamespace, data, expected, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class VipApplicationTransport : HttpMessageHandler
    {
        private readonly object gate = new();
        private VipHttpHold? nextSignRead, nextSignWrite;
        private bool failNextSignWrite;
        internal int SignReads, SignWrites;

        internal VipHttpHold HoldNextSignRead()
        {
            lock (gate)
            {
                if (nextSignRead is not null)
                    throw new InvalidOperationException("VIP sign read already held");
                return nextSignRead = new();
            }
        }

        internal VipHttpHold HoldNextSignWrite()
        {
            lock (gate)
            {
                if (nextSignWrite is not null)
                    throw new InvalidOperationException("VIP sign write already held");
                return nextSignWrite = new();
            }
        }

        internal void FailNextSignWrite()
        {
            lock (gate)
            {
                if (failNextSignWrite)
                    throw new InvalidOperationException("VIP sign write failure already armed");
                failNextSignWrite = true;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/weapi/w/nuser/account/get")
                return Json(J(new
                {
                    code = 200,
                    profile = new
                    {
                        userId = JsonNode.Parse("9007199254740997"),
                        nickname = "VIP fixture",
                        avatarUrl = "https://img.invalid/avatar.jpg"
                    },
                    account = new { id = JsonNode.Parse("9007199254740997") }
                }));
            if (path == "/weapi/vip-center-bff/task/sign")
            {
                Interlocked.Increment(ref SignWrites);
                VipHttpHold? hold;
                bool fail;
                lock (gate)
                {
                    hold = nextSignWrite;
                    nextSignWrite = null;
                    fail = failNextSignWrite;
                    failNextSignWrite = false;
                }
                if (hold is not null)
                    await hold.WaitAsync(cancellationToken);
                if (fail)
                    throw new HttpRequestException("Synthetic lost VIP sign receipt");
                return Json(J(new { code = 200, data = true }));
            }
            if (path == "/eapi/vipnewcenter/app/minidesk/music/sign/pc")
            {
                Interlocked.Increment(ref SignReads);
                VipHttpHold? hold;
                lock (gate)
                {
                    hold = nextSignRead;
                    nextSignRead = null;
                }
                if (hold is not null)
                    await hold.WaitAsync(cancellationToken);
                return XeapiFixture.Response(J(new
                {
                    code = 200,
                    text = "10月黑胶乐签",
                    subText = "第2天，得3成长值",
                    redPrize = false,
                    redDay = false,
                    btnText = "打卡",
                    bgTexture = "https://img.invalid/sign.jpg",
                    signInfoList = new[]
                    {
                        new
                        {
                            dayText = "今天",
                            sign = false,
                            songCoverUrl = "",
                            signTime = 0,
                            today = true
                        }
                    }
                }));
            }
            if (path == "/eapi/vipnewcenter/app/resource/common/list")
                return XeapiFixture.Response(J(new
                {
                    code = 200,
                    data = new { contentList = Array.Empty<string>() }
                }));
            throw new InvalidOperationException("Unexpected VIP fixture request: " + path);
        }
    }

    private sealed class VipHttpHold
    {
        internal TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync(CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Arrived.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class NoRuntime : IRuntimeClient
    {
        public Task<JsonElement> CallAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("VIP native path must not start Node");
    }
}
