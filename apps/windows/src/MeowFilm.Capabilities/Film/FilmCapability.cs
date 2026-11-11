using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

public sealed class FilmCapability : IDesktopModule, IDesktopNavigationSource, IDesktopNavigationRetention
{
    internal const string HomeRoute = "film-home";
    internal const string DataRoute = "film-data";
    internal const string PlaybackRoute = "film-playback";
    internal const int DataPageSize = 24;

    private readonly IFilmHomeSource source;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<FilmHomeSection, CancellationTokenSource> requests = [];
    private readonly HashSet<Task> operations = [];
    private readonly Dictionary<string, FilmPlaybackPage> playbackPages = new(StringComparer.Ordinal);
    private IReadOnlySet<string> retainedNavigationLocations = new HashSet<string>(StringComparer.Ordinal);
    private readonly FilmView view;
    private readonly FrameworkElement settings;
    private readonly FilmRuleService? rules;
    private readonly FilmSourceService? sources;
    private readonly FilmSearchService? search;
    private readonly FilmMetadataService? metadata;
    private readonly FilmPlaybackSession? filmPlayback;
    private readonly FilmLibrary? library;
    private readonly System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private Task? favoriteRefresh;
    private bool favoriteDirty;
    private string favoriteTargetId = "";
    private Task? sourceStartup;
    private Task? preparation;
    private Task? disposal;
    private Task? abortion;
    private CancellationTokenSource? dataRequest;
    private FilmDataSection? dataRequestSection;
    private string route = HomeRoute;
    private long navigationGeneration;
    private bool stopping;
    private bool disposed;

    public FilmCapability(FilmRuleService rules, FilmSourceService sources, IRuntimeClient runtime, FilmMetadataService? metadata = null)
        : this(new DoubanFilmHomeSource(runtime), rules, sources,
            new FilmSearchService((runtime as IRuntimeConnectionSource ?? throw new InvalidOperationException("RUNTIME_CAPTURE_UNAVAILABLE"))
                .CaptureConnectionAsync, sources, metadata is null ? null : metadata.ResolveImage),
            metadata)
    {
    }

    internal FilmCapability(IFilmHomeSource source) : this(source, rules: null, sources: null) { }

    internal FilmCapability(IFilmHomeSource source, FilmSearchService search, FilmMetadataService? metadata = null)
        : this(source, null, null, search: search, metadata: metadata) { }

    internal FilmCapability(IFilmHomeSource source, FilmSearchService search, FilmPlaybackSession playback,
        FilmLibrary library, FilmMetadataService? metadata = null)
        : this(source, null, null, search, metadata, playback, library) { }

    public FilmCapability(FilmRuleService rules, FilmSourceService sources, IRuntimeClient runtime,
        IDocumentStore storage, NativePlayback playback,
        Func<CancellationToken, Task<RuntimeConnection>> capture, FilmMetadataService metadata)
        : this(new DoubanFilmHomeSource(runtime), rules, sources,
            new FilmSearchService(capture, sources, metadata.ResolveImage),
            metadata, new FilmPlaybackSession(capture, new FilmNativeMedia(playback),
                prepareOpen: (page, parameters, reconnect, token) =>
                    PreparePlaybackOpenAsync(sources, capture, page, parameters, reconnect, token)),
            new FilmLibrary(storage), new FilmPlaybackSettings(runtime)) { }

    internal FilmCapability(IFilmHomeSource source, FilmPlaybackSession playback, FilmLibrary library)
        : this(source, null, null, playback: playback, library: library) { }

