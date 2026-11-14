using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeowFilm.Capabilities;

internal sealed record FilmSectionDefinition(
    string Key,
    string Title,
    string Kind,
    string Category,
    string Type)
{
    internal bool Refresh { get; init; }
    internal static IReadOnlyList<FilmSectionDefinition> Home { get; } =
    [
        new("movie", "热门电影", "movie", "热门", "全部"),
        new("tv", "热门剧集", "tv", "tv", "tv"),
        new("anime", "新番放送", "tv", "tv", "tv_animation"),
        new("show", "热门综艺", "tv", "show", "show")
    ];
}

internal sealed record FilmFilterOptionDefinition(string Label, string Value);

internal sealed record FilmFilterRowDefinition(
    string Key,
    string Label,
    string DefaultValue,
    IReadOnlyList<FilmFilterOptionDefinition> Options);

internal static class FilmDataFilters
{
    private static readonly IReadOnlyList<FilmFilterRowDefinition> Movie =
    [
        new("category", "分类", "热门",
        [
            new("热门电影", "热门"),
            new("最新电影", "最新"),
            new("豆瓣高分", "豆瓣高分"),
            new("冷门佳片", "冷门佳片")
        ]),
        new("area", "地区", "全部",
        [
            new("全部", "全部"),
            new("华语", "华语"),
            new("欧美", "欧美"),
            new("韩国", "韩国"),
            new("日本", "日本")
        ])
    ];

    private static readonly IReadOnlyList<FilmFilterRowDefinition> Tv =
    [
        new("category", "分类", "all",
        [
            new("全部", "all"),
            new("动漫", "tv_animation"),
            new("纪录片", "tv_documentary")
        ]),
        new("area", "类型", "tv",
        [
            new("全部", "tv"),
            new("国产", "tv_domestic"),
            new("欧美", "tv_american"),
            new("日本", "tv_japanese"),
            new("韩国", "tv_korean")
        ])
    ];

    private static readonly IReadOnlyList<FilmFilterRowDefinition> Anime =
    [
        new("category", "分类", "tv_animation",
        [
            new("动漫", "tv_animation")
        ])
    ];

    private static readonly IReadOnlyList<FilmFilterRowDefinition> Show =
    [
        new("category", "分类", "all",
        [
            new("全部", "all")
        ]),
        new("area", "类型", "show",
        [
            new("全部", "show"),
            new("国内", "show_domestic"),
            new("国外", "show_foreign")
        ])
    ];

    internal static IReadOnlyList<FilmFilterRowDefinition> RowsFor(string key) => key switch
    {
        "movie" => Movie,
        "tv" => Tv,
        "anime" => Anime,
        "show" => Show,
        _ => throw new ArgumentException("Unknown film section.", nameof(key))
    };

    internal static FilmSectionDefinition BuildRequest(
        FilmSectionDefinition section,
        IEnumerable<FilmFilterRow> rows)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(rows);
        var values = rows.ToDictionary(row => row.Key, row => row.SelectedValue, StringComparer.Ordinal);
        string Read(string key) => values.TryGetValue(key, out var value)
            ? value
            : throw new ArgumentException("Missing film filter.", nameof(rows));

