using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativePlatformTests
{
    private static IEnumerable<TestCase> HomeHeartbeatCases()
    {
        yield return Case("home heartbeat never borrows card or playlist identity when its declared seed is invalid", async () =>
        {
            foreach (var raw in new[] {
                """{"resourceType":"star","resourceId":"987","songData":{"id":0}}""",
                """{"resourceType":"star","resourceId":"987","songData":{"id":"invalid"}}"""
            })
            {
                using var f = new Fixture(_ => Home(false, "1", JsonNode.Parse(raw)!.AsObject(),
                    J(new { resourceType = "fm", resourceId = "988" })), Session("wy"));
                var entries = (await f.Catalog.HomeAsync(new("wy"))).GetProperty("entries");
                Equal(2, entries.GetArrayLength());
                var entry = entries[0];
                Equal("unsupported", entry.GetProperty("action").GetProperty("type").GetString());
                True(!entry.GetProperty("available").GetBoolean());
                Equal("fm", entries[1].GetProperty("action").GetProperty("type").GetString());
                Equal(1, f.Calls);
            }
        });
        yield return Case("unseeded home heartbeat reads one account seed without hydrating the liked library", async () =>
        {
            foreach (bool embedded in new[] { false, true })
            {
                int metadata = 0, directory = 0;
                using var f = new Fixture(request =>
                {
                    string route = request.RequestUri!.AbsolutePath;
                    if (route == "/weapi/user/playlist") { directory++; return HeartbeatDirectory(); }
                    if (route == "/weapi/v3/song/detail")
                    {
                        metadata++;
                        // Only the first member is needed, not all 150 metadata rows.
                        return Json(J(new { code = 200, songs = new[] { Wy("1") } }));
                    }
                    var (path, _) = Eapi(request);
                    if (path == "/api/link/page/rcmd/resource/show")
                        return Home(false, "1", J(new { resourceType = "star", resourceId = "987", playlistId = "42" }),
                            J(new { lunaItemType = "heartRcmd", resourceId = "988", sid = "99" }));
                    Equal("/api/v6/playlist/detail", path);
                    return HeartbeatPlaylist(Enumerable.Range(1, 150).Select(i => i.ToString()).ToArray(), embedded ? [Wy("2")] : []);
                }, Session("wy"));
                var home = await f.Catalog.HomeAsync(new("wy"));
                foreach (var item in home.GetProperty("entries").EnumerateArray())
                {
                    Equal("heartbeat", item.GetProperty("action").GetProperty("type").GetString());
                    Equal(embedded ? "2" : "1", item.GetProperty("action").GetProperty("songId").GetString());
                    Song(item.GetProperty("entrySong"), "wy", embedded ? "2" : "1");
                }
                Equal(1, directory); Equal(embedded ? 0 : 1, metadata);
            }
        });
        yield return Case("home heartbeat missing seed remains a login entrance for guests", async () =>
        {
            using var f = new Fixture(_ => Home(false, "1", J(new { resourceType = "star", resourceId = "987" })));
            var entry = (await f.Catalog.HomeAsync(new("wy"))).GetProperty("entries")[0];
            Equal("heartbeat", entry.GetProperty("action").GetProperty("type").GetString());
            True(!entry.GetProperty("action").TryGetProperty("songId", out _));
            Equal("AUTH_REQUIRED", entry.GetProperty("errorCode").GetString()); Equal(1, f.Calls);
        });
        yield return Case("empty platform favorites disable only the unseeded heartbeat card", async () =>
        {
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/weapi/user/playlist") return HeartbeatDirectory();
                var (path, _) = Eapi(request);
                if (path == "/api/link/page/rcmd/resource/show")
                    return Home(false, "1", J(new { resourceType = "star", resourceId = "987" }),
                        J(new { resourceType = "playlist", resourceId = "301", name = "Network" }));
                Equal("/api/v6/playlist/detail", path);
                return HeartbeatPlaylist([], []);
            }, Session("wy"));
            var page = await f.Catalog.HomeAsync(new("wy"));
            var entries = page.GetProperty("entries");
            Equal(2, entries.GetArrayLength());
            True(!entries[0].GetProperty("available").GetBoolean());
            Equal("playlist", entries[1].GetProperty("action").GetProperty("type").GetString());
            True(entries[1].GetProperty("available").GetBoolean());
            Equal(3, f.Calls);
        });
        yield return Case("current heartbeat resolves account playlist and rejects nonmembers without a local playlist ID", async () =>
        {
            int requests = 0;
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/weapi/user/playlist") return HeartbeatDirectory();
                var (path, body) = Eapi(request);
                if (path == "/api/v6/playlist/detail") return HeartbeatPlaylist(["1", "2"], [Wy("1"), Wy("2")]);
                Equal("/api/playmode/intelligence/list", path);
                Equal("500", S(body["playlistId"])); Equal("2", S(body["songId"]));
                Equal("fromPlayOne", S(body["type"])); requests++;
                return Json(J(new { code = 200, data = new[] { new { songInfo = Wy("1") } } }));
            }, Session("wy"));
            var result = await f.Catalog.HeartbeatAsync(new("wy", MusicHeartbeatEntry.CurrentSong, "2"));
            Song(result.GetProperty("entrySong"), "wy", "2");
            await Error("UNSUPPORTED", () => f.Catalog.HeartbeatAsync(new("wy", MusicHeartbeatEntry.CurrentSong, "999")));
            Equal(1, requests);
        });
        yield return Case("heartbeat continuation preserves entry seed playlist account and count while nonmember tail advances", async () =>
        {
            foreach (var entry in new[] { MusicHeartbeatEntry.CurrentSong, MusicHeartbeatEntry.Favorites })
            {
                int members = 0, batches = 0;
                using var f = new Fixture(request =>
                {
                    if (request.RequestUri!.AbsolutePath == "/weapi/user/playlist")
                    { members++; return HeartbeatDirectory(); }
                    var (path, body) = Eapi(request);
                    if (path == "/api/v6/playlist/detail") return HeartbeatPlaylist(["1", "2"], [Wy("1"), Wy("2")]);
                    Equal("/api/playmode/intelligence/list", path);
                    Equal("500", S(body["playlistId"])); Equal("2", S(body["startMusicId"]));
                    Equal(batches == 0 ? "2" : "99", S(body["songId"])); Equal("2", S(body["count"]));
                    Equal(entry == MusicHeartbeatEntry.Favorites ? "fromPlayAll" : "fromPlayOne", S(body["type"]));
                    batches++;
                    return Json(J(new { code = 200, data = new[] { new { songInfo = Wy(batches == 1 ? "99" : "100") } } }));
                }, Session("wy"));
                var initial = await f.Catalog.HeartbeatAsync(new("wy", entry, "2"));
                var context = initial.GetProperty("context");
                var continuation = new MusicHeartbeatContinuation(context.GetProperty("playlistId").GetString()!,
                    context.GetProperty("startMusicId").GetString()!, context.GetProperty("accountId").GetString()!,
                    context.GetProperty("count").GetInt64());
                var next = await f.Catalog.HeartbeatAsync(new("wy", entry, "99", "500", continuation));
                Song(next.GetProperty("items")[0], "wy", "100");
                Equal("99", next.GetProperty("context").GetProperty("songId").GetString());
                Equal(Huge, next.GetProperty("context").GetProperty("accountId").GetString());
                Equal(1, members); Equal(2, batches); Equal(4, f.Calls);
                await Error("STALE_SESSION", () => f.Catalog.HeartbeatAsync(
                    new("wy", entry, "100", "500", continuation with { AccountId = "42" })));
                Equal(4, f.Calls);
            }
        });
        yield return Case("home heartbeat retains a snapshot seed across repeat entry and reads account context without library sync", async () =>
        {
            string displayed = "2";
            int homeReads = 0, heartbeats = 0;
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/weapi/user/playlist")
                    return HeartbeatDirectory();
                var (path, body) = Eapi(request);
                if (path == "/api/link/page/rcmd/resource/show")
                {
                    homeReads++;
                    return Home(false, "1", J(new { resourceType = "star", resourceId = "987",
                        resourceExtInfo = new { songData = Wy(displayed) } }));
                }
                if (path == "/api/v6/playlist/detail")
                    return HeartbeatPlaylist(["1", "2", "3"], [Wy("1"), Wy("2"), Wy("3")]);
                Equal("/api/playmode/intelligence/list", path);
                Equal("500", S(body["playlistId"]));
                Equal(heartbeats < 2 ? "2" : "3", S(body["songId"]));
                Equal(S(body["songId"]), S(body["startMusicId"]));
                Equal("fromPlayAll", S(body["type"]));
                heartbeats++;
                return Json(J(new { code = 200, data = new[] { new { songInfo = Wy("1") } } }));
            }, Session("wy"));
            // The catalog has an account session, but no library or synchronization owner.
            var page = await f.Catalog.HomeAsync(new("wy"));
            var seed = page.GetProperty("entries")[0].GetProperty("action").GetProperty("songId").GetString()!;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var batch = await f.Catalog.HeartbeatAsync(new("wy", MusicHeartbeatEntry.Favorites, seed));
                Song(batch.GetProperty("entrySong"), "wy", "2");
            }
            displayed = "3";
            var refreshed = await f.Catalog.HomeAsync(new("wy", Refresh: true));
            var newSeed = refreshed.GetProperty("entries")[0].GetProperty("action").GetProperty("songId").GetString()!;
            var next = await f.Catalog.HeartbeatAsync(new("wy", MusicHeartbeatEntry.Favorites, newSeed));
            Song(next.GetProperty("entrySong"), "wy", "3");
            Equal("2", seed); Equal(2, homeReads); Equal(3, heartbeats);
        });
    }
}
