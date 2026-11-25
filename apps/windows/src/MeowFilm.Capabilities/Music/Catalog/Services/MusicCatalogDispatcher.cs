using System.Globalization;
using System.Text.Json;

namespace MeowFilm.Capabilities;

internal sealed record CatalogTargetParams(
    string? Id = null,
    JsonElement? Personal = null,
    string? PageCode = null,
    string? Tag = null,
    string? Kind = null,
    string? Branch = null,
    string? Mode = null,
    string? Submode = null,
    bool Preview = false,
    string? RuleId = null,
    string? TabTagId = null,
    string[]? SourceIds = null,
    string? SourceType = null,
    string? Entry = null,
    string? SongId = null,
    string? PlaylistId = null,
    string? SeedEntryId = null,
    bool Restore = false,
    bool? Continue = null,
    string? Period = null,
    int? Year = null)
{
    internal static readonly CatalogTargetParams Empty = new();

    internal string CanonicalKey() => string.Join('\0',
        Id ?? "",
        Personal is { } p ? p.GetRawText() : "",
        PageCode ?? "",
        Tag ?? "",
        Kind ?? "",
        Branch ?? "",
        Mode ?? "",
        Submode ?? "",
        Preview ? "1" : "0",
        RuleId ?? "",
        TabTagId ?? "",
        SourceIds is { Length: > 0 } s ? string.Join('\u001f', s) : "",
        SourceType ?? "",
        Entry ?? "",
        SongId ?? "",
        PlaylistId ?? "",
        SeedEntryId ?? "",
        Restore ? "1" : "0",
        Continue switch { true => "1", false => "0", null => "" },
        Period ?? "",
        Year?.ToString(CultureInfo.InvariantCulture) ?? "");
}

// In-process route arguments, never credentials or a generic RPC/URL.
internal sealed record CatalogTarget(string Operation, string Platform, string Title, CatalogTargetParams Params,
    bool ManagedPlaylist = false, string Artwork = "", string Subtitle = "", int? Count = null)
{
    public bool Private => Operation is "personalPlaylists" or "daily" or "fm" or "similar" or
        "heartbeat" || Params.Personal is not null;
    public bool Cacheable => Operation is not ("fm" or "similar" or "heartbeat");
    public int Limit => Operation == "personalPlaylists" ? 30 :
        Params.Personal is { } p && MusicCatalogDto.Text(p, "kind") == "kugou-owned" ? 30 : 100;
}

/// <summary>Pure non-UI catalog operation dispatcher and section/route helpers.</summary>
internal static class MusicCatalogDispatcher
{
    internal static readonly MusicCatalogChoice[] FmScenes =
    [
        new("NIGHT_EMO", "伤感"), new("EXERCISE", "运动"), new("SLEEP_HELP", "助眠"), new("RELAX", "放松"),
        new("CHEERFUL", "欢快"), new("LYRICAL", "抒情"), new("CURE", "治愈"), new("FOCUS", "专注"),
        new("SWEET", "情歌"), new("RHYTHM_BLUES", "R&B"), new("RAINY", "下雨天"), new("GAMES", "打游戏"),
        new("RAP", "说唱"), new("K_POP", "K-Pop"), new("ORIGINAL_MUSICIAL", "宝藏原创"), new("ELECTRONIC", "电音"),
        new("COMMUTE", "出行"), new("TAKE_SHOWER", "洗澡"), new("COFFEE_SHOP", "咖啡馆"), new("ROCK", "摇滚"),
        new("INSPIRATIONAL", "励志"), new("CHINESE", "华语"), new("ENGLISH", "欧美"), new("YUEYU", "粤语"),
        new("MANYAO", "慢摇DJ"), new("JINGDIAN", "经典"), new("LIGHT", "轻音乐"), new("GUOFENG", "国风"),
        new("FOLK", "民谣"), new("ACG", "二次元"), new("GUDIAN", "古典"), new("JAZZ", "爵士"),
        new("JAPANESE", "日语"), new("GLOBAL", "全球"), new("FRANCH", "法语"), new("BLUE", "蓝调"),
        new("DANCE", "舞蹈"), new("LATIN", "拉丁"), new("PUNK", "放克"), new("COUNTRY", "乡村乐"),
        new("MUSICAL", "音乐剧"), new("YINGSHI", "影视"), new("HOT_SONG", "热歌")
    ];
    internal static bool IsCatalogSourceRoute(string route) =>
        route is "catalog-home" or "catalog-channels" || route.StartsWith("catalog-channel/", StringComparison.Ordinal);

