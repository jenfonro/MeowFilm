using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public sealed record MusicDiscoveryKeyword(string Text, long Generation);
public sealed record MusicDiscoverySong(SongRow Song, string ChartId, long Generation)
{
    public string Title => Song.Title;
    public string Artist => Song.Artist;
}
public sealed record MusicDiscoveryChart(string Id, string Name, string Creator, string Cover,
    MusicDiscoverySong[] Songs, long Generation)
{
    public string CountText => $"{Songs.Length} 首";
}
public sealed record MusicDiscoveryFailure(string Stage, string? ChartId, string Code)
{
    public string Text => $"{Stage switch { "hot" => "热搜", "toplist" => "排行榜", "default" => "推荐词", _ => "榜单歌曲" }} · {MusicCatalogDto.ErrorText(Code)}";
}

public sealed partial class MusicCapability
{
    private MusicSearchDiscoverySnapshot? discoverySnapshot;
    private CancellationTokenSource? discoveryRequest;
    private Task discoveryTail = Task.CompletedTask;
    private long discoveryEpoch, navigationSubmission, discoveryViewEpoch;
    private bool discoveryViewVisible;
    private string discoveryStatus = "";
    public ObservableCollection<MusicDiscoveryKeyword> DiscoveryHot { get; } = [];
    public ObservableCollection<MusicDiscoveryKeyword> DiscoveryRecommended { get; } = [];
    public ObservableCollection<MusicDiscoveryChart> DiscoveryCharts { get; } = [];
    public ObservableCollection<MusicDiscoveryFailure> DiscoveryFailures { get; } = [];
    public bool IsDiscovery => location.Route == "catalog-discovery";
    public bool IsCatalogContent => IsCatalogPage && !IsDiscovery && !IsCatalogDetailPage && !IsSearchSongs;
    public bool CanUseDiscovery => !stopping && IsDiscovery && discoveryRequest is null &&
        discoverySnapshot is { } value && application.IsDiscoveryCurrent(value.Generation);
    public bool CanRotateDiscovery => CanUseDiscovery && commands == 0 && DiscoveryRecommended.Count > 0;
    public bool CanCancelDiscovery => !stopping && discoveryRequest is not null;
    public string DiscoveryStatus => discoveryStatus;

