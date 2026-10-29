// SPDX-License-Identifier: GPL-3.0-only
// Source-verified request recipes: existing providers + fixed upstream pins; see LICENSES/NOTICE.md.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    public Task<JsonElement> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null);
        var p = request!;
        PJ.PageInput(p.Page, p.Limit);
        PJ.Require(Enum.IsDefined(p.Kind));
        p = p with { Keyword = PJ.Input(p.Keyword).Trim() };
        PJ.Require(p.Keyword.Length > 0);
        return RunAsync(p.Platform, "search", p, p.Refresh, c => Search(c, p), cancellationToken);
    }
    private static async Task<JsonObject> Search(Call c, MusicSearchRequest p)
    {
        var songs = p.Kind == MusicSearchKind.Songs;
        if (c.Platform == "wy")
        {
            var data = await c.Wy("/api/cloudsearch/pc", PJ.Obj(("s", p.Keyword), ("type", songs ? 1 : 1000),
                ("limit", p.Limit), ("offset", (p.Page - 1) * p.Limit), ("total", true)));
            var total = PJ.Count(PJ.At(data, "result", songs ? "songCount" : "playlistCount"));
            var items = PJ.Rows(PJ.At(data, "result", songs ? "songs" : "playlists") ?? (total == 0 ? new JsonArray() : null), p.Limit)
                .Select(r => songs ? PlatformSongs.Wy(r) : PlatformSongs.Playlist("wy", r));
            return PJ.Page("wy", items, p.Page, p.Limit, total, p.Page * (long)p.Limit < total);
        }
        if (c.Platform == "tx")
        {
            var data = await c.Tx("music.search.SearchCgiService", "DoSearchForQQMusicMobile",
                PJ.Obj(("searchid", new System.Numerics.BigInteger(RandomNumberGenerator.GetBytes(8), true).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("query", p.Keyword), ("search_type", songs ? 0 : 3), ("num_per_page", p.Limit), ("page_num", p.Page),
                    ("highlight", 0), ("grp", 1), ("selectors", new JsonObject()), ("vec_selectors", new JsonArray())));
            var total = PJ.Count(PJ.At(data, "meta", "sum"));
            PJ.Require(int.TryParse(PJ.Scalar(PJ.At(data, "meta", "nextpage")), out var next) && (next == -1 || next > p.Page), "PAGINATION_STALLED");
            var items = PJ.Rows(PJ.At(data, "body", songs ? "item_song" : "item_songlist") ?? (total == 0 ? new JsonArray() : null), p.Limit)
                .Select(r => songs ? PlatformSongs.Tx(r) : PlatformSongs.Playlist("tx", r));
            return PJ.Page("tx", items, p.Page, p.Limit, total, next != -1, next);
        }
        var args = PJ.Obj(("keyword", p.Keyword), ("page", p.Page), ("pagesize", p.Limit),
            ("platform", "AndroidFilter"), ("iscorrection", 1), ("area_code", 1));
        if (songs) { args["privilegefilter"] = 0; args["dopicfull"] = 1; }
        else { args["albumhide"] = 0; args["nocollect"] = 0; }
        var kg = await c.Kg(songs ? "/v2/search/song" : "/v1/search/special", args, "complexsearch.kugou.com");
        var kgTotal = PJ.Count(PJ.At(kg, "data", "total"));
        var kgRows = ExactPage(PJ.First(PJ.At(kg, "data", "lists"), songs ? null : PJ.At(kg, "data", "info")), p.Page, p.Limit, kgTotal);
        return PJ.Page("kg", kgRows.Select(r => songs ? PlatformSongs.Kg(r) : PlatformSongs.Playlist("kg", r)),
            p.Page, p.Limit, kgTotal, p.Page * (long)p.Limit < kgTotal);
    }
    public Task<JsonElement> SongAsync(MusicSongRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null);
        var p = request!;
        PJ.Input(p.Id);
        return RunAsync(p.Platform, "song", p, p.Refresh, async c =>
        {
            if (c.Platform == "wy") return (await WyDetails(c, [PJ.IdInput(p.Id)]))[0];
            if (c.Platform == "tx")
            {
                PJ.IdInput(p.Id, "^[A-Za-z0-9]{1,64}$");
                var data = await c.Tx("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo",
                    PJ.Obj(("ctx", 0), ("client", 1), ("types", new[] { 0 }), ("modify_stamp", new[] { 0 }), ("mids", new[] { p.Id })));
                var rows = PJ.Rows(data["tracks"], 1);
                PJ.Require(rows.Count == 1 && PJ.S(rows[0], "mid") == p.Id, "IDENTITY_MISMATCH");
                return PlatformSongs.Tx(rows[0]);
            }
            var id = PlatformSongs.KgHash(JsonValue.Create(p.Id), "INVALID_REQUEST");
            var cookies = PJ.Cookies(c.Session); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var body = PJ.Obj(("appid", 1005), ("clientver", 20489), ("clienttime", now),
                ("data", PJ.Arr([PJ.Obj(("hash", id), ("audio_id", 0))])), ("dfid", PJ.Cookie(cookies, "dfid", "-")),
                ("key", PlatformProtocols.Md5($"1005{PlatformProtocols.KgSalt}20489{now}")), ("mid", PJ.Cookie(cookies, "KUGOU_API_MID", guestMid)));
            if (cookies.TryGetValue("token", out var token)) body["token"] = token;
            if (cookies.TryGetValue("userid", out var uid) && uid != "0") body["userid"] = uid;
            var dataKg = await c.Kg("/v1/audio/audio", new(), "kmr.service.kugou.com", body);
            var tracks = PJ.Rows(dataKg["data"], 1);
            PJ.Require(tracks.Count == 1, "IDENTITY_MISMATCH");
            var declared = new[] { tracks[0]["hash"], tracks[0]["FileHash"], PJ.At(tracks[0], "audio_info", "hash") }.Where(v => v is not null).ToArray();
            PJ.Require(declared.Length > 0 && declared.All(v => PlatformSongs.KgHash(v) == id), "IDENTITY_MISMATCH");
            var song = PlatformSongs.Kg(tracks[0]);
            PJ.Require(NativeMusicSong.NativeId(song) == id, "IDENTITY_MISMATCH");
            return song;
        }, cancellationToken);
    }
    private static async Task<List<JsonObject>> WyDetails(Call c, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return [];
        PJ.Require(ids.Count <= 100, "LIMIT");
        foreach (var id in ids) PJ.IdInput(id);
        var data = await c.Wy("/api/v3/song/detail", PJ.Obj(("c", "[" + string.Join(",", ids.Select(id => "{\"id\":" + id + "}")) + "]")), true);
        var songs = PJ.Rows(data["songs"], 100).Select(PlatformSongs.Wy).ToList();
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var song in songs) PJ.Require(map.TryAdd(NativeMusicSong.NativeId(song), song), "IDENTITY_MISMATCH");
        PJ.Require(map.Count == ids.Count && ids.All(map.ContainsKey), "IDENTITY_MISMATCH");
        return ids.Select(id => map[id]).ToList();
    }
    public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null);
        var p = request!;
        PJ.Input(p.Id);
        if (p.Personal is { } locator)
        {
            PJ.Require(Enum.IsDefined(locator.Kind) && Regex.IsMatch(PJ.Input(locator.Scope, 64), "^[a-f0-9]{64}$"));
            PJ.IdInput(locator.ListId);
            PJ.Require(locator.Total is null or >= 0 and <= 10000);
            if (locator.Version is not null) PJ.Id(JsonValue.Create(locator.Version), "^[0-9]{1,20}$", "INVALID_REQUEST");
            if (locator.Name is not null) PJ.Require(locator.Name.Length <= 4096);
            PJ.Require(p.Page >= 1 && p.Page <= (locator.Kind == MusicPersonalPlaylistKind.KugouOwned ? 334 : 100) &&
                p.Limit >= 1 && p.Limit <= (locator.Kind == MusicPersonalPlaylistKind.KugouOwned ? 30 : 100));
        }
        else PJ.PageInput(p.Page, p.Limit);
        if (p.Snapshot is not null) PJ.Require(Regex.IsMatch(p.Snapshot, "^[a-f0-9]{64}$"));
        return RunAsync(p.Platform, "playlist", p, p.Refresh,
            c => p.Personal is null ? Playlist(c, p) : PersonalPlaylist(c, p), cancellationToken);
    }
    private static async Task<JsonObject> Playlist(Call c, MusicPlaylistRequest p)
    {
        JsonObject page, info;
        string snapshot;
        if (c.Platform == "wy")
        {
            var id = PJ.IdInput(p.Id);
            // Membership is read from trackIds; the selected page is fetched below.
            // Do not download a second, unused embedded copy of every song.
            var data = await c.Wy("/api/v6/playlist/detail", PJ.Obj(("id", id), ("n", 0), ("s", 8)));
            var list = data["playlist"];
            PJ.Require(list is JsonObject && PJ.Id(PJ.At(list, "id")) == id, "IDENTITY_MISMATCH");
            var total = PJ.Count(PJ.At(list, "trackCount"));
            PJ.Require(total <= 10000, "LIMIT");
            var ids = PJ.Rows(PJ.At(list, "trackIds") ?? (PJ.At(list, "tracks") is JsonArray tr && tr.Count == total ? tr : total == 0 ? new JsonArray() : null), 10000)
                .Select(t => PJ.Id(t["id"])).ToArray();
            PJ.Require(ids.LongLength == total && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "INCOMPLETE_PLAYLIST");
            snapshot = PJ.Hash(PJ.Node(new object?[] { c.Scope, id, ids, PJ.At(list, "updateTime"), PJ.At(list, "trackUpdateTime") }));
            CheckSnapshot(p.Snapshot, snapshot);
            var songs = await WyDetails(c, ids.Skip((p.Page - 1) * p.Limit).Take(p.Limit).ToArray());
            page = PJ.Page("wy", songs, p.Page, p.Limit, total, p.Page * (long)p.Limit < total);
            info = PlatformSongs.Playlist("wy", list!);
            page["snapshotStrength"] = "ordered-membership";
        }
        else if (c.Platform == "tx")
        {
            var id = PJ.IdInput(p.Id);
            var data = await c.Tx("music.srfDissInfo.DissInfo", "CgiGetDiss",
                PJ.Obj(("disstid", JsonNode.Parse(id)), ("dirid", 0), ("tag", 1), ("song_begin", (p.Page - 1) * p.Limit),
                    ("song_num", p.Limit), ("userinfo", 1), ("orderlist", 1), ("onlysonglist", 0)));
            var total = PJ.Count(data["total_song_num"]);
            var more = PJ.Count(data["hasmore"]); PJ.Require(more is 0 or 1, "INVALID_RESPONSE");
            info = PlatformSongs.Playlist("tx", data["dirinfo"] ?? new JsonObject());
            PJ.Require(PJ.S(info, "id") == id, "IDENTITY_MISMATCH");
            var songs = ExactPage(data["songlist"] ?? (total == 0 ? new JsonArray() : null), p.Page, p.Limit, total)
                .Select(PlatformSongs.Tx).ToArray();
            PJ.Require(PJ.Count(data["songlist_size"]) == songs.Length, "INVALID_RESPONSE");
            snapshot = PJ.Hash(PJ.Node(new object?[] { c.Scope, id, total, PJ.S(info, "name"), PJ.At(data, "dirinfo", "mtime") }));
            CheckSnapshot(p.Snapshot, snapshot);
            page = PJ.Page("tx", songs, p.Page, p.Limit, total, more == 1);
            page["snapshotStrength"] = "metadata-only";
        }
        else
        {
            var id = PJ.IdInput(p.Id, @"^[\w-]{1,200}$");
            PJ.Require(!id.StartsWith("id_", StringComparison.Ordinal));
            var data = (await c.Kg("/pubsongs/v2/get_other_list_file_nofilt",
                PJ.Obj(("area_code", 1), ("begin_idx", (p.Page - 1) * p.Limit), ("plat", 1), ("type", 1), ("mode", 1),
                    ("personal_switch", 1), ("extend_fields", "abtags,hot_cmt,popularization"), ("pagesize", p.Limit), ("global_collection_id", id))))["data"];
            var list = PJ.At(data, "list_info");
            PJ.Require(list is JsonObject && (PJ.At(list, "global_collection_id") is null || PJ.S(list, "global_collection_id") == id), "IDENTITY_MISMATCH");
            var total = PJ.Count(PJ.First(PJ.At(list, "count"), PJ.At(data, "count")));
            var songs = ExactPage(PJ.First(PJ.At(data, "songs"), PJ.At(data, "info")) ?? (total == 0 ? new JsonArray() : null),
                p.Page, p.Limit, total).Select(PlatformSongs.Kg);
            var cardInput = (JsonObject)list!.DeepClone(); cardInput["global_collection_id"] = id;
            info = PlatformSongs.Playlist("kg", cardInput);
            snapshot = PJ.Hash(PJ.Node(new object?[] { c.Scope, id, total, PJ.S(info, "name"), PJ.At(list, "version") }));
            CheckSnapshot(p.Snapshot, snapshot);
            page = PJ.Page("kg", songs, p.Page, p.Limit, total, p.Page * (long)p.Limit < total);
            page["snapshotStrength"] = "metadata-only";
        }
        page["id"] = p.Id; page["name"] = info["name"]?.DeepClone(); page["cover"] = info["cover"]?.DeepClone();
        page["snapshot"] = snapshot;
        return page;
    }
    private static void CheckSnapshot(string? expected, string actual)
    {
        if (expected is not null) PJ.Require(expected == actual, "SNAPSHOT_CHANGED");
    }
    private static List<JsonObject> ExactPage(JsonNode? input, int page, int limit, long total)
    {
        var values = PJ.Rows(input, limit);
        PJ.Require(values.Count == Math.Min(limit, Math.Max(0, total - (page - 1L) * limit)), "INCOMPLETE_PAGE");
        return values;
    }
    public Task<JsonElement> AlbumAsync(MusicAlbumRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request!; PJ.IdInput(p.Id);
        return RunAsync(p.Platform, "album", p, p.Refresh, async c =>
        {
            PJ.Require(c.Platform == "wy", "UNSUPPORTED");
            var data = await c.Wy("/api/v1/album/" + p.Id, new(), true);
            var album = data["album"];
            PJ.Require(album is JsonObject && PJ.Id(PJ.At(album, "id")) == p.Id, "IDENTITY_MISMATCH");
            var songs = PJ.Rows(data["songs"], 1000).Select(PlatformSongs.Wy).ToArray();
            if (PJ.At(album, "size") is { } count) PJ.Require(PJ.Count(count) == songs.Length, "INCOMPLETE_PLAYLIST");
            return PJ.Obj(("platform", "wy"), ("id", p.Id), ("name", PJ.Metadata(PJ.At(album, "name"))),
                ("cover", PJ.Image(PJ.At(album, "picUrl"))), ("items", PJ.Arr(songs)), ("count", songs.Length),
                ("total", songs.Length), ("more", false), ("continuation", null), ("source", "wy"), ("engine", "NeteaseAlbum"));
        }, cancellationToken);
    }
}
