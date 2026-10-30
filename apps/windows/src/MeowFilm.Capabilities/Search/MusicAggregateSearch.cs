using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public sealed record MusicSearchProgressRow(string Platform, int Page, int Count, bool Loading, bool More, string Error)
{
    public string Name => new MusicMetadataPlatformRow(Platform, false).Label;
    public string Progress => Loading ? $"第 {Page + 1} 页 · 搜索中" : $"{Count} 项 · 第 {Page} 页";
    public string ErrorText => Error.Length == 0 ? "" : MusicCatalogDto.ErrorText(Error);
    public bool CanRetry => !Loading && Error.Length > 0;
}

public sealed partial class MusicCapability
{
    private const string AggregateOverview = "overview";
    private readonly Dictionary<string, JsonElement> aggregateSnapshots = [];
    private readonly Dictionary<string, double> aggregateScroll = [];
    private readonly Dictionary<string, string> aggregateIds = [];
    private string aggregateKeyword = "", aggregateKind = "songs", aggregateView = AggregateOverview;
    private long aggregateEpoch, aggregateAccountEpoch;
    private bool aggregateStarting;
    private Task aggregateStartTail = Task.CompletedTask;
    private readonly Dictionary<string, AggregateStartFlight> aggregateStarts = [];
    private sealed record AggregateStartKey(string Keyword, string Kind, string Platforms,
        long AccountEpoch);
    private sealed class AggregateStartFlight(AggregateStartKey key, long epoch)
    {
        internal AggregateStartKey Key { get; } = key;
        internal long Epoch { get; } = epoch;
        internal Task Work { get; set; } = Task.CompletedTask;
    }
    public ObservableCollection<MusicSearchProgressRow> SearchProgress { get; } = [];
    public bool IsAggregateSearch => location.Route == "catalog-search";
    public string AggregateSearchTab => aggregateView;
    public bool IsAggregateOverview => IsAggregateSearch && aggregateView == AggregateOverview;
    public bool IsAggregateSongs => IsAggregateSearch && aggregateView == "songs";
    public bool IsAggregatePlaylists => IsAggregateSearch && aggregateView == "playlists";
    public bool IsCatalogBrowsePage => IsCatalogPage && !IsAggregateSearch && !IsDiscovery;
    public bool HasAggregateSearch => aggregateKeyword.Length > 0 &&
        (aggregateSnapshots.Count > 0 || aggregateStarts.Count > 0);
    public bool CanAggregateMore => CanCatalogBrowse && !IsAggregateOverview && !aggregateStarting &&
        aggregateSnapshots.TryGetValue(aggregateKind, out var snapshot) &&
        !snapshot.GetProperty("loading").GetBoolean() && snapshot.GetProperty("more").GetBoolean();
    public bool CanLoadMoreAggregateSongs => IsAggregateSongs && CanAggregateMore;
    public bool CanLoadMoreAggregatePlaylists => IsAggregatePlaylists && CanAggregateMore;
    public bool CanCancelAggregateSearch => !stopping && (aggregateStarting ||
        aggregateSnapshots.Values.Any(s => !s.GetProperty("cancelled").GetBoolean()));
    public string AggregateSearchSummary => !aggregateSnapshots.TryGetValue(aggregateKind, out var snapshot) ? "" :
        $"{snapshot.GetProperty("items").GetArrayLength()} 项" + (snapshot.GetProperty("loading").GetBoolean() ? " · 搜索中" : "");
    public string AggregateSearchStatus { get; private set; } = "";

    private void InitializeAggregateSearch() => application.SearchChanged += AggregateSearchChanged;
    private void UnsubscribeAggregateSearch() => application.SearchChanged -= AggregateSearchChanged;
    private void AggregateSearchChanged(JsonElement snapshot)
    {
        if (page.Dispatcher.HasShutdownStarted || page.Dispatcher.HasShutdownFinished) return;
        _ = page.Dispatcher.InvokeAsync(() =>
        {
            if (disposed || stopping) return;
            try { AcceptAggregateSnapshot(snapshot); }
            catch { AggregateSearchStatus = "搜索结果格式无效"; SearchBindings(); }
        }, DispatcherPriority.Background);
    }
    private void SearchBindings()
    {
        foreach (string name in new[] { nameof(IsAggregateSearch), nameof(IsCatalogBrowsePage), nameof(HasAggregateSearch),
            nameof(AggregateSearchTab), nameof(IsAggregateOverview), nameof(IsAggregateSongs), nameof(IsAggregatePlaylists),
            nameof(CanAggregateMore), nameof(CanLoadMoreAggregateSongs), nameof(CanLoadMoreAggregatePlaylists),
            nameof(CanCancelAggregateSearch), nameof(AggregateSearchSummary), nameof(AggregateSearchStatus) })
            Changed(name);
    }
    private void RememberAggregateSearch()
    {
        if (IsAggregateSearch) aggregateScroll[aggregateView] = page.CaptureScroll();
    }

