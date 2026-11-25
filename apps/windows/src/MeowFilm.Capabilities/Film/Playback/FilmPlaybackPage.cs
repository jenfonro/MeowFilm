using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

/// <summary>Stable presentation of runtime-owned data, with page-local directory and layout state.</summary>
public sealed partial class FilmPlaybackPage : INotifyPropertyChanged
{
    private sealed record DirectoryKey(string? Source, string Line, string? Metadata, int? Season);
    private readonly FilmPlaybackSession? session;
    private readonly FilmLibrary? library;
    private readonly FilmPlaybackSettings? preferences;
    private readonly Dictionary<DirectoryKey, BrowsingSlice> directories = [];
    private long metadataRevision;
    private FilmPlaybackSnapshot? snapshot;
    private FilmWorkInfo? workInfo;
    private bool isListLayout, overviewExpanded, favorite, favoriteBusy, searchMoreBusy;
    private string connectionError = "";
    private static readonly string[] MetadataProperties =
    [
        nameof(Title), nameof(Poster), nameof(Backdrop), nameof(Metadata), nameof(Genres),
        nameof(Score), nameof(HasScore), nameof(ScoreProvider), nameof(Directors), nameof(Cast), nameof(Overview)
    ];
    private static readonly string[] SnapshotProperties =
    [
        nameof(SourceId), nameof(Season), nameof(MetadataSourceId), nameof(SelectedSource),
        nameof(CanBrowseSources), nameof(SelectedLine), nameof(SelectedSeason), nameof(SelectedEpisode),
        nameof(IsFavorite), nameof(CanFavorite),
        nameof(ShowLinePicker), nameof(CanBrowseLines), nameof(SourcePlaceholder), nameof(LinePlaceholder), nameof(HasMetadataMappings), nameof(HasSeasons), nameof(HasImageEpisodes),
        nameof(ShowEpisodeCards), nameof(ShowEpisodeList), nameof(ShowMovieCandidates), nameof(CanBrowseRaw),
        nameof(EntryResolution), nameof(RequiresSourceSearch), nameof(CanStartSourceSearch), nameof(CanSearchMore),
        nameof(ListLoading), nameof(ListError), nameof(ShowListStatus), nameof(ListStatus), nameof(ListMessage),
        nameof(ShowListMessage), nameof(CanPlay), nameof(CanSelectDirectory), nameof(CanRetryMedia), nameof(CanRetryLoading), nameof(PreviewEpisodes),
        nameof(ConnectionFailed), nameof(ExecutionFailed), nameof(NeedsRecovery), nameof(MetadataAssociationFailed),
        nameof(MetadataAssociationErrorMessage)
    ];
    private static readonly string[] PlaybackProperties =
    [
        nameof(HasCurrent), nameof(CanBlockAttempt), nameof(CurrentCandidateId), nameof(ShortVideo),
        nameof(ReportFailed), nameof(ReportErrorMessage), nameof(PlayerPanLabel), nameof(PlayerQualityLabel),
        nameof(PlayerPanOptions), nameof(PlayerQualityOptions), nameof(ShowPlayerSwitchActions),
        nameof(PlayerBusy), nameof(PlayerBuffering), nameof(PlayerStatusVisible), nameof(PlayerStatusText),
        nameof(PlayerStageProgress), nameof(StatusText)
    ];
    private static readonly string[] MediaProperties =
    [
        nameof(HasOutput), nameof(NoOutput), nameof(OwnsNativeMedia), nameof(IsPlaying),
        nameof(Position), nameof(Duration), nameof(CanSeek), nameof(PlaybackRate), nameof(PlaybackAspect),
        nameof(ProgressLabel), nameof(Volume), nameof(PlayerBuffering), nameof(PlayerStatusVisible),
        nameof(PlayerStatusText), nameof(StatusText), nameof(MediaView), nameof(MediaIdentity)
    ];

