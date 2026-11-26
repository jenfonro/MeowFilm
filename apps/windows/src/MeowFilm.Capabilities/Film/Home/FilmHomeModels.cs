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

internal static class FilmDataFilters
{
    private static readonly IReadOnlyList<CatalogFilterRowDefinition> Movie =
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

    private static readonly IReadOnlyList<CatalogFilterRowDefinition> Tv =
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

    private static readonly IReadOnlyList<CatalogFilterRowDefinition> Anime =
    [
        new("category", "分类", "tv_animation",
        [
            new("动漫", "tv_animation")
        ])
    ];

    private static readonly IReadOnlyList<CatalogFilterRowDefinition> Show =
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

    internal static IReadOnlyList<CatalogFilterRowDefinition> RowsFor(string key) => key switch
    {
        "movie" => Movie,
        "tv" => Tv,
        "anime" => Anime,
        "show" => Show,
        _ => throw new ArgumentException("Unknown film section.", nameof(key))
    };

    internal static FilmSectionDefinition BuildRequest(
        FilmSectionDefinition section,
        IEnumerable<CatalogFilterRow> rows)
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

public interface IFilmCardDisplay : ICatalogCardDisplay
{
    FilmCardItem ToCard();
}

public sealed record FilmCardItem(string Id, string Title, string Poster, string Score) : IFilmCardDisplay
{
    public bool HasScore => Score.Length > 0;
    public string TextBadge { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool HasTextBadge => TextBadge.Length > 0;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool ShowPlayMark => true;
    public NovelBook? NovelBook => null;
    public NovelSourceService? NovelSources => null;
    public FilmSearchContext? SearchContext { get; init; }
    public FilmEntry? Entry { get; init; }
    public FilmCardItem ToCard() => this;

    public bool HasSameDisplay(ICatalogCardDisplay other) =>
        other is FilmCardItem film && HasSameContent(film);

    internal bool HasSameContent(FilmCardItem other)
    {
        if (this == other) return true;
        return Id == other.Id && Title == other.Title && Poster == other.Poster && Score == other.Score &&
            TextBadge == other.TextBadge && Subtitle == other.Subtitle && SearchContext == other.SearchContext &&
            (Entry is null ? other.Entry is null : Entry.SameContent(other.Entry));
    }
}

public sealed class FilmHomeSection : CatalogHomeSection
{
    internal FilmHomeSection(FilmSectionDefinition definition)
        : base(
            (definition ?? throw new ArgumentNullException(nameof(definition))).Key,
            definition.Title,
            defaultFailedText: "栏目加载失败",
            emptyText: "暂无数据")
    {
        Definition = definition;
    }

    internal FilmSectionDefinition Definition { get; }
    internal FilmMetadataCache? Cache { get; private set; }

    internal void Complete(IReadOnlyList<FilmCardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Cache = (items as FilmHomePage)?.Cache;
        CompleteItems(items);
    }
}

public sealed class FilmDataSection : CatalogDataSection
{
    private readonly FilmSectionDefinition homeDefinition;
    private FilmSectionDefinition definition;
    private int nextStart;

    internal FilmDataSection(FilmSectionDefinition definition)
        : base(
            (definition ?? throw new ArgumentNullException(nameof(definition))).Key,
            definition.Title,
            FilmDataFilters.RowsFor(definition.Key).Select(row => new CatalogFilterRow(row)).ToArray(),
            defaultFailedText: "影片数据加载失败",
            emptyText: "暂无数据")
    {
        homeDefinition = definition;
        this.definition = FilmDataFilters.BuildRequest(homeDefinition, FilterRows);
    }

    internal FilmSectionDefinition Definition => definition;
    internal int NextStart => nextStart;
    internal bool CanSeedFromHome => definition == homeDefinition;
    internal FilmMetadataCache? Cache { get; private set; }

    internal bool SelectFilter(CatalogFilterOption option)
    {
        if (!SelectFilterOption(option)) return false;
        definition = FilmDataFilters.BuildRequest(homeDefinition, FilterRows);
        Cache = null;
        nextStart = 0;
        return true;
    }

    internal void Seed(IEnumerable<ICatalogCardDisplay> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!CanSeedFromHome || Items.Count != 0 || nextStart != 0) return;
        SeedUniqueItems(items, markReady: false, more: true);
        nextStart = Items.Count;
    }

    internal void Complete(IReadOnlyList<FilmCardItem> items, int start, int requestedLimit)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (start < 0 || requestedLimit < 1 || start != nextStart)
            throw new ArgumentOutOfRangeException(nameof(start));
        nextStart = checked(start + items.Count);
        Cache = (items as FilmHomePage)?.Cache;
        AppendUniqueItems(items, _ => items.Count >= requestedLimit);
    }

    internal void Replace(IReadOnlyList<FilmCardItem> items, int next, bool more, FilmMetadataCache? cache)
    {
        nextStart = next;
        Cache = cache;
        ReplaceAllItems(items, more);
    }
}
