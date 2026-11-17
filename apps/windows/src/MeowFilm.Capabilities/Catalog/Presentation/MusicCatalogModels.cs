using System.Text.Json;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

public sealed record MusicCatalogPlatform(string Key, string Name);
public sealed record MusicCatalogTag(string Id, string Name, string Group = "", bool Selected = false);
public sealed record MusicCatalogTagGroup(string Id, IReadOnlyList<MusicCatalogTag> Items);
public sealed record MusicCatalogChoice(string Key, string Name);
public sealed record MusicRankPreview(string Title, string Artist, string Cover, int Rank, int? LastRank, SongRow? Song = null)
{
    private string title = Title, artist = Artist, cover = Cover;
    public string Title { get => Song?.Title ?? title; init => title = value; }
    public string Artist { get => Song?.Artist ?? artist; init => artist = value; }
    public string Cover { get => Song?.Cover ?? cover; init => cover = value; }
    // A provider without previous-rank data publishes no movement; it is not a new entry.
    public string Movement => LastRank is not { } last ? "" : last == 0 ? "新" : last > Rank ? "↑" : last < Rank ? "↓" : "−";
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
    int? DayOfMonth = null, string Decoration = "",
    bool RequiresAccount = false, bool Display = true, string Placement = "",
    bool DirectPlay = true, JsonElement? PlaybackSource = null, string RankText = "", string SalesText = "",
    bool IsCloudFavorites = false, bool IsManagedPlaylist = false, int? Count = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string title = Title, subtitle = Subtitle, cover = Cover;
    public string Title { get => Song?.Title ?? title; init => title = value; }
    public string Subtitle { get => Song?.Artist ?? subtitle; init => subtitle = value; }
    public string Cover { get => Song?.Cover ?? cover; init => cover = value; }
    internal void RefreshSong(JsonElement? value)
    {
        if (Song is null || value is null) return;
        Song.ApplyRepository(value);
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(Cover), nameof(CanAlbum) })
            PropertyChanged?.Invoke(this, new(name));
    }
    private IReadOnlyList<MusicRankPreview>? initializedPreview;
    private bool isPreparing;
    public IReadOnlyList<MusicRankPreview> PreviewSongs => Preview is { Count: > 0 } ? Preview : initializedPreview ?? [];
    public IReadOnlyList<string> PreviewTitles => PreviewSongs.Take(3).Select((song, index) => $"{index + 1} {song.Title}").ToArray();
    public string FirstPreviewCover => PreviewSongs.ElementAtOrDefault(0)?.Cover is { Length: > 0 } cover ? cover : Cover;
    public string SecondPreviewCover => PreviewSongs.ElementAtOrDefault(1)?.Cover ?? "";
    public string ThirdPreviewCover => PreviewSongs.ElementAtOrDefault(2)?.Cover ?? "";
    public string FirstPreviewIdentity => PreviewSongs.ElementAtOrDefault(0) is { Cover.Length: > 0 } preview
        ? preview.Song?.Id ?? "" : Song?.Id ?? "";
    public string SecondPreviewIdentity => PreviewSongs.ElementAtOrDefault(1)?.Song?.Id ?? "";
    public string ThirdPreviewIdentity => PreviewSongs.ElementAtOrDefault(2)?.Song?.Id ?? "";
    public bool IsPreparing
    {
        get => isPreparing;
        internal set { if (isPreparing == value) return; isPreparing = value; PropertyChanged?.Invoke(this, new(nameof(IsPreparing))); }
    }
    internal void InitializePreview(IReadOnlyList<MusicRankPreview> songs)
    {
        initializedPreview = songs;
        foreach (var name in new[] { nameof(PreviewSongs), nameof(PreviewTitles), nameof(HasPreview),
            nameof(FirstPreviewCover), nameof(SecondPreviewCover), nameof(ThirdPreviewCover),
            nameof(FirstPreviewIdentity), nameof(SecondPreviewIdentity), nameof(ThirdPreviewIdentity) })
            PropertyChanged?.Invoke(this, new(name));
    }
    internal void RefreshPreview(Func<string, JsonElement?> read)
    {
        bool changed = false;
        foreach (var preview in PreviewSongs)
            if (preview.Song is { } song) changed |= song.ApplyRepository(read(song.Id));
        if (changed)
            foreach (var name in new[] { nameof(PreviewSongs), nameof(PreviewTitles),
                nameof(FirstPreviewCover), nameof(SecondPreviewCover), nameof(ThirdPreviewCover),
                nameof(FirstPreviewIdentity), nameof(SecondPreviewIdentity), nameof(ThirdPreviewIdentity) })
                PropertyChanged?.Invoke(this, new(name));
    }
    public bool HasPreview => PreviewSongs.Count > 0;
    public bool HasCalendarDay => DayOfMonth.HasValue;
    public bool IsPreviewEntry => Presentation == MusicCatalogEntryPresentation.Preview;
    public bool IsOverlayEntry => Presentation == MusicCatalogEntryPresentation.Overlay;
    public bool IsAnimatedEntry => Presentation == MusicCatalogEntryPresentation.Animated;
    public bool CanRequestPreview => Display && IsPreviewEntry && CanOpen && Kind is "playlist" or "daily" or "album";
    public bool CanPlay => DirectPlay && CanOpen && (Song is not null || Kind is "song" or "playlist" or "daily" or "fm" or "similar" or "heartbeat" or "album");
    public bool CanOpen => Available && (Song is not null || MusicCatalogDto.SupportsAction(Platform, Kind));
    public bool CanSavePlaylist => CanOpen && Kind == "playlist" && !IsManagedPlaylist && Locator is null;
    public bool CanFavorite => Available && Song is not null;
    public bool CanSimilar => CanFavorite && Recommendations.RecommendationJson.NativeId(Song!.Raw).Length > 0;
    public bool CanHeartbeatFavorites => CanOpen && Platform == "wy" && IsCloudFavorites;
    public string AlbumId => Song is { } song ? MusicCatalogDto.Text(SongJson.PrimaryInfo(song.Raw), "albumId") : "";
    public bool CanAlbum => CanFavorite && Platform == "wy" && AlbumId.Length > 0;
    public string Key => Platform + "/" + (StableKey.Length > 0 ? StableKey : Kind + "/" + Identity);
    public string ContentKey => Platform + "/" + Kind + "/" + Identity;
    private string Identity => MemberId.Length > 0 ? MemberId : ActionId.Length > 0 ? ActionId : Song?.Id ?? Id;
}
public sealed record MusicCatalogSongColumn(IReadOnlyList<MusicCatalogCard> Items);
public sealed record MusicCatalogPlaylistRow(IReadOnlyList<MusicCatalogCard> Items);
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
    string Id = "", string Type = "", MusicCatalogCard? Action = null, string Layout = "",
    bool IsLoading = false)
{
    public double HorizontalOffset { get; set; }
    public MusicCatalogSectionPresentation Presentation => Type switch
    {
        "entries" => MusicCatalogSectionPresentation.Entries,
        "banner" => MusicCatalogSectionPresentation.Banner,
        "artists" => MusicCatalogSectionPresentation.Artists,
        "rankings" when Layout is "ranked-songs" or "vip-ranked-songs" ||
            Layout.Equals("TOP_3", StringComparison.OrdinalIgnoreCase) =>
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
    public bool IsAlbumChart => Type == "albums" && Layout == "ranking";
    public bool IsSongShelfSection => IsSongSection && Layout is "rail" or "grid";
    public bool IsPlaylistSection => Presentation == MusicCatalogSectionPresentation.Shelf &&
        Items.Count > 0 && Items.All(item => item.Kind is "playlist" or "album");
    public IReadOnlyList<MusicCatalogSongColumn> SongColumns => Items
        .Chunk(3).Select(items => new MusicCatalogSongColumn(items)).ToArray();
    public MusicCatalogSection DisplayableCards(bool includeAccountEntries = true) => this with
    {
        Items = Items.Where(item => item.Display && (includeAccountEntries || !item.RequiresAccount)).ToArray(),
        Action = Action is { } action && (!action.Display || !includeAccountEntries && action.RequiresAccount) ? null : Action
    };
}

// Provider DTOs are not generic resources. Domain songId and upstream action ID
// remain distinct, including on nested carousel / entry song cards.
public static class MusicCatalogDto
{
    public static bool SupportsAction(string platform, string kind)
    {
        if (platform is not ("wy" or "tx" or "kg" or "kw" or "mg")) return false;
        return kind is "song" or "playlist" ||
            (platform is "wy" or "tx" or "kg") && kind == "channel" ||
            platform == "wy" && kind is "daily" or "fm" or "similar" or "heartbeat" or "album" or "artist" or "chart";
    }
    public static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    public static MusicCatalogCard[] SearchResults(JsonElement items, string kind)
    {
        MusicDto.Require(kind is "songs" or "playlists" && items.ValueKind == JsonValueKind.Array);
        return ReadItems(items, item => SearchResult(item, kind));
    }
    internal static MusicCatalogCard SearchResult(JsonElement item, string kind)
    {
        MusicDto.Require(kind is "songs" or "playlists");
        string platform = kind == "songs" ? SongJson.Platform(item) : Text(item, "source");
        MusicDto.Require(platform is "wy" or "tx" or "kw" or "kg" or "mg");
        return Card(item, platform, kind == "songs" ? "song" : "playlist");
    }

    public static IReadOnlyList<MusicCatalogSection> Read(JsonElement reply, string platform, string operation,
        string title, string searchKind = "songs")
    {
        var sections = new List<MusicCatalogSection>();
        MusicDto.Require(reply.ValueKind == JsonValueKind.Object);
        bool content = false, valid = false;
        if (reply.TryGetProperty("sections", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            content = true;
            if (groups.GetArrayLength() == 0) valid = true;
            foreach (var group in groups.EnumerateArray())
            {
                try
                {
                    MusicDto.Require(group.ValueKind == JsonValueKind.Object);
                    if (!Boolean(group, "display", true)) { valid = true; continue; }
                    var heading = Text(group, "title");
                    MusicCatalogCard? action = null;
                    if (group.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.Object)
                        try { action = Card(SectionActionItem(group, heading, a), platform, "resource"); }
                        catch (Exception error) when (IsItemFailure(error)) { }
                    var type = Text(group, "type");
                    sections.Add(new(heading, Cards(group.GetProperty("items"), platform, "resource"),
                        MusicDto.Text(group, "id"), type, action, Text(group, "layout")));
                    valid = true;
                }
                catch (Exception error) when (IsItemFailure(error)) { }
            }
        }
        if (reply.TryGetProperty("entries", out var entries))
        {
            content = true;
            try
            {
                var cards = Cards(entries, platform, "resource");
                if (cards.Length > 0) sections.Insert(0, new("", cards, "entries", "entries"));
                valid = true;
            }
            catch (Exception error) when (IsItemFailure(error)) { }
        }
        if (reply.TryGetProperty("items", out var items))
        {
            content = true;
            try
            {
                var cards = Cards(items, platform, operation == "channels" ? "descriptor" :
                    operation == "personalPlaylists" || operation == "search" && searchKind == "playlists" ? "playlist" : "song");
                sections.Add(new(title, cards, "items", operation));
                valid = true;
            }
            catch (Exception error) when (IsItemFailure(error)) { }
        }
        MusicDto.Require(content && valid);
        return sections;
    }
    private static JsonElement SectionActionItem(JsonElement group, string heading, JsonElement action)
    {
        var item = new Dictionary<string, object?>
        {
            ["name"] = heading,
            ["action"] = action.Clone()
        };
        foreach (var name in new[] { "available", "errorCode", "requiresAccount", "display" })
            if (group.TryGetProperty(name, out var value)) item[name] = value.Clone();
        return JsonSerializer.SerializeToElement(item);
    }
    public static IReadOnlyList<MusicCatalogTag> Tags(JsonElement reply)
    {
        var tags = new List<MusicCatalogTag>();
        void Add(JsonElement value, string inheritedSelection)
        {
            if (!value.TryGetProperty("tags", out var list)) return;
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
    private static bool IsItemFailure(Exception error) => error is MusicOperationException or JsonException or
        InvalidOperationException or KeyNotFoundException or FormatException or OverflowException;
    private static MusicCatalogCard[] Cards(JsonElement items, string platform, string fallback) =>
        ReadItems(items, item => Card(item, platform, fallback));
    private static MusicCatalogCard[] ReadItems(JsonElement items, Func<JsonElement, MusicCatalogCard> read)
    {
        MusicDto.Require(items.ValueKind == JsonValueKind.Array);
        var cards = new List<MusicCatalogCard>();
        int hidden = 0;
        foreach (var item in items.EnumerateArray())
        {
            try
            {
                MusicDto.Require(item.ValueKind == JsonValueKind.Object);
                if (!Boolean(item, "display", true)) { hidden++; continue; }
                cards.Add(read(item));
            }
            catch (Exception error) when (IsItemFailure(error)) { }
        }
        MusicDto.Require(items.GetArrayLength() == hidden || cards.Count > 0);
        return cards.ToArray();
    }
    private static MusicCatalogCard Card(JsonElement item, string platform, string fallback)
    {
        MusicDto.Require(item.ValueKind == JsonValueKind.Object);
        var presentation = EntryPresentation(item);
        var available = !item.TryGetProperty("available", out var enabled) || enabled.ValueKind == JsonValueKind.True;
        var error = Text(item, "errorCode");
        var requiresAccount = Boolean(item, "requiresAccount", false);
        var display = Boolean(item, "display", true);
        SongRow? song = null;
        if (fallback != "descriptor")
        {
            if (item.TryGetProperty("songId", out _)) song = SongRow.Read(item);
            else if (item.TryGetProperty("song", out var nested) && nested.ValueKind == JsonValueKind.Object) song = SongRow.Read(nested);
        }
        if (song is not null)
        {
            return new(song.Id, song.Title, song.Artist, song.Cover, "song", SongJson.Platform(song.Raw), song,
                SongJson.NativeId(song.Raw), Available: available, ErrorCode: error, StableKey: Text(item, "key"),
                MemberId: SongJson.NativeId(song.Raw), Presentation: presentation,
                PlaybackSource: item.TryGetProperty("playbackSource", out var playback) && playback.ValueKind == JsonValueKind.Object ? playback.Clone() : null,
                RequiresAccount: requiresAccount, Display: display);
        }
        var id = Text(item, "id");
        var kind = Text(item, "kind");
        bool cloudFavorites = platform == "wy" && kind == "favorites" && Text(item, "relationship") == "owned";
        var actionId = id; var pageCode = Text(item, "pageCode");
        var placement = "";
        JsonElement? actionValue = null, locator = null;
        if (fallback == "descriptor")
        {
            placement = Text(item, "placement");
        }
        else if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
        {
            actionValue = action.Clone();
            kind = Text(action, "type");
            actionId = Text(action, "id");
            if (kind == "channel" && action.TryGetProperty("channel", out var channel))
            {
                actionId = Text(channel, "id"); pageCode = Text(channel, "pageCode");
            }
            available &= SupportsAction(platform, kind);
            if (kind is "daily" or "fm" or "similar" or "heartbeat") actionId = kind;
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
        available &= actionId.Length > 0;
        return new(id.Length == 0 ? actionId : id, Text(item, "name"),
            Text(item, "subtitle") is { Length: > 0 } subtitle ? subtitle : Text(item, "creator"),
            Text(item, "cover"), kind, platform, ActionId: actionId, PageCode: pageCode,
            Available: available, ErrorCode: available ? "" : error.Length == 0 ? "UNSUPPORTED" : error, StableKey: Text(item, "key"),
            Action: actionValue, Locator: locator, Preview: Preview(item), UpdateFrequency: Text(item, "updateFrequency"),
            Presentation: presentation, DayOfMonth: CalendarDay(item),
            Decoration: Text(item, "decoration"), RequiresAccount: requiresAccount, Display: display, Placement: placement,
            DirectPlay: Boolean(item, "canPlay", true), RankText: Text(item, "rankText"), SalesText: Text(item, "salesText"),
            IsCloudFavorites: cloudFavorites, IsManagedPlaylist: Text(item, "relationship") == "owned",
            Count: item.TryGetProperty("count", out var count) && count.ValueKind != JsonValueKind.Null
                ? checked((int)MusicDto.Integer(item, "count")) : null);
    }
    private static bool Boolean(JsonElement item, string name, bool fallback)
    {
        if (!item.TryGetProperty(name, out var value)) return fallback;
        MusicDto.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return value.GetBoolean();
    }
    private static int? CalendarDay(JsonElement item)
    {
        if (!item.TryGetProperty("dayOfMonth", out var day) || day.ValueKind == JsonValueKind.Null) return null;
        long value = MusicDto.Integer(item, "dayOfMonth");
        MusicDto.Require(value is >= 1 and <= 31);
        return (int)value;
    }
    private static MusicCatalogEntryPresentation EntryPresentation(JsonElement item) =>
        Text(item, "presentation") switch
        {
            "preview" => MusicCatalogEntryPresentation.Preview,
            "overlay" => MusicCatalogEntryPresentation.Overlay,
            "animated" => MusicCatalogEntryPresentation.Animated,
            "static" => MusicCatalogEntryPresentation.Static,
            _ => MusicCatalogEntryPresentation.Static
        };
    private static MusicRankPreview[] Preview(JsonElement item)
    {
        if (!item.TryGetProperty("preview", out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        int Rank(JsonElement row, string key) => row.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
        return rows.EnumerateArray().Take(3).Select(row => new MusicRankPreview(Text(row, "title"), Text(row, "artist"),
            Text(row, "cover"), Rank(row, "rank"), row.TryGetProperty("lastRank", out _) ? Rank(row, "lastRank") : null,
            row.TryGetProperty("song", out var song) && song.ValueKind == JsonValueKind.Object ? SongRow.Read(song) : null)).ToArray();
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
        "STALE_SESSION" or "SESSION_CHANGED" => "平台会话已变更",
        "REVISION_CONFLICT" => "内容已变更，请刷新",
        "PAGINATION_STALLED" => "分页未前进",
        "INCOMPLETE_PAGE" => "歌单内容不完整",
        "LIMIT" => "内容超过资源限制",
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