    private Task BeginAggregateSearchAsync(string keyword, CancellationToken token, bool refresh = false)
    {
        if (stopping) return Task.CompletedTask;
        if (!searchPreferencesLoaded) return CatalogBusinessError("SEARCH_SETTINGS_UNCONFIRMED");
        RememberAggregateSearch();
        if (aggregateKeyword != keyword)
        {
            aggregateView = AggregateOverview;
            aggregateKind = "songs";
            CatalogSearchKind = "歌曲";
        }
        string kind = aggregateView == "playlists" ? "playlists" : "songs";
        aggregateKind = kind;
        string[] platforms = SearchDispatchPlatforms(kind);
        var key = AggregateStartIdentity(keyword, kind, platforms);
        if (aggregateKeyword == keyword && aggregateAccountEpoch == accountEpoch &&
            aggregateStarts.TryGetValue(kind, out var pending) && pending.Epoch == aggregateEpoch &&
            pending.Key == key)
        {
            aggregateKind = kind;
            return EnsureAggregateViewAsync(pending.Work, pending.Epoch, token, kind);
        }
        if (!refresh && aggregateKeyword == keyword && aggregateAccountEpoch == accountEpoch)
        {
            aggregateKind = kind;
            bool hasSnapshot = aggregateSnapshots.ContainsKey(kind);
            var current = hasSnapshot ? RestoreAggregateSearchAsync(token) : StartMissingAggregateKindAsync(kind, token);
            return EnsureAggregateViewAsync(current, aggregateEpoch, token, hasSnapshot ? null : kind);
        }
        var previous = aggregateStartTail;
        long epoch = ++aggregateEpoch, navigation = AdvanceLoadGeneration();
        loadCancellation?.Cancel();
        var oldIds = aggregateIds.Values.ToArray();
        aggregateIds.Clear(); aggregateSnapshots.Clear(); aggregateScroll.Clear();
        aggregateStarts.Clear();
        aggregateKeyword = keyword; aggregateKind = kind; aggregateAccountEpoch = accountEpoch;
        aggregateStarting = true; AggregateSearchStatus = "";
        location = new("catalog-search", keyword); heading = keyword;
        catalogQuery = keyword; catalogLoadedKind = CatalogSearchKind;
        catalogLoadedPlatform = "aggregate"; catalogLoadedTarget = null; catalogExpiresAt = null;
        CatalogSections.Clear(); CatalogTags.Clear(); Songs.Clear(); SearchProgress.Clear();
        collection = null; selectedSong = null; total = 0; status = "";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        var flight = new AggregateStartFlight(key, epoch);
        aggregateStarts[kind] = flight;
        aggregateStartTail = flight.Work = Start();
        RefreshBindings();
        var work = EnsureAggregateViewAsync(flight.Work, epoch, token, kind);
        PublishAggregateView(restore: false);
        return work;

        async Task Start()
        {
            await Task.Yield();
            try
            {
                await previous;
                foreach (string oldId in oldIds)
                    await application.ExecuteAsync("music.search.cancel", new { id = oldId }, lifetime.Token);
                if (stopping || epoch != aggregateEpoch || navigation != loadGeneration) return;
                // Start the first visible kind; the comprehensive projection admits
                // its other visible kind after this bounded start has settled.
                token.ThrowIfCancellationRequested();
                var snapshot = await Call("music.search.start", new { keyword, kind, platforms }, token);
                string id = MusicDto.Text(snapshot, "id");
                if (stopping || epoch != aggregateEpoch || navigation != loadGeneration)
                {
                    await application.ExecuteAsync("music.search.cancel", new { id }, lifetime.Token);
                    return;
                }
                aggregateIds[kind] = id;
                AcceptAggregateSnapshot(snapshot);
                // A very fast provider can finish before its id is admitted
                // to the UI. Read one current snapshot; never start polling.
                AcceptAggregateSnapshot(await Call("music.search.get", new { id }, token));
            }
            catch (OperationCanceledException)
            {
                if (epoch == aggregateEpoch)
                    await CancelAggregateIds(aggregateIds.Values.ToArray());
            }
            catch (Exception error)
            {
                if (!stopping && epoch == aggregateEpoch)
                {
                    AggregateSearchStatus = MusicCatalogDto.ErrorText(error is MusicOperationException e ? e.Code : "INVALID_RESPONSE");
                    await CancelAggregateIds(aggregateIds.Values.ToArray());
                }
            }
            finally
            {
                loadDrains.Remove(done.Task); done.TrySetResult();
                CompleteAggregateStart(flight);
            }
        }
    }

