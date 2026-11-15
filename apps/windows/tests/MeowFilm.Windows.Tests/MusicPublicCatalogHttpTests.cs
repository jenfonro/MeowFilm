// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Windows.Tests.MusicNativeHttpSeamTests;
namespace MeowFilm.Windows.Tests;
public static class MusicPublicCatalogHttpTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
        {
            string p = platform;
            yield return new("music.public.runtime." + p + ".search-song-playlist-use-business-parameters", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture();
                f.OnRequest = (call, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { platform = call.Platform, operation = call.Operation, parameters = call.Parameters }));
                var search = await f.Catalog.SearchAsync(new(p, "完整歌名 / 中文", MusicSearchKind.Playlists, Page: 107, Limit: 31));
                Check(search.GetProperty("parameters").GetProperty("keyword").GetString() == "完整歌名 / 中文" && search.GetProperty("parameters").GetProperty("page").GetInt32() == 107);
                var song = await f.Catalog.SongAsync(new(p, "9007199254740993123"));
                Check(song.GetProperty("parameters").GetProperty("id").GetString() == "9007199254740993123");
                var continuation = MusicPlatformRuntimeFixture.Json(new { batch = 9, index = 17 });
                var page = await f.Catalog.PlaylistAsync(new(p, "canonical:alias", Page: 4, Limit: 73, Continuation: continuation));
                Check(page.GetProperty("parameters").GetProperty("continuation").GetRawText() == continuation.GetRawText());
                Check(f.Requests.Count == 3 && f.Requests.All(c => c.Platform == p));
            });
        }
        yield return new("music.public.runtime.catalog-disposal-fences-only-the-catalog-borrower", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { }));
            await f.Catalog.SearchAsync(new("kw", "x")); f.Catalog.Dispose();
            await Failure("CLOSED", () => f.Catalog.SearchAsync(new("kw", "x")));
            await f.Client.ReadAsync("kw", "search", new { keyword = "next" }, default); Check(f.Requests.Count == 2);
        });
    }
}
