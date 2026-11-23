using System.Collections.ObjectModel;
using System.Text.Json;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MeowFilm.Capabilities;

/// <summary>Standalone owner for music catalog browsing, home lifecycle, channels, personal pages, daily style, and VIP page.</summary>
public sealed partial class MusicCatalogController : MusicController
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

    private readonly MusicSettingsController settings;
    private readonly MusicAccountsController accounts;
    private readonly MusicPlaybackController playbackController;
    private readonly MusicQueueController queue;
    private readonly MusicDiscoveryController discovery;

    internal MusicCatalogController(MusicRuntimeContext ctx, MusicSettingsController settings,
        MusicAccountsController accounts, MusicPlaybackController playbackController,
        MusicQueueController queue, MusicDiscoveryController discovery) : base(ctx)
    {
        this.settings = settings;
        this.accounts = accounts;
        this.playbackController = playbackController;
        this.queue = queue;
        this.discovery = discovery;
        homeSnapshotStore = new(cacheDatabase);
        vipPage = new(application, ctx.Dispatcher);
    }

    internal Func<ObservableCollection<SongRow>> GetCatalogSongsSource { get; set; } = null!;
    internal Action<string> OnClearSelectionForCatalog { get; set; } = null!;
    internal Action OnClearSelectionOnly { get; set; } = null!;
    internal Func<SongRow, Task> OnAppendSongNextAsync { get; set; } = null!;
    internal Func<SongRow, Task> OnFavoriteSongAsync { get; set; } = null!;
    internal Func<SongRow, Task> OnToggleSongFavoriteAsync { get; set; } = null!;
    internal Func<string> GetLibraryHeading { get; set; } = null!;
    internal Func<ObservableCollection<PlaylistRow>> GetNetworkPlaylists { get; set; } = null!;
    internal Action OnNotifyLibraryDetailBindings { get; set; } = null!;
    internal Action OnNotifyLibrarySelectionChanged { get; set; } = null!;
    internal Action<int, bool> OnPublishCatalogSummary { get; set; } = null!;
    internal Func<CancellationToken, Task> OnRefreshLibraryPlaylistsAsync { get; set; } = null!;
    internal Func<int> GetLibraryTotal { get; set; } = null!;
    internal Func<string, CancellationToken, bool, Task> OnBeginSearchAsync { get; set; } = null!;
    internal Func<Task> OnCancelSearchAsync { get; set; } = null!;
    internal Func<bool> GetHasSearchSession { get; set; } = null!;
    internal Func<bool> GetIsSearchPage { get; set; } = null!;
    internal Func<bool> GetIsSearchSongs { get; set; } = null!;
    internal Action OnRememberSearch { get; set; } = null!;
    internal Func<CancellationToken, Task> OnRestoreSearchAsync { get; set; } = null!;
    internal Action OnRetireSearchTracked { get; set; } = null!;
    internal Func<long> GetSearchAccountEpoch { get; set; } = null!;
    internal Action OnSearchBindings { get; set; } = null!;
    internal Func<string> GetSearchKeyword { get; set; } = null!;
    internal Func<ObservableCollection<SongRow>> GetSearchSongsSource { get; set; } = null!;
    internal Func<string> GetSearchView { get; set; } = null!;
    internal Func<Task> OnSwitchSearchKindAsync { get; set; } = null!;

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

    private readonly ObservableCollection<MusicCatalogSection> homeSections = [], utilitySections = [];
    private ObservableCollection<MusicCatalogSection> channelSections = [];
    internal IEnumerable<ObservableCollection<MusicCatalogSection>> RetainedChannelSections =>
        catalogLocations.Values.Select(page => page.ChannelSections).OfType<ObservableCollection<MusicCatalogSection>>();
    internal event Action<ObservableCollection<MusicCatalogSection>>? ChannelPageRetired;
    private readonly Dictionary<string, ObservableCollection<MusicCatalogSection>> searchSections = new()
    { [MusicSearchController.SearchOverview] = [], ["songs"] = [], ["playlists"] = [] };

    public ObservableCollection<MusicCatalogSection> CatalogSections => GetIsSearchPage() ? searchSections[GetSearchView()] :
        IsCatalogHome ? homeSections : IsChannelLanding ? channelSections : utilitySections;
    public ObservableCollection<MusicCatalogSection> HomeSections => homeDisplaySections;
    public ObservableCollection<MusicCatalogSection> ChannelSections => channelSections;
    public ObservableCollection<MusicCatalogSection> SearchOverviewSections => searchSections[MusicSearchController.SearchOverview];
    internal ObservableCollection<MusicCatalogSection> SearchPlaylistSections => searchSections["playlists"];
    public ObservableCollection<MusicCatalogSection> UtilitySections => utilitySections;

    public bool IsCatalogContent => IsCatalogPage && !discovery.IsDiscovery && !IsCatalogDetailPage && !GetIsSearchSongs();
    public bool IsCatalogBrowsePage => IsCatalogPage && !GetIsSearchPage() && !discovery.IsDiscovery;
    public bool IsUtilityCatalogSurface => IsCatalogContent && !IsCatalogHome && !IsChannelLanding && !GetIsSearchPage();
    public bool ShowCatalogLoading => IsCatalogContent && !GetIsSearchPage() && ctx.LoadCancellation is not null && CatalogSections.Count == 0;
    public string Status => ctx.Status;

    private string? lastChannelRoute;
    private string channelEntryError = "";
    private bool catalogSourceReload;
    private CatalogPageState? publishedHomeState;
    private readonly ObservableCollection<MusicCatalogSection> homeDisplaySections = [];
    private readonly Dictionary<MusicCatalogSection, MusicCatalogSection> homeProjection = new(ReferenceEqualityComparer.Instance);
    public ObservableCollection<MusicCatalogSection> DisplayCatalogSections => IsCatalogHome ? homeDisplaySections : CatalogSections;
    public bool IsCatalogHome => ctx.CurrentRoute == "catalog-home";
    public ObservableCollection<MusicCatalogCard> ChannelTabs { get; } = [];
    public bool IsChannelDirectory => ctx.CurrentRoute == "catalog-channels";
    public bool IsVipChannel => IsChannelLanding && catalogChannelKinds.GetValueOrDefault(ctx.CurrentRoute) == "vip";
    public string CurrentChannelKey => ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal) ? ctx.CurrentRoute[16..] : "";
    public ObservableCollection<MusicCatalogCard> VisibleChannelTabs { get; } = [];
    public ObservableCollection<MusicCatalogCard> MoreChannelTabs { get; } = [];
    public bool HasMoreChannelTabs => MoreChannelTabs.Any();
    public bool HasChannelTabs => IsChannelLanding && ChannelTabs.Count > 0;
    public bool IsCatalogUtilityPage => IsCatalogBrowsePage && !IsCatalogLanding && !IsCatalogDetailPage;
    public bool IsCatalogLanding => ctx.CurrentRoute is "catalog-home" or "catalog-channels" || IsChannelLanding;
    public bool IsChannelLanding => ctx.CurrentRoute == "catalog-channels" || ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal);

    private readonly ObservableCollection<MusicCatalogTag> channelTags = [], utilityTags = [];
    public ObservableCollection<MusicCatalogTag> CatalogTags => IsChannelLanding ? channelTags : utilityTags;
    public IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups { get; private set; } = [];
    public MusicCatalogTag? SelectedCatalogTag => CatalogTags.FirstOrDefault(tag => tag.Selected);
    public bool IsCatalogPage => ctx.CurrentRoute.StartsWith("catalog-", StringComparison.Ordinal);
    public bool IsCatalogPlaylist => ctx.CurrentRoute.StartsWith("catalog-playlist/", StringComparison.Ordinal);
    public bool IsCatalogArtist => ctx.CurrentRoute.StartsWith("catalog-artist/", StringComparison.Ordinal);
    public bool IsCatalogDetailPage => IsCatalogPlaylist || IsCatalogArtist || IsCatalogDaily;
    public bool CanCatalogBrowse => !stopping && commands == 0;
    public bool CanCatalogNext => !IsCatalogHome && CanCatalogBrowse && ctx.LoadCancellation is null && catalogMore;
    public bool CanCatalogPrevious => CanCatalogBrowse && ctx.LoadCancellation is null && !IsCatalogHome && !IsCatalogDetailPage && catalogPage > 1;
    public bool CanLoadMoreCatalogPlaylist => IsCatalogPlaylist && CanCatalogNext;
    public bool HasCatalogTags => CatalogTags.Count > 0 && ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal);
    public bool CanCatalogAction => CanCatalogBrowse && ctx.LoadCancellation is null && IsCatalogCollection &&
        (!IsDailyStyle || !dailyStyleBusy && !dailyStyleNeedsRead && catalogAccountStamp.Length > 0);
    public bool CanPlayCatalogPlaylist => CanCatalogAction && GetLibraryTotal() > 0;
    public bool CanCancelCatalog => ctx.LoadCancellation is not null || homeContinuationCancellation is not null || catalogAction is not null;
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

    internal string CatalogArtwork => catalogArtwork;
    internal int? CatalogSummaryCount => catalogSummaryCount;
    internal string CatalogLoadedPlatform => catalogLoadedPlatform;
    internal string CatalogQuery => catalogQuery;
    internal CatalogTarget? CatalogLoadedTarget => catalogLoadedTarget;
    public bool ShowDetailLoading => IsCatalogDetailPage && ctx.LoadCancellation is not null && !catalogMembersLoaded;
    public string DetailSubtitle => IsCatalogDetailPage ? catalogSubtitle : "";

    private readonly Dictionary<string, CatalogLocation> catalogLocations = [];
    private readonly Dictionary<string, string> catalogChannelCodes = [];
    private readonly Dictionary<string, string> catalogChannelKinds = [];
    private readonly Dictionary<int, CatalogPageState> catalogPages = [];

    private sealed record CatalogPageState(MusicCatalogSection[] Sections, MusicCatalogTag[] Tags,
        string Tag, string Title, string Artwork, bool More, JsonElement? Next, int Total, double Scroll = 0,
        CatalogTarget? Target = null, string AccountStamp = "", long? ExpiresAt = null, JsonElement? DailySelection = null)
    {
        public CatalogOptionalReads? OptionalReads { get; set; }
        public bool ContinuationBlocked { get; init; }
    }
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
    private CatalogLoadFlight? catalogLoadFlight;

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        Changed(nameof(Status));
        CatalogBindings();
    }

    internal void NotifyStatusChanged() => Changed(nameof(Status));
    internal IEnumerable<MusicCatalogSection> AllRetainedSections() =>
        homeSections.Concat(homeDisplaySections).Concat(channelSections).Concat(utilitySections)
            .Concat(searchSections.Values.SelectMany(value => value))
            .Concat(catalogPages.Values.SelectMany(page => page.Sections))
            .Concat(catalogLocations.Values.SelectMany(saved => saved.Pages.Values).SelectMany(page => page.Sections));

    internal static IEnumerable<MusicCatalogCard> RepositoryCards(IEnumerable<MusicCatalogSection> sections) =>
        sections.SelectMany(section => section.Items.Concat(section.Action is { } action ? [action] : [])
            .Concat(section.Tabs?.SelectMany(tab => tab.Items) ?? []));

    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, JsonElement?> read,
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
        var snapshot = await application.SongSnapshotsAsync(ids, token);
        token.ThrowIfCancellationRequested();
        if (stopping || disposed) return;
        JsonElement? Read(string id) => snapshot.Songs.GetValueOrDefault(id);
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
            nameof(IsUtilityCatalogSurface), nameof(IsCatalogHome), nameof(IsCatalogPage),
            nameof(IsChannelDirectory), nameof(CurrentChannelKey), nameof(IsCatalogLanding),
            nameof(IsCatalogUtilityPage), nameof(IsChannelLanding), nameof(IsCatalogPlaylist),
            nameof(IsCatalogArtist), nameof(IsCatalogDetailPage), nameof(IsCatalogBrowsePage),
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

    internal void ClearSearchSections()
    {
        foreach (var sections in searchSections.Values) sections.Clear();
    }

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
        catalogLoadedKind = CatalogSearchKind = kindLabel;
    }

    public void CatalogBindings()
    {
        foreach (var name in new[]
        {
            nameof(ShowCatalogLoading), nameof(IsVipChannel), nameof(HasMoreChannelTabs), nameof(HasChannelTabs),
            nameof(CanCatalogNext), nameof(CanCatalogPrevious), nameof(CanCatalogBrowse), nameof(HasCatalogTags), nameof(CatalogTagGroups),
            nameof(CanCatalogAction), nameof(CanPlayCatalogPlaylist), nameof(CanCancelCatalog), nameof(CatalogPageText), nameof(SelectedCatalogTag),
            nameof(CatalogPlatformChoices), nameof(SelectedCatalogPlatform), nameof(CatalogSearchKind),
            nameof(ShowDetailLoading), nameof(DetailSubtitle), nameof(CanLoadMoreCatalogPlaylist)
        })
            Changed(name);
        OnNotifyLibraryDetailBindings();
        DailyStyleBindings();
        PersonalCatalogBindings();
        NetworkPlaylistBindings();
        OnSearchBindings();
    }

    private void SelectChannelPage(string route, string platform)
    {
        if (!route.StartsWith("catalog-channel/", StringComparison.Ordinal)) return;
        string key = route + "/" + platform;
        long observation = CatalogAccountObservation(platform);
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
        RetireCatalogOptionalReads(page.Route);
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
        if (discovery.IsDiscovery) return;
        if (GetIsSearchPage()) { OnRememberSearch(); return; }
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
            { ChannelSections = previous?.ChannelSections, Observation = previous?.Observation ?? CatalogAccountObservation(catalogLoadedPlatform) };
        while (catalogLocations.Count > 8)
        {
            var retired = catalogLocations.FirstOrDefault(pair => pair.Value.Route != lastChannelRoute);
            if (retired.Key is null) break;
            RetireCatalogLocation(retired.Key);
        }
    }

    internal Task ChangeCatalogPlatformAsync(MusicCatalogPlatform platform)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogPage || platform == SelectedCatalogPlatform ||
            !CatalogPlatformChoices.Contains(platform)) return Task.CompletedTask;
        RememberCatalogLocation();
        SelectedCatalogPlatform = platform;
        if (platform.Key != "tx") CatalogPersonalKind = "all";
        return LoadCatalogAsync(IsCatalogPersonal ? "catalog-personal" : "catalog-home", "", 1, null, lifetime.Token);
    }

    public Task CatalogSearchKindAsync() => GetIsSearchPage() ? OnSwitchSearchKindAsync() : Task.CompletedTask;

    public Task CatalogTagAsync(MusicCatalogTag tag)
    {
        if (ctx.Publishing || !CanCatalogBrowse || tag is null || !CatalogTags.Contains(tag) ||
            !ctx.CurrentRoute.StartsWith("catalog-channel/", StringComparison.Ordinal) ||
            tag.Id == catalogTag && ctx.LoadCancellation is null) return Task.CompletedTask;
        return LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, 1, null, lifetime.Token, tag: tag.Id);
    }

    public Task RefreshCatalogAsync()
    {
        if (!IsCatalogPage || stopping || IsCatalogDaily && dailyStyleBusy || IsUnsupportedCatalogRoute(ctx.CurrentRoute)) return Task.CompletedTask;
        ctx.RetireOperationStatus();
        if (IsCatalogDaily) RetireDailyStyleEditor();
        return GetIsSearchPage() ? OnBeginSearchAsync(GetSearchKeyword(), lifetime.Token, true) :
            LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, 1, null, lifetime.Token, refresh: true, tag: catalogTag);
    }

    public async Task CatalogPageAsync(int direction)
    {
        if (direction is not (-1 or 1) || (direction > 0 ? !CanCatalogNext : !CanCatalogPrevious)) return;
        RememberCatalogLocation();
        int next = catalogPage + direction;
        if (catalogPages.TryGetValue(next, out var cached) && RestoreCatalogPage(cached, catalogLoadedPlatform))
        {
            ctx.AdvanceLoadGeneration();
            InvalidateCatalogAction();
            await PublishCatalogAsync(ctx.CurrentRoute, catalogQuery, catalogLoadedPlatform, catalogLoadedKind, next, cached,
                token: lifetime.Token, restore: true);
            return;
        }
        await LoadCatalogAsync(ctx.CurrentRoute, catalogQuery, next, catalogContinuation, lifetime.Token, tag: catalogTag);
    }

    public Task LoadMoreCatalogPlaylistAsync() =>
        CanLoadMoreCatalogPlaylist ? CatalogPageAsync(1) : Task.CompletedTask;

    internal bool ContainsCatalogCard(MusicCatalogCard card) =>
        HasChannelTabs && ChannelTabs.Any(item => ReferenceEquals(item, card)) || DisplayCatalogSections.Any(section =>
        ReferenceEquals(section.Action, card) || section.Items.Any(item => ReferenceEquals(item, card)));

    internal void SelectCatalogSectionTab(MusicCatalogTab tab)
    {
        var section = CatalogSections.FirstOrDefault(section => section.Tabs?.Contains(tab) == true);
        if (section is null) return;
        section.SelectTab(tab);
        if (IsCatalogHome) RefreshHomeDisplay();
        else RefreshCatalogDemandMembers();
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
                var songCards = CatalogSections.SelectMany(s => s.Items).Where(item => item.Song is not null).ToArray();
                int index = Array.FindIndex(songCards, item => ReferenceEquals(item, card));
                if (index < 0) return Task.CompletedTask;
                int occurrence = songCards.Take(index).Count(item => SongJson.NativeId(item.Song!.Raw) == card.MemberId);
                return CatalogPlaylistActionAsync("play", index, card.Song.Id,
                    card.MemberId.Length > 0 ? card.MemberId : null, memberOccurrence: occurrence);
            }
            if (IsCatalogBatch)
            {
                var songCards = CatalogSections.SelectMany(s => s.Items).Where(item => item.Song is not null).ToArray();
                int index = Array.FindIndex(songCards, item => ReferenceEquals(item, card));
                return index < 0 ? Task.CompletedTask : CatalogBatchActionAsync("play", index);
            }
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
        return FavoriteSongCardAsync(card, toggle: false);
    }

    public Task ToggleCatalogCardFavoriteAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist")
            return NetworkPlaylistForCard(card) is { } reference
                ? RemoveNetworkPlaylistAsync(reference)
                : SaveNetworkPlaylistAsync(card);
        return FavoriteSongCardAsync(card, toggle: true);
    }

    private Task FavoriteSongCardAsync(MusicCatalogCard card, bool toggle)
    {
        if (card.Song is null || !card.CanFavorite) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return toggle ? OnToggleSongFavoriteAsync(card.Song) : OnFavoriteSongAsync(card.Song);
    }

    internal async Task CatalogNavigateAsync(string route, CancellationToken token)
    {
        if (route == "catalog-home") { await NavigateHomeAsync(token); return; }
        if (IsCatalogSourceRoute(route))
        {
            var submission = ctx.NavigationSubmission;
            try { await settings.EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!stopping && submission == ctx.NavigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (stopping || submission != ctx.NavigationSubmission) return;
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
                catalogLoadedTarget = null; catalogMore = false; catalogContinuation = null;
                catalogPage = 1;
                ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings(); return;
            }
        }
        if (route == "catalog-search") { await OnRestoreSearchAsync(token); return; }
        if (!IsCatalogSourceRoute(route) && !route.Contains('/') && SelectedCatalogPlatform.Key is "kw" or "mg")
            SelectedCatalogPlatform = CatalogPlatforms[0];
        InvalidateCatalogAction();
        var platform = route.Split('/') is { Length: 3 } parts ? parts[1] :
            IsCatalogSourceRoute(route) ? settings.CatalogSourcePlatform : SelectedCatalogPlatform.Key;
        bool playlist = route.StartsWith("catalog-playlist/", StringComparison.Ordinal);
        if (!playlist && catalogLocations.TryGetValue(route + "/" + platform, out var saved) &&
            saved.Pages.TryGetValue(saved.Page, out var savedPage) &&
            savedPage.Target?.Cacheable != false && RestoreCatalogPage(savedPage, platform))
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

    public void CancelCatalog()
    {
        if (stopping) return;
        if (GetIsSearchPage()) { _ = OnCancelSearchAsync(); return; }
        RetireCatalogOptionalReads(ctx.CurrentRoute);
        RetireCatalogLoad();
        InvalidateCatalogAction();
        ctx.SetStatusQuiet("已取消"); ctx.RefreshBindings();
    }

    private static bool IsCatalogSourceRoute(string route) =>
        route is "catalog-home" or "catalog-channels" || route.StartsWith("catalog-channel/", StringComparison.Ordinal);

    internal void RetireCatalogSourcePages()
    {
        var previousSelection = ctx.CaptureContentSelection();
        RetireCatalogOptionalReads();
        catalogChannelKinds.Clear();
        // A shown or loading landing of the retired source reloads when the page is shown again.
        catalogSourceReload |= IsCatalogLanding && catalogLoadedPlatform.Length > 0;
        if (catalogLoadFlight is { } flight && IsCatalogSourceRoute(flight.Request.Key.Route))
            RetireCatalogLoad();
        foreach (var key in catalogLocations.Where(pair => IsCatalogSourceRoute(pair.Value.Route))
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
            InvalidateCatalogAction();
            ctx.SetLocation(new(IsCatalogHome ? "catalog-home" : "catalog-channels"),
                heading: IsCatalogHome ? "发现音乐" : "音乐频道", clearCatalogSongs: true, clearSelection: true, clearTotal: true);
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = catalogTag = catalogPageCode = catalogQuery = catalogArtwork = "";
            catalogAccountStamp = ""; catalogLoadedTarget = null;
            catalogContinuation = null; catalogMore = false; catalogPage = 1;
        }
        ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
    }

    // An unknown source admits no landing request: show the requested landing with the
    // restore error instead of another page's or another source's content.
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

    // Settings hides the music page and the shell does not replay navigation on return.
    // A landing retired by a source change, or left unavailable, reads once when the page
    // is shown again with an authoritative source; a hidden page issues no read.
    public Task ActivateCatalogLandingAsync()
    {
        // Passive visibility must not replace initialization or an active navigation.
        // The shell completes initialization before requesting its first destination.
        if (stopping || !settings.CatalogSourceLoaded || ctx.LoadCancellation is not null) return Task.CompletedTask;
        if (IsCatalogHome && homeRefreshRequired)
            return NavigateHomeAsync(lifetime.Token);
        if (!catalogSourceReload) return Task.CompletedTask;
        catalogSourceReload = false;
        if (!IsCatalogLanding || catalogLoadedPlatform == settings.CatalogSourcePlatform)
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
        OnRetireSearchTracked();
        var wasPublishing = ctx.Publishing; ctx.Publishing = true;
        try
        {
            RetireCatalogLoad();
            RetireHomeAuthority();
            foreach (var key in catalogLocations.Keys.ToArray()) RetireCatalogLocation(key);
            catalogChannelCodes.Clear(); catalogChannelKinds.Clear(); catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogTargets.Clear(); catalogLoadedTarget = null;
            catalogArtwork = catalogSubtitle = ""; catalogSummaryCount = null; catalogMembersLoaded = false; catalogAccountStamp = "";
            catalogTag = catalogPageCode = catalogQuery = ""; catalogContinuation = null; catalogMore = false; catalogPage = 1;
            channelTags.Clear(); utilityTags.Clear(); CatalogTagGroups = [];
            utilitySections.Clear();
            foreach (var sections in searchSections.Values) sections.Clear();
            GetSearchSongsSource().Clear();
            if (IsCatalogPage && !IsCatalogHome) GetCatalogSongsSource().Clear();
            ChannelTabs.Clear(); VisibleChannelTabs.Clear(); MoreChannelTabs.Clear(); lastChannelRoute = null; channelEntryError = "";
            if (IsCatalogHome && publishedHomeState is { } home)
                catalogPages[1] = home;
            else if (IsCatalogPage)
            {
                ctx.ActiveSongs.Clear();
                OnClearSelectionForCatalog("音乐内容");
            }
            ctx.RefreshBindings();
        }
        finally { ctx.Publishing = wasPublishing; }
    }

    private void RetireCatalogLoad()
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

    private async Task LoadCatalogAsync(string route, string query, int pageNumber, JsonElement? continuation,
        CancellationToken token, bool refresh = false, string tag = "")
    {
        if (stopping || IsUnsupportedCatalogRoute(route)) return;
        token.ThrowIfCancellationRequested();
        if (IsCatalogSourceRoute(route) && !settings.CatalogSourceLoaded)
        {
            var submission = ctx.NavigationSubmission;
            try { await settings.EnsureCatalogSourceSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                if (!stopping && submission == ctx.NavigationSubmission) PublishCatalogSourceUnavailable(route);
                return;
            }
            if (stopping || submission != ctx.NavigationSubmission) return;
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
            InvalidateCatalogAction();
        var generation = ctx.AdvanceLoadGeneration();
        var stamp = ctx.NextStatusEpoch();
        ctx.LoadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var flight = new CatalogLoadFlight(request, generation, stamp, cancellation);
        ctx.LoadCancellation = cancellation;
        catalogLoadFlight = flight;
        ctx.ClearStatusQuiet();
        var previousSelection = ctx.CaptureContentSelection();
        if ((ctx.CurrentRoute != route || catalogLoadedPlatform != request.Key.Platform) &&
            !(IsCatalogHome && request.Target?.Private == true && request.Target.Operation is not ("daily" or "playlist")))
        {
            RetireDailyStyleEditor();
            ctx.SetLocation(new(route),
                heading: request.Target?.Title ?? (route.StartsWith("catalog-playlist/", StringComparison.Ordinal) ? "歌单"
                    : route is "catalog-daily" || route.StartsWith("catalog-daily/", StringComparison.Ordinal) ? "每日推荐" : "音乐内容"),
                clearCatalogSongs: true, clearSelection: true, clearTotal: true);
            if (request.Target?.Operation == "daily") CatalogDailyBranch = MusicCatalogDto.Text(request.Target.Parameters, "branch");
            if (IsChannelLanding) CatalogTagGroups = [];
            else if (!IsCatalogHome) { utilitySections.Clear(); }
            CatalogTags.Clear();
            catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null);
            catalogLoadedPlatform = request.Key.Platform; catalogLoadedTarget = request.Target;
            catalogPage = 1; catalogMore = false; catalogContinuation = null;
            catalogTag = catalogQuery = "";
            catalogArtwork = request.Target?.Artwork ?? "";
            catalogSubtitle = request.Target?.Subtitle ?? "";
            catalogSummaryCount = request.Target?.Count; catalogMembersLoaded = false;
        }
        if (IsChannelLanding && ctx.CurrentRoute == route) SelectChannelPage(route, request.Key.Platform);
        ctx.PublishContentSelection(previousSelection); ctx.RefreshBindings();
        flight.Work = RunCatalogFlightAsync(flight);
        loadDrains.Add(flight.Work);
        _ = ObserveCatalogLoadAsync(flight);
        await SubscribeCatalogLoad(flight, token);
    }

    private CatalogLoadRequest CaptureCatalogLoadRequest(string route, string query, int number,
        JsonElement? continuation, bool refresh, string tag)
    {
        var platform = IsCatalogSourceRoute(route) ? settings.CatalogSourcePlatform : SelectedCatalogPlatform.Key;
        var kind = CatalogSearchKind;
        var routeParts = route.Split('/', 3);
        if (routeParts.Length == 3) platform = routeParts[1];
        var target = CatalogRouteTarget(route, platform);
        if (target is not null) platform = target.Platform;
        var cursor = continuation?.Clone();
        var key = new CatalogLoadKey(route, query, number, cursor?.GetRawText() ?? "", refresh, tag,
            platform, kind, catalogPageCode, accounts.AccountEpoch, CatalogAccountObservation(platform),
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
                        ctx.RefreshBindings();
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
            if (ReferenceEquals(ctx.LoadCancellation, flight.Cancellation))
            {
                ctx.LoadCancellation = null;
                if (!disposed) { ctx.RefreshBindings(); }
            }
            flight.Cancellation.Dispose();
            if (!stopping && flight.Published && flight.Generation == ctx.LoadGeneration) StartHomeContinuation();
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
            MusicDto.Require(CatalogPlatforms.Any(item => item.Key == platform) && number >= 1);
            if (target?.Private == true) await ReadCatalogAccountForRequestAsync(generation, token);
            else if (platform is "wy" or "tx" or "kg") await accounts.EnsureAccountsForRequestAsync(token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != ctx.LoadGeneration || key.AccountObservation != CatalogAccountObservation(platform)) return;
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
                    if (target is not null) ApplyCatalogTarget(target, parameters, number, continuation, refresh);
                    else
                    {
                        parameters["page"] = number; parameters["limit"] = 100;
                        if (continuation is not null) parameters["continuation"] = continuation.Value;
                    }
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
                    _ => throw new MusicOperationException("INVALID_REQUEST")
                };
                if (operation == "home" && continuation is not null) parameters["continuation"] = continuation.Value;
            }
            var reply = await CallSettled("music.catalog." + operation, parameters, token);
            token.ThrowIfCancellationRequested();
            if (stopping || generation != ctx.LoadGeneration || key.AccountObservation != CatalogAccountObservation(platform)) return;
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
                Scroll: number > 1 && operation is ("home" or "channel" or "playlist") ? ctx.CaptureScroll() : 0,
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
                isCurrent: () => generation == ctx.LoadGeneration && key.AccountObservation == CatalogAccountObservation(platform),
                beforePublish: () =>
                {
                    if (number == 1) { catalogPages.Clear(); catalogCursors.Clear(); catalogCursors.Add(null); }
                    else if (operation is "playlist" or "home") catalogPages.Clear();
                    while (catalogCursors.Count <= number) catalogCursors.Add(null);
                    catalogCursors[number] = next; catalogPages[number] = state;
                })) return;
            if (operation == "home") SaveHomeContent(platform, number, reply);
            flight.Published = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!stopping && generation == ctx.LoadGeneration && stamp == ctx.StatusEpoch &&
                key.AccountObservation == CatalogAccountObservation(platform))
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
                    SelectedCatalogPlatform = CatalogPlatforms.FirstOrDefault(p => p.Key == catalogLoadedPlatform) ?? SelectedCatalogPlatform;
                    CatalogSearchKind = catalogLoadedKind;
                    if (catalogPages.TryGetValue(catalogPage, out var previous)) PublishPersonalCatalog(previous);
                    ctx.NotifyStatusChanged(); CatalogBindings();
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

    private CancellationTokenSource? homeContinuationCancellation;

    private void StartHomeContinuation()
    {
        if (stopping || !IsCatalogHome || !catalogMore || ctx.LoadCancellation is not null ||
            homeContinuationCancellation is not null) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        homeContinuationCancellation = request;
        Changed(nameof(CanCancelCatalog));
        var generation = ctx.LoadGeneration;
        var work = Read();
        loadDrains.Add(work);
        _ = Drain(work);
        async Task Drain(Task task) { try { await task; } finally { loadDrains.Remove(task); } }
        async Task Read()
        {
            await Task.Yield();
            try
            {
                while (!stopping && generation == ctx.LoadGeneration && !request.IsCancellationRequested && IsCatalogHome && catalogMore)
                {
                    int previous = catalogPage;
                    var next = CaptureCatalogLoadRequest(ctx.CurrentRoute, catalogQuery, previous + 1, catalogContinuation, true, catalogTag);
                    await LoadCatalogCoreAsync(new(next, generation, ctx.StatusEpoch, request));
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
        Func<bool>? isCurrent = null, Action? beforePublish = null, bool restore = false)
    {
        long generation = ctx.LoadGeneration, submission = ctx.NavigationSubmission;
        long observation = CatalogAccountObservation(platform);
        string account = CatalogAccountKey(platform);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loadDrains.Add(drained.Task);
        bool Current() => !stopping && generation == ctx.LoadGeneration && submission == ctx.NavigationSubmission &&
            observation == CatalogAccountObservation(platform) && account == CatalogAccountKey(platform) &&
            isCurrent?.Invoke() != false;
        try
        {
            // Retained receipts already follow repository publications through RepositorySongsChanged.
            if (!restore) await RefreshCatalogSongsAsync(RepositoryCards(state.Sections), token);
            token.ThrowIfCancellationRequested();
            if (!Current()) return false;
            beforePublish?.Invoke();
            Publish();
            return true;
        }
        catch (MusicOperationException error) when (error.Code is "STORAGE_UNAVAILABLE" or "CORRUPT_STORAGE" or "STORAGE")
        {
            if (Current()) { ctx.SetStatus(MusicCatalogDto.ErrorText(error.Code)); CatalogBindings(); }
            return false;
        }
        finally { loadDrains.Remove(drained.Task); drained.TrySetResult(); }

        void Publish()
        {
            ctx.Publishing = true;
            try
            {
                var previousSelection = ctx.CaptureContentSelection();
                ctx.SetLocation(new(route), heading: state.Title, clearCollection: true);
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
                {
                    ReconcileSections(CatalogSections, sectionsToPublish);
                }
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
                OnPublishCatalogSummary(state.Total, !append);
                var visibleCount = IsCatalogHome
                    ? homeDisplaySections.Sum(section => section.Items.Count)
                    : state.Sections.Sum(section => section.Items.Count);
                if (!append) ctx.SetStatusQuiet(visibleCount == 0 ? "暂无内容" : "");
                if (state.ContinuationBlocked) ctx.SetStatusQuiet(MusicCatalogDto.ErrorText("PAGINATION_STALLED"));
                ctx.PublishContentSelection(previousSelection);
                OnNotifyLibrarySelectionChanged(); ctx.RefreshBindings();
            }
            finally { ctx.Publishing = false; }
            ActivateCatalogOptionalReads(route, platform, state, initializeEntries);
            if (append) return;
            _ = ctx.Dispatcher.EnqueueAsync(() => { if (!stopping && generation == ctx.LoadGeneration) ctx.RestoreScroll(state.Scroll); }, DispatcherQueuePriority.Low);
        }
    }
}
