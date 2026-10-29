// SPDX-License-Identifier: GPL-3.0-only
// Fixed qq-library.ts/kugou.ts + pinned user.get_fav_song / playlist_track_all_new recipes.
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    private static JsonObject PrivateLocator(Call c, string kind, string listId, long? total, string? version, string? name)
        => PJ.Obj(("scope", c.Scope), ("kind", kind), ("listId", listId), ("total", total), ("version", version),
            ("name", name ?? ""), ("pageSize", kind == "kugou-owned" ? 30 : 100));

    private static async Task<JsonObject> PersonalPlaylist(Call c, MusicPlaylistRequest request)
    {
        var p = request; var locator = p.Personal!;
        c.Auth();
        PJ.Require(locator.Scope == c.Scope, "STALE_SESSION");
        JsonObject page;
        if (locator.Kind == MusicPersonalPlaylistKind.QqFavorites)
        {
            PJ.Require(c.Platform == "tx" && p.Id == "favorites" && locator.ListId == "201");
            var cookies = PJ.Cookies(c.Session);
            var euin = PJ.Cookie(cookies, "encrypt_uin", PJ.Cookie(cookies, "encryptUin"));
            PJ.Require(euin.Length > 0 && (!cookies.TryGetValue("encryptUin", out var other) || other == euin), "INVALID_CREDENTIALS");
            var data = await c.Tx("music.srfDissInfo.DissInfo", "CgiGetDiss", PJ.Obj(
                ("disstid", 0), ("dirid", 201), ("tag", true), ("song_begin", (p.Page - 1) * p.Limit),
                ("song_num", p.Limit), ("userinfo", true), ("orderlist", true), ("enc_host_uin", euin)));
            var info = data["dirinfo"];
            PJ.Require(info is JsonObject && PJ.Id(PJ.At(info, "creator", "musicid")) == c.Session!.UserId &&
                PJ.Scalar(PJ.At(info, "dirid")) == "201", "IDENTITY_MISMATCH");
            var total = PJ.Count(data["total_song_num"]);
            PJ.Require(total <= 10000, "PAGINATION_LIMIT");
            var more = PJ.Count(data["hasmore"]); PJ.Require(more is 0 or 1, "INVALID_RESPONSE");
            var rows = ExactPage(data["songlist"] ?? (total == 0 ? new JsonArray() : null), p.Page, p.Limit, total);
            PJ.Require(PJ.Count(data["songlist_size"]) == rows.Count, "INCOMPLETE_PAGE");
            var songs = rows.Select(PlatformSongs.Tx).ToArray();
            var members = songs.Select(NativeMusicSong.NativeId).ToArray();
            PJ.Require(members.Distinct(StringComparer.Ordinal).Count() == members.Length, "PAGINATION_STALLED");
            var snapshot = PJ.Hash(PJ.Node(new object?[] { c.Scope, "favorites", total, PJ.At(info, "mtime"), PJ.At(info, "dirid") }));
            CheckSnapshot(p.Snapshot, snapshot);
            page = PJ.Page("tx", songs, p.Page, p.Limit, total, more == 1);
            page["name"] = PJ.Metadata(PJ.First(PJ.At(info, "title"), JsonValue.Create("我喜欢")));
            page["cover"] = PJ.Image(PJ.First(PJ.At(info, "picurl"), PJ.At(info, "cover")));
            page["snapshot"] = snapshot; page["memberIds"] = PJ.Node(members);
        }
        else
        {
            PJ.Require(c.Platform == "kg" && p.Id == "0:" + locator.ListId);
            var cookies = PJ.Cookies(c.Session); var uid = c.Session!.UserId; var token = PJ.Cookie(cookies, "token");
            var response = await c.Kg("/v4/get_list_all_file_v3", new(), "cloudlist.service.kugou.com",
                PJ.Obj(("listid", locator.ListId), ("userid", uid), ("type", 0), ("page", p.Page), ("pagesize", p.Limit),
                    ("area_code", 1), ("allplatform", 1), ("show_cover", 1), ("token", token)));
            var data = response["data"] as JsonObject ?? response;
            var info = data["list_info"];
            foreach (var declared in new[] { data["listid"], PJ.At(info, "listid") }.Where(x => x is not null))
                PJ.Require(PJ.Id(declared) == locator.ListId, "IDENTITY_MISMATCH");
            if (PJ.At(info, "type") is { } type) PJ.Require(PJ.Count(type) == 0, "IDENTITY_MISMATCH");
            if (PJ.At(info, "list_create_userid") is { } owner) PJ.Require(PJ.Id(owner) == uid, "IDENTITY_MISMATCH");
            var count = PJ.First(data["count"], data["total"], PJ.At(info, "count"), response["count"], response["total"]);
            var total = count is null ? locator.Total : PJ.Count(count);
            PJ.Require(total is not null, "INCOMPLETE_PAGE");
            PJ.Require(total <= 10000, "PAGINATION_LIMIT");
            if (locator.Total is not null) PJ.Require(total == locator.Total, "SNAPSHOT_CHANGED");
            var rawVersion = PJ.First(PJ.At(info, "list_ver"), data["list_ver"], response["list_ver"]);
            var version = rawVersion is null ? locator.Version : PJ.Id(rawVersion);
            if (locator.Version is not null) PJ.Require(version == locator.Version, "SNAPSHOT_CHANGED");
            var rows = ExactPage(PJ.First(data["info"], data["songs"], response["info"]) ?? (total == 0 ? new JsonArray() : null),
                p.Page, p.Limit, total!.Value);
            var members = rows.Select(row => PJ.Id(row["fileid"], "^[1-9][0-9]{0,19}$")).ToArray();
            PJ.Require(members.Distinct(StringComparer.Ordinal).Count() == members.Length, "PAGINATION_STALLED");
            var songs = rows.Select(PlatformSongs.Kg).ToArray();
            var snapshot = PJ.Hash(PJ.Node(new object?[] { c.Scope, p.Id, total, version }));
            CheckSnapshot(p.Snapshot, snapshot);
            page = PJ.Page("kg", songs, p.Page, p.Limit, total.Value, p.Page * (long)p.Limit < total, maximumPages: 334);
            page["name"] = PJ.Metadata(PJ.First(PJ.At(info, "name"), JsonValue.Create(locator.Name ?? "歌单")));
            page["cover"] = PJ.Image(PJ.First(PJ.At(info, "cover"), PJ.At(info, "imgurl")));
            page["snapshot"] = snapshot; page["memberIds"] = PJ.Node(members); page["version"] = version;
            // Global ordering needs the owner to assemble all pages; per-page sorting would corrupt that order.
            page["memberSorts"] = PJ.Node(rows.Select(row => row["sort"] is null ? (long?)null : PJ.Count(row["sort"])).ToArray());
        }
        page["id"] = p.Id; page["snapshotStrength"] = "metadata-only";
        return page;
    }
}