        return section.Key switch
        {
            "movie" => section with { Category = Read("category"), Type = Read("area") },
            "tv" => section with
            {
                Category = "tv",
                Type = Read("category") is { } category && category != "all"
                    ? category
                    : Read("area")
            },
            "anime" => section with { Category = "tv", Type = Read("category") },
            "show" => section with { Category = "show", Type = Read("area") },
            _ => throw new ArgumentException("Unknown film section.", nameof(section))
        };
    }

    internal static bool IsAllowedRequest(FilmSectionDefinition section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var home = FilmSectionDefinition.Home.SingleOrDefault(candidate =>
            candidate.Key == section.Key);
        if (home is null || home.Title != section.Title || home.Kind != section.Kind)
            return false;

        return section.Key switch
        {
            "movie" => Values(Movie, "category").Contains(section.Category) &&
                Values(Movie, "area").Contains(section.Type),
            "tv" => section.Category == "tv" &&
                (Values(Tv, "category").Where(value => value != "all")
                    .Concat(Values(Tv, "area"))).Contains(section.Type),
            "anime" => section.Category == "tv" && section.Type == "tv_animation",
            "show" => section.Category == "show" && Values(Show, "area").Contains(section.Type),
            _ => false
        };
    }

    private static IEnumerable<string> Values(
        IReadOnlyList<FilmFilterRowDefinition> rows,
        string key) =>
        rows.Single(row => row.Key == key).Options.Select(option => option.Value);
}

public sealed class FilmFilterOption : INotifyPropertyChanged
{
    private bool isSelected;

    internal FilmFilterOption(
        string filterKey,
        string filterLabel,
        FilmFilterOptionDefinition definition,
        bool isSelected)
    {
        FilterKey = filterKey;
        Label = definition.Label;
        Value = definition.Value;
        AutomationName = $"{filterLabel}：{Label}";
        this.isSelected = isSelected;
    }

    internal string FilterKey { get; }
    public string Label { get; }
    public string Value { get; }
    public string AutomationName { get; }
    public bool IsSelected => isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Select(bool selected)
    {
        if (isSelected == selected) return;
        isSelected = selected;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}

public sealed class FilmFilterRow
{
    internal FilmFilterRow(FilmFilterRowDefinition definition)
    {
        Key = definition.Key;
        Label = definition.Label;
        Options = definition.Options.Select(option => new FilmFilterOption(
            definition.Key,
            definition.Label,
            option,
            option.Value == definition.DefaultValue)).ToArray();
        if (Options.Count(option => option.IsSelected) != 1)
            throw new ArgumentException("Invalid film filter default.", nameof(definition));
    }

    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<FilmFilterOption> Options { get; }
    internal string SelectedValue => Options.Single(option => option.IsSelected).Value;

    internal bool Contains(FilmFilterOption option) =>
        option is not null && option.FilterKey == Key && Options.Contains(option);

    internal bool Select(FilmFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!Contains(option)) throw new ArgumentException("Unknown film filter option.", nameof(option));
        if (option.IsSelected) return false;
        foreach (var candidate in Options)
            candidate.Select(ReferenceEquals(candidate, option));
        return true;
    }
}

public sealed record FilmCardItem(string Id, string Title, string Poster, string Score)
{
    public bool HasScore => Score.Length > 0;
    public string TextBadge { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool HasTextBadge => TextBadge.Length > 0;
    public bool HasSubtitle => Subtitle.Length > 0;
    public FilmSearchContext? SearchContext { get; init; }
    public FilmPlaybackRequest? PlaybackContext { get; init; }

    internal bool HasSameContent(FilmCardItem other)
    {
        if (this == other) return true;
        return Id == other.Id && Title == other.Title && Poster == other.Poster && Score == other.Score &&
            TextBadge == other.TextBadge && Subtitle == other.Subtitle && SearchContext == other.SearchContext &&
            SameRequest(PlaybackContext, other.PlaybackContext);

        static bool SameRequest(FilmPlaybackRequest? left, FilmPlaybackRequest? right) =>
            left == right || left is not null && right is not null &&
            left.Target == right.Target && left.Sources == right.Sources && left.Query == right.Query && left.EntrySurface == right.EntrySurface &&
            left.DataOwner == right.DataOwner && SameJson(left.Results, right.Results) &&
            SameJson(left.Search, right.Search) && SameJson(left.Metadata, right.Metadata) &&
            SameJson(left.CacheEntries, right.CacheEntries);
        static bool SameJson(JsonElement? left, JsonElement? right) =>
            left is { } value && right is { } other ? JsonElement.DeepEquals(value, other) : left is null && right is null;
    }
}

internal enum FilmHomeSectionState
{
    Pending,
    Loading,
    Ready,
    Failed,
    Cancelled
}

public sealed class FilmHomeSection : INotifyPropertyChanged
{
    private FilmHomeSectionState state = FilmHomeSectionState.Pending;

