using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

/// <summary>Stable presentation of runtime-owned data, with page-local directory and layout state.</summary>
public sealed partial class FilmPlaybackPage : INotifyPropertyChanged
{
    private sealed record DirectoryKey(string? Source, string Revision, string Line, string? Metadata, int? Season);
    private readonly FilmPlaybackSession? session;
    private readonly FilmLibrary? library;
    private readonly Dictionary<DirectoryKey, FilmEpisodeDirectory> directories = [];
    private long metadataRevision;
    private FilmPlaybackSnapshot? snapshot;
    private bool isListLayout, overviewExpanded, favorite, favoriteBusy, searchMoreBusy;
    private string connectionError = "";
    private static readonly string[] MetadataProperties =
    [
        nameof(Title), nameof(Poster), nameof(Backdrop), nameof(Logo), nameof(Metadata), nameof(Genres),
        nameof(Score), nameof(HasScore), nameof(ScoreProvider), nameof(Directors), nameof(Cast), nameof(Overview)
    ];
    private static readonly string[] SnapshotProperties =
    [
        nameof(SourceId), nameof(LineId), nameof(Season), nameof(MetadataSourceId), nameof(SelectedSource),
        nameof(CanBrowseSources), nameof(SelectedLine), nameof(SelectedSeason), nameof(SelectedEpisode),
        nameof(SeasonLabel), nameof(EpisodeCountLabel), nameof(Metadata), nameof(IsFavorite), nameof(CanFavorite),
        nameof(HasLines), nameof(HasMetadataMappings), nameof(HasSeasons), nameof(HasImageEpisodes),
        nameof(ShowEpisodeCards), nameof(ShowEpisodeList), nameof(ShowMovieCandidates), nameof(CanBrowseRaw),
        nameof(EntryResolution), nameof(RequiresSourceSearch), nameof(CanStartSourceSearch), nameof(CanSearchMore),
        nameof(ListLoading), nameof(ListError), nameof(ShowListStatus), nameof(ListStatus), nameof(ListMessage),
        nameof(ShowListMessage), nameof(CanPlay), nameof(CanRetryMedia), nameof(ShortVideo), nameof(PreviewEpisodes),
        nameof(HasCurrent), nameof(CurrentLabel), nameof(CurrentCandidateId), nameof(ConnectionFailed),
        nameof(PrefetchStatus), nameof(ReportFailed), nameof(ReportErrorMessage), nameof(MetadataAssociationFailed),
        nameof(MetadataAssociationErrorMessage), nameof(PlayerPanLabel), nameof(PlayerQualityLabel),
        nameof(PlayerPanOptions), nameof(PlayerQualityOptions), nameof(ShowPlayerSwitchActions), nameof(SelectedPanOption),
        nameof(PlayerBusy), nameof(PlayerBuffering), nameof(PlayerStatusVisible), nameof(PlayerStatusText),
        nameof(PlayerStageProgress), nameof(StatusText)
    ];
    private static readonly string[] MediaProperties =
    [
        nameof(HasOutput), nameof(NoOutput), nameof(OwnsNativeMedia), nameof(IsPlaying), nameof(PlayLabel),
        nameof(Position), nameof(Duration), nameof(CanSeek), nameof(PlaybackRate), nameof(PlaybackAspect),
        nameof(ProgressLabel), nameof(Volume), nameof(PlayerBuffering), nameof(PlayerStatusVisible),
        nameof(PlayerStatusText), nameof(StatusText), nameof(MediaView)
    ];

    internal FilmPlaybackPage(FilmCardItem item, FilmPlaybackRequest request,
        FilmPlaybackSession? session, FilmLibrary? library)
    {
        Item = item; Request = request; this.session = session; this.library = library;
        NavigationIdentity = item.SearchContext is null ? Guid.NewGuid().ToString("N") : "search:" + Guid.NewGuid().ToString("N");
        request.Target.Validate();
        request.ResumeTarget?.Validate();
        if (SearchContext is { } context)
        {
            context.Session.Service.Retain(context.Session);
            context.Session.ReferenceChanged += SearchChanged;
        }
    }

