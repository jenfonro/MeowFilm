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
    IDebugModuleAdapterSource, IRuntimeRecoveryScope, ICatalogBrowseController
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
    private readonly IStorageDatabase storage;
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
        this.storage = storage;
        search = new FilmSearchService(capture, sources);
        nativeFilmPlayback = playback;
        filmPlayback = new FilmPlaybackSession(playback,
            (reconnect, token) => ConnectPlaybackRuntimeAsync(sources, capture, reconnect, token));
        library = new FilmLibrary(storage);
        business = new FilmPlaybackSettings(runtime, storage);

        CacheSettingsView = new FilmMetadataCacheSettingsView(metadata);
        metadata.Saved += MetadataSaved;
        filmPlayback.OperationFailed += PlaybackFailed;
        filmPlayback.MediaStateChanged += PlaybackMediaChanged;
        library.Changed += FavoritesChanged;
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
        settings = new FilmSettingsView(rules, sources, storage, business, () => Playback?.TargetId, metadata);
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
    internal FilmLibrary FavoritesLibrary => library;
    internal event EventHandler? HistoryInvalidated;
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
    public bool IsBrowse => route is HomeRoute or DataRoute;
    public bool IsPlayback => route == PlaybackRoute;
    System.Collections.IEnumerable ICatalogBrowseController.BrowseSections => Sections;
    bool ICatalogBrowseController.HasSections => true;
    bool ICatalogBrowseController.ShowHomeLoading => false;
    bool ICatalogBrowseController.ShowHomeStatus => false;
    string ICatalogBrowseController.HomeStatusText => "";
    bool ICatalogBrowseController.CanRetryHome => false;
    bool ICatalogBrowseController.CanImportHome => false;
    public CatalogDataSection BrowseActiveData => ActiveData;
    string ICatalogBrowseController.DataRefreshAutomationName => "刷新影片数据";
    string ICatalogBrowseController.DataListAutomationName => "影片列表";
    string ICatalogBrowseController.DataRetryAutomationName => "重试加载影片数据";
    string ICatalogBrowseController.DataMoreAutomationName => "加载更多影片";
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
        filmPlayback.OperationFailed -= PlaybackFailed;
        filmPlayback.MediaStateChanged -= PlaybackMediaChanged;
        library.Changed -= FavoritesChanged;
        disposed = true;
        lifetime.Dispose();
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));

    public FilmMetadataService Metadata => metadata;
    public FilmRuleService Rules => rules;
    public FilmSourceService Sources => sources;

    public async Task<ExportedFilmPreferences> ExportPreferencesAsync(CancellationToken token = default)
    {
        var prefs = await business.ReadPreferencesAsync(token).ConfigureAwait(false);
        return new ExportedFilmPreferences(
            prefs.RuleOrder,
            prefs.PanOrder,
            prefs.Keywords,
            prefs.SiteCleanKeywords,
            prefs.IosAutoPrepare,
            prefs.MetadataViewProvider);
    }

    public async Task SaveImportedPreferencesAsync(
        string[]? ruleOrder,
        string[]? panOrder,
        string[]? keywords,
        string[]? siteCleanKeywords,
        bool? iosAutoPrepare,
        string? metadataViewProvider,
        CancellationToken token = default)
    {
        var current = await business.ReadPreferencesAsync(token).ConfigureAwait(false);
        string[] nextRuleOrder = current.RuleOrder;
        if (ruleOrder is not null)
        {
            try { nextRuleOrder = FilmPreferences.NormalizeRuleOrder(ruleOrder); } catch { }
        }
        string[] nextPanOrder = current.PanOrder;
        if (panOrder is not null)
        {
            try { nextPanOrder = FilmPreferences.NormalizePanOrder(panOrder); } catch { }
        }
        string[] nextKeywords = current.Keywords;
        if (keywords is not null)
        {
            var cleaned = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            try { nextKeywords = FilmPreferences.NormalizeList(cleaned); } catch { }
        }
        string[] nextClean = current.SiteCleanKeywords;
        if (siteCleanKeywords is not null)
        {
            var cleaned = siteCleanKeywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            try { nextClean = FilmPreferences.NormalizeList(cleaned); } catch { }
        }
        bool nextAutoPrepare = iosAutoPrepare ?? current.IosAutoPrepare;
        string nextProvider = metadataViewProvider is "tmdb" or "douban" ? metadataViewProvider : current.MetadataViewProvider;

        var merged = new FilmPreferences(nextRuleOrder, nextPanOrder, nextKeywords, nextClean, nextAutoPrepare, nextProvider);
        if (!current.ContentEquals(merged))
            await business.SavePreferencesAsync(merged).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportedPanAccount>> ExportPanAccountsAsync(CancellationToken token = default)
    {
        var repo = new PanCredentialRepository(storage);
        var (rows, revision) = await repo.ReadAllAsync(token).ConfigureAwait(false);
        if (revision == 0) return [];
        return rows.Select(r => new ExportedPanAccount(
            r.Provider, r.Cookie, r.Username, r.Password, r.Authorization, r.RefreshedAtMs)).ToArray();
    }

    public async Task ReplacePanAccountsAsync(
        IReadOnlyDictionary<string, ExportedPanAccount?> accounts,
        CancellationToken token = default)
    {
        var panStore = new PanSettingsStore(sources.Runtime, storage);
        foreach (var descriptor in PanSettingsStore.Providers)
        {
            token.ThrowIfCancellationRequested();
            string provider = descriptor.Provider;
            if (!accounts.TryGetValue(provider, out var entry) || entry is null)
            {
                try { await panStore.ClearAsync(provider, token).ConfigureAwait(false); } catch { }
                continue;
            }
            var dict = new Dictionary<string, object>(StringComparer.Ordinal);
            if (descriptor.HasField("cookie") && !string.IsNullOrEmpty(entry.Cookie) &&
                !entry.Cookie.Contains('\0') && entry.Cookie.IndexOfAny(['\r', '\n']) < 0)
                dict["cookie"] = entry.Cookie;
            if (descriptor.HasField("username") && !string.IsNullOrEmpty(entry.Username) &&
                !entry.Username.Contains('\0') && entry.Username.IndexOfAny(['\r', '\n']) < 0)
                dict["username"] = entry.Username;
            if (descriptor.HasField("password") && !string.IsNullOrEmpty(entry.Password) &&
                !entry.Password.Contains('\0'))
                dict["password"] = entry.Password;
            if (descriptor.HasField("authorization") && !string.IsNullOrEmpty(entry.Authorization) &&
                !entry.Authorization.Contains('\0') && entry.Authorization.IndexOfAny(['\r', '\n']) < 0)
                dict["authorization"] = entry.Authorization;
            if (descriptor.HasField("refreshedAt") && entry.RefreshedAtMs is >= 0 and <= BridgeCodec.MaximumSafeInteger)
                dict["refreshedAt"] = entry.RefreshedAtMs.Value;

            try { await panStore.SaveAsync(provider, dict, token).ConfigureAwait(false); } catch { }
        }
    }

    public Task RefreshSettingsAfterImportAsync() => settings.RefreshAfterExternalImportAsync();
}