    internal static bool IsCatalogRoute(string route) =>
        route is "catalog-home" or "catalog-channels" or "catalog-personal" or "catalog-daily" or "catalog-fm" or "catalog-search" ||
        new[] { "playlist", "channel", "chart", "album", "artist", "daily", "fm", "similar", "heartbeat" }
            .Any(kind => route.StartsWith("catalog-" + kind + "/", StringComparison.Ordinal));

    internal static CatalogTarget Target(string operation, string platform, string title, CatalogTargetParams? parameters = null) =>
        new(operation, platform, title, parameters ?? CatalogTargetParams.Empty);

    internal static string CardDailyBranch(MusicCatalogCard card) =>
        card.Action is { } action && MusicCatalogDto.Text(action, "branch") == "style" ? "style" : "default";

    internal static Dictionary<string, object?> CardReadParameters(MusicCatalogCard card)
    {
        var parameters = new Dictionary<string, object?> { ["platform"] = card.Platform };
        if (card.Kind == "daily") parameters["branch"] = CardDailyBranch(card);
        else if (card.Kind == "fm")
        {
            parameters["preview"] = true;
            if (card.Action is { } action)
                foreach (var name in new[] { "mode", "submode", "ruleId", "tabTagId" })
                    if (MusicCatalogDto.Text(action, name) is { Length: > 0 } value) parameters[name] = value;
        }
        else
        {
            parameters["id"] = card.ActionId;
            if (card.Kind == "playlist") { parameters["page"] = 1; parameters["limit"] = 3; }
            if (card.Locator is { } locator) parameters["personal"] = locator;
        }
        return parameters;
    }

    internal static string PreviewReadKey(MusicCatalogCard card) =>
        card.Kind + ":" + JsonSerializer.Serialize(CardReadParameters(card));

    internal static string CollectionReadKey(MusicCatalogCard card) =>
        card.CoverRef is { Kind: "playlist" } reference ? reference.Id : "";

    internal static Task<JsonElement> InvokeCardReadAsync(
        MusicNativeApplication application, MusicCatalogCard card, bool previewLimit, CancellationToken token)
    {
        if (card.Kind == "daily")
            return application.GetCatalogDailyAsync(card.Platform, CardDailyBranch(card), refresh: false, ct: token);
        if (card.Kind == "fm")
        {
            string mode = "default", submode = "";
            string? ruleId = null, tabTagId = null;
            if (card.Action is { } action)
            {
                if (MusicCatalogDto.Text(action, "mode") is { Length: > 0 } m) mode = m;
                if (MusicCatalogDto.Text(action, "submode") is { Length: > 0 } s) submode = s;
                if (MusicCatalogDto.Text(action, "ruleId") is { Length: > 0 } r) ruleId = r;
                if (MusicCatalogDto.Text(action, "tabTagId") is { Length: > 0 } t) tabTagId = t;
            }
            return application.GetCatalogFmAsync(card.Platform, mode, submode, preview: true, ruleId: ruleId, tabTagId: tabTagId, ct: token);
        }
        if (card.Kind == "playlist")
            return application.GetCatalogPlaylistAsync(card.Platform, card.ActionId, page: 1,
                limit: previewLimit ? 3 : 100, refresh: false, personal: card.Locator, continuation: null, ct: token);
        if (card.Kind == "album")
            return application.GetCatalogAlbumAsync(card.Platform, card.ActionId, refresh: false, ct: token);
        throw new MusicOperationException("INVALID_REQUEST");
    }