    public FilmCardItem Item { get; private set; }
    public FilmPlaybackRequest Request { get; private set; }
    public string NavigationIdentity { get; }
    public FilmSearchContext? SearchContext => Item.SearchContext;
    internal static FilmPlaybackPage FromSearch(FilmCardItem item, FilmPlaybackRequest request, FilmPlaybackSession? session, FilmLibrary? library)
    {
        if (item.SearchContext is null) throw new FilmPlaybackException("INVALID_REQUEST");
        request = request.Own();
        return new(item with { PlaybackContext = request }, request, session, library);
    }
    internal void UpdateItem(FilmCardItem item) { Item = item with { PlaybackContext = Request }; Changed(""); }
    private bool searchReleased;
    internal void ReleaseSearch()
    {
        ReleaseDirectories();
        if (searchReleased || SearchContext is not { } context) return;
        searchReleased = true;
        context.Session.ReferenceChanged -= SearchChanged;
        context.Session.Service.Release(context.Session);
    }
    private void SearchChanged(object? sender, long revision) => PublishSearch();
    internal void PublishSearch()
    {
        if (!searchReleased && SearchContext is { Session.Retired: false } context)
            session?.UpdateSearch(this, context.Revision);
    }
    internal void FreezeSearchScope(IReadOnlyList<JsonElement> sources)
    {
        if (Request.Sources is null)
            Request = Request with { Sources = sources.Select(source => source.Clone()).ToArray() };
    }
    internal FilmPlaybackTarget ActiveTarget => snapshot?.Target ?? Request.ResumeTarget ?? Request.Target;
    internal FilmPlaybackTarget EntryTarget => snapshot?.EntryTarget ?? Request.Target;
    internal string? EntrySurface => snapshot?.EntrySurface is { Length: > 0 } surface ? surface : Request.EntrySurface;
    internal bool HasPlaybackSnapshot => snapshot is not null;
    internal bool IsDoubanEntry => EntryTarget.MetadataProvider == "douban";
    internal bool HasUnresolvedEntry => IsDoubanEntry && Request.ResumeTarget is null && ActiveTarget.Kind == "group" &&
        EntryResolution is ("loading" or "unmatched" or "error") &&
        FilmJson.Text(snapshot?.Raw ?? default, "error") != "SOURCE_NOT_FOUND" &&
        ActiveTarget.Source is null && FilmJson.Array(ActiveTarget.Members ?? default).Length == 0;
    internal bool IsUnmatchedDoubanEntry => HasUnresolvedEntry && EntryResolution == "unmatched";
    internal FilmPlaybackRequest CaptureRequest
    {
        get
        {
            var target = ActiveTarget;
            var originalSource = Request.ResumeTarget?.Source ?? Request.Target.Source;
            if ((target.Kind is "source" or "group") && target.Source is { } active && originalSource is { } original &&
                FilmJson.Text(active, "candidateId").Length == 0 &&
                new[] { "key", "detailId", "lineId" }.All(name =>
                    FilmJson.Member(active, name).ValueKind == FilmJson.Member(original, name).ValueKind &&
                    (FilmJson.Member(active, name).ValueKind == JsonValueKind.Undefined ||
                        JsonElement.DeepEquals(FilmJson.Member(active, name), FilmJson.Member(original, name)))))
            {
                // Public snapshots omit private raw play references. Before a selected
                // opaque candidate exists, retain the exact original typed selectors.
                var source = JsonNode.Parse(active.GetRawText())!.AsObject();
                foreach (string key in new[] { "playId", "episodeId" })
                    if (FilmJson.Member(original, key).ValueKind != JsonValueKind.Undefined)
                        source[key] = JsonNode.Parse(original.GetProperty(key).GetRawText());
                target = target with { Source = JsonSerializer.SerializeToElement(source) };
            }
            if (IsDoubanEntry)
            {
                var request = Request with
                {
                    Target = EntryTarget, EntrySurface = EntrySurface, Metadata = Request.Metadata?.Clone(),
                    ResumeTarget = snapshot is null ? Request.ResumeTarget : HasUnresolvedEntry ? null : target
                };
                if (snapshot is not null && target.Kind == "group")
                {
                    var remembered = FilmPlaybackOpen.RememberResults(FilmJson.Encode(request), snapshot);
                    request = request with { ResultHints = FilmJson.Member(remembered, "resultHints").Clone() };
                }
                return request;
            }
            bool metadataSourceResume = target.Kind == "source" && EntryTarget.Kind is ("movie" or "tv") &&
                EntryTarget.MetadataProvider == "tmdb";
            var metadata = metadataSourceResume &&
                (DisplayProvider != EntryTarget.MetadataProvider || DisplayMetadataId != EntryTarget.MetadataId)
                ? Request.Metadata ?? default : DisplayMetadata;
            return Request with { Target = metadataSourceResume ? EntryTarget : target, EntrySurface = EntrySurface,
                ResumeTarget = metadataSourceResume ||
                    snapshot is not null && (Request.SearchReference is not null || Request.ResumeTarget is not null)
                    ? target : Request.ResumeTarget,
                Metadata = metadata.ValueKind == JsonValueKind.Object ? metadata.Clone() : null };
        }
    }
    private JsonElement DisplayMetadata
    {
        get
        {
            if (IsDoubanEntry && (EntryResolution != "matched" ||
                snapshot is not { } entry || entry.Metadata.ValueKind != JsonValueKind.Object))
                return Request.Metadata ?? snapshot?.Metadata ?? default;
            if (snapshot is { } state &&
                (state.Metadata.ValueKind == JsonValueKind.Object || SearchContext is null || ActiveTarget.Kind != "group"))
                return state.Metadata;
            return Request.Metadata ?? default;
        }
    }
    private JsonElement DisplaySource => snapshot?.SelectedMetadataSource ?? default;
    private string DisplayKind => snapshot?.ContentKind ?? ActiveTarget.Kind;
    private string? DisplayProvider => FilmJson.Text(DisplaySource, "provider") is { Length: > 0 } provider
        ? provider : ActiveTarget.MetadataProvider;
    private string? DisplayMetadataId => FilmJson.Text(DisplaySource, "metadataId") is { Length: > 0 } id
        ? id : ActiveTarget.MetadataId;
    private bool DisplaysOriginalIdentity => DisplayProvider == Request.Target.MetadataProvider &&
        DisplayMetadataId == Request.Target.MetadataId;
    private FilmMetadataSupplement? Supplement => DisplayProvider == "douban" ? snapshot?.MetadataSupplement : null;
    private JsonElement SupplementMetadata => Supplement?.Metadata ?? default;
    private bool UseOriginalValue(string first, string second)
    {
        if (!DisplaysOriginalIdentity) return false;
        var detail = FilmJson.Member(MetadataCache?.Components ?? default, "detail");
        string state = FilmJson.Text(detail, "status");
        if (FilmJson.Member(detail, "fetchedAt").ValueKind == JsonValueKind.Number || state is "ready" or "empty")
            return false;
        return state is "failed" or "missing" ||
            FilmJson.Member(DisplayMetadata, first).ValueKind == JsonValueKind.Undefined &&
            FilmJson.Member(DisplayMetadata, second).ValueKind == JsonValueKind.Undefined;
    }
    public string Title => FilmJson.Text(DisplayMetadata, "title") is { Length: > 0 } title ? title :
        FilmJson.Text(DisplayMetadata, "name") is { Length: > 0 } name ? name : ActiveTarget.Title;
    public string Poster
    {
        get
        {
            var detail = DisplayMetadata;
            string poster = FilmJson.Text(detail, "poster_path");
            if (poster.Length == 0) poster = FilmJson.Text(FilmJson.Member(detail, "pic"), "normal");
            if (poster.Length == 0) poster = FilmJson.Text(SupplementMetadata, "poster_path");
            return poster.Length > 0 ? poster : UseOriginalValue("poster_path", "pic") ? Item.Poster : "";
        }
    }
    public string Backdrop => FilmJson.Text(DisplayMetadata, "backdrop_path") is { Length: > 0 } backdrop ? backdrop :
        FilmJson.Text(SupplementMetadata, "backdrop_path") is { Length: > 0 } supplement ? supplement : Poster;
    public string Logo => LogoFrom(DisplayMetadata) is { Length: > 0 } logo ? logo : LogoFrom(SupplementMetadata);
    private static string LogoFrom(JsonElement detail) => FilmJson.Text(detail, "logo_path") is { Length: > 0 } logo ? logo :
        FilmJson.Array(FilmJson.Member(detail, "images"), "logos")
            .Select(row => FilmJson.Text(row, "file_path")).FirstOrDefault(path => path.Length > 0) ?? "";
    public string Metadata
    {
        get
        {
            var detail = DisplayMetadata;
            var values = new List<string>();
            string year = FilmJson.Text(detail, "year");
            if (year.Length == 0)
            {
                string date = FilmJson.Text(detail, DisplayKind == "movie" ? "release_date" : "first_air_date");
                if (date.Length >= 4) year = date[..4];
            }
            if (year.Length > 0) values.Add(year);
            string countries = FilmJson.Names(FilmJson.Member(detail, "production_countries"));
            if (countries.Length == 0) countries = FilmJson.Names(FilmJson.Member(detail, "countries"));
            if (countries.Length > 0) values.Add(countries);
            if (FilmJson.Integer(detail, "runtime") is > 0 and var minutes) values.Add($"{minutes}分钟");
            if (DisplayKind == "tv" && SeasonLabel.Length > 0) values.Add(SeasonLabel);
            if (Episodes.Count > 0 && DisplayKind != "movie") values.Add(EpisodeCountLabel);
            return string.Join(" / ", values);
        }
    }
    public string Genres => FilmJson.Names(FilmJson.Member(DisplayMetadata, "genres"));
    public string Score => FilmJson.Text(DisplayMetadata, "vote_average") is { Length: > 0 } vote && vote != "0" ? vote :
        FilmJson.Text(FilmJson.Member(DisplayMetadata, "rating"), "value") is { Length: > 0 } rating && rating != "0" ? rating :
        UseOriginalValue("vote_average", "rating") ? Item.Score : "";
    public bool HasScore => Score.Length > 0 && Score != "0";
    public string ScoreProvider => FilmJson.Member(DisplayMetadata, "vote_average").ValueKind == JsonValueKind.Number ? "TMDB" :
        FilmJson.Member(DisplayMetadata, "rating").ValueKind == JsonValueKind.Object ? "豆瓣" :
        DisplayProvider == "tmdb" ? "TMDB" : DisplayProvider == "douban" ? "豆瓣" : "";
    public string Directors
    {
        get
        {
            var detail = DisplayMetadata;
            string directors = FilmJson.Names(FilmJson.Member(detail, "directors"));
            if (directors.Length == 0) directors = CreditDirectors(detail);
            return directors.Length > 0 ? directors : CreditDirectors(SupplementMetadata);
        }
    }
    public string Cast
    {
        get
        {
            var detail = DisplayMetadata;
            string cast = FilmJson.Names(FilmJson.Member(FilmJson.Member(detail, "credits"), "cast"));
            if (cast.Length == 0) cast = FilmJson.Names(FilmJson.Member(detail, "actors"));
            return cast.Length > 0 ? cast : FilmJson.Names(FilmJson.Member(FilmJson.Member(SupplementMetadata, "credits"), "cast"));
        }
    }
    private static string CreditDirectors(JsonElement detail) => string.Join(" / ",
        FilmJson.Array(FilmJson.Member(detail, "credits"), "crew")
            .Where(value => FilmJson.Text(value, "job") == "Director")
            .Select(value => FilmJson.Text(value, "name")).Where(name => name.Length > 0).Distinct());
    public string Overview => FilmJson.Text(DisplayMetadata, "overview") is { Length: > 0 } overview
        ? overview : FilmJson.Text(DisplayMetadata, "intro") is { Length: > 0 } intro ? intro :
            FilmJson.Text(SupplementMetadata, "overview");
    public FilmEpisodeDirectory? EpisodeDirectory { get; private set; }
    public IReadOnlyList<FilmEpisodeItem> Episodes => EpisodeDirectory?.Episodes ?? [];
    public IReadOnlyList<FilmCandidateItem> Candidates => EpisodeDirectory?.Candidates ?? [];
    public IReadOnlyList<FilmCardItem> Recommendations { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Sources { get; private set; } = [];
    public IReadOnlyList<FilmSelection> MetadataSources { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Lines { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Seasons { get; private set; } = [];
    public string? SourceId => FilmJson.Text(snapshot?.Browsing ?? default, "sourceId") is { Length: > 0 } id ? id : null;
    public string? LineId => FilmJson.Text(snapshot?.Browsing ?? default, "lineId") is { Length: > 0 } id ? id : null;
    public int? Season => FilmJson.Integer(snapshot?.Browsing ?? default, "season") ?? snapshot?.Target.Season;
    public string? MetadataSourceId => FilmJson.Text(snapshot?.Raw ?? default, "metadataSourceId") is { Length: > 0 } id ? id : null;
    public FilmSelection? SelectedSource => SourceId is { } id ? Sources.FirstOrDefault(value => value.Id == id && value.MetadataSourceId is null) :
        Sources.FirstOrDefault(value => value.MetadataSourceId == MetadataSourceId && value.MetadataSourceId is not null);
    public bool CanBrowseSources => SelectedSource is not null;
    public FilmSelection? SelectedLine => Lines.FirstOrDefault(value => value.WireId is { } id &&
        JsonElement.DeepEquals(id, FilmJson.Member(snapshot?.Browsing ?? default, "lineId")));
    public FilmSelection? SelectedSeason => Seasons.FirstOrDefault(value => value.Season == Season) ?? Seasons.FirstOrDefault();
    public FilmEpisodeItem? SelectedEpisode => Episodes.FirstOrDefault(value => value.IsSelected);
    public string SeasonLabel => SelectedSeason?.Label ?? (DisplayKind == "movie" ? "正片" : "");
    public string EpisodeCountLabel => DisplayKind == "movie" ? "" :
        DisplayKind == "source" ? $"共{Episodes.Count}项" : $"共{Episodes.Count}集";
    public bool IsListLayout => isListLayout;
    public bool IsHorizontalLayout => !isListLayout;
    public bool OverviewExpanded => overviewExpanded;
    public bool IsFavorite => favorite;
    public bool CanFavorite => library is not null && !favoriteBusy && session?.IsCurrentPage(this) == true;
    public bool Applying { get; private set; }
    public bool HasLines => Lines.Count > 0;
    public bool HasMetadataMappings => SourceId is not null && MetadataSources.Count > 1;
    public bool HasSeasons => Seasons.Count > 1;
    public bool HasImageEpisodes => DisplayKind != "movie" && Episodes.Count > 0;
    public bool ShowEpisodeCards => HasImageEpisodes && IsHorizontalLayout;
    public bool ShowEpisodeList => HasImageEpisodes && IsListLayout;
    public bool ShowMovieCandidates => DisplayKind == "movie";
    public bool CanBrowseRaw => SourceId is not null &&
        FilmJson.Array(snapshot?.Browsing ?? default, "originalEpisodes").Length > 0 &&
        (!ListLoading || FilmJson.Bool(snapshot?.Browsing ?? default, "refreshing")) && ListError.Length == 0;
    public string EntryResolution => FilmJson.Text(snapshot?.Raw ?? default, "entryResolution");
    public bool RequiresSourceSearch => IsUnmatchedDoubanEntry &&
        FilmJson.Array(snapshot?.Raw ?? default, "sources").Length == 0;
    public bool CanStartSourceSearch => session?.IsCurrentPage(this) == true && RequiresSourceSearch && !PlayerBusy;
    public bool CanSearchMore => snapshot is not null && session?.IsCurrentPage(this) == true && !searchMoreBusy &&
        PlayerStage is not ("loading" or "searching") && !RequiresSourceSearch &&
        !FilmJson.Array(snapshot.Raw, "search").Any(row => FilmJson.Bool(row, "loading")) &&
        EntryResolution is not ("loading" or "error") &&
        FilmJson.Text(snapshot.Raw, "error") != "SOURCE_NOT_FOUND" && !FilmJson.Bool(snapshot.Raw, "searchComplete");
    public bool ListLoading => FilmJson.Bool(snapshot?.Browsing ?? default, "loading");
    public string ListError => FilmJson.Error(snapshot?.Browsing ?? default, "error");
    public bool ShowListStatus => ListError.Length > 0 || ListLoading || snapshot is null ||
        Episodes.Count == 0 && Candidates.Count == 0 && EntryResolution is not ("loading" or "error" or "unmatched");
    public string ListStatus => ListError.Length > 0 ? ListError : ListLoading ? "加载中…" : snapshot is null ? "加载中…" : "暂无内容";
    public string ListMessage => ListLoading || snapshot is null ? "加载中…" : "暂无内容";
    public bool ShowListMessage => ShowListStatus && ListError.Length == 0;
    public bool CanPlay => session?.IsCurrentPage(this) == true && snapshot is not null && !RequiresSourceSearch;
    public bool CanRetryMedia => CanPlay && (snapshot!.AttemptId.Length > 0 || HasCurrent ||
        FilmJson.Text(snapshot.Raw, "stage") is "error" or "unavailable");
    internal FilmMetadataCache? MetadataCache => snapshot?.MetadataCache;
    public bool HasOutput => session?.HasOutput == true;
    public bool NoOutput => !HasOutput;
    public bool OwnsNativeMedia => session?.OwnsNativeMedia == true;
    public bool IsPlaying => session?.IsPlaying == true;
    public string PlayLabel => IsPlaying ? "暂停" : "播放";
    public double Position => session?.Position ?? 0;
    public double Duration => session?.Duration ?? 0;
    public bool CanSeek => HasOutput && Duration > 0;
    public double PlaybackRate => session?.Rate ?? 1;
    public string? PlaybackAspect => session?.Aspect;
    internal string? MediaIdentity => session?.MediaIdentity;
    internal string? CurrentAttemptId => snapshot?.CurrentId;
    public string ProgressLabel => $"{FilmJson.Time(Position)} / {FilmJson.Time(Duration)}";
    public int Volume
    {
        get => session?.Volume ?? 0;
        set { if (HasOutput) RequireSession().Volume = value; Changed(nameof(Volume)); }
    }
    public ContentControl? MediaView => session?.MediaView;
    public bool ShortVideo => FilmJson.Bool(snapshot?.Raw ?? default, "shortVideo");
    public bool PreviewEpisodes => FilmJson.Bool(snapshot?.Raw ?? default, "previewEpisodes");
    public bool HasCurrent => snapshot?.CurrentId.Length > 0;
    public string CurrentLabel => FilmJson.Text(FilmJson.Member(snapshot?.Current ?? default, "candidate"), "name");
    public string StatusText => connectionError.Length > 0 ? FilmPlaybackError.Message(connectionError, "连接失败") : FilmJson.Text(snapshot?.Raw ?? default, "stage") switch
    {
        "loading" => "加载中…", "searching" => "匹配中…", "resolving" => "取址中…",
        "prepared" => "已准备", "pending" => "准备播放…", "playing" => IsPlaying ? "播放中" : "已暂停",
        "error" => FilmPlaybackError.Message(FilmJson.Text(snapshot?.Raw ?? default, "error"), "播放失败"),
        "unavailable" => "暂无可用片源", "ended" => "已结束", "stopped" => "已停止", _ => ""
    };
    public bool ConnectionFailed => connectionError.Length > 0;
    public string PrefetchStatus => string.Join(" · ", FilmJson.Array(snapshot?.Raw ?? default, "prefetch")
        .Select(value => FilmJson.Text(value, "state") switch
        {
            "resolving" => "相邻集准备中…", "prepared" => "相邻集地址已准备",
            "buffered" => "下一集媒体已缓存", "error" => "相邻集准备失败", _ => ""
        }).Where(label => label.Length > 0).Distinct());
    public bool ReportFailed => FilmJson.Text(snapshot?.Raw ?? default, "reportError").Length > 0;
    public string ReportErrorMessage => ReportFailed ? "观看上报失败" : "";
    public bool MetadataAssociationFailed => FilmJson.Text(snapshot?.Raw ?? default, "metadataError").Length > 0;
    public string CurrentCandidateId => FilmJson.Text(FilmJson.Member(snapshot?.Current ?? default, "candidate"), "id");
    internal JsonElement RawBrowsing => snapshot?.Browsing ?? default;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(FilmPlaybackSnapshot value)
    {
        Applying = true;
        try
        {
            string previousIdentity = FavoriteTarget.Id;
            var previous = snapshot;
            var previousMetadata = DisplayMetadata;
            var previousSupplement = SupplementMetadata;
            string? previousProvider = DisplayProvider;
            string previousKind = DisplayKind;
            var previousEpisodes = Episodes;
            var previousCandidates = Candidates;
            var previousDirectory = EpisodeDirectory;
            if (previous is not null && previous.SessionId != value.SessionId) ReleaseDirectories();
            snapshot = value; connectionError = "";
            if (previousIdentity != FavoriteTarget.Id) favorite = false;
            bool metadataChanged = previous is null || !FilmJson.Equal(previousMetadata, DisplayMetadata) ||
                !FilmJson.Equal(previousSupplement, SupplementMetadata) || previousProvider != DisplayProvider ||
                previousKind != DisplayKind;
            if (metadataChanged) metadataRevision++;
            if (previous is null || !FilmJson.Equal(FilmJson.Member(previous.Raw, "metadataSources"),
                FilmJson.Member(value.Raw, "metadataSources")))
            {
                var metadataSources = FilmJson.Array(value.Raw, "metadataSources").Select(row =>
                {
                    var providerValue = FilmJson.Member(row, "provider");
                    var id = FilmJson.Member(row, "id");
                    if (providerValue.ValueKind != JsonValueKind.String ||
                        providerValue.GetString() is not ("tmdb" or "douban") ||
                        id.ValueKind != JsonValueKind.String || id.GetString()!.Length == 0) return null;
                    string provider = providerValue.GetString()!;
                    return new FilmSelection(null, provider == "tmdb" ? "TMDB" : "豆瓣",
                        MetadataSourceId: id.GetString(), Provider: provider);
                }).OfType<FilmSelection>().ToArray();
                if (!MetadataSources.SequenceEqual(metadataSources))
                { MetadataSources = metadataSources; Changed(nameof(MetadataSources)); }
            }
            var sourceRows = FilmJson.Array(value.Raw, "sources");
            if (previous is null || !FilmJson.Equal(FilmJson.Member(previous.Raw, "sources"),
                FilmJson.Member(value.Raw, "sources")) || !FilmJson.Equal(FilmJson.Member(previous.Raw, "metadataSources"),
                FilmJson.Member(value.Raw, "metadataSources")))
            {
                var choices = MetadataSources.ToList();
                var repeatedNames = sourceRows.GroupBy(row => FilmJson.Text(FilmJson.Member(row, "source"), "name"), StringComparer.Ordinal)
                    .Where(group => group.Key.Length > 0 && group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                choices.AddRange(sourceRows.Select(row =>
                {
                    string name = FilmJson.Text(FilmJson.Member(row, "source"), "name"), title = FilmJson.Text(row, "title");
                    string label = name.Length == 0 ? title :
                        repeatedNames.Contains(name) && title.Length > 0 && title != name ? $"{name} · {title}" : name;
                    return new FilmSelection(FilmJson.RequiredText(row, "id"), label);
                }));
                if (!Sources.SequenceEqual(choices)) { Sources = choices; Changed(nameof(Sources)); }
                var sourceIds = sourceRows.Select(row => FilmJson.Text(row, "id")).ToHashSet(StringComparer.Ordinal);
                var metadataIds = MetadataSources.Select(row => row.MetadataSourceId).ToHashSet(StringComparer.Ordinal);
                var revisions = sourceRows.ToDictionary(row => FilmJson.Text(row, "id"), SourceVersion, StringComparer.Ordinal);
                foreach (var key in directories.Keys.Where(key =>
                    key.Source is { } source && (!sourceIds.Contains(source) || revisions[source] != key.Revision) ||
                    key.Metadata is { } metadata && !metadataIds.Contains(metadata)).ToArray())
                    directories.Remove(key);
            }
            if (previous is null || !FilmJson.Equal(FilmJson.Member(previous.Browsing, "lines"),
                FilmJson.Member(value.Browsing, "lines")))
            {
                var lines = FilmJson.Array(value.Browsing, "lines").Select(line =>
                {
                    var id = FilmJson.Identity(line, "id", required: true);
                    string provider = FilmJson.Text(line, "provider");
                    return new FilmSelection(FilmJson.String(id), PlayerProviderFamily(provider).Length > 0 ? PlayerPanName(provider) :
                        FilmJson.Text(line, "label") is { Length: > 0 } label ? label : provider,
                        WireId: id, Provider: provider.Length > 0 ? provider : null);
                }).ToArray();
                if (Lines.Count != lines.Length || !Lines.Zip(lines).All(pair => (pair.First with { WireId = null }) ==
                    (pair.Second with { WireId = null }) &&
                    FilmJson.Equal(pair.First.WireId ?? default, pair.Second.WireId ?? default)))
                { Lines = lines; Changed(nameof(Lines)); }
            }
            var trustedSeasons = FilmJson.Member(value.Metadata, "doubanSeasons");
            var seasons = trustedSeasons.ValueKind == JsonValueKind.Array ? trustedSeasons : FilmJson.Member(value.Metadata, "seasons");
            if (metadataChanged)
            {
                var nextSeasons = DisplayKind == "movie" ? [] :
                    FilmJson.Array(seasons).Select(row => new { Row = row, Number = FilmJson.Integer(row, "season_number") ?? FilmJson.Integer(row, "season") })
                        .Where(row => row.Number is not null)
                        .Select(row => new FilmSelection(null, FilmJson.Text(row.Row, "name") is { Length: > 0 } name ? name :
                            row.Number == 0 ? "特别篇" : $"第{row.Number}季", row.Number)).DistinctBy(row => row.Season).ToArray();
                if (!Seasons.SequenceEqual(nextSeasons)) { Seasons = nextSeasons; Changed(nameof(Seasons)); }
            }
            bool failed = ListError.Length > 0 || ListLoading && !FilmJson.Bool(value.Browsing, "refreshing");
            var invalidSources = sourceRows.Where(row => InvalidatesDirectory(FilmJson.Text(row, "error")))
                .Select(row => FilmJson.Text(row, "id")).ToHashSet(StringComparer.Ordinal);
            if (InvalidatesDirectory(FilmJson.Text(value.Browsing, "error")) && SourceId is { } invalidSource)
                invalidSources.Add(invalidSource);
            bool invalidMetadata = InvalidatesDirectory(FilmJson.Text(value.Raw, "metadataError")) ||
                InvalidatesDirectory(MetadataCache?.ErrorCode ?? "");
            foreach (var key in directories.Keys.Where(key => key.Source is { } source && invalidSources.Contains(source) ||
                invalidMetadata && key.Metadata == MetadataSourceId).ToArray())
                directories.Remove(key);
            var lineIdentity = FilmJson.Member(value.Browsing, "lineId");
            var directoryKey = new DirectoryKey(SourceId,
                SourceVersion(sourceRows.FirstOrDefault(row => FilmJson.Text(row, "id") == SourceId)),
                lineIdentity.ValueKind == JsonValueKind.Undefined ? "" : lineIdentity.GetRawText(), MetadataSourceId, Season);
            failed |= invalidSources.Contains(SourceId ?? "") || invalidMetadata;
            if (!directories.TryGetValue(directoryKey, out var directory) && !failed)
            {
                directory = new(); directories.Add(directoryKey, directory);
            }
            EpisodeDirectory = failed ? null : directory;
            if (!failed && directory is not null)
            {
                directory.Apply(this, FilmJson.Member(SourceId is null ? value.Raw : value.Browsing, "episodes"), seasons,
                    FilmJson.Member(value.Raw, "candidates"), metadataRevision, SourceId is not null, DisplayKind == "movie",
                    !ReferenceEquals(previousDirectory, directory) || previous is null ||
                        !FilmJson.Equal(FilmJson.Member(previous.Raw, "candidates"), FilmJson.Member(value.Raw, "candidates")));
                directory.Select(this);
            }
            if (metadataChanged)
            {
                var recommendations = DisplayProvider is { } provider ? Similar(value.Metadata, provider, DisplayKind) : [];
                if (recommendations.Count == 0 && Supplement is { } supplement)
                    recommendations = Similar(supplement.Metadata, "tmdb", supplement.Kind);
                if (!Recommendations.Select(item => (item.Id, item.Title, item.Poster, item.Score)).SequenceEqual(
                    recommendations.Select(item => (item.Id, item.Title, item.Poster, item.Score))))
                { Recommendations = recommendations; Changed(nameof(Recommendations)); }
                foreach (string property in MetadataProperties) Changed(property);
            }
            if (!ReferenceEquals(previousDirectory, EpisodeDirectory)) Changed(nameof(EpisodeDirectory));
            if (!ReferenceEquals(previousEpisodes, Episodes)) Changed(nameof(Episodes));
            if (!ReferenceEquals(previousCandidates, Candidates)) Changed(nameof(Candidates));
            foreach (string property in SnapshotProperties) Changed(property);
        }
        finally { Applying = false; }
    }

    private static string SourceVersion(JsonElement row)
    {
        var source = FilmJson.Member(row, "source");
        return FilmJson.Text(source, "revision") + ":" + FilmJson.Text(source, "generation");
    }
    private static bool InvalidatesDirectory(string code) =>
        code is "STALE_SOURCE" or "SOURCE_DISABLED" or "SOURCE_NOT_FOUND" or "STALE_ACCOUNT" or "STALE_METADATA";
    internal string ContentPoster => Poster.Length > 0 ? Poster : Item.Poster;
    internal void ReleaseDirectories()
    {
        directories.Clear(); EpisodeDirectory = null;
        Changed(nameof(EpisodeDirectory)); Changed(nameof(Episodes)); Changed(nameof(Candidates));
    }

    private static IReadOnlyList<FilmCardItem> Similar(JsonElement detail, string provider, string kind)
    {
        var rows = FilmJson.Array(FilmJson.Member(detail, "similar"), "results");
        return rows.Select(row =>
        {
            string id = FilmJson.Text(row, "id"), title = FilmJson.Text(row, provider == "douban" || kind == "movie" ? "title" : "name");
            if (id.Length == 0 || title.Length == 0) return null;
            string poster = FilmJson.Text(row, "poster_path"), score = FilmJson.Text(row, "vote_average");
            if (provider == "douban")
            {
                poster = FilmJson.Text(FilmJson.Member(row, "pic"), "normal");
                score = FilmJson.Text(FilmJson.Member(row, "rating"), "value");
            }
            return new FilmCardItem($"{provider}:{kind}:{id}", title, poster, score)
            {
                PlaybackContext = new(new($"{provider}:{kind}:{id}", kind, title, provider, id))
            };
        }).OfType<FilmCardItem>().ToArray();
    }

    internal bool Selected(FilmEpisodeItem item)
    {
        if (snapshot is null) return false;
        if (item.CandidateId is not null) return item.CandidateId == FilmJson.Text(
            FilmJson.Member(snapshot.AttemptId.Length > 0 ? snapshot.Attempt : snapshot.Current, "candidate"), "id");
        return item.Season == snapshot.Target.Season && item.Number == snapshot.Target.Episode;
    }
    internal void NotifyMedia() { foreach (string property in MediaProperties) Changed(property); }
    internal void SetConnectionError(string code) { connectionError = code; Changed(""); }
    internal void SetListLayout(bool value)
    {
        if (isListLayout == value) return;
        isListLayout = value;
        Changed(nameof(IsListLayout)); Changed(nameof(IsHorizontalLayout));
        Changed(nameof(ShowEpisodeCards)); Changed(nameof(ShowEpisodeList));
    }
    internal void ToggleOverview() { overviewExpanded = !overviewExpanded; Changed(nameof(OverviewExpanded)); }
    private FilmPlaybackSession RequireSession()
    {
        if (session is null) throw new FilmPlaybackException("METHOD_UNAVAILABLE");
        session.RequirePage(this);
        return session;
    }
    internal Task CommandAsync(string action, object? parameters = null) => RequireSession().CommandAsync(action, parameters);
    internal Task PlayAsync() => CommandAsync("play");
    internal Task PauseOrResumeAsync() => HasOutput ? RequireSession().PauseAsync() : Task.CompletedTask;
    internal async Task SearchMoreSourcesAsync()
    {
        if (!CanSearchMore) return;
        searchMoreBusy = true; Changed(nameof(CanSearchMore));
        try { await CommandAsync("search.more"); }
        finally { searchMoreBusy = false; Changed(nameof(CanSearchMore)); }
    }
    internal Task SearchSourcesAsync() => CanStartSourceSearch ? CommandAsync("search.more") : Task.CompletedTask;
    internal Task RetryMediaAsync()
    {
        string attemptId = snapshot?.AttemptId is { Length: > 0 } pending ? pending : snapshot?.CurrentId ?? "";
        return CommandAsync("media.retry", attemptId.Length > 0 ? new { attemptId } : null);
    }
    internal Task SeekAsync(double value) => RequireSession().SeekAsync(value);
    internal Task RateAsync(double value) => RequireSession().RateAsync(value);
    internal Task AspectAsync(string? value) => RequireSession().AspectAsync(value);
    internal Task ReconnectAsync() => RequireSession().ReconnectAsync();
    internal void RetainOwner(Window? owner) => session?.RetainOwner(this, owner);
    internal Task EndRetainedOwnerAsync() => session?.EndRetainedOwnerAsync(this) ?? Task.CompletedTask;
    internal Task SelectEpisodeAsync(FilmEpisodeItem item)
    {
        if (!Episodes.Contains(item) || !item.CanSelect) throw new FilmPlaybackException("INVALID_REQUEST");
        return item.CandidateId is { } id ? CommandAsync("select.candidate", new { candidateId = id }) :
            CommandAsync("select.episode", new { episode = new { season = item.Season, episode = item.Number, globalEpisode = item.GlobalEpisode } });
    }
    internal Task SelectCandidateAsync(string id)
    {
        if (!Candidates.Any(item => item.Id == id)) throw new FilmPlaybackException("INVALID_REQUEST");
        return CommandAsync("select.candidate", new { candidateId = id });
    }
    internal Task SelectOriginalCandidateAsync(string id)
    {
        if (!CanBrowseRaw || !FilmJson.Array(RawBrowsing, "originalEpisodes")
            .Any(row => FilmJson.Text(row, "candidateId") == id))
            throw new FilmPlaybackException("INVALID_REQUEST");
        return CommandAsync("select.candidate", new { candidateId = id });
    }
    internal Task BrowseSourceAsync(FilmSelection value)
    {
        if (!Sources.Contains(value)) throw new FilmPlaybackException("INVALID_REQUEST");
        return value.MetadataSourceId is { } metadataId
            ? CommandAsync("browse.metadata", new { metadataSourceId = metadataId })
            : CommandAsync("browse", new { sourceId = value.Id });
    }
    internal Task BrowseMetadataMappingAsync(FilmSelection value)
    {
        if (SourceId is null || !MetadataSources.Contains(value) || value.MetadataSourceId is null)
            throw new FilmPlaybackException("INVALID_REQUEST");
        return CommandAsync("browse.metadata", new { metadataSourceId = value.MetadataSourceId, keepSource = true });
    }
    internal Task BrowseLineAsync(FilmSelection value)
    {
        if (!Lines.Contains(value) || value.WireId is null) throw new FilmPlaybackException("INVALID_REQUEST");
        return CommandAsync("browse", new { sourceId = SourceId, lineId = value.WireId });
    }
    internal Task BrowseSeasonAsync(FilmSelection value)
    {
        if (!Seasons.Contains(value) || value.Season is null) return Task.CompletedTask;
        return CommandAsync("browse.season", new { season = value.Season });
    }
    internal async Task LoadFavoriteAsync()
    {
        if (library is null) return;
        string identity = FavoriteTarget.Id;
        var items = await library.ReadAsync();
        if (identity == FavoriteTarget.Id) RefreshFavorite(items);
    }
    private FilmPlaybackTarget FavoriteTarget => IsDoubanEntry ? EntryTarget : ActiveTarget;
    internal void RefreshFavorite(IReadOnlyList<FilmCardItem> items)
    {
        favorite = items.Any(item => item.PlaybackContext?.Target.Id == FavoriteTarget.Id);
        Changed(nameof(IsFavorite));
    }
    internal async Task ToggleFavoriteAsync()
    {
        if (library is null || favoriteBusy) return;
        RequireSession();
        favoriteBusy = true; Changed(nameof(CanFavorite));
        try
        {
            await library.SetFavoriteAsync(Item with
            {
                Id = FavoriteTarget.Id, Title = Title, Poster = Poster, Score = Score,
                SearchContext = null, PlaybackContext = CaptureRequest
            }, !favorite);
            await LoadFavoriteAsync();
        }
        finally { favoriteBusy = false; Changed(nameof(CanFavorite)); Changed(nameof(IsFavorite)); }
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
