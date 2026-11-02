// SPDX-License-Identifier: GPL-3.0-only
// Read-only personal recipes from fixed iWebPlus/upstream pins. No writes/reporting/login fallback.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    public Task<JsonElement> DailyAsync(MusicDailyRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null && Enum.IsDefined(request.Branch)); var p = request!;
        return RunAsync(p.Platform, "daily", p, p.Refresh, c =>
        {
            PJ.Require(c.Platform == "wy", "UNSUPPORTED"); c.Auth();
            var style = p.Branch == MusicDailyBranch.Style;
            return c.Xeapi(style ? "/api/homepage/category/daily/song/list" : "/api/v3/discovery/recommend/songs",
                style ? new() : PJ.Obj(("ispush", false)), data =>
                {
                    var body = data["data"] as JsonObject ?? data;
                    // A missing array, failed signature/decryption or failed upstream code never becomes empty success.
                    var rows = PJ.Rows(PJ.First(body["dailySongs"], data["recommend"]), 1000);
                    var branch = style ? "style" : "default";
                    var batch = Batch(c, "daily", rows, PJ.Obj(("branch", branch)));
                    var now = DateTimeOffset.UtcNow;
                    var local = now.ToOffset(TimeSpan.FromHours(8));
                    var six = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
                    if (six <= local) six = six.AddDays(1);
                    batch["expiresAt"] = (style ? now.AddMinutes(1) : six).ToUnixTimeMilliseconds();
                    batch["id"] = "daily:" + branch; batch["branch"] = branch;
                    batch["name"] = style ? "风格推荐" : "每日推荐";
                    batch["collectionType"] = "daily"; batch["engine"] = "NeteaseCollection"; batch["source"] = "wy"; batch["more"] = false;
                    batch["cover"] = PJ.S(PJ.Array(batch["items"]).FirstOrDefault(), "cover_url");
                    batch["category"] = PJ.At(body, "tags", "categoryId") is { } category ? PJ.Id(category) : "";
                    var tags = PJ.At(body, "tags", "tagVOList");
                    batch["tags"] = PJ.Arr(PJ.Rows(tags ?? new JsonArray(), 100).Select(t =>
                        PJ.Obj(("id", PJ.Id(t["tagId"])), ("name", PJ.Metadata(t["tagName"])))));
                    return batch;
                });
        }, cancellationToken, privateCache: true);
    }
    private static readonly string[] FmScenes = ["NIGHT_EMO", "EXERCISE", "SLEEP_HELP", "RELAX", "CHEERFUL", "LYRICAL", "CURE", "FOCUS",
        "SWEET", "RHYTHM_BLUES", "RAINY", "GAMES", "RAP", "K_POP", "ORIGINAL_MUSICIAL", "ELECTRONIC", "COMMUTE",
        "TAKE_SHOWER", "COFFEE_SHOP", "ROCK", "INSPIRATIONAL", "CHINESE", "ENGLISH", "YUEYU", "MANYAO", "JINGDIAN",
        "LIGHT", "GUOFENG", "FOLK", "ACG", "GUDIAN", "JAZZ", "JAPANESE", "GLOBAL", "FRANCH", "BLUE", "DANCE",
        "LATIN", "PUNK", "COUNTRY", "MUSICAL", "YINGSHI"];
    public Task<JsonElement> FmAsync(MusicFmRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null && Enum.IsDefined(request.Mode)); var p = request!;
        if (p.Submode is null) throw new MusicPlatformException("INVALID_REQUEST");
        var rule = !string.IsNullOrEmpty(p.RuleId) || !string.IsNullOrEmpty(p.TabTagId);
        if (rule)
        {
            PJ.Require(p.Mode == MusicFmMode.Default && p.Submode == "");
            if (!string.IsNullOrEmpty(p.RuleId)) { PJ.IdInput(p.RuleId); PJ.Require(string.IsNullOrEmpty(p.TabTagId)); }
            else PJ.Require(p.TabTagId is not null && (Regex.IsMatch(p.TabTagId, "^[1-9][0-9]{0,19}$") || PlatformDiscovery.FmTabs.Contains(p.TabTagId)));
        }
        else PJ.Require((p.Mode == MusicFmMode.Scene) == (p.Submode.Length > 0) && (p.Submode.Length == 0 || FmScenes.Contains(p.Submode)));
        return RunAsync(p.Platform, "fm", p, false, async c =>
        {
            PJ.Require(c.Platform == "wy", "UNSUPPORTED"); c.Auth();
            if (rule)
            {
                return await c.Wy(p.Preview ? "/api/tag/tab/fm/cover" : "/api/tag/tab/fm/recommends",
                    RuleParameters(p), data =>
                {
                    if (p.Preview)
                    {
                        PJ.Require(PJ.At(data, "data") is JsonObject && PJ.At(data, "data", "cover") is not null, "INVALID_RESPONSE");
                        return PJ.Obj(("platform", "wy"), ("kind", "fm-preview"), ("cover", PJ.Image(PJ.At(data, "data", "cover"))),
                            ("preview", true), ("context", PJ.Obj(("provider", "rule"), ("ruleId", p.RuleId ?? ""), ("tabTagId", p.TabTagId ?? ""))));
                    }
                    return Batch(c, "fm", PJ.Rows(data["data"], 5), PJ.Obj(("provider", "rule"), ("ruleId", p.RuleId ?? ""), ("tabTagId", p.TabTagId ?? "")));
                });
            }
            var mode = p.Mode switch { MusicFmMode.Familiar => "FAMILIAR", MusicFmMode.Explore => "EXPLORE",
                MusicFmMode.Puzzle => "PUZZLE_MODE_RCMD", MusicFmMode.Scene => "SCENE_RCMD", _ => "DEFAULT" };
            return await c.Wy("/api/v1/radio/get", PJ.Obj(("mode", mode), ("subMode", p.Submode), ("limit", p.Preview ? 1 : 3)),
                data => Batch(c, "fm", RadioRows(data, p.Preview ? 1 : 3), PJ.Obj(("mode", mode), ("submode", p.Submode), ("preview", p.Preview))));
        }, cancellationToken, cacheable: false);
    }
    private static JsonObject RuleParameters(MusicFmRequest p)
    {
        var data = PJ.Obj(("ruleType", 1), ("ruleId", p.RuleId ?? ""), ("tabTagId", p.TabTagId ?? ""), ("firstSongId", ""), ("firstSongAlg", ""));
        if (!p.Preview) data["limit"] = 5;
        return data;
    }
    private static string[] SongIds(IReadOnlyList<string>? ids, int max)
    {
        PJ.Require(ids is not null && ids.Count > 0 && ids.Count <= max);
        var copy = ids!.ToArray();
        foreach (var id in copy) PJ.IdInput(id);
        return copy;
    }
    private static string IntegerArray(IEnumerable<string> ids) => "[" + string.Join(",", ids) + "]";
    private static List<JsonObject> RadioRows(JsonObject data, int max)
        => PJ.Rows(data["data"] is JsonArray ? data["data"] : PJ.At(data, "data", "songs"), max);
    private static JsonObject Batch(Call c, string kind, List<JsonObject> rows, JsonObject context, JsonObject? algorithms = null)
    {
        var items = rows.Select(PlatformSongs.Wy).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var song in items) PJ.Require(seen.Add(NativeMusicSong.NativeId(song)), "IDENTITY_MISMATCH");
        var provenance = rows.Select((r, i) =>
        {
            string id = NativeMusicSong.NativeId(items[i]);
            return PJ.Obj(("id", id),
                ("alg", PJ.Metadata(PJ.First(r["alg"], PJ.At(r, "songInfo", "alg"), algorithms?[id]))),
                ("source", kind));
        }).ToArray();
        return PJ.Obj(("platform", c.Platform), ("kind", kind), ("items", PJ.Arr(items)), ("count", items.Length),
            ("empty", items.Length == 0), ("context", context), ("recommendations", PJ.Arr(provenance)),
            ("fetchedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("expiresAt", null));
    }
    public Task<JsonElement> SimilarAsync(MusicSimilarRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request! with { SourceIds = SongIds(request!.SourceIds, 20) };
        return RunAsync(p.Platform, "similar", p, false, c =>
        {
            PJ.Require(c.Platform == "wy", "UNSUPPORTED"); c.Auth();
            return c.Wy("/api/v1/radio/get", PJ.Obj(("limit", "3"), ("mode", "SIMILAR"), ("sourceType", "song"),
                ("sourceIds", IntegerArray(p.SourceIds)), ("unplaySonIds", "[]")),
                data => Batch(c, "similar", RadioRows(data, 3), PJ.Obj(("sourceIds", p.SourceIds), ("sourceType", "song"))), true);
        }, cancellationToken, cacheable: false);
    }
    public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request!;
        PJ.IdInput(p.PlaylistId); PJ.IdInput(p.StartMusicId); PJ.IdInput(p.SongId);
        return RunAsync(p.Platform, "heartbeat", p, false, c =>
        {
            PJ.Require(c.Platform == "wy", "UNSUPPORTED"); c.Auth();
            return c.Wy("/api/playmode/intelligence/list", PJ.Obj(("playlistId", p.PlaylistId), ("startMusicId", p.StartMusicId),
                ("songId", p.SongId), ("type", p.FromHome ? "fromPlayAll" : "fromPlayOne"), ("count", 100)), data =>
            {
                // Missing songInfo is not enough metadata for a playable placeholder.
                var rows = PJ.Rows(data["data"], 100);
                PJ.Require(rows.All(r => r["songInfo"] is JsonObject), "INVALID_RESPONSE");
                return Batch(c, "heartbeat", rows, PJ.Obj(("playlistId", p.PlaylistId), ("startMusicId", p.StartMusicId),
                    ("songId", p.SongId), ("fromHome", p.FromHome)));
            });
        }, cancellationToken, cacheable: false);
    }
    public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null); var p = request! with { SongIds = SongIds(request!.SongIds, 1000) };
        PJ.IdInput(p.CurrentPlaySongId); PJ.Require(p.SongIds.Contains(p.CurrentPlaySongId, StringComparer.Ordinal));
        return RunAsync(p.Platform, "queue-recommendations", p, false, c =>
        {
            PJ.Require(c.Platform == "wy", "UNSUPPORTED"); c.Auth();
            return c.Wy("/api/homepage/more/rcmd/song", PJ.Obj(("songIds", IntegerArray(p.SongIds)),
                ("currentPlaySongId", p.CurrentPlaySongId), ("source", "curlist_more_rcmd")),
                data => Batch(c, "queue-recommendations", PJ.Rows(PJ.At(data, "data", "songList"), 1000),
                    PJ.Obj(("songIds", p.SongIds), ("currentPlaySongId", p.CurrentPlaySongId)), PJ.At(data, "data", "algMap") as JsonObject));
        }, cancellationToken, cacheable: false);
    }
    public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest request, CancellationToken cancellationToken = default)
    {
        PJ.Require(request is not null && Enum.IsDefined(request.Kind));
        var p = request! with { Continuation = CloneContinuation(request!.Continuation) };
        PJ.PageInput(p.Page, p.Limit);
        if (p.Refresh) PJ.Require(p.Page == 1 && p.Continuation is null);
        return RunAsync(p.Platform, "personal-playlists", p, p.Refresh, async c =>
        {
            c.Auth(); var uid = PJ.IdInput(c.Session!.UserId);
            var target = c.Platform + ":personal-playlists:" + p.Kind.ToString().ToLowerInvariant();
            var prev = Continuation(p.Continuation, c, target, "page", "limit", "seen", "total", "version", "snapshot",
                "txPhase", "txOffset", "txTotal", "txSeen");
            PJ.Require(p.Page == 1 ? p.Continuation is null : p.Continuation is not null &&
                PJ.Count(prev["page"]) == p.Page && PJ.Count(prev["limit"]) == p.Limit);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (prev["seen"] is JsonArray old)
                foreach (var value in old) PJ.Require(PJ.Text(value).Length is > 0 and <= 240 && seen.Add(PJ.Text(value)), "INVALID_REQUEST");
            else PJ.Require(p.Page == 1);
            List<JsonObject> items; long? total = null; string? version = null; string? snapshot = null; bool more;
            JsonObject? providerCursor = null;
            if (c.Platform == "wy")
            {
                PJ.Require(p.Kind == MusicPersonalListKind.All, "UNSUPPORTED");
                return await c.Wy("/api/user/playlist", PJ.Obj(("uid", uid), ("limit", p.Limit),
                    ("offset", (p.Page - 1L) * p.Limit), ("includeVideo", true)), data =>
                {
                    var raw = PJ.Rows(data["playlist"], p.Limit); var wyMore = PJ.Bool(data["more"]);
                    if (wyMore) PJ.Require(raw.Count == p.Limit, "INCOMPLETE_PAGE");
                    var wyItems = raw.Select(r =>
                    {
                        var owner = PJ.Id(PJ.At(r, "creator", "userId"));
                        if (r["userId"] is { } declared) PJ.Require(PJ.Id(declared) == owner, "IDENTITY_MISMATCH");
                        var card = PlatformSongs.Playlist("wy", r); var id = PJ.S(card, "id");
                        card["ownerId"] = owner; card["relationship"] = owner == uid ? "owned" : "subscribed";
                        card["kind"] = owner == uid && PJ.Scalar(r["specialType"]) == "5" ? "favorites" : "playlist";
                        card["locator"] = PJ.Obj(("playlistId", id), ("specialType", r["specialType"]));
                        card["action"] = PJ.Obj(("type", "playlist"), ("id", id)); card["available"] = true;
                        return card;
                    }).ToList();
                    return Complete(wyItems, null, null, null, wyMore, null);
                }, true);
            }
            else if (c.Platform == "tx")
            {
                (items, total, more, snapshot, providerCursor) = await TxPersonal(c, p, uid, prev);
            }
            else
            {
                PJ.Require(p.Kind == MusicPersonalListKind.All, "UNSUPPORTED");
                PJ.Require(p.Limit <= 30); // Fixed reference requests 30, never skip capped directory pages.
                var cookies = PJ.Cookies(c.Session); var token = PJ.Cookie(cookies, "token");
                var data = await c.Kg("/v7/get_all_list", PJ.Obj(("plat", 1), ("userid", uid), ("token", token)), "cloudlist.service.kugou.com",
                    PJ.Obj(("userid", uid), ("token", token), ("total_ver", 979), ("type", 2), ("page", p.Page), ("pagesize", p.Limit)));
                var body = data["data"] as JsonObject ?? data;
                total = PJ.OptionalCount(PJ.First(body["count"], body["total"], data["count"], data["total"]));
                if (total is null && new[] { "list_count", "collect_count", "album_count" }.Any(k => body[k] is not null))
                    total = new[] { "list_count", "collect_count", "album_count" }.Sum(k => PJ.Count(body[k]));
                version = PJ.First(body["total_ver"], data["total_ver"]) is { } v ? PJ.Id(v) : null;
                var list = PJ.First(body["info"], body["songs"], data["info"]);
                var raw = PJ.Rows(list ?? (total == 0 ? new JsonArray() : null), p.Limit);
                items = raw.Select(r => KgPersonalItem(r, uid, c)).ToList();
                more = total is { } count ? seen.Count + items.Count < count : items.Count > 0;
                if (total is { } sum) PJ.Require(seen.Count + items.Count <= sum && (items.Count > 0 || seen.Count == sum), "INCOMPLETE_PAGE");
            }
            return Complete(items, total, version, snapshot, more, providerCursor);
            JsonObject Complete(List<JsonObject> pageItems, long? pageTotal, string? pageVersion,
                string? pageSnapshot, bool hasMore, JsonObject? cursor)
            {
                if (prev["total"] is not null) PJ.Require(pageTotal == PJ.Count(prev["total"]), "SNAPSHOT_CHANGED");
                if (prev["version"] is not null) PJ.Require(pageVersion == PJ.Scalar(prev["version"]), "SNAPSHOT_CHANGED");
                if (prev["snapshot"] is not null) CheckSnapshot(PJ.S(prev, "snapshot"), pageSnapshot ?? "");
                foreach (var item in pageItems) PJ.Require(seen.Add(PJ.S(item, "id")), "PAGINATION_STALLED");
                if (hasMore) PJ.Require(pageItems.Count > 0 || cursor is not null, "PAGINATION_STALLED");
                var next = hasMore ? PJ.Obj(("scope", c.Scope), ("target", target), ("page", checked(p.Page + 1)), ("limit", p.Limit),
                    ("seen", seen.ToArray()), ("total", pageTotal), ("version", pageVersion), ("snapshot", pageSnapshot)) : null;
                if (next is not null && cursor is not null)
                    foreach (var field in cursor) next[field.Key] = field.Value?.DeepClone();
                return PJ.Obj(("platform", c.Platform), ("accountId", uid), ("collection", p.Kind.ToString().ToLowerInvariant()),
                    ("items", PJ.Arr(pageItems)), ("page", p.Page), ("limit", p.Limit), ("total", pageTotal), ("more", hasMore),
                    ("continuation", next), ("snapshot", pageSnapshot), ("version", pageVersion));
            }
        }, cancellationToken, cacheable: false);
    }
    private static JsonObject KgPersonalItem(JsonObject r, string uid, Call c)
    {
        var id = PJ.Id(r["listid"], "^[1-9][0-9]{0,19}$"); var type = PJ.Count(r["type"]);
        PJ.Require(type is 0 or 1, "INVALID_RESPONSE");
        var owner = PJ.Id(r["list_create_userid"]); var source = PJ.Count(r["source"]);
        var count = PJ.Count(r["count"]); var name = PJ.Metadata(r["name"]); PJ.Require(name.Length > 0, "INVALID_RESPONSE");
        var special = PJ.Scalar(r["is_def"]) is "1" or "2" || PJ.Scalar(r["is_default"]) == "1";
        var favorite = type == 0 && owner == uid && special && name is "我喜欢" or "我喜欢的音乐";
        var global = PJ.Scalar(PJ.First(r["global_collection_id"], r["list_create_gid"]));
        var playable = Regex.IsMatch(global, @"^[A-Za-z0-9_-]{1,200}$") && !global.StartsWith("id_", StringComparison.Ordinal);
        var owned = type == 0 && owner == uid && source != 2;
        var locator = owned ? PrivateLocator(c, "kugou-owned", id, count, r["list_ver"] is { } version ? PJ.Id(version) : null, name) : new JsonObject();
        locator["listId"] = id; locator["listType"] = type; locator["globalCollectionId"] = playable ? global : null;
        locator["source"] = source; locator["listVersion"] = r["list_ver"]?.DeepClone();
        var action = owned ? PJ.Obj(("type", "playlist"), ("id", $"0:{id}"), ("locator", locator)) :
            playable ? PJ.Obj(("type", "playlist"), ("id", global)) : PJ.Obj(("type", "unsupported"));
        return PJ.Obj(("id", $"{type}:{id}"), ("name", name), ("cover", PJ.Image(PJ.First(r["cover"], r["imgurl"]))),
            ("count", count), ("source", "kg"), ("engine", "Kugou"), ("ownerId", owner),
            ("relationship", type == 0 && owner == uid && source != 2 ? "owned" : "subscribed"),
            ("kind", favorite ? "favorites" : "playlist"),
            ("locator", locator), ("action", action),
            ("available", owned || playable), ("errorCode", owned || playable ? "" : "UNSUPPORTED"));
    }
    private static async Task<(List<JsonObject> Items, long? Total, bool More, string? Snapshot, JsonObject? Cursor)> TxPersonal(
        Call c, MusicPersonalPlaylistsRequest p, string uid, JsonObject previous)
    {
        var cookies = PJ.Cookies(c.Session);
        var euin = PJ.Cookie(cookies, "encrypt_uin", PJ.Cookie(cookies, "encryptUin"));
        if (cookies.TryGetValue("encrypt_uin", out var a) && cookies.TryGetValue("encryptUin", out var b))
            PJ.Require(a == b, "INVALID_CREDENTIALS");
        var created = new List<JsonObject>();
        if (p.Kind != MusicPersonalListKind.Subscribed)
        {
            var data = await c.Tx("music.musicasset.PlaylistBaseRead", "GetPlaylistByUin", PJ.Obj(("uin", uid)));
            var raw = PJ.RowsAll(data["v_playlist"]);
            PJ.Require(PJ.Bool(data["bFinish"]) && PJ.Count(data["total"]) == raw.Count, "INCOMPLETE_PAGE");
            var favorites = PrivateLocator(c, "qq-favorites", "201", null, null, "我喜欢");
            favorites["playlistId"] = "favorites"; favorites["dirId"] = "201";
            created.Add(PJ.Obj(("id", "favorites"), ("name", "我喜欢"), ("cover", ""), ("count", null),
                ("source", "tx"), ("engine", "QQ"), ("ownerId", uid), ("relationship", "owned"), ("kind", "favorites"),
                ("locator", favorites), ("action", PJ.Obj(("type", "playlist"), ("id", "favorites"), ("locator", favorites))), ("available", true)));
            foreach (var r in raw)
            {
                if (PJ.Scalar(r["dirid"]) == "201")
                {
                    PJ.Require(PJ.Id(r["uin"]) == uid && r["invalid"] is not null && !PJ.Bool(r["invalid"]), "IDENTITY_MISMATCH");
                    continue;
                }
                created.Add(TxPersonalItem(r, uid, true));
            }
            PJ.Require(created.Select(r => PJ.S(r, "id")).Distinct(StringComparer.Ordinal).Count() == created.Count, "IDENTITY_MISMATCH");
        }
        if (p.Kind == MusicPersonalListKind.Created)
        {
            var page = LocalPage(created, c, p);
            return (page.Item1, page.Item2, page.Item3, page.Item4, null);
        }
        PJ.Require(euin.Length > 0, "INVALID_CREDENTIALS");
        async Task<(List<JsonObject> Items, long Total, bool More)> FavoritePage(long offset, int size)
        {
            var data = await c.Tx("music.musicasset.PlaylistFavRead", "CgiGetPlaylistFavInfo", PJ.Obj(("uin", euin), ("offset", offset), ("size", size)));
            PJ.Require(data["hide"] is not null && !PJ.Bool(data["hide"]), "FORBIDDEN");
            PJ.Require(data["v_failTids"] is JsonArray failed && failed.Count == 0, "INCOMPLETE_PAGE");
            var total = PJ.Count(data["total"]); var more = PJ.Count(data["hasmore"]);
            PJ.Require(more is 0 or 1, "INVALID_RESPONSE");
            var raw = PJ.Rows(data["v_list"], size);
            PJ.Require(offset + raw.Count <= total && (more == 1 ? raw.Count > 0 && offset + raw.Count < total : offset + raw.Count == total), "INCOMPLETE_PAGE");
            return (raw.Select(r => TxPersonalItem(r, uid, false)).ToList(), total, more == 1);
        }
        if (p.Kind == MusicPersonalListKind.Subscribed)
        {
            var r = await FavoritePage((p.Page - 1L) * p.Limit, p.Limit);
            if (r.More) PJ.Require(r.Items.Count == p.Limit, "INCOMPLETE_PAGE");
            return (r.Items, r.Total, r.More, null, null);
        }
        // All is a streaming union, NOT an all-pages action owner. At most one favorite page per call.
        var snapshot = PJ.Hash(PJ.Obj(("scope", c.Scope), ("items", PJ.Arr(created))));
        if (previous["snapshot"] is not null) CheckSnapshot(PJ.S(previous, "snapshot"), snapshot);
        var phase = p.Page == 1 ? "created" : PJ.S(previous, "txPhase");
        PJ.Require(phase is "created" or "subscribed");
        var offset = p.Page == 1 ? 0 : PJ.Count(previous["txOffset"]);
        var beforeOffset = offset; var beforePhase = phase;
        var rawSeen = new HashSet<string>(StringComparer.Ordinal);
        if (previous["txSeen"] is JsonArray already)
            foreach (var value in already) PJ.Require(rawSeen.Add(PJ.Id(value)), "INVALID_REQUEST");
        else PJ.Require(p.Page == 1);
        long? remoteTotal = previous["txTotal"] is { } previousTotal ? PJ.Count(previousTotal) : null;
        var items = new List<JsonObject>();
        if (phase == "created")
        {
            PJ.Require(offset <= created.Count, "SNAPSHOT_CHANGED");
            items.AddRange(created.Skip((int)offset).Take(p.Limit));
            offset += items.Count;
            if (offset == created.Count) { phase = "subscribed"; offset = 0; }
        }
        var more = true;
        if (phase == "subscribed" && items.Count < p.Limit)
        {
            var page = await FavoritePage(offset, p.Limit - items.Count);
            if (remoteTotal is not null) PJ.Require(remoteTotal == page.Total, "SNAPSHOT_CHANGED");
            remoteTotal = page.Total;
            var owned = created.ToDictionary(i => PJ.S(i, "id"), StringComparer.Ordinal);
            foreach (var item in page.Items)
            {
                var id = PJ.S(item, "id");
                PJ.Require(rawSeen.Add(id), "PAGINATION_STALLED");
                if (owned.TryGetValue(id, out var own)) PJ.Require(PJ.S(item, "ownerId") == PJ.S(own, "ownerId"), "IDENTITY_MISMATCH");
                else items.Add(item);
            }
            offset += page.Items.Count;
            more = page.More;
        }
        if (more) PJ.Require(phase != beforePhase || offset > beforeOffset, "PAGINATION_STALLED");
        var cursor = more ? PJ.Obj(("txPhase", phase), ("txOffset", offset), ("txTotal", remoteTotal), ("txSeen", rawSeen.ToArray())) : null;
        // A union's total cannot be inferred by adding backend totals because entries can overlap.
        long? total = more ? null : PJ.Array(previous["seen"]).LongCount() + items.Count;
        return (items, total, more, snapshot, cursor);
    }
    private static (List<JsonObject>, long?, bool, string?) LocalPage(List<JsonObject> all, Call c, MusicPersonalPlaylistsRequest p)
    {
        var snapshot = PJ.Hash(PJ.Obj(("scope", c.Scope), ("items", PJ.Arr(all))));
        var items = all.Skip((int)Math.Min(all.Count, (p.Page - 1L) * p.Limit)).Take(p.Limit).ToList();
        return (items, all.Count, p.Page * (long)p.Limit < all.Count, snapshot);
    }
    private static JsonObject TxPersonalItem(JsonObject raw, string uid, bool created)
    {
        var owner = PJ.Id(raw["uin"]); var dirId = PJ.Id(raw["dirid"], "^[1-9][0-9]{0,19}$");
        if (created) PJ.Require(owner == uid && raw["invalid"] is not null && !PJ.Bool(raw["invalid"]), "IDENTITY_MISMATCH");
        var card = PlatformSongs.Playlist("tx", raw);
        var favorite = created && dirId == "201";
        card["ownerId"] = owner; card["relationship"] = created ? "owned" : "subscribed"; card["kind"] = favorite ? "favorites" : "playlist";
        card["locator"] = PJ.Obj(("playlistId", card["id"]), ("dirId", dirId));
        card["action"] = favorite ? PJ.Obj(("type", "unsupported")) : PJ.Obj(("type", "playlist"), ("id", card["id"]));
        card["available"] = !favorite;
        if (favorite) card["errorCode"] = "UNSUPPORTED";
        return card;
    }
}
