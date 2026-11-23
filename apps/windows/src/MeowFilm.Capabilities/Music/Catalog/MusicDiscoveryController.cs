using System.Collections.ObjectModel;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Capabilities;

public sealed record MusicDiscoveryKeyword(string Text, long Generation);
public sealed record MusicDiscoverySong(SongRow Song, string ChartId, long Generation) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Title => Song.Title;
    public string Artist => Song.Artist;
    internal void RefreshSong(System.Text.Json.JsonElement? value, long publication = -1)
    {
        if (!Song.ApplyRepository(value, publication)) return;
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Artist)));
    }
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

/// <summary>Standalone owner for music search-discovery state, hot keywords, and charts.</summary>
public sealed class MusicDiscoveryController : MusicController
{
    private readonly MusicSettingsController settings;
    private readonly MusicPlaybackController playbackController;
    private MusicSearchDiscoverySnapshot? discoverySnapshot;
    private CancellationTokenSource? discoveryRequest;
    private Task discoveryTail = Task.CompletedTask;
    private long discoveryEpoch, discoveryViewEpoch;
    private bool discoveryViewVisible;
    private string discoveryStatus = "";

    internal MusicDiscoveryController(MusicRuntimeContext ctx, MusicSettingsController settings, MusicPlaybackController playbackController) : base(ctx)
    {
        this.settings = settings;
        this.playbackController = playbackController;
        application.DiscoveryInvalidated += DiscoveryInvalidated;
    }

    internal Action OnClearCatalogSectionsAndTags { get; set; } = () => { };
    internal Action OnInvalidateCatalogAction { get; set; } = () => { };
    internal Func<CatalogTarget, string?, Task> OnOpenCatalogTargetAsync { get; set; } = (_, _) => Task.CompletedTask;
    internal Func<bool> GetIsSearchPage { get; set; } = () => false;
    internal Func<string> GetSearchKeyword { get; set; } = () => "";

    public ObservableCollection<MusicDiscoveryKeyword> DiscoveryHot { get; } = [];
    public ObservableCollection<MusicDiscoveryKeyword> DiscoveryRecommended { get; } = [];
    public ObservableCollection<MusicDiscoveryChart> DiscoveryCharts { get; } = [];
    public ObservableCollection<MusicDiscoveryFailure> DiscoveryFailures { get; } = [];
    public bool IsDiscovery => ctx.CurrentRoute == "catalog-discovery";
    public bool CanUseDiscovery => !stopping && IsDiscovery && discoveryRequest is null &&
        discoverySnapshot is { } value && application.IsDiscoveryCurrent(value.Generation);
    public bool CanRotateDiscovery => CanUseDiscovery && commands == 0 && DiscoveryRecommended.Count > 0;
    public bool CanCancelDiscovery => !stopping && discoveryRequest is not null;
    public string DiscoveryStatus => discoveryStatus;
    internal bool HasActiveRequest => discoveryRequest is not null;

    internal void Unsubscribe() => application.DiscoveryInvalidated -= DiscoveryInvalidated;

