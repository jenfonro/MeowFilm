using System.Collections.ObjectModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone Catalog service owning public home and channels browsing, home snapshot persistence,
/// home display projection, account-change recovery, channel tab/location caches, and page load flights.
/// </summary>
internal sealed partial class MusicCatalogBrowseService
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

    private readonly MusicRuntimeContext ctx;
    private readonly MusicSettingsController settings;
    private readonly MusicAccountsController accounts;
    private readonly MusicSearchController search;
    private readonly MusicPersonalCatalogService personal;
    private readonly MusicCatalogActionState catalogActions;
    private readonly MusicContentState contentState;
    private readonly Action<string> notify;
    private readonly Action catalogBindings;
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

    internal MusicCatalogBrowseService(
        MusicRuntimeContext ctx,
        MusicSettingsController settings,
        MusicAccountsController accounts,
        MusicSearchController search,
        MusicPersonalCatalogService personal,
        MusicCatalogActionState catalogActions,
        MusicContentState contentState,
        Action<string> notify,
        Action catalogBindings)
    {
        this.ctx = ctx;
        this.settings = settings;
        this.accounts = accounts;
        this.search = search;
        this.personal = personal;
        this.catalogActions = catalogActions;
        this.contentState = contentState;
        this.notify = notify;
        this.catalogBindings = catalogBindings;
        homeSnapshotStore = new(ctx.CacheDatabase);
        optionalReads = new(
            ctx,
            personal.CatalogAccountKey,
            accounts.PlatformObservation,
            () => IsCatalogPage,
            GetDisplayCardsForOptionalDemand,
            key => key == "catalog-home" || key == lastChannelRoute || catalogLocations.Values.Any(saved => saved.Route == key),
            personal.RequireCatalogAccount);
    }

    internal event Action<ObservableCollection<MusicCatalogSection>>? ChannelPageRetired;
    internal event Action<string, long>? CatalogAccountObserved;

    // Public/internal presentation state
    internal ObservableCollection<MusicCatalogSection> CatalogSections => search.IsSearchPage ? search.ActiveSections :
        IsCatalogHome ? homeSections : IsChannelLanding ? channelSections : utilitySections;
    internal ObservableCollection<MusicCatalogSection> HomeSections => homeDisplaySections;
    internal ObservableCollection<MusicCatalogSection> ChannelSections => channelSections;
    internal ObservableCollection<MusicCatalogSection> UtilitySections => utilitySections;
    internal ObservableCollection<MusicCatalogSection> DisplayCatalogSections => IsCatalogHome ? homeDisplaySections : CatalogSections;
    internal IEnumerable<ObservableCollection<MusicCatalogSection>> RetainedChannelSections =>
        catalogLocations.Values.Select(page => page.ChannelSections).OfType<ObservableCollection<MusicCatalogSection>>();

    internal ObservableCollection<MusicCatalogCard> ChannelTabs { get; } = [];
    internal ObservableCollection<MusicCatalogCard> VisibleChannelTabs { get; } = [];
    internal ObservableCollection<MusicCatalogCard> MoreChannelTabs { get; } = [];
    internal ObservableCollection<MusicCatalogTag> CatalogTags => IsChannelLanding ? channelTags : utilityTags;
    internal IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups { get; private set; } = [];
    internal MusicCatalogTag? SelectedCatalogTag => CatalogTags.FirstOrDefault(tag => tag.Selected);

    internal string HomePlatform => homePlatform;
    internal string CatalogAccountNotice => catalogAccountNotice;
    internal void ClearCatalogAccountNotice() => catalogAccountNotice = "";
    internal void CancelStartupForShutdown() => startupCancellation?.Cancel();
    internal string CatalogArtwork => catalogArtwork;
    internal int? CatalogSummaryCount => catalogSummaryCount;
    internal string CatalogLoadedPlatform => catalogLoadedPlatform;
    internal string CatalogQuery => catalogQuery;
    internal bool HasHomeContinuation => homeContinuationCancellation is not null;

    internal bool IsDiscoveryRoute => ctx.CurrentRoute == "catalog-discovery";
    internal bool IsCatalogHome => ctx.CurrentRoute == "catalog-home";
    internal bool IsChannelDirectory => ctx.CurrentRoute == "catalog-channels";
    internal bool IsChannelLanding => ctx.CurrentRoute == "catalog-channels" || ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal);
    internal bool IsVipChannel => IsChannelLanding && catalogChannelKinds.GetValueOrDefault(ctx.CurrentRoute) == "vip";
    internal string CurrentChannelKey => ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal) ? ctx.CurrentRoute[16..] : "";
    internal bool HasMoreChannelTabs => MoreChannelTabs.Any();
    internal bool HasChannelTabs => IsChannelLanding && ChannelTabs.Count > 0;
    internal bool IsCatalogPage => ctx.CurrentRoute.StartsWith("catalog-", StringComparison.Ordinal);
    internal bool IsCatalogPlaylist => ctx.CurrentRoute.StartsWith("catalog-playlist/", StringComparison.Ordinal);
    internal bool IsCatalogArtist => ctx.CurrentRoute.StartsWith("catalog-artist/", StringComparison.Ordinal);
    internal bool IsCatalogDetailPage => IsCatalogPlaylist || IsCatalogArtist || personal.IsCatalogDaily;
    internal bool IsCatalogContent => IsCatalogPage && !IsDiscoveryRoute && !IsCatalogDetailPage && !search.IsSearchSongs;
    internal bool IsCatalogBrowsePage => IsCatalogPage && !search.IsSearchPage && !IsDiscoveryRoute;
    internal bool IsCatalogLanding => ctx.CurrentRoute is "catalog-home" or "catalog-channels" || IsChannelLanding;
    internal bool IsCatalogUtilityPage => IsCatalogBrowsePage && !IsCatalogLanding && !IsCatalogDetailPage;
    internal bool IsUtilityCatalogSurface => IsCatalogContent && !IsCatalogHome && !IsChannelLanding && !search.IsSearchPage;
    internal bool ShowCatalogLoading => IsCatalogContent && !search.IsSearchPage && ctx.LoadCancellation is not null && CatalogSections.Count == 0;
    internal bool CanCatalogBrowse => !ctx.Stopping && ctx.Commands == 0;
    internal bool CanCatalogNext => !IsCatalogHome && CanCatalogBrowse && ctx.LoadCancellation is null && catalogMore;
    internal bool CanCatalogPrevious => CanCatalogBrowse && ctx.LoadCancellation is null && !IsCatalogHome && !IsCatalogDetailPage && catalogPage > 1;
    internal bool CanLoadMoreCatalogPlaylist => IsCatalogPlaylist && CanCatalogNext;
    internal bool HasCatalogTags => CatalogTags.Count > 0 && ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal);
    internal string CatalogPageText => IsCatalogPage ? $"第 {catalogPage} 页" : "";
    internal bool ShowDetailLoading => IsCatalogDetailPage && ctx.LoadCancellation is not null && !catalogMembersLoaded;
    internal string DetailSubtitle => IsCatalogDetailPage ? catalogSubtitle : "";

    internal bool ContainsCatalogCard(MusicCatalogCard card) =>
        HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card)) || DisplayCatalogSections.Any(section =>
        ReferenceEquals(section.Action, card) || section.Items.Any(item => ReferenceEquals(item, card)));

    internal void RegisterChannelRouteMeta(string destination, string pageCode, string channelKind)
    {
        catalogChannelCodes[destination] = pageCode;
        catalogChannelKinds[destination] = channelKind;
        while (catalogChannelCodes.Count > 100) catalogChannelCodes.Remove(catalogChannelCodes.Keys.First());
    }

    // Optional reads delegation
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
        if (ctx.Stopping) optionalReads.Retire();
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

    // Search/Discovery cooperation hooks
    internal void BeginSearchCatalogState(string keyword, string kindLabel)
    {
        catalogQuery = keyword; catalogLoadedKind = kindLabel;
        catalogLoadedPlatform = ""; personal.LoadedTarget = null;
        CatalogTags.Clear();
    }

    internal void PublishSearchCatalogMetadata(string keyword, string kindLabel)
    {
        catalogQuery = keyword; catalogLoadedPlatform = ""; personal.LoadedTarget = null;
        personal.AccountStamp = ""; catalogMore = false; catalogContinuation = null;
        catalogLoadedKind = search.SearchKindLabel = kindLabel;
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

    // Daily style bridge helpers
    internal CatalogPageState? CurrentPageState => catalogPages.GetValueOrDefault(catalogPage);

    internal void RetireSavedDailyStyleSnapshots(string saveAccount)
    {
        bool BelongsToSavedAccount(CatalogPageState page) => page.AccountStamp == saveAccount &&
            page.Target?.Operation == "daily" && page.Target.Params.Branch == "style";
        foreach (var key in catalogLocations.Where(pair => pair.Value.Pages.Values.Any(BelongsToSavedAccount))
            .Select(pair => pair.Key).ToArray()) RetireCatalogLocation(key);
        foreach (var page in catalogPages.Where(pair => BelongsToSavedAccount(pair.Value)).ToArray())
            catalogPages[page.Key] = page.Value with { ExpiresAt = 0 };
    }

    internal void ClearCurrentDailyPageState()
    {
        catalogPages.Clear();
        catalogMembersLoaded = false;
        contentState.CatalogSongsSource.Clear();
        utilitySections.Clear();
    }

    internal Task<bool> PublishDailyStylePageAsync(CatalogPageState state, Func<bool> isCurrent) =>
        PublishCatalogAsync(ctx.CurrentRoute, "", "wy", "歌曲", 1, state,
            token: ctx.Lifetime.Token, isCurrent: isCurrent, beforePublish: () =>
            {
                catalogPages.Clear();
                catalogPages[1] = state;
            });

    // Repository song refresh
    internal IEnumerable<MusicCatalogSection> AllRetainedSections() =>
        homeSections.Concat(homeDisplaySections).Concat(channelSections).Concat(utilitySections)
            .Concat(search.AllSearchSections)
            .Concat(catalogPages.Values.SelectMany(page => page.Sections))
            .Concat(catalogLocations.Values.SelectMany(saved => saved.Pages.Values).SelectMany(page => page.Sections));

    internal static IEnumerable<MusicCatalogCard> RepositoryCards(IEnumerable<MusicCatalogSection> sections) =>
        sections.SelectMany(section => section.Items.Concat(section.Action is { } action ? [action] : [])
            .Concat(section.Tabs?.SelectMany(tab => tab.Items) ?? []));

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, Library.MusicSongRecord?> read,
        long publication, IEnumerable<MusicCatalogCard> cachedSearchCards)
    {
        var cards = RepositoryCards(AllRetainedSections())
            .Concat(cachedSearchCards).Concat(ChannelTabs)
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
        var snapshot = await ctx.Application.SongSnapshotsAsync(ids, token);
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || ctx.Disposed) return;
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
            notify(nameof(MusicCatalogController.CatalogSections));
            notify(nameof(MusicCatalogController.DisplayCatalogSections));
        }
        UpdateChannelSelection();
        foreach (var name in new[]
        {
            nameof(MusicCatalogController.IsUtilityCatalogSurface), nameof(MusicCatalogController.IsCatalogHome),
            nameof(MusicCatalogController.IsCatalogPage), nameof(MusicCatalogController.IsChannelDirectory),
            nameof(MusicCatalogController.CurrentChannelKey), nameof(MusicCatalogController.IsCatalogLanding),
            nameof(MusicCatalogController.IsCatalogUtilityPage), nameof(MusicCatalogController.IsChannelLanding),
            nameof(MusicCatalogController.IsCatalogPlaylist), nameof(MusicCatalogController.IsCatalogArtist),
            nameof(MusicCatalogController.IsCatalogDetailPage), nameof(MusicCatalogController.IsCatalogBrowsePage),
            nameof(MusicCatalogController.IsCatalogContent)
        })
            notify(name);
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
        notify(nameof(MusicCatalogController.ChannelSections));
    }

    internal void RetireCatalogLocation(string key)
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
            pair.Value.Pages.Values.Any(page => page.AccountStamp != personal.CatalogAccountKey(platform)))).ToArray())
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
        if (IsDiscoveryRoute) return;
        if (search.IsSearchPage) { search.RememberSearch(); return; }
        if (!IsCatalogPage) return;
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