    private async Task EnsureAggregateViewAsync(Task primary, long epoch, CancellationToken token,
        string? attemptedKind = null)
    {
        if (stopping || epoch != aggregateEpoch || aggregateView != AggregateOverview)
        {
            await primary.WaitAsync(token);
            return;
        }
        List<Task> starts = [primary];
        foreach (string kind in new[] { "songs", "playlists" })
        {
            if (kind == attemptedKind || aggregateSnapshots.ContainsKey(kind)) continue;
            starts.Add(StartMissingAggregateKindAsync(kind, token));
        }
        await Task.WhenAll(starts).WaitAsync(token);
    }

    private AggregateStartKey AggregateStartIdentity(string keyword, string kind,
        string[] platforms) =>
        new(keyword, kind, string.Join("\u001f", platforms), accountEpoch);

    private void CompleteAggregateStart(AggregateStartFlight flight)
    {
        if (aggregateStarts.TryGetValue(flight.Key.Kind, out var current) &&
            ReferenceEquals(current, flight))
            aggregateStarts.Remove(flight.Key.Kind);
        aggregateStarting = aggregateStarts.Values.Any(value => value.Epoch == aggregateEpoch);
        if (disposed) return;
        if (!stopping && flight.Epoch == aggregateEpoch && IsAggregateSearch &&
            (aggregateView == AggregateOverview || aggregateKind == flight.Key.Kind))
        {
            bool missing = !aggregateSnapshots.ContainsKey(flight.Key.Kind);
            string message = AggregateSearchStatus;
            PublishAggregateView(restore: false);
            if (missing && message.Length > 0)
            {
                AggregateSearchStatus = message;
                SearchBindings();
            }
        }
        else SearchBindings();
    }

    private void AcceptAggregateSnapshot(JsonElement value)
    {
        string id = MusicDto.Text(value, "id"), kind = MusicDto.Text(value, "kind");
        if (MusicDto.Text(value, "keyword") != aggregateKeyword || !aggregateIds.TryGetValue(kind, out var expected) || id != expected) return;
        long revision = MusicDto.Integer(value, "revision");
        if (aggregateSnapshots.TryGetValue(kind, out var before) && revision < MusicDto.Integer(before, "revision")) return;
        MusicDto.Require(value.GetProperty("items").ValueKind == JsonValueKind.Array &&
            value.GetProperty("items").GetArrayLength() <= 10000 &&
            value.GetProperty("platforms").ValueKind == JsonValueKind.Array &&
            value.GetProperty("platforms").GetArrayLength() <= 5);
        aggregateSnapshots[kind] = value.Clone();
        if (IsAggregateSearch && (aggregateView == AggregateOverview || aggregateKind == kind))
            PublishAggregateView(restore: false);
        SearchBindings();
    }

    private MusicCatalogCard[] AggregateCards(string kind) =>
        aggregateSnapshots.TryGetValue(kind, out var snapshot)
            ? MusicCatalogDto.Aggregate(snapshot.GetProperty("items"), kind)
            : [];
    private bool AggregateKindLoading(string kind) =>
        aggregateStarts.TryGetValue(kind, out var start) && start.Epoch == aggregateEpoch ||
        aggregateSnapshots.TryGetValue(kind, out var snapshot) &&
        snapshot.GetProperty("loading").GetBoolean();