    internal void CancelForShutdown()
    {
        discoveryEpoch++;
        discoveryRequest?.Cancel();
    }

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        DiscoveryBindings();
    }

    internal void NotifyRouteChanged()
    {
        Changed(nameof(IsDiscovery));
        DiscoveryBindings();
    }

    internal void DiscoveryBindings()
    {
        Changed(nameof(CanUseDiscovery));
        Changed(nameof(CanRotateDiscovery));
        Changed(nameof(CanCancelDiscovery));
        Changed(nameof(DiscoveryStatus));
    }

    private void DiscoveryInvalidated()
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
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

    internal Task LoadDiscoveryAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (stopping) return Task.CompletedTask;
        _ = settings.ActivateSearchHistoryAsync(SearchHistorySurface.Discovery);
        bool retainCompleted = refresh && discoverySnapshot is { } completed &&
            application.IsDiscoveryCurrent(completed.Generation);
        long epoch = ++discoveryEpoch, navigation = ctx.AdvanceLoadGeneration();
        ctx.LoadCancellation?.Cancel(); discoveryRequest?.Cancel();
        var previous = discoveryTail;
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        discoveryRequest = request; ctx.LoadCancellation = request;
        var previousSelection = ctx.CaptureContentSelection();
        ctx.SetLocation(new("catalog-discovery"), heading: "搜索发现", clearLibrary: true);
        OnClearCatalogSectionsAndTags();
        if (!retainCompleted) ClearDiscovery();
        discoveryStatus = "加载中"; ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
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
                if (stopping || epoch != discoveryEpoch || navigation != ctx.LoadGeneration || !IsDiscovery) return;
                if (!application.IsDiscoveryCurrent(result.Generation)) throw new MusicOperationException("STALE_SESSION");
                PublishDiscovery(result);
                var target = ctx.Locations.GetValueOrDefault("catalog-discovery");
                if (target is not null) _ = ctx.Dispatcher.EnqueueAsync(() =>
                { if (epoch == discoveryEpoch && navigation == ctx.LoadGeneration && IsDiscovery) ctx.RestoreScroll(target.Scroll); });
            }
            catch (OperationCanceledException)
            { if (epoch == discoveryEpoch && IsDiscovery) discoveryStatus = "已取消"; }
            catch (Exception error)
            {
                if (!stopping && epoch == discoveryEpoch && navigation == ctx.LoadGeneration && IsDiscovery)
                    discoveryStatus = error is MusicOperationException { Code: "CANCELLED" } ? "已取消" :
                        MusicCatalogDto.ErrorText(error is MusicOperationException e ? e.Code : "INVALID_RESPONSE");
            }
            finally
            {
                if (ReferenceEquals(discoveryRequest, request)) discoveryRequest = null;
                if (ReferenceEquals(ctx.LoadCancellation, request)) ctx.LoadCancellation = null;
                request.Dispose(); loadDrains.Remove(done.Task); done.TrySetResult();
                if (!disposed) ctx.RefreshBindings();
            }
        }
    }

    internal Task RetireDiscoveryAsync()
    {
        var previous = discoveryTail;
        discoveryEpoch++;
        discoveryRequest?.Cancel();
        ClearDiscovery(); DiscoveryBindings();
        return discoveryTail = Retire();
        async Task Retire()
        {
            await Task.Yield();
            // Retire only the page subscription. The service owns its in-flight
            // snapshot and the next page may join it without another request.
            await previous;
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
            ? settings.ActivateSearchHistoryAsync(SearchHistorySurface.Discovery)
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
        if (IsDiscovery) ctx.RememberLocation();
        return RetireDiscoveryPageAsync();
    }

    internal Task RetireDiscoveryPageAsync() => Task.WhenAll(
        RetireDiscoveryAsync(),
        settings.DeactivateSearchHistoryAsync(SearchHistorySurface.Discovery));

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
        await ctx.SearchAsync(word.Text, lifetime.Token);
        if (!stopping && GetIsSearchPage() && GetSearchKeyword() == word.Text)
            await RequestNavigationAsync("catalog-search");
    }

    private bool CurrentDiscoveryChart(MusicDiscoveryChart chart) =>
        CanUseDiscovery && DiscoveryCharts.Any(c => ReferenceEquals(c, chart));

    public Task OpenDiscoveryChartAsync(MusicDiscoveryChart chart)
    {
        if (!CurrentDiscoveryChart(chart) || commands > 0) return Task.CompletedTask;
        string artwork = chart.Cover.Length > 0
            ? chart.Cover
            : chart.Songs.Select(s => s.Song.Cover).FirstOrDefault(c => c.Length > 0) ?? "";
        return OnOpenCatalogTargetAsync(
            MusicCatalogController.Target("playlist", "wy", chart.Name, new { id = chart.Id }) with
            {
                Artwork = artwork,
                Subtitle = chart.Creator,
                Count = chart.Songs.Length > 0 ? chart.Songs.Length : null
            },
            chart.Id);
    }

    public Task UseDiscoverySongAsync(MusicDiscoverySong row, bool play)
    {
        if (!CanUseDiscovery || commands > 0 || row is null) return Task.CompletedTask;
        var chart = DiscoveryCharts.FirstOrDefault(c => c.Songs.Any(s => ReferenceEquals(s, row)));
        if (chart is null) return Task.CompletedTask;
        long epoch = discoveryEpoch;
        if (play)
        {
            playbackController.TrackMusicRecovery(playbackController.RetireMusicRecovery());
            OnInvalidateCatalogAction(); playbackController.CancelOnline();
            return RunPlaybackActionAsync(async generation =>
            {
                if (epoch != discoveryEpoch || !CurrentDiscoveryChart(chart)) return;
                object context = new { sourcePlatform = "wy", sourceType = "playlist", sourceId = chart.Id, sourceTitle = chart.Name };
                await playbackController.QueueCall("music.queue.replace", new { songs = chart.Songs.Select(s => s.Song.Raw).ToArray(),
                    index = Array.IndexOf(chart.Songs, row), context });
                await playbackController.ReadQueue(lifetime.Token);
                if (generation == ctx.PlaybackActionGeneration && epoch == discoveryEpoch && CurrentDiscoveryChart(chart))
                    await playbackController.StartCurrent(lifetime.Token, admission: () => generation == ctx.PlaybackActionGeneration);
            }, supersede: true);
        }
        return RunAsync(async () =>
        {
            if (epoch != discoveryEpoch || !CurrentDiscoveryChart(chart)) return;
            object context = new { sourcePlatform = "wy", sourceType = "playlist", sourceId = chart.Id, sourceTitle = chart.Name };
            await playbackController.QueueCall("music.queue.append", new { songs = new[] { row.Song.Raw }, next = true, context });
            await playbackController.ReadQueue(lifetime.Token);
        });
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, System.Text.Json.JsonElement?> read, long publication)
    {
        foreach (var row in DiscoveryCharts.SelectMany(chart => chart.Songs).Where(row => ids.Contains(row.Song.Id)))
            row.RefreshSong(read(row.Song.Id), publication);
    }
}
