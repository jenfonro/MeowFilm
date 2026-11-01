// SPDX-License-Identifier: GPL-3.0-only
// Fixed LX Migu public catalog, native adaptation. See LICENSES/NOTICE.md.
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    private const string MgMobileAgent = "Mozilla/5.0 (Linux; U; Android 11.0.0; zh-cn; MI 11 Build/OPR1.170623.032) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30";
    private const string MgPlaylistAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1";
    private const string MgSearchDevice = "963B7AA0D21511ED807EE5846EC87D20";
    internal static string MiguSearchSignature(string keyword, string timestamp) => Convert.ToHexStringLower(MD5.HashData(
        Encoding.UTF8.GetBytes(keyword + "6cdc72a439cef99a3418d2a78aa28c73" +
            "yyapp2d16148780a1dcc7408e06336b98cfd50" + MgSearchDevice + timestamp)));
    private static HttpRequestMessage MgRequest(string endpoint, Dictionary<string, string> query,
        bool playlist = false, Dictionary<string, string>? form = null)
    {
        var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, PublicUrl(endpoint, query));
        request.Headers.UserAgent.ParseAdd(playlist ? MgPlaylistAgent : MgMobileAgent);
        request.Headers.Referrer = new("https://m.music.migu.cn/");
        request.Headers.Add("channel", "0146921");
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        return request;
    }
    private static async Task<JsonObject> MgRead(Call c, HttpRequestMessage request)
    {
        var root = PJ.Parse(await c.PublicRead(request));
        var codes = new[] { root["code"], root["returnCode"] }.Where(v => v is not null).ToArray();
        PJ.Require(codes.Length > 0 && codes.All(v => PJ.Scalar(v) == "000000"), "UPSTREAM_REJECTED");
        if (root["data"] is null) return root;
        return root["data"] as JsonObject ?? throw new MusicPlatformException("INVALID_RESPONSE");
    }
    private static async Task<JsonObject> MgSearch(Call c, MusicSearchRequest p)
    {
        var songs = p.Kind == MusicSearchKind.Songs;
        const string songSwitch = "{\"song\":1,\"album\":0,\"singer\":0,\"tagSong\":1,\"mvSong\":0,\"bestShow\":1,\"songlist\":0,\"lyricSong\":0}";
        const string listSwitch = "{\"song\":0,\"album\":0,\"singer\":0,\"tagSong\":0,\"mvSong\":0,\"bestShow\":0,\"songlist\":1,\"lyricSong\":0}";
        var request = MgRequest("https://jadeite.migu.cn/music_search/v3/search/searchAll", new()
        {
            ["isCorrect"] = songs ? "0" : "1", ["isCopyright"] = "1", ["searchSwitch"] = songs ? songSwitch : listSwitch,
            ["pageSize"] = Dec(p.Limit), ["text"] = p.Keyword, ["pageNo"] = Dec(p.Page), ["sort"] = "0", ["sid"] = "USS"
        });
        var timestamp = Dec(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        request.Headers.Add("uiVersion", "A_music_3.6.1");
        request.Headers.Add("deviceId", MgSearchDevice);
        request.Headers.Add("timestamp", timestamp);
        request.Headers.Add("sign", MiguSearchSignature(p.Keyword, timestamp));
        var raw = await MgRead(c, request);
        var result = PJ.At(raw, songs ? "songResultData" : "songListResultData");
        var total = PJ.Count(PJ.At(result, "totalCount"));
        var more = p.Page * (long)p.Limit < total;
        if (!songs)
            return PJ.Page("mg", ExactPage(PJ.At(result, "result") ?? (total == 0 ? new JsonArray() : null),
                p.Page, p.Limit, total).Select(r => PlatformSongs.PublicPlaylist("mg", r)),
                p.Page, p.Limit, total, more);
        // totalCount counts groups, not their versions. Never truncate the expanded page to limit.
        var groups = PJ.At(result, "resultList") ?? (total == 0 ? new JsonArray() : null);
        PJ.Require(groups is JsonArray, "INVALID_RESPONSE");
        var array = (JsonArray)groups!;
        PJ.Require(array.Count <= p.Limit, "LIMIT");
        PJ.Require(array.Count == Math.Min(p.Limit, Math.Max(0, total - (p.Page - 1L) * p.Limit)), "INCOMPLETE_PAGE");
        var items = new List<JsonObject>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        int rawCount = 0;
        for (int index = 0; index < array.Count; index++)
        {
            PJ.Require(array[index] is JsonArray, "INVALID_RESPONSE");
            var rows = PJ.Rows(array[index], ((JsonArray)array[index]!).Count);
            PJ.Require(rows.Count > 0, "INCOMPLETE_PAGE");
            rawCount = checked(rawCount + rows.Count);
            foreach (var row in rows)
            {
                var song = PlatformSongs.Mg(row);
                if (!seen.Add(PJ.S(NativeMusicSong.SourceData(song), "copyrightId"))) continue;
                items.Add(PJ.Obj(("rank", (p.Page - 1L) * p.Limit + index + 1), ("song", song)));
            }
        }
        PJ.Require(!more || p.Page < int.MaxValue, "INVALID_RESPONSE");
        return PJ.Obj(("platform", "mg"), ("items", PJ.Arr(items)), ("page", p.Page), ("limit", p.Limit),
            ("total", total), ("totalUnit", "groups"), ("groupCount", array.Count), ("rawItemCount", rawCount),
            ("more", more), ("continuation", more ? PJ.Obj(("page", p.Page + 1)) : null));
    }
    private static async Task<JsonObject> MgSong(Call c, MusicSongRequest p)
    {
        var id = PJ.IdInput(p.Id, PlatformSongs.MiguNativeId);
        var data = await MgRead(c, MgRequest("https://c.musicapp.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do",
            new() { ["resourceType"] = "2" }, form: new() { ["resourceId"] = id }));
        var rows = PJ.Rows(data["resource"], 1);
        PJ.Require(rows.Count == 1 && PJ.Scalar(rows[0]["songId"]) == id, "IDENTITY_MISMATCH");
        return PlatformSongs.Mg(rows[0]);
    }
    private static async Task<JsonObject> MgPlaylist(Call c, MusicPlaylistRequest p)
    {
        var id = PJ.IdInput(p.Id, PlatformSongs.PublicNumericId);
        var metadata = await MgRead(c, MgRequest("https://c.musicapp.migu.cn/MIGUM3.0/resource/playlist/v2.0",
            new() { ["playlistId"] = id }, playlist: true));
        var info = PlatformSongs.PublicPlaylist("mg", metadata, detail: true);
        PJ.Require(PJ.S(info, "id") == id, "IDENTITY_MISMATCH");
        long total = PJ.Count(metadata["musicNum"]);
        PJ.Require(total <= 10000, "LIMIT");
        long offset = (p.Page - 1L) * p.Limit;
        int expected = (int)Math.Min(p.Limit, Math.Max(0, total - offset));
        var items = new List<JsonObject>();
        string? snapshot = null;
        for (long batch = offset / 50; ; batch++)
        {
            var raw = await MgRead(c, MgRequest("https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0", new()
            { ["playlistId"] = id, ["pageNo"] = Dec(batch + 1), ["pageSize"] = "50" }, playlist: true));
            PJ.Require(PJ.Count(raw["totalCount"]) == total, "SNAPSHOT_CHANGED");
            if (raw["playlistId"] is { } declared)
                PJ.Require(PJ.Scalar(declared) == id, "IDENTITY_MISMATCH");
            CheckDeclaredPage(raw, "pageNo", batch + 1); CheckDeclaredPage(raw, "pageSize", 50);
            var revision = PJ.Hash(PJ.Node(new object?[] { c.Scope, id, total, PJ.S(info, "name"),
                PJ.Scalar(metadata["ownerId"]), PJ.S(info, "creator"), PJ.Scalar(raw["publishTime"]) }));
            CheckSnapshot(p.Snapshot, revision); CheckSnapshot(snapshot, revision); snapshot ??= revision;
            var rows = ExactPage(raw["songList"] ?? (total == 0 ? new JsonArray() : null), batch + 1, 50, total);
            int start = checked((int)Math.Max(offset - batch * 50, 0));
            items.AddRange(rows.Skip(start).Take(expected - items.Count).Select(PlatformSongs.Mg));
            if (items.Count == expected) break;
            PJ.Require(batch < 199 && rows.Count == 50, "INCOMPLETE_PAGE");
        }
        return PublicPlaylistPage(c, p, info, items, total, snapshot!, offset);
    }
}
