using System.Collections.ObjectModel;
using System.Text.Json;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone Catalog service owning public home and channels browsing, home snapshot persistence,
/// home display projection, account-change recovery, channel tab/location caches, and page load flights.
/// </summary>
internal sealed class MusicCatalogBrowseService
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

    // Home snapshot & lifecycle
    internal async Task ClearHomeSnapshotAsync(CancellationToken token = default)
    {
        homeSnapshot = null;
        homeContentPages.Clear();
        homeRefreshRequired = true;
        await homeSnapshotStore.ClearAsync(token).ConfigureAwait(false);
    }

    internal Task<(bool HasSnapshot, int PageCount, int SectionCount, int CardCount)> GetHomeSnapshotStatsAsync(CancellationToken token = default) =>
        homeSnapshotStore.GetStatsAsync(token);

    internal Task PrepareStartupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ctx.Stopping) return Task.FromException(new MusicOperationException("CLOSED"));
        if (startupPreparation is null)
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.LoadDrains.Add(drained.Task);
            var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
            startupCancellation = request;
            startupPreparation = Prepare(request, drained);
            _ = Observe(startupPreparation);
        }
        return startupPreparation.WaitAsync(cancellationToken);

        static async Task Observe(Task work) { try { await work; } catch { /* The caller owns startup failure. */ } }
        async Task Prepare(CancellationTokenSource request, TaskCompletionSource drained)
        {
            await Task.Yield();
            try
            {
                await accounts.EnsureAccountsForRequestAsync(request.Token);
                await settings.EnsureCatalogSourceSettingsAsync(request.Token);
                if (!homeSnapshotRead)
                {
                    homeSnapshot = await MusicHomeSnapshotCodec.ReadAsync(
                        homeSnapshotStore, ctx.Application.ReadSongRecord, request.Token);
                    homeSnapshotRead = true;
                }
            }
            catch { startupPreparation = null; throw; }
            finally
            {
                if (ReferenceEquals(startupCancellation, request)) startupCancellation = null;
                request.Dispose();
                ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult();
            }
        }
    }

    internal async Task NavigateHomeAsync(CancellationToken token)
    {
        long submission = ctx.NavigationSubmission;
        try
        {
            await PrepareStartupAsync(token);
            await settings.EnsureCatalogSourceSettingsAsync(token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            if (!ctx.Stopping && submission == ctx.NavigationSubmission)
            {
                if (!settings.CatalogSourceLoaded) PublishCatalogSourceUnavailable("catalog-home");
                else ctx.SetStatus(MusicRuntimeContext.ErrorText(error));
            }
            return;
        }
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || submission != ctx.NavigationSubmission) return;
        var sourcePlatform = settings.CatalogSourcePlatform;
        if (publishedHomeState is null && IsCatalogHome && catalogLoadFlight is { } flight && !flight.Work.IsCompleted &&
            flight.Request.Key.Route == "catalog-home" && flight.Request.Key.Platform == sourcePlatform)
        {
            await SubscribeCatalogLoad(flight, token);
            return;
        }
        RetireCatalogLoad();
        if (publishedHomeState is { } published && homePlatform == sourcePlatform)
        {
            if (!await PublishCatalogAsync("catalog-home", "", homePlatform, "歌曲", homePage, published,
                initializeEntries: !homeRefreshRequired, token: token, restore: true, beforePublish: () =>
                {
                    catalogPages.Clear();
                    catalogCursors.Clear(); catalogCursors.AddRange(homeCursors);
                })) return;
        }
        else if (homeSnapshot is { } saved && saved.Platform == sourcePlatform)
        {
            MusicCatalogSection[] sections = [];
            foreach (var page in saved.Pages)
                sections = MusicCatalogDispatcher.MergeSections(sections, page);
            var state = new CatalogPageState(sections, [], "", "发现音乐", "", false, null,
                sections.Sum(section => section.Items.Count));
            if (!await PublishCatalogAsync("catalog-home", "", saved.Platform, "歌曲", 1, state,
                initializeEntries: false, token: token, beforePublish: () =>
                {
                    catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
                    homePlatform = saved.Platform;
                    homeContentPages.Clear(); homeContentPages.AddRange(saved.Pages);
                })) return;
        }
        if (publishedHomeState is { } current && homePlatform == sourcePlatform)
        {
            catalogPages[homePage] = current;
            if (homeRefreshRequired) StartHomeRefresh();
            else StartHomeContinuation();
            return;
        }
        catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
        homeRefreshRequired = false;
        await LoadCatalogAsync("catalog-home", "", 1, null, token, refresh: true);
    }

    private void StartHomeRefresh()
    {
        if (ctx.Stopping || !IsCatalogHome || !settings.CatalogSourceLoaded || settings.IsSettingsRequested) return;
        homeRefreshRequired = false;
        _ = Refresh();
        async Task Refresh()
        {
            try { await LoadCatalogAsync("catalog-home", "", 1, null, ctx.Lifetime.Token, refresh: true); }
            catch (OperationCanceledException) { }
        }
    }

    private void SaveHomeContent(string platform, int number, MusicCatalogSection[] pageSections)
    {
        if (number == 1) homeContentPages.Clear();
        homeContentPages.Add(pageSections);
        homePlatform = platform;
        homeSnapshot = new(platform, homeContentPages.ToArray());
        homeCursors = catalogCursors.ToArray();
        var saved = homeSnapshot;
        var write = Save();
        ctx.LoadDrains.Add(write);
        _ = Observe();
        async Task Save()
        {
            try { await MusicHomeSnapshotCodec.SaveAsync(homeSnapshotStore, saved, ctx.Lifetime.Token); }
            catch (OperationCanceledException) { }
        }
        async Task Observe()
        {
            try { await write; }
            finally { ctx.LoadDrains.Remove(write); }
        }
    }

    internal void OnCatalogAccountObserved(string platform, long observation)
    {
        if (ctx.Dispatcher.IsClosing) return;
        _ = ctx.Dispatcher.EnqueueAsync(() =>
        {
            if (ctx.Stopping) return;
            optionalReads.RetireForAccount(platform, observation);
            RetireChannelAccountPages(platform, observation);
            changedCatalogAccounts.Add(platform);
            CatalogAccountObserved?.Invoke(platform, observation);
            bool currentRead = catalogLoadFlight is { } flight && flight.Request.Key.Platform == platform &&
                flight.Request.Key.AccountObservation >= observation;
            if (platform == settings.CatalogSourcePlatform &&
                !(currentRead && catalogLoadFlight!.Request.Key.Route == "catalog-home") &&
                publishedHomeState?.AccountStamp != personal.CatalogAccountKey(platform))
                RetireHomeAuthority();
            if (platform == catalogLoadedPlatform && !currentRead && personal.AccountStamp != personal.CatalogAccountKey(platform))
                RetireCatalogContent();
            BeginHomeAccountRecovery(forceRead: false);
        });
    }

    internal void RefreshHomeDisplay()
    {
        if (homePlatform.Length == 0) return;
        bool includeAccountEntries = publishedHomeState?.AccountStamp == personal.CatalogAccountKey(homePlatform) ||
            accounts.HasSettledCurrentAccount(homePlatform);
        var display = homeSections.Select(source =>
        {
            var projected = source.DisplayableCards(includeAccountEntries);
            if (homeProjection.TryGetValue(source, out var existing) &&
                existing.Items.SequenceEqual(projected.Items, ReferenceEqualityComparer.Instance) &&
                ReferenceEquals(existing.Action, projected.Action)) return existing;
            return homeProjection[source] = projected;
        }).ToArray();
        foreach (var obsolete in homeProjection.Keys.Where(source =>
            !homeSections.Contains(source, ReferenceEqualityComparer.Instance)).ToArray())
            homeProjection.Remove(obsolete);
        ReconcileSections(homeDisplaySections, display);
        if (!IsCatalogHome) return;
        optionalReads.RefreshDemandMembers();
        var songs = display.SelectMany(section => section.Items).Select(card => card.Song).OfType<SongRow>().ToArray();
        MusicRuntimeContext.ReconcileSongRows(ctx.ActiveSongs, songs);
        if (ctx.RawStatus is "" or "暂无内容")
            ctx.SetStatus(display.Length == 0 ? "暂无内容" : "");
    }

    private void RetireHomeAuthority()
    {
        optionalReads.Retire("catalog-home");
        homeRefreshRequired = true;
        homePage = 1; homeCursors = [null];
        if (publishedHomeState is { } home)
            publishedHomeState = home with { More = false, Next = null, AccountStamp = "", ExpiresAt = null };
        RefreshHomeDisplay();
    }

    internal void BeginHomeAccountRecovery(bool forceRead)
    {
        if (ctx.Stopping || !settings.CatalogSourceLoaded) return;
        homeAccountRecoveryPending = true;
        homeAccountRecoveryForceRead |= forceRead;
        if (!homeAccountRecovery.IsCompleted) return;
        homeAccountRecovery = Recover();
        ctx.LoadDrains.Add(homeAccountRecovery);
        _ = Drain(homeAccountRecovery);
        async Task Drain(Task work) { try { await work; } finally { ctx.LoadDrains.Remove(work); } }
        async Task Recover()
        {
            await Task.Yield();
            try
            {
                while (!ctx.Stopping && homeAccountRecoveryPending)
                {
                    bool forced = homeAccountRecoveryForceRead;
                    homeAccountRecoveryPending = false;
                    homeAccountRecoveryForceRead = false;
                    if (forced) accounts.MarkAccountsStale();
                    await ctx.CommandTail;
                    if (ctx.Stopping) return;
                    var changed = changedCatalogAccounts.ToArray();
                    changedCatalogAccounts.Clear();
                    await accounts.EnsureAccountsForRequestAsync(ctx.Lifetime.Token);
                    if (ctx.Stopping) return;
                    var account = accounts.Accounts.Single(a => a.Platform == settings.CatalogSourcePlatform);
                    if (forced && account.State is "none" or "revoked") RetireHomeAuthority();
                    if (homeRefreshRequired && IsCatalogHome) StartHomeRefresh();
                    if (changed.Any(platform => accounts.Accounts.Single(a => a.Platform == platform).State == "revoked"))
                        PublishCatalogAccountNotice("AUTH_EXPIRED");
                }
            }
            catch (OperationCanceledException) { }
            catch (MusicOperationException) { /* Preserve the original operation notice. */ }
            finally
            {
                homeAccountRecoveryPending = false;
                homeAccountRecoveryForceRead = false;
            }
        }
    }

    internal void PublishCatalogAccountNotice(string code)
    {
        catalogAccountNotice = MusicCatalogDto.ErrorText(code);
        ctx.NotifyStatusChanged();
    }

    internal void RecoverHomeAfterAuthenticationFailure()
    {
        if (ctx.Stopping || !IsCatalogHome ||
            accounts.Accounts.Single(a => a.Platform == settings.CatalogSourcePlatform).State is not ("saved" or "verified")) return;
        BeginHomeAccountRecovery(forceRead: true);
    }

    // Navigation & browsing operations
    internal Task ChangeCatalogPlatformAsync(MusicCatalogPlatform platform)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogPage || platform == personal.SelectedCatalogPlatform ||
            !personal.PlatformChoicesFor(catalogLoadedPlatform).Contains(platform)) return Task.CompletedTask;
        RememberCatalogLocation();
        personal.SelectedCatalogPlatform = platform;
        if (platform.Key != "tx") personal.CatalogPersonalKind = "all";
        return LoadCatalogAsync(personal.IsCatalogPersonal ? "catalog-personal" : "catalog-home", "", 1, null, ctx.Lifetime.Token);
    }

    internal Task CatalogTagAsync(MusicCatalogTag tag)
    {
        if (ctx.Publishing || !CanCatalogBrowse || tag is null || !CatalogTags.Contains(tag) ||
            !ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal) ||
            tag.Id == catalogTag && ctx.LoadCancellation is null) return Task.CompletedTask;
        return LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, 1, null, ctx.Lifetime.Token, tag: tag.Id);
    }

    internal Task RefreshCatalogAsync()
    {
        if (!IsCatalogPage || ctx.Stopping || personal.IsCatalogDaily && personal.DailyStyleBusy || personal.IsUnsupportedCatalogRoute(ctx.CurrentRoute))
            return Task.CompletedTask;
        ctx.RetireOperationStatus();
        if (personal.IsCatalogDaily) personal.RetireDailyStyleEditor();
        return search.IsSearchPage ? search.BeginSearchAsync(search.SearchKeyword, ctx.Lifetime.Token, refresh: true) :
            LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, 1, null, ctx.Lifetime.Token, refresh: true, tag: catalogTag);
    }

    internal async Task CatalogPageAsync(int direction)
    {
        if (direction is not (-1 or 1) || (direction > 0 ? !CanCatalogNext : !CanCatalogPrevious)) return;
        RememberCatalogLocation();
        int next = catalogPage + direction;
        if (catalogPages.TryGetValue(next, out var cached) && personal.RestoreCatalogPage(cached, catalogLoadedPlatform))
        {
            ctx.AdvanceLoadGeneration();
            catalogActions.Invalidate();
            await PublishCatalogAsync(ctx.CurrentRoute, catalogQuery, catalogLoadedPlatform, catalogLoadedKind, next, cached,
                token: ctx.Lifetime.Token, restore: true);
            return;
        }
        await LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, next, catalogContinuation, ctx.Lifetime.Token, tag: catalogTag);
    }

    internal void SelectCatalogSectionTab(MusicCatalogTab tab)
    {
        var section = CatalogSections.FirstOrDefault(section => section.Tabs?.Contains(tab) == true);
        if (section is null) return;
        section.SelectTab(tab);
        if (IsCatalogHome) RefreshHomeDisplay();
        else optionalReads.RefreshDemandMembers();
    }

    internal async Task CatalogNavigateAsync(string route, CancellationToken token)
    {
        if (route == "catalog-home") { await NavigateHomeAsync(token); return; }
        if (MusicCatalogDispatcher.IsCatalogSourceRoute(route))
        {
            var submission = ctx.NavigationSubmission;
            try { await settings.EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!ctx.Stopping && submission == ctx.NavigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (ctx.Stopping || submission != ctx.NavigationSubmission) return;
            if (route.StartsWith("catalog-channel/", StringComparison.Ordinal) &&
                route.Split('/', 3)[1] != settings.CatalogSourcePlatform)
                route = "catalog-channels";
        }
        if (route == "catalog-channels" || route == lastChannelRoute && channelEntryError.Length > 0)
        {
            if (lastChannelRoute is not null) route = lastChannelRoute;
            if (channelEntryError.Length > 0)
            {
                RetireCatalogLoad();
                var previousSelection = ctx.CaptureContentSelection();
                ctx.SetLocation(new(route), heading: "音乐频道", clearSelection: true, clearTotal: true);
                ctx.SetStatusQuiet(channelEntryError);
                catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
                personal.LoadedTarget = null; catalogMore = false; catalogContinuation = null;
                catalogPage = 1;
                ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings(); return;
            }
        }
        if (route == "catalog-search") { await search.RestoreSearchAsync(token); return; }
        if (!MusicCatalogDispatcher.IsCatalogSourceRoute(route) && !route.Contains('/') && personal.SelectedCatalogPlatform.Key is "kw" or "mg")
            personal.SelectedCatalogPlatform = personal.CatalogPlatforms[0];
        catalogActions.Invalidate();
        var platform = route.Split('/') is { Length: 3 } parts ? parts[1] :
            MusicCatalogDispatcher.IsCatalogSourceRoute(route) ? settings.CatalogSourcePlatform : personal.SelectedCatalogPlatform.Key;
        bool playlist = route.StartsWith("catalog-playlist/", StringComparison.Ordinal);
        if (!playlist && catalogLocations.TryGetValue(route + "/" + platform, out var saved) &&
            saved.Pages.TryGetValue(saved.Page, out var savedPage) &&
            savedPage.Target?.Cacheable != false && personal.RestoreCatalogPage(savedPage, platform))
        {
            token.ThrowIfCancellationRequested();
            RetireCatalogLoad();
            if (!await PublishCatalogAsync(route, saved.Query, platform, saved.Kind, saved.Page, savedPage,
                token: token, restore: true, beforePublish: () =>
                {
                    catalogPages.Clear(); foreach (var p in saved.Pages) catalogPages[p.Key] = p.Value;
                    catalogCursors.Clear(); catalogCursors.AddRange(saved.Cursors);
                    catalogPageCode = saved.PageCode;
                })) return;
            return;
        }
        if (route.StartsWith("catalog-channel/", StringComparison.Ordinal))
            RetireCatalogLocation(route + "/" + platform);
        catalogPageCode = catalogChannelCodes.GetValueOrDefault(route) ?? "";
        await LoadCatalogAsync(route, "", 1, null, token, refresh: playlist);
    }

    internal void CancelCatalog()
    {
        if (ctx.Stopping) return;
        if (search.IsSearchPage) { _ = search.CancelSearchAsync(); return; }
        optionalReads.Retire(ctx.CurrentRoute);
        RetireCatalogLoad();
        catalogActions.Invalidate();
        ctx.SetStatusQuiet("已取消"); ctx.RefreshBindings();
    }

    internal void RetireCatalogSourcePages()
    {
        var previousSelection = ctx.CaptureContentSelection();
        optionalReads.Retire();
        catalogChannelKinds.Clear();
        catalogSourceReload |= IsCatalogLanding && catalogLoadedPlatform.Length > 0;
        if (catalogLoadFlight is { } flight && MusicCatalogDispatcher.IsCatalogSourceRoute(flight.Request.Key.Route))
            RetireCatalogLoad();
        foreach (var key in catalogLocations.Where(pair => MusicCatalogDispatcher.IsCatalogSourceRoute(pair.Value.Route))
            .Select(pair => pair.Key).ToArray())
            RetireCatalogLocation(key);
        foreach (var key in catalogChannelCodes.Keys.Where(key => key.StartsWith("catalog-channel/", StringComparison.Ordinal)).ToArray())
            catalogChannelCodes.Remove(key);
        homeSections.Clear(); homeDisplaySections.Clear(); homeProjection.Clear();
        channelTags.Clear(); CatalogTagGroups = [];
        ChannelTabs.Clear(); VisibleChannelTabs.Clear(); MoreChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
        publishedHomeState = null;
        homeContentPages.Clear(); homePlatform = ""; homeRefreshRequired = true;
        homePage = 1; homeCursors = [null];
        if (IsCatalogLanding)
        {
            ctx.AdvanceLoadGeneration();
            catalogActions.Invalidate();
            ctx.SetLocation(new(IsCatalogHome ? "catalog-home" : "catalog-channels"),
                heading: IsCatalogHome ? "发现音乐" : "音乐频道", clearCatalogSongs: true, clearSelection: true, clearTotal: true);
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = catalogTag = catalogPageCode = catalogQuery = catalogArtwork = "";
            personal.AccountStamp = ""; personal.LoadedTarget = null;
            catalogContinuation = null; catalogMore = false; catalogPage = 1;
        }
        ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
    }

    private void PublishCatalogSourceUnavailable(string route)
    {
        RetireCatalogLoad();
        var previousSelection = ctx.CaptureContentSelection();
        ctx.SetLocation(new(route == "catalog-home" ? "catalog-home" : "catalog-channels"));
        RetireCatalogSourcePages();
        ctx.PublishContentSelection(previousSelection);
        catalogSourceReload = true;
        ctx.SetStatusQuiet(settings.CatalogSourceStatus);
        ctx.RefreshBindings();
    }

    internal Task ActivateCatalogLandingAsync()
    {
        if (ctx.Stopping || !settings.CatalogSourceLoaded || ctx.LoadCancellation is not null) return Task.CompletedTask;
        if (IsCatalogHome && homeRefreshRequired)
            return NavigateHomeAsync(ctx.Lifetime.Token);
        if (!catalogSourceReload) return Task.CompletedTask;
        catalogSourceReload = false;
        if (!IsCatalogLanding || catalogLoadedPlatform == settings.CatalogSourcePlatform)
            return Task.CompletedTask;
        return LoadCatalogAsync(IsCatalogHome ? "catalog-home" : "catalog-channels", "", 1, null, ctx.Lifetime.Token);
    }

    internal void RetireCatalogSession()
    {
        catalogActions.Invalidate();
        RetireCatalogContent();
    }

    private void RetireCatalogContent()
    {
        optionalReads.Retire();
        personal.ClearForSessionRetire();
        search.RetireSearchTracked();
        var wasPublishing = ctx.Publishing; ctx.Publishing = true;
        try
        {
            RetireCatalogLoad();
            RetireHomeAuthority();
            foreach (var key in catalogLocations.Keys.ToArray()) RetireCatalogLocation(key);
            catalogChannelCodes.Clear(); catalogChannelKinds.Clear(); catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogArtwork = catalogSubtitle = ""; catalogSummaryCount = null; catalogMembersLoaded = false;
            catalogTag = catalogPageCode = catalogQuery = ""; catalogContinuation = null; catalogMore = false; catalogPage = 1;
            channelTags.Clear(); utilityTags.Clear(); CatalogTagGroups = [];
            utilitySections.Clear();
            search.ClearSearchSections();
            search.SearchSongsSource.Clear();
            if (IsCatalogPage && !IsCatalogHome) contentState.CatalogSongsSource.Clear();
            ChannelTabs.Clear(); VisibleChannelTabs.Clear(); MoreChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
            if (IsCatalogHome && publishedHomeState is { } home)
                catalogPages[1] = home;
            else if (IsCatalogPage)
            {
                ctx.ActiveSongs.Clear();
                contentState.ClearSelectionForCatalog("音乐内容");
            }
            ctx.RefreshBindings();
        }
        finally { ctx.Publishing = wasPublishing; }
    }

    internal void RetireCatalogLoad()
    {
        ctx.AdvanceLoadGeneration();
        var cancellation = ctx.LoadCancellation;
        ctx.LoadCancellation = null;
        catalogLoadFlight = null;
        CancelCatalogLoad(cancellation);
    }

    private bool RetireCatalogLoad(CatalogLoadFlight flight)
    {
        if (!ReferenceEquals(catalogLoadFlight, flight) || flight.Generation != ctx.LoadGeneration)
        {
            CancelCatalogLoad(flight.Cancellation);
            return false;
        }
        ctx.AdvanceLoadGeneration();
        catalogLoadFlight = null;
        if (ReferenceEquals(ctx.LoadCancellation, flight.Cancellation)) ctx.LoadCancellation = null;
        CancelCatalogLoad(flight.Cancellation);
        return true;
    }

    internal static void CancelCatalogLoad(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal async Task LoadCatalogAsync(string route, string query, int pageNumber, JsonElement? continuation,
        CancellationToken token, bool refresh = false, string tag = "")
    {
        if (ctx.Stopping || personal.IsUnsupportedCatalogRoute(route)) return;
        token.ThrowIfCancellationRequested();
        if (MusicCatalogDispatcher.IsCatalogSourceRoute(route) && !settings.CatalogSourceLoaded)
        {
            var submission = ctx.NavigationSubmission;
            try { await settings.EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!ctx.Stopping && submission == ctx.NavigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (ctx.Stopping || submission != ctx.NavigationSubmission) return;
        }
        var request = CaptureCatalogLoadRequest(route, query, pageNumber, continuation, refresh, tag);
        if (catalogLoadFlight is { } current && !current.Work.IsCompleted &&
            current.Generation == ctx.LoadGeneration && !current.Cancellation.IsCancellationRequested &&
            current.Request.Key == request.Key)
        {
            await SubscribeCatalogLoad(current, token);
            return;
        }

        if (route != "catalog-home" || !IsCatalogHome || catalogLoadedPlatform != request.Key.Platform)
            catalogActions.Invalidate();
        var generation = ctx.AdvanceLoadGeneration();
        var stamp = ctx.NextStatusEpoch();
        ctx.LoadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
        var flight = new CatalogLoadFlight(request, generation, stamp, cancellation);
        ctx.LoadCancellation = cancellation;
        catalogLoadFlight = flight;
        ctx.ClearStatusQuiet();
        var previousSelection = ctx.CaptureContentSelection();
        if ((ctx.CurrentRoute != route || catalogLoadedPlatform != request.Key.Platform) &&
            !(IsCatalogHome && request.Target?.Private == true && request.Target.Operation is not ("daily" or "playlist")))
        {
            personal.RetireDailyStyleEditor();
            ctx.SetLocation(new(route),
                heading: request.Target?.Title ?? (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) ? "歌单"
                    : route is "catalog-daily" || route.StartsWith("catalog-daily/", StringComparison.Ordinal) ? "每日推荐" : "音乐内容"),
                clearCatalogSongs: true, clearSelection: true, clearTotal: true);
            if (request.Target?.Operation == "daily") personal.CatalogDailyBranch = request.Target.Params.Branch ?? "";
            if (IsChannelLanding) CatalogTagGroups = [];
            else if (!IsCatalogHome) { utilitySections.Clear(); }
            CatalogTags.Clear();
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = request.Key.Platform; personal.LoadedTarget = request.Target;
            catalogPage = 1; catalogMore = false; catalogContinuation = null;
            catalogTag = catalogQuery = "";
            catalogArtwork = request.Target?.Artwork ?? "";
            catalogSubtitle = request.Target?.Subtitle ?? "";
            catalogSummaryCount = request.Target?.Count; catalogMembersLoaded = false;
        }
        if (IsChannelLanding && ctx.CurrentRoute == route) SelectChannelPage(route, request.Key.Platform);
        ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
        flight.Work = RunCatalogFlightAsync(flight);
        ctx.LoadDrains.Add(flight.Work);
        _ = ObserveCatalogLoadAsync(flight);
        await SubscribeCatalogLoad(flight, token);
    }

    private CatalogLoadRequest CaptureCatalogLoadRequest(string route, string query, int number,
        JsonElement? continuation, bool refresh, string tag)
    {
        var platform = MusicCatalogDispatcher.IsCatalogSourceRoute(route) ? settings.CatalogSourcePlatform : personal.SelectedCatalogPlatform.Key;
        var kind = search.SearchKindLabel;
        var routeParts = route.Split('/', 3);
        if (routeParts.Length == 3) platform = routeParts[1];
        var target = personal.CatalogRouteTarget(route, platform);
        if (target is not null) platform = target.Platform;
        var cursor = continuation?.Clone();
        var key = new CatalogLoadKey(route, query, number, cursor?.GetRawText() ?? "", refresh, tag,
            platform, kind, catalogPageCode, accounts.AccountEpoch, accounts.PlatformObservation(platform),
            target?.Operation ?? "", target?.Platform ?? "", target?.Title ?? "",
            target?.Params.CanonicalKey() ?? "");
        return new(key, cursor, target);
    }

    private Task SubscribeCatalogLoad(CatalogLoadFlight flight, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref flight.Subscribers);
        return WaitAsync();

        async Task WaitAsync()
        {
            var callerCancelled = false;
            try
            {
                if (cancellationToken.CanBeCanceled)
                    await flight.Work.WaitAsync(cancellationToken);
                else
                    await flight.Work;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref flight.Subscribers) == 0 &&
                    callerCancelled && !flight.Work.IsCompleted)
                {
                    if (RetireCatalogLoad(flight) && !ctx.Disposed)
                        ctx.RefreshBindings();
                }
            }
        }
    }

    private async Task ObserveCatalogLoadAsync(CatalogLoadFlight flight)
    {
        try { await flight.Work; }
        catch { /* The active subscriber observes unexpected owner failures. */ }
        finally { ctx.LoadDrains.Remove(flight.Work); }
    }

    private async Task RunCatalogFlightAsync(CatalogLoadFlight flight)
    {
        await Task.Yield(); // Publish the unique request owner before any port can complete synchronously.
        try { await LoadCatalogCoreAsync(flight); }
        finally
        {
            if (ReferenceEquals(catalogLoadFlight, flight)) catalogLoadFlight = null;
            if (ReferenceEquals(ctx.LoadCancellation, flight.Cancellation))
            {
                ctx.LoadCancellation = null;
                if (!ctx.Disposed) ctx.RefreshBindings();
            }
            flight.Cancellation.Dispose();
            if (!ctx.Stopping && flight.Published && flight.Generation == ctx.LoadGeneration) StartHomeContinuation();
        }
    }

    private async Task ReadCatalogAccountForRequestAsync(long generation, CancellationToken token)
    {
        long epoch = accounts.AccountEpoch;
        await accounts.EnsureAccountsForRequestAsync(token);
        token.ThrowIfCancellationRequested();
        if (ctx.Stopping || generation != ctx.LoadGeneration || epoch != accounts.AccountEpoch) throw new OperationCanceledException(token);
    }

    private async Task LoadCatalogCoreAsync(CatalogLoadFlight flight)
    {
        var request = flight.Request;
        var key = request.Key;
        var generation = flight.Generation;
        var stamp = flight.StatusStamp;
        var token = flight.Cancellation.Token;
        var route = key.Route;
        var query = key.Query;
        var number = key.Number;
        var continuation = request.Continuation;
        var refresh = key.Refresh;
        var tag = key.Tag;
        var platform = key.Platform;
        var kind = key.Kind;
        var target = request.Target;
        try
        {
            MusicDto.Require(personal.CatalogPlatforms.Any(item => item.Key == platform) && number >= 1);
            if (target?.Private == true) await ReadCatalogAccountForRequestAsync(generation, token);
            else if (platform is "wy" or "tx" or "kg") await accounts.EnsureAccountsForRequestAsync(token);
            token.ThrowIfCancellationRequested();
            if (ctx.Stopping || generation != ctx.LoadGeneration || key.AccountObservation != accounts.PlatformObservation(platform)) return;
            var accountStamp = personal.CatalogAccountKey(platform);
            string operation, title;
            string? routeId = null;
            if (target is not null && target.Operation != "playlist")
            {
                operation = target.Operation; title = target.Title;
            }
            else if (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) || route.StartsWith("catalog-channel/", StringComparison.Ordinal))
            {
                var parts = route.Split('/', 3);
                MusicDto.Require(parts.Length == 3 && personal.CatalogPlatforms.Any(item => item.Key == parts[1]));
                platform = parts[1];
                routeId = Uri.UnescapeDataString(parts[2]);
                operation = parts[0] == "catalog-playlist" ? "playlist" : "channel";
                title = operation == "playlist" ? "歌单" : "频道";
            }
            else
            {
                (operation, title) = route switch
                {
                    "catalog-home" => ("home", "发现音乐"),
                    "catalog-channels" => ("channels", "音乐频道"),
                    _ => throw new MusicOperationException("INVALID_REQUEST")
                };
            }
            var reply = await MusicCatalogDispatcher.InvokeOperationAsync(
                ctx.Application, operation, platform, target, routeId, tag, key.PageCode, number, continuation, refresh, token);
            token.ThrowIfCancellationRequested();
            if (ctx.Stopping || generation != ctx.LoadGeneration || key.AccountObservation != accounts.PlatformObservation(platform)) return;
            if (accountStamp != personal.CatalogAccountKey(platform)) throw new MusicOperationException("STALE_SESSION");
            var pageSections = MusicCatalogDto.Read(reply, platform, operation, title, kind == "歌单" ? "playlists" : "songs").ToArray();
            var sections = pageSections;
            bool more = MusicCatalogDto.Boolean(reply, "more", false);
            var c = MusicCatalogDto.Field(reply, "continuation");
            JsonElement? next = c.ValueKind == JsonValueKind.Object ? c.Clone() : null;
            bool continuationBlocked = more && (next is null ||
                catalogCursors.Take(number).Any(old => MusicCatalogDto.CursorKey(old) == MusicCatalogDto.CursorKey(next)));
            if (number > 1)
            {
                if (operation is "home" or "channel" or "playlist")
                    sections = MusicCatalogDispatcher.MergeSections(CatalogSections, pageSections);
            }
            var tags = target?.Operation == "daily" ? Array.Empty<MusicCatalogTag>() : MusicCatalogDto.Tags(reply).ToArray();
            var selected = MusicCatalogDto.Text(reply, "selectedTag");
            var name = MusicCatalogDto.Text(reply, operation is "home" or "channel" or "artistActivity" or "chart" or "charts" ? "title" : "name");
            var state = new CatalogPageState(sections, tags, selected, name.Length > 0 ? name : title,
                MusicCatalogDto.Text(reply, "cover"), more, next,
                MusicCatalogDto.Number(reply, "total") ?? sections.Sum(s => s.Items.Count),
                Scroll: number > 1 && operation is ("home" or "channel" or "playlist") ? ctx.CaptureScroll() : 0,
                Target: target, AccountStamp: accountStamp,
                ExpiresAt: MusicCatalogDispatcher.CatalogExpiry(reply),
                DailySelection: target?.Operation == "daily" ? MusicCatalogDispatcher.DailySelectionSnapshot(reply) : null)
                { ContinuationBlocked = continuationBlocked };
            if (number > 1 && catalogPages.TryGetValue(catalogPage, out var previousPage))
                state = state with { OptionalReads = previousPage.OptionalReads };
            if (operation == "playlist")
                optionalReads.RememberCollectionMetadata(platform, MusicCatalogDto.Text(reply, "id"), name, state.Artwork,
                    MusicCatalogDto.LongNumber(reply, "playCount"));
            if (operation == "channels")
            {
                ChannelTabs.Clear();
                foreach (var card in sections.SelectMany(s => s.Items).Where(c => c.Kind == "channel" && c.Display))
                    ChannelTabs.Add(card);
                UpdateChannelTabs();
                var first = VisibleChannelTabs.Concat(MoreChannelTabs).FirstOrDefault(c => c.CanOpen);
                if (first is not null)
                {
                    lastChannelRoute = "catalog-channel/" + first.Platform + "/" + Uri.EscapeDataString(first.ActionId);
                    catalogChannelCodes[lastChannelRoute] = first.PageCode;
                    catalogChannelKinds[lastChannelRoute] = first.ChannelKind;
                    catalogPageCode = first.PageCode;
                    var previousSelection = ctx.CaptureContentSelection();
                    ctx.SetLocation(new(lastChannelRoute));
                    catalogLoadedPlatform = first.Platform;
                    SelectChannelPage(lastChannelRoute, first.Platform);
                    ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
                    var channelKey = key with { Route = lastChannelRoute, Platform = first.Platform, PageCode = first.PageCode };
                    await LoadCatalogCoreAsync(new CatalogLoadFlight(new(channelKey, null, null), generation, stamp, flight.Cancellation));
                    return;
                }
            }
            if (operation == "channels") state = state with { Sections = [], Total = 0 };
            if (!await PublishCatalogAsync(route, query, platform, kind, number, state,
                append: operation == "home" && number > 1, token: token,
                isCurrent: () => generation == ctx.LoadGeneration && key.AccountObservation == accounts.PlatformObservation(platform),
                beforePublish: () =>
                {
                    if (number == 1) { catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null); }
                    else if (operation is "playlist" or "home") catalogPages.Clear();
                    while (catalogCursors.Count <= number) catalogCursors.Add(null);
                    catalogCursors[number] = next; catalogPages[number] = state;
                })) return;
            if (operation == "home") SaveHomeContent(platform, number, pageSections);
            flight.Published = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!ctx.Stopping && generation == ctx.LoadGeneration && stamp == ctx.StatusEpoch &&
                key.AccountObservation == accounts.PlatformObservation(platform))
            {
                if (!IsCatalogHome && error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "SESSION_CHANGED" })
                    RetireCatalogSession();
                var message = MusicCatalogDto.ErrorText(error is MusicOperationException failure ? failure.Code : "INVALID_RESPONSE");
                ctx.SetStatusQuiet(message);
                if (route == "catalog-channels" || route == lastChannelRoute && channelSections.Count == 0)
                    channelEntryError = message;
                if (error is MusicOperationException { Code: "PAGINATION_STALLED" })
                {
                    catalogMore = false;
                    if (catalogPages.TryGetValue(catalogPage, out var prior))
                        catalogPages[catalogPage] = prior with { More = false };
                }
                ctx.Publishing = true;
                try
                {
                    personal.SelectedCatalogPlatform = personal.CatalogPlatforms.FirstOrDefault(p => p.Key == catalogLoadedPlatform) ?? personal.SelectedCatalogPlatform;
                    search.SearchKindLabel = catalogLoadedKind;
                    if (catalogPages.TryGetValue(catalogPage, out var previous)) personal.PublishPersonalCatalog(previous);
                    ctx.NotifyStatusChanged(); catalogBindings();
                }
                finally { ctx.Publishing = false; }
                if (error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" })
                {
                    PublishCatalogAccountNotice(((MusicOperationException)error).Code);
                    RecoverHomeAfterAuthenticationFailure();
                }
            }
        }
    }

    private void StartHomeContinuation()
    {
        if (ctx.Stopping || !IsCatalogHome || !catalogMore || ctx.LoadCancellation is not null ||
            homeContinuationCancellation is not null) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
        homeContinuationCancellation = request;
        notify(nameof(MusicCatalogController.CanCancelCatalog));
        var generation = ctx.LoadGeneration;
        var work = Read();
        ctx.LoadDrains.Add(work);
        _ = Drain(work);
        async Task Drain(Task task) { try { await task; } finally { ctx.LoadDrains.Remove(task); } }
        async Task Read()
        {
            await Task.Yield();
            try
            {
                while (!ctx.Stopping && generation == ctx.LoadGeneration && !request.IsCancellationRequested && IsCatalogHome && catalogMore)
                {
                    int previous = catalogPage;
                    var next = CaptureCatalogLoadRequest(ctx.CurrentRoute, catalogQuery, previous + 1, catalogContinuation, true, catalogTag);
                    await LoadCatalogCoreAsync(new(next, generation, ctx.StatusEpoch, request));
                    if (catalogPage == previous) break;
                }
            }
            finally
            {
                if (ReferenceEquals(homeContinuationCancellation, request)) homeContinuationCancellation = null;
                request.Dispose();
                if (!ctx.Disposed) catalogBindings();
            }
        }
    }

    private static void ReconcileSections(ObservableCollection<MusicCatalogSection> target, MusicCatalogSection[] incoming)
    {
        for (int index = 0; index < incoming.Length; index++)
        {
            if (index == target.Count) target.Add(incoming[index]);
            else if (!ReferenceEquals(target[index], incoming[index])) target[index] = incoming[index];
        }
        while (target.Count > incoming.Length) target.RemoveAt(target.Count - 1);
    }

    internal async Task<bool> PublishCatalogAsync(string route, string query, string platform, string kind, int number, CatalogPageState state,
        bool append = false, bool initializeEntries = true, CancellationToken token = default,
        Func<bool>? isCurrent = null, Action? beforePublish = null, bool restore = false)
    {
        long generation = ctx.LoadGeneration, submission = ctx.NavigationSubmission;
        long observation = accounts.PlatformObservation(platform);
        string account = personal.CatalogAccountKey(platform);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.LoadDrains.Add(drained.Task);
        bool Current() => !ctx.Stopping && generation == ctx.LoadGeneration && submission == ctx.NavigationSubmission &&
            observation == accounts.PlatformObservation(platform) && account == personal.CatalogAccountKey(platform) &&
            isCurrent?.Invoke() != false;
        try
        {
            if (!restore) await RefreshCatalogSongsAsync(RepositoryCards(state.Sections), token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return false;
            beforePublish?.Invoke();
            Publish();
            return true;
        }
        catch (MusicOperationException error) when (error.Code is "STORAGE_UNAVAILABLE" or "CORRUPT_STORAGE" or "STORAGE")
        {
            if (Current()) { ctx.SetStatus(MusicCatalogDto.ErrorText(error.Code)); catalogBindings(); }
            return false;
        }
        finally { ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult(); }

        void Publish()
        {
            ctx.Publishing = true;
            try
            {
                var previousSelection = ctx.CaptureContentSelection();
                ctx.SetLocation(new(route), heading: state.Title, clearCollection: true);
                if (route.StartsWith("catalog-channel/", StringComparison.Ordinal))
                { lastChannelRoute = route; channelEntryError = ""; }
                catalogQuery = query; catalogLoadedPlatform = platform; catalogLoadedKind = kind; search.SearchKindLabel = kind;
                catalogPage = number; catalogMore = state.More && !state.ContinuationBlocked; catalogContinuation = state.Next;
                catalogTag = state.Tag;
                catalogArtwork = state.Artwork.Length > 0 ? state.Artwork : state.Target?.Artwork ?? "";
                catalogSubtitle = state.Target?.Subtitle ?? "";
                catalogSummaryCount = state.Total; catalogMembersLoaded = true;
                personal.PublishPersonalCatalog(state);
                if (state.DailySelection is { } daily) personal.PublishDailyStyleSelection(daily);
                personal.SelectedCatalogPlatform = personal.CatalogPlatforms.Single(p => p.Key == platform);
                if (IsChannelLanding) SelectChannelPage(route, platform);
                var sectionsToPublish = state.Sections;
                if (!IsCatalogHome)
                {
                    var songs = state.Sections.SelectMany(s => s.Items).Select(i => i.Song).OfType<SongRow>().ToArray();
                    MusicRuntimeContext.ReconcileSongRows(ctx.ActiveSongs, songs);
                    if (songs.Length > 0 && songs.Length == ctx.ActiveSongs.Count)
                    {
                        int songIndex = 0;
                        bool anyReplaced = false;
                        var aligned = new MusicCatalogSection[state.Sections.Length];
                        for (int s = 0; s < state.Sections.Length; s++)
                        {
                            var section = state.Sections[s];
                            MusicCatalogCard[]? items = null;
                            for (int i = 0; i < section.Items.Count; i++)
                            {
                                var item = section.Items[i];
                                if (item.Song is null) continue;
                                var reconciled = ctx.ActiveSongs[songIndex++];
                                if (!ReferenceEquals(item.Song, reconciled))
                                {
                                    items ??= section.Items.ToArray();
                                    items[i] = item with { Song = reconciled };
                                    anyReplaced = true;
                                }
                            }
                            aligned[s] = items is not null ? section with { Items = items } : section;
                        }
                        if (anyReplaced)
                        {
                            sectionsToPublish = aligned;
                            if (catalogPages.TryGetValue(number, out var stored) && ReferenceEquals(stored, state))
                                catalogPages[number] = state = state with { Sections = aligned };
                        }
                    }
                }
                if (!CatalogSections.SequenceEqual(sectionsToPublish, ReferenceEqualityComparer.Instance))
                    ReconcileSections(CatalogSections, sectionsToPublish);
                if (IsCatalogHome)
                {
                    publishedHomeState = state; homePage = number; homePlatform = platform;
                    RefreshHomeDisplay();
                }
                if (!CatalogTags.SequenceEqual(state.Tags))
                {
                    CatalogTags.Clear(); foreach (var tag in state.Tags) CatalogTags.Add(tag);
                    if (IsChannelLanding)
                        CatalogTagGroups = channelTags.GroupBy(tag => tag.Group, StringComparer.Ordinal)
                            .Select(group => new MusicCatalogTagGroup(group.Key, group.ToArray())).ToArray();
                }
                contentState.PublishCatalogSummary(state.Total, !append);
                var visibleCount = IsCatalogHome
                    ? homeDisplaySections.Sum(section => section.Items.Count)
                    : state.Sections.Sum(section => section.Items.Count);
                if (!append) ctx.SetStatusQuiet(visibleCount == 0 ? "暂无内容" : "");
                if (state.ContinuationBlocked) ctx.SetStatusQuiet(MusicCatalogDto.ErrorText("PAGINATION_STALLED"));
                ctx.PublishContentSelection(previousSelection);
                contentState.NotifySelectionChanged(); ctx.RefreshBindings();
            }
            finally { ctx.Publishing = false; }
            state.OptionalReads = optionalReads.Activate(route, platform, state.OptionalReads, initializeEntries);
            if (append) return;
            _ = ctx.Dispatcher.EnqueueAsync(() => { if (!ctx.Stopping && generation == ctx.LoadGeneration) ctx.RestoreScroll(state.Scroll); }, DispatcherQueuePriority.Low);
        }
    }
}
