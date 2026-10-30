// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 jenfonro and iWebPlus contributors; derived from iWebPlayer.
// Native data-only translation of fixed b900c259 discovery-normalize.js.
// No JS/DSL execution, URL navigation or generic endpoint dispatch. See LICENSES/NOTICE.md.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

internal static class PlatformDiscovery
{
    private static string S(JsonNode? n, params string[] path) => PJ.S(n, path);
    private static string Id(JsonNode? n) => Regex.IsMatch(PJ.Scalar(n), "^[0-9]{1,20}$") ? PJ.Scalar(n) : "";
    private static long? Rank(JsonNode? n, long min) => long.TryParse(PJ.Scalar(n), out var v) && v >= min ? v : null;
    private static JsonNode? A(JsonNode? n, params string[] path) => PJ.At(n, path);
    private static JsonNode? Pick(JsonNode? n, params string[] names) => PJ.First(names.Select(k => A(n, k)).ToArray());
    private static IEnumerable<JsonNode?> Children(JsonNode? n) => PJ.Array(A(n, "children"));
    private static string Title(JsonNode? n) => PJ.Metadata(PJ.First(Pick(n, "name", "title"), A(n, "uiElement", "mainTitle", "title")));
    private static string Cover(JsonNode? n) => PJ.Image(PJ.First(Pick(n, "cover", "coverImg", "coverUrl", "picUrl", "coverImgUrl", "background"),
        A(n, "uiElement", "image", "imageUrl"), PJ.Array(A(n, "uiElement", "images")).Select(x => Pick(x, "imageUrl", "url")).FirstOrDefault()));
    private static JsonNode? Nested(JsonNode? n)
    {
        if (n is JsonObject or JsonArray) return n;
        var s = PJ.Text(n);
        if (s.Length is 0 or >= 50000) return null;
        try { return JsonNode.Parse(s, documentOptions: new JsonDocumentOptions { MaxDepth = 25 }); }
        catch (JsonException) { return null; }
    }
    private static Dictionary<string, string[]> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0].Replace("+", " ", StringComparison.Ordinal)), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(p => Uri.UnescapeDataString((p.Length == 2 ? p[1] : "").Replace("+", " ", StringComparison.Ordinal))).ToArray(), StringComparer.Ordinal);
    internal static JsonObject? Action(JsonNode? value, int depth = 0)
    {
        if (depth > 8) return null;
        if (Nested(value) is JsonObject data)
        {
            if (S(data, "type") is "resource" or "play")
            {
                var p = A(data, "params"); var kind = S(p, "resourceType").ToLowerInvariant(); var id = Id(A(p, "query"));
                if (id.Length > 0 && kind is "playlist" or "song_list" or "list" or "song" or "album")
                    return PJ.Obj(("type", kind is "song_list" or "list" ? "playlist" : kind), ("id", id));
            }
            return Action(PJ.First(A(data, "clickAction"), Pick(data, "url", "targetUrl"), A(data, "urlAction", "url")), depth + 1);
        }
        var text = PJ.Text(value);
        if (text.Length is 0 or > 12000 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "orpheus" ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort) return null;
        var path = uri.Host + uri.AbsolutePath; var q = Query(uri);
        string Q(string key) => q.TryGetValue(key, out var values) && values.Length == 1 ? values[0] : "";
        var match = Regex.Match(path, @"^(playlist|song|album)/([0-9]{1,20})$");
        if (match.Success) return PJ.Obj(("type", match.Groups[1].Value), ("id", match.Groups[2].Value));
        if (path is "songrcmd" or "songrcmd/" or "nm/play/dailySong")
            return PJ.Obj(("type", "daily"), ("branch", (Q("tab").Length > 0 ? Q("tab") : Q("type")) == "style" ? "style" : "default"));
        if (path is "nm/luna/privatefm" or "privatefm" or "privatefm/") return PJ.Obj(("type", "fm"), ("mode", "DEFAULT"), ("submode", ""));
        if (path == "nm/play/similarFM")
        {
            if (Q("sourceType") != "song" || (q.TryGetValue("sourceName", out var names) && names.Length > 1)) return null;
            if (Nested(JsonValue.Create(Q("sourceId"))) is not JsonArray ids || ids.Count is 0 or > 20 ||
                ids.Any(n => !Regex.IsMatch(PJ.Scalar(n), "^[1-9][0-9]{0,19}$"))) return null;
            return PJ.Obj(("type", "similar"), ("sourceType", "song"), ("sourceIds", ids.Select(PJ.Scalar).ToArray()), ("sourceName", Q("sourceName")));
        }
        if (path is "rnpage" or "rnpage/")
        {
            var component = Q("component");
            if (component == "cube-renderer-rn" && Regex.IsMatch(Q("page"), "^[a-zA-Z0-9_-]{6,100}$"))
                return PJ.Obj(("type", "channel"), ("channel", PJ.Obj(("kind", "cube"), ("pageCode", Q("page")), ("id", Q("page")))));
            var kind = component switch { "rn-ranklist-homepage" => "rankings", "rn-playlist-square" or "rn-playlistsquare" => "playlists",
                "rn-vip-portal" => "vip", "rn-music-homepage" => "featured", _ => "" };
            if (kind.Length > 0) return PJ.Obj(("type", "channel"), ("channel", PJ.Obj(("kind", kind), ("id", kind))));
        }
        if (path == "nm/mainTab/select" && Q("tab") == "music")
        {
            var id = Q("musicTabCode").Length > 0 ? Q("musicTabCode") : Q("subTab");
            if (id.Length > 0) return PJ.Obj(("type", "channel"), ("channel", PJ.Obj(("id", id))));
        }
        return null;
    }
    private static JsonObject NativeSong(JsonNode raw, string resourceCover = "")
    {
        var s = PJ.First(Pick(raw, "songInfo", "songData", "song"), raw)!;
        var copy = s is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
        copy["id"] = Id(Pick(s, "id", "songId", "resourceId"));
        if (copy["name"] is null) copy["name"] = Title(s);
        if (copy["ar"] is null && copy["artists"] is null)
            copy["ar"] = PJ.Arr(Regex.Split(S(s, "artistName").Length > 0 ? S(s, "artistName") : S(s, "subTitle"), @"\s*[/、]\s*")
                .Where(x => x.Length > 0).Select(x => PJ.Obj(("name", x))));
        if (copy["al"] is null && copy["album"] is null) copy["al"] = PJ.Obj(("name", ""), ("picUrl", Cover(s)));
        if (Cover(s).Length == 0 && resourceCover.Length > 0) copy["coverUrl"] = resourceCover;
        return PlatformSongs.Wy(copy);
    }
    internal static JsonObject? Resource(JsonNode? raw, bool modern = false, bool banner = false, bool channel = false)
    {
        if (raw is not JsonObject r) return null;
        var ext = A(r, "resourceExtInfo");
        var kind = PJ.Text(Pick(r, "resourceType", "targetType", "type", "creativeType")).ToLowerInvariant();
        if (Regex.IsMatch(kind, "video|podcast|voice|djradio|djprogram|mv")) return null;
        var play = Nested(A(r, "playBtnData"));
        var primary = Pick(r, "action", "url", "orpheus", "clickAction");
        var click = Action(primary) ?? Action(A(play, "detailUrl"));
        var id = Id(Pick(r, "rankId", "resourceId", "id", "resource"));
        var result = PJ.Obj(("id", id), ("name", Title(r)), ("cover", Cover(r)),
            ("subtitle", PJ.Metadata(Pick(r, "subTitle", "subtitle"))),
            ("badge", modern ? PJ.Text(PJ.Array(A(r, "tags")).FirstOrDefault()) : ""), ("source", "wy"), ("engine", "Netease"));
        JsonObject Finish(string type, JsonObject action)
        { result["kind"] = type; result["action"] = action; return result; }
        JsonObject Unsupported() => Finish("unsupported", PJ.Obj(("type", "unsupported"), ("code", "UNSUPPORTED")));
        if (modern && S(r, "singleLineTitle").Length > 0)
        {
            var name = S(result, "name"); var subtitle = S(result, "subtitle");
            result["name"] = kind == "dailysongs" && A(r, "dayOfMonth") is not null ? Regex.Split(name, @"\s*\|\s*")[0] : subtitle.Length > 0 ? subtitle : name;
            result["subtitle"] = S(r, "singleLineTitle");
        }
        if (kind is "similarsong" or "similarartist" || S(click, "type") == "similar")
        {
            var declared = Action(primary ?? A(play, "detailUrl"));
            if (kind == "similarartist" || S(declared, "type") != "similar") return Unsupported();
            result["id"] = "similar:song:" + string.Join(",", PJ.Array(A(declared, "sourceIds")).Select(PJ.Scalar));
            return Finish("similar", declared!);
        }
        if (A(ext, "songData") is not null || A(ext, "song") is not null || kind == "song" || S(click, "type") == "song")
        {
            var songRaw = PJ.First(A(ext, "songData"), A(ext, "song")) as JsonObject ?? (JsonObject)r.DeepClone();
            if (A(ext, "songData") is null && A(ext, "song") is null) songRaw["id"] = S(click, "id").Length > 0 ? S(click, "id") : id;
            if (Id(Pick(songRaw, "id", "songId", "resourceId")).Length == 0) return null;
            var song = NativeSong(songRaw, Cover(r)); var nativeId = NativeMusicSong.NativeId(song);
            result["song"] = song; result["id"] = nativeId;
            return Finish("song", PJ.Obj(("type", "song"), ("id", nativeId)));
        }
        if (kind == "dailysongs" || S(click, "type") == "daily")
        {
            var day = Rank(A(r, "dayOfMonth"), 1);
            result["dayOfMonth"] = day is <= 31 ? PJ.Node(day) : null;
            var expandedTitle = Title(r);
            result["previewSubtitle"] = modern && expandedTitle != S(result, "name") ? expandedTitle : "";
            return Finish("collection", PJ.Obj(("type", "daily"), ("branch", S(click, "branch") == "style" ? "style" : "default")));
        }
        if (kind == "star" || S(r, "lunaItemType") == "heartRcmd") return Finish("collection", PJ.Obj(("type", "heartbeat")));
        if (kind == "fm" || S(click, "type") == "fm")
        {
            var mode = S(r, "mode"); var submode = PJ.Text(Pick(r, "subMode", "submode"));
            result["coverLabel"] = S(r, "fmLogoTextContent");
            if (mode.Length == 0 && submode.Length > 0) return Unsupported();
            if (mode.Length == 0 && (channel || PJ.Scalar(A(r, "ruleId")).Length > 0 || PJ.Scalar(A(r, "tabTagId")).Length > 0))
            {
                var rule = Id(A(r, "ruleId")); var tab = PJ.Scalar(A(r, "tabTagId"));
                if (PJ.Scalar(A(r, "ruleId")).Length > 0 ? rule.Length == 0 :
                    !Regex.IsMatch(tab, "^[1-9][0-9]{0,19}$") && !FmTabs.Contains(tab)) return Unsupported();
                return Finish("fm", PJ.Obj(("type", "fm"), ("provider", "rule"), ("ruleId", rule),
                    ("tabTagId", rule.Length > 0 ? "" : tab), ("sourceName", S(result, "coverLabel").Length > 0 ? S(result, "coverLabel") : S(result, "name"))));
            }
            if (mode.Length > 0 && mode is not ("DEFAULT" or "FAMILIAR" or "EXPLORE" or "PUZZLE_MODE_RCMD" or "SCENE_RCMD")) return Unsupported();
            var action = PJ.Obj(("type", "fm"), ("mode", mode.Length > 0 ? mode : "DEFAULT"), ("submode", submode));
            if (channel) action["entry"] = "channel";
            return Finish("fm", action);
        }
        if (kind is "playlist" or "play_list" or "song_list" or "list" or "scroll_playlist" or "toplist" || S(click, "type") == "playlist")
        {
            var pid = S(click, "id").Length > 0 ? S(click, "id") : id;
            if (pid.Length == 0)
            {
                var declared = PJ.First(A(play, "playAction", "songIds"), A(r, "songIds"));
                if (declared is not JsonArray ids || ids.Count == 0) return null;
                if (ids.Count > 1000) return Unsupported();
                var precise = ids.Select(Id).Where(x => Regex.IsMatch(x, "^[1-9][0-9]*$")).Distinct(StringComparer.Ordinal).ToArray();
                if (precise.Length == 0) return null;
                result["id"] = "songs:" + S(r, "resourceId")[..Math.Min(100, S(r, "resourceId").Length)] + ":" + PlatformSongs.IdentityHash(string.Join(",", precise))[..8].TrimStart('0');
                result["engine"] = "NeteaseCollection"; result["collectionType"] = "songs"; result["count"] = precise.Length;
                return Finish(kind == "toplist" ? "ranking" : "collection", PJ.Obj(("type", "song-collection"), ("ids", precise)));
            }
            if (S(r, "coverType") == "ALBUM_LOGO" && Uri.TryCreate(PJ.Text(primary ?? A(play, "detailUrl")), UriKind.Absolute, out var uri) &&
                uri.Scheme == "orpheus" && uri.Host == "playlist" && uri.AbsolutePath == "/" + pid && uri.UserInfo.Length == 0)
            {
                var q = Query(uri);
                if (q.TryGetValue("coverUrl", out var covers) && covers.Length == 1 && PJ.Image(JsonValue.Create(covers[0])) is { Length: > 0 } cover) result["cover"] = cover;
            }
            result["id"] = pid;
            result["count"] = PJ.Node(PJ.OptionalCount(PJ.First(Pick(r, "trackCount", "songCount"), A(ext, "songCount"))));
            result["creator"] = S(r, "creator", "nickname");
            result["preview"] = PJ.Arr(PJ.Array(Pick(r, "rankItems", "trackRankList")).Select(row => PJ.Obj(
                ("id", Id(Pick(row, "trackId", "songId"))), ("title", PJ.Text(Pick(row, "songName", "title"))),
                ("artist", PJ.Text(Pick(row, "artistName", "subTitle"))), ("cover", Cover(row)),
                ("rank", Rank(A(row, "rank"), 1)), ("lastRank", Rank(A(row, "lastRank"), 0)))));
            result["songCover"] = PJ.Image(A(r, "songCoverImgUrl")); result["updateFrequency"] = S(r, "updateFrequency");
            return Finish(kind == "toplist" ? "ranking" : "playlist", PJ.Obj(("type", "playlist"), ("id", pid)));
        }
        if (!banner && (kind is "album" or "digitalbum" or "digitalalbum" || S(click, "type") == "album"))
        {
            var aid = S(click, "id").Length > 0 ? S(click, "id") : id;
            if (aid.Length == 0) return null;
            result["id"] = aid; result["engine"] = "NeteaseAlbum";
            result["artist"] = PJ.Text(Pick(r, "artistName", "subtitle"));
            result["count"] = PJ.Node(PJ.OptionalCount(A(r, "songCount")));
            return Finish("album", PJ.Obj(("type", "album"), ("id", aid)));
        }
        if (S(click, "type") == "channel") { result["id"] = S(click, "channel", "id"); return Finish("channel", click!); }
        if (kind == "dragon_ball" && Action(PJ.First(A(r, "uiElement", "mainTitle", "action"), A(r, "action"))) is { } actionEntry)
            return Finish("entry", actionEntry);
        return null;
    }
    internal static readonly string[] FmTabs = ["rap", "electronic", "sleep", "chinese", "occident", "korea", "classical"];
    private static IEnumerable<JsonObject> Leaves(JsonNode? node, int depth = 0)
    {
        if (depth > 16) yield break;
        if (node is JsonObject o)
        {
            if (o["resourceType"] is not null || o["songInfo"] is not null || o["songData"] is not null ||
                A(o, "resourceExtInfo", "songData") is not null || o["targetType"] is not null || o["action"] is not null && o["resourceId"] is not null)
            { yield return o; yield break; }
            foreach (var p in o.Where(p => p.Key is not ("playBtn" or "playBtnData" or "playAll" or "playAction" or "clickAction" or "log" or "track" or "crossPlatformConfig")))
                foreach (var leaf in Leaves(p.Value, depth + 1)) yield return leaf;
        }
        else if (node is JsonArray a)
            foreach (var child in a) foreach (var leaf in Leaves(child, depth + 1)) yield return leaf;
    }
    private static IEnumerable<JsonNode?> Roots(JsonNode? dsl)
    {
        dsl = Nested(dsl);
        if (dsl is not JsonObject o) return [];
        if (o["blockResource"] is JsonObject block) return [block];
        if (A(o, "content", "items") is not null || o["resources"] is not null || o["items"] is not null) return [o];
        var candidates = o.Select(p => p.Value).OfType<JsonObject>().ToArray();
        var roots = candidates.Where(v => new[] { "blockResource", "header", "block", "resources", "items" }.Any(k => v[k] is not null) || A(v, "content", "items") is not null).ToArray();
        return roots.Length > 0 ? roots.Select(v => v["blockResource"] ?? v) : candidates;
    }
    private static string Heading(JsonNode? block, JsonNode? item) => PJ.True(A(block, "hideTitle")) ? ""
        : PJ.Text(PJ.First(A(item, "title"), A(item, "header", "title"), A(item, "block", "title"),
            A(block, "uiElement", "subTitle", "title"), A(block, "uiElement", "mainTitle", "title")));
    internal static JsonObject Page(JsonObject raw, string target, string cursor = "")
    {
        var data = raw["data"] as JsonObject ?? raw;
        var blocks = PJ.Rows(data["blocks"], 1000);
        var entries = new JsonArray(); var sections = new JsonArray(); var omitted = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var bi = 0; bi < blocks.Count; bi++)
        {
            var block = blocks[bi]; var code = PJ.Text(Pick(block, "positionCode", "blockCode", "code", "id"));
            if (code.Length == 0) code = "block-" + bi;
            var nth = occurrences.GetValueOrDefault(code); occurrences[code] = nth + 1;
            var discriminator = PJ.Scalar(Pick(block, "id", "channelCode"));
            if (discriminator.Length == 0) discriminator = cursor.Length > 0 ? cursor : "root";
            var baseId = $"{target}:{code}:{discriminator}:{nth}";
            var roots = block["dslData"] is not null ? Roots(block["dslData"]).ToArray() : [block];
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            for (var ri = 0; ri < roots.Length; ri++)
            {
                var r = roots[ri];
                var native = new List<JsonObject>();
                if (block["dslData"] is not null) native.AddRange(Leaves(r));
                else foreach (var creative in PJ.Array(block["creatives"]).OfType<JsonObject>())
                {
                    if (Regex.IsMatch(S(creative, "creativeType"), "video|voice|podcast", RegexOptions.IgnoreCase)) continue;
                    var resources = PJ.Array(creative["resources"]).OfType<JsonObject>().ToArray();
                    if (resources.Length == 0)
                    {
                        var copy = (JsonObject)creative.DeepClone(); copy["resourceId"] = creative["creativeId"]?.DeepClone(); native.Add(copy);
                    }
                    else foreach (var resource in resources)
                    {
                        var copy = (JsonObject)creative.DeepClone();
                        foreach (var p in resource) copy[p.Key] = p.Value?.DeepClone();
                        copy["creativeType"] = creative["creativeType"]?.DeepClone(); native.Add(copy);
                    }
                }
                var unique = new List<JsonObject>();
                foreach (var n in native)
                {
                    var resource = Resource(n, modern: block["dslData"] is not null);
                    if (resource is null) { if (S(n, "resourceType").Length > 0) omitted.Add(S(n, "resourceType")); continue; }
                    var identity = S(resource, "kind") + ":" + (S(resource, "id").Length > 0 ? S(resource, "id") : S(resource, "action", "type")) + ":" + S(resource, "action", "branch");
                    if (emitted.Add(identity)) unique.Add(resource);
                }
                var entrance = unique.Any(x => S(x, "action", "type") is "daily" or "heartbeat" or "fm" or "similar") ||
                    native.Any(x => S(x, "resourceType") == "dragon_ball") ||
                    (A(block, "dslData", "dslShowTitle") is { } show && !PJ.Bool(show) && !S(block, "showType").StartsWith("PLAYLIST_SQUARE_", StringComparison.Ordinal));
                if (entrance)
                {
                    for (var i = 0; i < unique.Count; i++)
                    {
                        var x = unique[i]; x["key"] = $"{baseId}:entry:{S(x, "kind")}:{(S(x, "id").Length > 0 ? S(x, "id") : i.ToString())}:{S(x, "action", "branch")}";
                        var entryKind = S(x, "action", "type") is { Length: > 0 } actionKind ? actionKind : S(x, "kind");
                        x["presentation"] = entryKind switch
                        {
                            "playlist" or "daily" or "album" => "preview",
                            "similar" => "overlay",
                            "fm" or "heartbeat" => "animated",
                            _ => "static"
                        };
                        entries.Add(x);
                    }
                    continue;
                }
                JsonObject? run = null;
                foreach (var x in unique)
                {
                    var type = S(x, "kind") == "song" ? "songs" : S(x, "kind") == "ranking" ? "rankings" : "playlists";
                    if (run is null || S(run, "type") != type)
                    {
                        run = PJ.Obj(("id", $"{baseId}:{ri}:{sections.Count(s => S(s, "id").StartsWith(baseId + ":", StringComparison.Ordinal))}"),
                            ("title", target == "wy:home" && code == "PAGE_RECOMMEND_MY_SHEET" && type == "playlists" ? "最近常听" : Heading(block, r)),
                            ("type", type), ("layout", Regex.IsMatch(S(block, "showType"), "MULTI.*GRID|FLAT|GRID", RegexOptions.IgnoreCase) ? "grid" : "rail"),
                            ("action", Action(PJ.First(A(r, "action"), A(r, "header", "action")))), ("items", new JsonArray()));
                        sections.Add(run);
                    }
                    ((JsonArray)run["items"]!).Add((type == "songs" ? x["song"]! : x).DeepClone());
                }
            }
        }
        var more = PJ.True(data["hasMore"]); var nextCursor = PJ.Scalar(data["cursor"]);
        if (more) PJ.Require(nextCursor.Length > 0 && nextCursor != cursor, "PAGINATION_STALLED");
        var positionCodes = PJ.Encode(PJ.Node(blocks.Select(b => S(b, "positionCode")).Where(x => x.Length > 0).ToArray()));
        var continuation = more ? PJ.Obj(("cursor", nextCursor), ("blockCodeOrderList", data["blockCodeOrderList"] ?? JsonValue.Create("")),
            ("loadedPositionCodes", positionCodes)) : null;
        return PJ.Obj(("id", target), ("entries", entries), ("sections", sections), ("more", more), ("omitted", omitted.ToArray()),
            ("continuation", continuation));
    }
    internal static JsonObject Directory(JsonObject raw)
    {
        var d = raw["data"] as JsonObject ?? raw; var scene = d["generalizedSceneShow"] ?? d;
        var rows = PJ.Rows(PJ.First(A(scene, "generalizedMap", "nowChannelItems"), d["nowChannelItems"], d["channels"]), 100);
        var items = new JsonArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var action = Action(Pick(r, "url", "action")); var code = S(r, "code");
            var kind = code switch { "chart" => "rankings", "playlist" => "playlists", "feature" => "featured",
                "artist" => "artists", "vip" => "vip", "newsong" => "new-songs", _ => S(action, "channel", "kind") };
            var name = Title(r); if (name.Length == 0) continue;
            var id = code.Length > 0 ? code : S(action, "channel", "id").Length > 0 ? S(action, "channel", "id") : "channel-" + items.Count;
            if (id == "music_top_tab_acg") id = "acg";
            if (id == "playlist") name = "歌单广场";
            PJ.Require(ids.Add(id), "INVALID_RESPONSE");
            var item = PJ.Obj(("id", id), ("name", name), ("kind", kind.Length > 0 ? kind : "unsupported"));
            if (kind == "cube" && S(action, "channel", "pageCode").Length > 0) item["pageCode"] = S(action, "channel", "pageCode");
            items.Add(item);
        }
        if (ids.Add("artist")) items.Add(PJ.Obj(("id", "artist"), ("name", "歌手"), ("kind", "artists")));
        return PJ.Obj(("platform", "wy"), ("items", items));
    }
    internal static JsonObject Rankings(JsonObject data, string target)
    {
        var groups = PJ.Rows(PJ.First(data["data"], data["list"]), 100);
        var sections = new JsonArray();
        foreach (var g in groups)
        {
            var items = PJ.Array(g["list"]).OfType<JsonObject>().Where(r => S(r, "targetType") == "PLAYLIST" && (r["canPlay"] is null || PJ.Bool(r["canPlay"])))
                .Select(r => { var copy = (JsonObject)r.DeepClone(); copy["resourceType"] = "playlist"; return Resource(copy); }).Where(r => r is not null).ToArray();
            if (items.Length == 0) continue;
            sections.Add(PJ.Obj(("id", target + ":" + (S(g, "categoryCode").Length > 0 ? S(g, "categoryCode") : sections.Count.ToString())),
                ("title", S(g, "name")), ("type", "rankings"), ("layout", PJ.Text(Pick(g, "frontDisplayType", "displayType")) is { Length: > 0 } l ? l : "ONLY_COVER"),
                ("items", PJ.Arr(items))));
        }
        return PJ.Obj(("id", target), ("entries", new JsonArray()), ("sections", sections), ("more", false), ("continuation", null));
    }
    internal static JsonObject? VipRecommendations(JsonObject recommendations, string target)
    {
        var data = recommendations["data"] as JsonObject ?? recommendations;
        var songs = PJ.Rows(PJ.First(data["songInfoList"], recommendations["songInfoList"]), 100)
            .Select(PlatformSongs.Wy).ToArray();
        return songs.Length == 0 ? null :
            PJ.Obj(("id", target + ":recommend"), ("title", S(data, "title") is { Length: > 0 } title ? title : "黑胶VIP 每日推荐"),
                ("type", "songs"), ("layout", "vip-songs"), ("items", PJ.Arr(songs)));
    }

    internal static JsonObject? VipRankings(JsonObject charts, string target)
    {
        var items = PJ.Array(Rankings(charts, target + ":source")["sections"])
            .SelectMany(section => PJ.Array(A(section, "items")))
            .OfType<JsonObject>()
            .Where(item => Regex.IsMatch(S(item, "name"), "^黑胶(?:SVIP|VIP)", RegexOptions.IgnoreCase))
            .Select(item => item.DeepClone()).ToArray();
        return items.Length == 0 ? null :
            PJ.Obj(("id", target + ":rankings"), ("title", "黑胶VIP 排行榜"),
                ("type", "rankings"), ("layout", "vip-ranked-songs"), ("items", PJ.Arr(items)));
    }

    internal static JsonObject Vip(JsonObject? recommendations, JsonObject? rankings, string target)
    {
        var sections = new JsonArray();
        if (recommendations is not null) sections.Add(recommendations);
        if (rankings is not null) sections.Add(rankings);
        return PJ.Obj(("id", target), ("entries", new JsonArray()), ("sections", sections),
            ("more", false), ("continuation", null));
    }
    internal static (int Area, int Type, int Initial, string Token) ArtistSelection(string? value)
    {
        var token = string.IsNullOrEmpty(value) ? "-1:-1:-1" : value;
        var parts = token.Split(':');
        PJ.Require(parts.Length == 3, "INVALID_REQUEST");
        var areaValid = int.TryParse(parts[0], out var area);
        var typeValid = int.TryParse(parts[1], out var type);
        var initialValid = int.TryParse(parts[2], out var initial);
        PJ.Require(areaValid && typeValid && initialValid && area is -1 or 7 or 96 or 8 or 16 or 0 &&
            type is -1 or 1 or 2 or 3 && (initial is -1 or 0 || initial is >= 65 and <= 90), "INVALID_REQUEST");
        return (area, type, initial, $"{area}:{type}:{initial}");
    }
    internal static JsonObject Artists(JsonObject raw, string target,
        (int Area, int Type, int Initial, string Token) selection, int offset, int limit)
    {
        var data = raw["data"] as JsonObject ?? raw;
        var artists = PJ.Rows(PJ.First(data["artists"], raw["artists"]), limit);
        var items = artists.Select(artist =>
        {
            var id = Id(Pick(artist, "id", "artistId"));
            var name = Title(artist);
            PJ.Require(id.Length > 0 && name.Length > 0);
            var count = PJ.OptionalCount(Pick(artist, "musicSize", "songCount"));
            return PJ.Obj(("id", id), ("name", name), ("subtitle", count is null ? "" : "单曲：" + count),
                ("cover", Cover(artist)), ("kind", "artist"), ("source", "wy"));
        }).ToArray();
        var moreNode = PJ.First(data["more"], raw["more"]);
        var more = moreNode is not null && PJ.Bool(moreNode);
        if (more) PJ.Require(items.Length > 0 && offset <= 9960, "PAGINATION_STALLED");

        string Token(int area, int type, int initial) => $"{area}:{type}:{initial}";
        var tags = new JsonArray();
        void Add(string group, IEnumerable<(int Value, string Name)> options)
        {
            foreach (var option in options)
            {
                var id = group switch
                {
                    "area" => Token(option.Value, selection.Type, selection.Initial),
                    "type" => Token(selection.Area, option.Value, selection.Initial),
                    _ => Token(selection.Area, selection.Type, option.Value)
                };
                var selected = group switch
                {
                    "area" => option.Value == selection.Area,
                    "type" => option.Value == selection.Type,
                    _ => option.Value == selection.Initial
                };
                tags.Add(PJ.Obj(("id", id), ("name", option.Name), ("group", group), ("selected", selected)));
            }
        }
        Add("area", [(-1, "全部"), (7, "华语"), (96, "欧美"), (8, "日本"), (16, "韩国"), (0, "其他")]);
        Add("type", [(-1, "全部"), (1, "男歌手"), (2, "女歌手"), (3, "乐队组合")]);
        Add("initial", new[] { (-1, "热门") }.Concat(Enumerable.Range(65, 26).Select(value => (value, ((char)value).ToString())))
            .Append((0, "#")));

        var continuation = more ? PJ.Obj(("offset", offset + items.Length), ("tag", selection.Token)) : null;
        return PJ.Obj(("id", target), ("entries", new JsonArray()), ("sections", PJ.Arr([
                PJ.Obj(("id", target + ":artists"), ("title", ""), ("type", "artists"), ("layout", "artist-grid"),
                    ("items", PJ.Arr(items)))
            ])), ("tags", tags), ("selectedTag", selection.Token), ("more", more), ("continuation", continuation));
    }
    internal static JsonArray AlgorithmTags(JsonObject raw)
    {
        var result = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonNode? tag, string group)
        {
            var id = Id(A(tag, "algTagId")); var name = S(tag, "algTagName");
            if (id.Length > 0 && name.Length > 0 && seen.Add(id)) result.Add(PJ.Obj(("id", id), ("name", name), ("group", group)));
        }
        foreach (var tag in PJ.Array(PJ.First(A(raw, "data", "algTagInfos"), raw["algTagInfos"])))
        {
            var children = PJ.Array(A(tag, "children")).ToArray();
            if (children.Length > 0) foreach (var child in children) Add(child, S(tag, "algTagName"));
            else Add(tag, "");
        }
        return result;
    }
    internal static JsonObject Cube(JsonObject raw, string target, string selectedTag)
    {
        var protocol = PJ.First(A(raw, "data", "pageProtocol"), raw["pageProtocol"]);
        PJ.Require(A(protocol, "pages") is JsonArray, "INVALID_RESPONSE");
        var groups = new List<JsonNode>(); var parents = new Dictionary<JsonNode, JsonNode?>(); var groupIds = new Dictionary<JsonNode, string>();
        void Collect(JsonNode? n, JsonNode? parent = null, int depth = 0)
        {
            if (n is null || depth > 25) return;
            if (S(n, "componentName") is "MusicGenreSongListItem" or "TabItem")
            { groupIds[n] = S(n, "id").Length > 0 ? S(n, "id") : "tab-" + groups.Count; parents[n] = parent; groups.Add(n); return; }
            foreach (var child in Children(n)) Collect(child, n, depth + 1);
        }
        foreach (var n in PJ.Array(A(protocol, "pages"))) Collect(n);
        var tags = PJ.Arr(groups.Select(n => PJ.Obj(("id", groupIds[n]), ("name", S(n, "props", "title")), ("selected", PJ.True(A(n, "props", "isDefaultTab"))))).Where(t => S(t, "name").Length > 0));
        var parent = groups.Count > 0 ? parents[groups[0]] : null;
        var siblings = groups.Where(g => parents[g] == parent).ToArray();
        var defaultGroup = siblings.Length == 0 ? null : siblings[S(parent, "componentName") == "MusicGenreSongList" && !PJ.True(A(parent, "props", "useNewTabBarStyle")) ? siblings.Length / 2 : 0];
        var selected = tags.Any(t => S(t, "id") == selectedTag) ? selectedTag : S(tags.FirstOrDefault(t => PJ.True(A(t, "selected"))), "id");
        if (selected.Length == 0) selected = defaultGroup is not null ? groupIds[defaultGroup] : S(tags.FirstOrDefault(), "id");
        var group = groups.FirstOrDefault(g => groupIds[g] == selected);
        var page = PJ.Obj(("id", target), ("title", S(protocol, "title")), ("entries", new JsonArray()), ("sections", new JsonArray()),
            ("tags", tags), ("selectedTag", selected), ("more", false), ("continuation", null));
        var sections = (JsonArray)page["sections"]!;
        void Add(JsonNode n, string type, IEnumerable<JsonNode?> values, string layout = "rail", string title = "")
        {
            var items = PJ.Arr(values); if (items.Count == 0) return;
            sections.Add(PJ.Obj(("id", $"{target}:{(S(n, "id").Length > 0 ? S(n, "id") : sections.Count.ToString())}:{type}"),
                ("type", type), ("title", title), ("layout", layout), ("items", items)));
        }
        JsonObject? ComponentResource(JsonNode? n)
        {
            if (A(n, "props") is not JsonObject props) return null;
            var row = (JsonObject)props.DeepClone(); row["resourceType"] = S(n, "componentName") == "Entrance" ? "" : S(n, "componentName").ToLowerInvariant();
            return Resource(row);
        }
        void Walk(JsonNode? n, int depth = 0, string heading = "")
        {
            if (n is null || depth > 25 || groups.Contains(n) && n != group) return;
            var p = A(n, "props"); var children = Children(n).ToArray();
            if (A(p, "songList", "ruleId") is { } rule)
                page["feed"] = PJ.Obj(("id", target + ":feed:" + S(n, "id")), ("title", S(p, "songList", "title")),
                    ("songs", new JsonArray()), ("more", true), ("cursor", 0),
                    ("request", PJ.Obj(("ruleId", PJ.Scalar(rule)), ("pointIds", string.Join(",", PJ.Array(A(p, "songList", "pointId")).Select(x => Id(A(x, "id"))).Where(x => x.Length > 0))),
                        ("tabTagId", ""), ("algTagIds", ""))));
            if (A(p, "carousel") is JsonArray carousel)
            {
                Add(n, "banner", carousel.Select(x =>
                {
                    var a = Action(A(x, "action")); if (a is null || S(a, "type") is not ("song" or "playlist") || x is not JsonObject r) return null;
                    var row = (JsonObject)r.DeepClone(); row["resourceId"] = a["id"]?.DeepClone(); row["resourceType"] = S(a, "type"); return Resource(row, banner: true);
                }).Where(x => x is not null)); return;
            }
            if (A(p, "recommendModule") is JsonArray modules)
            {
                var tabs = new JsonArray();
                for (var i = 0; i < modules.Count; i++)
                {
                    var g = modules[i]; var items = PJ.Arr(PJ.Array(A(g, "modules")).OfType<JsonObject>().Select(x =>
                    {
                        var row = (JsonObject)x.DeepClone(); row["resourceType"] = x["type"]?.DeepClone();
                        if (S(x, "type") == "fm" && PJ.Scalar(x["tabTagId"]).Length == 0) row["tabTagId"] = A(p, "tab")?.DeepClone();
                        return Resource(row, channel: true);
                    }).Where(x => x is not null));
                    if (items.Count > 0) tabs.Add(PJ.Obj(("id", $"{S(n, "id")}:group:{i}"), ("name", S(g, "title")), ("selected", PJ.True(A(g, "isDefaultTab"))), ("items", items)));
                }
                if (tabs.Count > 0)
                {
                    var tab = tabs.FirstOrDefault(t => PJ.True(A(t, "selected"))) ?? tabs[0];
                    Add(n, "playlists", PJ.Array(A(tab, "items")), title: S(p, "title"));
                    if (tabs.Count > 1 && sections.LastOrDefault() is JsonObject section) { section["tabs"] = tabs.DeepClone(); section["selectedTab"] = S(tab, "id"); }
                }
                return;
            }
            if (S(p, "playlistIds").Length > 0)
            {
                var names = S(p, "titles").Split(',');
                Add(n, "rankings", S(p, "playlistIds").Split(',').Select((id, i) => Resource(PJ.Obj(("id", id), ("name", i < names.Length ? names[i] : ""), ("resourceType", "playlist")))).Where(x => x is not null),
                    "ranked-songs", S(p, "rankModuleTitle")); return;
            }
            if (S(n, "componentName") == "BillboardSingleSongList" && Id(A(p, "id")).Length > 0)
            {
                Add(n, "playlists", [Resource(PJ.Obj(("id", A(p, "id")), ("name", S(p, "cardTitle")), ("subtitle", S(p, "cardSubTitle")), ("resourceType", "playlist")))],
                    title: S(p, "cardTitle")); return;
            }
            if (A(p, "albumData") is JsonObject album)
            {
                var row = (JsonObject)album.DeepClone(); row["resourceType"] = "album"; row["subtitle"] = album["subTitle"]?.DeepClone();
                Add(n, "playlists", new[] { Resource(row) }.Where(x => x is not null), title: heading); return;
            }
            if (S(n, "componentName") == "InfiniteScrollList")
            {
                var rows = new List<JsonNode?>();
                void Songs(JsonNode? value, int level = 0)
                {
                    if (value is null || level > 12) return;
                    if (S(value, "componentName") == "Song") { var r = ComponentResource(value); if (r?["song"] is { } song) rows.Add(song); }
                    else foreach (var child in Children(value)) Songs(child, level + 1);
                }
                foreach (var child in children.Where(c => S(c, "componentName") is "Grid" or "Song")) Songs(child);
                page["feed"] = PJ.Obj(("id", target + ":feed:" + S(n, "id")), ("title", S(p, "listTitleStr")), ("songs", PJ.Arr(rows)),
                    ("more", false), ("cursor", null), ("request", null));
                page["unsupportedContinuation"] = true;
                foreach (var child in children.Where(c => S(c, "componentName") is not ("Grid" or "Song"))) Walk(child, depth + 1, heading);
                return;
            }
            if (S(n, "componentName") is "Grid" or "Scroll")
            {
                var resources = children.Where(c => S(c, "componentName") is "Playlist" or "Song" or "Album" or "Entrance")
                    .Select(ComponentResource).Where(x => x is not null).ToArray();
                Add(n, "songs", resources.Where(x => S(x, "kind") == "song").Select(x => x!["song"]), title: heading);
                Add(n, "playlists", resources.Where(x => S(x, "kind") != "song"),
                    S(n, "componentName") == "Grid" ? "grid" : "rail", heading);
                foreach (var child in children.Where(c => S(c, "componentName") is not ("Playlist" or "Song" or "Album" or "Entrance"))) Walk(child, depth + 1);
                return;
            }
            foreach (var child in children)
                if (S(child, "componentName") == "Title") heading = PJ.Text(Pick(A(child, "props"), "mainTitle", "title"));
                else Walk(child, depth + 1, heading);
        }
        foreach (var node in PJ.Array(A(protocol, "pages"))) Walk(node);
        return page;
    }
}
