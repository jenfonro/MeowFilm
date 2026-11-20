using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Threading;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private static readonly MusicCatalogPlatform NeteasePlatform = new("wy", "网易云音乐");
    private static readonly MusicCatalogPlatform KuwoPlatform = new("kw", "酷我音乐");
    private static readonly MusicCatalogPlatform MiguPlatform = new("mg", "咪咕音乐");
    private static readonly IReadOnlyList<MusicCatalogPlatform> StandardCatalogChoices = Array.AsReadOnly<MusicCatalogPlatform>(
        [NeteasePlatform, new("tx", "QQ 音乐"), new("kg", "酷狗音乐")]);
    private static readonly IReadOnlyList<MusicCatalogPlatform> KuwoPlatformChoices =
        Array.AsReadOnly<MusicCatalogPlatform>([.. StandardCatalogChoices, KuwoPlatform]);
    private static readonly IReadOnlyList<MusicCatalogPlatform> MiguPlatformChoices =
        Array.AsReadOnly<MusicCatalogPlatform>([.. StandardCatalogChoices, MiguPlatform]);

    public IReadOnlyList<MusicCatalogPlatform> CatalogPlatforms { get; } =
        Array.AsReadOnly<MusicCatalogPlatform>([.. StandardCatalogChoices, KuwoPlatform, MiguPlatform]);
    public IReadOnlyList<MusicCatalogPlatform> CatalogPlatformChoices => catalogLoadedPlatform switch
    {
        "kw" => KuwoPlatformChoices,
        "mg" => MiguPlatformChoices,
        _ => StandardCatalogChoices
    };
    public MusicCatalogPlatform SelectedCatalogPlatform { get; internal set; } = NeteasePlatform;
    public string[] CatalogSearchKinds { get; } = ["歌曲", "歌单"];
    public string CatalogSearchKind { get; set; } = "歌曲";
    private readonly ObservableCollection<MusicCatalogSection> homeSections = [], channelSections = [], utilitySections = [];
    private readonly Dictionary<string, ObservableCollection<MusicCatalogSection>> searchSections = new()
    { [SearchOverview] = [], ["songs"] = [], ["playlists"] = [] };
    public ObservableCollection<MusicCatalogSection> CatalogSections => IsSearchPage ? searchSections[searchView] :
        IsCatalogHome ? homeSections : IsChannelLanding ? channelSections : utilitySections;
    public ObservableCollection<MusicCatalogSection> HomeSections => homeDisplaySections;
    internal Func<Task<Action?>>? PrepareHomeReplacement { get; set; }
    public ObservableCollection<MusicCatalogSection> ChannelSections => channelSections;
    public ObservableCollection<MusicCatalogSection> SearchOverviewSections => searchSections[SearchOverview];
    public ObservableCollection<MusicCatalogSection> UtilitySections => utilitySections;
    public bool IsUtilityCatalogSurface => IsCatalogContent && !IsCatalogHome && !IsChannelLanding && !IsSearchPage;
    public bool ShowCatalogSkeleton => IsCatalogContent && !IsSearchPage && loadCancellation is not null && CatalogSections.Count == 0;
    public bool ShowSearchSkeleton => IsSearchPage && (IsSearchOverview ? CatalogSections.All(s => s.Items.Count == 0) :
        IsSearchSongs ? Songs.Count == 0 : CatalogSections.All(s => s.Items.Count == 0)) &&
        (IsSearchOverview ? SearchSongsLoading || SearchPlaylistsLoading : SearchKindLoading(searchKind));
    private string? lastChannelRoute;
    private string channelEntryError = "";
    private bool catalogSourceReload;
    private CatalogPageState? publishedHomeState, publishedChannelState;
    private readonly ObservableCollection<MusicCatalogSection> homeDisplaySections = [];
    private readonly Dictionary<MusicCatalogSection, MusicCatalogSection> homeProjection = new(ReferenceEqualityComparer.Instance);
    public ObservableCollection<MusicCatalogSection> DisplayCatalogSections => IsCatalogHome ? homeDisplaySections : CatalogSections;
    public bool IsCatalogHome => location.Route == "catalog-home";
    public ObservableCollection<MusicCatalogCard> ChannelTabs { get; } = [];
    public bool IsChannelDirectory => location.Route == "catalog-channels";
    public bool IsVipChannel => IsChannelLanding && catalogChannelKinds.GetValueOrDefault(location.Route) == "vip";
    public string CurrentChannelKey => location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal) ? location.Route[16..] : "";
    public IEnumerable<MusicCatalogCard> VisibleChannelTabs =>
        ChannelTabs.Where(card => card.Placement == "primary");
    public IEnumerable<MusicCatalogCard> MoreChannelTabs =>
        ChannelTabs.Where(card => card.Placement == "more");
    public bool HasMoreChannelTabs => MoreChannelTabs.Any();
    public bool HasChannelTabs => IsChannelLanding && ChannelTabs.Count > 0;
    public bool IsCatalogUtilityPage => IsCatalogBrowsePage && !IsCatalogLanding && !IsCatalogDetailPage;
    public bool IsCatalogLanding => location.Route is "catalog-home" or "catalog-channels" || IsChannelLanding;
    public bool IsChannelLanding => location.Route == "catalog-channels" || location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal);
    private readonly ObservableCollection<MusicCatalogTag> channelTags = [], utilityTags = [];
    public ObservableCollection<MusicCatalogTag> CatalogTags => IsChannelLanding ? channelTags : utilityTags;
    public IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups { get; private set; } = [];
    public MusicCatalogTag? SelectedCatalogTag => CatalogTags.FirstOrDefault(tag => tag.Selected);
    public bool IsCatalogPage => location.Route.StartsWith("catalog-", StringComparison.Ordinal);
    public bool IsCatalogPlaylist => location.Route.StartsWith("catalog-playlist/", StringComparison.Ordinal);
    public bool IsCatalogArtist => location.Route.StartsWith("catalog-artist/", StringComparison.Ordinal);
    public bool IsCatalogDetailPage => IsCatalogPlaylist || IsCatalogArtist || IsCatalogDaily;
    public bool IsLocalLibraryPage => IsSongList || IsArchives;
    public bool CanCatalogBrowse => !stopping && commands == 0;
    public bool CanCatalogNext => !IsCatalogHome && CanCatalogBrowse && loadCancellation is null && catalogMore;
    public bool CanCatalogPrevious => CanCatalogBrowse && loadCancellation is null && !IsCatalogHome && !IsCatalogDetailPage && catalogPage > 1;
    public bool CanLoadMoreCatalogPlaylist => IsCatalogPlaylist && CanCatalogNext;
    public bool HasCatalogTags => CatalogTags.Count > 0 && location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal);
    public bool CanCatalogAction => CanCatalogBrowse && loadCancellation is null && IsCatalogCollection &&
        (!IsDailyStyle || !dailyStyleBusy && !dailyStyleNeedsRead && catalogAccountStamp.Length > 0);
    public bool CanPlayCatalogPlaylist => CanCatalogAction && total > 0;
    public bool CanCancelCatalog => loadCancellation is not null || homeContinuationCancellation is not null || catalogActionCancellation is not null;
    public string CatalogPageText => IsCatalogPage ? $"第 {catalogPage} 页" : "";
    private bool catalogMore;
    private int catalogPage = 1;
    private JsonElement? catalogContinuation;
    private readonly List<JsonElement?> catalogCursors = [null];
    private string catalogQuery = "", catalogLoadedPlatform = "", catalogTag = "", catalogPageCode = "",
        catalogArtwork = "";
    private string catalogLoadedKind = "歌曲";
    private int? catalogSummaryCount;
    private bool catalogMembersLoaded;
    private string catalogSubtitle = "";
    public bool ShowDetailSkeleton => IsCatalogDetailPage && loadCancellation is not null && !catalogMembersLoaded;
    public string DetailSubtitle => IsCatalogDetailPage ? catalogSubtitle : "";
    private readonly Dictionary<string, CatalogLocation> catalogLocations = [];
    private readonly Dictionary<string, string> catalogChannelCodes = [];
    private readonly Dictionary<string, string> catalogChannelKinds = [];
    private readonly Dictionary<int, CatalogPageState> catalogPages = [];
    private sealed record CatalogPageState(MusicCatalogSection[] Sections, MusicCatalogTag[] Tags,
        string Tag, string Title, string Artwork, bool More, JsonElement? Next, int Total, double Scroll = 0,
        CatalogTarget? Target = null, string AccountStamp = "", long? ExpiresAt = null, JsonElement? DailySelection = null)
    {
        public CatalogOptionalReads OptionalReads { get; init; } = new();
        public bool ContinuationBlocked { get; init; }
    }
    private sealed record CatalogLocation(string Route, string Platform, string Query, string Kind, string PageCode,
        int Page, Dictionary<int, CatalogPageState> Pages, JsonElement?[] Cursors);
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
    private CatalogLoadFlight? catalogLoadFlight;

    public void CatalogBindings()
    {
        foreach (var name in new[] { nameof(CatalogSections), nameof(Songs), nameof(IsUtilityCatalogSurface), nameof(ShowCatalogSkeleton), nameof(DisplayCatalogSections), nameof(IsCatalogHome), nameof(IsCatalogPage), nameof(IsChannelDirectory), nameof(IsVipChannel), nameof(CurrentChannelKey), nameof(VisibleChannelTabs), nameof(MoreChannelTabs), nameof(HasMoreChannelTabs), nameof(IsCatalogLanding), nameof(IsCatalogUtilityPage), nameof(IsChannelLanding), nameof(HasChannelTabs), nameof(IsLocalLibraryPage), nameof(IsCatalogPlaylist), nameof(IsCatalogArtist), nameof(IsCatalogDetailPage),
            nameof(CanCatalogNext), nameof(CanCatalogPrevious), nameof(CanCatalogBrowse), nameof(HasCatalogTags), nameof(CatalogTagGroups),
            nameof(CanCatalogAction), nameof(CanPlayCatalogPlaylist), nameof(CanCancelCatalog), nameof(CatalogPageText), nameof(SelectedCatalogTag),
            nameof(CatalogPlatformChoices), nameof(SelectedCatalogPlatform), nameof(CatalogSearchKind),
            nameof(IsContentDetailPage), nameof(ShowsSongTable), nameof(DetailArtwork), nameof(DetailCountText),
            nameof(ShowDetailSkeleton), nameof(DetailSubtitle), nameof(DetailSectionTitle), nameof(DetailArtworkName), nameof(CanPlayContentDetail),
            nameof(CanLoadMoreCatalogPlaylist) }) Changed(name);
        DailyStyleBindings();
        PersonalCatalogBindings();
        NetworkPlaylistBindings();
        SearchBindings();
    }
    private void RememberCatalogLocation()
    {
        if (IsDiscovery) return;
        if (IsSearchPage) { RememberSearch(); return; }
        if (!IsCatalogPage || !catalogPages.TryGetValue(catalogPage, out var state)) return;
        catalogPages[catalogPage] = state with { Scroll = Presentation.CaptureScroll() };
        if (IsCatalogHome)
        {
            publishedHomeState = catalogPages[catalogPage];
            homePage = catalogPage; homeCursors = catalogCursors.ToArray();
            return;
        }
        var key = location.Route + "/" + catalogLoadedPlatform;
        catalogLocations.Remove(key);
        catalogLocations[key] = new(location.Route, catalogLoadedPlatform, catalogQuery, catalogLoadedKind,
            catalogPageCode, catalogPage, new(catalogPages), catalogCursors.ToArray());
        while (catalogLocations.Count > 8)
        {
            var retired = catalogLocations.FirstOrDefault(pair => pair.Value.Route != lastChannelRoute);
            if (retired.Key is null) break;
            catalogLocations.Remove(retired.Key);
        }
    }
    internal Task ChangeCatalogPlatformAsync(MusicCatalogPlatform platform)
    {
        if (publishing || !CanCatalogBrowse || !IsCatalogPage || platform == SelectedCatalogPlatform ||
            !CatalogPlatformChoices.Contains(platform)) return Task.CompletedTask;
        RememberCatalogLocation();
        SelectedCatalogPlatform = platform;
        if (platform.Key != "tx") CatalogPersonalKind = "all";
        return LoadCatalogAsync(IsCatalogPersonal ? "catalog-personal" : "catalog-home", "", 1, null, lifetime.Token);
    }
    public Task CatalogSearchKindAsync() => IsSearchPage ? SwitchSearchKindAsync() :
        publishing || !CanCatalogBrowse || location.Route != "catalog-search" || catalogQuery.Length == 0 ||
        CatalogSearchKind == catalogLoadedKind && loadCancellation is null ? Task.CompletedTask :
        LoadCatalogAsync("catalog-search", catalogQuery, 1, null, lifetime.Token);
    public Task CatalogTagAsync(MusicCatalogTag tag)
    {
        if (publishing || !CanCatalogBrowse || tag is null || !CatalogTags.Contains(tag) ||
            !location.Route.StartsWith("catalog-channel/", StringComparison.Ordinal) ||
            tag.Id == catalogTag && loadCancellation is null) return Task.CompletedTask;
        return LoadCatalogAsync(location.Route, catalogQuery, 1, null, lifetime.Token, tag: tag.Id);
    }
    public Task RefreshCatalogAsync()
    {
        if (!IsCatalogPage || stopping || IsCatalogDaily && dailyStyleBusy || IsUnsupportedCatalogRoute(location.Route)) return Task.CompletedTask;
        RetireOperationStatus();
        if (IsCatalogDaily) RetireDailyStyleEditor();
        return IsSearchPage ? BeginSearchAsync(searchKeyword, lifetime.Token, refresh: true) :
            LoadCatalogAsync(location.Route, catalogQuery, 1, null, lifetime.Token, refresh: true, tag: catalogTag);
    }
    public async Task CatalogPageAsync(int direction)
    {
        if (direction is not (-1 or 1) || (direction > 0 ? !CanCatalogNext : !CanCatalogPrevious)) return;
        RememberCatalogLocation();
        int next = catalogPage + direction;
        if (catalogPages.TryGetValue(next, out var cached) && RestoreCatalogPage(cached, catalogLoadedPlatform))
        {
            AdvanceLoadGeneration();
            InvalidateCatalogAction();
            await PublishCatalogAsync(location.Route, catalogQuery, catalogLoadedPlatform, catalogLoadedKind, next, cached,
                token: lifetime.Token);
            return;
        }
        await LoadCatalogAsync(location.Route, catalogQuery, next, catalogContinuation, lifetime.Token, tag: catalogTag);
    }
    public Task LoadMoreCatalogPlaylistAsync() =>
        CanLoadMoreCatalogPlaylist ? CatalogPageAsync(1) : Task.CompletedTask;
    private bool ContainsCatalogCard(MusicCatalogCard card) =>
        HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card)) || DisplayCatalogSections.Any(section =>
        ReferenceEquals(section.Action, card) || section.Items.Any(item => ReferenceEquals(item, card)));
    internal void SelectCatalogSectionTab(MusicCatalogTab tab)
    {
        var section = CatalogSections.FirstOrDefault(section => section.Tabs?.Contains(tab) == true);
        if (section is null) return;
        section.SelectTab(tab);
        if (IsCatalogHome) RefreshHomeDisplay();
        RefreshCatalogDemandMembers();
    }
    public Task OpenCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || card is null || !card.CanOpen || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (IsPrivateCatalogCard(card))
            return TrackCatalogAction(async (signal, current) =>
            {
                if (current()) await OpenCatalogCardCoreAsync(card);
            }, card: card);
        return OpenCatalogCardCoreAsync(card);
    }
    private Task OpenCatalogCardCoreAsync(MusicCatalogCard card)
    {
        var channelTab = HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card));
        if (!channelTab)
        {
            try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        }
        if (card.Kind == "song" && card.Song is not null)
        {
            if (IsCatalogPlaylist)
            {
                int index = Songs.IndexOf(card.Song);
                int occurrence = Songs.Take(index).Count(song => SongJson.NativeId(song.Raw) == card.MemberId);
                return CatalogPlaylistActionAsync("play", index, card.Song.Id,
                    card.MemberId.Length > 0 ? card.MemberId : null, memberOccurrence: occurrence);
            }
            if (IsCatalogBatch) return CatalogBatchActionAsync("play", Songs.IndexOf(card.Song));
            try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
            return PlayCatalogSectionAsync(card);
        }
        if (card.Kind == "song") return PlayCatalogSongAsync(card);
        if (card.Kind == "playlist" && card.Locator is null)
            return OpenCatalogTargetAsync(Target("playlist", card.Platform, card.Title, new { id = card.ActionId })
                with { ManagedPlaylist = card.IsManagedPlaylist, Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count }, card.ActionId);
        if (card.Locator is not null || card.Kind is "daily" or "fm" or "similar" or "heartbeat" or "album" or "chart")
            return OpenPersonalCatalogCardAsync(card);
        RememberCatalogLocation();
        var destination = "catalog-" + card.Kind + "/" + card.Platform + "/" + Uri.EscapeDataString(card.ActionId);
        catalogChannelCodes[destination] = card.PageCode;
        catalogChannelKinds[destination] = card.ChannelKind;
        while (catalogChannelCodes.Count > 100) catalogChannelCodes.Remove(catalogChannelCodes.Keys.First());
        return RequestNavigationAsync(destination);
    }
    public Task FavoriteCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return SaveNetworkPlaylistAsync(card);
        if (card.Song is null || !card.CanFavorite) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return FavoriteSongAsync(card.Song);
    }
    private async Task CatalogNavigateAsync(string route, CancellationToken token)
    {
        if (route == "catalog-home") { await NavigateHomeAsync(token); return; }
        if (IsCatalogSourceRoute(route))
        {
            var submission = navigationSubmission;
            try { await EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!stopping && submission == navigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (stopping || submission != navigationSubmission) return;
            if (route.StartsWith("catalog-channel/", StringComparison.Ordinal) &&
                route.Split('/', 3)[1] != catalogSourcePlatform)
                route = "catalog-channels";
        }
        if (route == "catalog-channels" || route == lastChannelRoute && channelEntryError.Length > 0)
        {
            if (lastChannelRoute is not null) route = lastChannelRoute;
            if (channelEntryError.Length > 0)
            {
                RetireCatalogLoad();
                location = new(route); status = channelEntryError; heading = "音乐频道";
                catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
                catalogLoadedTarget = null; catalogMore = false; catalogContinuation = null;
                catalogPage = 1; total = 0; selectedSong = null;
                RefreshBindings(); CatalogBindings(); return;
            }
        }
        if (route == "catalog-search") { await RestoreSearchAsync(token); return; }
        if (!IsCatalogSourceRoute(route) && !route.Contains('/') && SelectedCatalogPlatform.Key is "kw" or "mg")
            SelectedCatalogPlatform = CatalogPlatforms[0];
        InvalidateCatalogAction();
        var platform = route.Split('/') is { Length: 3 } parts ? parts[1] :
            IsCatalogSourceRoute(route) ? catalogSourcePlatform : SelectedCatalogPlatform.Key;
        bool playlist = route.StartsWith("catalog-playlist/", StringComparison.Ordinal);
        if (!playlist && catalogLocations.TryGetValue(route + "/" + platform, out var saved) &&
            saved.Pages[saved.Page].Target?.Cacheable != false && RestoreCatalogPage(saved.Pages[saved.Page], platform))
        {
            token.ThrowIfCancellationRequested();
            RetireCatalogLoad();
            if (!await PublishCatalogAsync(route, saved.Query, platform, saved.Kind, saved.Page, saved.Pages[saved.Page],
                token: token, beforePublish: () =>
                {
                    catalogPages.Clear(); foreach (var p in saved.Pages) catalogPages[p.Key] = p.Value;
                    catalogCursors.Clear(); catalogCursors.AddRange(saved.Cursors);
                    catalogPageCode = saved.PageCode;
                })) return;
            StartHomeContinuation();
            return;
        }
        if (route.StartsWith("catalog-channel/", StringComparison.Ordinal) && publishedChannelState is { } channel && !RestoreCatalogPage(channel, platform))
        { channelSections.Clear(); publishedChannelState = null; }
        catalogPageCode = catalogChannelCodes.GetValueOrDefault(route) ?? "";
        await LoadCatalogAsync(route, "", 1, null, token, refresh: playlist);
    }
    public void CancelCatalog()
    {
        if (stopping) return;
        if (IsSearchPage) { _ = CancelSearchAsync(); return; }
        RetireCatalogOptionalReads(location.Route);
        RetireCatalogLoad();
        InvalidateCatalogAction();
        status = "已取消"; RefreshBindings(); CatalogBindings();
    }
    private static bool IsCatalogSourceRoute(string route) =>
        route is "catalog-home" or "catalog-channels" || route.StartsWith("catalog-channel/", StringComparison.Ordinal);

    private void RetireCatalogSourcePages()
    {
        RetireCatalogOptionalReads();
        catalogChannelKinds.Clear();
        // A shown or loading landing of the retired source reloads when the page is shown again.
        catalogSourceReload |= IsCatalogLanding && catalogLoadedPlatform.Length > 0;
        if (catalogLoadFlight is { } flight && IsCatalogSourceRoute(flight.Request.Key.Route))
            RetireCatalogLoad();
        foreach (var key in catalogLocations.Where(pair => IsCatalogSourceRoute(pair.Value.Route))
            .Select(pair => pair.Key).ToArray())
            catalogLocations.Remove(key);
        foreach (var key in catalogChannelCodes.Keys.Where(key => key.StartsWith("catalog-channel/", StringComparison.Ordinal)).ToArray())
            catalogChannelCodes.Remove(key);
        homeSections.Clear(); homeDisplaySections.Clear(); homeProjection.Clear();
        channelSections.Clear(); channelTags.Clear(); CatalogTagGroups = [];
        ChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
        publishedHomeState = publishedChannelState = null;
        homeContentPages.Clear(); homePlatform = ""; homeRefreshRequired = true;
        homePage = 1; homeCursors = [null];
        if (IsCatalogLanding)
        {
            AdvanceLoadGeneration();
            InvalidateCatalogAction();
            location = new(IsCatalogHome ? "catalog-home" : "catalog-channels");
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = catalogTag = catalogPageCode = catalogQuery = catalogArtwork = "";
            catalogAccountStamp = ""; catalogExpiresAt = null; catalogLoadedTarget = null;
            catalogContinuation = null; catalogMore = false; catalogPage = 1;
            catalogSongs.Clear(); selectedSong = null; total = 0;
            heading = IsCatalogHome ? "发现音乐" : "音乐频道";
        }
        RefreshBindings();
    }
    // An unknown source admits no landing request: show the requested landing with the
    // restore error instead of another page's or another source's content.
    private void PublishCatalogSourceUnavailable(string route)
    {
        RetireCatalogLoad();
        location = new(route == "catalog-home" ? "catalog-home" : "catalog-channels");
        RetireCatalogSourcePages();
        catalogSourceReload = true;
        status = catalogSourceStatus;
        RefreshBindings(); CatalogBindings();
    }
    // Settings hides the music page and the shell does not replay navigation on return.
    // A landing retired by a source change, or left unavailable, reads once when the page
    // is shown again with an authoritative source; a hidden page issues no read.
    public Task ActivateCatalogLandingAsync()
    {
        // Passive visibility must not replace initialization or an active navigation.
        // The shell completes initialization before requesting its first destination.
        if (stopping || !catalogSourceLoaded || loadCancellation is not null) return Task.CompletedTask;
        if (IsCatalogHome && homeRefreshRequired)
            return NavigateHomeAsync(lifetime.Token);
        if (!catalogSourceReload) return Task.CompletedTask;
        catalogSourceReload = false;
        if (!IsCatalogLanding || catalogLoadedPlatform == catalogSourcePlatform)
            return Task.CompletedTask;
        return LoadCatalogAsync(IsCatalogHome ? "catalog-home" : "catalog-channels", "", 1, null, lifetime.Token);
    }
    public void RetireCatalogSession()
    {
        InvalidateCatalogAction();
        RetireCatalogContent();
    }
    private void RetireCatalogContent()
    {
        RetireCatalogOptionalReads();
        RetireDailyStyleEditor(); dailyStyleSelected = []; dailyStyleSelectedCategory = ""; dailyStyleNeedsRead = false;
        RetireSearchTracked();
        var wasPublishing = publishing; publishing = true;
        try
        {
            RetireCatalogLoad();
            RetireHomeAuthority();
            catalogLocations.Clear(); catalogChannelCodes.Clear(); catalogChannelKinds.Clear(); catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogTargets.Clear(); catalogLoadedTarget = null;
            catalogArtwork = catalogSubtitle = ""; catalogSummaryCount = null; catalogMembersLoaded = false; catalogAccountStamp = ""; catalogExpiresAt = null;
            catalogTag = catalogPageCode = catalogQuery = ""; catalogContinuation = null; catalogMore = false; catalogPage = 1;
            channelTags.Clear(); utilityTags.Clear(); CatalogTagGroups = [];
            channelSections.Clear(); utilitySections.Clear();
            foreach (var sections in searchSections.Values) sections.Clear();
            searchSongs.Clear();
            if (IsCatalogPage && !IsCatalogHome) catalogSongs.Clear();
            ChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
            publishedChannelState = null;
            if (IsCatalogHome && publishedHomeState is { } home)
                catalogPages[1] = home;
            else if (IsCatalogPage) { Songs.Clear(); selectedSong = null; total = 0; heading = "音乐内容"; Changed(nameof(SelectedSong)); }
            RefreshBindings(); CatalogBindings();
        }
        finally { publishing = wasPublishing; }
    }
    private void RetireCatalogLoad()
    {
        AdvanceLoadGeneration();
        var cancellation = loadCancellation;
        loadCancellation = null;
        catalogLoadFlight = null;
        CancelCatalogLoad(cancellation);
    }
    private bool RetireCatalogLoad(CatalogLoadFlight flight)
    {
        if (!ReferenceEquals(catalogLoadFlight, flight) || flight.Generation != loadGeneration)
        {
            CancelCatalogLoad(flight.Cancellation);
            return false;
        }
        AdvanceLoadGeneration();
        catalogLoadFlight = null;
        if (ReferenceEquals(loadCancellation, flight.Cancellation)) loadCancellation = null;
        CancelCatalogLoad(flight.Cancellation);
        return true;
    }
    private static void CancelCatalogLoad(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private async Task LoadCatalogAsync(string route, string query, int pageNumber, JsonElement? continuation,
        CancellationToken token, bool refresh = false, string tag = "")
    {
        if (stopping || IsUnsupportedCatalogRoute(route)) return;
        token.ThrowIfCancellationRequested();
        if (IsCatalogSourceRoute(route) && !catalogSourceLoaded)
        {
            var submission = navigationSubmission;
            try { await EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!stopping && submission == navigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (stopping || submission != navigationSubmission) return;
        }
        var request = CaptureCatalogLoadRequest(route, query, pageNumber, continuation, refresh, tag);
        if (catalogLoadFlight is { } current && !current.Work.IsCompleted &&
            current.Generation == loadGeneration && !current.Cancellation.IsCancellationRequested &&
            current.Request.Key == request.Key)
        {
            await SubscribeCatalogLoad(current, token);
            return;
        }

        if (route != "catalog-home" || !IsCatalogHome || catalogLoadedPlatform != request.Key.Platform)
            InvalidateCatalogAction();
        var generation = AdvanceLoadGeneration();
        var stamp = ++statusEpoch;
        loadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var flight = new CatalogLoadFlight(request, generation, stamp, cancellation);
        loadCancellation = cancellation;
        catalogLoadFlight = flight;
        status = "";
        if ((location.Route != route || catalogLoadedPlatform != request.Key.Platform) &&
            !(IsCatalogHome && request.Target?.Private == true && request.Target.Operation is not ("daily" or "playlist")))
        {
            RetireDailyStyleEditor();
            location = new(route);
            if (request.Target?.Operation == "daily") CatalogDailyBranch = MusicCatalogDto.Text(request.Target.Parameters, "branch");
            if (IsChannelLanding) { channelSections.Clear(); publishedChannelState = null; CatalogTagGroups = []; }
            else if (!IsCatalogHome) { utilitySections.Clear(); }
            CatalogTags.Clear(); catalogSongs.Clear();
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = request.Key.Platform; catalogLoadedTarget = request.Target;
            catalogPage = 1; catalogMore = false; catalogContinuation = null;
            catalogTag = catalogQuery = "";
            catalogArtwork = request.Target?.Artwork ?? "";
            catalogSubtitle = request.Target?.Subtitle ?? "";
            catalogSummaryCount = request.Target?.Count; catalogMembersLoaded = false;
            heading = request.Target?.Title ?? (IsCatalogPlaylist ? "歌单" : IsCatalogDaily ? "每日推荐" : "音乐内容");
            total = 0; selectedSong = null;
        }
        RefreshBindings();
        CatalogBindings();
        flight.Work = RunCatalogFlightAsync(flight);
        loadDrains.Add(flight.Work);
        _ = ObserveCatalogLoadAsync(flight);
        await SubscribeCatalogLoad(flight, token);
    }

    private CatalogLoadRequest CaptureCatalogLoadRequest(string route, string query, int number,
        JsonElement? continuation, bool refresh, string tag)
    {
        var platform = IsCatalogSourceRoute(route) ? catalogSourcePlatform : SelectedCatalogPlatform.Key;
        var kind = CatalogSearchKind;
        var routeParts = route.Split('/', 3);
        if (routeParts.Length == 3) platform = routeParts[1];
        var target = CatalogRouteTarget(route, platform);
        if (target is not null) platform = target.Platform;
        var cursor = continuation?.Clone();
        var key = new CatalogLoadKey(route, query, number, cursor?.GetRawText() ?? "", refresh, tag,
            platform, kind, catalogPageCode, accountEpoch, CatalogAccountObservation(platform),
            target?.Operation ?? "", target?.Platform ?? "", target?.Title ?? "",
            target?.Parameters.GetRawText() ?? "");
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
                    if (RetireCatalogLoad(flight) && !disposed)
                    {
                        RefreshBindings();
                        CatalogBindings();
                    }
                }
            }
        }
    }

    private async Task ObserveCatalogLoadAsync(CatalogLoadFlight flight)
    {
        try { await flight.Work; }
        catch { /* The active subscriber observes unexpected owner failures. */ }
        finally { loadDrains.Remove(flight.Work); }
    }

    private async Task RunCatalogFlightAsync(CatalogLoadFlight flight)
    {
        await Task.Yield(); // Publish the unique request owner before any port can complete synchronously.
        try { await LoadCatalogCoreAsync(flight); }
        finally
        {
            if (ReferenceEquals(catalogLoadFlight, flight)) catalogLoadFlight = null;
            if (ReferenceEquals(loadCancellation, flight.Cancellation))
            {
                loadCancellation = null;
                if (!disposed) { RefreshBindings(); CatalogBindings(); }
            }
            flight.Cancellation.Dispose();
            if (!stopping && flight.Published && flight.Generation == loadGeneration) StartHomeContinuation();
        }
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
            MusicDto.Require(CatalogPlatforms.Any(item => item.Key == platform));
            MusicDto.Require(CatalogPlatforms.Any(item => item.Key == platform) && number >= 1);
            if (target?.Private == true) await ReadCatalogAccountForRequestAsync(platform, generation, token);
            else if (platform is "wy" or "tx" or "kg") await EnsureAccountsForRequestAsync(token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != loadGeneration || key.AccountObservation != CatalogAccountObservation(platform)) return;
            var accountStamp = CatalogAccountKey(platform);
            string operation, title;
            var parameters = new Dictionary<string, object?> { ["platform"] = platform, ["refresh"] = refresh };
            if (target is not null && target.Operation != "playlist")
            {
                operation = target.Operation; title = target.Title;
                ApplyCatalogTarget(target, parameters, number, continuation, refresh);
            }
            else if (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) || route.StartsWith("catalog-channel/", StringComparison.Ordinal))
            {
                var parts = route.Split('/', 3);
                MusicDto.Require(parts.Length == 3 && CatalogPlatforms.Any(item => item.Key == parts[1]));
                platform = parts[1]; parameters["platform"] = platform;
                parameters["id"] = Uri.UnescapeDataString(parts[2]);
                operation = parts[0] == "catalog-playlist" ? "playlist" : "channel";
                title = operation == "playlist" ? "歌单" : "频道";
                if (operation == "playlist")
                {
                    parameters["page"] = number; parameters["limit"] = target?.Limit ?? 100;
                    if (continuation is not null) parameters["continuation"] = continuation.Value;
                    if (target is not null) ApplyCatalogTarget(target, parameters, number, continuation, refresh);
                    parameters["id"] = Uri.UnescapeDataString(parts[2]);
                }
                else
                {
                    parameters["tag"] = tag;
                    if (key.PageCode.Length > 0) parameters["pageCode"] = key.PageCode;
                    if (continuation is not null) parameters["continuation"] = continuation.Value;
                }
            }
            else
            {
                (operation, title) = route switch
                {
                    "catalog-home" => ("home", "发现音乐"),
                    "catalog-channels" => ("channels", "音乐频道"),
                    "catalog-search" => ("search", "平台搜索"),
                    _ => throw new MusicOperationException("INVALID_REQUEST")
                };
                if (operation == "search")
                {
                    parameters["keyword"] = query; parameters["kind"] = kind == "歌单" ? "playlists" : "songs";
                    parameters["page"] = number; parameters["limit"] = 30;
                }
                else if (operation == "home" && continuation is not null) parameters["continuation"] = continuation.Value;
            }
            var reply = await CallSettled("music.catalog." + operation, parameters, token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != loadGeneration || key.AccountObservation != CatalogAccountObservation(platform)) return;
            if (accountStamp != CatalogAccountKey(platform)) throw new MusicOperationException("STALE_SESSION");
            var sections = MusicCatalogDto.Read(reply, platform, operation, title, kind == "歌单" ? "playlists" : "songs").ToArray();
            bool more = MusicCatalogDto.Boolean(reply, "more", false);
            var c = MusicCatalogDto.Field(reply, "continuation");
            JsonElement? next = c.ValueKind == JsonValueKind.Object ? c.Clone() : null;
            bool continuationBlocked = more && (next is null ||
                catalogCursors.Take(number).Any(old => MusicCatalogDto.CursorKey(old) == MusicCatalogDto.CursorKey(next)));
            if (number > 1)
            {
                if (operation is "home" or "channel" or "playlist")
                    sections = MergeCatalogSections(CatalogSections, sections);
            }
            var tags = target?.Operation == "daily" ? Array.Empty<MusicCatalogTag>() : MusicCatalogDto.Tags(reply).ToArray();
            var selected = MusicCatalogDto.Text(reply, "selectedTag");
            var name = MusicCatalogDto.Text(reply, operation is "home" or "channel" or "artistActivity" or "chart" or "charts" ? "title" : "name");
            var state = new CatalogPageState(sections, tags, selected, name.Length > 0 ? name : title,
                MusicCatalogDto.Text(reply, "cover"), more, next,
                MusicCatalogDto.Number(reply, "total") ?? sections.Sum(s => s.Items.Count),
                Scroll: number > 1 && operation is ("home" or "channel" or "playlist") ? Presentation.CaptureScroll() : 0,
                Target: target, AccountStamp: accountStamp, ExpiresAt: CatalogExpiry(reply), DailySelection: target?.Operation == "daily" ? DailySelectionSnapshot(reply) : null)
                { ContinuationBlocked = continuationBlocked };
            if (number > 1 && catalogPages.TryGetValue(catalogPage, out var previousPage))
                state = state with { OptionalReads = previousPage.OptionalReads };
            if (operation == "playlist")
                RememberCatalogCollectionMetadata(platform, MusicCatalogDto.Text(reply, "id"), name, state.Artwork,
                    MusicCatalogDto.LongNumber(reply, "playCount"));
            if (operation == "channels")
            {
                ChannelTabs.Clear();
                foreach (var card in sections.SelectMany(s => s.Items).Where(c => c.Kind == "channel" &&
                    c.Display))
                    ChannelTabs.Add(card);
                var first = VisibleChannelTabs.Concat(MoreChannelTabs).FirstOrDefault(c => c.CanOpen);
                if (first is not null)
                {
                    lastChannelRoute = "catalog-channel/" + first.Platform + "/" + Uri.EscapeDataString(first.ActionId);
                    catalogChannelCodes[lastChannelRoute] = first.PageCode;
                    catalogChannelKinds[lastChannelRoute] = first.ChannelKind;
                    catalogPageCode = first.PageCode;
                    location = new(lastChannelRoute);
                    catalogLoadedPlatform = first.Platform;
                    RefreshBindings(); CatalogBindings();
                    var channelKey = key with { Route = lastChannelRoute, Platform = first.Platform, PageCode = first.PageCode };
                    await LoadCatalogCoreAsync(new CatalogLoadFlight(new(channelKey, null, null), generation, stamp, flight.Cancellation));
                    return;
                }
            }
            if (operation == "channels") state = state with { Sections = [], Total = 0 };
            Action? startReplacement = null;
            if (operation == "home" && number == 1 && homeDisplaySections.Count > 0 && PrepareHomeReplacement is { } prepare)
            {
                startReplacement = await prepare();
                token.ThrowIfCancellationRequested();
                if (stopping || generation != loadGeneration || key.AccountObservation != CatalogAccountObservation(platform)) return;
                if (accountStamp != CatalogAccountKey(platform)) throw new MusicOperationException("STALE_SESSION");
            }
            if (!await PublishCatalogAsync(route, query, platform, kind, number, state,
                append: operation == "home" && number > 1, token: token,
                isCurrent: () => generation == loadGeneration && key.AccountObservation == CatalogAccountObservation(platform),
                beforePublish: () =>
                {
                    if (number == 1) { catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null); }
                    else if (operation is "playlist" or "home") catalogPages.Clear();
                    while (catalogCursors.Count <= number) catalogCursors.Add(null);
                    catalogCursors[number] = next; catalogPages[number] = state;
                    startReplacement?.Invoke();
                })) return;
            if (operation == "home") SaveHomeContent(platform, number, reply);
            flight.Published = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && generation == loadGeneration && stamp == statusEpoch &&
                key.AccountObservation == CatalogAccountObservation(platform))
            {
                if (!IsCatalogHome && error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" or "STALE_SESSION" or "SESSION_CHANGED" })
                    RetireCatalogSession();
                status = MusicCatalogDto.ErrorText(error is MusicOperationException failure ? failure.Code : "INVALID_RESPONSE");
                if (route == "catalog-channels" || route == lastChannelRoute && channelSections.Count == 0)
                    channelEntryError = status;
                if (error is MusicOperationException { Code: "PAGINATION_STALLED" })
                {
                    catalogMore = false;
                    if (catalogPages.TryGetValue(catalogPage, out var prior))
                        catalogPages[catalogPage] = prior with { More = false };
                }
                publishing = true;
                try
                {
                    SelectedCatalogPlatform = CatalogPlatforms.FirstOrDefault(p => p.Key == catalogLoadedPlatform) ?? SelectedCatalogPlatform;
                    CatalogSearchKind = catalogLoadedKind;
                    if (catalogPages.TryGetValue(catalogPage, out var previous)) PublishPersonalCatalog(previous);
                    Changed(nameof(Status)); CatalogBindings();
                }
                finally { publishing = false; }
                if (error is MusicOperationException { Code: "AUTH_REQUIRED" or "AUTH_EXPIRED" })
                {
                    PublishCatalogAccountNotice(((MusicOperationException)error).Code);
                    RecoverHomeAfterAuthenticationFailure();
                }
            }
        }
    }
    private CancellationTokenSource? homeContinuationCancellation;
    internal Task HomeInitialization { get; private set; } = Task.CompletedTask;
    private void StartHomeContinuation()
    {
        if (stopping || !IsCatalogHome || !catalogMore || loadCancellation is not null ||
            homeContinuationCancellation is not null) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        homeContinuationCancellation = request;
        var generation = loadGeneration;
        HomeInitialization = Read();
        loadDrains.Add(HomeInitialization);
        _ = Drain(HomeInitialization);
        async Task Drain(Task work) { try { await work; } finally { loadDrains.Remove(work); } }
        async Task Read()
        {
            await Task.Yield();
            try
            {
                while (!stopping && generation == loadGeneration && !request.IsCancellationRequested && IsCatalogHome && catalogMore)
                {
                    int previous = catalogPage;
                    var next = CaptureCatalogLoadRequest(location.Route, catalogQuery, previous + 1, catalogContinuation, true, catalogTag);
                    await LoadCatalogCoreAsync(new(next, generation, statusEpoch, request));
                    if (catalogPage == previous) break; // A real failure is published once; no background retry loop.
                }
            }
            finally
            {
                if (ReferenceEquals(homeContinuationCancellation, request)) homeContinuationCancellation = null;
                request.Dispose();
                if (!disposed) CatalogBindings();
            }
        }
    }
    private static MusicCatalogSection[] MergeCatalogSections(IEnumerable<MusicCatalogSection> existingSections, MusicCatalogSection[] incoming)
    {
        var result = existingSections.ToList();
        foreach (var section in incoming)
        {
            var index = result.FindIndex(s => s.Id == section.Id);
            if (index < 0) result.Add(section);
            else
            {
                var existing = result[index];
                var merged = section with { Items = existing.Items.Concat(section.Items).ToArray() };
                merged.HorizontalOffset = existing.HorizontalOffset;
                result[index] = merged;
            }
        }
        return result.ToArray();
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
    private async Task<bool> PublishCatalogAsync(string route, string query, string platform, string kind, int number, CatalogPageState state,
        bool append = false, bool initializeEntries = true, CancellationToken token = default,
        Func<bool>? isCurrent = null, Action? beforePublish = null)
    {
        long generation = loadGeneration, submission = navigationSubmission;
        long observation = CatalogAccountObservation(platform);
        string account = CatalogAccountKey(platform);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        bool Current() => !stopping && generation == loadGeneration && submission == navigationSubmission &&
            observation == CatalogAccountObservation(platform) && account == CatalogAccountKey(platform) &&
            isCurrent?.Invoke() != false;
        try
        {
            await RefreshCatalogSongsAsync(RepositoryCards(state.Sections), token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return false;
            beforePublish?.Invoke();
            Publish();
            return true;
        }
        catch (MusicOperationException error) when (error.Code is "STORAGE_UNAVAILABLE" or "CORRUPT_STORAGE" or "STORAGE")
        {
            if (Current()) { status = MusicCatalogDto.ErrorText(error.Code); Changed(nameof(Status)); CatalogBindings(); }
            return false;
        }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); }

        void Publish()
        {
            publishing = true;
            try
            {
                location = new(route); heading = state.Title;
                if (route.StartsWith("catalog-channel/", StringComparison.Ordinal))
                { lastChannelRoute = route; channelEntryError = ""; }
                catalogQuery = query; catalogLoadedPlatform = platform; catalogLoadedKind = kind; CatalogSearchKind = kind;
                catalogPage = number; catalogMore = state.More && !state.ContinuationBlocked; catalogContinuation = state.Next;
                catalogTag = state.Tag;
                catalogArtwork = state.Artwork.Length > 0 ? state.Artwork : state.Target?.Artwork ?? "";
                catalogSubtitle = state.Target?.Subtitle ?? "";
                catalogSummaryCount = state.Total; catalogMembersLoaded = true;
                PublishPersonalCatalog(state);
                if (state.DailySelection is { } daily) PublishDailyStyleSelection(daily);
                SelectedCatalogPlatform = CatalogPlatforms.Single(p => p.Key == platform);
                if (!CatalogSections.SequenceEqual(state.Sections, ReferenceEqualityComparer.Instance))
                {
                    ReconcileSections(CatalogSections, state.Sections);
                }
                if (IsCatalogHome)
                {
                    publishedHomeState = state; homePage = number; homePlatform = platform;
                    RefreshHomeDisplay();
                }
                else if (IsChannelLanding) publishedChannelState = state;
                if (!CatalogTags.SequenceEqual(state.Tags))
                {
                    CatalogTags.Clear(); foreach (var tag in state.Tags) CatalogTags.Add(tag);
                    if (IsChannelLanding)
                        CatalogTagGroups = channelTags.GroupBy(tag => tag.Group, StringComparer.Ordinal)
                            .Select(group => new MusicCatalogTagGroup(group.Key, group.ToArray())).ToArray();
                }
                IEnumerable<MusicCatalogSection> songSections = IsCatalogHome ? homeDisplaySections : state.Sections;
                var songs = songSections.SelectMany(s => s.Items).Select(i => i.Song).OfType<SongRow>().ToArray();
                if (!Songs.SequenceEqual(songs, ReferenceEqualityComparer.Instance))
                    ReconcileSongRows(Songs, songs);
                if (!append) selectedSong = null;
                collection = null; total = state.Total;
                var visibleCount = IsCatalogHome
                    ? homeDisplaySections.Sum(section => section.Items.Count)
                    : state.Sections.Sum(section => section.Items.Count);
                if (!append) status = visibleCount == 0 ? "暂无内容" : "";
                if (state.ContinuationBlocked) status = MusicCatalogDto.ErrorText("PAGINATION_STALLED");
                Changed(nameof(SelectedSong)); RefreshBindings(); CatalogBindings();
            }
            finally { publishing = false; }
            ActivateCatalogOptionalReads(route, platform, state, initializeEntries);
            if (append) return;
            _ = Presentation.Dispatcher.EnqueueAsync(() => { if (!stopping && generation == loadGeneration) Presentation.RestoreScroll(state.Scroll); }, DispatcherQueuePriority.Low);
        }
    }
}
