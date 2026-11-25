using System.Collections.ObjectModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for music catalog browsing, public home/channels loading, personal/daily-style routing,
/// and playlist/card actions &amp; network-playlist CRUD. Zero reference to <see cref="MusicSearchController"/>.
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
    private readonly MusicCatalogActionState catalogActions;
    private readonly MusicContentState contentState;
    private readonly MusicHomeSnapshotStore homeSnapshotStore;
    private readonly MusicCatalogOptionalReadService optionalReads;
    private readonly MusicVipPage vipPage;

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
    private bool catalogSourceReload;
    private CatalogPageState? publishedHomeState;
    private CatalogLoadFlight? catalogLoadFlight;

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
        MusicCatalogActionState catalogActions,
        MusicContentState contentState) : base(ctx)
    {
        this.settings = settings;
        this.accounts = accounts;
        this.playbackController = playbackController;
        this.queue = queue;
        this.catalogActions = catalogActions;
        this.contentState = contentState;
        catalogActions.Invalidated += OnActionInvalidated;
        homeSnapshotStore = new(ctx.CacheDatabase);
        optionalReads = new(
            ctx,
            CatalogAccountKey,
            accounts.PlatformObservation,
            () => IsCatalogPage,
            GetDisplayCardsForOptionalDemand,
            key => key == "catalog-home" || key == lastChannelRoute || catalogLocations.Values.Any(saved => saved.Route == key),
            RequireCatalogAccount);
        vipPage = new(application, ctx.Dispatcher);
    }

    // Explicit upward events wired at MusicCapability composition root.
    internal event Func<CancellationToken, Task>? PlaylistsChanged;
    internal event Func<SongRow, Task>? SongAppendNextRequested;
    internal event Func<SongRow, bool, Task>? SongFavoriteRequested;
    internal event Action<ObservableCollection<MusicCatalogSection>>? ChannelPageRetired;
    internal event Action? SessionRetiring;

    // Sections, channels, tags & browse state
    internal IEnumerable<ObservableCollection<MusicCatalogSection>> RetainedChannelSections =>
        catalogLocations.Values.Select(page => page.ChannelSections).OfType<ObservableCollection<MusicCatalogSection>>();
    public ObservableCollection<MusicCatalogSection> CatalogSections => contentState.IsSearchPage ? contentState.SearchActiveSections :
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
    public bool IsCatalogContent => IsCatalogPage && !IsDiscoveryRoute && !IsCatalogDetailPage && !contentState.IsSearchSongs;
    public bool IsCatalogBrowsePage => IsCatalogPage && !contentState.IsSearchPage && !IsDiscoveryRoute;
    public bool IsCatalogLanding => ctx.CurrentRoute is "catalog-home" or "catalog-channels" || IsChannelLanding;
    public bool IsCatalogUtilityPage => IsCatalogBrowsePage && !IsCatalogLanding && !IsCatalogDetailPage;
    public bool IsUtilityCatalogSurface => IsCatalogContent && !IsCatalogHome && !IsChannelLanding && !contentState.IsSearchPage;
    public bool ShowCatalogLoading => IsCatalogContent && !contentState.IsSearchPage && ctx.LoadCancellation is not null && CatalogSections.Count == 0;
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

    // VIP page
    public MusicVipPage VipPage => vipPage;
    internal Task UpdateVipPageAsync(bool viewVisible) => vipPage.SetSurfaceAsync(viewVisible, IsVipChannel);
    public Task SignVipPageAsync() => RunAsync(async () =>
    {
        try { await vipPage.SignAsync(); }
        catch (Exception error) { await Notify(ErrorText(error)); }
    });
    public Task OpenVipBannerAsync()
    {
        var current = vipPage.Banner;
        if (!CanCatalogBrowse || current is null || !vipPage.Owns(current))
            return Task.CompletedTask;
        if (current.ResourceType == "SONG")
            return PlayCatalogSongAsync("wy", current.ResourceId, () => vipPage.Owns(current));
        if (current.ResourceType != "PLAYLIST") return Task.CompletedTask;
        return OpenCatalogTargetAsync(
            Target("playlist", "wy", "歌单", new(Id: current.ResourceId)) with { Artwork = current.ImageUrl },
            current.ResourceId);
    }

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
            nameof(ShowCatalogLoading), nameof(IsVipChannel), nameof(HasMoreChannelTabs), nameof(HasChannelTabs),
            nameof(CanCatalogNext), nameof(CanCatalogPrevious), nameof(CanCatalogBrowse), nameof(HasCatalogTags), nameof(CatalogTagGroups),
            nameof(CanCatalogAction), nameof(CanPlayCatalogPlaylist), nameof(CanCancelCatalog), nameof(CatalogPageText), nameof(SelectedCatalogTag),
            nameof(CatalogPlatformChoices), nameof(SelectedCatalogPlatform),
            nameof(ShowDetailLoading), nameof(DetailSubtitle), nameof(CanLoadMoreCatalogPlaylist)
        })
            Changed(name);
        contentState.NotifyDetailBindings();
        DailyStyleBindings();
        PersonalCatalogBindings();
        NetworkPlaylistBindings();
    }

    public void InvalidateCatalogAction() => catalogActions.Invalidate();

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
        catalogLoadedKind = contentState.SearchKindLabel = kindLabel;
    }

    internal void ClearCatalogTags()
    {
        if (CatalogTags.Count > 0) CatalogTags.Clear();
    }

    internal void ClearCatalogSectionsAndTags()
    {
        CatalogSections.Clear();
        CatalogTags.Clear();
    }

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, Library.MusicSongRecord?> read, long publication)
    {
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
    }

    private async Task RefreshCatalogSongsAsync(IEnumerable<MusicCatalogCard> cards, CancellationToken token)
    {
        var rows = cards.Distinct<MusicCatalogCard>(ReferenceEqualityComparer.Instance).ToArray();
        var ids = rows.SelectMany(card => card.PreviewSongs.Select(preview => preview.Song).Append(card.Song))
            .OfType<SongRow>().Select(song => song.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        var snapshot = await application.SongSnapshotsAsync(ids, token);
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        Library.MusicSongRecord? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
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
            nameof(IsCatalogContent)
        })
            Changed(name);
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
        if (IsDiscoveryRoute || contentState.IsSearchPage || !IsCatalogPage) return;
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
