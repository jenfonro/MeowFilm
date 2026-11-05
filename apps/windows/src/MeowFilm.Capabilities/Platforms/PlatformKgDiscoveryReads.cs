// SPDX-License-Identifier: GPL-3.0-only
// Fixed KuGouMusicApi discovery recipes; see LICENSES/NOTICE.md.
using System.Globalization;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

public sealed partial class MusicPlatformCatalog
{
    private static async Task<JsonObject> KgTags(Call c) =>
        PlatformKgDiscovery.Tags(await c.Kg("/pubsongs/v1/get_tags_by_type", new(), body:
            PJ.Obj(("tag_type", "collection"), ("tag_id", 0), ("source", 3))));

    private static async Task<JsonObject[]> KgRanks(Call c) =>
        PlatformKgDiscovery.Ranks(await c.Kg("/ocean/v6/rank/list",
            PJ.Obj(("plat", 2), ("withsong", 1), ("parentid", 0))));

    // Upstream sends the cookie userid string for an account and numeric 0 anonymously.
    private static object KgUserId(Call c) => (object?)c.Session?.UserId ?? 0;

    private static async Task<(JsonObject[] Items, bool More)> KgPlaylists(Call c, long page, string tag, int size)
    {
        string time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var (mid, _) = PlatformProtocols.KgIdentity(PJ.Cookies(c.Session), c.KgClient);
        var body = PJ.Obj(("appid", 1005), ("mid", mid), ("clientver", 20489), ("platform", "android"),
            ("clienttime", time), ("userid", KgUserId(c)), ("module_id", 1), ("page", page),
            ("pagesize", size), ("key", PlatformProtocols.Md5("1005" + PlatformProtocols.KgSalt + "20489" + time)),
            ("special_recommend", PJ.Obj(("withtag", 1), ("withsong", 1), ("sort", 1), ("ugc", 1),
                ("is_selected", 0), ("withrecommend", 1), ("area_code", 1),
                ("categoryid", PJ.Count(JsonValue.Create(tag))))),
            ("req_multi", 1), ("retrun_min", 5), ("return_special_falg", 1));
        return PlatformKgDiscovery.Playlists(await c.Kg("/v2/special_recommend", new(),
            "specialrec.service.kugou.com", body));
    }
    private static async Task<(JsonObject[] Items, long Total)> KgNewSongs(Call c, long page, int size) =>
        PlatformKgDiscovery.Songs(await c.Kg("/musicadservice/container/v1/newsong_publish", new(),
            body: PJ.Obj(("rank_id", 21608), ("userid", KgUserId(c)),
                ("page", page), ("pagesize", size), ("tags", new JsonArray()))), page, size);

    // A continuation only exists for a following page of the exact JSON integer contract.
    private static long KgPage(JsonObject previous)
    {
        if (previous.Count == 0) return 1;
        PJ.Require(long.TryParse(PJ.Scalar(previous["page"]), NumberStyles.None, CultureInfo.InvariantCulture, out var page) &&
            page is >= 2 and <= 9007199254740991);
        return page;
    }