    private void PublishAggregateView(bool restore)
    {
        var songCards = AggregateCards("songs");
        var playlistCards = AggregateCards("playlists");
        RetireCatalogPreviewSurface();
        string? selected = selectedSong?.SelectionKey;
        double scroll = restore ? aggregateScroll.GetValueOrDefault(aggregateView) : page.CaptureScroll();
        publishing = true;
        try
        {
            catalogQuery = aggregateKeyword; catalogLoadedPlatform = "aggregate"; catalogLoadedTarget = null;
            catalogAccountStamp = ""; catalogExpiresAt = null; catalogMore = false; catalogContinuation = null;
            catalogLoadedKind = CatalogSearchKind = aggregateKind == "songs" ? "歌曲" : "歌单";
            heading = aggregateKeyword; collection = null;
            CatalogSections.Clear();
            if (aggregateView == AggregateOverview)
            {
                CatalogSections.Add(new("单曲", songCards.Take(6).ToArray(),
                    IsLoading: AggregateKindLoading("songs")));
                CatalogSections.Add(new("歌单", playlistCards.Take(6).ToArray(),
                    IsLoading: AggregateKindLoading("playlists")));
                total = songCards.Length + playlistCards.Length;
            }
            else if (aggregateView == "songs")
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
            SearchProgress.Clear();
            if (aggregateSnapshots.TryGetValue(aggregateKind, out var active))
                foreach (var state in active.GetProperty("platforms").EnumerateArray())
                    SearchProgress.Add(new(MusicDto.Text(state, "platform"), MusicDto.Count(state, "page"),
                        MusicDto.Count(state, "count"), state.GetProperty("loading").GetBoolean(),
                        state.GetProperty("more").GetBoolean(), MusicCatalogDto.Text(state, "error")));
            AggregateSearchStatus = AggregateViewStatus();
            status = ""; RefreshBindings();
        }
        finally { publishing = false; }
        long generation = loadGeneration, epoch = aggregateEpoch;
        _ = page.Dispatcher.InvokeAsync(() =>
        {
            if (!stopping && generation == loadGeneration && epoch == aggregateEpoch && IsAggregateSearch)
                page.RestoreScroll(scroll);
        }, DispatcherPriority.Loaded);
    }

