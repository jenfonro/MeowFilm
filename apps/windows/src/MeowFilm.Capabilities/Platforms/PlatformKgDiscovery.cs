// SPDX-License-Identifier: GPL-3.0-only
// Fixed KuGouMusicApi discovery recipes; see LICENSES/NOTICE.md.
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal static class PlatformKgDiscovery
{
    internal static JsonObject Tags(JsonObject response)
    {
        var groups = new JsonArray();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var tagIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in PJ.RowsAll(response["data"]))
        {
            string id = PJ.Id(group["tag_id"]), name = PJ.Metadata(group["tag_name"]);
            PJ.Require(groupIds.Add(id) && name.Length > 0, "INVALID_RESPONSE");
            var tags = new JsonArray();
            foreach (var tag in PJ.RowsAll(group["son"]))
            {
                string tagId = PJ.Id(tag["tag_id"]), tagName = PJ.Metadata(tag["tag_name"]);
                PJ.Require(tagIds.Add(tagId) && tagId != "0" && tagName.Length > 0 &&
                    PJ.Scalar(tag["parent_id"]) == id, "IDENTITY_MISMATCH");
                tags.Add(PJ.Obj(("id", tagId), ("name", tagName), ("group", name)));
            }
            groups.Add(PJ.Obj(("id", id), ("name", name), ("tags", tags)));
        }
        return PJ.Obj(("groups", groups));
    }
    internal static JsonObject Descriptor(string id, string name, string placement = "primary", string? pageCode = null) =>
        PJ.Obj(("id", id), ("name", name), ("kind", "channel"), ("placement", placement),
            ("available", true), ("pageCode", pageCode ?? ""));

    internal static JsonObject Directory(JsonObject categories)
    {
        var items = new JsonArray(Descriptor("feature", "精选"), Descriptor("playlist", "歌单广场"),
            Descriptor("chart", "排行榜"), Descriptor("newsong", "新歌"));
        foreach (var group in PJ.RowsAll(categories["groups"]))
            if (PJ.Array(group["tags"]).Any())
                items.Add(Descriptor("category-" + PJ.S(group, "id"), PJ.S(group, "name"), "more"));
        return PJ.Obj(("platform", "kg"), ("items", items));
    }
    internal static JsonObject ChannelAction(string id, string? pageCode = null) =>
        PJ.Obj(("type", "channel"), ("channel", PJ.Obj(("id", id), ("pageCode", pageCode ?? ""))));

    internal static JsonObject Section(string id, string title, string type, IEnumerable<JsonObject> items,
        JsonObject? action = null, string layout = "rail") =>
        PJ.Obj(("id", id), ("title", title), ("type", type), ("layout", layout),
            ("items", PJ.Arr(items)), ("action", action));

    internal static (JsonObject[] Items, bool More) Playlists(JsonObject response)
    {
        PJ.Require(response["data"] is JsonObject, "INVALID_RESPONSE");
        var data = response["data"]!;
        long hasNext = PJ.Count(data["has_next"]);
        PJ.Require(hasNext is 0 or 1, "INVALID_RESPONSE");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = PJ.RowsAll(data["special_list"]).Select(raw =>
        {
            var card = PlatformSongs.Playlist("kg", raw);
            PJ.Require(PJ.S(card, "name").Length > 0 && ids.Add(PJ.S(card, "id")), "IDENTITY_MISMATCH");
            card["action"] = PJ.Obj(("type", "playlist"), ("id", PJ.S(card, "id")));
            if (raw["songs"] is JsonArray songs)
            {
                var preview = new JsonArray();
                foreach (var song in PJ.RowsAll(songs).Take(3))
                {
                    var normalized = PlatformSongs.Kg(song);
                    preview.Add(PJ.Obj(("title", PJ.S(normalized, "title")), ("artist", PJ.S(normalized, "artist")),
                        ("cover", PJ.S(normalized, "cover_url"))));
                }
                card["preview"] = preview;
            }
            return card;
        }).ToArray();
        PJ.Require(!hasNext.Equals(1L) || items.Length > 0, "PAGINATION_STALLED");
        return (items, hasNext == 1);
    }
    internal static JsonObject[] Ranks(JsonObject response)
    {
        var rows = PJ.RowsAll(PJ.At(response, "data", "info"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cards = new List<JsonObject>();
        void Add(JsonObject raw)
        {
            string id = PJ.Id(raw["rankid"]), cid = PJ.Id(raw["rank_cid"]);
            string name = PJ.Metadata(raw["rankname"]);
            PJ.Require(ids.Add(id) && name.Length > 0, "IDENTITY_MISMATCH");
            var preview = new JsonArray();
            if (raw["songinfo"] is JsonArray songs)
                foreach (var song in PJ.RowsAll(songs).Take(3))
                    preview.Add(PJ.Obj(("title", PJ.Metadata(song["name"])), ("artist", PJ.Metadata(song["author"])),
                        ("cover", PJ.Image(PJ.At(song, "trans_param", "union_cover"))),
                        ("rank", preview.Count + 1)));
            cards.Add(PJ.Obj(("id", "rank-" + id), ("name", name),
                ("cover", PJ.Image(PJ.First(raw["imgurl"], raw["img_cover"]))), ("source", "kg"),
                ("action", ChannelAction("rank-" + id, cid)), ("preview", preview),
                ("updateFrequency", PJ.Metadata(raw["update_frequency"]))));
        }
        foreach (var row in rows) Add(row);
        return cards.ToArray();
    }
    internal static (JsonObject[] Items, long Total) Songs(JsonObject response, long page, int limit, string? rankCid = null)
    {
        JsonNode? rows;
        JsonNode? total;
        if (rankCid is null) { rows = response["data"]; total = response["total"]; }
        else
        {
            rows = PJ.At(response, "data", "songlist");
            total = PJ.At(response, "data", "total");
            if (response["total"] is { } outer) PJ.Require(PJ.Count(outer) == PJ.Count(total), "IDENTITY_MISMATCH");
        }
        long count = PJ.Count(total);
        var songs = PJ.RowsAll(rows);
        // Position paging: every page holds exactly its share of the declared total.
        long expected = Math.Min(limit, Math.Max(0, count - (page - 1) * limit));
        PJ.Require(songs.Count <= expected, "INVALID_RESPONSE");
        PJ.Require(songs.Count == expected, "INCOMPLETE_PAGE");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var items = songs.Select(raw =>
        {
            if (rankCid is not null && raw["rank_cid"] is { } cid)
                PJ.Require(PJ.Scalar(cid) == rankCid, "IDENTITY_MISMATCH");
            var song = PlatformSongs.Kg(raw);
            PJ.Require(identities.Add(PJ.S(song, "songId")), "IDENTITY_MISMATCH");
            return song;
        }).ToArray();
        return (items, count);
    }
}
