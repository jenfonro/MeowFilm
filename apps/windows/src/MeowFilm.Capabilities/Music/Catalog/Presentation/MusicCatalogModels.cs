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
    public string DisplayLine => $"{Rank}  {Title} − {Artist}  {Movement}";
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
    bool IsCloudFavorites = false, bool IsManagedPlaylist = false, int? Count = null,
    string Label = "", MusicCatalogCoverReference? CoverRef = null, bool UsesSongDisplay = true, string ChannelKind = "",
    string ContextText = "", long? PlayCount = null, string CoverLabel = "") : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string title = Title, subtitle = Subtitle, cover = Cover;
    private string contextText = ContextText;
    private long? playCount = PlayCount;
    public string Title { get => UsesSongDisplay ? Song?.Title ?? title : title; init => title = value; }
    public string Subtitle { get => UsesSongDisplay ? Song?.Artist ?? subtitle : subtitle; init => subtitle = value; }
    public string Cover { get => UsesSongDisplay ? Song?.Cover ?? cover : cover; init => cover = value; }
    public string SongId => Song?.Id ?? "";
    public bool HasCover => !string.IsNullOrWhiteSpace(Cover);
    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        internal set { if (isSelected == value) return; isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public bool HasLabel => Label.Length > 0;
    public long? PlayCount { get => playCount; init => playCount = value; }
    public string ContextText { get => contextText; init => contextText = value; }
    public bool HasPlayCount => PlayCount.HasValue;
    public bool HasCoverLabel => CoverLabel.Length > 0;
    public string PlayCountText => PlayCount is not { } count ? "" : count >= 100_000_000 ?
        (count / 100_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "亿" : count >= 10_000 ?
        (count / 10_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "万" :
        count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Caption => !HasLabel ? Title : ContextText.Length == 0 ? Subtitle :
        Kind == "fm" ? $"从「{ContextText}」开启无限漫游" : ContextText;
    internal void FillCover(string value)
    {
        if (Cover.Length > 0 || value.Length == 0) return;
        cover = value;
        foreach (var name in new[] { nameof(Cover), nameof(HasCover), nameof(FirstPreviewCover) })
            PropertyChanged?.Invoke(this, new(name));
    }
    internal void FillCollectionMetadata(string name, string artwork, long? plays)
    {
        FillCover(artwork);
        if (!HasLabel && (IsPreviewEntry || Title.Length == 0) && name.Length > 0 && title != name)
        {
            title = name;
            foreach (var property in new[] { nameof(Title), nameof(Caption) })
                PropertyChanged?.Invoke(this, new(property));
        }
        if (HasLabel && contextText.Length == 0 && name.Length > 0 && name != Label)
        {
            contextText = name;
            PropertyChanged?.Invoke(this, new(nameof(ContextText)));
            PropertyChanged?.Invoke(this, new(nameof(Caption)));
        }
        if (plays.HasValue && plays != playCount)
        {
            playCount = plays;
            foreach (var property in new[] { nameof(PlayCount), nameof(HasPlayCount), nameof(PlayCountText) })
                PropertyChanged?.Invoke(this, new(property));
        }
    }
    internal void RefreshSong(JsonElement? value, long publication = -1)
    {
        if (Song is null || value is null || !Song.ApplyRepository(value, publication)) return;
        if (UsesSongDisplay)
        {
            PropertyChanged?.Invoke(this, new(nameof(Title)));
            PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
            PropertyChanged?.Invoke(this, new(nameof(Cover)));
            PropertyChanged?.Invoke(this, new(nameof(HasCover)));
            PropertyChanged?.Invoke(this, new(nameof(Caption)));
        }
        PropertyChanged?.Invoke(this, new(nameof(CanAlbum)));
    }
    private IReadOnlyList<MusicRankPreview>? initializedPreview;
    private bool isPreparing;
    public IReadOnlyList<MusicRankPreview> PreviewSongs => Preview is { Count: > 0 } ? Preview : initializedPreview ?? [];
    public IReadOnlyList<MusicRankPreview> TopPreviewSongs => PreviewSongs.Count <= 3 ? PreviewSongs : PreviewSongs.Take(3).ToArray();
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
        if (ReferenceEquals(initializedPreview, songs) || Preview is { Count: > 0 }) return;
        initializedPreview = songs;
        foreach (var name in new[] { nameof(PreviewSongs), nameof(TopPreviewSongs), nameof(PreviewTitles), nameof(HasPreview),
            nameof(FirstPreviewCover), nameof(SecondPreviewCover), nameof(ThirdPreviewCover),
            nameof(FirstPreviewIdentity), nameof(SecondPreviewIdentity), nameof(ThirdPreviewIdentity) })
            PropertyChanged?.Invoke(this, new(name));
    }
    internal void RefreshPreview(Func<string, JsonElement?> read, long publication = -1)
    {
        bool changed = false;
        foreach (var preview in PreviewSongs)
            if (preview.Song is { } song) changed |= song.ApplyRepository(read(song.Id), publication);
        if (changed)
            foreach (var name in new[] { nameof(PreviewSongs), nameof(TopPreviewSongs), nameof(PreviewTitles),
                nameof(FirstPreviewCover), nameof(SecondPreviewCover), nameof(ThirdPreviewCover),
                nameof(FirstPreviewIdentity), nameof(SecondPreviewIdentity), nameof(ThirdPreviewIdentity) })
                PropertyChanged?.Invoke(this, new(name));
    }
    public bool HasPreview => PreviewSongs.Count > 0;
    public bool HasCalendarDay => DayOfMonth.HasValue;
    public bool IsPreviewEntry => Presentation == MusicCatalogEntryPresentation.Preview;
    public bool CanRequestPreview => Display && IsPreviewEntry && CanOpen && Kind is "playlist" or "daily" or "album";
    public bool CanRequestFMArtwork => Display && CanOpen && Kind == "fm" && Cover.Length == 0;
    public bool CanPlay => DirectPlay && CanOpen && Kind is "song" or "playlist" or "daily" or "fm" or "similar" or "heartbeat" or "album";
    public bool CanOpen => Available && (Kind == "song" && Song is not null ||
        ActionId.Length > 0 && MusicCatalogDto.SupportsAction(Platform, Kind));
    public bool CanSavePlaylist => CanOpen && Kind == "playlist" && !IsManagedPlaylist && Locator is null;
    public bool CanFavorite => Available && Kind == "song" && Song is not null;
    public bool CanSimilar => CanFavorite && Recommendations.RecommendationJson.NativeId(Song!.Raw).Length > 0;
    public bool CanHeartbeatFavorites => CanOpen && Platform == "wy" && IsCloudFavorites;
    public string AlbumId => Song is { } song ? MusicCatalogDto.Text(SongJson.PrimaryInfo(song.Raw), "albumId") : "";
    public bool CanAlbum => CanFavorite && Platform == "wy" && AlbumId.Length > 0;
    public string Key => Platform + "/" + (StableKey.Length > 0 ? StableKey : Kind + "/" + Identity);
    public string ContentKey => Platform + "/" + Kind + "/" + Identity;
    private string Identity => MemberId.Length > 0 ? MemberId : ActionId.Length > 0 ? ActionId : Song?.Id ?? Id;
}
public sealed record MusicCatalogCoverReference(string Kind, string Id);
public sealed record MusicCatalogTab(string Id, string Title, IReadOnlyList<MusicCatalogCard> Items) : INotifyPropertyChanged
{
    private bool selected;
    public bool Selected => selected;
    public string ItemStatus => selected ? "已选中" : "";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        PropertyChanged?.Invoke(this, new(nameof(Selected)));
        PropertyChanged?.Invoke(this, new(nameof(ItemStatus)));
    }
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
    string Id = "", string Type = "", MusicCatalogCard? Action = null, string Layout = "",
    bool IsLoading = false, IReadOnlyList<MusicCatalogTab>? Tabs = null, string SelectedTab = "", string ErrorCode = "") : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private IReadOnlyList<MusicCatalogCard> items = Items;
    private string selectedTab = SelectedTab;
    public IReadOnlyList<MusicCatalogCard> Items { get => items; init => items = value; }
    public string SelectedTab { get => selectedTab; init => selectedTab = value; }
    public bool HasTabs => Tabs is { Count: > 0 };
    internal void SyncTabSelection()
    {
        if (Tabs is null) return;
        foreach (var tab in Tabs) tab.SetSelected(tab.Id == selectedTab);
    }
    internal void SelectTab(MusicCatalogTab tab)
    {
        if (Tabs?.Contains(tab) != true || selectedTab == tab.Id) return;
        items = tab.Items; selectedTab = tab.Id; HorizontalOffset = 0;
        SyncTabSelection();
        foreach (var name in new[] { nameof(Items), nameof(SelectedTab), nameof(HorizontalOffset) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public double HorizontalOffset { get; set; }
    public MusicCatalogSectionPresentation Presentation => Layout switch
    {
        "feature-rail" => MusicCatalogSectionPresentation.Entries,
        "banner" => MusicCatalogSectionPresentation.Banner,
        "artist-grid" => MusicCatalogSectionPresentation.Artists,
        "ranking" or "ranked-songs" or "vip-ranked-songs" => MusicCatalogSectionPresentation.RankingPreviews,
        "ranking-small" => MusicCatalogSectionPresentation.RankingCovers,
        "song-columns" or "song-list" or "vip-songs" => MusicCatalogSectionPresentation.Songs,
        _ => MusicCatalogSectionPresentation.Shelf
    };
    public bool IsSongSection => Presentation == MusicCatalogSectionPresentation.Songs;
    public bool IsArtistSection => Presentation == MusicCatalogSectionPresentation.Artists;
    public bool IsRankingPreviewSection => Presentation == MusicCatalogSectionPresentation.RankingPreviews;
    public bool IsRankingCoverSection => Presentation == MusicCatalogSectionPresentation.RankingCovers;
    public bool IsBannerSection => Presentation == MusicCatalogSectionPresentation.Banner;
    public bool IsEntrySection => Presentation == MusicCatalogSectionPresentation.Entries;
    public bool IsAlbumChart => Layout == "album-ranking";
    public bool IsSongShelfSection => Layout == "song-columns";
    public bool IsPlaylistSection => Layout is "playlist-rail" or "playlist-grid";
    public bool IsGridSection => Layout is "playlist-grid" or "channel-grid";
    public bool IsArtistUpdatesSection => Layout == "artist-updates";
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
    internal static JsonElement Field(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    internal static IEnumerable<JsonElement> Rows(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    internal static int? Number(JsonElement value, string key) =>
        Field(value, key) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int result) ? result : null;
    internal static long? LongNumber(JsonElement value, string key) =>
        Field(value, key) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out long result) ? result : null;
    internal static bool Boolean(JsonElement value, string key, bool fallback) => Field(value, key).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    private static JsonElement? Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.Clone() : null;
    private static SongRow? Song(JsonElement value) => Text(value, "songId").Length > 0 ? new(value.Clone()) : null;

    public static MusicCatalogCard[] SearchResults(JsonElement items, string kind) =>
        Rows(items).Select(item => SearchResult(item, kind)).ToArray();
    internal static MusicCatalogCard SearchResult(JsonElement item, string kind) => kind == "songs"
        ? SongCard(item, SongJson.Platform(item)) : Collection(item, Text(item, "source"));

    public static IReadOnlyList<MusicCatalogSection> Read(JsonElement reply, string platform, string operation,
        string title, string searchKind = "songs")
    {
        if (operation is "home" or "channel" or "artistActivity" or "chart" or "charts")
            return Rows(Field(reply, "sections")).Select(group =>
            {
                var heading = Text(group, "title");
                var action = Object(Field(group, "action"));
                var tabs = Rows(Field(group, "tabs")).Select(tab => new MusicCatalogTab(Text(tab, "id"),
                    Text(tab, "name"), Cards(Field(tab, "items"), platform))).ToArray();
                return new MusicCatalogSection(heading, Cards(Field(group, "items"), platform), Text(group, "id"),
                    Action: action is { } a ? new MusicCatalogCard("", heading, "", "", Text(a, "type"), platform,
                        ActionId: ActionId(a), Action: a, Locator: Object(Field(a, "locator")),
                        PageCode: Text(Field(a, "channel"), "pageCode"),
                        ChannelKind: Text(Field(a, "channel"), "channelKind"),
                        RequiresAccount: Boolean(group, "requiresAccount", false)) : null,
                    Layout: Text(group, "layout"), Tabs: tabs, SelectedTab: Text(group, "selectedTab"),
                    ErrorCode: Text(group, "errorCode"));
            }).ToArray();
        bool collections = operation is "personalPlaylists" or "playlists" || operation == "search" && searchKind == "playlists";
        var cards = Rows(Field(reply, "items")).Select(item => operation == "channels" ? Descriptor(item, platform) :
            collections ? Collection(item, platform) : SongCard(item, platform)).ToArray();
        return [new(title, cards, "items", Layout: operation == "channels" ? "channel-grid" : collections ? "playlist-rail" : "song-list")];
    }
    public static IReadOnlyList<MusicCatalogTag> Tags(JsonElement reply)
    {
        var tags = new List<MusicCatalogTag>();
        void Add(JsonElement value, string inheritedSelection)
        {
            var selected = Text(value, "selectedTag");
            if (selected.Length == 0) selected = inheritedSelection;
            foreach (var tag in Rows(Field(value, "tags")))
            {
                var id = Text(tag, "id");
                var row = new MusicCatalogTag(id, Text(tag, "name"), Text(tag, "group"),
                    Boolean(tag, "selected", false) || id == selected);
                if (!tags.Any(existing => existing.Group == row.Group && existing.Id == row.Id)) tags.Add(row);
            }
        }
        var rootSelection = Text(reply, "selectedTag");
        Add(reply, rootSelection);
        foreach (var section in Rows(Field(reply, "sections"))) Add(section, rootSelection);
        return tags;
    }
    private static MusicCatalogCard[] Cards(JsonElement items, string platform) =>
        Rows(items).Select(item => Card(item, platform)).ToArray();
    private static string ActionId(JsonElement action) => Text(action, "type") switch
    {
        "daily" or "fm" or "similar" or "heartbeat" => Text(action, "type"),
        "channel" => Text(Field(action, "channel"), "id"),
        _ => Text(action, "id")
    };
    private static MusicCatalogCard Card(JsonElement item, string platform)
    {
        var action = Field(item, "action");
        var reference = Field(item, "coverRef");
        return new(Text(item, "id"), Text(item, "title"), Text(item, "subtitle"), Text(item, "cover"),
            Text(item, "kind"), platform, Song(Field(item, "song")), ActionId(action),
            Text(Field(action, "channel"), "pageCode"), Boolean(item, "available", true), Text(item, "errorCode"),
            Text(item, "key"), Object(action), Object(Field(item, "locator")) ?? Object(Field(action, "locator")),
            Preview: Preview(item), UpdateFrequency: Text(item, "updateFrequency"), Presentation: EntryPresentation(item),
            DayOfMonth: Number(item, "dayOfMonth") is >= 1 and <= 31 ? Number(item, "dayOfMonth") : null, Decoration: Text(item, "decoration"),
            RequiresAccount: Boolean(item, "requiresAccount", false), Display: Boolean(item, "display", true),
            DirectPlay: Boolean(item, "canPlay", true), PlaybackSource: Object(Field(item, "playbackSource")),
            RankText: Text(item, "rankText"), SalesText: Text(item, "salesText"), Count: Number(item, "count"),
            IsManagedPlaylist: Text(item, "relationship") == "owned",
            Label: Text(item, "label"), ChannelKind: Text(Field(action, "channel"), "channelKind"),
            CoverRef: Text(reference, "kind") == "playlist" && Text(reference, "id").Length > 0
                ? new("playlist", Text(reference, "id")) : null, UsesSongDisplay: false,
            ContextText: Text(item, "contextText"), PlayCount: LongNumber(item, "playCount"), CoverLabel: Text(item, "coverLabel"));
    }
    private static MusicCatalogCard SongCard(JsonElement item, string platform) =>
        new(Text(item, "songId"), Text(item, "title"), Text(item, "artist"), Text(item, "cover_url"), "song", platform,
            Song(item), SongJson.NativeId(item), MemberId: SongJson.NativeId(item));
    internal static MusicCatalogCard Collection(JsonElement item, string platform) =>
        new(Text(item, "id"), Text(item, "name"), Text(item, "creator"), Text(item, "cover"), "playlist", platform,
            ActionId: Text(item, "id"), Locator: Object(Field(item, "locator")),
            RequiresAccount: Boolean(item, "requiresAccount", false), Display: Boolean(item, "display", true),
            IsCloudFavorites: platform == "wy" && Text(item, "kind") == "favorites" && Text(item, "relationship") == "owned",
            IsManagedPlaylist: Text(item, "relationship") == "owned", Count: Number(item, "count"), PlayCount: LongNumber(item, "playCount"));
    private static MusicCatalogCard Descriptor(JsonElement item, string platform) =>
        new(Text(item, "id"), Text(item, "name"), "", Text(item, "cover"), "channel", platform,
            ActionId: Text(item, "id"), PageCode: Text(item, "pageCode"), Placement: Text(item, "placement"),
            Available: Boolean(item, "available", true), Display: Boolean(item, "display", true),
            ChannelKind: Text(item, "channelKind"));
    private static MusicCatalogEntryPresentation EntryPresentation(JsonElement item) => Text(item, "presentation") switch
    {
        "preview" => MusicCatalogEntryPresentation.Preview,
        "overlay" => MusicCatalogEntryPresentation.Overlay,
        "animated" => MusicCatalogEntryPresentation.Animated,
        _ => MusicCatalogEntryPresentation.Static
    };
    private static MusicRankPreview[] Preview(JsonElement item) => Rows(Field(item, "preview")).Take(3)
        .Select(row => new MusicRankPreview(Text(row, "title"), Text(row, "artist"), Text(row, "cover"),
            Number(row, "rank") ?? 0, Number(row, "lastRank"), Song(Field(row, "song")))).ToArray();
    public static string CursorKey(JsonElement? value)
    {
        if (value is null) return "";
        // Progress cursor, not opaque scope/order fields that may change while stalled.
        foreach (var key in new[] { "cursor", "offset", "page", "before" })
            if (value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty(key, out var cursor)) return key + ":" + cursor.ToString();
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
