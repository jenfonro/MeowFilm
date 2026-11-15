// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities;
using static MeowFilm.Windows.Tests.MusicNativeHttpSeamTests;
namespace MeowFilm.Windows.Tests;
public static class MusicKgDiscoveryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music KG canonical rank keeps its real action locator and known preview song", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            var song = MusicSongFixture.Online("kg", "known-hash", "Synthetic track", duration: 279);
            f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { platform = "kg", sections = new[] { new {
                id = "rankings", title = "榜单", type = "rankings", items = new[] { new { id = "rank-8888", title = "Current rank", cover = "",
                    action = new { type = "channel", channel = new { id = "rank-8888", pageCode = "156133" } },
                    preview = new[] { new { title = "Synthetic track", artist = "Synthetic artist", cover = "", song } } }
                } } }, more = false }));
            var result = await f.Catalog.HomeAsync(new("kg"));
            var card = MusicCatalogDto.Read(result, "kg", "home", "").Single().Items.Single();
            Check(card.CanOpen && card.ActionId == "rank-8888" && card.PageCode == "156133");
            Check(card.PreviewSongs.Count == 1 && f.Requests.Count == 1);
        });
        yield return new("Music KG discovery accepts dynamic section counts and optional artwork", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { platform = "kg", sections = Enumerable.Range(0, 80).Select(i => new {
                id = "section-" + i, title = "栏目" + i, type = "playlists", items = new[] { new { id = "playlist-" + i, name = "歌单", cover = "", action = new { type = "playlist", id = "playlist-" + i } } }
            }).ToArray(), more = true, continuation = new { cursor = "actual-next" } }));
            var result = await f.Catalog.HomeAsync(new("kg")); Check(result.GetProperty("sections").GetArrayLength() == 80);
            Check(result.GetProperty("continuation").GetProperty("cursor").GetString() == "actual-next");
        });
        foreach (string code in new[] { "INVALID_RESPONSE", "NETWORK_ERROR", "UPSTREAM_ERROR", "VERIFICATION_REQUIRED" })
        {
            string c = code;
            yield return new("Music KG discovery preserves actual " + c + " failure", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => throw new MeowFilm.Capabilities.Platforms.MusicPlatformException(c);
                await Failure(c, () => f.Catalog.HomeAsync(new("kg"))); Check(f.Requests.Count == 1);
            });
        }
    }
}
