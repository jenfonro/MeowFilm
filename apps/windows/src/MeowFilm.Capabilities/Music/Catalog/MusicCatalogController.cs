using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for music catalog browsing, public home/channels loading, personal/daily-style/VIP routing,
/// and playlist/card actions &amp; network-playlist CRUD.
/// </summary>
public sealed partial class MusicCatalogController : MusicController
{
    private sealed record CatalogLocation(string Route, string Platform, string Query, string Kind, string PageCode,
        int Page, Dictionary<int, CatalogPageState> Pages, JsonElement?[] Cursors)
    {
        public ObservableCollection<MusicCatalogSection>? ChannelSections { get; init; }
        public long Observation { get; init; }
    }

    private sealed record CatalogLoadKey(string Route, string Query, int Number, string Continuation,
        bool Refresh, string Tag, string Platform, string Kind, string PageCode, long AccountEpoch,
        long AccountObservation,
        string TargetOperation, string TargetPlatform, string TargetTitle, string TargetParameters);

    private sealed record CatalogLoadRequest(CatalogLoadKey Key, JsonElement? Continuation, CatalogTarget? Target);

    private sealed class CatalogLoadFlight(CatalogLoadRequest request, long generation, long statusStamp,
        CancellationTokenSource cancellation)
    {
        public CatalogLoadRequest Request { get; } = request;
        public long Generation { get; } = generation;
        public long StatusStamp { get; } = statusStamp;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Work { get; set; } = Task.CompletedTask;
        public int Subscribers;
        public bool Published;
    }

    private readonly MusicSettingsController settings;
    private readonly MusicAccountsController accounts;
    private readonly MusicPlaybackController playbackController;
    private readonly MusicQueueController queue;
    private readonly MusicLibraryController library;
    private readonly MusicSearchController search;
    private readonly MusicHomeSnapshotStore homeSnapshotStore;
    private readonly MusicCatalogOptionalReadService optionalReads;

    private readonly ObservableCollection<MusicCatalogSection> homeSections = [], utilitySections = [];
    private ObservableCollection<MusicCatalogSection> channelSections = [];
    private readonly ObservableCollection<MusicCatalogSection> homeDisplaySections = [];
    private readonly Dictionary<MusicCatalogSection, MusicCatalogSection> homeProjection = new(ReferenceEqualityComparer.Instance);
    private readonly ObservableCollection<MusicCatalogTag> channelTags = [], utilityTags = [];
    private readonly Dictionary<string, CatalogLocation> catalogLocations = [];
    private readonly Dictionary<string, string> catalogChannelCodes = [];
    private readonly Dictionary<string, string> catalogChannelKinds = [];
    private readonly Dictionary<int, CatalogPageState> catalogPages = [];
    private readonly List<JsonElement?> catalogCursors = [null];
    private readonly List<MusicCatalogSection[]> homeContentPages = [];
    private readonly HashSet<string> changedCatalogAccounts = [];

    private MusicHomeSnapshot? homeSnapshot;
    private Task? startupPreparation;
    private CancellationTokenSource? startupCancellation;
    private CancellationTokenSource? homeContinuationCancellation;
    private bool homeSnapshotRead, homeRefreshRequired = true;
    private string homePlatform = "";
    private int homePage = 1;
    private JsonElement?[] homeCursors = [null];
    private Task homeAccountRecovery = Task.CompletedTask;
    private bool homeAccountRecoveryPending, homeAccountRecoveryForceRead;
    private string catalogAccountNotice = "";

    private string? lastChannelRoute;
    private string channelEntryError = "";
    private bool catalogSourceReload, channelRefreshRequired, catalogViewVisible;
    private CatalogPageState? publishedHomeState;
    private CatalogLoadFlight? catalogLoadFlight;

    private long actionGeneration;
    private MusicCatalogCard? preparingCard;
    private SongRow? catalogSelectedSong;
    private string catalogHeading = "发现音乐";
    private int catalogTotal;
    private bool catalogMore;
    private int catalogPage = 1;
    private JsonElement? catalogContinuation;
    private string catalogQuery = "", catalogLoadedPlatform = "", catalogTag = "", catalogPageCode = "",
        catalogArtwork = "";
    private string catalogLoadedKind = "歌曲";
    private int? catalogSummaryCount;
    private bool catalogMembersLoaded;
    private string catalogSubtitle = "";

