using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Search;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Capabilities;

/// <summary>Standalone owner for online comprehensive music search sessions, tabs, sections, and incremental results.</summary>
public sealed class MusicSearchController : MusicController
{
    internal const string SearchOverview = "overview";
    private readonly MusicSettingsController settings;
    private readonly MusicAccountsController accounts;
    private readonly MusicCatalogActionState catalogActions;
    private readonly MusicContentState contentState;
    private readonly Dictionary<string, ObservableCollection<MusicCatalogSection>> searchSections;
    private readonly Dictionary<string, MusicSearchSnapshot> searchSnapshots = [];
    private readonly Dictionary<string, SearchCardsCache> searchCardCache = [];
    private readonly Dictionary<string, double> searchScroll = [];
    private readonly Dictionary<string, string> searchIds = [];
    private string? searchSelectedKey;
    private string searchKeyword = "", searchKind = "songs", searchView = SearchOverview;
    private long searchEpoch, searchAccountEpoch;
    private bool searchStarting, searchRefresh;
    private Task searchStartTail = Task.CompletedTask;
    private readonly Dictionary<string, SearchStartFlight> searchStarts = [];

    private sealed record SearchStartKey(string Keyword, string Kind, string Platforms, long AccountEpoch);
    private sealed class SearchStartFlight(SearchStartKey key, long epoch)
    {
        internal SearchStartKey Key { get; } = key;
        internal long Epoch { get; } = epoch;
        internal Task Work { get; set; } = Task.CompletedTask;
    }
    private sealed record SearchCardsCache(MusicSongRecord[]? Songs, MusicSearchPlaylistRecord[]? Playlists, MusicCatalogCard[] Cards);

    internal MusicSearchController(
        MusicRuntimeContext ctx,
        MusicSettingsController settings,
        MusicAccountsController accounts,
        MusicCatalogActionState catalogActions,
        MusicContentState contentState) : base(ctx)
    {
        this.settings = settings;
        this.accounts = accounts;
        this.catalogActions = catalogActions;
        this.contentState = contentState;
        searchSections = new()
        {
            [SearchOverview] = contentState.SearchOverviewSections,
            ["songs"] = contentState.SearchSongSections,
            ["playlists"] = contentState.SearchPlaylistSections
        };
        application.SearchChanged += SearchChanged;
        contentState.SelectionChanged += OnContentSelectionChanged;
        contentState.SelectionCleared += NotifySelectionChanged;
    }

    internal event Action? CatalogPreviewSurfaceRetiring;
    internal event Action<string, string>? SearchSessionStarted;
    internal event Action<string, string>? SearchMetadataPublished;
    internal event Action? CatalogTagsClearing;

    private bool CanBrowse => !stopping && commands == 0;

    public string[] SearchKinds { get; } = ["歌曲", "歌单"];
    public string SearchKindLabel
    {
        get => contentState.SearchKindLabel;
        set
        {
            if (contentState.SearchKindLabel == value) return;
            contentState.SearchKindLabel = value;
            Changed();
        }
    }

    public ObservableCollection<MusicCatalogSection> OverviewSections => contentState.SearchOverviewSections;
    internal ObservableCollection<MusicCatalogSection> PlaylistSections => contentState.SearchPlaylistSections;
    internal ObservableCollection<MusicCatalogSection> ActiveSections => searchSections[searchView];
    internal IEnumerable<MusicCatalogSection> AllSearchSections => searchSections.Values.SelectMany(value => value);

    internal void ClearSearchSections()
    {
        foreach (var sections in searchSections.Values) sections.Clear();
    }

    public ObservableCollection<SongRow> SearchSongsSource { get; } = [];
    public SongRow? SearchSelectedSong
    {
        get => IsSearchPage ? contentState.SelectedSong : SearchSongsSource.FirstOrDefault(song => song.SelectionKey == searchSelectedKey);
        set { if (IsSearchPage) contentState.SetSelectedSong(value); }
    }
    internal string SearchKeyword => searchKeyword;
    internal string SearchView => searchView;
    internal long SearchAccountEpoch => searchAccountEpoch;