    internal FilmHomeSection(FilmSectionDefinition definition) =>
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));

    internal FilmSectionDefinition Definition { get; }
    internal FilmHomeSectionState State => state;
    public double HorizontalOffset { get; set; }
    internal FilmMetadataCache? Cache { get; private set; }

    public string Key => Definition.Key;
    public string Title => Definition.Title;
    public ObservableCollection<FilmCardItem> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool ShowSkeleton => !HasItems && state is FilmHomeSectionState.Pending or FilmHomeSectionState.Loading;
    public bool ShowStatus => !HasItems && !ShowSkeleton;
    public bool CanRetry => state is FilmHomeSectionState.Failed or FilmHomeSectionState.Cancelled;
    public bool CanRefresh => state != FilmHomeSectionState.Loading;
    public string StatusText => state switch
    {
        FilmHomeSectionState.Pending or FilmHomeSectionState.Loading => "加载中…",
        FilmHomeSectionState.Failed => "栏目加载失败",
        FilmHomeSectionState.Cancelled => "加载已取消",
        FilmHomeSectionState.Ready when Items.Count == 0 => "暂无数据",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Begin()
    {
        state = FilmHomeSectionState.Loading;
        PublishState();
    }

    internal void Complete(IReadOnlyList<FilmCardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (Items.Count != items.Count || Items.Where((item, index) => !item.HasSameContent(items[index])).Any())
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        Cache = (items as FilmHomePage)?.Cache;
        state = FilmHomeSectionState.Ready;
        PublishState();
    }

    internal void Fail()
    {
        state = FilmHomeSectionState.Failed;
        PublishState();
    }

    internal void Cancel()
    {
        state = FilmHomeSectionState.Cancelled;
        PublishState();
    }

    private void PublishState()
    {
        Changed(nameof(HasItems));
        Changed(nameof(ShowStatus));
        Changed(nameof(ShowSkeleton));
        Changed(nameof(CanRetry));
        Changed(nameof(CanRefresh));
        Changed(nameof(StatusText));
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}

internal enum FilmDataSectionState
{
    Pending,
    Loading,
    Ready,
    Failed,
    Cancelled
}

public sealed class FilmDataSection : INotifyPropertyChanged
{
    private readonly FilmSectionDefinition homeDefinition;
    private FilmSectionDefinition definition;
    private FilmDataSectionState state = FilmDataSectionState.Pending;
    private bool hasMore = true;
    private int nextStart;

    internal FilmDataSection(FilmSectionDefinition definition)
    {
        homeDefinition = definition ?? throw new ArgumentNullException(nameof(definition));
        FilterRows = FilmDataFilters.RowsFor(definition.Key)
            .Select(row => new FilmFilterRow(row))
            .ToArray();
        this.definition = FilmDataFilters.BuildRequest(homeDefinition, FilterRows);
    }

    internal FilmSectionDefinition Definition => definition;
    internal FilmDataSectionState State => state;
    internal int NextStart => nextStart;
    internal bool CanSeedFromHome => definition == homeDefinition;
    internal FilmMetadataCache? Cache { get; private set; }
    internal bool ReloadPending { get; private set; }

    public string Key => homeDefinition.Key;
    public string Title => homeDefinition.Title;
    public IReadOnlyList<FilmFilterRow> FilterRows { get; }
    public ObservableCollection<FilmCardItem> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool ShowSkeleton => !HasItems && state is FilmDataSectionState.Pending or FilmDataSectionState.Loading;
    public bool ShowStatus => !HasItems && !ShowSkeleton;
    public bool CanRetry => !HasItems && state is FilmDataSectionState.Failed or FilmDataSectionState.Cancelled;
    public bool CanRefresh => state != FilmDataSectionState.Loading;
    public bool ShowPageAction => HasItems &&
        (hasMore || state is FilmDataSectionState.Loading or FilmDataSectionState.Failed or FilmDataSectionState.Cancelled);
    public bool CanPage => HasItems && (hasMore || ReloadPending) &&
        state is FilmDataSectionState.Ready or FilmDataSectionState.Failed or FilmDataSectionState.Cancelled;
    public string PageActionText => state switch
    {
        FilmDataSectionState.Loading => "加载中…",
        FilmDataSectionState.Failed or FilmDataSectionState.Cancelled => "重试",
        _ => "加载更多"
    };
    public string StatusText => state switch
    {
        FilmDataSectionState.Pending or FilmDataSectionState.Loading => "加载中…",
        FilmDataSectionState.Failed => "影片数据加载失败",
        FilmDataSectionState.Cancelled => "加载已取消",
        FilmDataSectionState.Ready when Items.Count == 0 => "暂无数据",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Contains(FilmFilterOption option) =>
        FilterRows.Any(row => row.Contains(option));

    internal bool SelectFilter(FilmFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var row = FilterRows.SingleOrDefault(candidate => candidate.Contains(option)) ??
            throw new ArgumentException("Unknown film filter option.", nameof(option));
        if (!row.Select(option)) return false;
        definition = FilmDataFilters.BuildRequest(homeDefinition, FilterRows);
        Items.Clear();
        Cache = null;
        ReloadPending = false;
        nextStart = 0;
        hasMore = true;
        state = FilmDataSectionState.Pending;
        PublishState();
        return true;
    }

    internal void Seed(IEnumerable<FilmCardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!CanSeedFromHome || Items.Count != 0 || nextStart != 0) return;
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (known.Add(item.Id))
                Items.Add(item);
        nextStart = Items.Count;
        PublishState();
    }

    internal void Begin(bool replace = false)
    {
        ReloadPending = replace;
        state = FilmDataSectionState.Loading;
        PublishState();
    }

    internal void Complete(IReadOnlyList<FilmCardItem> items, int start, int requestedLimit)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (start < 0 || requestedLimit < 1 || start != nextStart)
            throw new ArgumentOutOfRangeException(nameof(start));
        var known = Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
            if (known.Add(item.Id))
                Items.Add(item);
        nextStart = checked(start + items.Count);
        hasMore = items.Count >= requestedLimit;
        Cache = (items as FilmHomePage)?.Cache;
        ReloadPending = false;
        state = FilmDataSectionState.Ready;
        PublishState();
    }

    internal void Replace(IReadOnlyList<FilmCardItem> items, int next, bool more, FilmMetadataCache? cache)
    {
        var unique = items.DistinctBy(item => item.Id).ToArray();
        if (Items.Count != unique.Length || Items.Where((item, index) => !item.HasSameContent(unique[index])).Any())
        {
            Items.Clear();
            foreach (var item in unique) Items.Add(item);
        }
        nextStart = next;
        hasMore = more;
        Cache = cache;
        ReloadPending = false;
        state = FilmDataSectionState.Ready;
        PublishState();
    }

    internal void Fail()
    {
        state = FilmDataSectionState.Failed;
        PublishState();
    }

    internal void Cancel()
    {
        state = FilmDataSectionState.Cancelled;
        PublishState();
    }

    private void PublishState()
    {
        Changed(nameof(HasItems));
        Changed(nameof(ShowStatus));
        Changed(nameof(ShowSkeleton));
        Changed(nameof(CanRetry));
        Changed(nameof(CanRefresh));
        Changed(nameof(ShowPageAction));
        Changed(nameof(CanPage));
        Changed(nameof(PageActionText));
        Changed(nameof(StatusText));
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
