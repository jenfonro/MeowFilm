using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed record MusicCatalogPlatform(string Key, string Name);
public sealed record MusicCatalogTag(string Id, string Name, string Group = "", bool Selected = false);
public sealed record MusicCatalogTagGroup(string Id, IReadOnlyList<MusicCatalogTag> Items);
public sealed record MusicCatalogChoice(string Key, string Name);
public sealed record MusicRankPreview(string Title, string Artist, string Cover, int Rank, int LastRank)
{
    public string Movement => LastRank == 0 ? "新" : LastRank > Rank ? "↑" : LastRank < Rank ? "↓" : "−";
}
public enum MusicCatalogEntryPresentation
{
    Preview,
    Overlay,
    Animated,
    Static
}
public sealed record MusicCatalogCard(string Id, string Title, string Subtitle, string Cover,
    string Kind, string Platform, SongRow? Song = null, string ActionId = "", string PageCode = "",
    bool Available = true, string ErrorCode = "", string StableKey = "", JsonElement? Action = null,
    JsonElement? Locator = null, string MemberId = "", IReadOnlyList<MusicRankPreview>? Preview = null,
    string UpdateFrequency = "", MusicCatalogEntryPresentation Presentation = MusicCatalogEntryPresentation.Static,
    string PreviewSubtitle = "", int? DayOfMonth = null)
{
    public IReadOnlyList<MusicRankPreview> PreviewSongs => Preview ?? [];
    public bool HasPreview => PreviewSongs.Count > 0;
    public string ExpandedSubtitle => PreviewSubtitle.Length > 0 ? PreviewSubtitle : Subtitle;
    public bool HasCalendarDay => DayOfMonth.HasValue;
    public bool IsPreviewEntry => Presentation == MusicCatalogEntryPresentation.Preview;
    public bool IsOverlayEntry => Presentation == MusicCatalogEntryPresentation.Overlay;
    public bool IsAnimatedEntry => Presentation == MusicCatalogEntryPresentation.Animated;
    public bool CanRequestPreview => IsPreviewEntry && CanOpen;
    public bool CanPlay => CanOpen && (Song is not null || Kind is "song" or "playlist" or "daily" or "fm" or "similar" or "album");
    public bool CanOpen => Available && (Song is not null || MusicCatalogDto.SupportsAction(Platform, Kind));
    public bool CanFavorite => Available && Song is not null;
    public bool CanSimilar => CanFavorite && Platform == "wy" && ActionId.Length > 0;
    public string AlbumId => Song is { } song && song.Raw.TryGetProperty("source_data", out var source)
        ? MusicCatalogDto.Text(source, "albumId") : "";
    public bool CanAlbum => CanFavorite && Platform == "wy" && AlbumId.Length > 0;
    public string ErrorText => Available ? "" : MusicCatalogDto.ErrorText(ErrorCode);
    public bool RequiresLogin => !Available && ErrorCode is "AUTH_REQUIRED" or "AUTH_EXPIRED";
    public string AccessHint => ErrorText.Length > 0 ? ErrorText : Title;
    public string Key => Platform + "/" + (StableKey.Length > 0 ? StableKey : Kind + "/" + Identity);
    public string ContentKey => Platform + "/" + Kind + "/" + Identity;
    private string Identity => MemberId.Length > 0 ? MemberId : ActionId.Length > 0 ? ActionId : Song?.Id ?? Id;
}
public enum MusicCatalogSectionPresentation
{
    Shelf,
    Entries,
    Banner,
    Songs,
    Artists,
    RankingCovers,
    RankingPreviews
}
public sealed record MusicCatalogSection(string Title, IReadOnlyList<MusicCatalogCard> Items,
    string Id = "", string Type = "", MusicCatalogCard? Action = null, string Layout = "")
{
    public double HorizontalOffset { get; set; }
    public MusicCatalogSectionPresentation Presentation => Type switch
    {
        "entries" => MusicCatalogSectionPresentation.Entries,
        "banner" => MusicCatalogSectionPresentation.Banner,
        "artists" => MusicCatalogSectionPresentation.Artists,
        "rankings" when Layout is "ranked-songs" or "vip-ranked-songs" ||
            !Layout.Equals("ONLY_COVER", StringComparison.OrdinalIgnoreCase) && Items.Any(item => item.HasPreview) =>
            MusicCatalogSectionPresentation.RankingPreviews,
        "rankings" => MusicCatalogSectionPresentation.RankingCovers,
        _ when Items.Count > 0 && Items.All(item => item.Song is not null) => MusicCatalogSectionPresentation.Songs,
        _ => MusicCatalogSectionPresentation.Shelf
    };
    public bool IsSongSection => Presentation == MusicCatalogSectionPresentation.Songs;
    public bool IsArtistSection => Presentation == MusicCatalogSectionPresentation.Artists;
    public bool IsRankingPreviewSection => Presentation == MusicCatalogSectionPresentation.RankingPreviews;
    public bool IsRankingCoverSection => Presentation == MusicCatalogSectionPresentation.RankingCovers;
    public bool IsBannerSection => Presentation == MusicCatalogSectionPresentation.Banner;
    public bool IsEntrySection => Presentation == MusicCatalogSectionPresentation.Entries;
    public bool IsPlaylistSection => Presentation == MusicCatalogSectionPresentation.Shelf &&
        Items.Count > 0 && Items.All(item => item.Kind is "playlist" or "album");
    public MusicCatalogSection WithoutLoginOnlyCards() => this with
    {
        Items = Items.Where(item => !item.RequiresLogin).ToArray(),
        Action = Action?.RequiresLogin == true ? null : Action
    };
}