    public bool IsSearchPage => ctx.CurrentRoute == "catalog-search";
    public string SearchTab => searchView;
    public bool IsSearchOverview => IsSearchPage && searchView == SearchOverview;
    public bool IsSearchSongs => IsSearchPage && searchView == "songs";
    public bool IsSearchPlaylists => IsSearchPage && searchView == "playlists";
    public bool HasSearchSession => searchKeyword.Length > 0 &&
        (searchSnapshots.Count > 0 || searchStarts.Count > 0);
    public bool CanSearchMore => CanBrowse && !IsSearchOverview && !searchStarting &&
        searchSnapshots.TryGetValue(searchKind, out var snapshot) &&
        !snapshot.Loading && snapshot.More;
    public bool CanLoadMoreSearchSongs => IsSearchSongs && CanSearchMore;
    public bool CanLoadMoreSearchPlaylists => IsSearchPlaylists && CanSearchMore;
    public bool SearchSongsLoading => SearchKindLoading("songs");
    public bool SearchPlaylistsLoading => SearchKindLoading("playlists");
    public bool ShowSearchLoading => IsSearchSongs && SearchSongsLoading && SearchSongsSource.Count == 0;
    public string SearchStatus { get; private set; } = "";

    private void SyncSharedState()
    {
        contentState.SearchKeyword = searchKeyword;
        contentState.SearchView = searchView;
        contentState.SearchAccountEpoch = searchAccountEpoch;
        contentState.HasSearchSession = HasSearchSession;
    }

    private void OnContentSelectionChanged(SongRow? value)
    {
        if (IsSearchPage) searchSelectedKey = value?.SelectionKey;
        Changed(nameof(SearchSelectedSong));
    }

    internal void Unsubscribe()
    {
        application.SearchChanged -= SearchChanged;
        contentState.SelectionChanged -= OnContentSelectionChanged;
        contentState.SelectionCleared -= NotifySelectionChanged;
    }

