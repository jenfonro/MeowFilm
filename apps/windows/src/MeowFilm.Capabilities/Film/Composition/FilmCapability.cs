using MeowFilm.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    internal FilmView DebugView => view;
    internal string DebugRoute => route;
    internal FilmPlaybackSession DebugFilmPlayback => filmPlayback;
    internal FilmPlayback DebugNativeFilmPlayback => nativeFilmPlayback;
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

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        Search.Start(query, cancellationToken);
        return Task.CompletedTask;
    }

    public string OperationErrorText(Exception error) =>
        error is ArgumentException ? "无法打开影片页面" : "影片内容加载失败";

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
        if (!dispatcher.IsClosing)
        {
            if (dispatcher.HasThreadAccess) view.PlaybackViewControl.ClosePlayerWindow();
            else try { await dispatcher.EnqueueAsync(() => view.PlaybackViewControl.ClosePlayerWindow()); } catch { }
        }
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
        if (!dispatcher.IsClosing)
        {
            if (dispatcher.HasThreadAccess) view.PlaybackViewControl.ClosePlayerWindow();
            else try { await dispatcher.EnqueueAsync(() => view.PlaybackViewControl.ClosePlayerWindow()); } catch { }
        }
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
