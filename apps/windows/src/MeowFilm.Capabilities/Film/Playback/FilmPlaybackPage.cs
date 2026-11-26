using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

/// <summary>Stable presentation projection of runtime-owned film session snapshots.</summary>
public sealed partial class FilmPlaybackPage : INotifyPropertyChanged
{
    private readonly FilmPlaybackSession? session;
    private readonly FilmLibrary? library;
    private readonly FilmPlaybackSettings? preferences;
    private FilmPlaybackSnapshot? snapshot;
    private FilmWorkSnapshot? workInfo;
    private bool isListLayout, overviewExpanded, favorite, favoriteBusy, searchMoreBusy, episodesMoreBusy;
    private string connectionError = "";

    private static readonly string[] MetadataProperties =
    [
        nameof(Title), nameof(Poster), nameof(Backdrop), nameof(Metadata), nameof(Genres),
        nameof(Score), nameof(HasScore), nameof(ScoreProvider),
        nameof(Directors), nameof(HasDirectors), nameof(Cast), nameof(HasCast),
        nameof(Overview), nameof(HasOverview)
    ];

    private static readonly string[] SnapshotProperties =
    [
        nameof(SourceId), nameof(LineId), nameof(Season),
        nameof(SelectedSource), nameof(SelectedLine), nameof(SelectedSeason), nameof(SelectedEpisode),
        nameof(CanBrowseSources), nameof(ShowLinePicker), nameof(CanBrowseLines),
        nameof(SourcePlaceholder), nameof(LinePlaceholder),
        nameof(IsFavorite), nameof(CanFavorite),
        nameof(HasMetadataMappings), nameof(HasSeasons), nameof(HasImageEpisodes),
        nameof(ShowEpisodeCards), nameof(ShowEpisodeList), nameof(ShowMovieCandidates),
        nameof(CanBrowseRaw), nameof(ActiveItemId),
        nameof(CanSearchMore), nameof(PreviewEpisodes), nameof(CanTogglePreview),
        nameof(ShowLoadMoreEpisodes), nameof(CanLoadMoreEpisodes), nameof(LoadMoreEpisodesText),
        nameof(ListLoading), nameof(ListError), nameof(ShowListStatus), nameof(ListStatus),
        nameof(ListMessage), nameof(ShowListMessage),
        nameof(CanPlay), nameof(CanSelectDirectory), nameof(CanRetryMedia), nameof(CanRetryLoading),
        nameof(ConnectionFailed), nameof(ExecutionFailed), nameof(NeedsRecovery)
    ];

