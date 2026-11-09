using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Desktop;
using MeowFilm.Platform.Scripting;

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

    public FilmCapability(FilmRuleService rules, FilmSourceService sources, IFilmSearchMetadata? metadata = null)
        : this(new DoubanFilmHomeSource(), rules, sources,
            new FilmSearchService(new NativeJavaScriptJsonEngine(FilmSearchService.LoadProgramSource()), sources, rules, metadata),
            metadata as FilmMetadataService)
    {
    }

    internal FilmCapability(IFilmHomeSource source) : this(source, null, null, null, null) { }

    internal FilmCapability(IFilmHomeSource source, FilmSearchService search, FilmMetadataService? metadata = null)
        : this(source, null, null, search, metadata) { }

    private FilmCapability(IFilmHomeSource source, FilmRuleService? rules, FilmSourceService? sources,
        FilmSearchService? search = null, FilmMetadataService? metadata = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.rules = rules;
        this.sources = sources;
        this.search = search;
        this.metadata = metadata;
        if (metadata is not null) metadata.Saved += MetadataSaved;
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
            ? new FilmSettingsView(rules, sources, metadata)
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
    private void MetadataSaved(object? sender, EventArgs e) => Search.InvalidateMetadata();
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

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stopping) return Task.CompletedTask;
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.CompletedTask;
    }

    /// <summary>Called by App composition, never by view visibility. Failures stay
    /// observable on the source service and do not disable native rule settings.</summary>
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
            if (Playback is null)
                SelectPlayback(new("preview:long-season", "漫长的季节",
                    "pack://application:,,,/MeowFilm.Capabilities;component/Film/Presentation/Assets/poster-long-season.jpg",
                    "9.4"), "tv");
            return;
        }
        if (route == DataRoute)
        {
            if (ActiveData.LoadsOnEntry)
            {
                if (ActiveData.CanSeedFromHome)
                {
                    var home = Sections.Single(section => section.Key == ActiveData.Key);
                    ActiveData.Seed(home.Items);
                }
                await StartDataLoad(
                    ActiveData,
                    generation,
                    NextDataLimit(ActiveData),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return;
        }

        var targets = Sections.Where(section => section.LoadsOnEntry && !requests.ContainsKey(section)).ToArray();
        if (targets.Length == 0) return;
        await Task.WhenAll(targets.Select(section =>
            StartSectionLoad(section, generation, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
    }

    public string CaptureNavigationLocation() =>
        IsPlayback && Playback is { } page ? $"{PlaybackRoute}:{page.NavigationIdentity}" : route;

    public Task RestoreNavigationAsync(string location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stopping) return Task.CompletedTask;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (location.StartsWith(PlaybackRoute + ":", StringComparison.Ordinal))
        {
            if (!playbackPages.TryGetValue(location[(PlaybackRoute.Length + 1)..], out var page))
                throw new ArgumentException("Unknown film playback location.", nameof(location));
            Playback = page;
            ReleaseUnreachableSearchPages();
            Changed(nameof(Playback));
            return NavigateAsync(PlaybackRoute, token);
        }
        return NavigateAsync(location, token);
    }

    internal void SuspendPresentation()
    {
        navigationGeneration++;
        RetireRequests();
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

    internal Task OpenPlaybackAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || (!IsHome && !IsData)) return Task.CompletedTask;
        bool visible = IsHome
            ? Sections.Any(section => section.Items.Contains(item))
            : ActiveData.Items.Contains(item);
        if (!visible) throw new ArgumentException("Unknown film item.", nameof(item));
        string sectionKey = IsData ? ActiveData.Key :
            Sections.First(section => section.Items.Contains(item)).Key;
        SelectPlayback(item, sectionKey);
        if (NavigationRequested is not { } requested)
            return NavigateAsync(PlaybackRoute, lifetime.Token);
        requested(this, new(PlaybackRoute));
        return Task.CompletedTask;
    }

    public void ReceiveSearchPlayback(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed) throw new InvalidOperationException("CLOSED");
        var context = item.SearchContext ?? throw new ArgumentException("Missing search context.", nameof(item));
        if (!ReferenceEquals(search?.Current, context.Session))
            throw new ArgumentException("Retired search session.", nameof(item));
        var page = playbackPages.Values.FirstOrDefault(candidate =>
            candidate.SearchContext is { } selected && selected.SameSelection(context));
        if (page is null)
        {
            page = FilmPlaybackPage.FromSearch(item);
            playbackPages.Add(page.NavigationIdentity, page);
        }
        else page.UpdateItem(item);
        Playback = page;
        ReleaseUnreachableSearchPages();
        Changed(nameof(Playback));
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

    internal Task OpenRecommendationAsync(FilmCardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (stopping || disposed || !IsPlayback || Playback is null) return Task.CompletedTask;
        if (!Playback.Recommendations.Contains(item))
            throw new ArgumentException("Unknown film recommendation.", nameof(item));
        SelectPlayback(item, "tv");
        if (NavigationRequested is not { } requested)
            return NavigateAsync(PlaybackRoute, lifetime.Token);
        requested(this, new(PlaybackRoute));
        return Task.CompletedTask;
    }

    private void SelectPlayback(FilmCardItem item, string sectionKey)
    {
        if (!playbackPages.TryGetValue(item.Id, out var page))
        {
            page = FilmPlaybackPreview.Create(item, sectionKey);
            playbackPages.Add(item.Id, page);
        }
        else page.UpdateItem(item);
        if (ReferenceEquals(Playback, page)) return;
        Playback = page;
        ReleaseUnreachableSearchPages();
        Changed(nameof(Playback));
    }

    internal async Task RetrySectionAsync(FilmHomeSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (stopping || disposed || !IsHome || !Sections.Contains(section) ||
            requests.ContainsKey(section) || !section.CanRetry)
            return;
        await StartSectionLoad(section, navigationGeneration, lifetime.Token);
    }

    internal async Task RetryDataAsync()
    {
        var section = ActiveData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanRetry)
            return;
        await StartDataLoad(section, navigationGeneration, NextDataLimit(section), lifetime.Token);
    }

    internal async Task LoadMoreDataAsync()
    {
        var section = ActiveData;
        if (stopping || disposed || !IsData || dataRequest is not null || !section.CanPage)
            return;
        await StartDataLoad(section, navigationGeneration, NextDataLimit(section), lifetime.Token);
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
        CancellationToken cancellationToken)
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
                var items = await source.LoadAsync(section.Definition, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (CanPublish(section, request, generation)) section.Complete(items);
            }
            catch (OperationCanceledException)
            {
                if (CanPublish(section, request, generation)) section.Cancel();
            }
            catch
            {
                if (CanPublish(section, request, generation)) section.Fail();
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
        CancellationToken cancellationToken)
    {
        if (limit < 1 || limit > DataPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        int start = section.NextStart;
        var definition = section.Definition;
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        dataRequest = request;
        dataRequestSection = section;
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
                var items = await source.LoadAsync(definition, start, limit, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (CanPublishData(section, request, generation))
                    section.Complete(items, start, limit);
            }
            catch (OperationCanceledException)
            {
                if (CanPublishData(section, request, generation)) section.Cancel();
            }
            catch
            {
                if (CanPublishData(section, request, generation)) section.Fail();
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
        if (abortion is not null) return abortion;
        if (disposal is not null) return disposal;
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
        if (abortion is not null) return new(abortion);
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
        // Final release may drain an admitted settings write alongside an already closed search.
        // Preparation keeps this observer; final/forced disposal must retire it first.
        if (metadata is not null) metadata.Saved -= MetadataSaved;
        await Task.WhenAll(search?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            metadata?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            rules?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            sources?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        if (sourceStartup is not null) await sourceStartup;
    }

    private void Release()
    {
        if (disposed) return;
        disposed = true;
        foreach (var page in playbackPages.Values) page.ReleaseSearch();
        source.Dispose();
        lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