    internal static Task<JsonElement> InvokeOperationAsync(
        MusicNativeApplication application,
        string operation,
        string platform,
        CatalogTarget? target,
        string? routeId,
        string tag,
        string pageCode,
        int page,
        JsonElement? continuation,
        bool refresh,
        CancellationToken token)
    {
        var p = target?.Params ?? CatalogTargetParams.Empty;
        static string Str(string? value, string fallback = "") =>
            value is { Length: > 0 } ? value : fallback;
        static string? OptStr(string? value) =>
            value is { Length: > 0 } ? value : null;

        return operation switch
        {
            "home" => application.GetCatalogHomeAsync(platform, continuation, refresh, token),
            "channels" => application.GetCatalogChannelsAsync(platform, refresh, token),
            "channel" => application.GetCatalogChannelAsync(
                platform,
                routeId ?? Str(p.Id),
                pageCode.Length > 0 ? pageCode : OptStr(p.PageCode),
                continuation,
                tag.Length > 0 ? tag : OptStr(p.Tag),
                refresh,
                token),
            "playlist" => application.GetCatalogPlaylistAsync(
                platform,
                routeId ?? Str(p.Id),
                page,
                target?.Limit ?? 100,
                refresh,
                p.Personal is { ValueKind: JsonValueKind.Object } personal ? personal.Clone() : null,
                continuation,
                token),
            "personalPlaylists" => application.GetCatalogPersonalPlaylistsAsync(
                platform,
                Str(p.Kind, "all"),
                page,
                target?.Limit ?? 30,
                continuation,
                refresh,
                token),
            "daily" => application.GetCatalogDailyAsync(platform, Str(p.Branch, "default"), refresh, token),
            "fm" => application.GetCatalogFmAsync(
                platform,
                Str(p.Mode, "default"),
                Str(p.Submode, ""),
                p.Preview,
                OptStr(p.RuleId),
                OptStr(p.TabTagId),
                token),
            "similar" => application.GetCatalogSimilarAsync(
                platform,
                p.SourceIds ?? [],
                Str(p.SourceType),
                token),
            "heartbeat" => application.GetCatalogHeartbeatAsync(
                platform,
                Str(p.Entry, "favorites"),
                OptStr(p.SongId),
                OptStr(p.PlaylistId),
                token),
            "album" => application.GetCatalogAlbumAsync(platform, routeId ?? Str(p.Id), refresh, token),
            "chart" => application.GetCatalogChartAsync(
                platform,
                Str(p.Kind),
                Str(p.Period, "daily"),
                p.Year,
                refresh,
                token),
            "artist" => application.GetCatalogArtistAsync(platform, routeId ?? Str(p.Id), refresh, token),
            _ => throw new MusicOperationException("INVALID_REQUEST")
        };
    }

    internal static MusicCatalogSection[] MergeSections(
        IEnumerable<MusicCatalogSection> existingSections, MusicCatalogSection[] incoming)
    {
        var result = existingSections.ToList();
        foreach (var section in incoming)
        {
            var index = result.FindIndex(s => s.Id == section.Id);
            if (index < 0) result.Add(section);
            else
            {
                var existing = result[index];
                var merged = section with { Items = existing.Items.Concat(section.Items).ToArray() };
                merged.HorizontalOffset = existing.HorizontalOffset;
                result[index] = merged;
            }
        }
        return result.ToArray();
    }

    internal static long? CatalogExpiry(JsonElement reply)
    {
        var expires = MusicCatalogDto.Field(reply, "expiresAt");
        return expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var value) ? value : null;
    }

    internal static JsonElement DailySelectionSnapshot(JsonElement reply) => JsonSerializer.SerializeToElement(new
    {
        category = MusicCatalogDto.Text(reply, "category"),
        tags = MusicCatalogDto.Rows(MusicCatalogDto.Field(reply, "tags")).ToArray()
    });
}
