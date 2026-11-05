// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public sealed record MusicChartRequest(string Platform, string Kind, string Period = "daily",
    int? Year = null, bool Refresh = false);

public interface IMusicChartCatalog
{
    Task<JsonElement> ChartAsync(MusicChartRequest request, CancellationToken cancellationToken = default);
}

// Known music charts have typed native destinations, not an executable H5 URL.
public sealed partial class MusicPlatformCatalog : IMusicChartCatalog
{
    public Task<JsonElement> ChartAsync(MusicChartRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null);
        var p = request!;
        PJ.Require(p.Platform == "wy", "UNSUPPORTED");
        PJ.Require(p.Kind is "albums" or "singles" or "song-sales" or "user-recommendations");
        PJ.Require(p.Period is "daily" or "weekly" or "yearly" or "overall");
        PJ.Require(p.Year is null || p.Year >= 1 && p.Year <= DateTime.UtcNow.Year);
        PJ.Require(p.Kind is "albums" or "singles" || p.Period == "daily" && p.Year is null);
        return RunAsync(p.Platform, "chart", p, p.Refresh, async c =>
        {
            JsonObject[] items;
            string title;
            if (p.Kind is "albums" or "singles")
            {
                var period = p.Period switch { "weekly" => "week", "yearly" => "year", "overall" => "total", _ => "daily" };
                var input = PJ.Obj(("albumType", p.Kind == "albums" ? 0 : 1), ("limit", 50));
                if (p.Period == "yearly") input["year"] = p.Year ?? DateTime.UtcNow.Year;
                items = await c.Wy("/api/feealbum/songsaleboard/" + period + "/type" + (p.Period == "yearly" ? "/" : ""), input, raw =>
                    PJ.Rows(raw["products"], 1000).Select((product, index) =>
                    {
                        var id = PJ.Id(product["albumId"]);
                        PJ.Require(id != "0", "INVALID_RESPONSE");
                        return PJ.Obj(("id", id), ("name", PJ.Metadata(product["albumName"])),
                            ("cover", PJ.Image(product["coverUrl"])),
                            ("subtitle", PJ.Metadata(product["artistName"])),
                            ("rankText", (index + 1).ToString(CultureInfo.InvariantCulture)),
                            ("salesText", PJ.OptionalCount(product["saleNum"]) is { } sales
                                ? sales.ToString("N0", CultureInfo.InvariantCulture) + " 张" : ""),
                            ("kind", "album"), ("action", PJ.Obj(("type", "album"), ("id", id))));
                    }).ToArray(), true);
                title = p.Kind == "albums" ? "数字专辑畅销榜" : "数字单曲榜";
            }
            else if (p.Kind == "song-sales")
            {
                var ids = await c.Wy("/api/single/sales-ranking/get", new(), raw =>
                    PJ.Rows(PJ.At(raw, "data", "itemList"), 1000)
                        .Select(row => PJ.Id(row["songId"])).ToArray(), true);
                var songs = new List<JsonObject>();
                foreach (var batch in ids.Chunk(100)) songs.AddRange(await WyDetails(c, batch));
                items = songs.ToArray();
                title = "歌曲畅销指数榜";
            }
            else
            {
                items = await c.Wy("/api/charts/user/rcmd/song/detail",
                    PJ.Obj(("chartCode", "USER_RCMD_SONG"), ("period", "")), raw =>
                        PJ.Rows(PJ.At(raw, "data", "charts"), 1000)
                            .Select(row => PlatformSongs.Wy(row["songData"]!)).ToArray(), true);
                title = "全民热推榜";
            }
            PJ.Require(items.Select(item => PJ.S(item, "id").Length > 0 ? PJ.S(item, "id") : NativeMusicSong.NativeId(item))
                .Distinct(StringComparer.Ordinal).Count() == items.Length, "IDENTITY_MISMATCH");
            return PJ.Obj(("platform", "wy"), ("name", title), ("chartKind", p.Kind),
                ("sections", PJ.Arr([PJ.Obj(("id", "chart:" + p.Kind), ("title", title),
                    ("type", p.Kind is "albums" or "singles" ? "albums" : "songs"),
                    ("layout", p.Kind is "albums" or "singles" ? "ranking" : "list"), ("items", PJ.Arr(items)))])),
                ("count", items.Length), ("total", items.Length), ("more", false), ("continuation", null));
        }, cancellationToken);
    }
}