    internal MusicCatalogController(
        MusicRuntimeContext ctx,
        MusicSettingsController settings,
        MusicAccountsController accounts,
        MusicPlaybackController playbackController,
        MusicQueueController queue,
        MusicLibraryController library,
        MusicSearchController search) : base(ctx)
    {
        this.settings = settings;
        this.accounts = accounts;
        this.playbackController = playbackController;
        this.queue = queue;
        this.library = library;
        this.search = search;
        homeSnapshotStore = new(ctx.CacheDatabase);
        optionalReads = new(
            ctx,
            CatalogAccountKey,
            accounts.PlatformObservation,
            () => IsCatalogPage,
            GetDisplayCardsForOptionalDemand,
            key => key == "catalog-home" || key == lastChannelRoute || catalogLocations.Values.Any(saved => saved.Route == key),
            RequireCatalogAccount);
        application.Library.CatalogSourceChanged += OnCatalogSourceChanged;
        application.Accounts.SessionChanged += OnAccountSessionChanged;
        accounts.PropertyChanged += OnAccountsPropertyChanged;
        library.NetworkPlaylists.CollectionChanged += OnNetworkPlaylistsChanged;
        application.VipPage.VipPageInvalidated += OnVipPageInvalidated;
    }

    private void OnCatalogSourceChanged(MusicCatalogSourceRecord sourceRecord)
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (!stopping && !disposed) RetireCatalogSourcePages();
        });
    }

    private void OnAccountSessionChanged(string platform)
    {
        var observation = accounts.RecordPlatformObservation(platform);
        OnCatalogAccountObserved(platform, observation);
    }

    private void OnAccountsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (stopping || disposed || e.PropertyName != nameof(MusicAccountsController.Accounts)) return;
        RefreshHomeDisplay();
        CatalogBindings();
    }

    private void OnNetworkPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!stopping && !disposed) NetworkPlaylistBindings();
    }

    internal event Action<ObservableCollection<MusicCatalogSection>>? ChannelPageRetired;

    internal long ActionGeneration => actionGeneration;
    internal MusicCatalogCard? PreparingCard => preparingCard;

    internal void SetPreparingCard(MusicCatalogCard? card)
    {
        if (ReferenceEquals(preparingCard, card)) return;
        if (preparingCard is not null) preparingCard.IsPreparing = false;
        preparingCard = card;
        if (preparingCard is not null) preparingCard.IsPreparing = true;
    }

    internal void ClearPreparingCard() => SetPreparingCard(null);

    internal void ClearPreparingCardIf(Func<MusicCatalogCard, bool> predicate)
    {
        if (preparingCard is { } current && predicate(current)) ClearPreparingCard();
    }

    internal void ClearPreparingCardIfEquals(MusicCatalogCard? card, long generation)
    {
        if (generation == actionGeneration && ReferenceEquals(preparingCard, card))
            ClearPreparingCard();
    }

    public ObservableCollection<SongRow> CatalogSongsSource { get; } = [];

    public SongRow? CatalogSelectedSong
    {
        get => catalogSelectedSong;
        set
        {
            if (value is not null && !CatalogSongsSource.Any(row => ReferenceEquals(row, value)))
            {
                Changed();
                return;
            }
            if (ReferenceEquals(catalogSelectedSong, value)) return;
            catalogSelectedSong = value;
            Changed();
        }
    }

    internal void ClearCatalogSelection()
    {
        if (catalogSelectedSong is null) return;
        catalogSelectedSong = null;
        Changed(nameof(CatalogSelectedSong));
    }

    public string CatalogHeading => catalogHeading;
    public int CatalogTotal => catalogTotal;
    public string CatalogDetailArtwork => IsCatalogDetailPage
        ? (catalogArtwork.Length > 0 ? catalogArtwork : CatalogSongsSource.FirstOrDefault(row => row.Cover.Length > 0)?.Cover ?? "")
        : "";
    public string CatalogDetailCountText => IsCatalogDetailPage
        ? catalogSummaryCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
        : "";
    public string CatalogDetailSectionTitle => IsCatalogArtist ? "热门歌曲" : "歌曲";
    public string CatalogDetailArtworkName => IsCatalogArtist ? "歌手头像" : "歌单封面";

    internal void SetCatalogLocationState(string? nextHeading, bool clearCatalogSongs, bool clearSelection, bool clearTotal)
    {
        if (nextHeading is not null && catalogHeading != nextHeading) { catalogHeading = nextHeading; Changed(nameof(CatalogHeading)); }
        if (clearTotal && catalogTotal != 0) { catalogTotal = 0; Changed(nameof(CatalogTotal)); Changed(nameof(CanPlayCatalogPlaylist)); }
        if (clearCatalogSongs) CatalogSongsSource.Clear();
        if (clearSelection) ClearCatalogSelection();
    }

    // Sections, channels, tags & browse state
    internal IEnumerable<ObservableCollection<MusicCatalogSection>> RetainedChannelSections =>
        catalogLocations.Values.Select(page => page.ChannelSections).OfType<ObservableCollection<MusicCatalogSection>>();
    public ObservableCollection<MusicCatalogSection> CatalogSections => search.IsSearchPage ? search.ActiveSections :
        IsCatalogHome ? homeSections : IsChannelLanding ? channelSections : utilitySections;
    public ObservableCollection<MusicCatalogSection> HomeSections => homeDisplaySections;
    public ObservableCollection<MusicCatalogSection> ChannelSections => channelSections;
    public ObservableCollection<MusicCatalogSection> UtilitySections => utilitySections;
    public ObservableCollection<MusicCatalogSection> DisplayCatalogSections => IsCatalogHome ? homeDisplaySections : CatalogSections;
    public ObservableCollection<MusicCatalogCard> ChannelTabs { get; } = [];
    public ObservableCollection<MusicCatalogCard> VisibleChannelTabs { get; } = [];
    public ObservableCollection<MusicCatalogCard> MoreChannelTabs { get; } = [];
    public ObservableCollection<MusicCatalogTag> CatalogTags => IsChannelLanding ? channelTags : utilityTags;
    public IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups { get; private set; } = [];
    public MusicCatalogTag? SelectedCatalogTag => CatalogTags.FirstOrDefault(tag => tag.Selected);

    private bool IsDiscoveryRoute => ctx.CurrentRoute == "catalog-discovery";
    public bool IsCatalogHome => ctx.CurrentRoute == "catalog-home";
    public bool IsChannelDirectory => ctx.CurrentRoute == "catalog-channels";
    public bool IsChannelLanding => ctx.CurrentRoute == "catalog-channels" || ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal);
    public bool IsVipChannel => IsChannelLanding && catalogChannelKinds.GetValueOrDefault(ctx.CurrentRoute) == "vip";
    public string CurrentChannelKey => ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal) ? ctx.CurrentRoute[16..] : "";
    public bool HasMoreChannelTabs => MoreChannelTabs.Any();
    public bool HasChannelTabs => IsChannelLanding && ChannelTabs.Count > 0;
    public bool IsCatalogPage => ctx.CurrentRoute.StartsWith("catalog-", StringComparison.Ordinal);
    public bool IsCatalogPlaylist => ctx.CurrentRoute.StartsWith("catalog-playlist/", StringComparison.Ordinal);
    public bool IsCatalogArtist => ctx.CurrentRoute.StartsWith("catalog-artist/", StringComparison.Ordinal);
    public bool IsCatalogDetailPage => IsCatalogPlaylist || IsCatalogArtist || IsCatalogDaily;
    public bool IsCatalogContent => IsCatalogPage && !IsDiscoveryRoute && !IsCatalogDetailPage && !search.IsSearchSongs;
    public bool IsCatalogBrowsePage => IsCatalogPage && !search.IsSearchPage && !IsDiscoveryRoute;
    public bool IsCatalogLanding => ctx.CurrentRoute is "catalog-home" or "catalog-channels" || IsChannelLanding;
    public bool IsCatalogUtilityPage => IsCatalogBrowsePage && !IsCatalogLanding && !IsCatalogDetailPage;
    public bool IsUtilityCatalogSurface => IsCatalogContent && !IsCatalogHome && !IsChannelLanding && !search.IsSearchPage;
    public bool ShowCatalogLoading => IsCatalogContent && !search.IsSearchPage && ctx.LoadCancellation is not null && CatalogSections.Count == 0;
    public string Status => ctx.Status;
    public bool CanCatalogBrowse => !stopping && commands == 0;
    public bool CanCatalogNext => !IsCatalogHome && CanCatalogBrowse && ctx.LoadCancellation is null && catalogMore;
    public bool CanCatalogPrevious => CanCatalogBrowse && ctx.LoadCancellation is null && !IsCatalogHome && !IsCatalogDetailPage && catalogPage > 1;
    public bool CanLoadMoreCatalogPlaylist => IsCatalogPlaylist && CanCatalogNext;
    public bool HasCatalogTags => CatalogTags.Count > 0 && ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal);
    public bool CanCancelCatalog => ctx.LoadCancellation is not null || homeContinuationCancellation is not null || catalogAction is not null;
    public string CatalogPageText => IsCatalogPage ? $"第 {catalogPage} 页" : "";
    public bool ShowDetailLoading => IsCatalogDetailPage && ctx.LoadCancellation is not null && !catalogMembersLoaded;
    public string DetailSubtitle => IsCatalogDetailPage ? catalogSubtitle : "";

    internal string CatalogArtwork => catalogArtwork;
    internal int? CatalogSummaryCount => catalogSummaryCount;
    internal string HomePlatform => homePlatform;
    internal string CatalogAccountNotice => catalogAccountNotice;
    internal void ClearCatalogAccountNotice() => catalogAccountNotice = "";
    internal void CancelStartupForShutdown() => startupCancellation?.Cancel();

    internal static bool IsCatalogRoute(string route) => MusicCatalogDispatcher.IsCatalogRoute(route);
    internal static CatalogTarget Target(string operation, string platform, string title, CatalogTargetParams? parameters = null) =>
        MusicCatalogDispatcher.Target(operation, platform, title, parameters);

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        Changed(nameof(Status));
        CatalogBindings();
    }

    internal void NotifyStatusChanged() => Changed(nameof(Status));

    public void CatalogBindings()
    {
        foreach (var name in new[]
        {
            nameof(CatalogHeading), nameof(CatalogTotal), nameof(CatalogSelectedSong),
            nameof(CatalogDetailArtwork), nameof(CatalogDetailCountText), nameof(CatalogDetailSectionTitle), nameof(CatalogDetailArtworkName),
            nameof(ShowCatalogLoading), nameof(IsVipChannel), nameof(HasMoreChannelTabs), nameof(HasChannelTabs),
            nameof(CanCatalogNext), nameof(CanCatalogPrevious), nameof(CanCatalogBrowse), nameof(HasCatalogTags), nameof(CatalogTagGroups),
            nameof(CanCatalogAction), nameof(CanPlayCatalogPlaylist), nameof(CanCancelCatalog), nameof(CatalogPageText), nameof(SelectedCatalogTag),
            nameof(CatalogPlatformChoices), nameof(SelectedCatalogPlatform),
            nameof(ShowDetailLoading), nameof(DetailSubtitle), nameof(CanLoadMoreCatalogPlaylist)
        })
            Changed(name);
        DailyStyleBindings();
        PersonalCatalogBindings();
        NetworkPlaylistBindings();
        VipPageBindings();
        _ = SyncVipSurfaceAsync();
    }

    public void InvalidateCatalogAction()
    {
        actionGeneration++;
        ClearPreparingCard();
        OnActionInvalidated();
    }

    internal bool ContainsCatalogCard(MusicCatalogCard card) =>
        HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card)) || DisplayCatalogSections.Any(section =>
        ReferenceEquals(section.Action, card) || section.Items.Any(item => ReferenceEquals(item, card)));

    private void RegisterChannelRouteMeta(string destination, string pageCode, string channelKind)
    {
        catalogChannelCodes[destination] = pageCode;
        catalogChannelKinds[destination] = channelKind;
        while (catalogChannelCodes.Count > 100) catalogChannelCodes.Remove(catalogChannelCodes.Keys.First());
    }

    private IEnumerable<MusicCatalogCard> GetDisplayCardsForOptionalDemand()
    {
        foreach (var section in DisplayCatalogSections)
        {
            foreach (var item in section.Items) yield return item;
            if (section.Action is { } action) yield return action;
        }
        if (HasChannelTabs)
            foreach (var tab in ChannelTabs) yield return tab;
    }

    internal void OnLoadGenerationAdvanced()
    {
        optionalReads.RetireSurface();
        if (stopping) optionalReads.Retire();
        CancelCatalogLoad(homeContinuationCancellation);
        homeContinuationCancellation = null;
    }

    internal async ValueTask DisposeResourcesAsync()
    {
        application.Library.CatalogSourceChanged -= OnCatalogSourceChanged;
        application.Accounts.SessionChanged -= OnAccountSessionChanged;
        accounts.PropertyChanged -= OnAccountsPropertyChanged;
        library.NetworkPlaylists.CollectionChanged -= OnNetworkPlaylistsChanged;
        application.VipPage.VipPageInvalidated -= OnVipPageInvalidated;
        PrepareVipShutdown();
        await optionalReads.DisposeAsync().ConfigureAwait(false);
        await homeSnapshotStore.DisposeAsync().ConfigureAwait(false);
    }

    internal void RetireCatalogPreviewSurface() => optionalReads.RetireSurface();
    internal void UpdateCatalogVisibleCards(IEnumerable<MusicCatalogCard> cards) => optionalReads.UpdateVisibleCards(cards);

    internal void BeginSearchCatalogState(string keyword, string kindLabel)
    {
        catalogQuery = keyword; catalogLoadedKind = kindLabel;
        catalogLoadedPlatform = ""; catalogLoadedTarget = null;
        CatalogTags.Clear();
    }

    internal void PublishSearchCatalogMetadata(string keyword, string kindLabel)
    {
        catalogQuery = keyword; catalogLoadedPlatform = ""; catalogLoadedTarget = null;
        catalogAccountStamp = ""; catalogMore = false; catalogContinuation = null;
        catalogLoadedKind = search.SearchKindLabel = kindLabel;
    }

    internal void ClearCatalogTags()
    {
        if (CatalogTags.Count > 0) CatalogTags.Clear();
    }

    internal void ClearSectionsAndTags()
    {
        CatalogSections.Clear();
        CatalogTags.Clear();
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, MusicSongRecord?> read, long publication)
    {
        foreach (var song in CatalogSongsSource.Append(catalogSelectedSong).OfType<SongRow>().Where(s => ids.Contains(s.Id)))
            song.ApplyRepository(read(song.Id), publication);
        var allSections = homeSections.Concat(homeDisplaySections).Concat(channelSections).Concat(utilitySections)
            .Concat(catalogPages.Values.SelectMany(page => page.Sections))
            .Concat(catalogLocations.Values.SelectMany(saved => saved.Pages.Values).SelectMany(page => page.Sections));
        var cards = MusicCatalogSection.RepositoryCards(allSections).Concat(ChannelTabs)
            .Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance);
        foreach (var card in cards)
        {
            if (card.Song is { } song && ids.Contains(song.Id)) card.RefreshSong(read(song.Id), publication);
            if (card.PreviewSongs.Any(preview => preview.Song is { } song && ids.Contains(song.Id)))
                card.RefreshPreview(read, publication);
        }
        Changed(nameof(CatalogDetailArtwork));
    }

    private async Task RefreshCatalogSongsAsync(IEnumerable<MusicCatalogCard> cards, CancellationToken token)
    {
        var rows = cards.Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance).ToArray();
        var ids = rows.SelectMany(card => card.PreviewSongs.Select(preview => preview.Song).Append(card.Song))
            .OfType<SongRow>().Select(song => song.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        var snapshot = await application.Library.SongSnapshotsAsync(ids, token);
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        MusicSongRecord? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
        foreach (var card in rows)
        {
            if (card.Song is { } song) card.RefreshSong(Read(song.Id), snapshot.Publication);
            card.RefreshPreview(Read, snapshot.Publication);
        }
    }

    internal void NotifyRouteChanged(bool sectionsChanged)
    {
        if (sectionsChanged)
        {
            Changed(nameof(CatalogSections));
            Changed(nameof(DisplayCatalogSections));
        }
        UpdateChannelSelection();
        foreach (var name in new[]
        {
            nameof(IsUtilityCatalogSurface), nameof(IsCatalogHome),
            nameof(IsCatalogPage), nameof(IsChannelDirectory),
            nameof(CurrentChannelKey), nameof(IsCatalogLanding),
            nameof(IsCatalogUtilityPage), nameof(IsChannelLanding),
            nameof(IsCatalogPlaylist), nameof(IsCatalogArtist),
            nameof(IsCatalogDetailPage), nameof(IsCatalogBrowsePage),
            nameof(IsCatalogContent), nameof(IsVipChannel),
            nameof(CatalogDetailArtwork), nameof(CatalogDetailCountText),
            nameof(CatalogDetailSectionTitle), nameof(CatalogDetailArtworkName),
            nameof(DetailSubtitle), nameof(ShowDetailLoading), nameof(CanPlayCatalogPlaylist)
        })
            Changed(name);
        VipPageBindings();
        _ = SyncVipSurfaceAsync();
    }

    private void UpdateChannelSelection()
    {
        var key = CurrentChannelKey;
        foreach (var card in ChannelTabs)
            card.IsSelected = key.Length > 0 && key == card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
    }

    private void SelectChannelPage(string route, string platform)
    {
        if (!route.StartsWith("catalog-channel/", StringComparison.Ordinal)) return;
        string key = route + "/" + platform;
        long observation = accounts.PlatformObservation(platform);
        catalogLocations.TryGetValue(key, out var page);
        if (page is not null && page.Observation != observation)
        { RetireCatalogLocation(key); page = null; }
        if (page is null)
        {
            page = new(route, platform, "", "歌曲", "", 1, [], [null])
                { ChannelSections = [], Observation = observation };
            catalogLocations.Add(key, page);
        }
        if (ReferenceEquals(channelSections, page.ChannelSections)) return;
        channelSections = page.ChannelSections!;
        Changed(nameof(ChannelSections));
    }

    private void RetireCatalogLocation(string key)
    {
        if (!catalogLocations.Remove(key, out var page)) return;
        optionalReads.Retire(page.Route);
        if (page.ChannelSections is not { } sections) return;
        sections.Clear();
        ChannelPageRetired?.Invoke(sections);
    }

    private void RetireChannelAccountPages(string platform, long observation)
    {
        foreach (var pair in catalogLocations.Where(pair => pair.Value.Platform == platform &&
            pair.Value.ChannelSections is not null && (pair.Value.Observation < observation ||
            pair.Value.Pages.Values.Any(page => page.AccountStamp != CatalogAccountKey(platform)))).ToArray())
            RetireCatalogLocation(pair.Key);
    }

    private void UpdateChannelTabs()
    {
        Update(VisibleChannelTabs, "primary"); Update(MoreChannelTabs, "more");
        UpdateChannelSelection();
        void Update(ObservableCollection<MusicCatalogCard> target, string placement)
        {
            var incoming = ChannelTabs.Where(card => card.Placement == placement).ToArray();
            for (int index = 0; index < incoming.Length; index++)
                if (index == target.Count) target.Add(incoming[index]);
                else if (!ReferenceEquals(target[index], incoming[index])) target[index] = incoming[index];
            while (target.Count > incoming.Length) target.RemoveAt(target.Count - 1);
        }
    }

    internal void RememberCatalogLocation()
    {
        if (IsDiscoveryRoute || search.IsSearchPage || !IsCatalogPage) return;
        if (!catalogPages.TryGetValue(catalogPage, out var state))
        { RetireCatalogLocation(ctx.CurrentRoute + "/" + catalogLoadedPlatform); return; }
        catalogPages[catalogPage] = state with { Scroll = ctx.CaptureScroll() };
        if (IsCatalogHome)
        {
            publishedHomeState = catalogPages[catalogPage];
            homePage = catalogPage; homeCursors = catalogCursors.ToArray();
            return;
        }
        var key = ctx.CurrentRoute + "/" + catalogLoadedPlatform;
        catalogLocations.Remove(key, out var previous);
        catalogLocations[key] = new(ctx.CurrentRoute, catalogLoadedPlatform, catalogQuery, catalogLoadedKind,
            catalogPageCode, catalogPage, new(catalogPages), catalogCursors.ToArray())
            { ChannelSections = previous?.ChannelSections, Observation = previous?.Observation ?? accounts.PlatformObservation(catalogLoadedPlatform) };
        while (catalogLocations.Count > 8)
        {
            var retired = catalogLocations.FirstOrDefault(pair => pair.Value.Route != lastChannelRoute);
            if (retired.Key is null) break;
            RetireCatalogLocation(retired.Key);
        }
    }
}
