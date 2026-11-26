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

public interface IFilmCardDisplay
{
    string Title { get; }
    string Poster { get; }
    string Score { get; }
    bool HasScore { get; }
    string TextBadge { get; }
    bool HasTextBadge { get; }
    string Subtitle { get; }
    bool HasSubtitle { get; }
    FilmCardItem ToCard();
}

public sealed record FilmCardItem(string Id, string Title, string Poster, string Score) : IFilmCardDisplay
{
    public bool HasScore => Score.Length > 0;
    public string TextBadge { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool HasTextBadge => TextBadge.Length > 0;
    public bool HasSubtitle => Subtitle.Length > 0;
    public FilmSearchContext? SearchContext { get; init; }
    public FilmEntry? Entry { get; init; }
    public FilmCardItem ToCard() => this;

    internal bool HasSameContent(FilmCardItem other)
    {
        if (this == other) return true;
        return Id == other.Id && Title == other.Title && Poster == other.Poster && Score == other.Score &&
            TextBadge == other.TextBadge && Subtitle == other.Subtitle && SearchContext == other.SearchContext &&
            (Entry is null ? other.Entry is null : Entry.SameContent(other.Entry));
    }
}

internal static class FilmCardCollection
{
    internal static void Present(ObservableCollection<FilmCardItem> current, IReadOnlyList<FilmCardItem> desired)
    {
        var unique = new List<FilmCardItem>(desired.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in desired)
            if (identities.Add(item.Id)) unique.Add(item);
        for (int index = current.Count - 1; index >= 0; index--)
            if (!identities.Contains(current[index].Id)) current.RemoveAt(index);
        for (int index = 0; index < unique.Count; index++)
        {
            var next = unique[index];
            if (index >= current.Count || current[index].Id != next.Id)
            {
                int previous = -1;
                for (int candidate = index + 1; candidate < current.Count; candidate++)
                    if (current[candidate].Id == next.Id) { previous = candidate; break; }
                if (previous >= 0) current.Move(previous, index);
                else current.Insert(index, next);
            }
            if (!current[index].HasSameContent(next)) current[index] = next;
        }
        while (current.Count > unique.Count) current.RemoveAt(current.Count - 1);
    }
}

internal enum FilmSectionState
{
    Pending,
    Loading,
    Ready,
    Failed,
    Cancelled
}

public sealed class FilmHomeSection : INotifyPropertyChanged
{
    private FilmSectionState state = FilmSectionState.Pending;

    internal FilmHomeSection(FilmSectionDefinition definition) =>
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));

    internal FilmSectionDefinition Definition { get; }
    internal FilmSectionState State => state;
    public double HorizontalOffset { get; set; }
    internal FilmMetadataCache? Cache { get; private set; }

    public string Key => Definition.Key;
    public string Title => Definition.Title;
    public ObservableCollection<FilmCardItem> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool ShowLoading => !HasItems && state is FilmSectionState.Pending or FilmSectionState.Loading;
    public bool ShowStatus => !HasItems && !ShowLoading;
    public bool CanRetry => state is FilmSectionState.Failed or FilmSectionState.Cancelled;
    public bool CanRefresh => state != FilmSectionState.Loading;
    public string StatusText => state switch
    {
        FilmSectionState.Failed => "栏目加载失败",
        FilmSectionState.Cancelled => "加载已取消",
        FilmSectionState.Ready when Items.Count == 0 => "暂无数据",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Begin()
    {
        state = FilmSectionState.Loading;
        PublishState();
    }

    internal void Complete(IReadOnlyList<FilmCardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        FilmCardCollection.Present(Items, items);
        Cache = (items as FilmHomePage)?.Cache;
        state = FilmSectionState.Ready;
        PublishState();
    }

    internal void Fail()
    {
        state = FilmSectionState.Failed;
        PublishState();
    }

    internal void Cancel()
    {
        state = FilmSectionState.Cancelled;
        PublishState();
    }

    private void PublishState()
    {
        Changed(nameof(HasItems));
        Changed(nameof(ShowStatus));
        Changed(nameof(ShowLoading));
        Changed(nameof(CanRetry));
        Changed(nameof(CanRefresh));
        Changed(nameof(StatusText));
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}

public sealed class FilmDataSection : INotifyPropertyChanged
{
    private readonly FilmSectionDefinition homeDefinition;
    private FilmSectionDefinition definition;
    private FilmSectionState state = FilmSectionState.Pending;
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
    internal FilmSectionState State => state;
    internal int NextStart => nextStart;
    internal bool CanSeedFromHome => definition == homeDefinition;
    internal FilmMetadataCache? Cache { get; private set; }
    internal bool ReloadPending { get; private set; }

    public string Key => homeDefinition.Key;
    public string Title => homeDefinition.Title;
    public IReadOnlyList<FilmFilterRow> FilterRows { get; }
    public ObservableCollection<FilmCardItem> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool ShowLoading => !HasItems && state is FilmSectionState.Pending or FilmSectionState.Loading;
    public bool ShowStatus => !HasItems && !ShowLoading;
    public bool CanRetry => !HasItems && state is FilmSectionState.Failed or FilmSectionState.Cancelled;
    public bool CanRefresh => state != FilmSectionState.Loading;
    public bool ShowPageAction => HasItems &&
        (hasMore || state is FilmSectionState.Loading or FilmSectionState.Failed or FilmSectionState.Cancelled);
    public bool CanPage => HasItems && (hasMore || ReloadPending) &&
        state is FilmSectionState.Ready or FilmSectionState.Failed or FilmSectionState.Cancelled;
    public string PageActionText => state switch
    {
        FilmSectionState.Loading => "加载中…",
        FilmSectionState.Failed or FilmSectionState.Cancelled => "重试",
        _ => "加载更多"
    };
    public string StatusText => state switch
    {
        FilmSectionState.Failed => "影片数据加载失败",
        FilmSectionState.Cancelled => "加载已取消",
        FilmSectionState.Ready when Items.Count == 0 => "暂无数据",
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
        state = FilmSectionState.Pending;
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
        state = FilmSectionState.Loading;
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
        state = FilmSectionState.Ready;
        PublishState();
    }

    internal void Replace(IReadOnlyList<FilmCardItem> items, int next, bool more, FilmMetadataCache? cache)
    {
        FilmCardCollection.Present(Items, items);
        nextStart = next;
        hasMore = more;
        Cache = cache;
        ReloadPending = false;
        state = FilmSectionState.Ready;
        PublishState();
    }

    internal void Fail()
    {
        state = FilmSectionState.Failed;
        PublishState();
    }

    internal void Cancel()
    {
        state = FilmSectionState.Cancelled;
        PublishState();
    }

    private void PublishState()
    {
        Changed(nameof(HasItems));
        Changed(nameof(ShowStatus));
        Changed(nameof(ShowLoading));
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
