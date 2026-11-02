using System.Text.Json;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private const string SearchOverview = "overview";
    private readonly Dictionary<string, JsonElement> searchSnapshots = [];
    private readonly Dictionary<string, double> searchScroll = [];
    private readonly Dictionary<string, string> searchIds = [];
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
    public string SearchStatus { get; private set; } = "";

    private void InitializeSearch() => application.SearchChanged += SearchChanged;
    private void UnsubscribeSearch() => application.SearchChanged -= SearchChanged;
    private void SearchChanged(JsonElement snapshot)
    {
        if (page.Dispatcher.HasShutdownStarted || page.Dispatcher.HasShutdownFinished) return;
        _ = page.Dispatcher.InvokeAsync(() =>
        {
            if (disposed || stopping) return;
            try { AcceptSearchSnapshot(snapshot); }
            catch { SearchStatus = "搜索结果格式无效"; SearchBindings(); }
        }, DispatcherPriority.Background);
    }
    private void SearchBindings()
    {
        foreach (string name in new[] { nameof(IsSearchPage), nameof(IsCatalogBrowsePage), nameof(HasSearchSession),
            nameof(SearchTab), nameof(IsSearchOverview), nameof(IsSearchSongs), nameof(IsSearchPlaylists),
            nameof(CanSearchMore), nameof(CanLoadMoreSearchSongs), nameof(CanLoadMoreSearchPlaylists),
            nameof(SearchStatus) })
            Changed(name);
    }
    private void RememberSearch()
    {
        if (IsSearchPage) searchScroll[searchView] = page.CaptureScroll();
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
        searchIds.Clear(); searchSnapshots.Clear(); searchScroll.Clear();
        searchStarts.Clear();
        searchKeyword = keyword; searchKind = kind; searchAccountEpoch = accountEpoch;
        searchStarting = true; searchRefresh = refresh; SearchStatus = "";
        location = new("catalog-search", keyword); heading = keyword;
        catalogQuery = keyword; catalogLoadedKind = CatalogSearchKind;
        catalogLoadedPlatform = ""; catalogLoadedTarget = null; catalogExpiresAt = null;
        CatalogSections.Clear(); CatalogTags.Clear(); Songs.Clear();
        collection = null; selectedSong = null; total = 0; status = "";
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

    private MusicCatalogCard[] SearchCards(string kind) =>
        searchSnapshots.TryGetValue(kind, out var snapshot)
            ? MusicCatalogDto.SearchResults(snapshot.GetProperty("items"), kind)
            : [];
    private bool SearchKindLoading(string kind) =>
        searchStarts.TryGetValue(kind, out var start) && start.Epoch == searchEpoch ||
        searchSnapshots.TryGetValue(kind, out var snapshot) &&
        snapshot.GetProperty("loading").GetBoolean();

    private void PublishSearchView(bool restore)
    {
        var songCards = SearchCards("songs");
        var playlistCards = SearchCards("playlists");
        RetireCatalogPreviewSurface();
        string? selected = selectedSong?.SelectionKey;
        double scroll = restore ? searchScroll.GetValueOrDefault(searchView) : page.CaptureScroll();
        publishing = true;
        try
        {
            catalogQuery = searchKeyword; catalogLoadedPlatform = ""; catalogLoadedTarget = null;
            catalogAccountStamp = ""; catalogExpiresAt = null; catalogMore = false; catalogContinuation = null;
            catalogLoadedKind = CatalogSearchKind = searchKind == "songs" ? "歌曲" : "歌单";
            heading = searchKeyword; collection = null;
            CatalogSections.Clear();
            if (searchView == SearchOverview)
            {
                CatalogSections.Add(new("单曲", songCards.Take(6).ToArray(),
                    IsLoading: SearchKindLoading("songs")));
                CatalogSections.Add(new("歌单", playlistCards.Take(6).ToArray(),
                    IsLoading: SearchKindLoading("playlists")));
                total = songCards.Length + playlistCards.Length;
            }
            else if (searchView == "songs")
            {
                CatalogSections.Add(new("", songCards));
                total = songCards.Length;
            }
            else
            {
                CatalogSections.Add(new("", playlistCards));
                total = playlistCards.Length;
            }
            CatalogTags.Clear();
            Songs.Clear(); foreach (var card in songCards) if (card.Song is { } song) Songs.Add(song);
            SelectedSong = Songs.FirstOrDefault(s => s.SelectionKey == selected);
            SearchStatus = SearchViewStatus();
            status = ""; RefreshBindings();
        }
        finally { publishing = false; }
        long generation = loadGeneration, epoch = searchEpoch;
        _ = page.Dispatcher.InvokeAsync(() =>
        {
            if (!stopping && generation == loadGeneration && epoch == searchEpoch && IsSearchPage)
                page.RestoreScroll(scroll);
        }, DispatcherPriority.Loaded);
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
        searchIds.Clear(); searchSnapshots.Clear(); searchScroll.Clear(); searchKeyword = "";
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
        Songs.Clear();
        SelectedSong = null;
        total = 0;
        SearchStatus = "";
        RefreshBindings();
    }
}