    internal void CancelForShutdown() => searchEpoch++;
    private IEnumerable<MusicCatalogCard> CachedSearchCards() => searchCardCache.Values.SelectMany(value => value.Cards);
    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, MusicSongRecord?> read, long publication)
    {
        foreach (var song in SearchSongsSource)
            if (ids.Contains(song.Id)) song.ApplyRepository(read(song.Id), publication);
        var cards = MusicCatalogSection.RepositoryCards(AllSearchSections)
            .Concat(CachedSearchCards())
            .Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance);
        foreach (var card in cards)
        {
            if (card.Song is { } song && ids.Contains(song.Id)) card.RefreshSong(read(song.Id), publication);
            if (card.PreviewSongs.Any(preview => preview.Song is { } s && ids.Contains(s.Id)))
                card.RefreshPreview(read, publication);
        }
    }

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        Changed(nameof(SearchSelectedSong));
        Changed(nameof(SearchKindLabel));
        SearchBindings();
    }

    internal void NotifyRouteChanged(bool refreshBindings = true)
    {
        SyncSharedState();
        Changed(nameof(IsSearchPage));
        Changed(nameof(SearchTab));
        Changed(nameof(IsSearchOverview));
        Changed(nameof(IsSearchSongs));
        Changed(nameof(IsSearchPlaylists));
        if (refreshBindings) SearchBindings();
    }

    internal void NotifySelectionChanged() => Changed(nameof(SearchSelectedSong));

    private void SearchChanged(MusicSearchSnapshot snapshot)
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (disposed || stopping) return;
            try { AcceptSearchSnapshot(snapshot); }
            catch { SearchStatus = "搜索结果格式无效"; SearchBindings(); }
        }, DispatcherQueuePriority.Low);
    }

    internal void SearchBindings()
    {
        SyncSharedState();
        Changed(nameof(HasSearchSession));
        Changed(nameof(ShowSearchLoading));
        Changed(nameof(CanSearchMore));
        Changed(nameof(CanLoadMoreSearchSongs));
        Changed(nameof(CanLoadMoreSearchPlaylists));
        Changed(nameof(SearchSongsLoading));
        Changed(nameof(SearchPlaylistsLoading));
        Changed(nameof(SearchStatus));
    }

    internal void RememberSearch()
    {
        if (IsSearchPage)
        {
            searchScroll[searchView] = ctx.CaptureScroll();
            if (contentState.SelectedSong is { } sel) searchSelectedKey = sel.SelectionKey;
        }
    }

    internal async Task BeginSearchAsync(string keyword, CancellationToken token, bool refresh = false, bool overview = false)
    {
        if (stopping) return;
        RememberSearch();
        var previousSelection = ctx.CaptureContentSelection();
        string view = searchKeyword != keyword || overview ? SearchOverview : searchView;
        string kind = view == "playlists" ? "playlists" : "songs";
        if (!settings.SearchPreferencesLoaded(kind) &&
            !await settings.EnsureSearchPreferencesLoadedAsync(kind, token))
        {
            ctx.SetStatus(MusicCatalogDto.ErrorText("SEARCH_SETTINGS_UNCONFIRMED"));
            return;
        }
        if (stopping) return;
        token.ThrowIfCancellationRequested();
        string[] platforms = settings.SearchDispatchPlatforms(kind);
        var key = SearchStartIdentity(keyword, kind, platforms);
        if (searchKeyword == keyword && searchAccountEpoch == accounts.AccountEpoch &&
            searchStarts.TryGetValue(kind, out var pending) && pending.Epoch == searchEpoch &&
            pending.Key == key)
        {
            await EnsureSearchViewAsync(pending.Work, pending.Epoch, token, kind);
            return;
        }
        var previous = searchStartTail;
        long epoch = ++searchEpoch, navigation = ctx.AdvanceLoadGeneration();
        ctx.LoadCancellation?.Cancel();
        bool retainContent = refresh && searchKeyword == keyword && searchAccountEpoch == accounts.AccountEpoch;
        searchIds.Clear(); searchSnapshots.Clear();
        if (!retainContent)
        {
            searchCardCache.Clear(); searchScroll.Clear();
            ClearSearchSections();
            SearchSongsSource.Clear(); searchSelectedKey = null; ctx.ResetSearchPresentation();
        }
        searchStarts.Clear();
        searchKeyword = keyword; searchView = view; searchKind = kind; searchAccountEpoch = accounts.AccountEpoch;
        SearchKindLabel = kind == "songs" ? "歌曲" : "歌单";
        searchStarting = true; searchRefresh = refresh; SearchStatus = "";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        var flight = new SearchStartFlight(key, epoch);
        searchStarts[kind] = flight;
        SyncSharedState();
        ctx.SetLocation(new("catalog-search", keyword), heading: keyword, clearSelection: !retainContent);
        SearchSessionStarted?.Invoke(keyword, SearchKindLabel);
        searchStartTail = flight.Work = Start();
        ctx.PublishContentSelection(previousSelection, false);
        var work = EnsureSearchViewAsync(flight.Work, epoch, token, kind);
        PublishSearchView(restore: false);
        await work;

        async Task Start()
        {
            await Task.Yield();
            try
            {
                await previous;
                if (stopping || epoch != searchEpoch || navigation != ctx.LoadGeneration) return;
                token.ThrowIfCancellationRequested();
                var snapshot = await application.StartSearchAsync(keyword, kind, platforms, refresh, token);
                string id = snapshot.Id;
                if (stopping || epoch != searchEpoch || navigation != ctx.LoadGeneration)
                {
                    try { await application.CancelSearchAsync(id, lifetime.Token); }
                    catch (OperationCanceledException) { }
                    catch (MusicOperationException) { }
                    return;
                }
                searchIds[kind] = id;
                AcceptSearchSnapshot(snapshot);
                AcceptSearchSnapshot(await application.GetSearchAsync(id, token));
            }
            catch (OperationCanceledException)
            {
                if (epoch == searchEpoch)
                    await CancelSearchIds(searchIds.Values.ToArray());
            }
            catch (Exception error)
            {
                if (!stopping && epoch == searchEpoch)
                {
                    SearchStatus = MusicCatalogDto.ErrorText(error is MusicOperationException e ? e.Code : "INVALID_RESPONSE");
                    await CancelSearchIds(searchIds.Values.ToArray());
                }
            }
            finally
            {
                loadDrains.Remove(done.Task); done.TrySetResult();
                CompleteSearchStart(flight);
            }
        }
    }

    private async Task EnsureSearchViewAsync(Task primary, long epoch, CancellationToken token,
        string? attemptedKind = null)
    {
        if (stopping || epoch != searchEpoch || searchView != SearchOverview)
        {
            await primary.WaitAsync(token);
            return;
        }
        List<Task> starts = [primary];
        foreach (string kind in new[] { "songs", "playlists" })
        {
            if (kind == attemptedKind || searchSnapshots.ContainsKey(kind)) continue;
            starts.Add(StartMissingSearchKindAsync(kind, token));
        }
        await Task.WhenAll(starts).WaitAsync(token);
    }

    private SearchStartKey SearchStartIdentity(string keyword, string kind, string[] platforms) =>
        new(keyword, kind, string.Join("\u001f", platforms), accounts.AccountEpoch);

    private void CompleteSearchStart(SearchStartFlight flight)
    {
        if (searchStarts.TryGetValue(flight.Key.Kind, out var current) &&
            ReferenceEquals(current, flight))
            searchStarts.Remove(flight.Key.Kind);
        searchStarting = searchStarts.Values.Any(value => value.Epoch == searchEpoch);
        SyncSharedState();
        if (disposed) return;
        if (!stopping && flight.Epoch == searchEpoch && IsSearchPage &&
            (searchView == SearchOverview || searchKind == flight.Key.Kind))
        {
            bool missing = !searchSnapshots.ContainsKey(flight.Key.Kind);
            string message = SearchStatus;
            PublishSearchView(restore: false);
            if (missing && message.Length > 0)
            {
                SearchStatus = message;
                Changed(nameof(SearchStatus));
            }
        }
        else SearchBindings();
    }

    private void AcceptSearchSnapshot(MusicSearchSnapshot value)
    {
        if (value.Keyword != searchKeyword || !searchIds.TryGetValue(value.Kind, out var expected) || value.Id != expected) return;
        if (searchSnapshots.TryGetValue(value.Kind, out var before) && value.Revision < before.Revision) return;
        searchSnapshots[value.Kind] = value;
        SyncSharedState();
        if (IsSearchPage && (searchView == SearchOverview || searchKind == value.Kind))
            PublishSearchView(restore: false);
        else SearchBindings();
    }

    private MusicCatalogCard[] SearchCards(string kind)
    {
        if (!searchSnapshots.TryGetValue(kind, out var snapshot))
            return searchCardCache.TryGetValue(kind, out var retained) ? retained.Cards : [];
        if (searchRefresh && snapshot.Loading && snapshot.ItemCount == 0 &&
            searchCardCache.TryGetValue(kind, out var refreshing)) return refreshing.Cards;
        if (kind == "songs")
        {
            var songs = snapshot.Songs;
            if (searchCardCache.TryGetValue(kind, out var cached) && cached.Songs is { } prev)
            {
                if (ReferenceEquals(prev, songs) ||
                    prev.Length == songs.Length && prev.Zip(songs).All(pair => pair.First.ContentEquals(pair.Second)))
                {
                    searchCardCache[kind] = new(songs, null, cached.Cards);
                    return cached.Cards;
                }
                var cards = new MusicCatalogCard[songs.Length];
                for (int i = 0; i < songs.Length; i++)
                {
                    bool unchanged = i < prev.Length && prev[i].ContentEquals(songs[i]);
                    cards[i] = unchanged ? cached.Cards[i] : MusicCatalogDto.SongCard(songs[i]);
                }
                searchCardCache[kind] = new(songs, null, cards);
                return cards;
            }
            var initial = songs.Select(MusicCatalogDto.SongCard).ToArray();
            searchCardCache[kind] = new(songs, null, initial);
            return initial;
        }
        else
        {
            var playlists = snapshot.Playlists;
            if (searchCardCache.TryGetValue(kind, out var cached) && cached.Playlists is { } prev)
            {
                if (ReferenceEquals(prev, playlists) ||
                    prev.Length == playlists.Length && prev.Zip(playlists).All(pair => pair.First.ContentEquals(pair.Second)))
                {
                    searchCardCache[kind] = new(null, playlists, cached.Cards);
                    return cached.Cards;
                }
                var cards = new MusicCatalogCard[playlists.Length];
                for (int i = 0; i < playlists.Length; i++)
                {
                    bool unchanged = i < prev.Length && prev[i].ContentEquals(playlists[i]);
                    cards[i] = unchanged ? cached.Cards[i] : MusicCatalogDto.Collection(playlists[i]);
                }
                searchCardCache[kind] = new(null, playlists, cards);
                return cards;
            }
            var initial = playlists.Select(MusicCatalogDto.Collection).ToArray();
            searchCardCache[kind] = new(null, playlists, initial);
            return initial;
        }
    }

    private bool SearchKindLoading(string kind) =>
        searchStarts.TryGetValue(kind, out var start) && start.Epoch == searchEpoch ||
        searchSnapshots.TryGetValue(kind, out var snapshot) && snapshot.Loading;

    private void PublishSearchView(bool restore)
    {
        var songCards = SearchCards("songs");
        var playlistCards = SearchCards("playlists");
        CatalogPreviewSurfaceRetiring?.Invoke();
        string? selected = restore || searchRefresh ? searchSelectedKey : contentState.SelectedSong?.SelectionKey;
        var nextSongs = SearchSongs(songCards);
        var (prefix, songsChanged) = SearchSongDelta(nextSongs);
        bool reset = !restore && songsChanged && !prefix && IsSearchSongs;
        double scroll = reset ? ctx.CaptureScroll() : searchScroll.GetValueOrDefault(searchView);
        bool restoreScroll = restore || reset;
        var sections = SearchSections(songCards, playlistCards);
        ctx.Publishing = true;
        try
        {
            string kindLabel = searchKind == "songs" ? "歌曲" : "歌单";
            SearchKindLabel = kindLabel;
            SyncSharedState();
            SearchMetadataPublished?.Invoke(searchKeyword, kindLabel);
            bool sectionsChanged = ApplySearchSections(sections);
            int totalCount = searchView == SearchOverview ? songCards.Length + playlistCards.Length :
                searchView == "songs" ? songCards.Length : playlistCards.Length;
            contentState.PublishSearchSummary(searchKeyword, totalCount);
            CatalogTagsClearing?.Invoke();
            var songs = ctx.ActiveSongs;
            if (songsChanged) MusicRuntimeContext.ReconcileSongRows(songs, nextSongs);
            if (restore || searchRefresh || songsChanged || sectionsChanged)
            {
                var restored = songs.FirstOrDefault(song => song.SelectionKey == selected);
                if (!ReferenceEquals(contentState.SelectedSong, restored)) contentState.SetSelectedSong(restored);
            }
            SearchStatus = SearchViewStatus();
            ctx.ClearStatusQuiet();
            ctx.RefreshBindings();
        }
        finally { ctx.Publishing = false; }
        if (!restoreScroll) return;
        long generation = ctx.LoadGeneration, epoch = searchEpoch;
        string view = searchView;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (!stopping && generation == ctx.LoadGeneration && epoch == searchEpoch && IsSearchPage && view == searchView)
                ctx.RestoreScroll(scroll);
        }, DispatcherQueuePriority.Low);
    }

    private (bool Prefix, bool Changed) SearchSongDelta(SongRow[] next)
    {
        var songs = ctx.ActiveSongs;
        bool prefix = next.Length >= songs.Count, changed = next.Length != songs.Count;
        for (int index = 0; index < Math.Min(next.Length, songs.Count); index++)
        {
            if (ReferenceEquals(next[index], songs[index])) continue;
            changed = true;
            if (prefix && next[index].SelectionKey != songs[index].SelectionKey) prefix = false;
        }
        return (prefix, changed);
    }

    private static SongRow[] SearchSongs(MusicCatalogCard[] cards) =>
        cards.Select(card => card.Song).OfType<SongRow>().ToArray();

    private MusicCatalogSection[] SearchSections(MusicCatalogCard[] songCards, MusicCatalogCard[] playlistCards)
    {
        if (searchView == SearchOverview)
            return [Section("songs", "单曲", songCards.Take(6).ToArray()), Section("playlists", "歌单", playlistCards.Take(6).ToArray())];
        return searchView == "songs" ? [Section("songs", "", songCards)] : [Section("playlists", "", playlistCards)];
    }

    private MusicCatalogSection Section(string kind, string title, MusicCatalogCard[] cards)
    {
        var id = "catalog-search:" + kind;
        var currentSections = ActiveSections;
        if (currentSections.FirstOrDefault(section => section.Id == id && section.Title == title) is { } existing &&
            existing.Items.SequenceEqual(cards, ReferenceEqualityComparer.Instance))
            return existing;
        return new(title, cards, Id: id, Layout: kind == "songs" ? "song-list" : "playlist-rail");
    }

    private bool ApplySearchSections(MusicCatalogSection[] sections)
    {
        var currentSections = ActiveSections;
        bool changed = false;
        bool sameLayout = currentSections.Count == sections.Length &&
            sections.Select((section, index) => currentSections[index].Title == section.Title &&
                currentSections[index].Id == section.Id && currentSections[index].Layout == section.Layout).All(same => same);
        if (!sameLayout)
        {
            currentSections.Clear();
            foreach (var section in sections) currentSections.Add(section);
            return true;
        }
        for (int index = 0; index < sections.Length; index++)
        {
            if (ReferenceEquals(currentSections[index], sections[index])) continue;
            currentSections[index] = sections[index];
            changed = true;
        }
        return changed;
    }

    private string SearchViewStatus()
    {
        string[] kinds = searchView == SearchOverview ? ["songs", "playlists"] : [searchKind];
        var snapshots = kinds.Where(searchSnapshots.ContainsKey).Select(kind => searchSnapshots[kind]).ToArray();
        if (snapshots.Length == 0 || snapshots.Any(value => value.Loading)) return "";
        var codes = snapshots.Select(value => value.Code).ToArray();
        if (codes.Contains("FUSION_FAILED", StringComparer.Ordinal)) return "搜索结果排序失败";
        if (codes.Contains("PARTIAL", StringComparer.Ordinal)) return "部分平台搜索失败";
        int count = snapshots.Sum(value => value.ItemCount);
        if (count > 0) return "";
        if (codes.All(code => code == "NO_PLATFORMS")) return "未启用搜索平台";
        if (codes.All(code => code == "CANCELLED")) return "已取消";
        return snapshots.Length == kinds.Length ? "无搜索结果" : "";
    }

    internal Task RestoreSearchAsync(CancellationToken token, bool navigate = true)
    {
        token.ThrowIfCancellationRequested();
        if (navigate) { ctx.AdvanceLoadGeneration(); ctx.LoadCancellation?.Cancel(); }
        var previousSelection = ctx.CaptureContentSelection();
        SyncSharedState();
        ctx.SetLocation(new("catalog-search", searchKeyword));
        ctx.PublishContentSelection(previousSelection, false);
        PublishSearchView(restore: true);
        return Task.CompletedTask;
    }

    public Task ReturnToSearchAsync() => !HasSearchSession || stopping
        ? Task.CompletedTask : RequestNavigationAsync("catalog-search");

    public Task SwitchSearchKindAsync() => OpenSearchTabAsync(SearchKindLabel == "歌单" ? "playlists" : "songs");

    public Task OpenSearchTabAsync(string tab)
    {
        if (ctx.Publishing || !IsSearchPage || stopping) return Task.CompletedTask;
        if (tab is not (SearchOverview or "songs" or "playlists")) return Task.CompletedTask;
        if (searchView != tab) catalogActions.Invalidate();
        RememberSearch();
        var previousSelection = ctx.CaptureContentSelection();
        searchView = tab;
        searchKind = tab == "playlists" ? "playlists" : "songs";
        SearchKindLabel = searchKind == "songs" ? "歌曲" : "歌单";
        SyncSharedState();
        ctx.PublishContentSelection(previousSelection, false);
        PublishSearchView(restore: true);
        return tab == SearchOverview
            ? EnsureSearchViewAsync(Task.CompletedTask, searchEpoch, lifetime.Token)
            : searchSnapshots.ContainsKey(searchKind)
                ? Task.CompletedTask
                : StartMissingSearchKindAsync(searchKind, lifetime.Token);
    }

    private async Task StartMissingSearchKindAsync(string kind, CancellationToken token)
    {
        long epoch = searchEpoch, navigation = ctx.LoadGeneration;
        string keyword = searchKeyword;
        bool refresh = searchRefresh;
        if (stopping || keyword.Length == 0) return;
        if (!settings.SearchPreferencesLoaded(kind) &&
            !await settings.EnsureSearchPreferencesLoadedAsync(kind, token))
        {
            if (!stopping && epoch == searchEpoch && navigation == ctx.LoadGeneration)
                ctx.SetStatus(MusicCatalogDto.ErrorText("SEARCH_SETTINGS_UNCONFIRMED"));
            return;
        }
        if (stopping || epoch != searchEpoch || navigation != ctx.LoadGeneration) return;
        token.ThrowIfCancellationRequested();
        string[] platforms = settings.SearchDispatchPlatforms(kind);
        var key = SearchStartIdentity(keyword, kind, platforms);
        if (searchStarts.TryGetValue(kind, out var pending) && pending.Epoch == epoch &&
            pending.Key == key)
        {
            await pending.Work.WaitAsync(token);
            return;
        }
        if (searchView != SearchOverview && searchKind == kind) ClearSearchVisibleSnapshot();
        var previous = searchStartTail;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        var flight = new SearchStartFlight(key, epoch);
        searchStarts[kind] = flight;
        searchStarting = true;
        searchStartTail = flight.Work = Track();
        SearchBindings();
        await flight.Work;

        async Task Track()
        {
            await Task.Yield();
            string? startedId = null;
            try
            {
                await previous;
                if (stopping || epoch != searchEpoch || navigation != ctx.LoadGeneration) return;
                if (searchSnapshots.ContainsKey(kind))
                {
                    if (IsSearchPage && (searchView == SearchOverview || searchKind == kind))
                        PublishSearchView(restore: false);
                    return;
                }
                token.ThrowIfCancellationRequested();
                var snapshot = await application.StartSearchAsync(keyword, kind, platforms, refresh, token);
                string id = snapshot.Id;
                startedId = id;
                if (stopping || epoch != searchEpoch || navigation != ctx.LoadGeneration)
                {
                    try { await application.CancelSearchAsync(id, lifetime.Token); }
                    catch (OperationCanceledException) { }
                    catch (MusicOperationException) { }
                    return;
                }
                searchIds[kind] = id;
                AcceptSearchSnapshot(snapshot);
                AcceptSearchSnapshot(await application.GetSearchAsync(id, token));
            }
            catch (OperationCanceledException)
            {
                if (startedId is not null && epoch == searchEpoch)
                    await CancelSearchIds([startedId]);
            }
            catch (Exception error)
            {
                if (!stopping && epoch == searchEpoch)
                {
                    SearchStatus = MusicCatalogDto.ErrorText(error is MusicOperationException e ? e.Code : "INVALID_RESPONSE");
                    if (startedId is not null) await CancelSearchIds([startedId]);
                }
            }
            finally
            {
                loadDrains.Remove(done.Task); done.TrySetResult();
                CompleteSearchStart(flight);
            }
        }
    }

    public Task MoreSearchAsync() => !CanSearchMore ? Task.CompletedTask :
        !searchIds.TryGetValue(searchKind, out string? id) ? Task.CompletedTask :
        RunAsync(async () => AcceptSearchSnapshot(await application.NextSearchAsync(id, lifetime.Token)));

    public Task CancelSearchAsync()
    {
        if (stopping) return Task.CompletedTask;
        long epoch = ++searchEpoch; searchStarting = false;
        string[] ids = searchIds.Values.ToArray();
        var previous = searchStartTail;
        return RunAsync(async () =>
        {
            await previous;
            await CancelSearchIds(ids);
            if (!stopping && epoch == searchEpoch)
            {
                SearchStatus = "已取消";
                SearchBindings();
            }
        });
    }

    private async Task CancelSearchIds(string[] ids)
    {
        foreach (string id in ids)
        {
            try
            {
                var receipt = await application.CancelSearchAsync(id, lifetime.Token);
                AcceptSearchSnapshot(receipt);
            }
            catch (OperationCanceledException) { }
            catch (MusicOperationException error)
            {
                MeowFilm.Core.Debug.DebugLog.Warn(MeowFilm.Core.Debug.DebugCategories.Search,
                    "search", "search_cancel_failed", new { id, code = error.Code });
            }
        }
    }

    internal async Task RetireSearchAsync()
    {
        ++searchEpoch; searchStarting = false;
        CatalogPreviewSurfaceRetiring?.Invoke();
        string[] ids = searchIds.Values.ToArray();
        searchIds.Clear(); searchSnapshots.Clear(); searchCardCache.Clear(); searchScroll.Clear(); searchKeyword = "";
        ClearSearchSections();
        SearchSongsSource.Clear(); searchSelectedKey = null;
        ctx.ResetSearchPresentation();
        var previousSelection = ctx.CaptureContentSelection();
        searchView = SearchOverview; searchKind = "songs";
        SyncSharedState();
        if (!ctx.PublishContentSelection(previousSelection, true)) SearchBindings();
        await searchStartTail;
        await CancelSearchIds(ids);
    }

    internal void RetireSearchTracked()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        _ = Retire();
        async Task Retire()
        {
            try { await RetireSearchAsync(); }
            catch { }
            finally { loadDrains.Remove(done.Task); done.TrySetResult(); }
        }
    }

    internal void ClearSearchVisibleSnapshot()
    {
        CatalogPreviewSurfaceRetiring?.Invoke();
        ActiveSections.Clear();
        CatalogTagsClearing?.Invoke();
        if (IsSearchSongs || !HasSearchSession) SearchSongsSource.Clear();
        contentState.SetSelectedSong(null);
        contentState.ClearTotal();
        SearchStatus = "";
        ctx.RefreshBindings();
    }

    internal async Task RetireAndClearForPolicyChangeAsync()
    {
        await RetireSearchAsync();
        if (IsSearchPage) ClearSearchVisibleSnapshot();
    }
}