    private static readonly string[] PlaybackProperties =
    [
        nameof(HasCurrent), nameof(CanBlockAttempt), nameof(ActiveItemId), nameof(ShortVideo),
        nameof(ReportFailed),
        nameof(PlayerPanLabel), nameof(PlayerQualityLabel),
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

    internal FilmPlaybackPage(FilmCardItem item, FilmEntry entry,
        FilmPlaybackSession? session, FilmLibrary? library, FilmPlaybackSettings? preferences = null)
    {
        entry.Validate();
        Entry = entry.Own();
        Item = item with { Entry = Entry };
        this.session = session;
        this.library = library;
        this.preferences = preferences;
        NavigationIdentity = Guid.NewGuid().ToString("N");
    }

    public FilmCardItem Item { get; private set; }
    public FilmEntry Entry { get; private set; }
    public string NavigationIdentity { get; }
    public FilmSearchContext? SearchContext => Item.SearchContext;

    internal void UpdateItem(FilmCardItem item)
    {
        if (item.Entry is { } nextEntry)
        {
            nextEntry.Validate();
            Entry = nextEntry.Own();
        }
        Item = item with { Entry = Entry };
        Changed("");
    }

    internal void Release()
    {
        metadataChoiceGeneration++;
        EpisodeDirectory.Apply("empty", default, "", "");
        Changed(nameof(Episodes));
        Changed(nameof(Candidates));
    }

    internal string TargetId => snapshot?.TargetId is { Length: > 0 } id ? id : Entry.TargetId;
    internal bool NoSources => snapshot?.NoSources == true;
    internal bool HasPlaybackSnapshot => snapshot is not null;
    internal bool HasExecution => session?.IsCurrentPage(this) == true && session.HasSession && !ConnectionFailed;
    internal double PageOffset { get; set; }
    internal double MetadataOffset { get; set; }
    internal double RecommendationOffset { get; set; }

    public FilmEpisodeDirectory EpisodeDirectory { get; } = new();
    public IReadOnlyList<FilmEpisodeItem> Episodes => EpisodeDirectory.Episodes;
    public IReadOnlyList<FilmCandidateItem> Candidates => EpisodeDirectory.Candidates;
    public IReadOnlyList<FilmCardItem> Recommendations { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Sources { get; private set; } = [];
    public IReadOnlyList<FilmSelection> MetadataSources { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Lines { get; private set; } = [];
    public IReadOnlyList<FilmSelection> Seasons { get; private set; } = [];

    public string? SourceId => snapshot?.SourceSelectedId;
    public string? LineId => snapshot?.LineSelectedId;
    public int? Season => snapshot?.DirectorySelectedSeason;
    public FilmSelection? SelectedSource => SourceId is { } id ? Sources.FirstOrDefault(s => s.Id == id) : null;
    public FilmSelection? SelectedLine => LineId is { } id ? Lines.FirstOrDefault(l => l.Id == id) : null;
    public FilmSelection? SelectedSeason => Season is { } s
        ? Seasons.FirstOrDefault(x => x.Season == s) ?? Seasons.FirstOrDefault()
        : Seasons.FirstOrDefault();
    public FilmEpisodeItem? SelectedEpisode => Episodes.FirstOrDefault(e => e.IsSelected);

    public bool CanBrowseSources => HasExecution && !NoSources && snapshot?.SourceDisabled != true && Sources.Count > 0;
    public bool ShowLinePicker => snapshot?.LineVisible == true;
    public bool CanBrowseLines => HasExecution && ShowLinePicker && Lines.Count > 0 &&
        (!ListLoading || snapshot?.DirectoryRefreshing == true);
    public string SourcePlaceholder => NoSources ? "暂无可用视频源" : snapshot?.SourcePlaceholder ?? "加载中…";
    public string LinePlaceholder => ListLoading ? "加载中…" : "选择线路";

    public bool IsListLayout => isListLayout;
    public bool IsHorizontalLayout => !isListLayout;
    public bool OverviewExpanded => overviewExpanded;
    public bool IsFavorite => favorite;
    public bool CanFavorite => library is not null && !favoriteBusy;
    public bool Applying { get; private set; }

    private string DirectoryMode => snapshot?.DirectoryMode ?? "empty";
    public bool HasMetadataMappings => MetadataSources.Count > 1;
    public bool HasSeasons => Seasons.Count > 1;
    public bool HasImageEpisodes => DirectoryMode is "episodes" or "files" && Episodes.Count > 0;
    public bool ShowEpisodeCards => !ShowListStatus && HasImageEpisodes && IsHorizontalLayout;
    public bool ShowEpisodeList => !ShowListStatus && HasImageEpisodes && IsListLayout;
    public bool ShowMovieCandidates => !ShowListStatus && DirectoryMode == "movies" && Candidates.Count > 0;

    internal JsonElement DirectoryRawItems => snapshot?.DirectoryRawItems ?? default;
    internal string ActiveItemId =>
        Episodes.FirstOrDefault(e => e.IsSelected)?.Identity ??
        Candidates.FirstOrDefault(c => c.IsSelected)?.Id ??
        snapshot?.Current?.CandidateId ??
        snapshot?.Attempt?.CandidateId ?? "";

    public bool CanBrowseRaw => HasExecution && ShowLinePicker &&
        DirectoryRawItems.ValueKind == JsonValueKind.Array && DirectoryRawItems.GetArrayLength() > 0 &&
        ListError.Length == 0 && !ListLoading;

    public bool CanSearchMore => HasExecution && !NoSources && !searchMoreBusy &&
        snapshot?.SourceCanLoadMore == true && !snapshot.SourceLoadingMore;
    public bool PreviewEpisodes => snapshot?.PreviewEpisodes == true;
    public bool CanTogglePreview => HasExecution && snapshot?.CanTogglePreview == true;

    public bool ShowLoadMoreEpisodes => snapshot is not null && !ShowListStatus &&
        (snapshot.DirectoryCanLoadMore || snapshot.DirectoryLoadingMore || episodesMoreBusy);
    public bool CanLoadMoreEpisodes => HasExecution && !episodesMoreBusy &&
        snapshot?.DirectoryCanLoadMore == true && !snapshot.DirectoryLoadingMore;
    public string LoadMoreEpisodesText => (episodesMoreBusy || snapshot?.DirectoryLoadingMore == true) ? "加载中…" : "加载更多";

    public bool ListLoading => snapshot is null || snapshot.DirectoryLoading;
    public string ListError => snapshot?.DirectoryError is { Length: > 0 } err && err != "NO_VIDEO_SOURCES"
        ? FilmPlaybackError.Message(err, "加载失败")
        : "";
    public bool ShowListStatus => ListError.Length > 0 ||
        (ListLoading && snapshot?.DirectoryRefreshing != true && Episodes.Count == 0 && Candidates.Count == 0) ||
        (Episodes.Count == 0 && Candidates.Count == 0);
    public string ListStatus => ListError.Length > 0 ? ListError :
        NoSources ? "暂无可用视频源" :
        (ListLoading || PlayerBusy) ? "加载中…" : "暂无选集";
    public string ListMessage => ListStatus;
    public bool ShowListMessage => ShowListStatus;

    public bool CanPlay => HasExecution && !ExecutionFailed && snapshot is not null && !NoSources;
    public bool CanSelectDirectory => HasExecution && !ExecutionFailed && !NoSources &&
        (Episodes.Count > 0 || Candidates.Count > 0);
    public bool CanRetryMedia => HasExecution && !ExecutionFailed && !NoSources &&
        session?.SnapshotFor(this)?.RetryMediaAttemptId.Length > 0;
    public bool CanRetryLoading => HasExecution && !ExecutionFailed && !NoSources && !ListLoading &&
        (ListError.Length > 0 || CurrentStage == "error");

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
    public bool CanBlockAttempt => HasExecution && !ExecutionFailed && !NoSources &&
        (snapshot?.CanBlockSource == true || BlockAttemptId is { Length: > 0 });
    internal string CurrentStage => PlayerStage;
    internal string CurrentSnapshotError => session?.SnapshotFor(this)?.Error ?? snapshot?.Error ?? "";
    public string ProgressLabel => $"{FilmJson.Time(Position)} / {FilmJson.Time(Duration)}";

    public int Volume
    {
        get => session?.Volume ?? 0;
        set { if (HasOutput) RequireSession().Volume = value; Changed(nameof(Volume)); }
    }

    public FrameworkElement? MediaView => session?.MediaView;
    public bool ShortVideo => snapshot?.ShortVideo == true;
    public bool HasCurrent => HasExecution && session?.SnapshotFor(this)?.CurrentId.Length > 0;

    public string StatusText =>
        ExecutionFailed ? FilmPlaybackError.Message(session!.ExecutionError, "播放状态提交失败") :
        connectionError.Length > 0 ? FilmPlaybackError.Message(connectionError, "连接失败") :
        NoSources ? "暂无可用视频源" :
        PlayerStage switch
        {
            "loading" => "加载中…",
            "searching" or "history" or "manual" or "cached" => "匹配中…",
            "resolving" => "取址中…",
            "prepared" => "已准备",
            "pending" => "准备播放…",
            "playing" => PlayerBuffering ? "缓冲中…" : HasOutput ? IsPlaying ? "播放中" : "已暂停" : "准备播放…",
            "error" => FilmPlaybackError.Message(CurrentSnapshotError, "播放失败"),
            "unavailable" => FilmPlaybackError.Message(CurrentSnapshotError, "未找到匹配片源"),
            "ended" => "已结束",
            "stopped" => "已停止",
            _ => ""
        };

    public bool ConnectionFailed => connectionError.Length > 0;
    public bool ExecutionFailed => session?.IsCurrentPage(this) == true && session.ExecutionError.Length > 0;
    public bool NeedsRecovery => ConnectionFailed || ExecutionFailed;
    public bool ReportFailed => (snapshot?.ReportError.Length ?? 0) > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(FilmPlaybackSnapshot value)
    {
        bool wasApplying = Applying;
        Applying = true;
        try
        {
            string previousTargetId = TargetId;
            var previous = snapshot;
            var previousWork = workInfo;
            var previousEpisodes = Episodes;
            var previousCandidates = Candidates;

            snapshot = value;
            workInfo = value.Work;

            if (previousTargetId != TargetId) favorite = false;

            bool metadataChanged = previous is null || (previousWork is { } oldWork
                ? !oldWork.SameContent(workInfo)
                : workInfo is not null);

            if (!Sources.SequenceEqual(value.SourceOptions))
            {
                Sources = value.SourceOptions;
                Changed(nameof(Sources));
            }

            var nextMetadataSources = value.SourceOptions
                .Where(opt => opt.Kind is "tmdb" or "douban")
                .ToArray();
            if (!MetadataSources.SequenceEqual(nextMetadataSources))
            {
                MetadataSources = nextMetadataSources;
                Changed(nameof(MetadataSources));
            }

            if (!Lines.SequenceEqual(value.LineOptions))
            {
                Lines = value.LineOptions;
                Changed(nameof(Lines));
            }

            if (!Seasons.SequenceEqual(value.DirectorySeasons))
            {
                Seasons = value.DirectorySeasons;
                Changed(nameof(Seasons));
            }

            bool keepExistingWhileRefreshing = value.DirectoryLoading && value.DirectoryRefreshing &&
                (Episodes.Count > 0 || Candidates.Count > 0) &&
                value.DirectoryItems.ValueKind == JsonValueKind.Array &&
                value.DirectoryItems.GetArrayLength() == 0;
            if (!keepExistingWhileRefreshing)
            {
                EpisodeDirectory.Apply(value.DirectoryMode, value.DirectoryItems, Poster, Overview);
            }

            if (metadataChanged)
            {
                var related = value.Work.RelatedCards;
                if (!Recommendations.Select(c => (c.Id, c.Title, c.Poster, c.Score))
                    .SequenceEqual(related.Select(c => (c.Id, c.Title, c.Poster, c.Score))))
                {
                    Recommendations = related;
                    Changed(nameof(Recommendations));
                }
                foreach (string property in MetadataProperties) Changed(property);
            }

            if (!ReferenceEquals(previousEpisodes, Episodes)) Changed(nameof(Episodes));
            if (!ReferenceEquals(previousCandidates, Candidates)) Changed(nameof(Candidates));
            foreach (string property in SnapshotProperties) Changed(property);
            NotifyPlayback();
        }
        finally
        {
            Applying = wasApplying;
        }
    }

    internal void NotifyMedia(params string[] properties)
    {
        foreach (string property in properties.Length > 0 ? properties : MediaProperties) Changed(property);
    }

    internal void ExecutionEnded()
    {
        metadataChoiceGeneration++;
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

    private void NotifyPlayback()
    {
        foreach (string property in PlaybackProperties) Changed(property);
    }

    internal void NotifyExecutionError()
    {
        foreach (string property in SnapshotProperties) Changed(property);
        NotifyPlayback();
    }

    internal void SetListLayout(bool value)
    {
        if (isListLayout == value) return;
        isListLayout = value;
        Changed(nameof(IsListLayout));
        Changed(nameof(IsHorizontalLayout));
        Changed(nameof(ShowEpisodeCards));
        Changed(nameof(ShowEpisodeList));
    }

    internal void ToggleOverview()
    {
        if (!HasOverview) return;
        overviewExpanded = !overviewExpanded;
        Changed(nameof(OverviewExpanded));
    }

    private FilmPlaybackSession RequireSession()
    {
        if (session is null) throw new FilmPlaybackException("METHOD_UNAVAILABLE");
        session.RequirePage(this);
        return session;
    }

    internal Task CommandAsync(string action, object? parameters = null) =>
        RequireSession().CommandAsync(action, parameters);

    internal Task PlayAsync() => CommandAsync("play");
    internal Task PauseOrResumeAsync() => HasOutput ? RequireSession().PauseAsync() : Task.CompletedTask;

    internal async Task SearchMoreSourcesAsync()
    {
        if (!CanSearchMore) return;
        searchMoreBusy = true;
        Changed(nameof(CanSearchMore));
        try { await CommandAsync("search.more"); }
        finally { searchMoreBusy = false; Changed(nameof(CanSearchMore)); }
    }

    internal async Task LoadMoreEpisodesAsync()
    {
        if (!CanLoadMoreEpisodes) return;
        episodesMoreBusy = true;
        Changed(nameof(ShowLoadMoreEpisodes));
        Changed(nameof(CanLoadMoreEpisodes));
        Changed(nameof(LoadMoreEpisodesText));
        try { await CommandAsync("episodes.more"); }
        finally
        {
            episodesMoreBusy = false;
            Changed(nameof(ShowLoadMoreEpisodes));
            Changed(nameof(CanLoadMoreEpisodes));
            Changed(nameof(LoadMoreEpisodesText));
        }
    }

    internal Task RetryMediaAsync()
    {
        string attemptId = session?.SnapshotFor(this)?.RetryMediaAttemptId ?? "";
        return CanRetryMedia && attemptId.Length > 0 ? CommandAsync("media.retry", new { attemptId }) : Task.CompletedTask;
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
        return SelectItemAsync(item.Identity);
    }

    internal Task SelectCandidateAsync(string id)
    {
        if (!CanSelectDirectory || Candidates.All(item => item.Id != id))
            throw new FilmPlaybackException("INVALID_REQUEST");
        return SelectItemAsync(id);
    }

    internal Task SelectItemAsync(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) throw new FilmPlaybackException("INVALID_REQUEST");
        return CommandAsync("select.item", new { itemId });
    }

    internal async Task LoadFavoriteAsync()
    {
        if (library is null) return;
        string identity = TargetId;
        var items = await library.ReadAsync();
        if (identity == TargetId) RefreshFavorite(items);
    }

    internal void RefreshFavorite(IReadOnlyList<FilmCardItem> items)
    {
        string targetId = TargetId;
        favorite = items.Any(item => item.Entry?.TargetId == targetId || item.Id == targetId);
        Changed(nameof(IsFavorite));
    }

    internal async Task ToggleFavoriteAsync()
    {
        if (library is null || favoriteBusy) return;
        favoriteBusy = true;
        Changed(nameof(CanFavorite));
        string targetId = TargetId;
        bool nextState = !favorite;
        try
        {
            var favEntry = new FilmEntry(
                TargetId: targetId,
                Keyword: workInfo?.Keyword is { Length: > 0 } kw ? kw : Entry.Keyword,
                Title: Title,
                Subtitle: workInfo?.Subtitle is { Length: > 0 } sub ? sub : Entry.Subtitle,
                Poster: Poster,
                Backdrop: Backdrop,
                Score: Score,
                TextBadge: Entry.TextBadge,
                SiteLabel: Entry.SiteLabel,
                Kind: workInfo?.Kind ?? Entry.Kind,
                Year: int.TryParse(workInfo?.Year, out int yr) && yr > 0 ? yr : Entry.Year,
                Tmdb: workInfo?.Tmdb ?? Entry.Tmdb,
                Douban: workInfo?.Douban ?? Entry.Douban,
                SiteEntry: Entry.SiteEntry);
            await library.SetFavoriteAsync(Item with
            {
                Id = targetId,
                Title = Title,
                Poster = Poster,
                Score = Score,
                SearchContext = null,
                Entry = favEntry
            }, nextState);
            if (TargetId == targetId) favorite = nextState;
        }
        finally
        {
            favoriteBusy = false;
            Changed(nameof(CanFavorite));
            Changed(nameof(IsFavorite));
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
