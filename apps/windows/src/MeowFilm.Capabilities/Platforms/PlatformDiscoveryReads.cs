// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    private static JsonElement? CloneContinuation(JsonElement? value)
    {
        if (value is null) return null;
        PJ.Require(value.Value.ValueKind == JsonValueKind.Object);
        return value.Value.Clone();
    }
    private static JsonObject Continuation(JsonElement? value, Call c, string target, params string[] allowed)
    {
        if (value is null) return new();
        var input = PJ.Parse(Encoding.UTF8.GetBytes(value.Value.GetRawText()));
        PJ.Require(input.All(p => allowed.Contains(p.Key) || p.Key is "scope" or "target"));
        PJ.Require(PJ.S(input, "scope") == c.Scope && PJ.S(input, "target") == target, "STALE_SESSION");
        return input;
    }
    private static JsonObject BindDiscovery(JsonObject page, Call c, string target, JsonNode? previous = null)
    {
        void Walk(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var n in array) Walk(n); return; }
            if (node is not JsonObject o) return;
            foreach (var key in o.Select(p => p.Key).Where(k => k is "notice" or "message" ||
                Regex.IsMatch(k, "cookie|token|password|authorization", RegexOptions.IgnoreCase)).ToArray()) o.Remove(key);
            foreach (var child in o.Select(p => p.Value).ToArray()) Walk(child);
            var action = o["action"];
            if (PJ.S(action, "type") is { Length: > 0 } type)
            {
                var supported = type is "song" or "playlist" or "channel" or "chart" or "album" or "daily" or "fm" or "similar" or "heartbeat";
                var needsAccount = type is "daily" or "fm" or "similar" or "heartbeat";
                var display = !needsAccount || c.Session is not null;
                o["requiresAccount"] = needsAccount;
                o["display"] = display;
                o["available"] = supported && display;
                if (!supported || needsAccount && c.Session is null) o["errorCode"] = needsAccount && c.Session is null ? "AUTH_REQUIRED" : "UNSUPPORTED";
            }
        }
        Walk(page);
        if (page["continuation"] is JsonObject next)
        {
            var cursor = PJ.Scalar(PJ.First(next["cursor"], next["before"], next["offset"], next["page"]));
            var old = PJ.Scalar(PJ.First(PJ.At(previous, "cursor"), PJ.At(previous, "before"), PJ.At(previous, "offset"), PJ.At(previous, "page")));
            if (cursor.Length > 0) PJ.Require(cursor != old, "PAGINATION_STALLED");
            var count = PJ.Array(page["entries"]).Count() + PJ.Array(page["sections"]).Sum(s => PJ.Array(PJ.At(s, "items")).Count());
            PJ.Require(count > 0, "PAGINATION_STALLED");
            next["scope"] = c.Scope; next["target"] = target;
        }
        page["platform"] = c.Platform;
        return page;
    }
    private static Task<JsonObject> Content(Call c, JsonElement? continuation, string kind, string target)
    {
        var prev = Continuation(continuation, c, target, "cursor", "blockCodeOrderList", "loadedPositionCodes");
        var cursor = PJ.Scalar(prev["cursor"]); if (cursor.Length == 0) cursor = "0";
        PJ.Require(long.TryParse(cursor, out var numeric) && numeric is >= 0 and <= 9007199254740991 && Regex.IsMatch(cursor, "^[0-9]{1,16}$"));
        var input = PJ.Obj(("pageCode", kind == "home" ? "HOME_RECOMMEND_PAGE" : "HOME_DISCOVERY_PAGE"), ("cursor", numeric),
            ("isFirstScreen", cursor == "0"), ("pageStyleType", "noCutBlock"), ("refresh", false));
        foreach (var key in new[] { "blockCodeOrderList", "loadedPositionCodes" })
            if (PJ.S(prev, key).Length > 0) input[key] = PJ.InputAll(PJ.S(prev, key));
        return c.Wy("/api/link/page/rcmd/resource/show", input, data =>
        {
            Shape(data);
            var page = PlatformDiscovery.Page(data, target, cursor);
            if (page["continuation"] is JsonObject next)
            {
                JsonArray ParseCodes(string text)
                {
                    if (text.Length == 0) return new();
                    try
                    {
                        var result = JsonNode.Parse(text) as JsonArray;
                        PJ.Require(result is not null && result.All(v => PJ.Text(v).Length > 0));
                        return result!;
                    }
                    catch (JsonException) { throw new MusicPlatformException("INVALID_REQUEST"); }
                }
                var all = ParseCodes(PJ.S(prev, "loadedPositionCodes")).Concat(ParseCodes(PJ.S(next, "loadedPositionCodes"))).Select(PJ.Text).Distinct(StringComparer.Ordinal).ToArray();
                next["loadedPositionCodes"] = PJ.Encode(PJ.Node(all));
            }
            return BindDiscovery(page, c, target, prev);
        });
    }
    private static void Shape(JsonObject data)
    {
        PJ.Require(PJ.At(data, "data", "blocks") is JsonArray, "INVALID_RESPONSE");
        if (PJ.At(data, "data", "hasMore") is { } more)
        {
            PJ.Bool(more);
            if (PJ.True(more)) PJ.Require(PJ.At(data, "data", "cursor") is not null, "PAGINATION_STALLED");
        }
    }
    private static Task<JsonObject> Directory(Call c) =>
        c.Wy("/api/link/position/show/resource", PJ.Obj(("positionCode", "music_top_tab_full")), PlatformDiscovery.Directory);
    public Task<JsonElement> HomeAsync(MusicHomeRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request! with { Continuation = CloneContinuation(request!.Continuation) };
        return RunAsync(p.Platform, "home", p, p.Refresh, c =>
        {
            if (c.Platform == "kg") return KgHome(c, p);
            PJ.Require(c.Platform == "wy", c.Session is null ? "AUTH_REQUIRED" : "UNSUPPORTED");
            return Content(c, p.Continuation, "home", "wy:home");
        }, cancellationToken);
    }
    public async Task<JsonElement> ChannelsAsync(MusicChannelsRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request!;
        var directory = await ChannelDirectoryAsync(p, cancellationToken);
        return p.Platform == "wy"
            ? PJ.Element(PlatformDiscovery.ChannelDirectory(JsonObject.Create(directory)!))
            : directory;
    }
    private Task<JsonElement> ChannelDirectoryAsync(MusicChannelsRequest p, CancellationToken cancellationToken)
    {
        return RunAsync(p.Platform, "channels", p, p.Refresh, async c =>
        {
            if (c.Platform == "kg") return PlatformKgDiscovery.Directory(await KgTags(c));
            PJ.Require(c.Platform == "wy", "UNSUPPORTED");
            return await Directory(c);
        }, cancellationToken, privateCache: p.Platform == "wy");
    }
    public Task<JsonElement> ChannelAsync(MusicChannelRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request! with { Continuation = CloneContinuation(request!.Continuation) };
        PJ.Input(p.Id); if (p.PageCode is not null) PJ.Input(p.PageCode, 100); if (p.Tag is { Length: > 0 }) PJ.Input(p.Tag);
        return RunAsync(p.Platform, "channel", p, p.Refresh, async c =>
        {
            if (c.Platform == "kg") return await KgChannel(c, p);
            PJ.Require(c.Platform == "wy", c.Session is null ? "AUTH_REQUIRED" : "UNSUPPORTED");
            // Refreshing channel content does not refresh its still-current directory.
            var directory = await ChannelDirectoryAsync(new("wy"), c.Token);
            await c.CheckSessionAsync();
            var client = await clients.AcquireWyAsync(c.Token);
            c.Guard(); // Adopting a trusted directory update must not hide a replaced identity.
            c.Client = client;
            var descriptor = PJ.Array(JsonObject.Create(directory)!["items"])
                .OfType<JsonObject>().FirstOrDefault(d => PJ.S(d, "id") == p.Id);
            PJ.Require(descriptor is not null, "NOT_FOUND");
            if (p.PageCode is not null) PJ.Require(PJ.S(descriptor, "pageCode") == p.PageCode, "IDENTITY_MISMATCH");
            var target = "wy:channel:" + p.Id;
            JsonObject page;
            switch (PJ.S(descriptor, "kind"))
            {
                case "featured": page = await Content(c, p.Continuation, "featured", target); break;
                case "rankings":
                    PJ.Require(p.Continuation is null);
                    page = await c.Wy("/api/toplist/detail/v2", new(),
                        data => BindDiscovery(PlatformDiscovery.Rankings(data, target), c, target), true);
                    break;
                case "new-songs":
                    PJ.Require(p.Continuation is null);
                    page = await c.Wy("/api/v1/discovery/new/songs", PJ.Obj(("areaId", 0), ("total", true)),
                        data => BindDiscovery(PJ.Obj(("id", target), ("entries", new JsonArray()), ("sections", PJ.Arr([
                            PJ.Obj(("id", target + ":songs"), ("title", PJ.S(descriptor, "name")), ("type", "songs"), ("layout", "list"),
                                ("items", PJ.Arr(PJ.Rows(data["data"], 500).Select(PlatformSongs.Wy))))])), ("more", false), ("continuation", null)), c, target), true);
                    break;
                case "playlists":
                    var prev = Continuation(p.Continuation, c, target, "cursor", "tag");
                    var category = p.Tag ?? PJ.S(prev, "tag");
                    if (category.Length == 0) category = "推荐";
                    PJ.Require(category is "推荐" or "官方" or "华语" or "摇滚" or "民谣" or "电子" or "轻音乐", "INVALID_REQUEST");
                    if (p.Continuation is not null) PJ.Require(PJ.S(prev, "tag") == category, "SNAPSHOT_CHANGED");
                    var input = PJ.Obj(("categoryName", category), ("offset", 0), ("limit", 20));
                    if (PJ.Scalar(prev["cursor"]).Length > 0) input["cursor"] = PJ.Input(PJ.Scalar(prev["cursor"]), 24000);
                    page = await c.Wy("/api/playlist/square/block/page", input, square =>
                    {
                        Shape(square);
                        var squarePage = PlatformDiscovery.Page(square, target, PJ.Scalar(prev["cursor"]));
                        squarePage["tags"] = PJ.Arr(new[] { "推荐", "官方", "华语", "摇滚", "民谣", "电子", "轻音乐" }
                            .Select(name => PJ.Obj(("id", name), ("name", name), ("group", "playlist"), ("selected", name == category))));
                        squarePage["selectedTag"] = category;
                        if (squarePage["continuation"] is not null) squarePage["continuation"] = PJ.Obj(
                            ("cursor", PJ.Scalar(PJ.At(square, "data", "cursor"))), ("tag", category));
                        return BindDiscovery(squarePage, c, target, prev);
                    }, true);
                    break;
                case "cube": page = await Cube(c, p, descriptor!, target); break;
                case "artists":
                    var artistPrev = Continuation(p.Continuation, c, target, "offset", "tag");
                    var artistSelection = PlatformDiscovery.ArtistSelection(p.Tag ?? PJ.S(artistPrev, "tag"));
                    if (p.Continuation is not null) PJ.Require(PJ.S(artistPrev, "tag") == artistSelection.Token, "SNAPSHOT_CHANGED");
                    var offset = artistPrev["offset"] is null ? 0 : PJ.Count(artistPrev["offset"]);
                    page = await c.Wy("/api/v1/artist/list", PJ.Obj(("area", artistSelection.Area),
                        ("type", artistSelection.Type), ("initial", artistSelection.Initial), ("offset", offset),
                        ("limit", 40), ("total", true)),
                        artists => BindDiscovery(PlatformDiscovery.Artists(artists, target, artistSelection, offset, 40),
                            c, target, artistPrev), true);
                    break;
                case "vip":
                    PJ.Require(p.Continuation is null);
                    async Task<(JsonObject? Section, MusicPlatformException? Failure)> ReadVip(
                        Func<Task<JsonObject?>> read)
                    {
                        try { return (await read(), null); }
                        catch (MusicPlatformException error) when (error.Code is not
                            ("CANCELLED" or "TIMEOUT" or "STALE_SESSION" or "CLOSED" or "CREDENTIALS_UNAVAILABLE"))
                        {
                            c.Guard();
                            return (null, error);
                        }
                    }
                    var recommendations = await ReadVip(() =>
                        c.Wy("/api/vipnewcenter/app/viptab/recommend/song/list", new(),
                            response => PlatformDiscovery.VipRecommendations(response, target)));
                    var charts = await ReadVip(() => c.Wy("/api/toplist/detail/v2", new(),
                        response => PlatformDiscovery.VipRankings(response, target), true));
                    if (recommendations.Failure is not null && charts.Failure is not null)
                        throw recommendations.Failure!;
                    page = BindDiscovery(PlatformDiscovery.Vip(recommendations.Section, charts.Section, target),
                        c, target);
                    break;
                case "artist-activity": throw new MusicPlatformException(c.Session is null ? "AUTH_REQUIRED" : "UNSUPPORTED");
                default: throw new MusicPlatformException("UNSUPPORTED");
            }
            page["channel"] = PlatformDiscovery.ChannelDescriptor(descriptor!);
            page["title"] = descriptor!["name"]?.DeepClone();
            return page;
        }, cancellationToken);
    }
    private static async Task CompleteCubePlaylists(Call c, JsonObject page)
    {
        var sections = PJ.Array(page["sections"]).ToArray();
        var visible = sections.SelectMany(section => PJ.Array(PJ.At(section, "items")))
            .OfType<JsonObject>().Where(card => PJ.S(card, "action", "type") == "playlist").ToArray();
        var cards = visible.Concat(sections.SelectMany(section => PJ.Array(PJ.At(section, "tabs"))
                .SelectMany(tab => PJ.Array(PJ.At(tab, "items")))).OfType<JsonObject>())
            .Where(card => PJ.S(card, "action", "type") == "playlist").ToArray();
        var ids = visible.Where(card => PJ.S(card, "name").Length == 0 || PJ.S(card, "cover").Length == 0)
            .Select(card => PJ.S(card, "action", "id")).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var batch in ids.Chunk(100))
        {
            var summaries = await c.Wy("/api/tag/tab/playlists", PJ.Obj(("ids", string.Join(",", batch))), raw =>
            {
                var rows = PJ.Rows(raw["data"], batch.Length);
                PJ.Require(rows.Count == batch.Length, "NOT_FOUND");
                var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var summary = PlatformSongs.Playlist("wy", row);
                    var id = PJ.S(summary, "id");
                    PJ.Require(batch.Contains(id, StringComparer.Ordinal) && result.TryAdd(id, summary), "IDENTITY_MISMATCH");
                    if (PJ.S(summary, "creator").Length == 0) summary["creator"] = PJ.Metadata(row["userName"]);
                }
                return result;
            });
            foreach (var card in cards)
            {
                if (!summaries.TryGetValue(PJ.S(card, "action", "id"), out var summary)) continue;
                foreach (var field in new[] { "name", "cover", "creator" })
                    if (PJ.S(card, field).Length == 0) card[field] = summary[field]?.DeepClone();
                if (card["count"] is null) card["count"] = summary["count"]?.DeepClone();
            }
        }
    }
    private static async Task<JsonObject> Cube(Call c, MusicChannelRequest p, JsonObject descriptor, string target)
    {
        var code = PJ.S(descriptor, "pageCode"); PJ.Require(Regex.IsMatch(code, "^[a-zA-Z0-9_-]{6,100}$"), "INVALID_RESPONSE");
        var prev = Continuation(p.Continuation, c, target, "offset", "ruleId", "pointIds", "tabTagId", "algTagIds", "tag");
        var selectedTag = p.Tag ?? PJ.S(prev, "tag");
        var layout = await c.Wy("/api/cube/render/page/protocol", PJ.Obj(("pageCode", code), ("scene", "music")), raw =>
        {
            var cubePage = PlatformDiscovery.Cube(raw, target, selectedTag);
            var isLayoutTag = PJ.Array(cubePage["tags"]).Any(t => PJ.S(t, "id") == selectedTag);
            if (isLayoutTag) PJ.Require(PJ.S(cubePage, "selectedTag") == selectedTag, "SNAPSHOT_CHANGED");
            if (p.Continuation is not null) PJ.Require(PJ.S(prev, "tag") == selectedTag, "SNAPSHOT_CHANGED");
            if (PJ.At(cubePage, "feed", "request") is JsonObject pageRequest)
            {
                PJ.Id(pageRequest["ruleId"], "^[0-9]{1,20}$");
                if (p.Continuation is not null) PJ.Require(PJ.S(prev, "ruleId") == PJ.S(pageRequest, "ruleId") &&
                    PJ.S(prev, "pointIds") == PJ.S(pageRequest, "pointIds"), "SNAPSHOT_CHANGED");
            }
            else
            {
                if (selectedTag.Length > 0) PJ.Require(isLayoutTag, "NOT_FOUND");
                PJ.Require(p.Continuation is null, "PAGINATION_STALLED");
                cubePage = BindDiscovery(cubePage, c, target);
            }
            return (Page: cubePage, LayoutTag: isLayoutTag);
        }, true);
        var page = layout.Page; var layoutTag = layout.LayoutTag;
        await CompleteCubePlaylists(c, page);
        if (PJ.At(page, "feed", "request") is JsonObject req)
        {
            var offset = prev["offset"] is null ? 0 : PJ.Count(prev["offset"]); PJ.Require(offset <= 3000);
            var algTagIds = !layoutTag && selectedTag.Length > 0 ? selectedTag : "";
            if (p.Continuation is not null) PJ.Require(PJ.S(prev, "algTagIds") == algTagIds && PJ.S(prev, "tabTagId") == "", "SNAPSHOT_CHANGED");
            var tags = await c.Wy("/api/tag/tab/tags", PJ.Obj(("ruleId", req["ruleId"]), ("ruleType", 1), ("tabTagId", "")), tagData =>
            {
                var tagInfos = PJ.First(PJ.At(tagData, "data", "algTagInfos"), tagData["algTagInfos"]);
                PJ.Require(tagInfos is null or JsonArray, "INVALID_RESPONSE");
                var algorithmTags = PlatformDiscovery.AlgorithmTags(tagData);
                if (selectedTag.Length > 0 && !layoutTag) PJ.Require(algorithmTags.Any(t => PJ.S(t, "id") == selectedTag), "NOT_FOUND");
                return algorithmTags;
            });
            return await c.Wy("/api/tag/tab/alg/recommends", PJ.Obj(("ruleType", 1), ("ruleId", req["ruleId"]), ("tabTagId", ""),
                ("algTagIds", algTagIds), ("extMap", PJ.Encode(PJ.Obj(("pointIds", req["pointIds"])))), ("limit", 30), ("offset", offset),
                ("trialMode", 103), ("source", offset > 0 ? "nextPage" : "reset")), feedData =>
            {
                var body = feedData["data"] as JsonObject ?? feedData;
                var songs = PJ.Rows(body["songs"], 30).Select(PlatformSongs.Wy).ToArray();
                var more = PJ.Bool(body["hasMore"]);
                if (more) PJ.Require(songs.Length > 0 && offset < 2970, "PAGINATION_STALLED");
                var feed = (JsonObject)page["feed"]!;
                var request = (JsonObject)req.DeepClone(); request["algTagIds"] = algTagIds;
                feed["songs"] = PJ.Arr(songs); feed["tags"] = tags; feed["selectedTag"] = selectedTag;
                feed["request"] = request; feed["more"] = more; feed["cursor"] = more ? JsonValue.Create(offset + 30) : null;
                page["more"] = more;
                ((JsonArray)page["sections"]!).Add(PJ.Obj(("id", feed["id"]), ("title", feed["title"]), ("type", "songs"), ("layout", "list"), ("items", PJ.Arr(songs))));
                page = BindDiscovery(page, c, target);
                if (more)
                {
                    var next = (JsonObject)request.DeepClone();
                    next["tag"] = selectedTag; next["offset"] = offset + 30; next["scope"] = c.Scope; next["target"] = target;
                    page["continuation"] = next;
                }
                return page;
            });
        }
        return page;
    }
}