    private string AggregateViewStatus()
    {
        string[] kinds = aggregateView == AggregateOverview ? ["songs", "playlists"] : [aggregateKind];
        var snapshots = kinds.Where(aggregateSnapshots.ContainsKey).Select(kind => aggregateSnapshots[kind]).ToArray();
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

    private Task RestoreAggregateSearchAsync(CancellationToken token, bool navigate = true)
    {
        token.ThrowIfCancellationRequested();
        if (navigate) { AdvanceLoadGeneration(); loadCancellation?.Cancel(); }
        location = new("catalog-search", aggregateKeyword);
        PublishAggregateView(restore: true);
        return Task.CompletedTask;
    }
    public Task ReturnToAggregateSearchAsync() => !HasAggregateSearch || stopping
        ? Task.CompletedTask : RequestNavigationAsync("catalog-search");
    public Task SwitchAggregateSearchKindAsync()
        => OpenAggregateSearchTabAsync(CatalogSearchKind == "歌单" ? "playlists" : "songs");
    public Task OpenAggregateSearchTabAsync(string tab)
    {
        if (publishing || !IsAggregateSearch || stopping) return Task.CompletedTask;
        if (tab is not (AggregateOverview or "songs" or "playlists")) return Task.CompletedTask;
        RememberAggregateSearch();
        aggregateView = tab;
        aggregateKind = tab == "playlists" ? "playlists" : "songs";
        CatalogSearchKind = aggregateKind == "songs" ? "歌曲" : "歌单";
        PublishAggregateView(restore: true);
        SearchBindings();
        return tab == AggregateOverview
            ? EnsureAggregateViewAsync(Task.CompletedTask, aggregateEpoch, lifetime.Token)
            : aggregateSnapshots.ContainsKey(aggregateKind)
                ? Task.CompletedTask
                : StartMissingAggregateKindAsync(aggregateKind, lifetime.Token);
    }
    private Task StartMissingAggregateKindAsync(string kind, CancellationToken token)
    {
        long epoch = aggregateEpoch, navigation = loadGeneration;
        string keyword = aggregateKeyword;
        string[] platforms = SearchDispatchPlatforms(kind);
        var key = AggregateStartIdentity(keyword, kind, platforms);
        if (aggregateStarts.TryGetValue(kind, out var pending) && pending.Epoch == epoch &&
            pending.Key == key)
            return pending.Work.WaitAsync(token);
        if (aggregateView != AggregateOverview && aggregateKind == kind) ClearAggregateVisibleSnapshot();
        var previous = aggregateStartTail;
        var flight = new AggregateStartFlight(key, epoch);
        aggregateStarts[kind] = flight;
        aggregateStarting = true;
        aggregateStartTail = flight.Work = Track();
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
                    if (epoch != aggregateEpoch || navigation != loadGeneration) return;
                    if (aggregateSnapshots.ContainsKey(kind))
                    {
                        if (IsAggregateSearch && (aggregateView == AggregateOverview || aggregateKind == kind))
                            PublishAggregateView(restore: false);
                        return;
                    }
                    var snapshot = await Call("music.search.start", new { keyword, kind, platforms }, token);
                    string id = MusicDto.Text(snapshot, "id");
                    if (epoch != aggregateEpoch || navigation != loadGeneration)
                    { await application.ExecuteAsync("music.search.cancel", new { id }, lifetime.Token); return; }
                    aggregateIds[kind] = id;
                    AcceptAggregateSnapshot(snapshot);
                    AcceptAggregateSnapshot(await Call("music.search.get", new { id }, token));
                });
            }
            finally { CompleteAggregateStart(flight); }
        }
    }
    public Task MoreAggregateSearchAsync() => !CanAggregateMore ? Task.CompletedTask :
        AggregateCommandAsync("music.search.next", null);
    public Task RetryAggregateSearchAsync(MusicSearchProgressRow row) =>
        stopping || !IsAggregateSearch || !SearchProgress.Contains(row) || !row.CanRetry ? Task.CompletedTask :
        AggregateCommandAsync("music.search.retry", row.Platform);
    private Task AggregateCommandAsync(string method, string? platform)
    {
        if (!aggregateIds.TryGetValue(aggregateKind, out string? id)) return Task.CompletedTask;
        return RunAsync(async () => AcceptAggregateSnapshot(await Call(method,
            platform is null ? new { id } : (object)new { id, platform }, lifetime.Token)));
    }
    public Task CancelAggregateSearchAsync()
    {
        if (stopping) return Task.CompletedTask;
        ++aggregateEpoch; aggregateStarting = false;
        string[] ids = aggregateIds.Values.ToArray();
        var previous = aggregateStartTail;
        return RunAsync(async () =>
        {
            await previous;
            await CancelAggregateIds(ids);
            AggregateSearchStatus = "已取消"; SearchBindings();
        });
    }
    private async Task CancelAggregateIds(string[] ids)
    {
        foreach (string id in ids)
        {
            var receipt = await application.ExecuteAsync("music.search.cancel", new { id }, lifetime.Token);
            if (receipt.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                AcceptAggregateSnapshot(receipt);
        }
    }
    private async Task RetireAggregateSearchAsync()
    {
        ++aggregateEpoch; aggregateStarting = false;
        RetireCatalogPreviewSurface();
        string[] ids = aggregateIds.Values.ToArray();
        aggregateIds.Clear(); aggregateSnapshots.Clear(); aggregateScroll.Clear(); aggregateKeyword = "";
        aggregateView = AggregateOverview; aggregateKind = "songs";
        SearchProgress.Clear(); SearchBindings();
        await aggregateStartTail;
        await CancelAggregateIds(ids);
    }
    private void RetireAggregateSearchTracked()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        _ = Retire();
        async Task Retire()
        {
            try { await RetireAggregateSearchAsync(); }
            catch { /* The application retains physical drain ownership. No stale UI result survives. */ }
            finally { loadDrains.Remove(done.Task); done.TrySetResult(); }
        }
    }

    private void ClearAggregateVisibleSnapshot()
    {
        RetireCatalogPreviewSurface();
        CatalogSections.Clear();
        CatalogTags.Clear();
        Songs.Clear();
        SelectedSong = null;
        SearchProgress.Clear();
        total = 0;
        AggregateSearchStatus = "";
        RefreshBindings();
    }
}
