using System.Text.Json;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private const string SearchOverview = "overview";
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
    private sealed record SearchStartKey(string Keyword, string Kind, string Platforms,
        long AccountEpoch);
    private sealed class SearchStartFlight(SearchStartKey key, long epoch)
    {
        internal SearchStartKey Key { get; } = key;
        internal long Epoch { get; } = epoch;
        internal Task Work { get; set; } = Task.CompletedTask;
    }
    private sealed record SearchCardsCache(JsonElement Items, MusicCatalogCard[] Cards);
    public bool IsSearchPage => location.Route == "catalog-search";
    public string SearchTab => searchView;
    public bool IsSearchOverview => IsSearchPage && searchView == SearchOverview;
    public bool IsSearchSongs => IsSearchPage && searchView == "songs";
    public bool IsSearchPlaylists => IsSearchPage && searchView == "playlists";
    public bool IsCatalogBrowsePage => IsCatalogPage && !IsSearchPage && !IsDiscovery;
    public bool HasSearchSession => searchKeyword.Length > 0 &&
        (searchSnapshots.Count > 0 || searchStarts.Count > 0);
    public bool CanSearchMore => CanCatalogBrowse && !IsSearchOverview && !searchStarting &&
        searchSnapshots.TryGetValue(searchKind, out var snapshot) &&
        !snapshot.GetProperty("loading").GetBoolean() && snapshot.GetProperty("more").GetBoolean();
    public bool CanLoadMoreSearchSongs => IsSearchSongs && CanSearchMore;
    public bool CanLoadMoreSearchPlaylists => IsSearchPlaylists && CanSearchMore;
    public bool SearchSongsLoading => SearchKindLoading("songs");
    public bool SearchPlaylistsLoading => SearchKindLoading("playlists");
    public string SearchStatus { get; private set; } = "";

    private void InitializeSearch() => application.SearchChanged += SearchChanged;
    private void UnsubscribeSearch() => application.SearchChanged -= SearchChanged;
    private void SearchChanged(JsonElement snapshot)
    {
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return;
        _ = Presentation.Dispatcher.InvokeAsync(() =>
        {
            if (disposed || stopping) return;
            try { AcceptSearchSnapshot(snapshot); }
            catch { SearchStatus = "搜索结果格式无效"; SearchBindings(); }
        }, DispatcherPriority.Background);
    }
    private void SearchBindings()
    {
        Changed(nameof(ContentPage));
        foreach (string name in new[] { nameof(IsSearchPage), nameof(IsCatalogBrowsePage), nameof(HasSearchSession),
            nameof(CatalogSections), nameof(ShowSearchSkeleton), nameof(Songs), nameof(SearchTab), nameof(IsSearchOverview), nameof(IsSearchSongs), nameof(IsSearchPlaylists),
            nameof(CanSearchMore), nameof(CanLoadMoreSearchSongs), nameof(CanLoadMoreSearchPlaylists),
            nameof(SearchSongsLoading), nameof(SearchPlaylistsLoading), nameof(SearchStatus) })
            Changed(name);
    }
    private void RememberSearch()
    {
        if (IsSearchPage)
        {
            searchScroll[searchView] = Presentation.CaptureScroll();
            if (selectedSong is not null) searchSelectedKey = selectedSong.SelectionKey;
        }
    }

    private Task BeginSearchAsync(string keyword, CancellationToken token, bool refresh = false)
    {
        if (stopping) return Task.CompletedTask;
        if (!searchPreferencesLoaded) return CatalogBusinessError("SEARCH_SETTINGS_UNCONFIRMED");
        RememberSearch();
        if (searchKeyword != keyword)
        {
            searchView = SearchOverview;
            searchKind = "songs";
            CatalogSearchKind = "歌曲";
        }
        string kind = searchView == "playlists" ? "playlists" : "songs";
        searchKind = kind;
        string[] platforms = SearchDispatchPlatforms(kind);
        var key = SearchStartIdentity(keyword, kind, platforms);
        if (searchKeyword == keyword && searchAccountEpoch == accountEpoch &&
            searchStarts.TryGetValue(kind, out var pending) && pending.Epoch == searchEpoch &&
            pending.Key == key)
        {
            searchKind = kind;
            return EnsureSearchViewAsync(pending.Work, pending.Epoch, token, kind);
        }
        var previous = searchStartTail;
        long epoch = ++searchEpoch, navigation = AdvanceLoadGeneration();
        loadCancellation?.Cancel();
        bool retainContent = refresh && searchKeyword == keyword && searchAccountEpoch == accountEpoch;
        searchIds.Clear(); searchSnapshots.Clear();
        if (!retainContent)
        {
            searchCardCache.Clear(); searchScroll.Clear();
            foreach (var sections in searchSections.Values) sections.Clear();
            searchSongs.Clear(); searchSelectedKey = null; Presentation.ResetSearchPresentation();
        }
        searchStarts.Clear();
        searchKeyword = keyword; searchKind = kind; searchAccountEpoch = accountEpoch;
        searchStarting = true; searchRefresh = refresh; SearchStatus = "";
        location = new("catalog-search", keyword); heading = keyword;
        catalogQuery = keyword; catalogLoadedKind = CatalogSearchKind;
        catalogLoadedPlatform = ""; catalogLoadedTarget = null; catalogExpiresAt = null;
        CatalogTags.Clear();
        collection = null;
        if (!retainContent) selectedSong = null;
        total = 0; status = "";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        var flight = new SearchStartFlight(key, epoch);
        searchStarts[kind] = flight;
        searchStartTail = flight.Work = Start();
        RefreshBindings();
        var work = EnsureSearchViewAsync(flight.Work, epoch, token, kind);
        PublishSearchView(restore: false);
        return work;

        async Task Start()
        {
            await Task.Yield();
            try
            {
                await previous;
                if (stopping || epoch != searchEpoch || navigation != loadGeneration) return;
                // Start the first visible kind; the comprehensive projection admits
                // its other visible kind after this bounded start has settled.
                token.ThrowIfCancellationRequested();
                var snapshot = await Call("music.search.start", new { keyword, kind, platforms, refresh }, token);
                string id = MusicDto.Text(snapshot, "id");
                if (stopping || epoch != searchEpoch || navigation != loadGeneration)
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

    private SearchStartKey SearchStartIdentity(string keyword, string kind,
        string[] platforms) =>
        new(keyword, kind, string.Join("\u001f", platforms), accountEpoch);

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
                SearchBindings();
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
        SearchBindings();
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
        RetireCatalogPreviewSurface();
        string? selected = restore || searchRefresh ? searchSelectedKey : selectedSong?.SelectionKey;
        var nextSongs = SearchSongs(songCards);
        var (prefix, songsChanged) = SearchSongDelta(nextSongs);
        bool reset = !restore && songsChanged && !prefix && IsSearchSongs;
        double scroll = reset ? Presentation.CaptureScroll() : searchScroll.GetValueOrDefault(searchView);
        bool restoreScroll = restore || reset;
        var sections = SearchSections(songCards, playlistCards);
        publishing = true;
        try
        {
            catalogQuery = searchKeyword; catalogLoadedPlatform = ""; catalogLoadedTarget = null;
            catalogAccountStamp = ""; catalogExpiresAt = null; catalogMore = false; catalogContinuation = null;
            catalogLoadedKind = CatalogSearchKind = searchKind == "songs" ? "歌曲" : "歌单";
            heading = searchKeyword; collection = null;
            bool sectionsChanged = ApplySearchSections(sections);
            total = searchView == SearchOverview ? songCards.Length + playlistCards.Length :
                searchView == "songs" ? songCards.Length : playlistCards.Length;
            if (CatalogTags.Count > 0) CatalogTags.Clear();
            if (songsChanged) ApplySearchSongs(nextSongs, prefix);
            if (restore || searchRefresh || songsChanged || sectionsChanged)
            {
                var restored = Songs.FirstOrDefault(song => song.SelectionKey == selected);
                if (!ReferenceEquals(selectedSong, restored)) SelectedSong = restored;
            }
            SearchStatus = SearchViewStatus();
            status = ""; RefreshBindings();
            if (sectionsChanged) Changed(nameof(CatalogSections));
        }
        finally { publishing = false; }
        if (!restoreScroll) return;
        long generation = loadGeneration, epoch = searchEpoch;
        string view = searchView;
        _ = Presentation.Dispatcher.InvokeAsync(() =>
        {
            if (!stopping && generation == loadGeneration && epoch == searchEpoch && IsSearchPage && view == searchView)
                Presentation.RestoreScroll(scroll);
        }, DispatcherPriority.Loaded);
    }

    private (bool Prefix, bool Changed) SearchSongDelta(SongRow[] next)
    {
        bool prefix = next.Length >= Songs.Count, changed = next.Length != Songs.Count;
        for (int index = 0; index < Math.Min(next.Length, Songs.Count); index++)
        {
            if (ReferenceEquals(next[index], Songs[index])) continue;
            changed = true;
            if (prefix && next[index].SelectionKey != Songs[index].SelectionKey) prefix = false;
        }
        return (prefix, changed);
    }

    private static SongRow[] SearchSongs(MusicCatalogCard[] cards) =>
        cards.Select(card => card.Song).OfType<SongRow>().ToArray();

    private MusicCatalogSection[] SearchSections(MusicCatalogCard[] songCards,
        MusicCatalogCard[] playlistCards)
    {
        if (searchView == SearchOverview)
        {
            return
            [
                Section("单曲", songCards.Take(6).ToArray()),
                Section("歌单", playlistCards.Take(6).ToArray())
            ];
        }
        if (searchView == "songs")
        {
            return [Section("", songCards)];
        }
        return [Section("", playlistCards)];
    }

    private MusicCatalogSection Section(string title, MusicCatalogCard[] cards)
    {
        if (CatalogSections.FirstOrDefault(section => section.Title == title) is { } existing &&
            existing.Items.SequenceEqual(cards, ReferenceEqualityComparer.Instance))
            return existing;
        return new(title, cards);
    }

    private bool ApplySearchSections(MusicCatalogSection[] sections)
    {
        bool changed = false;
        bool sameLayout = CatalogSections.Count == sections.Length &&
            sections.Select((section, index) => CatalogSections[index].Title == section.Title &&
                CatalogSections[index].Id == section.Id && CatalogSections[index].Type == section.Type).All(same => same);
        if (!sameLayout)
        {
            CatalogSections.Clear();
            foreach (var section in sections) CatalogSections.Add(section);
            return true;
        }
        for (int index = 0; index < sections.Length; index++)
        {
            if (ReferenceEquals(CatalogSections[index], sections[index])) continue;
            CatalogSections[index] = sections[index];
            changed = true;
        }
        return changed;
    }

    private void ApplySearchSongs(SongRow[] next, bool prefix)
    {
        if (prefix)
        {
            for (int index = 0; index < Songs.Count; index++)
                if (!ReferenceEquals(Songs[index], next[index])) Songs[index] = next[index];
            for (int index = Songs.Count; index < next.Length; index++) Songs.Add(next[index]);
            return;
        }
        Songs.Clear();
        foreach (var song in next) Songs.Add(song);
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

    private Task RestoreSearchAsync(CancellationToken token, bool navigate = true)
    {
        token.ThrowIfCancellationRequested();
        if (navigate) { AdvanceLoadGeneration(); loadCancellation?.Cancel(); }
        location = new("catalog-search", searchKeyword);
        PublishSearchView(restore: true);
        return Task.CompletedTask;
    }
    public Task ReturnToSearchAsync() => !HasSearchSession || stopping
        ? Task.CompletedTask : RequestNavigationAsync("catalog-search");
    public Task SwitchSearchKindAsync()
        => OpenSearchTabAsync(CatalogSearchKind == "歌单" ? "playlists" : "songs");
    public Task OpenSearchTabAsync(string tab)
    {
        if (publishing || !IsSearchPage || stopping) return Task.CompletedTask;
        if (tab is not (SearchOverview or "songs" or "playlists")) return Task.CompletedTask;
        RememberSearch();
        searchView = tab;
        searchKind = tab == "playlists" ? "playlists" : "songs";
        CatalogSearchKind = searchKind == "songs" ? "歌曲" : "歌单";
        PublishSearchView(restore: true);
        SearchBindings();
        return tab == SearchOverview
            ? EnsureSearchViewAsync(Task.CompletedTask, searchEpoch, lifetime.Token)
            : searchSnapshots.ContainsKey(searchKind)
                ? Task.CompletedTask
                : StartMissingSearchKindAsync(searchKind, lifetime.Token);
    }
    private Task StartMissingSearchKindAsync(string kind, CancellationToken token)
    {
        long epoch = searchEpoch, navigation = loadGeneration;
        string keyword = searchKeyword;
        string[] platforms = SearchDispatchPlatforms(kind);
        bool refresh = searchRefresh;
        var key = SearchStartIdentity(keyword, kind, platforms);
        if (searchStarts.TryGetValue(kind, out var pending) && pending.Epoch == epoch &&
            pending.Key == key)
            return pending.Work.WaitAsync(token);
        if (searchView != SearchOverview && searchKind == kind) ClearSearchVisibleSnapshot();
        var previous = searchStartTail;
        var flight = new SearchStartFlight(key, epoch);
        searchStarts[kind] = flight;
        searchStarting = true;
        searchStartTail = flight.Work = Track();
        SearchBindings();
        return flight.Work;

        async Task Track()
        {
            await Task.Yield();
            try
            {
                await RunAsync(async () =>
                {
                    await previous;
                    if (epoch != searchEpoch || navigation != loadGeneration) return;
                    if (searchSnapshots.ContainsKey(kind))
                    {
                        if (IsSearchPage && (searchView == SearchOverview || searchKind == kind))
                            PublishSearchView(restore: false);
                        return;
                    }
                    var snapshot = await Call("music.search.start", new { keyword, kind, platforms, refresh }, token);
                    string id = MusicDto.Text(snapshot, "id");
                    if (epoch != searchEpoch || navigation != loadGeneration)
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
    private async Task RetireSearchAsync()
    {
        ++searchEpoch; searchStarting = false;
        RetireCatalogPreviewSurface();
        string[] ids = searchIds.Values.ToArray();
        searchIds.Clear(); searchSnapshots.Clear(); searchCardCache.Clear(); searchScroll.Clear(); searchKeyword = "";
        foreach (var sections in searchSections.Values) sections.Clear();
        searchSongs.Clear(); searchSelectedKey = null;
        Presentation.ResetSearchPresentation();
        searchView = SearchOverview; searchKind = "songs";
        SearchBindings();
        await searchStartTail;
        await CancelSearchIds(ids);
    }
    private void RetireSearchTracked()
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

    private void ClearSearchVisibleSnapshot()
    {
        RetireCatalogPreviewSurface();
        CatalogSections.Clear();
        CatalogTags.Clear();
        if (IsSearchSongs || !HasSearchSession) searchSongs.Clear();
        SelectedSong = null;
        total = 0;
        SearchStatus = "";
        RefreshBindings();
    }
}