    private FilmCapability(IFilmHomeSource source, FilmRuleService? rules, FilmSourceService? sources,
        FilmSearchService? search = null, FilmMetadataService? metadata = null,
        FilmPlaybackSession? playback = null, FilmLibrary? library = null, FilmPlaybackSettings? business = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.rules = rules;
        this.sources = sources;
        this.search = search;
        this.metadata = metadata;
        if (metadata is not null) metadata.Saved += MetadataSaved;
        filmPlayback = playback;
        this.library = library;
        if (playback is not null)
        {
            playback.OperationFailed += PlaybackFailed;
            playback.MediaStateChanged += PlaybackMediaChanged;
        }
        if (library is not null) library.Changed += FavoritesChanged;
        if (sources is not null) sources.Changed += PlaybackSourcesChanged;
        Sections = FilmSectionDefinition.Home.Select(definition => new FilmHomeSection(definition)).ToArray();
        DataSections = FilmSectionDefinition.Home.Select(definition => new FilmDataSection(definition)).ToArray();
        ActiveData = DataSections[0];
        Navigation =
        [
            new(HomeRoute, "影视", "\uE8B2"),
            new(DataRoute, "影片数据", "\uE8B2", ShowInSidebar: false),
            new(PlaybackRoute, "播放页", "\uE8B2", ShowInSidebar: false)
        ];
        view = new(this);
        settings = rules is not null && sources is not null
            ? new FilmSettingsView(rules, sources, business, library, () => Playback?.ActiveTarget, metadata)
            : new Grid { Background = Brushes.Transparent }; // Home-only test composition.
    }

    public string Id => "film";
    public string Title => "影视";
    public string Version => "0.1.0";
    public ObservableCollection<NavigationItem> Navigation { get; }
    public FrameworkElement View => view;
    public FrameworkElement SettingsView => settings;
    public FrameworkElement? PlayerView => null;
    public IReadOnlyList<FilmHomeSection> Sections { get; }
    public IReadOnlyList<FilmDataSection> DataSections { get; }
    public FilmDataSection ActiveData { get; private set; }
    public FilmPlaybackPage? Playback { get; private set; }
    internal int RetainedSearchPlaybackPages => playbackPages.Values.Count(page => page.SearchContext is not null);
    public FilmSearchService Search => search ?? throw new InvalidOperationException("Film search is not composed.");
    private void MetadataSaved(object? sender, EventArgs e) => search?.InvalidateMetadata();
    public bool HasPlayback => Playback is not null;
    public FilmPlaybackMediaState PlaybackMedia => new(filmPlayback?.MediaIdentity, filmPlayback?.MediaTitle ?? "",
        filmPlayback?.HasOutput == true, filmPlayback?.IsPlaying == true,
        filmPlayback?.Position ?? 0, filmPlayback?.Duration ?? 0);
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
        if (stopping || disposed || sources is null) return Task.CompletedTask;
        return sourceStartup ??= StartCoreAsync();

        async Task StartCoreAsync()
        {
            try { await sources.StartAsync(lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Source owner publishes its fixed RuntimeError. */ }
        }
    }

