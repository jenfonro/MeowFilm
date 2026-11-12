using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

/// <summary>Presentation of one real Node snapshot. Only layout/expanded UI state is native-owned.</summary>
public sealed partial class FilmPlaybackPage : INotifyPropertyChanged
{
    private readonly FilmPlaybackSession? session;
    private readonly FilmLibrary? library;
    private FilmPlaybackSnapshot? snapshot;
    private bool isListLayout, overviewExpanded, favorite, favoriteBusy;
    private string connectionError = "";

    internal FilmPlaybackPage(FilmCardItem item, FilmPlaybackRequest request,
        FilmPlaybackSession? session, FilmLibrary? library)
    {
        Item = item; Request = request; this.session = session; this.library = library;
        NavigationIdentity = item.SearchContext is null ? Guid.NewGuid().ToString("N") : "search:" + Guid.NewGuid().ToString("N");
        request.Target.Validate();
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
        if (SearchContext is not null && Request.Sources is null)
            Request = Request with { Sources = sources.Select(source => source.Clone()).ToArray() };
    }
    internal FilmPlaybackTarget ActiveTarget => snapshot?.Target ?? Request.Target;
    internal bool HasPlaybackSnapshot => snapshot is not null;
    internal FilmPlaybackRequest CaptureRequest
    {
        get
        {
            var target = ActiveTarget;
            if ((target.Kind is "source" or "group") && target.Source is { } active && Request.Target.Source is { } original &&
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
            return Request with { Target = target,
                Metadata = DisplayMetadata.ValueKind == JsonValueKind.Object ? DisplayMetadata.Clone() : null };
        }
    }
    private JsonElement DisplayMetadata => snapshot is { } state &&
        (state.Metadata.ValueKind == JsonValueKind.Object || SearchContext is null || ActiveTarget.Kind != "group")
        ? state.Metadata : Request.Metadata ?? default;
    private bool DisplaysOriginalIdentity => ActiveTarget.MetadataProvider == Request.Target.MetadataProvider &&
        ActiveTarget.MetadataId == Request.Target.MetadataId;
    private FilmMetadataSupplement? Supplement => ActiveTarget.MetadataProvider == "douban" ? snapshot?.MetadataSupplement : null;
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
                string date = FilmJson.Text(detail, ActiveTarget.Kind == "movie" ? "release_date" : "first_air_date");
                if (date.Length >= 4) year = date[..4];
            }
            if (year.Length > 0) values.Add(year);
            string countries = FilmJson.Names(FilmJson.Member(detail, "production_countries"));
            if (countries.Length == 0) countries = FilmJson.Names(FilmJson.Member(detail, "countries"));
            if (countries.Length > 0) values.Add(countries);
            if (FilmJson.Integer(detail, "runtime") is > 0 and var minutes) values.Add($"{minutes}分钟");
            if (ActiveTarget.Kind == "tv" && SeasonLabel.Length > 0) values.Add(SeasonLabel);
            if (Episodes.Count > 0 && ActiveTarget.Kind != "movie") values.Add(EpisodeCountLabel);
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
        ActiveTarget.MetadataProvider == "tmdb" ? "TMDB" : ActiveTarget.MetadataProvider == "douban" ? "豆瓣" : "";
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
    public IReadOnlyList<FilmEpisodeItem> Episodes { get; private set; } = [];
    public IReadOnlyList<FilmCandidateItem> Candidates { get; private set; } = [];
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
    public FilmSelection? SelectedLine => Lines.FirstOrDefault(value => value.WireId is { } id &&
        JsonElement.DeepEquals(id, FilmJson.Member(snapshot?.Browsing ?? default, "lineId")));
    public FilmSelection? SelectedSeason => Seasons.FirstOrDefault(value => value.Season == Season) ?? Seasons.FirstOrDefault();
    public FilmEpisodeItem? SelectedEpisode => Episodes.FirstOrDefault(value => value.IsSelected);
    public string SeasonLabel => SelectedSeason?.Label ?? "";
    public string EpisodeCountLabel => ActiveTarget.Kind == "movie" ? "" :
        ActiveTarget.Kind == "source" ? $"共{Episodes.Count}项" : $"共{Episodes.Count}集";
    public bool IsListLayout => isListLayout;
    public bool IsHorizontalLayout => !isListLayout;
    public bool OverviewExpanded => overviewExpanded;
    public bool IsFavorite => favorite;
    public bool CanFavorite => library is not null && !favoriteBusy && session?.IsCurrentPage(this) == true;
    public bool Applying { get; private set; }
    public bool HasLines => Lines.Count > 0;
    public bool HasMetadataMappings => SourceId is not null && MetadataSources.Count > 1;
    public bool HasSeasons => Seasons.Count > 0;
    public bool HasImageEpisodes => SourceId is null && ActiveTarget.MetadataProvider == "tmdb" && Episodes.Count > 0;
    public bool ShowEpisodeCards => HasImageEpisodes && IsHorizontalLayout;
    public bool ShowEpisodeList => HasImageEpisodes && IsListLayout;
    public bool ShowPlainEpisodes => !HasImageEpisodes && ActiveTarget.Kind != "movie" && Episodes.Count > 0;
    public bool ShowMovieCandidates => ActiveTarget.Kind == "movie";
    public bool CanBrowseRaw => SourceId is not null &&
        FilmJson.Array(snapshot?.Browsing ?? default, "originalEpisodes").Length > 0 &&
        (!ListLoading || FilmJson.Bool(snapshot?.Browsing ?? default, "refreshing")) && ListError.Length == 0;
    public bool CanSearchMore => snapshot is not null && !FilmJson.Bool(snapshot.Raw, "searchComplete");
    public bool ListLoading => FilmJson.Bool(snapshot?.Browsing ?? default, "loading");
    public string ListError => FilmJson.Error(snapshot?.Browsing ?? default, "error");
    public bool ShowListStatus => ListLoading || ListError.Length > 0 || Episodes.Count == 0 && Candidates.Count == 0;
    public string ListStatus => ListError.Length > 0 ? ListError : ListLoading ? "加载中…" : snapshot is null ? "加载中…" : "暂无内容";
    public bool CanPlay => session?.IsCurrentPage(this) == true && snapshot is not null;
    public bool CanRefreshBrowsing => CanPlay && !ListLoading;
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
        "loading" or "searching" => "匹配中…", "resolving" => "取址中…",
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
            string previousIdentity = ActiveTarget.Id;
            snapshot = value; connectionError = "";
            if (previousIdentity != ActiveTarget.Id) favorite = false;
            MetadataSources = FilmJson.Array(value.Raw, "metadataSources").Select(row =>
            {
                string provider = FilmJson.RequiredText(row, "provider");
                if (provider is not ("tmdb" or "douban")) throw new FilmPlaybackException("INVALID_RESPONSE");
                return new FilmSelection(null, provider == "tmdb" ? "TMDB" : "豆瓣",
                    MetadataSourceId: FilmJson.RequiredText(row, "id"));
            }).ToArray();
            var choices = MetadataSources.ToList();
            choices.AddRange(FilmJson.Array(value.Raw, "sources").Select(source =>
                new FilmSelection(FilmJson.RequiredText(source, "id"), FilmJson.Text(source, "title"))));
            Sources = choices;
            Lines = FilmJson.Array(value.Browsing, "lines").Select(line =>
            {
                var id = FilmJson.Identity(line, "id", required: true);
                return new FilmSelection(FilmJson.String(id), FilmJson.Text(line, "label"), WireId: id);
            }).ToArray();
            var seasons = FilmJson.Array(value.Metadata, "seasons");
            if (seasons.Length == 0) seasons = FilmJson.Array(value.Metadata, "doubanSeasons");
            Seasons = ActiveTarget.Kind == "movie" || seasons.Length == 0 ? [new(null, "正片")] :
                seasons.Select(row => new { Row = row, Number = FilmJson.Integer(row, "season_number") ?? FilmJson.Integer(row, "season") })
                .Where(row => row.Number is not null)
                .Select(row => new FilmSelection(null, FilmJson.Text(row.Row, "name") is { Length: > 0 } name ? name :
                    row.Number == 0 ? "特别篇" : $"第{row.Number}季", row.Number)).DistinctBy(row => row.Season).ToArray();
            if (Seasons.Count == 1 && Seasons[0].Season != 0) Seasons = [Seasons[0] with { Label = "正片" }];
            bool failed = ListError.Length > 0 || ListLoading && !FilmJson.Bool(value.Browsing, "refreshing");
            var episodeRows = SourceId is null ? FilmJson.Array(value.Raw, "episodes") : FilmJson.Array(value.Browsing, "episodes");
            Episodes = failed ? [] : episodeRows.Select(row => new FilmEpisodeItem(this, row, SourceId is not null)).ToArray();
            Candidates = failed ? [] : FilmJson.Array(value.Raw, "candidates").Select(FilmCandidateItem.Read).ToArray();
            if (SourceId is not null)
            {
                var visible = episodeRows.Select(row => FilmJson.Text(row, "candidateId")).ToHashSet(StringComparer.Ordinal);
                Candidates = Candidates.Where(row => visible.Contains(row.Id)).ToArray();
            }
            if (ActiveTarget.Kind == "movie")
            {
                var duplicates = new Dictionary<string, int>(StringComparer.Ordinal);
                Candidates = Candidates.OrderBy(row => FilmCandidateItem.DisplayRank(row.Quality)).Select(row =>
                {
                    string label = row.DisplayLabel;
                    int count = duplicates.GetValueOrDefault(label) + 1;
                    duplicates[label] = count;
                    return row with { DisplayLabel = count == 1 ? label : $"{label} ({count})",
                        IsSelected = row.Id == CurrentCandidateId };
                }).ToArray();
            }
            Recommendations = ActiveTarget.MetadataProvider is { } provider
                ? Similar(value.Metadata, provider, ActiveTarget.Kind) : [];
            if (Recommendations.Count == 0 && Supplement is { } supplement)
                Recommendations = Similar(supplement.Metadata, "tmdb", supplement.Kind);
            Changed("");
        }
        finally { Applying = false; }
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
    internal void NotifyMedia() => Changed("");
    internal void SetConnectionError(string code) { connectionError = code; Changed(""); }
    internal void SetListLayout(bool value) { isListLayout = value; Changed(""); }
    internal void ToggleOverview() { overviewExpanded = !overviewExpanded; Changed(nameof(OverviewExpanded)); }
    private FilmPlaybackSession RequireSession()
    {
        if (session is null) throw new FilmPlaybackException("METHOD_UNAVAILABLE");
        session.RequirePage(this);
        return session;
    }
    internal Task CommandAsync(string action, object? parameters = null) => RequireSession().CommandAsync(action, parameters);
    internal Task PlayAsync() => HasOutput ? RequireSession().PauseAsync() : CommandAsync("play");
    internal Task RefreshBrowsingAsync() => CommandAsync("browse.refresh");
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
        string identity = ActiveTarget.Id;
        var items = await library.ReadAsync();
        if (identity == ActiveTarget.Id) RefreshFavorite(items);
    }
    internal void RefreshFavorite(IReadOnlyList<FilmCardItem> items)
    {
        favorite = items.Any(item => item.PlaybackContext?.Target.Id == ActiveTarget.Id);
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
                Id = ActiveTarget.Id, Title = Title, Poster = Poster, Score = Score,
                SearchContext = null, PlaybackContext = CaptureRequest
            }, !favorite);
            await LoadFavoriteAsync();
        }
        finally { favoriteBusy = false; Changed(nameof(CanFavorite)); Changed(nameof(IsFavorite)); }
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class FilmEpisodeItem : INotifyPropertyChanged
{
    private readonly FilmPlaybackPage page;
    private readonly bool raw;
    internal FilmEpisodeItem(FilmPlaybackPage page, JsonElement value, bool raw)
    {
        this.page = page; this.raw = raw;
        Season = FilmJson.Integer(value, "season") ?? FilmJson.Integer(value, "season_number");
        Number = FilmJson.Integer(value, "episode") ?? FilmJson.Integer(value, "episode_number");
        GlobalEpisode = FilmJson.Integer(value, "globalEpisode");
        if (!raw && (Season is null or < 0 || Number is null or < 0 ||
            (Season == 0 || Number == 0) && GlobalEpisode is not null))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        CandidateId = raw ? FilmJson.Text(value, "candidateId") is { Length: > 0 } id ? id : null : null;
        Title = FilmJson.Text(value, "name");
        Overview = FilmJson.Text(value, "overview");
        Image = FilmJson.Text(value, "still_path");
        Path = FilmJson.Text(value, "path");
        Duration = FilmJson.Integer(value, "runtime") is > 0 and var minutes ? $"{minutes}分钟" : "";
        Label = raw ? Title : Title.Length == 0 ? $"第{Number}集" : $"第{Number}集 · {Title}";
    }
    public int? Season { get; }
    public int? Number { get; }
    public int? GlobalEpisode { get; }
    public string? CandidateId { get; }
    public string Title { get; }
    public string Overview { get; }
    public string Image { get; }
    public string Duration { get; }
    public string Path { get; }
    public string Label { get; }
    public bool CanSelect => raw ? CandidateId is not null : Season is >= 0 && Number is >= 0;
    public bool IsSelected => page.Selected(this);
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}