// Provider DTOs are not generic resources. Domain songId and upstream action ID
// remain distinct, including on nested carousel / entry song cards.
public static class MusicCatalogDto
{
    private static readonly HashSet<string> ChannelKinds =
        ["featured", "cube", "playlists", "rankings", "new-songs", "artists", "vip"];
    public static bool SupportsAction(string platform, string kind)
    {
        if (platform is not ("wy" or "tx" or "kg" or "kw" or "mg")) return false;
        return kind is "song" or "playlist" ||
            (platform is "wy" or "tx" or "kg") && kind == "channel" ||
            platform == "wy" && kind is "daily" or "fm" or "similar" or "album" or "artist";
    }
    public static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    public static string SongNativeId(JsonElement song)
    {
        if (!song.TryGetProperty("source_data", out var source) || source.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "musicId", "songmid", "id" })
            if (Text(source, key) is { Length: > 0 } value) return value;
        return "";
    }

    public static MusicCatalogCard[] Aggregate(JsonElement items, string kind)
    {
        MusicDto.Require(kind is "songs" or "playlists" && items.ValueKind == JsonValueKind.Array &&
            items.GetArrayLength() <= 10000);
        return items.EnumerateArray().Select(item =>
        {
            string platform = kind == "songs" && item.TryGetProperty("source_data", out var source)
                ? Text(source, "source") : Text(item, "source");
            MusicDto.Require(platform is "wy" or "tx" or "kw" or "kg" or "mg");
            return Card(item, platform, kind == "songs" ? "song" : "playlist");
        }).ToArray();
    }

    public static IReadOnlyList<MusicCatalogSection> Read(JsonElement reply, string platform, string operation,
        string title, string searchKind = "songs")
    {
        MusicDto.Require(Text(reply, "platform") == platform);
        var sections = new List<MusicCatalogSection>();
        if (reply.TryGetProperty("sections", out var groups))
        {
            MusicDto.Require(groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() <= 100);
            foreach (var group in groups.EnumerateArray())
            {
                var heading = Text(group, "title");
                MusicCatalogCard? action = null;
                if (group.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object)
                    action = Card(JsonSerializer.SerializeToElement(new { name = heading, action = a }), platform, "resource");
                sections.Add(new(heading, Cards(group.GetProperty("items"), platform, "resource"),
                    MusicDto.Text(group, "id"), Text(group, "type"), action, Text(group, "layout")));
            }
        }
        if (reply.TryGetProperty("entries", out var entries))
        {
            var cards = Cards(entries, platform, "resource", requirePresentation: true);
            if (cards.Length > 0) sections.Insert(0, new("", cards, "entries", "entries"));
        }
        if (reply.TryGetProperty("items", out var items))
        {
            var cards = Cards(items, platform, operation == "channels" ? "descriptor" :
                operation == "personalPlaylists" || operation == "search" && searchKind == "playlists" ? "playlist" : "song");
            if (operation == "playlist" && reply.TryGetProperty("memberIds", out var members))
            {
                MusicDto.Require(members.ValueKind == JsonValueKind.Array && members.GetArrayLength() == cards.Length);
                var ids = members.EnumerateArray().ToArray();
                for (int i = 0; i < cards.Length; i++)
                {
                    MusicDto.Require(ids[i].ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ids[i].GetString()));
                    var id = ids[i].GetString()!;
                    MusicDto.Require(cards[i].MemberId.Length == 0 || cards[i].MemberId == id);
                    cards[i] = cards[i] with { MemberId = id };
                }
            }
            sections.Add(new(title, cards, "items", operation));
        }
        return sections;
    }
    public static IReadOnlyList<MusicCatalogTag> Tags(JsonElement reply)
    {
        var tags = new List<MusicCatalogTag>();
        void Add(JsonElement value, string inheritedSelection)
        {
            if (!value.TryGetProperty("tags", out var list)) return;
            MusicDto.Require(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= 100);
            var selected = Text(value, "selectedTag");
            if (selected.Length == 0) selected = inheritedSelection;
            foreach (var tag in list.EnumerateArray())
            {
                var id = MusicDto.Text(tag, "id");
                var group = "";
                if (tag.TryGetProperty("group", out var declaredGroup))
                {
                    MusicDto.Require(declaredGroup.ValueKind == JsonValueKind.String);
                    group = declaredGroup.GetString()!;
                }
                var row = new MusicCatalogTag(id, MusicDto.Text(tag, "name"), group,
                    tag.TryGetProperty("selected", out var active) && active.ValueKind == JsonValueKind.True || id == selected);
                if (!tags.Any(existing => existing.Group == row.Group && existing.Id == row.Id)) tags.Add(row);
            }
        }
        var rootSelection = Text(reply, "selectedTag");
        Add(reply, rootSelection);
        if (reply.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.Object) Add(feed, rootSelection);
        return tags;
    }
    private static MusicCatalogCard[] Cards(JsonElement items, string platform, string fallback,
        bool requirePresentation = false)
    {
        MusicDto.Require(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 1000);
        return items.EnumerateArray().Select(item => Card(item, platform, fallback, requirePresentation)).ToArray();
    }
    private static MusicCatalogCard Card(JsonElement item, string platform, string fallback,
        bool requirePresentation = false)
    {
        MusicDto.Require(item.ValueKind == JsonValueKind.Object);
        var presentation = EntryPresentation(item, requirePresentation);
        var available = !item.TryGetProperty("available", out var enabled) || enabled.ValueKind == JsonValueKind.True;
        var error = Text(item, "errorCode");
        SongRow? song = null;
        if (item.TryGetProperty("songId", out _)) song = SongRow.Read(item);
        else if (item.TryGetProperty("song", out var nested) && nested.ValueKind == JsonValueKind.Object) song = SongRow.Read(nested);
        if (song is not null)
        {
            MusicDto.Require(song.Raw.TryGetProperty("source_data", out var source) && Text(source, "source") == platform);
            return new(song.Id, song.Title, song.Artist, song.Cover, "song", platform, song,
                SongNativeId(song.Raw), Available: available, ErrorCode: error, StableKey: Text(item, "key"),
                MemberId: Text(item, "memberId"), Presentation: presentation);
        }
        var id = Text(item, "id");
        var kind = Text(item, "kind");
        var actionId = id; var pageCode = Text(item, "pageCode");
        JsonElement? actionValue = null, locator = null;
        if (fallback == "descriptor")
        {
            available &= ChannelKinds.Contains(kind);
            kind = "channel";
        }
        else if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            actionValue = action.Clone();
            kind = Text(action, "type");
            actionId = Text(action, "id");
            if (kind == "channel" && action.TryGetProperty("channel", out var channel))
            {
                actionId = Text(channel, "id"); pageCode = Text(channel, "pageCode");
                var channelKind = Text(channel, "kind");
                available &= channelKind.Length == 0 || ChannelKinds.Contains(channelKind);
            }
            available &= SupportsAction(platform, kind);
            if (kind is "daily" or "fm" or "similar") actionId = kind;
            if (action.TryGetProperty("locator", out var value) && value.ValueKind == JsonValueKind.Object)
                locator = value.Clone();
        }
        else
        {
            if (kind.Length == 0) kind = fallback;
            available &= kind is "playlist" or "artist"; // Never guess that an unknown entry is a channel.
        }
        if (locator is null && item.TryGetProperty("locator", out var rawLocator) && rawLocator.ValueKind == JsonValueKind.Object)
            locator = rawLocator.Clone();
        // Public subscribed KG cards also have a locator, but no private scope/kind.
        // Never turn those into private list access by guessing a locator.
        if (locator is { } personal && Text(personal, "kind") is not ("qq-favorites" or "kugou-owned")) locator = null;
        available &= actionId.Length > 0;
        return new(id.Length == 0 ? actionId : id, Text(item, "name"),
            Text(item, "subtitle") is { Length: > 0 } subtitle ? subtitle : Text(item, "creator"),
            Text(item, "cover"), kind, platform, ActionId: actionId, PageCode: pageCode,
            Available: available, ErrorCode: available ? "" : error.Length == 0 ? "UNSUPPORTED" : error, StableKey: Text(item, "key"),
            Action: actionValue, Locator: locator, Preview: Preview(item), UpdateFrequency: Text(item, "updateFrequency"),
            Presentation: presentation, PreviewSubtitle: Text(item, "previewSubtitle"), DayOfMonth: CalendarDay(item));
    }
    private static int? CalendarDay(JsonElement item)
    {
        if (!item.TryGetProperty("dayOfMonth", out var day) || day.ValueKind == JsonValueKind.Null) return null;
        MusicDto.Require(day.ValueKind == JsonValueKind.Number && day.TryGetInt32(out var value) && value is >= 1 and <= 31);
        return day.GetInt32();
    }
    private static MusicCatalogEntryPresentation EntryPresentation(JsonElement item, bool required)
    {
        if (!required) return MusicCatalogEntryPresentation.Static;
        MusicDto.Require(item.TryGetProperty("presentation", out var value) && value.ValueKind == JsonValueKind.String);
        var presentation = value.GetString() switch
        {
            "preview" => MusicCatalogEntryPresentation.Preview,
            "overlay" => MusicCatalogEntryPresentation.Overlay,
            "animated" => MusicCatalogEntryPresentation.Animated,
            "static" => MusicCatalogEntryPresentation.Static,
            _ => (MusicCatalogEntryPresentation?)null
        };
        MusicDto.Require(presentation.HasValue);
        return presentation.Value;
    }
    private static MusicRankPreview[] Preview(JsonElement item)
    {
        if (!item.TryGetProperty("preview", out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        int Rank(JsonElement row, string key) => row.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
        return rows.EnumerateArray().Take(3).Select(row => new MusicRankPreview(Text(row, "title"), Text(row, "artist"),
            Text(row, "cover"), Rank(row, "rank"), Rank(row, "lastRank"))).ToArray();
    }
    public static string CursorKey(JsonElement? value)
    {
        if (value is null) return "";
        // Progress cursor, not opaque scope/order fields that may change while stalled.
        foreach (var key in new[] { "cursor", "offset", "page", "before" })
            if (value.Value.TryGetProperty(key, out var cursor)) return key + ":" + cursor.ToString();
        return Canonical(value.Value);
    }
    private static string Canonical(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", v.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", v.EnumerateArray().Select(Canonical)) + "]",
        _ => v.GetRawText()
    };
    public static string ErrorText(string code) => code switch
    {
        "AUTH_REQUIRED" or "AUTH_EXPIRED" => "当前内容需要平台登录",
        "FORBIDDEN" => "当前账号无权访问",
        "INVALID_CREDENTIALS" or "CREDENTIALS_UNAVAILABLE" => "平台凭据不可用",
        "VERIFICATION_REQUIRED" => "平台要求验证码验证",
        "SIGNATURE_REQUIRED" => "平台请求签名不可用",
        "DEVICE_CONTEXT_REQUIRED" or "DEVICE_REQUIRED" => "平台客户端状态不可用",
        "UNSUPPORTED" => "当前内容暂不支持",
        "STALE_SESSION" or "SESSION_CHANGED" => "平台会话已变更",
        "SNAPSHOT_CHANGED" or "REVISION_CONFLICT" => "内容已变更，请刷新",
        "PAGINATION_STALLED" => "分页未前进",
        "INCOMPLETE_PAGE" or "INCOMPLETE_PLAYLIST" => "歌单内容不完整",
        "PAGINATION_LIMIT" or "LIMIT" => "内容超过数量限制",
        "IDENTITY_MISMATCH" => "平台内容身份不一致",
        "INVALID_RESPONSE" or "INVALID_REQUEST" => "平台内容格式无效",
        "RATE_LIMITED" => "平台请求过于频繁",
        "NETWORK_ERROR" => "平台网络请求失败",
        "UPSTREAM_ERROR" => "平台服务暂不可用",
        "ORIGIN_DENIED" or "REDIRECT_DENIED" => "平台请求地址未获授权",
        "NOT_FOUND" => "内容不存在",
        "CANCELLED" => "已取消",
        "TIMEOUT" => "请求超时",
        "SEARCH_SETTINGS_UNCONFIRMED" => "搜索设置待核对",
        "SEARCH_SETTINGS_CHANGED" => "搜索设置已变更，请重新搜索",
        "SEARCH_NOT_FOUND" => "搜索结果已失效",
        "SEARCH_BUSY" => "正在结束上次搜索",
        "CLOSED" => "内容连接已关闭",
        "CATALOG_TOKEN_EXPIRED" => "歌单加载结果已失效",
        "CATALOG_CONTENT_EXPIRED" => "内容已过期，请刷新",
        "COMMIT_UNKNOWN" => "保存结果未确认",
        "PENDING_WRITE" => "保存未完成，请重试保存",
        "STORAGE" or "STORAGE_UNAVAILABLE" => "本地保存失败",
        "STORAGE_CONFLICT" => "本地内容已变更，请刷新",
        _ => "平台内容暂不可用"
    };
}