    public async Task NavigateAsync(string route, CancellationToken cancellationToken)
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
            if (Playback is not null && filmPlayback is not null && (!filmPlayback.HasSession || !filmPlayback.IsCurrentPage(Playback)))
                await OpenPageSessionAsync(Playback, cancellationToken);
            else if (filmPlayback is not null) await filmPlayback.ResumePresentationAsync();
            return;
        }
        if (filmPlayback is not null) await filmPlayback.LeaveAsync(retainedOwner: filmPlayback.CanRetainMedia);
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
            ? page.SearchContext is not null ? $"{PlaybackRoute}:{page.NavigationIdentity}"
                : $"{PlaybackRoute}:{JsonSerializer.Serialize(page.Item with { PlaybackContext = page.CaptureRequest }, FilmJson.Options)}"
            : route;

    public async Task RestoreNavigationAsync(string location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping) return;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (location.StartsWith(PlaybackRoute + ":", StringComparison.Ordinal))
        {
            string identity = location[(PlaybackRoute.Length + 1)..];
            if (identity.StartsWith("search:", StringComparison.Ordinal))
            {
                if (!playbackPages.TryGetValue(identity, out var page))
                    throw new ArgumentException("Unknown film playback location.", nameof(location));
                Playback = page;
                ReleaseUnreachableSearchPages();
                Changed(nameof(Playback)); Changed(nameof(HasPlayback));
                await NavigateAsync(PlaybackRoute, token);
                return;
            }
            FilmCardItem item;
            try { item = JsonSerializer.Deserialize<FilmCardItem>(identity, FilmJson.Options)!; }
            catch (JsonException) { throw new ArgumentException("Unknown film playback location.", nameof(location)); }
            if (!await SelectPlaybackAsync(item, token)) return;
            await NavigateAsync(PlaybackRoute, token);
            return;
        }
        await NavigateAsync(location, token);
    }

    internal void SuspendPresentation(bool hiddenToTray = false)
    {
        navigationGeneration++;
        RetireRequests();
        // A tray hide is not a page exit. Only an already live output owns playback here.
        if (hiddenToTray && filmPlayback?.HasOutput == true) return;
        if (filmPlayback is not null) ObservePresentationLeave(filmPlayback.LeaveAsync(filmPlayback.CanRetainMedia));
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
        bool visible = IsHome
            ? Sections.Any(section => section.Items.Contains(item))
            : ActiveData.Items.Contains(item);
        if (!visible) throw new ArgumentException("Unknown film item.", nameof(item));
        if (!await SelectPlaybackAsync(item, lifetime.Token)) return;
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
        var context = item.SearchContext ?? throw new ArgumentException("Missing search context.", nameof(item));
        if (!ReferenceEquals(search?.Current, context.Session))
            throw new ArgumentException("Retired search session.", nameof(item));
        long ticket = ++navigationGeneration;
        var page = playbackPages.Values.FirstOrDefault(candidate =>
            candidate.SearchContext is { } selected && selected.SameSelection(context));
        if (page is null)
        {
            var request = await context.HandoffAsync(token);
            token.ThrowIfCancellationRequested();
            if (ticket != navigationGeneration) throw new OperationCanceledException(token);
            if (stopping || disposed || !ReferenceEquals(search?.Current, context.Session))
                throw new FilmPlaybackException("STALE_SESSION");
            page = FilmPlaybackPage.FromSearch(item, request, filmPlayback, library);
            playbackPages.Add(page.NavigationIdentity, page);
        }
        else page.UpdateItem(item);
        Playback = page;
        ReleaseUnreachableSearchPages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
    }

    public void RetainNavigationLocations(IReadOnlySet<string> locations)
    {
        retainedNavigationLocations = locations;
        ReleaseUnreachableSearchPages();
    }

    private void ReleaseUnreachableSearchPages()
    {
        foreach (var entry in playbackPages.Where(entry => entry.Value.SearchContext is not null &&
            !ReferenceEquals(entry.Value, Playback) &&
            !retainedNavigationLocations.Contains($"{PlaybackRoute}:{entry.Key}")).ToArray())
        {
            entry.Value.ReleaseSearch();
            playbackPages.Remove(entry.Key);
        }
    }

    internal async Task OpenRecommendationAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || !IsPlayback || Playback is null) return;
        if (!Playback.Recommendations.Contains(item))
            throw new ArgumentException("Unknown film recommendation.", nameof(item));
        if (!await SelectPlaybackAsync(item, lifetime.Token)) return;
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
        if (!await SelectPlaybackAsync(item with { PlaybackContext = request }, token)) return;
        if (NavigationRequested is { } requested) requested(this, new(PlaybackRoute));
        else await NavigateAsync(PlaybackRoute, token);
    }

    private async Task<bool> SelectPlaybackAsync(FilmCardItem item, CancellationToken token)
    {
        if (filmPlayback is null || item.PlaybackContext is not { } request)
            throw new FilmPlaybackException("CAPABILITY_UNAVAILABLE");
        long ticket = ++navigationGeneration;
        request = request.Own();
        var selected = new FilmPlaybackPage(item with { PlaybackContext = request }, request, filmPlayback, library);
        Playback = selected;
        ReleaseUnreachableSearchPages();
        Changed(nameof(Playback)); Changed(nameof(HasPlayback));
        await OpenPageSessionAsync(selected, token);
        return ticket == navigationGeneration && ReferenceEquals(Playback, selected) && !stopping && !disposed;
    }

    private async Task OpenPageSessionAsync(FilmPlaybackPage page, CancellationToken token)
    {
        if (filmPlayback is null) throw new FilmPlaybackException("CAPABILITY_UNAVAILABLE");
        long ticket = navigationGeneration;
        if (ticket != navigationGeneration || !ReferenceEquals(Playback, page) || stopping || disposed) return;
        var request = page.CaptureRequest;
        await filmPlayback.OpenAsync(page, new
        {
            platform = "windows", target = request.Target.Wire(), sources = request.Sources,
            query = request.Query, results = request.Results, search = request.Search, metadata = request.Metadata,
            cacheEntries = request.CacheEntries, resultHints = request.ResultHints, searchReference = request.SearchReference
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

    private IReadOnlyList<JsonElement> PlaybackSources() => sources is null ? [] : PlaybackSources(sources);

    private static IReadOnlyList<JsonElement> PlaybackSources(FilmSourceService sources)
    {
        return sources.Videos.Where(video => video.Options.Enabled)
            .Select(video => new { Video = video, Cat = sources.Cats.FirstOrDefault(cat => cat.InstallationId == video.Key.InstallationId) })
            .Where(row => row.Cat is { Enabled: true, State: "ready", Generation: > 0 })
            .Select(row => FilmJson.Encode(new
            {
                key = JsonSerializer.Serialize(new[] { row.Video.Key.InstallationId, row.Video.Key.SiteKey }),
                engine = "cat", installationId = row.Video.Key.InstallationId, siteId = row.Video.Key.SiteKey,
                generation = row.Cat!.Generation, name = row.Video.Name, enabled = row.Video.Options.Enabled,
                searchable = row.Video.Options.SearchEnabled
            })).ToArray();
    }

    private void PlaybackSourcesChanged(object? sender, EventArgs args) => filmPlayback?.SourcesChanged(PlaybackSources());
    private void PlaybackFailed(object? sender, string error) =>
        OperationFailed?.Invoke(this, FilmPlaybackError.Message(error, "播放操作失败"));
    private void PlaybackMediaChanged(object? sender, EventArgs args)
    {
        Changed(nameof(PlaybackMedia));
        string identity = Playback?.ActiveTarget.Id ?? "";
        if (identity != favoriteTargetId)
        {
            favoriteTargetId = identity;
            FavoritesChanged(sender, args);
        }
    }
    private void FavoritesChanged(object? sender, EventArgs args)
    {
        if (dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (stopping || disposed || library is null) return;
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
            var items = await library!.ReadAsync();
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
            catch (Exception) { OperationFailed?.Invoke(this, failure); }
            finally
            {
                operations.Remove(settled.Task);
                settled.TrySetResult();
            }
        }
    }

    public Task StepAsync(bool next) => filmPlayback?.CommandAsync(next ? "next" : "previous") ?? Task.CompletedTask;
    public Task ResumePlaybackAsync() => filmPlayback is { HasOutput: true } owner
        ? owner.SetPausedAsync(false) : filmPlayback?.CommandAsync("play") ?? Task.CompletedTask;
    public Task PausePlaybackAsync() => filmPlayback is { HasOutput: true } owner ? owner.SetPausedAsync(true) : Task.CompletedTask;
    public Task StopPlaybackAsync() => filmPlayback?.CommandAsync("stop") ?? Task.CompletedTask;
    public Task SeekPlaybackAsync(double seconds) => filmPlayback?.SeekAsync(seconds) ?? Task.CompletedTask;
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
            refresh: section.State is FilmDataSectionState.Failed or FilmDataSectionState.Cancelled);
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
            catch
            {
                if (CanPublish(section, request, generation))
                {
                    section.Fail();
                    OperationFailed?.Invoke(this, "栏目加载失败");
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
        int reloadCount = Math.Max(section.NextStart, DataPageSize);
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
                    var items = new List<FilmCardItem>();
                    FilmMetadataCache? cache = null;
                    int next = 0;
                    bool more;
                    do
                    {
                        var page = await source.LoadAsync(definition, next, DataPageSize, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        items.AddRange(page);
                        next = checked(next + page.Count);
                        more = page.Count >= DataPageSize;
                        if ((page as FilmHomePage)?.Cache is { } state &&
                            (cache is null || cache.ErrorCode.Length == 0 &&
                                (state.ErrorCode.Length > 0 || cache.Status != "stale"))) cache = state;
                    } while (more && next < reloadCount);
                    if (CanPublishData(section, request, generation))
                    {
                        section.Replace(items, next, more, cache);
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
            catch
            {
                if (CanPublishData(section, request, generation))
                {
                    section.Fail();
                    OperationFailed?.Invoke(this, "影片数据加载失败");
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
            catch { }
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
            catch { }
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
            search?.PrepareShutdownAsync() ?? Task.CompletedTask,
            metadata?.PrepareShutdownAsync() ?? Task.CompletedTask,
            rules?.PrepareShutdownAsync() ?? Task.CompletedTask,
            sources?.PrepareShutdownAsync() ?? Task.CompletedTask,
            filmPlayback?.CloseSessionAsync() ?? Task.CompletedTask,
            settings is FilmSettingsView filmSettings ? filmSettings.PreparePlaybackSettingsShutdownAsync() : Task.CompletedTask,
            DrainOperationsAsync());
        if (sourceStartup is not null) await sourceStartup;
    }

    public async Task CancelShutdownPreparationAsync()
    {
        if (disposed || disposal is not null || abortion is not null)
            throw new InvalidOperationException("CLOSED");
        if (preparation is null) return;
        await preparation;
        preparation = null;
        stopping = false;
        rules?.Resume();
        sources?.Resume();
        metadata?.Resume();
        search?.Resume();
        if (settings is FilmSettingsView filmSettings) filmSettings.ResumePlaybackSettings();
        if (sourceStartup is not null)
        {
            sourceStartup = null;
            _ = StartSourcesAsync();
        }
        foreach (var section in Sections.Where(section => section.State == FilmHomeSectionState.Loading))
            section.Cancel();
        foreach (var section in DataSections.Where(section => section.State == FilmDataSectionState.Loading))
            section.Cancel();
    }

    public Task AbortAsync()
    {
        if (abortion is { IsFaulted: false, IsCanceled: false }) return abortion;
        if (disposal is { IsFaulted: false, IsCanceled: false }) return disposal;
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

    public ValueTask DisposeAsync()
    {
        if (abortion is not null) return new(AbortAsync());
        if (disposal is not null) return new(disposal);
        var prepare = PrepareShutdownAsync();
        return new(disposal = DisposeCoreAsync(prepare));
    }

    private async Task DisposeCoreAsync(Task prepare)
    {
        await prepare;
        lifetime.Cancel();
        await DrainOperationsAsync();
        await ReleaseSettingsAsync();
        Release();
    }

    private async Task ReleaseSettingsAsync()
    {
        // Settings writes retain their receipt, but cannot notify a released search.
        if (metadata is not null) metadata.Saved -= MetadataSaved;
        if (settings is FilmSettingsView filmSettings) await filmSettings.PreparePlaybackSettingsShutdownAsync();
        await Task.WhenAll(search?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            metadata?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            rules?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            sources?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            filmPlayback?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            library?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        if (sourceStartup is not null) await sourceStartup;
    }

    private void Release()
    {
        if (disposed) return;
        foreach (var page in playbackPages.Values) page.ReleaseSearch();
        if (sources is not null) sources.Changed -= PlaybackSourcesChanged;
        if (filmPlayback is not null)
        {
            filmPlayback.OperationFailed -= PlaybackFailed;
            filmPlayback.MediaStateChanged -= PlaybackMediaChanged;
        }
        if (library is not null) library.Changed -= FavoritesChanged;
        source.Dispose();
        disposed = true;
        lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
