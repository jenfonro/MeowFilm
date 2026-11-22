using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace MeowFilm.Capabilities;

/// <summary>Standalone owner for online comprehensive music search sessions, tabs, and incremental results.</summary>
public sealed class MusicSearchController : MusicDomainController
{
    internal const string SearchOverview = "overview";
    private readonly Dictionary<string, JsonElement> searchSnapshots = [];
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
    private sealed record SearchCardsCache(JsonElement Items, MusicCatalogCard[] Cards);

    internal MusicSearchController(MusicCapability host) : base(host)
    {
        application.SearchChanged += SearchChanged;
    }

    public ObservableCollection<SongRow> SearchSongsSource { get; } = [];
    public string[] CatalogSearchKinds => host.Catalog.CatalogSearchKinds;
    public string CatalogSearchKind
    {
        get => host.Catalog.CatalogSearchKind;
        set { host.Catalog.CatalogSearchKind = value; Changed(); }
    }
    public SongRow? SearchSelectedSong
    {
        get => IsSearchPage ? host.Library.SelectedSong : SearchSongsSource.FirstOrDefault(song => song.SelectionKey == searchSelectedKey);
        set { if (IsSearchPage) host.Library.SelectedSong = value; }
    }
    internal string? SearchSelectedKey { get => searchSelectedKey; set => searchSelectedKey = value; }
    internal string SearchKeyword => searchKeyword;
    internal string SearchView => searchView;
    internal long SearchAccountEpoch => searchAccountEpoch;
    internal bool SearchStarting => searchStarting;

    public bool IsSearchPage => host.CurrentRoute == "catalog-search";
    public string SearchTab => searchView;
    public bool IsSearchOverview => IsSearchPage && searchView == SearchOverview;
    public bool IsSearchSongs => IsSearchPage && searchView == "songs";
    public bool IsSearchPlaylists => IsSearchPage && searchView == "playlists";
    public bool HasSearchSession => searchKeyword.Length > 0 &&
        (searchSnapshots.Count > 0 || searchStarts.Count > 0);
    public bool CanSearchMore => host.Catalog.CanCatalogBrowse && !IsSearchOverview && !searchStarting &&
        searchSnapshots.TryGetValue(searchKind, out var snapshot) &&
        !snapshot.GetProperty("loading").GetBoolean() && snapshot.GetProperty("more").GetBoolean();
    public bool CanLoadMoreSearchSongs => IsSearchSongs && CanSearchMore;
    public bool CanLoadMoreSearchPlaylists => IsSearchPlaylists && CanSearchMore;
    public bool SearchSongsLoading => SearchKindLoading("songs");
    public bool SearchPlaylistsLoading => SearchKindLoading("playlists");
    public bool ShowSearchLoading => IsSearchSongs && SearchSongsLoading && SearchSongsSource.Count == 0;
    public string SearchStatus { get; private set; } = "";