    private static JsonObject KgPageResult(Call c, string target, long page, bool more,
        IEnumerable<JsonObject> sections, string tag = "", JsonObject? previous = null)
    {
        // Continuations use the catalog's exact JSON integer contract, not a page quota.
        PJ.Require(!more || page < 9007199254740991, "INVALID_RESPONSE");
        var result = PJ.Obj(("id", target), ("sections", PJ.Arr(sections)), ("more", more),
            ("continuation", more ? PJ.Obj(("page", page + 1), ("tag", tag)) : null));
        return BindDiscovery(result, c, target, previous);
    }
    private static async Task<JsonObject> KgHome(Call c, MusicHomeRequest request, string target = "kg:home")
    {
        var previous = Continuation(request.Continuation, c, target, "page", "tag");
        PJ.Require(previous["tag"] is null || PJ.S(previous, "tag") == "", "SNAPSHOT_CHANGED");
        long page = KgPage(previous);
        var sections = new List<JsonObject>();
        var playlists = await KgPlaylists(c, page, "0", 20);
        sections.Add(PlatformKgDiscovery.Section(target + ":playlists", "推荐歌单", "playlists", playlists.Items,
            PlatformKgDiscovery.ChannelAction("playlist")));
        if (page == 1)
        {
            var songs = await KgNewSongs(c, 1, 12);
            sections.Add(PlatformKgDiscovery.Section(target + ":new", "新歌", "songs", songs.Items,
                PlatformKgDiscovery.ChannelAction("newsong")));
            sections.Add(PlatformKgDiscovery.Section(target + ":ranks", "排行榜", "rankings", (await KgRanks(c)).Take(12),
                PlatformKgDiscovery.ChannelAction("chart")));
        }
        return KgPageResult(c, target, page, playlists.More, sections, previous: previous);
    }
    private static async Task<JsonObject> KgChannel(Call c, MusicChannelRequest request)
    {
        if (request.Id == "feature")
        {
            PJ.Require((request.PageCode is null or "") && (request.Tag is null or ""));
            var home = await KgHome(c, new("kg", request.Continuation, request.Refresh), "kg:channel:feature");
            home["title"] = "精选";
            return home;
        }
        string target = "kg:channel:" + request.Id;
        if (request.Id == "chart")
        {
            PJ.Require(request.Continuation is null && (request.Tag is null or "") && (request.PageCode is null or ""));
            var result = KgPageResult(c, target, 1, false,
                [PlatformKgDiscovery.Section(target, "排行榜", "rankings", await KgRanks(c))]);
            result["title"] = "排行榜";
            return result;
        }
        if (request.Id == "newsong" || request.Id.StartsWith("rank-", StringComparison.Ordinal))
            return await KgSongChannel(c, request, target);
        PJ.Require(request.Id == "playlist" || request.Id.StartsWith("category-", StringComparison.Ordinal), "NOT_FOUND");
        PJ.Require(request.PageCode is null or "");
        var categories = await KgTags(c);
        var groups = PJ.RowsAll(categories["groups"]);
        JsonObject? group = null;
        if (request.Id != "playlist")
        {
            string id = PJ.IdInput(request.Id["category-".Length..]);
            group = groups.FirstOrDefault(item => PJ.S(item, "id") == id);
            PJ.Require(group is not null, "NOT_FOUND");
        }
        IEnumerable<JsonObject> selectedGroups = group is null ? groups : [group];
        var tags = selectedGroups.SelectMany(item => PJ.RowsAll(item["tags"])).ToArray();
        var previous = Continuation(request.Continuation, c, target, "page", "tag");
        string selected = request.Tag ?? PJ.S(previous, "tag");
        if (selected.Length == 0) selected = group is null ? "0" : tags.FirstOrDefault() is { } first ? PJ.S(first, "id") : "0";
        PJ.IdInput(selected, "^(0|[1-9][0-9]{0,19})$");
        PJ.Require((selected == "0" && group is null) || tags.Any(item => PJ.S(item, "id") == selected), "INVALID_REQUEST");
        if (request.Continuation is not null) PJ.Require(PJ.S(previous, "tag") == selected, "SNAPSHOT_CHANGED");
        long page = KgPage(previous);
        var playlists = await KgPlaylists(c, page, selected, 30);
        string title = group is null ? "歌单广场" : PJ.S(group, "name");
        var resultPage = KgPageResult(c, target, page, playlists.More,
            [PlatformKgDiscovery.Section(target, title, "playlists", playlists.Items)], selected, previous);
        var publishedTags = new JsonArray();
        if (group is null) publishedTags.Add(PJ.Obj(("id", "0"), ("name", "推荐"), ("group", ""), ("selected", selected == "0")));
        foreach (var tag in tags)
        {
            var copy = (JsonObject)tag.DeepClone(); copy["selected"] = PJ.S(tag, "id") == selected;
            publishedTags.Add(copy);
        }
        resultPage["tags"] = publishedTags; resultPage["selectedTag"] = selected; resultPage["title"] = title;
        return resultPage;
    }
    private static async Task<JsonObject> KgSongChannel(Call c, MusicChannelRequest request, string target)
    {
        PJ.Require(request.Tag is null or "");
        var previous = Continuation(request.Continuation, c, target, "page", "tag", "rankCid", "total");
        PJ.Require(previous["tag"] is null || PJ.S(previous, "tag") == "", "SNAPSHOT_CHANGED");
        long page = KgPage(previous);
        const int size = 30;
        (JsonObject[] Items, long Total) songs;
        string title, cid = "";
        if (request.Id == "newsong")
        {
            PJ.Require(request.PageCode is null or "");
            PJ.Require(previous["rankCid"] is null || PJ.S(previous, "rankCid") == "", "SNAPSHOT_CHANGED");
            songs = await KgNewSongs(c, page, size); title = "新歌";
        }
        else
        {
            PJ.IdInput(request.Id["rank-".Length..]);
            var rank = (await KgRanks(c)).FirstOrDefault(card => PJ.S(card, "id") == request.Id);
            PJ.Require(rank is not null, "NOT_FOUND");
            cid = PJ.S(PJ.At(rank, "action", "channel"), "pageCode");
            if (request.PageCode is not null) PJ.Require(request.PageCode == cid, "IDENTITY_MISMATCH");
            if (request.Continuation is not null) PJ.Require(PJ.S(previous, "rankCid") == cid, "SNAPSHOT_CHANGED");
            title = PJ.S(rank, "name");
            songs = PlatformKgDiscovery.Songs(await c.KgRank(PJ.Obj(("show_portrait_mv", 1), ("show_type_total", 1),
                ("filter_original_remarks", 1), ("area_code", 1), ("pagesize", size), ("rank_cid", PJ.Count(JsonValue.Create(cid))),
                ("type", 1), ("page", page), ("rank_id", PJ.Count(JsonValue.Create(request.Id["rank-".Length..]))))),
                page, size, cid);
        }
        if (request.Continuation is not null)
            PJ.Require(PJ.Scalar(previous["total"]) == songs.Total.ToString(CultureInfo.InvariantCulture), "SNAPSHOT_CHANGED");
        var result = KgPageResult(c, target, page, page * size < songs.Total,
            [PlatformKgDiscovery.Section(target, title, "songs", songs.Items, layout: "list")], previous: previous);
        if (result["continuation"] is JsonObject next) { next["rankCid"] = cid; next["total"] = songs.Total; }
        result["title"] = title;
        return result;
    }
}