    private void InitializeDiscovery() => application.DiscoveryInvalidated += DiscoveryInvalidated;
    private void UnsubscribeDiscovery() => application.DiscoveryInvalidated -= DiscoveryInvalidated;
    private void DiscoveryBindings()
    {
        foreach (string name in new[] { nameof(IsDiscovery), nameof(IsCatalogContent), nameof(CanUseDiscovery),
            nameof(CanRotateDiscovery), nameof(CanCancelDiscovery), nameof(DiscoveryStatus) }) Changed(name);
    }
    private void DiscoveryInvalidated()
    {
        if (Presentation.Dispatcher.HasShutdownStarted || Presentation.Dispatcher.HasShutdownFinished) return;
        _ = Presentation.Dispatcher.InvokeAsync(() =>
        {
            if (disposed || stopping) return;
            // Initial hydration may precede this callback but a new valid read may
            // already have completed. Never clear that newer, current snapshot.
            if (discoverySnapshot is { } old && !application.IsDiscoveryCurrent(old.Generation))
            { ClearDiscovery(); discoveryStatus = "账号状态已变更"; }
            DiscoveryBindings();
        });
    }
    private void ClearDiscovery()
    {
        discoverySnapshot = null;
        DiscoveryHot.Clear(); DiscoveryRecommended.Clear(); DiscoveryCharts.Clear(); DiscoveryFailures.Clear();
    }
    private void PublishDiscovery(MusicSearchDiscoverySnapshot value)
    {
        MusicDto.Require(value.Generation > 0 && value.Hot.Length <= 100 && value.Recommended.Length <= 6 &&
            value.Charts.Length <= 3 && value.Charts.All(c => c.Songs.Length <= 20));
        var charts = value.Charts.Select(c => new MusicDiscoveryChart(c.Id, c.Name, c.Creator, c.Cover,
            c.Songs.Select(s => new MusicDiscoverySong(SongRow.Read(s), c.Id, value.Generation)).ToArray(), value.Generation)).ToArray();
        ClearDiscovery(); discoverySnapshot = value;
        foreach (var hot in value.Hot) DiscoveryHot.Add(new(hot.Keyword, value.Generation));
        foreach (string text in value.Recommended) DiscoveryRecommended.Add(new(text, value.Generation));
        foreach (var chart in charts) DiscoveryCharts.Add(chart);
        foreach (var failure in value.Failures) DiscoveryFailures.Add(new(failure.Stage, failure.ChartId, failure.Code));
        discoveryStatus = value.Hot.Length == 0 && value.Charts.Length == 0 && value.Recommended.Length == 0 ? "暂无内容" : "";
        DiscoveryBindings();
    }
    private Task LoadDiscoveryAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (stopping) return Task.CompletedTask;
        _ = ActivateSearchHistoryAsync(SearchHistorySurface.Discovery);
        bool retainCompleted = refresh && discoverySnapshot is { } completed &&
            application.IsDiscoveryCurrent(completed.Generation);
        long epoch = ++discoveryEpoch, navigation = AdvanceLoadGeneration();
        loadCancellation?.Cancel(); discoveryRequest?.Cancel();
        var previous = discoveryTail;
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        discoveryRequest = request; loadCancellation = request;
        location = new("catalog-discovery"); heading = "搜索发现";
        collection = null; selectedSong = null; total = 0; status = "";
        CatalogSections.Clear(); CatalogTags.Clear(); Songs.Clear();
        if (!retainCompleted) ClearDiscovery();
        discoveryStatus = "加载中"; RefreshBindings();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(done.Task);
        return discoveryTail = Read();
        async Task Read()
        {
            await Task.Yield();
            try
            {
                await previous;
                request.Token.ThrowIfCancellationRequested();
                var result = await application.ReadDiscoveryAsync(refresh, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (stopping || epoch != discoveryEpoch || navigation != loadGeneration || !IsDiscovery) return;
                if (!application.IsDiscoveryCurrent(result.Generation)) throw new MusicOperationException("STALE_SESSION");
                PublishDiscovery(result);
                var target = locations.GetValueOrDefault("catalog-discovery");
                if (target is not null) _ = Presentation.Dispatcher.InvokeAsync(() =>
                { if (epoch == discoveryEpoch && navigation == loadGeneration && IsDiscovery) Presentation.RestoreScroll(target.Scroll); });
            }
            catch (OperationCanceledException)
            { if (epoch == discoveryEpoch && IsDiscovery) discoveryStatus = "已取消"; }
            catch (Exception error)
            {
                if (!stopping && epoch == discoveryEpoch && navigation == loadGeneration && IsDiscovery)
                    discoveryStatus = error is MusicOperationException { Code: "CANCELLED" } ? "已取消" :
                        MusicCatalogDto.ErrorText(error is MusicOperationException e ? e.Code : "INVALID_RESPONSE");
            }
            finally
            {
                if (ReferenceEquals(discoveryRequest, request)) discoveryRequest = null;
                if (ReferenceEquals(loadCancellation, request)) loadCancellation = null;
                request.Dispose(); loadDrains.Remove(done.Task); done.TrySetResult();
                if (!disposed) RefreshBindings();
            }
        }
    }
    private Task RetireDiscoveryAsync()
    {
        var previous = discoveryTail;
        discoveryEpoch++;
        discoveryRequest?.Cancel();
        ClearDiscovery(); DiscoveryBindings();
        return discoveryTail = Retire();
        async Task Retire()
        {
            await Task.Yield();
            // Also serialize retirement against the next read. Otherwise a
            // finishing Unloaded callback could cancel a newly opened page.
            await previous;
            await application.CancelDiscoveryAndDrainAsync();
        }
    }
    public async Task CancelDiscoveryAsync()
    {
        if (stopping || !IsDiscovery) return;
        await RetireDiscoveryAsync();
        if (IsDiscovery && !stopping) { discoveryStatus = "已取消"; DiscoveryBindings(); }
    }
    public Task RefreshDiscoveryAsync() => !stopping && IsDiscovery && commands == 0
        ? LoadDiscoveryAsync(true, lifetime.Token) : Task.CompletedTask;
    public async Task ActivateDiscoveryViewAsync()
    {
        discoveryViewVisible = true;
        var searchHistory = IsDiscovery
            ? ActivateSearchHistoryAsync(SearchHistorySurface.Discovery)
            : Task.CompletedTask;
        long visible = ++discoveryViewEpoch, work = discoveryEpoch;
        var previous = discoveryTail;
        bool needsRead = discoveryRequest is null || discoveryRequest.IsCancellationRequested;
        await previous;
        if (stopping || disposed || !discoveryViewVisible || visible != discoveryViewEpoch ||
            work != discoveryEpoch || !IsDiscovery || !needsRead || CanUseDiscovery)
        {
            await searchHistory;
            return;
        }
        // A Loaded following Unloaded must retain its intent even while the old
        // cancelled HTTP/read/dispose still owns resources. Conversely an ordinary
        // first load failure is not an invitation for an automatic retry.
        await Task.WhenAll(LoadDiscoveryAsync(false, lifetime.Token), searchHistory);
    }
    public Task DeactivateDiscoveryViewAsync()
    {
        discoveryViewVisible = false; discoveryViewEpoch++;
        if (IsDiscovery) RememberLocation();
        return RetireDiscoveryPageAsync();
    }