    internal FilmPlaybackPage(FilmCardItem item, FilmPlaybackRequest request,
        FilmPlaybackSession? session, FilmLibrary? library, FilmPlaybackSettings? preferences = null)
    {
        Item = item; Request = request; this.session = session; this.library = library;
        this.preferences = preferences;
        NavigationIdentity = Guid.NewGuid().ToString("N");
        request.Target.Validate();
        request.ResumeTarget?.Validate();
        if (SearchContext is { } context)
        {
            context.Session.ReferenceChanged += SearchChanged;
        }
    }

    public FilmCardItem Item { get; private set; }
    public FilmPlaybackRequest Request { get; private set; }
    public string NavigationIdentity { get; }
    public FilmSearchContext? SearchContext => Item.SearchContext;
    internal void UpdateItem(FilmCardItem item)
    {
        if (SearchContext is { } previous) previous.Session.ReferenceChanged -= SearchChanged;
        if (item.PlaybackContext is { } request) Request = request.Own();
        Item = item with { PlaybackContext = Request };
        if (!searchReleased && SearchContext is { } context) context.Session.ReferenceChanged += SearchChanged;
        Changed("");
    }
    private bool searchReleased;
    internal void Release()
    {
        metadataChoiceGeneration++;
        ReleaseDirectories();
        if (searchReleased || SearchContext is not { } context) return;
        searchReleased = true;
        context.Session.ReferenceChanged -= SearchChanged;
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
    internal bool HasExecution => session?.IsCurrentPage(this) == true && session.HasSession && !ConnectionFailed;
    internal double PageOffset { get; set; }
    internal double MetadataOffset { get; set; }
    internal double RecommendationOffset { get; set; }
    internal bool IsDoubanEntry => EntryTarget.MetadataProvider == "douban";
    internal bool HasUnresolvedEntry => IsDoubanEntry && Request.ResumeTarget is null && ActiveTarget.Kind == "group" &&
        EntryResolution is ("loading" or "unmatched" or "error") &&
        FilmJson.Text(snapshot?.Raw ?? default, "error") != "SOURCE_NOT_FOUND" &&
        ActiveTarget.Source is null && FilmJson.Array(ActiveTarget.Members ?? default).Length == 0;
    internal bool IsUnmatchedDoubanEntry => HasUnresolvedEntry && EntryResolution == "unmatched";
    public FilmEpisodeDirectory? EpisodeDirectory { get; private set; }
    public IReadOnlyList<FilmEpisodeItem> Episodes => EpisodeDirectory?.Episodes ?? [];
    public IReadOnlyList<FilmCandidateItem> Candidates => EpisodeDirectory?.Candidates ?? [];
    public IReadOnlyList<FilmCardItem> Recommendations { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Sources { get; private set; } = [];
    public IReadOnlyList<FilmSelection> MetadataSources { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Lines { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Seasons { get; private set; } = [];
    public string? SourceId => selection?.Source;
    public int? Season => selection?.Season;
    public string? MetadataSourceId => selection?.Metadata;
    public FilmSelection? SelectedSource => SourceId is { } id ? Sources.FirstOrDefault(value => value.Id == id && value.MetadataSourceId is null) :
        Sources.FirstOrDefault(value => value.Provider == aggregateProvider && value.MetadataSourceId is not null);
    public bool CanBrowseSources => HasExecution && Sources.Count > 0;
    public bool ShowLinePicker => SourceId is not null;
    public bool CanBrowseLines => HasExecution && Lines.Count > 0 && (!ListLoading || preservingRefresh);
    public string SourcePlaceholder => ListLoading ? "加载中…" : ListError.Length > 0 ? "加载失败" : "无可用视频源";
    public string LinePlaceholder => SourcePlaceholder;
    public FilmSelection? SelectedLine => Lines.FirstOrDefault(value => value.WireId is { } id &&
        id.GetRawText() == selection?.Line);
    public FilmSelection? SelectedSeason => Seasons.FirstOrDefault(value => value.Season == Season) ?? Seasons.FirstOrDefault();
    public FilmEpisodeItem? SelectedEpisode => Episodes.FirstOrDefault(value => value.IsSelected);
    public bool IsListLayout => isListLayout;
    public bool IsHorizontalLayout => !isListLayout;
    public bool OverviewExpanded => overviewExpanded;
    public bool IsFavorite => favorite;
    public bool CanFavorite => library is not null && !favoriteBusy;
    public bool Applying { get; private set; }
    public bool HasMetadataMappings => MetadataSources.Count > 1;
    public bool HasSeasons => Seasons.Count > 1;
    public bool HasImageEpisodes => DisplayKind != "movie" && Episodes.Count > 0;
    public bool ShowEpisodeCards => !ShowListStatus && HasImageEpisodes && IsHorizontalLayout;
    public bool ShowEpisodeList => !ShowListStatus && HasImageEpisodes && IsListLayout;
    public bool ShowMovieCandidates => !ShowListStatus && DisplayKind == "movie";
    public bool CanBrowseRaw => SourceId is not null &&
        FilmJson.Array(RawBrowsing, "originalEpisodes").Length > 0 &&
        ListError.Length == 0 && (!ListLoading || preservingRefresh) &&
        FilmJson.Text(RawBrowsing, "error").Length == 0;
    public string EntryResolution => FilmJson.Text(snapshot?.Raw ?? default, "entryResolution");
    public bool RequiresSourceSearch => IsUnmatchedDoubanEntry &&
        FilmJson.Array(snapshot?.Raw ?? default, "sources").Length == 0;
    public bool CanStartSourceSearch => HasExecution && RequiresSourceSearch && !PlayerBusy;
    public bool CanSearchMore => snapshot is not null && HasExecution && !searchMoreBusy &&
        PlayerStage is not ("loading" or "searching") && !RequiresSourceSearch &&
        !FilmJson.Array(snapshot.Raw, "search").Any(row => FilmJson.Bool(row, "loading")) &&
        EntryResolution is not ("loading" or "error") &&
        FilmJson.Text(snapshot.Raw, "error") != "SOURCE_NOT_FOUND" && !FilmJson.Bool(snapshot.Raw, "searchComplete");
    public bool ListLoading => browsingLoading;
    public string ListError => browsingError.Length == 0 ? "" : FilmPlaybackError.Message(browsingError, "加载失败");
    public bool ShowListStatus => ListError.Length > 0 || ListLoading && !preservingRefresh || EpisodeDirectory is null ||
        Episodes.Count == 0 && Candidates.Count == 0;
    public string ListStatus => ListError.Length > 0 ? ListError : ListLoading ? "加载中…" : "无可用视频源";
    public string ListMessage => ListStatus;
    public bool ShowListMessage => ShowListStatus && ListError.Length == 0 && !RequiresSourceSearch;
    public bool CanPlay => HasExecution && !ExecutionFailed && snapshot is not null && !RequiresSourceSearch &&
        FilmJson.Text(snapshot.Raw, "initializationError").Length == 0 &&
        PlayerStage != "unavailable";
    public bool CanSelectDirectory => HasExecution && !ExecutionFailed && !ShowListStatus &&
        (Episodes.Any(item => item.CanSelect) || Candidates.Any(item => item.PlaybackTarget is not null));
    public bool CanRetryMedia => HasExecution && !ExecutionFailed && session?.SnapshotFor(this) is { } executing &&
        FilmJson.Text(executing.Raw, "retryMediaAttemptId").Length > 0;
    public bool CanRetryLoading => HasExecution && !ExecutionFailed && !ListLoading &&
        FilmJson.Text(snapshot?.Raw ?? default, "initializationError").Length > 0;
    public bool HasOutput => session?.IsCurrentPage(this) == true && session.HasOutput;
    public bool NoOutput => !HasOutput;
    public bool OwnsNativeMedia => session?.IsCurrentPage(this) == true && session.OwnsNativeMedia;
    public bool IsPlaying => HasOutput && session?.IsPlaying == true;
    public double Position => session?.Position ?? 0;
    public double Duration => session?.Duration ?? 0;
    public double BufferedUntil => session?.BufferedUntil ?? 0;
    public bool CanSeek => HasOutput && session?.CanSeek == true;
    public bool IsMuted => session?.IsMuted ?? false;
    public double PlaybackRate => session?.Rate ?? 1;
    public string? PlaybackAspect => session?.Aspect;
    internal string? MediaIdentity => session?.MediaIdentity;
    internal string? CurrentAttemptId => session?.SnapshotFor(this)?.CurrentId;
    internal string? PendingAttemptId => session?.SnapshotFor(this)?.AttemptId is { Length: > 0 } id ? id : null;
    internal string? BlockAttemptId => CurrentAttemptId is { Length: > 0 } current ? current : PendingAttemptId;
    public bool CanBlockAttempt => HasExecution && !ExecutionFailed && BlockAttemptId is { Length: > 0 };
    internal string CurrentStage => PlayerStage;
    internal string CurrentSnapshotError => FilmJson.Text(session?.SnapshotFor(this)?.Raw ?? snapshot?.Raw ?? default, "error");
    public string ProgressLabel => $"{FilmJson.Time(Position)} / {FilmJson.Time(Duration)}";
    public int Volume
    {
        get => session?.Volume ?? 0;
        set { if (HasOutput) RequireSession().Volume = value; Changed(nameof(Volume)); }
    }
    public FrameworkElement? MediaView => session?.MediaView;
    public bool ShortVideo => FilmJson.Bool(snapshot?.Raw ?? default, "shortVideo");
    public bool PreviewEpisodes => FilmJson.Bool(snapshot?.Raw ?? default, "previewEpisodes");
    public bool HasCurrent => HasExecution && session?.SnapshotFor(this)?.CurrentId.Length > 0;
    public string StatusText => ExecutionFailed ? FilmPlaybackError.Message(session!.ExecutionError, "播放状态提交失败") :
        connectionError.Length > 0 ? FilmPlaybackError.Message(connectionError, "连接失败") : PlayerStage switch
    {
        "loading" => "加载中…", "searching" => "匹配中…", "resolving" => "取址中…",
        "prepared" => "已准备", "pending" => "准备播放…", "playing" => PlayerBuffering ? "缓冲中…" : HasOutput ? IsPlaying ? "播放中" : "已暂停" : "准备播放…",
        "error" => FilmPlaybackError.Message(FilmJson.Text(session?.SnapshotFor(this)?.Raw ?? snapshot?.Raw ?? default, "error"), "播放失败"),
        "unavailable" => "无可用视频源", "ended" => "已结束", "stopped" => "已停止", _ => ""
    };
    public bool ConnectionFailed => connectionError.Length > 0;
    public bool ExecutionFailed => session?.IsCurrentPage(this) == true && session.ExecutionError.Length > 0;
    public bool NeedsRecovery => ConnectionFailed || ExecutionFailed;
    public bool ReportFailed => FilmJson.Text(snapshot?.Raw ?? default, "reportError").Length > 0;
    public string ReportErrorMessage => ReportFailed ? "观看上报失败" : "";
    public bool MetadataAssociationFailed => FilmJson.Text(snapshot?.Raw ?? default, "metadataError").Length > 0;
    public string CurrentCandidateId => HasExecution ? FilmJson.Text(FilmJson.Member(snapshot?.Current ?? default, "candidate"), "id") : "";
    internal JsonElement RawBrowsing => display?.Browsing ?? default;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(FilmPlaybackSnapshot value, bool updateBrowsing = true)
    {
        bool wasApplying = Applying;
        Applying = true;
        try
        {
            string previousIdentity = FavoriteTarget.Id;
            var previous = snapshot;
            var previousWork = workInfo;
            var previousBrowsing = RawBrowsing;
            var previousDisplayMetadata = DisplayMetadata;
            var previousDisplayKind = DisplayKind;
            var previousEpisodes = Episodes;
            var previousCandidates = Candidates;
            var previousDirectory = EpisodeDirectory;
            if (previous is not null && previous.SessionId != value.SessionId)
            { preservingRead = true; unreadDirectories.UnionWith(directories.Keys); }
            if (previous is not null && !FilmJson.Equal(FilmJson.Member(previous.Raw, "sources"),
                FilmJson.Member(value.Raw, "sources")))
            {
                var currentSources = FilmJson.Array(value.Raw, "sources").ToDictionary(row => FilmJson.Text(row, "id"));
                foreach (var row in FilmJson.Array(previous.Raw, "sources"))
                {
                    string id = FilmJson.Text(row, "id");
                    if (!currentSources.TryGetValue(id, out var next) ||
                        new[] { "key", "engine", "installationId", "siteId", "generation", "revision", "enabled" }.Any(name =>
                            !FilmJson.Equal(FilmJson.Member(FilmJson.Member(row, "source"), name),
                                FilmJson.Member(FilmJson.Member(next, "source"), name))))
                        unreadDirectories.UnionWith(directories.Keys.Where(key => key.Source == id));
                }
            }
            if (previous?.SessionId == value.SessionId &&
                !FilmJson.Equal(FilmJson.Member(previous.Raw, "metadataSources"), FilmJson.Member(value.Raw, "metadataSources")))
            {
                var available = FilmJson.Array(value.Raw, "metadataSources").Select(row => FilmJson.Text(row, "id"))
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var key in directories.Keys.Where(key => key.Metadata is not null && !available.Contains(key.Metadata)).ToArray())
                {
                    directories.Remove(key);
                    unreadDirectories.Remove(key);
                    rememberedSelections.Remove((key.Source, key.Metadata));
                }
                if (selection?.Metadata is { } selected && !available.Contains(selected))
                {
                    selectionLocked = false;
                    CancelBrowseRead();
                    explicitRead = false;
                    preservingRead = false;
                }
            }
            snapshot = value;
            bool retainingWork = previous?.EntryTarget.Id == value.EntryTarget.Id && value.WorkInfo is null &&
                (FilmJson.Text(value.Raw, "stage") is "loading" or "error" ||
                    FilmJson.Text(value.Raw, "metadataError").Length > 0 || FilmJson.Bool(value.Browsing, "loading") ||
                    FilmJson.Text(value.Browsing, "error").Length > 0);
            if (!retainingWork) workInfo = value.WorkInfo;
            if (updateBrowsing) UpdateBrowsing(value, previous is null ||
                !FilmJson.Equal(FilmJson.Member(previous.Raw, "sources"), FilmJson.Member(value.Raw, "sources")));
            if (previousIdentity != FavoriteTarget.Id) favorite = false;
            bool metadataChanged = previous is null || (previousWork is { } work
                ? !work.SameContent(workInfo) : workInfo is not null);
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
                if (!MetadataSources.Any(row => row.Provider == aggregateProvider))
                    aggregateProvider = MetadataSources.FirstOrDefault(row => row.Provider == EntryTarget.MetadataProvider)?.Provider ??
                        MetadataSources.FirstOrDefault()?.Provider;
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
            }
            if (previous is null || !FilmJson.Equal(FilmJson.Member(previousBrowsing, "lines"), FilmJson.Member(RawBrowsing, "lines")))
            {
                var lines = FilmJson.Array(RawBrowsing, "lines").Select(line =>
                {
                    var id = FilmJson.Identity(line, "id", required: true);
                    string provider = FilmJson.Text(line, "provider");
                    return new FilmSelection(FilmJson.String(id), FilmJson.Text(line, "label") is { Length: > 0 } label ? label :
                        PlayerPanName(provider),
                        WireId: id, Provider: provider.Length > 0 ? provider : null);
                }).ToArray();
                if (Lines.Count != lines.Length || !Lines.Zip(lines).All(pair => (pair.First with { WireId = null }) ==
                    (pair.Second with { WireId = null }) &&
                    FilmJson.Equal(pair.First.WireId ?? default, pair.Second.WireId ?? default)))
                { Lines = lines; Changed(nameof(Lines)); }
            }
            if (previous is null || previousDisplayKind != DisplayKind || !FilmJson.Equal(previousDisplayMetadata, DisplayMetadata))
            {
                var trustedSeasons = FilmJson.Member(DisplayMetadata, "doubanSeasons");
                var seasons = trustedSeasons.ValueKind == JsonValueKind.Array ? trustedSeasons : FilmJson.Member(DisplayMetadata, "seasons");
                var nextSeasons = DisplayKind == "movie" ? [] :
                    FilmJson.Array(seasons).Select(row => new { Row = row, Number = FilmJson.Integer(row, "season_number") ?? FilmJson.Integer(row, "season") })
                        .Where(row => row.Number is not null)
                        .Select(row => new FilmSelection(null, FilmJson.Text(row.Row, "name") is { Length: > 0 } name ? name :
                            row.Number == 0 ? "特别篇" : $"第{row.Number}季", row.Number,
                            BusinessId: FilmJson.RequiredText(row.Row, "businessId"),
                            PlaybackTarget: FilmJson.Target(row.Row.GetProperty("playbackTarget")))).DistinctBy(row => row.Season).ToArray();
                if (Seasons.Count != nextSeasons.Length || !Seasons.Zip(nextSeasons).All(pair => SameSeason(pair.First, pair.Second)))
                { Seasons = nextSeasons; Changed(nameof(Seasons)); }
            }
            EpisodeDirectory?.Select(this);
            if (metadataChanged)
            {
                var recommendations = workInfo is { } info ? Similar(info.Metadata, info.Provider, info.Kind) : [];
                if (!Recommendations.Select(item => (item.Id, item.Title, item.Poster, item.Score)).SequenceEqual(
                    recommendations.Select(item => (item.Id, item.Title, item.Poster, item.Score))))
                { Recommendations = recommendations; Changed(nameof(Recommendations)); }
                foreach (string property in MetadataProperties) Changed(property);
            }
            if (!ReferenceEquals(previousDirectory, EpisodeDirectory)) Changed(nameof(EpisodeDirectory));
            if (!ReferenceEquals(previousEpisodes, Episodes)) Changed(nameof(Episodes));
            if (!ReferenceEquals(previousCandidates, Candidates)) Changed(nameof(Candidates));
            foreach (string property in SnapshotProperties) Changed(property);
            if (previous is null || new[] { "stage", "error", "attempt", "current", "target", "prefetch", "reportError", "constraint", "shortVideo" }
                .Any(name => !FilmJson.Equal(FilmJson.Member(previous.Raw, name), FilmJson.Member(value.Raw, name))))
                NotifyPlayback();
        }
        finally { Applying = wasApplying; }
    }

    private static bool SameSeason(FilmSelection left, FilmSelection right) =>
        (left with { PlaybackTarget = null }) == (right with { PlaybackTarget = null }) &&
        (left.PlaybackTarget is null
            ? right.PlaybackTarget is null
            : right.PlaybackTarget is { } next &&
                (left.PlaybackTarget with { Source = null, Members = null }) == (next with { Source = null, Members = null }) &&
                FilmJson.Equal(left.PlaybackTarget.Source ?? default, next.Source ?? default) &&
                FilmJson.Equal(left.PlaybackTarget.Members ?? default, next.Members ?? default));

    internal void ReleaseDirectories()
    {
        CancelBrowseRead();
        directories.Clear(); rememberedSelections.Clear(); unreadDirectories.Clear();
        display = null; selection = null; EpisodeDirectory = null;
        Changed(nameof(EpisodeDirectory)); Changed(nameof(Episodes)); Changed(nameof(Candidates));
    }

    internal bool Selected(FilmEpisodeItem item)
    {
        if (snapshot is null) return false;
        if (item.CandidateId is not null) return item.CandidateId == FilmJson.Text(
            FilmJson.Member(snapshot.AttemptId.Length > 0 ? snapshot.Attempt : snapshot.Current, "candidate"), "id");
        return item.Season == snapshot.Target.Season && item.Number == snapshot.Target.Episode;
    }
    internal void NotifyMedia(params string[] properties)
    { foreach (string property in properties.Length > 0 ? properties : MediaProperties) Changed(property); }
    internal void ExecutionEnded()
    {
        metadataChoiceGeneration++;
        CancelBrowseRead();
        SyncMediaTracksAndChapters(null, [], 0);
        foreach (string property in SnapshotProperties) Changed(property);
        NotifyPlayback();
        NotifyMedia();
    }
    internal void SetConnectionError(string code)
    {
        if (connectionError == code) return;
        connectionError = code;
        foreach (string property in SnapshotProperties) Changed(property);
        NotifyPlayback();
    }
    private void NotifyPlayback() { foreach (string property in PlaybackProperties) Changed(property); }
    internal void NotifyExecutionError()
    {
        foreach (string property in SnapshotProperties) Changed(property);
        NotifyPlayback();
    }
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
        var executing = session?.SnapshotFor(this);
        string attemptId = FilmJson.Text(executing?.Raw ?? default, "retryMediaAttemptId");
        return CanRetryMedia ? CommandAsync("media.retry", new { attemptId }) : Task.CompletedTask;
    }
    internal Task RetryLoadingAsync() => CanRetryLoading ? CommandAsync("browse.refresh") : Task.CompletedTask;
    internal Task SeekAsync(double value) => RequireSession().SeekAsync(value);
    internal Task RateAsync(double value) => RequireSession().RateAsync(value);
    internal Task AspectAsync(string? value) => RequireSession().AspectAsync(value);
    internal Task ReconnectAsync()
    {
        if (session is null || !session.IsCurrentPage(this)) throw new FilmPlaybackException("STALE_SESSION");
        return session.ReconnectAsync();
    }
    internal void RetainOwner(MeowFilm.Desktop.DesktopWindow? owner) => session?.RetainOwner(this, owner);
    internal Task EndRetainedOwnerAsync() => session?.EndRetainedOwnerAsync(this) ?? Task.CompletedTask;
    internal Task SelectEpisodeAsync(FilmEpisodeItem item)
    {
        if (!CanSelectDirectory || !Episodes.Contains(item) || !item.CanSelect)
            throw new FilmPlaybackException("INVALID_REQUEST");
        return SelectTargetAsync(item.PlaybackTarget ?? throw new FilmPlaybackException("INVALID_RESPONSE"));
    }
    internal Task SelectCandidateAsync(string id)
    {
        if (!CanSelectDirectory) throw new FilmPlaybackException("INVALID_REQUEST");
        var item = Candidates.FirstOrDefault(item => item.Id == id) ?? throw new FilmPlaybackException("INVALID_REQUEST");
        return SelectTargetAsync(item.PlaybackTarget ?? throw new FilmPlaybackException("INVALID_RESPONSE"));
    }
    internal Task SelectOriginalTargetAsync(JsonElement playbackTarget)
    {
        var row = FilmJson.Array(RawBrowsing, "originalEpisodes").FirstOrDefault(row =>
            FilmJson.Equal(FilmJson.Member(row, "playbackTarget"), playbackTarget));
        if (!CanBrowseRaw || row.ValueKind != JsonValueKind.Object)
            throw new FilmPlaybackException("INVALID_REQUEST");
        return SelectTargetAsync(FilmJson.FileTarget(playbackTarget));
    }
    private Task SelectTargetAsync(FilmPlaybackTarget target)
    {
        target.Validate();
        return CommandAsync("select.target", new Dictionary<string, object?>
        {
            ["target"] = target.Wire(), ["metadataSourceId"] = MetadataSourceId
        });
    }
    internal async Task LoadFavoriteAsync()
    {
        if (library is null) return;
        string identity = FavoriteTarget.Id;
        var items = await library.ReadAsync();
        if (identity == FavoriteTarget.Id) RefreshFavorite(items);
    }
    private FilmPlaybackTarget FavoriteTarget => EntryTarget;
    internal void RefreshFavorite(IReadOnlyList<FilmCardItem> items)
    {
        favorite = items.Any(item => item.PlaybackContext?.Target.Id == FavoriteTarget.Id);
        Changed(nameof(IsFavorite));
    }
    internal async Task ToggleFavoriteAsync()
    {
        if (library is null || favoriteBusy) return;
        favoriteBusy = true; Changed(nameof(CanFavorite));
        string identity = FavoriteTarget.Id;
        bool nextState = !favorite;
        try
        {
            await library.SetFavoriteAsync(Item with
            {
                Id = identity, Title = workInfo?.Title ?? EntryTarget.Title, Poster = Poster, Score = Score,
                SearchContext = null, PlaybackContext = CaptureRequest with { Target = FavoriteTarget }
            }, nextState);
            if (FavoriteTarget.Id == identity) favorite = nextState;
        }
        finally { favoriteBusy = false; Changed(nameof(CanFavorite)); Changed(nameof(IsFavorite)); }
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
