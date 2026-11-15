using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Windows.Tests;
public static partial class MusicNativePlatformTests
{
    private static IEnumerable<TestCase> HomeHeartbeatCases()
    {
        yield return Case("dynamic home replaces content without native identity or complete-layout proof", async () =>
        {
            int round = 0;
            await using var f = Peer(_ => Json(new { entries = Enumerable.Range(0, ++round).Select(i => new { id = "current-" + i, available = true }).ToArray() }));
            Equal(1, (await f.Catalog.HomeAsync(new("wy"))).GetProperty("entries").GetArrayLength());
            Equal(2, (await f.Catalog.HomeAsync(new("wy", Refresh: true))).GetProperty("entries").GetArrayLength());
            Equal(2, f.Requests.Count);
        });
        yield return Case("home preserves declared entry display independently from action availability", async () =>
        {
            var value = Json(new { platform = "wy", entries = new object[]
            {
                new { id = "daily", requiresAccount = true, display = false, available = false,
                    errorCode = "AUTH_REQUIRED", action = new { type = "daily", branch = "default" } },
                new { id = "fm", requiresAccount = false, display = true, available = false,
                    errorCode = "FORBIDDEN", action = new { type = "fm" } }
            } });
            await using var f = Peer(call =>
            {
                Equal("home", call.Operation); True(call.Parameters.GetProperty("refresh").GetBoolean());
                return value;
            });
            Equal(value.GetRawText(), (await f.Catalog.HomeAsync(new("wy", Refresh: true))).GetRawText());
            Equal(1, f.Requests.Count);
        });
        yield return Case("heartbeat uses the action seed and forwards actual progression without snapshot checks", async () =>
        {
            var continuation = new MusicHeartbeatContinuation("playlist-alias", "first-seed", Huge, 2);
            await using var f = Peer(call =>
            {
                Equal("heartbeat", call.Operation); Equal("tail", call.Parameters.GetProperty("songId").GetString());
                Equal("playlist-alias", call.Parameters.GetProperty("continuation").GetProperty("playlistId").GetString());
                return Json(new { items = new[] { MusicSongFixture.Online("wy", "new-member") }, context = new { count = 3 } });
            }, Session("wy"));
            var value = await f.Catalog.HeartbeatAsync(new("wy", MusicHeartbeatEntry.Favorites, "tail", "playlist-alias", continuation));
            Equal("new-member", MusicSongFixture.NativeId(value.GetProperty("items")[0])); Equal(1, f.Requests.Count);
        });
    }
}