    private Task RetireDiscoveryPageAsync() => Task.WhenAll(
        RetireDiscoveryAsync(),
        DeactivateSearchHistoryAsync(SearchHistorySurface.Discovery));

    public Task RotateDiscoveryAsync()
    {
        if (!CanRotateDiscovery || discoverySnapshot is not { } snapshot) return Task.CompletedTask;
        long epoch = discoveryEpoch;
        return RunAsync(async () =>
        {
            if (!CanUseDiscovery || epoch != discoveryEpoch || !ReferenceEquals(discoverySnapshot, snapshot)) return;
            var next = await application.RotateDiscoveryAsync(snapshot.Generation, lifetime.Token);
            if (!stopping && epoch == discoveryEpoch && IsDiscovery &&
                application.IsDiscoveryCurrent(next.Generation))
                PublishDiscovery(next);
        });
    }
    public async Task SearchDiscoveryKeywordAsync(MusicDiscoveryKeyword word)
    {
        if (!CanUseDiscovery || commands > 0 || word is null ||
            !(DiscoveryHot.Any(r => ReferenceEquals(r, word)) || DiscoveryRecommended.Any(r => ReferenceEquals(r, word)))) return;
        await SearchAsync(word.Text, lifetime.Token);
        if (!stopping && IsSearchPage && searchKeyword == word.Text) await RequestNavigationAsync("catalog-search");
    }
    private bool CurrentDiscoveryChart(MusicDiscoveryChart chart) =>
        CanUseDiscovery && DiscoveryCharts.Any(c => ReferenceEquals(c, chart));
    public Task OpenDiscoveryChartAsync(MusicDiscoveryChart chart) =>
        !CurrentDiscoveryChart(chart) || commands > 0 ? Task.CompletedTask :
            RequestNavigationAsync("catalog-playlist/wy/" + Uri.EscapeDataString(chart.Id));
    public Task UseDiscoverySongAsync(MusicDiscoverySong row, bool play)
    {
        if (!CanUseDiscovery || commands > 0 || row is null) return Task.CompletedTask;
        var chart = DiscoveryCharts.FirstOrDefault(c => c.Songs.Any(s => ReferenceEquals(s, row)));
        if (chart is null) return Task.CompletedTask;
        long epoch = discoveryEpoch;
        if (play)
        {
            TrackMusicRecovery(RetireMusicRecovery());
            InvalidateCatalogAction(); onlineCancellation?.Cancel();
        }
        return RunAsync(async () =>
        {
            if (epoch != discoveryEpoch || !CurrentDiscoveryChart(chart)) return;
            object context = new { sourcePlatform = "wy", sourceType = "playlist", sourceId = chart.Id, sourceTitle = chart.Name };
            if (play)
                await QueueCall("music.queue.replace", new { songs = chart.Songs.Select(s => s.Song.Raw).ToArray(),
                    index = Array.IndexOf(chart.Songs, row), context });
            else
                await QueueCall("music.queue.append", new { songs = new[] { row.Song.Raw }, next = true, context });
            await ReadQueue(lifetime.Token);
            // An admitted mutation is not rolled back on invalidation, but it
            // must never open a media input for a now-retired display callback.
            if (play && epoch == discoveryEpoch && CurrentDiscoveryChart(chart)) await StartCurrent(lifetime.Token);
        });
    }
}
