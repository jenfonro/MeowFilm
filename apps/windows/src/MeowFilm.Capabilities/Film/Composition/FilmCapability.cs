using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Debug;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed partial class FilmCapability : IDesktopModule, IDesktopNavigationSource, IDesktopNavigationRetention, IDesktopGeneralSettingsSource,
    IDebugModuleAdapterSource, IRuntimeRecoveryScope
{
    internal const string HomeRoute = "film-home";
    internal const string DataRoute = "film-data";
    internal const string PlaybackRoute = "film-playback";
    internal const int DataPageSize = 24;

    private readonly DoubanFilmHomeSource source;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<FilmHomeSection, CancellationTokenSource> requests = [];
    private readonly HashSet<Task> operations = [];
    private readonly Dictionary<string, FilmPlaybackPage> playbackPages = new(StringComparer.Ordinal);
    private IReadOnlySet<string> retainedNavigationLocations = new HashSet<string>(StringComparer.Ordinal);
    private readonly FilmView view;
    private readonly FilmSettingsView settings;
    private readonly FilmDebugAdapter debugAdapter;
    private readonly FilmRuleService rules;
    private readonly FilmSourceService sources;
    private readonly FilmSearchService search;
    private readonly FilmMetadataService metadata;
    private readonly FilmPlaybackSession filmPlayback;
    private readonly FilmPlayback nativeFilmPlayback;
    private readonly FilmLibrary library;
    private readonly FilmPlaybackSettings business;
    private readonly OwnerDispatcher dispatcher = OwnerDispatcher.Current;
    private Task? favoriteRefresh;
    private bool favoriteDirty;
    private string favoriteTargetId = "";
    private Task? sourceStartup;
    private Task? preparation;
    private Task? abortion;
    private Task? restoringPresentation;
    private CancellationTokenSource? dataRequest;
    private FilmDataSection? dataRequestSection;
    private string route = HomeRoute;
    private long navigationGeneration;
    private bool stopping;
    private bool disposed;

    public FilmCapability(FilmRuleService rules, FilmSourceService sources, IRuntimeClient runtime,
        IStorageDatabase storage, FilmPlayback playback,
        Func<CancellationToken, Task<RuntimeConnection>> capture, FilmMetadataService metadata)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(metadata);

        source = new DoubanFilmHomeSource(runtime);
        this.rules = rules;
        this.sources = sources;
        this.metadata = metadata;
        search = new FilmSearchService(capture, sources);
        nativeFilmPlayback = playback;
        filmPlayback = new FilmPlaybackSession(playback,
            (page, parameters, reconnect, token) =>
                PreparePlaybackOpenAsync(sources, capture, page, parameters, reconnect, token));
        library = new FilmLibrary(storage);
        business = new FilmPlaybackSettings(runtime, storage);

        CacheSettingsView = new FilmMetadataCacheSettingsView(metadata);
        metadata.Saved += MetadataSaved;
        filmPlayback.OperationFailed += PlaybackFailed;
        filmPlayback.MediaStateChanged += PlaybackMediaChanged;
        library.Changed += FavoritesChanged;
        sources.Changed += PlaybackSourcesChanged;
        (runtime as IRuntimeRecovery)?.RegisterRecoveryScope(this);

        Sections = FilmSectionDefinition.Home.Select(definition => new FilmHomeSection(definition)).ToArray();
        DataSections = FilmSectionDefinition.Home.Select(definition => new FilmDataSection(definition)).ToArray();
        ActiveData = DataSections[0];
        Navigation =
        [
            new(HomeRoute, "影视", "\uE8B2") { IconData = "M5,5 L19,5 Q21,5 21,7 L21,18 Q21,20 19,20 L5,20 Q3,20 3,18 L3,7 Q3,5 5,5 M3,10 L21,10 M7,5 L10,10 M14,5 L17,10" },
            new(DataRoute, "影片数据", "\uE8B2", ShowInSidebar: false),
            new(PlaybackRoute, "播放页", "\uE8B2", ShowInSidebar: false)
        ];
        view = new(this);
        settings = new FilmSettingsView(rules, sources, storage, business, () => Playback?.ActiveTarget, metadata);
        debugAdapter = new(this);
    }

    public string Id => "film";
    public string Title => "影视";
    public string Version => "0.1.0";
    public IDebugModuleAdapter DebugAdapter => debugAdapter;
    internal FilmSettingsView DebugSettings => settings;
    internal string DebugRoute => route;
    internal FilmPlaybackSession DebugFilmPlayback => filmPlayback;
    internal FilmPlayback DebugNativeFilmPlayback => nativeFilmPlayback;
    internal async Task SwitchToDataSectionAsync(FilmDataSection selected, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(ActiveData, selected))
        {
            ActiveData = selected;
            Changed(nameof(ActiveData));
        }
        await NavigateAsync(DataRoute, cancellationToken);
    }
    public ObservableCollection<NavigationItem> Navigation { get; }
    public FrameworkElement View => view;
    public FrameworkElement SettingsView => settings;
    public FrameworkElement? CacheSettingsView { get; }
    public FrameworkElement? PlayerView => null;
    public IReadOnlyList<FilmHomeSection> Sections { get; }
    public IReadOnlyList<FilmDataSection> DataSections { get; }
    public FilmDataSection ActiveData { get; private set; }
    public FilmPlaybackPage? Playback { get; private set; }
    public FilmSearchService Search => search;
    private void MetadataSaved(object? sender, EventArgs e) => search.InvalidateMetadata();
    public bool HasPlayback => Playback is not null;
    public FilmPlaybackMediaState PlaybackMedia => new(filmPlayback.MediaIdentity, filmPlayback.MediaTitle,
        filmPlayback.HasOutput, filmPlayback.IsPlaying,
        filmPlayback.Position, filmPlayback.Duration);
    public bool IsHome => route == HomeRoute;
    public bool IsData => route == DataRoute;
    public bool IsPlayback => route == PlaybackRoute;
    public string Heading => route switch
    {
        HomeRoute => "影视",
        DataRoute => "影片数据",
        PlaybackRoute => "播放页",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested;
    public event EventHandler<string>? OperationFailed;
    internal event EventHandler? RuntimeRestored;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping) return Task.CompletedTask;
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.CompletedTask;
    }

    /// <summary>Called by App composition, never by view visibility. Failures stay
    /// observable on the source service; rule settings use the same Node owner.</summary>
    public Task StartSourcesAsync()
    {
        if (stopping || disposed) return Task.CompletedTask;
        return sourceStartup ??= StartCoreAsync();

        async Task StartCoreAsync()
        {
            try { await sources.StartAsync(lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Source owner publishes its fixed RuntimeError. */ }
        }
    }

    public Task NavigateAsync(string route, CancellationToken cancellationToken) =>
        NavigateCoreAsync(route, cancellationToken, restoreOnly: false);

    private async Task NavigateCoreAsync(string route, CancellationToken cancellationToken, bool restoreOnly)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping) return;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (route is not (HomeRoute or DataRoute or PlaybackRoute))
            throw new ArgumentException("Unknown film route.", nameof(route));

        long generation = ++navigationGeneration;
        RetireRequests();
        this.route = route;
        Changed(nameof(IsHome));
        Changed(nameof(IsData));
        Changed(nameof(IsPlayback));
        Changed(nameof(Heading));
        if (route == PlaybackRoute)
        {
            if (Playback is not null && (!filmPlayback.HasSession || !filmPlayback.IsCurrentPage(Playback)))
                await OpenPageSessionAsync(Playback, cancellationToken, restoreOnly);
            else await filmPlayback.ResumePresentationAsync();
            return;
        }
        await filmPlayback.LeaveAsync(retainedOwner: filmPlayback.CanRetainMedia);
        if (route == DataRoute)
        {
            if (ActiveData.CanSeedFromHome)
            {
                var home = Sections.Single(section => section.Key == ActiveData.Key);
                ActiveData.Seed(home.Items);
            }
            await StartDataLoad(ActiveData, generation, DataPageSize, cancellationToken, replace: true);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var targets = Sections.Where(section => !requests.ContainsKey(section)).ToArray();
        await Task.WhenAll(targets.Select(section =>
            StartSectionLoad(section, generation, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public string CaptureNavigationLocation() =>
        IsPlayback && Playback is { } page
            ? $"{PlaybackRoute}:{page.NavigationIdentity}"
            : route;

    public async Task RestoreNavigationAsync(string location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping) return;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (location.StartsWith(PlaybackRoute + ":", StringComparison.Ordinal))
        {
            string identity = location[(PlaybackRoute.Length + 1)..];
            if (!playbackPages.TryGetValue(identity, out var page))
                throw new ArgumentException("Unknown film playback location.", nameof(location));
            Playback = page;
            ReleaseUnreachablePages();
            Changed(nameof(Playback)); Changed(nameof(HasPlayback));
            await NavigateCoreAsync(PlaybackRoute, token, restoreOnly: true);
            return;
        }
        await NavigateAsync(location, token);
    }

    internal void SuspendPresentation(bool hiddenToTray = false)
    {
        navigationGeneration++;
        RetireRequests();
        // Ordinary covers retire presentation reads, not the active page's execution.
        // The explicit tray policy still drains preparations without accepted output.
        if (hiddenToTray && filmPlayback is { HasOutput: false } owner)
            ObservePresentationLeave(owner.LeaveAsync(owner.CanRetainMedia));
    }

    public Task LeaveNavigationAsync()
    {
        navigationGeneration++;
        RetireRequests();
        return filmPlayback.LeaveAsync(filmPlayback.CanRetainMedia);
    }

    internal void ResumePresentation()
    {
        if (stopping || disposed || dispatcher.IsClosing || !DesktopVisual.IsVisible(view) || !IsPlayback || Playback is null) return;
        if (filmPlayback.IsOpeningPage(Playback)) return;
        if (restoringPresentation is { IsCompleted: false }) return;
        restoringPresentation = dispatcher.EnqueueAsync(async () =>
        {
            if (stopping || disposed || !DesktopVisual.IsVisible(view) || !IsPlayback || Playback is null) return;
            if (filmPlayback.IsOpeningPage(Playback)) return;
            if (filmPlayback.HasSession && filmPlayback.IsCurrentPage(Playback))
                await filmPlayback.ResumePresentationAsync();
            else await OpenPageSessionAsync(Playback, lifetime.Token, restoreOnly: true);
        }, DispatcherQueuePriority.Low);
        ObservePresentationLeave(restoringPresentation, "播放资料恢复失败");
    }

    public Task PrepareRuntimeRecoveryAsync(CancellationToken cancellationToken) =>
        dispatcher.EnqueueAsync(async () =>
        {
            if (stopping || disposed) return;
            navigationGeneration++;
            RetireRequests();
            await Task.WhenAll(search.SuspendRuntimeAsync(), filmPlayback.CloseSessionAsync());
        }, DispatcherQueuePriority.Normal, cancellationToken);

    public void ResumeAfterRuntimeRecovery()
    {
        if (dispatcher.IsClosing) return;
        _ = dispatcher.EnqueueAsync(new Action(() =>
        {
            if (stopping || disposed) return;
            ObservePresentationLeave(RestoreVisibleAsync(), "影片资料恢复失败");
        }));
    }

    private async Task RestoreVisibleAsync()
    {
        await sources.StartAsync(lifetime.Token);
        if (stopping || disposed) return;
        RuntimeRestored?.Invoke(this, EventArgs.Empty);
        if (!DesktopVisual.IsVisible(view)) return;
        if (IsPlayback) ResumePresentation();
        else if (IsHome)
            await Task.WhenAll(Sections.Where(section => section.State is not FilmSectionState.Ready)
                .Select(section => StartSectionLoad(section, navigationGeneration, lifetime.Token)));
        else if (IsData && ActiveData.State is not FilmSectionState.Ready)
            await StartDataLoad(ActiveData, navigationGeneration, NextDataLimit(ActiveData), lifetime.Token);
    }

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        Search.Start(query, cancellationToken);
        return Task.CompletedTask;
    }

    public string OperationErrorText(Exception error) =>
        error is ArgumentException ? "无法打开影片页面" : "影片内容加载失败";

    internal Task OpenDataAsync(FilmHomeSection? section = null)
    {
        if (stopping || disposed || !IsHome) return Task.CompletedTask;
        section ??= Sections[0];
        if (!Sections.Contains(section)) throw new ArgumentException("Unknown film section.", nameof(section));
        var selected = DataSections.Single(data => data.Key == section.Key);
        if (!ReferenceEquals(ActiveData, selected))
        {
            ActiveData = selected;
            Changed(nameof(ActiveData));
        }
        if (NavigationRequested is not { } requested)
            return NavigateAsync(DataRoute, lifetime.Token);
        requested(this, new(DataRoute));
        return Task.CompletedTask;
    }

    internal async Task OpenPlaybackAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || (!IsHome && !IsData)) return;
        if (!await SelectPlaybackAsync(item, lifetime.Token, IsHome ? "home" : "search")) return;
        if (NavigationRequested is not { } requested)
        {
            await NavigateAsync(PlaybackRoute, lifetime.Token);
            return;
        }
        requested(this, new(PlaybackRoute));
    }

    public async Task ReceiveSearchPlaybackAsync(FilmCardItem item, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed) throw new InvalidOperationException("CLOSED");
        var request = item.PlaybackContext ?? throw new ArgumentException("Missing film business identity.", nameof(item));
        var context = item.SearchContext;
        long ticket = ++navigationGeneration;
        var page = context is null ? null : playbackPages.Values.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.SearchContext, context));
        if (context is not null) request = await context.HandoffAsync(token);
        token.ThrowIfCancellationRequested();
        if (ticket != navigationGeneration) throw new OperationCanceledException(token);
        if (stopping || disposed) throw new FilmPlaybackException("CLOSED");
        if (page is null)
        {
            page = new FilmPlaybackPage(item with { PlaybackContext = request }, request.Own(), filmPlayback, library, business);
            playbackPages.Add(page.NavigationIdentity, page);
        }
        else page.UpdateItem(item with { PlaybackContext = request });
        Playback = page;
        ReleaseUnreachablePages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
    }

    public void RetainNavigationLocations(IReadOnlySet<string> locations)
    {
        retainedNavigationLocations = locations;
        ReleaseUnreachablePages();
    }

    private void ReleaseUnreachablePages()
    {
        foreach (var entry in playbackPages.Where(entry => !ReferenceEquals(entry.Value, Playback) &&
            !retainedNavigationLocations.Contains($"{PlaybackRoute}:{entry.Key}")).ToArray())
        {
            entry.Value.Release();
            playbackPages.Remove(entry.Key);
        }
    }

    internal async Task OpenRecommendationAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || !IsPlayback || Playback is null) return;
        if (!await SelectPlaybackAsync(item, lifetime.Token, Playback.EntrySurface)) return;
        if (NavigationRequested is not { } requested)
        {
            await NavigateAsync(PlaybackRoute, lifetime.Token);
            return;
        }
        requested(this, new(PlaybackRoute));
    }

    public async Task OpenPlaybackAsync(FilmCardItem item, FilmPlaybackRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        request = request with { EntrySurface = IsHome ? "home" : "search" };
        if (!await SelectPlaybackAsync(item with { PlaybackContext = request }, token)) return;
        if (NavigationRequested is { } requested) requested(this, new(PlaybackRoute));
        else await NavigateAsync(PlaybackRoute, token);
    }

    private async Task<bool> SelectPlaybackAsync(FilmCardItem item, CancellationToken token, string? entrySurface = null)
    {
        if (item.PlaybackContext is not { } request)
            throw new FilmPlaybackException("CAPABILITY_UNAVAILABLE");
        long ticket = ++navigationGeneration;
        request = (request with { EntrySurface = entrySurface ?? request.EntrySurface ?? "search" }).Own();
        var selected = new FilmPlaybackPage(item with { PlaybackContext = request }, request, filmPlayback, library, business);
        playbackPages.Add(selected.NavigationIdentity, selected);
        Playback = selected;
        ReleaseUnreachablePages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
        await OpenPageSessionAsync(selected, token);
        return ticket == navigationGeneration && ReferenceEquals(Playback, selected) && !stopping && !disposed;
    }

    private async Task OpenPageSessionAsync(FilmPlaybackPage page, CancellationToken token, bool restoreOnly = false)
    {
        long ticket = navigationGeneration;
        if (!ReferenceEquals(Playback, page) || stopping || disposed) return;
        var request = page.CaptureRequest;
        await filmPlayback.OpenAsync(page, new
        {
            platform = "windows", entrySurface = request.EntrySurface, restoreOnly = restoreOnly || page.HasPlaybackSnapshot,
            target = request.Target.Wire(), resumeTarget = request.ResumeTarget?.Wire(), sources = request.Sources,
            query = request.Query, results = request.Results, search = request.Search, metadata = request.Metadata,
            cacheEntries = request.CacheEntries, resultHints = request.ResultHints, searchReference = request.SearchReference,
            browsing = request.Browsing
        }, token);
        if (ticket == navigationGeneration && ReferenceEquals(Playback, page) && !stopping && !disposed)
            await page.LoadFavoriteAsync();
    }

    internal static async Task<FilmPlaybackOpen> PreparePlaybackOpenAsync(FilmSourceService sources,
        Func<CancellationToken, Task<RuntimeConnection>> capture, FilmPlaybackPage page, JsonElement parameters,
        bool reconnect, CancellationToken token)
    {
        // Only an explicit retry may recover a failed App epoch. Ordinary capture,
        // navigation and an event-stream reconnect never restart it.
        if (reconnect) await sources.RefreshAsync(token);
        var borrowed = await capture(token);
        borrowed.Revoked.ThrowIfCancellationRequested();
        await sources.StartAsync(token);
        var verified = await capture(token);
        if (!FilmPlaybackOpen.SameOwner(borrowed, verified)) throw new FilmPlaybackException("STALE_SESSION");
        return new(borrowed, FilmPlaybackOpen.ParametersFor(page, parameters, borrowed, PlaybackSources(sources)));
    }

    private IReadOnlyList<JsonElement> PlaybackSources() => PlaybackSources(sources);

    private static IReadOnlyList<JsonElement> PlaybackSources(FilmSourceService sources)
    {
        var (cats, videos) = sources.ReadSnapshot();
        return videos.Where(video => video.Options.Enabled)
            .Select(video => new { Video = video, Cat = cats.FirstOrDefault(cat => cat.InstallationId == video.Key.InstallationId) })
            .Where(row => row.Cat is { Enabled: true, State: "ready", Generation: > 0 })
            .Select(row => FilmJson.Encode(new
            {
                key = JsonSerializer.Serialize(new[] { row.Video.Key.InstallationId, row.Video.Key.SiteKey }),
                engine = "cat", installationId = row.Video.Key.InstallationId, siteId = row.Video.Key.SiteKey,
                generation = row.Cat!.Generation, name = row.Video.Name, enabled = row.Video.Options.Enabled,
                searchable = row.Video.Options.SearchEnabled
            })).ToArray();
    }

    private void PlaybackSourcesChanged(object? sender, EventArgs args)
    {
        if (dispatcher.IsClosing) return;
        if (dispatcher.HasThreadAccess) Apply();
        else _ = dispatcher.EnqueueAsync(Apply);
        void Apply()
        {
            if (!stopping && !disposed) filmPlayback.SourcesChanged(PlaybackSources());
        }
    }
    private void PlaybackFailed(object? sender, string error) =>
        OperationFailed?.Invoke(this, FilmPlaybackError.Message(error, "播放操作失败"));
    private void PlaybackMediaChanged(object? sender, EventArgs args)
    {
        Changed(nameof(PlaybackMedia));
        string identity = Playback?.EntryTarget.Id ?? "";
        if (identity != favoriteTargetId)
        {
            favoriteTargetId = identity;
            FavoritesChanged(sender, args);
        }
    }
    private void FavoritesChanged(object? sender, EventArgs args)
    {
        if (dispatcher.IsClosing) return;
        _ = dispatcher.EnqueueAsync(new Action(() =>
        {
            if (stopping || disposed) return;
            favoriteDirty = true;
            if (favoriteRefresh is { IsCompleted: false }) return;
            favoriteRefresh = RefreshFavoritesAsync();
            ObservePresentationLeave(favoriteRefresh, "收藏读取失败");
        }));
    }
    private async Task RefreshFavoritesAsync()
    {
        while (favoriteDirty && !stopping && !disposed)
        {
            favoriteDirty = false;
            var items = await library.ReadAsync(lifetime.Token);
            if (!stopping && !disposed) Playback?.RefreshFavorite(items);
        }
    }
    private void ObservePresentationLeave(Task operation, string failure = "停止播放失败")
    {
        // The notification owns the business error; shutdown owns completion of
        // that observation, not the original operation's success.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Add(settled.Task);
        _ = SettleAsync();
        async Task SettleAsync()
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!stopping && !disposed) OperationFailed?.Invoke(this, failure);
            }
            finally
            {
                operations.Remove(settled.Task);
                settled.TrySetResult();
            }
        }
    }

    public Task StepAsync(bool next) => filmPlayback.CommandAsync(next ? "next" : "previous");
    public Task ResumePlaybackAsync() => filmPlayback.HasOutput ? filmPlayback.SetPausedAsync(false) : Task.CompletedTask;
    public Task PausePlaybackAsync() => filmPlayback.HasOutput ? filmPlayback.SetPausedAsync(true) : Task.CompletedTask;
    public Task StopPlaybackAsync() => filmPlayback.CommandAsync("stop");
    public Task SeekPlaybackAsync(double seconds) => filmPlayback.SeekAsync(seconds);
    internal async Task RetrySectionAsync(FilmHomeSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (stopping || disposed || !IsHome || !Sections.Contains(section) ||
            requests.ContainsKey(section) || !section.CanRetry)
            return;
        await StartSectionLoad(section, navigationGeneration, lifetime.Token, refresh: true);
    }

    internal Task RefreshSectionAsync(FilmHomeSection section) =>
        !stopping && !disposed && IsHome && Sections.Contains(section) && !requests.ContainsKey(section)
            ? StartSectionLoad(section, navigationGeneration, lifetime.Token, refresh: true)
            : Task.CompletedTask;

    internal Task RefreshDataAsync() =>
        !stopping && !disposed && IsData && dataRequest is null
            ? StartDataLoad(ActiveData, navigationGeneration, DataPageSize, lifetime.Token, replace: true, refresh: true)
            : Task.CompletedTask;

    internal async Task RetryDataAsync()
    {
        var section = ActiveData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanRetry)
            return;
        await StartDataLoad(section, navigationGeneration, NextDataLimit(section), lifetime.Token,
            replace: section.ReloadPending, refresh: true);
    }

    internal async Task LoadMoreDataAsync()
    {
        var section = ActiveData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanPage)
            return;
        await StartDataLoad(section, navigationGeneration, NextDataLimit(section), lifetime.Token,
            replace: section.ReloadPending,
            refresh: section.State is FilmSectionState.Failed or FilmSectionState.Cancelled);
    }

    internal async Task SelectDataFilterAsync(FilmFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var section = ActiveData;
        if (stopping || disposed || !IsData) return;
        if (!section.Contains(option))
            throw new ArgumentException("Unknown film filter option.", nameof(option));
        if (option.IsSelected) return;

        long generation = ++navigationGeneration;
        RetireDataRequest();
        if (!section.SelectFilter(option)) return;
        await StartDataLoad(section, generation, DataPageSize, lifetime.Token);
    }

    private Task StartSectionLoad(
        FilmHomeSection section,
        long generation,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        requests.Add(section, request);
        section.Begin();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        operations.Add(operation);
        _ = RunAsync();
        return operation;

        async Task RunAsync()
        {
            try
            {
                await Task.Yield();
                request.Token.ThrowIfCancellationRequested();
                var items = await source.LoadAsync(section.Definition with { Refresh = refresh }, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (CanPublish(section, request, generation))
                {
                    section.Complete(items);
                    NotifyCacheError(section.Cache);
                }
            }
            catch (OperationCanceledException)
            {
                if (CanPublish(section, request, generation)) section.Cancel();
            }
            catch (Exception error)
            {
                if (CanPublish(section, request, generation))
                {
                    section.Fail();
                    OperationFailed?.Invoke(this, error is FilmHomeException home
                        ? FilmPlaybackError.Message(home.Code, "栏目加载失败")
                        : "栏目加载失败");
                }
            }
            finally
            {
                if (requests.TryGetValue(section, out var current) && ReferenceEquals(current, request))
                    requests.Remove(section);
                operations.Remove(operation);
                request.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private bool CanPublish(
        FilmHomeSection section,
        CancellationTokenSource request,
        long generation) =>
        !stopping && !disposed && IsHome && generation == navigationGeneration &&
        requests.TryGetValue(section, out var current) && ReferenceEquals(current, request);

    private Task StartDataLoad(
        FilmDataSection section,
        long generation,
        int limit,
        CancellationToken cancellationToken,
        bool replace = false,
        bool refresh = false)
    {
        if (limit < 1 || limit > DataPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        int start = replace ? 0 : section.NextStart;
        var definition = section.Definition with { Refresh = refresh };
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        dataRequest = request;
        dataRequestSection = section;
        section.Begin(replace);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = completion.Task;
        operations.Add(operation);
        _ = RunAsync();
        return operation;

        async Task RunAsync()
        {
            try
            {
                await Task.Yield();
                request.Token.ThrowIfCancellationRequested();
                if (replace)
                {
                    var page = await source.LoadAsync(definition, 0, DataPageSize, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    var cache = (page as FilmHomePage)?.Cache;
                    if (CanPublishData(section, request, generation))
                    {
                        section.Replace(page, page.Count, page.Count >= DataPageSize, cache);
                        NotifyCacheError(cache);
                    }
                }
                else
                {
                    var items = await source.LoadAsync(definition, start, limit, request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    if (CanPublishData(section, request, generation))
                    {
                        section.Complete(items, start, limit);
                        NotifyCacheError(section.Cache);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (CanPublishData(section, request, generation)) section.Cancel();
            }
            catch (Exception error)
            {
                if (CanPublishData(section, request, generation))
                {
                    section.Fail();
                    OperationFailed?.Invoke(this, error is FilmHomeException home
                        ? FilmPlaybackError.Message(home.Code, "影片数据加载失败")
                        : "影片数据加载失败");
                }
            }
            finally
            {
                if (ReferenceEquals(dataRequest, request))
                {
                    dataRequest = null;
                    dataRequestSection = null;
                }
                operations.Remove(operation);
                request.Dispose();
                completion.TrySetResult();
            }
        }
    }

    private void NotifyCacheError(FilmMetadataCache? cache)
    {
        if (cache?.ErrorCode is { Length: > 0 } code)
            OperationFailed?.Invoke(this, FilmPlaybackError.Message(code, "影片数据刷新失败"));
    }

    private bool CanPublishData(
        FilmDataSection section,
        CancellationTokenSource request,
        long generation) =>
        !stopping && !disposed && IsData && ReferenceEquals(ActiveData, section) &&
        generation == navigationGeneration && ReferenceEquals(dataRequest, request) &&
        ReferenceEquals(dataRequestSection, section);

    private static int NextDataLimit(FilmDataSection section) =>
        section.NextStart < DataPageSize ? DataPageSize - section.NextStart : DataPageSize;

    private void RetireRequests()
    {
        var retired = requests.Values.ToArray();
        foreach (var section in requests.Keys) section.Cancel();
        requests.Clear();
        foreach (var request in retired)
        {
            try { request.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "requests", "section.cancel.failed", error: error);
            }
        }
        RetireDataRequest();
    }

    private void RetireDataRequest()
    {
        var retiredData = dataRequest;
        if (retiredData is not null) dataRequestSection?.Cancel();
        dataRequest = null;
        dataRequestSection = null;
        if (retiredData is not null)
        {
            try { retiredData.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.FilmCatalog, "requests", "data.cancel.failed", error: error);
            }
        }
    }

    private async Task DrainOperationsAsync()
    {
        while (operations.Count > 0)
            await Task.WhenAll(operations.ToArray());
    }

    public Task PrepareShutdownAsync()
    {
        if (preparation is not null) return preparation;
        stopping = true;
        navigationGeneration++;
        RetireRequests();
        return preparation = PrepareCoreAsync();
    }

    private async Task PrepareCoreAsync()
    {
        await Task.WhenAll(
            search.PrepareShutdownAsync(),
            metadata.PrepareShutdownAsync(),
            rules.PrepareShutdownAsync(),
            sources.PrepareShutdownAsync(),
            filmPlayback.CloseSessionAsync(),
            settings.PreparePlaybackSettingsShutdownAsync(),
            DrainOperationsAsync());
        if (sourceStartup is not null) await sourceStartup;
    }

    public Task AbortAsync()
    {
        if (abortion is { IsFaulted: false, IsCanceled: false }) return abortion;
        return abortion = AbortCoreAsync();
    }

    private async Task AbortCoreAsync()
    {
        stopping = true;
        navigationGeneration++;
        lifetime.Cancel();
        RetireRequests();
        await DrainOperationsAsync();
        await ReleaseSettingsAsync();
        Release();
    }

    public ValueTask DisposeAsync() => new(AbortAsync());

    private async Task ReleaseSettingsAsync()
    {
        // Settings writes retain their receipt, but cannot notify a released search.
        metadata.Saved -= MetadataSaved;
        await settings.PreparePlaybackSettingsShutdownAsync();
        await Task.WhenAll(search.DisposeAsync().AsTask(),
            metadata.DisposeAsync().AsTask(),
            rules.DisposeAsync().AsTask(),
            sources.DisposeAsync().AsTask(),
            filmPlayback.DisposeAsync().AsTask(),
            library.DisposeAsync().AsTask());
        if (sourceStartup is not null) await sourceStartup;
    }

    private void Release()
    {
        if (disposed) return;
        foreach (var page in playbackPages.Values) page.Release();
        playbackPages.Clear();
        sources.Changed -= PlaybackSourcesChanged;
        filmPlayback.OperationFailed -= PlaybackFailed;
        filmPlayback.MediaStateChanged -= PlaybackMediaChanged;
        library.Changed -= FavoritesChanged;
        disposed = true;
        lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