    internal void Unsubscribe() => application.SearchChanged -= SearchChanged;
    internal void CancelForShutdown() => searchEpoch++;
    internal IEnumerable<MusicCatalogCard> CachedSearchCards() => searchCardCache.Values.SelectMany(value => value.Cards);

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        Changed(nameof(SearchSelectedSong));
        SearchBindings();
    }

    internal void NotifyRouteChanged()
    {
        Changed(nameof(IsSearchPage));
        Changed(nameof(SearchTab));
        Changed(nameof(IsSearchOverview));
        Changed(nameof(IsSearchSongs));
        Changed(nameof(IsSearchPlaylists));
        Changed(nameof(ShowSearchLoading));
        SearchBindings();
    }

    internal void NotifySelectionChanged() => Changed(nameof(SearchSelectedSong));

    private void SearchChanged(JsonElement snapshot)
    {
        if (Presentation.Dispatcher.IsClosing) return;
        _ = Presentation.Dispatcher.EnqueueAsync(() =>
        {
            if (disposed || stopping) return;
            try { AcceptSearchSnapshot(snapshot); }
            catch { SearchStatus = "搜索结果格式无效"; SearchBindings(); }
        }, DispatcherQueuePriority.Low);
    }

    internal void SearchBindings()
    {
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
            searchScroll[searchView] = Presentation.CaptureScroll();
            if (host.Library.SelectedSong is { } sel) searchSelectedKey = sel.SelectionKey;
        }
    }

    internal async Task BeginSearchAsync(string keyword, CancellationToken token, bool refresh = false, bool overview = false)
    {
        if (stopping) return;
        RememberSearch();
        var previousSelection = host.CaptureContentSelection();
        string view = searchKeyword != keyword || overview ? SearchOverview : searchView;
        string kind = view == "playlists" ? "playlists" : "songs";
        if (!host.Settings.SearchPreferencesLoaded(kind) &&
            !await host.Settings.EnsureSearchPreferencesLoadedAsync(kind, token))
        {
            await host.Catalog.CatalogBusinessError("SEARCH_SETTINGS_UNCONFIRMED");
            return;
        }
        if (stopping) return;
        token.ThrowIfCancellationRequested();
        string[] platforms = host.Settings.SearchDispatchPlatforms(kind);
        var key = SearchStartIdentity(keyword, kind, platforms);
        if (searchKeyword == keyword && searchAccountEpoch == host.Accounts.AccountEpoch &&
            searchStarts.TryGetValue(kind, out var pending) && pending.Epoch == searchEpoch &&
            pending.Key == key)
        {
            await EnsureSearchViewAsync(pending.Work, pending.Epoch, token, kind);
            return;
        }
        var previous = searchStartTail;
        long epoch = ++searchEpoch, navigation = host.AdvanceLoadGeneration();
        host.LoadCancellation?.Cancel();
        bool retainContent = refresh && searchKeyword == keyword && searchAccountEpoch == host.Accounts.AccountEpoch;
        searchIds.Clear(); searchSnapshots.Clear();
        if (!retainContent)
        {
            searchCardCache.Clear(); searchScroll.Clear();
            host.Catalog.ClearSearchSections();
            SearchSongsSource.Clear(); searchSelectedKey = null; Presentation.ResetSearchPresentation();
        }
        searchStarts.Clear();
        searchKeyword = keyword; searchView = view; searchKind = kind; searchAccountEpoch = host.Accounts.AccountEpoch;
        host.Catalog.CatalogSearchKind = kind == "songs" ? "歌曲" : "歌单";
        Changed(nameof(CatalogSearchKind));
        searchStarting = true; searchRefresh = refresh; SearchStatus = "";
        host.SetLocation(new("catalog-search", keyword), heading: keyword, clearSelection: !retainContent);
        host.Catalog.BeginSearchCatalogState(keyword, host.Catalog.CatalogSearchKind);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        var flight = new SearchStartFlight(key, epoch);
        searchStarts[kind] = flight;
        searchStartTail = flight.Work = Start();
        host.PublishContentSelection(previousSelection);
        var work = EnsureSearchViewAsync(flight.Work, epoch, token, kind);
        PublishSearchView(restore: false);
        await work;

        async Task Start()
        {
            await Task.Yield();
            try
            {
                await previous;
                if (stopping || epoch != searchEpoch || navigation != host.LoadGeneration) return;
                // Start the first visible kind; the comprehensive projection admits
                // its other visible kind after this bounded start has settled.
                token.ThrowIfCancellationRequested();
                var snapshot = await Call("music.search.start", new { keyword, kind, platforms, refresh }, token);
                string id = MusicDto.Text(snapshot, "id");
                if (stopping || epoch != searchEpoch || navigation != host.LoadGeneration)
                {
                    await application.ExecuteAsync("music.search.cancel", new { id }, lifetime.Token);
                    return;
                }
                searchIds[kind] = id;
                AcceptSearchSnapshot(snapshot);
                // A very fast provider can finish before its id is admitted
                // to the UI. Read one current snapshot; never start polling.
                AcceptSearchSnapshot(await Call("music.search.get", new { id }, token));
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
        new(keyword, kind, string.Join("\u001f", platforms), host.Accounts.AccountEpoch);

    private void CompleteSearchStart(SearchStartFlight flight)
    {
        if (searchStarts.TryGetValue(flight.Key.Kind, out var current) &&
            ReferenceEquals(current, flight))
            searchStarts.Remove(flight.Key.Kind);
        searchStarting = searchStarts.Values.Any(value => value.Epoch == searchEpoch);
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

    private void AcceptSearchSnapshot(JsonElement value)
    {
        string id = MusicDto.Text(value, "id"), kind = MusicDto.Text(value, "kind");
        if (MusicDto.Text(value, "keyword") != searchKeyword || !searchIds.TryGetValue(kind, out var expected) || id != expected) return;
        long revision = MusicDto.Integer(value, "revision");
        if (searchSnapshots.TryGetValue(kind, out var before) && revision < MusicDto.Integer(before, "revision")) return;
        MusicDto.Require(value.GetProperty("items").ValueKind == JsonValueKind.Array &&
            value.GetProperty("platforms").ValueKind == JsonValueKind.Array &&
            value.GetProperty("platforms").GetArrayLength() <= 5);
        searchSnapshots[kind] = value.Clone();
        if (IsSearchPage && (searchView == SearchOverview || searchKind == kind))
            PublishSearchView(restore: false);
        else SearchBindings();
    }

    private MusicCatalogCard[] SearchCards(string kind)
    {
        if (!searchSnapshots.TryGetValue(kind, out var snapshot))
            return searchCardCache.TryGetValue(kind, out var retained) ? retained.Cards : [];
        var items = snapshot.GetProperty("items");
        if (searchRefresh && snapshot.GetProperty("loading").GetBoolean() && items.GetArrayLength() == 0 &&
            searchCardCache.TryGetValue(kind, out var refreshing)) return refreshing.Cards;
        if (searchCardCache.TryGetValue(kind, out var cached) && JsonElement.DeepEquals(cached.Items, items))
        {
            searchCardCache[kind] = new(items, cached.Cards);
            return cached.Cards;
        }
        if (!searchCardCache.TryGetValue(kind, out cached))
        {
            searchCardCache[kind] = new(items, MusicCatalogDto.SearchResults(items, kind));
            return searchCardCache[kind].Cards;
        }
        var cards = new MusicCatalogCard[items.GetArrayLength()];
        var previous = cached.Items.EnumerateArray();
        int index = 0;
        foreach (var item in items.EnumerateArray())
        {
            bool unchanged = previous.MoveNext() && JsonElement.DeepEquals(previous.Current, item);
            cards[index] = unchanged ? cached.Cards[index] : MusicCatalogDto.SearchResult(item, kind);
            index++;
        }
        searchCardCache[kind] = new(items, cards);
        return cards;
    }

    private bool SearchKindLoading(string kind) =>
        searchStarts.TryGetValue(kind, out var start) && start.Epoch == searchEpoch ||
        searchSnapshots.TryGetValue(kind, out var snapshot) &&
        snapshot.GetProperty("loading").GetBoolean();

    private void PublishSearchView(bool restore)
    {
        var songCards = SearchCards("songs");
        var playlistCards = SearchCards("playlists");
        host.Catalog.RetireCatalogPreviewSurface();
        string? selected = restore || searchRefresh ? searchSelectedKey : host.Library.SelectedSong?.SelectionKey;
        var nextSongs = SearchSongs(songCards);
        var (prefix, songsChanged) = SearchSongDelta(nextSongs);
        bool reset = !restore && songsChanged && !prefix && IsSearchSongs;
        double scroll = reset ? Presentation.CaptureScroll() : searchScroll.GetValueOrDefault(searchView);
        bool restoreScroll = restore || reset;
        var sections = SearchSections(songCards, playlistCards);
        host.Publishing = true;
        try
        {
            string kindLabel = searchKind == "songs" ? "歌曲" : "歌单";
            host.Catalog.PublishSearchCatalogMetadata(searchKeyword, kindLabel);
            Changed(nameof(CatalogSearchKind));
            bool sectionsChanged = ApplySearchSections(sections);
            int totalCount = searchView == SearchOverview ? songCards.Length + playlistCards.Length :
                searchView == "songs" ? songCards.Length : playlistCards.Length;
            host.Library.PublishSearchSummary(searchKeyword, totalCount);
            if (host.Catalog.CatalogTags.Count > 0) host.Catalog.CatalogTags.Clear();
            var songs = host.ActiveSongs;
            if (songsChanged) MusicCapability.ReconcileSongRows(songs, nextSongs);
            if (restore || searchRefresh || songsChanged || sectionsChanged)
            {
                var restored = songs.FirstOrDefault(song => song.SelectionKey == selected);
                if (!ReferenceEquals(host.Library.SelectedSong, restored)) host.Library.SelectedSong = restored;
            }
            SearchStatus = SearchViewStatus();
            host.ClearStatusQuiet();
            host.RefreshBindings();
        }
        finally { host.Publishing = false; }
        if (!restoreScroll) return;
        long generation = host.LoadGeneration, epoch = searchEpoch;
        string view = searchView;
        _ = Presentation.Dispatcher.EnqueueAsync(() =>
        {
            if (!stopping && generation == host.LoadGeneration && epoch == searchEpoch && IsSearchPage && view == searchView)
                Presentation.RestoreScroll(scroll);
        }, DispatcherQueuePriority.Low);
    }

    private (bool Prefix, bool Changed) SearchSongDelta(SongRow[] next)
    {
        var songs = host.ActiveSongs;
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
        {
            return
            [
                Section("songs", "单曲", songCards.Take(6).ToArray()),
                Section("playlists", "歌单", playlistCards.Take(6).ToArray())
            ];
        }
        if (searchView == "songs")
        {
            return [Section("songs", "", songCards)];
        }
        return [Section("playlists", "", playlistCards)];
    }

    private MusicCatalogSection Section(string kind, string title, MusicCatalogCard[] cards)
    {
        var id = "catalog-search:" + kind;
        if (host.Catalog.CatalogSections.FirstOrDefault(section => section.Id == id && section.Title == title) is { } existing &&
            existing.Items.SequenceEqual(cards, ReferenceEqualityComparer.Instance))
            return existing;
        return new(title, cards, Id: id, Layout: kind == "songs" ? "song-list" : "playlist-rail");
    }

    private bool ApplySearchSections(MusicCatalogSection[] sections)
    {
        var catalogSections = host.Catalog.CatalogSections;
        bool changed = false;
        bool sameLayout = catalogSections.Count == sections.Length &&
            sections.Select((section, index) => catalogSections[index].Title == section.Title &&
                catalogSections[index].Id == section.Id && catalogSections[index].Layout == section.Layout).All(same => same);
        if (!sameLayout)
        {
            catalogSections.Clear();
            foreach (var section in sections) catalogSections.Add(section);
            return true;
        }
        for (int index = 0; index < sections.Length; index++)
        {
            if (ReferenceEquals(catalogSections[index], sections[index])) continue;
            catalogSections[index] = sections[index];
            changed = true;
        }
        return changed;
    }

    private string SearchViewStatus()
    {
        string[] kinds = searchView == SearchOverview ? ["songs", "playlists"] : [searchKind];
        var snapshots = kinds.Where(searchSnapshots.ContainsKey).Select(kind => searchSnapshots[kind]).ToArray();
        if (snapshots.Length == 0 || snapshots.Any(value => value.GetProperty("loading").GetBoolean())) return "";
        var codes = snapshots.Select(value => MusicCatalogDto.Text(value, "code")).ToArray();
        if (codes.Contains("FUSION_FAILED", StringComparer.Ordinal)) return "搜索结果排序失败";
        if (codes.Contains("PARTIAL", StringComparer.Ordinal)) return "部分平台搜索失败";
        int count = snapshots.Sum(value => value.GetProperty("items").GetArrayLength());
        if (count > 0) return "";
        if (codes.All(code => code == "NO_PLATFORMS")) return "未启用搜索平台";
        if (codes.All(code => code == "CANCELLED")) return "已取消";
        return snapshots.Length == kinds.Length ? "无搜索结果" : "";
    }

    internal Task RestoreSearchAsync(CancellationToken token, bool navigate = true)
    {
        token.ThrowIfCancellationRequested();
        if (navigate) { host.AdvanceLoadGeneration(); host.LoadCancellation?.Cancel(); }
        var previousSelection = host.CaptureContentSelection();
        host.SetLocation(new("catalog-search", searchKeyword));
        host.PublishContentSelection(previousSelection);
        PublishSearchView(restore: true);
        return Task.CompletedTask;
    }

    public Task ReturnToSearchAsync() => !HasSearchSession || stopping
        ? Task.CompletedTask : RequestNavigationAsync("catalog-search");

    public Task SwitchSearchKindAsync()
        => OpenSearchTabAsync(host.Catalog.CatalogSearchKind == "歌单" ? "playlists" : "songs");

    public Task OpenSearchTabAsync(string tab)
    {
        if (host.Publishing || !IsSearchPage || stopping) return Task.CompletedTask;
        if (tab is not (SearchOverview or "songs" or "playlists")) return Task.CompletedTask;
        if (searchView != tab) host.Catalog.InvalidateCatalogAction();
        RememberSearch();
        var previousSelection = host.CaptureContentSelection();
        searchView = tab;
        searchKind = tab == "playlists" ? "playlists" : "songs";
        host.Catalog.CatalogSearchKind = searchKind == "songs" ? "歌曲" : "歌单";
        Changed(nameof(CatalogSearchKind));
        host.PublishContentSelection(previousSelection);
        PublishSearchView(restore: true);
        return tab == SearchOverview
            ? EnsureSearchViewAsync(Task.CompletedTask, searchEpoch, lifetime.Token)
            : searchSnapshots.ContainsKey(searchKind)
                ? Task.CompletedTask
                : StartMissingSearchKindAsync(searchKind, lifetime.Token);
    }

    private async Task StartMissingSearchKindAsync(string kind, CancellationToken token)
    {
        if (!host.Settings.SearchPreferencesLoaded(kind) &&
            !await host.Settings.EnsureSearchPreferencesLoadedAsync(kind, token))
        {
            await host.Catalog.CatalogBusinessError("SEARCH_SETTINGS_UNCONFIRMED");
            return;
        }
        if (stopping) return;
        token.ThrowIfCancellationRequested();
        long epoch = searchEpoch, navigation = host.LoadGeneration;
        string keyword = searchKeyword;
        string[] platforms = host.Settings.SearchDispatchPlatforms(kind);
        bool refresh = searchRefresh;
        var key = SearchStartIdentity(keyword, kind, platforms);
        if (searchStarts.TryGetValue(kind, out var pending) && pending.Epoch == epoch &&
            pending.Key == key)
        {
            await pending.Work.WaitAsync(token);
            return;
        }
        if (searchView != SearchOverview && searchKind == kind) ClearSearchVisibleSnapshot();
        var previous = searchStartTail;
        var flight = new SearchStartFlight(key, epoch);
        searchStarts[kind] = flight;
        searchStarting = true;
        searchStartTail = flight.Work = Track();
        SearchBindings();
        await flight.Work;

        async Task Track()
        {
            await Task.Yield();
            try
            {
                await RunAsync(async () =>
                {
                    await previous;
                    if (epoch != searchEpoch || navigation != host.LoadGeneration) return;
                    if (searchSnapshots.ContainsKey(kind))
                    {
                        if (IsSearchPage && (searchView == SearchOverview || searchKind == kind))
                            PublishSearchView(restore: false);
                        return;
                    }
                    var snapshot = await Call("music.search.start", new { keyword, kind, platforms, refresh }, token);
                    string id = MusicDto.Text(snapshot, "id");
                    if (epoch != searchEpoch || navigation != host.LoadGeneration)
                    { await application.ExecuteAsync("music.search.cancel", new { id }, lifetime.Token); return; }
                    searchIds[kind] = id;
                    AcceptSearchSnapshot(snapshot);
                    AcceptSearchSnapshot(await Call("music.search.get", new { id }, token));
                });
            }
            finally { CompleteSearchStart(flight); }
        }
    }

    public Task MoreSearchAsync() => !CanSearchMore ? Task.CompletedTask :
        !searchIds.TryGetValue(searchKind, out string? id) ? Task.CompletedTask :
        RunAsync(async () => AcceptSearchSnapshot(await Call("music.search.next", new { id }, lifetime.Token)));

    public Task CancelSearchAsync()
    {
        if (stopping) return Task.CompletedTask;
        ++searchEpoch; searchStarting = false;
        string[] ids = searchIds.Values.ToArray();
        var previous = searchStartTail;
        return RunAsync(async () =>
        {
            await previous;
            await CancelSearchIds(ids);
            SearchStatus = "已取消"; SearchBindings();
        });
    }

    private async Task CancelSearchIds(string[] ids)
    {
        foreach (string id in ids)
        {
            var receipt = await application.ExecuteAsync("music.search.cancel", new { id }, lifetime.Token);
            if (receipt.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                AcceptSearchSnapshot(receipt);
        }
    }

    internal async Task RetireSearchAsync()
    {
        ++searchEpoch; searchStarting = false;
        host.Catalog.RetireCatalogPreviewSurface();
        string[] ids = searchIds.Values.ToArray();
        searchIds.Clear(); searchSnapshots.Clear(); searchCardCache.Clear(); searchScroll.Clear(); searchKeyword = "";
        host.Catalog.ClearSearchSections();
        SearchSongsSource.Clear(); searchSelectedKey = null;
        Presentation.ResetSearchPresentation();
        var previousSelection = host.CaptureContentSelection();
        searchView = SearchOverview; searchKind = "songs";
        host.PublishContentSelection(previousSelection); SearchBindings();
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
            catch { /* The application retains physical drain ownership. No stale UI result survives. */ }
            finally { loadDrains.Remove(done.Task); done.TrySetResult(); }
        }
    }

    internal void ClearSearchVisibleSnapshot()
    {
        host.Catalog.RetireCatalogPreviewSurface();
        host.Catalog.CatalogSections.Clear();
        host.Catalog.CatalogTags.Clear();
        if (IsSearchSongs || !HasSearchSession) SearchSongsSource.Clear();
        host.Library.SelectedSong = null;
        host.Library.ClearTotal();
        SearchStatus = "";
        host.RefreshBindings();
    }
}
