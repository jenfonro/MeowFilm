// SPDX-License-Identifier: GPL-3.0-only
// Fixed LX public metadata recipes, native adaptation. See LICENSES/NOTICE.md.
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    private const string KwUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";
    private static string PublicUrl(string endpoint, IEnumerable<KeyValuePair<string, string>> query) =>
        endpoint + (query.Any() ? "?" + string.Join("&", query.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) : "");
    private static string Dec(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static HttpRequestMessage KwRequest(string endpoint, Dictionary<string, string>? query = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PublicUrl(endpoint, query ?? []));
        request.Headers.UserAgent.ParseAdd(KwUserAgent);
        request.Headers.Referrer = new("https://www.kuwo.cn/");
        return request;
    }
    private static async Task<JsonObject> KwSearch(Call c, MusicSearchRequest p)
    {
        var songs = p.Kind == MusicSearchKind.Songs;
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["all"] = p.Keyword, ["pn"] = Dec(p.Page - 1), ["rn"] = Dec(p.Limit),
            ["rformat"] = "json", ["encoding"] = "utf8"
        };
        var recipe = songs
            ? new[] { ("client", "kt"), ("uid", "794762570"), ("ver", "kwplayer_ar_9.2.2.1"), ("vipver", "1"),
                ("show_copyright_off", "1"), ("newver", "1"), ("ft", "music"), ("cluster", "0"),
                ("strategy", "2012"), ("vermerge", "1"), ("mobi", "1"), ("issubtitle", "1") }
            : new[] { ("ver", "mbox"), ("vipver", "MUSIC_8.7.7.0_BCS37"), ("plat", "pc"),
                ("devid", "28156413"), ("ft", "playlist"), ("pay", "0"), ("needliveshow", "0") };
        foreach (var (key, value) in recipe) query.Add(key, value);
        var raw = PublicCatalogData.KuwoJson(await c.PublicRead(KwRequest("http://search.kuwo.cn/r.s", query)));
        var total = PJ.Count(raw["TOTAL"]);
        PJ.Require(!songs || total == 0 || PJ.Scalar(raw["SHOW"]) != "0", "INVALID_RESPONSE");
        var rows = ExactPage(raw["abslist"] ?? (total == 0 ? new JsonArray() : null), p.Page, p.Limit, total);
        return PJ.Page("kw", rows.Select(r => songs ? PlatformSongs.Kw(r) : PlatformSongs.PublicPlaylist("kw", r)),
            p.Page, p.Limit, total, p.Page * (long)p.Limit < total);
    }
    private static async Task<JsonObject> KwSong(Call c, MusicSongRequest p)
    {
        var id = PJ.IdInput(p.Id, PlatformSongs.PublicNumericId);
        var data = PJ.Parse(await c.PublicRead(KwRequest("https://m.kuwo.cn/newh5/singles/songinfoandlrc",
            new() { ["musicId"] = id })));
        JsonNode raw;
        if (PJ.Scalar(data["status"]) == "301" && PJ.S(data, "msg") == "音乐查询失败")
        {
            raw = PublicCatalogData.KuwoPage(await c.PublicRead(KwRequest("https://www.kuwo.cn/play_detail/" + id)), id);
        }
        else
        {
            PJ.Require(PJ.Scalar(data["status"]) == "200", "UPSTREAM_REJECTED");
            raw = PJ.At(data, "data", "songinfo") ?? throw new MusicPlatformException("INVALID_RESPONSE");
        }
        PJ.Require(PlatformSongs.KwId(raw) == id, "IDENTITY_MISMATCH");
        return PlatformSongs.Kw(raw, requireQuality: false);
    }
    private static async Task<JsonObject> KwPlaylist(Call c, MusicPlaylistRequest p)
    {
        var id = PJ.IdInput(p.Id, PlatformSongs.PublicNumericId);
        int offset = (p.Page - 1) * p.Limit, first = offset / 1000;
        var items = new List<JsonObject>();
        JsonObject? info = null;
        long total = 0;
        string? snapshot = null;
        for (int batch = first; ; batch++)
        {
            var raw = PublicCatalogData.KuwoJson(await c.PublicRead(KwRequest("http://nplserver.kuwo.cn/pl.svc", new()
            {
                ["op"] = "getlistinfo", ["pid"] = id, ["pn"] = Dec(batch), ["rn"] = "1000",
                ["encode"] = "utf8", ["keyset"] = "pl2012", ["identity"] = "kuwo",
                ["pcmp4"] = "1", ["vipver"] = "MUSIC_9.0.5.0_W1", ["newver"] = "1"
            })));
            PJ.Require(PJ.S(raw, "result") == "ok", "UPSTREAM_REJECTED");
            var card = PlatformSongs.PublicPlaylist("kw", raw, detail: true);
            PJ.Require(PJ.S(card, "id") == id, "IDENTITY_MISMATCH");
            var count = PJ.Count(raw["total"]);
            PJ.Require(count <= 10000, "LIMIT");
            CheckDeclaredPage(raw, "pn", batch); CheckDeclaredPage(raw, "rn", 1000);
            var revision = PJ.Hash(PJ.Node(new object?[] { c.Scope, id, count, PJ.S(card, "name"),
                PJ.Scalar(raw["uid"]), PJ.S(card, "creator") }));
            CheckSnapshot(p.Snapshot, revision); CheckSnapshot(snapshot, revision);
            snapshot ??= revision; info ??= card; total = count;
            var rows = ExactPage(raw["musiclist"] ?? (count == 0 ? new JsonArray() : null), batch + 1, 1000, count);
            int start = Math.Max(offset - batch * 1000, 0);
            var expected = (int)Math.Min(p.Limit, Math.Max(0, count - offset));
            items.AddRange(rows.Skip(start).Take(expected - items.Count).Select(r => PlatformSongs.Kw(r)));
            if (items.Count == expected) break;
            PJ.Require(batch < 9 && rows.Count == 1000, "INCOMPLETE_PAGE");
        }
        return PublicPlaylistPage(c, p, info!, items, total, snapshot!, offset);
    }
    private static void CheckDeclaredPage(JsonNode data, string field, int expected)
    {
        if (PJ.At(data, field) is { } declared)
            PJ.Require(PJ.Count(declared) == expected, "PAGINATION_STALLED");
    }
    private static JsonObject PublicPlaylistPage(Call c, MusicPlaylistRequest p, JsonObject info,
        List<JsonObject> items, long total, string snapshot, int offset)
    {
        var page = PJ.Page(c.Platform, items, p.Page, p.Limit, total, p.Page * (long)p.Limit < total);
        page["id"] = p.Id; page["name"] = info["name"]?.DeepClone(); page["cover"] = info["cover"]?.DeepClone();
        page["snapshot"] = snapshot; page["snapshotStrength"] = "metadata-only";
        // The server has no occurrence IDs. Keep ordered duplicate membership without
        // pretending these local identities are a strong upstream revision.
        page["memberIds"] = PJ.Arr(items.Select((s, i) => JsonValue.Create(c.Platform + ":" + p.Id + ":" +
            Dec(offset + i) + ":" + NativeMusicSong.NativeId(s) +
            (c.Platform == "mg" ? ":" + PJ.S(s, "source_data", "copyrightId") : ""))));
        return page;
    }
}
