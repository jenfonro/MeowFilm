using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativePlatformTests
{
    private static IEnumerable<TestCase> ChartCases()
    {
        yield return Case("ranking layout and H5 destinations follow the directory rather than playability or fixed counts", async () =>
        {
            foreach (int count in new[] { 6, 7 })
            {
                var tracks = Enumerable.Range(1, 3).Select(i => new { songName = "曲目 " + i,
                    artistName = "艺人", coverUrl = "https://art.example.test/" + i + ".jpg", rank = i }).ToArray();
                var cards = Enumerable.Range(1, 4).Select(i => (object)new { targetType = "PLAYLIST", id = i,
                    name = "榜单 " + i, canPlay = true, trackRankList = tracks }).ToList();
                cards.Add(new { targetType = "H5", id = 0, name = "畅销榜", toplistCode = "DIGITAL_ALBUM", canPlay = false, trackRankList = tracks });
                cards.Add(new { targetType = "H5", id = 0, name = "歌曲指数", toplistCode = "SONG_SALES", canPlay = false,
                    frontTargetUrl = "https://music.163.com/st/album/m/album/rank?type=2", trackRankList = tracks });
                if (count == 7) cards.Add(new { targetType = "H5", id = 0, name = "有声书榜", toplistCode = "VOICE_BOOK_CHART", canPlay = false });
                using var f = new Fixture(req => req.RequestUri!.AbsolutePath.Contains("position/show", StringComparison.Ordinal)
                    ? Directory() : Json(J(new { code = 200, data = new[] {
                        new { name = "推荐榜", categoryCode = "TOPPING", frontDisplayType = "ONLY_COVER_SMALL", list = cards.Take(2).ToArray() },
                        new { name = "官方榜", categoryCode = "OFFICIAL", frontDisplayType = "TOP_3", list = cards.ToArray() }
                    } })));
                var reply = await f.Catalog.ChannelAsync(new("wy", "chart"));
                var sections = MusicCatalogDto.Read(reply, "wy", "channel", "");
                True(sections[0].IsRankingCoverSection && sections[1].IsRankingPreviewSection);
                Equal(count, sections[1].Items.Count);
                var album = sections[1].Items[4]; var sales = sections[1].Items[5];
                True(album.CanOpen && !album.CanPlay && !album.CanRequestPreview);
                Equal("chart", album.Kind); Equal("albums", album.ActionId); Equal("song-sales", sales.ActionId);
                Equal("https://art.example.test/1.jpg", album.FirstPreviewCover);
                Equal("https://art.example.test/2.jpg", album.SecondPreviewCover);
                Equal("https://art.example.test/3.jpg", album.ThirdPreviewCover);
                if (count == 7) True(sections[1].Items[6].Display && !sections[1].Items[6].CanOpen);
                Equal(2, f.Calls); // Directory and ranking summaries only, never members.
            }
        });
        foreach (string kind in new[] { "albums", "singles" })
        foreach (var (period, path) in new[] { ("daily", "daily/type"), ("weekly", "week/type"),
            ("yearly", "year/type/"), ("overall", "total/type") })
            yield return Case("native sales chart preserves album identity " + kind + " " + period, async () =>
            {
                using var f = new Fixture(req => {
                    Weapi(req, "/weapi/feealbum/songsaleboard/" + path);
                    return Json(J(new { code = 200, products = new[] { new { albumId = 156507145, albumName = "合成专辑",
                        artistName = "艺人", coverUrl = "https://art.example.test/album.jpg", rank = 0, saleNum = 350 } } }));
                });
                var reply = await f.Catalog.ChartAsync(new("wy", kind, period, period == "yearly" ? 2024 : null));
                var section = MusicCatalogDto.Read(reply, "wy", "chart", "").Single();
                True(section.IsAlbumChart); var card = section.Items.Single();
                Equal("album", card.Kind); Equal("156507145", card.ActionId); Equal("1", card.RankText); Equal("350 张", card.SalesText);
                True(card.CanOpen && card.CanPlay); Equal(1, f.Calls);
            });
        yield return Case("song sales and user recommendations use distinct endpoints and preserve complete song identity", async () =>
        {
            using var f = new Fixture(req => {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/weapi/single/sales-ranking/get")
                    return Json(J(new { code = 200, data = new { itemList = new[] { new { songId = 2 }, new { songId = 1 } } } }));
                if (path == "/weapi/v3/song/detail")
                    return Json(J(new { code = 200, songs = new[] { Wy("1"), Wy("2") } }));
                Weapi(req, "/weapi/charts/user/rcmd/song/detail");
                return Json(J(new { code = 200, data = new { charts = new[] { new { songData = Wy("3") } } } }));
            });
            var sales = MusicCatalogDto.Read(await f.Catalog.ChartAsync(new("wy", "song-sales")), "wy", "chart", "").Single();
            True(sales.Items.Select(card => card.ActionId).SequenceEqual(["2", "1"]));
            var recommendations = MusicCatalogDto.Read(await f.Catalog.ChartAsync(new("wy", "user-recommendations")), "wy", "chart", "").Single();
            Equal("3", recommendations.Items.Single().ActionId); Equal(3, f.Calls);
            await Error("INVALID_REQUEST", () => f.Catalog.ChartAsync(new("wy", "arbitrary-url")));
            await Error("INVALID_REQUEST", () => f.Catalog.ChartAsync(new("wy", "song-sales", "weekly")));
            Equal(3, f.Calls);
        });
        yield return Case("song resource retains explicit complete playlist source without polluting the song contract", async () =>
        {
            using var f = new Fixture(_ => Home(false, "1", J(new { resourceType = "song",
                resourceExtInfo = new { songData = Wy("2") },
                playBtnData = JsonSerializer.Serialize(new { detailUrl = "orpheus://playlist/500", playAction = new { songIds = new[] { 1, 2, 3 } } }) })));
            var reply = await f.Catalog.HomeAsync(new("wy"));
            var card = MusicCatalogDto.Read(reply, "wy", "home", "").Single().Items.Single();
            Equal("2", card.ActionId); Equal("500", card.PlaybackSource!.Value.GetProperty("id").GetString());
            True(!card.Song!.Raw.TryGetProperty("playbackSource", out _)); Equal(1, f.Calls);
        });
    }
}
