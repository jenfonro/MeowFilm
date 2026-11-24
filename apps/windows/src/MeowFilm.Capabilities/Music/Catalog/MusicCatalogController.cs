using System.Collections.ObjectModel;

namespace MeowFilm.Capabilities;

/// <summary>
/// Lean Layer-5 presentation coordinator for music catalog browsing, delegating public home/channels
/// loading to <see cref="MusicCatalogBrowseService"/>, personal/daily-style routing to
/// <see cref="MusicPersonalCatalogService"/>, and playlist/card actions &amp; network-playlist CRUD to
/// <see cref="MusicPlaylistDetailService"/>.
/// </summary>
public sealed class MusicCatalogController : MusicController
{
    private readonly MusicAccountsController accounts;
    private readonly MusicSearchController search;
    private readonly MusicCatalogActionState catalogActions;
    private readonly MusicContentState contentState;
    private readonly MusicPersonalCatalogService personal;
    private readonly MusicCatalogBrowseService browse;
    private readonly MusicPlaylistDetailService detail;
    private readonly MusicVipPage vipPage;

    internal MusicCatalogController(
        MusicRuntimeContext ctx,
        MusicSettingsController settings,
        MusicAccountsController accounts,
        MusicPlaybackController playbackController,
        MusicQueueController queue,
        MusicSearchController search,
        MusicCatalogActionState catalogActions,
        MusicContentState contentState) : base(ctx)
    {
        this.accounts = accounts;
        this.search = search;
        this.catalogActions = catalogActions;
        this.contentState = contentState;
        Action<string> notify = property => Changed(property);
        personal = new(ctx, accounts, queue, search, catalogActions, contentState, notify);
        browse = new(ctx, settings, accounts, search, personal, catalogActions, contentState, notify, CatalogBindings);
        detail = new(ctx, settings, accounts, playbackController, queue, browse, personal, catalogActions, contentState, notify);
        vipPage = new(application, ctx.Dispatcher);
    }

    // Explicit upward events wired at MusicCapability composition root.
    internal event Func<CancellationToken, Task>? PlaylistsChanged
    {
        add => detail.PlaylistsChanged += value;
        remove => detail.PlaylistsChanged -= value;
    }
    internal event Func<SongRow, Task>? SongAppendNextRequested
    {
        add => detail.SongAppendNextRequested += value;
        remove => detail.SongAppendNextRequested -= value;
    }
    internal event Func<SongRow, bool, Task>? SongFavoriteRequested
    {
        add => detail.SongFavoriteRequested += value;
        remove => detail.SongFavoriteRequested -= value;
    }
    internal event Action<ObservableCollection<MusicCatalogSection>>? ChannelPageRetired
    {
        add => browse.ChannelPageRetired += value;
        remove => browse.ChannelPageRetired -= value;
    }

    // Platform & search-kind choices
    public IReadOnlyList<MusicCatalogPlatform> CatalogPlatforms => personal.CatalogPlatforms;
    public IReadOnlyList<MusicCatalogPlatform> CatalogPlatformChoices => personal.PlatformChoicesFor(browse.CatalogLoadedPlatform);
    public MusicCatalogPlatform SelectedCatalogPlatform
    {
        get => personal.SelectedCatalogPlatform;
        internal set => personal.SelectedCatalogPlatform = value;
    }
    public string[] CatalogSearchKinds { get; } = ["歌曲", "歌单"];
    public string CatalogSearchKind
    {
        get => search.SearchKindLabel;
        set => search.SearchKindLabel = value;
    }

    // Sections, channels, tags & browse state
    internal IEnumerable<ObservableCollection<MusicCatalogSection>> RetainedChannelSections => browse.RetainedChannelSections;
    public ObservableCollection<MusicCatalogSection> CatalogSections => browse.CatalogSections;
    public ObservableCollection<MusicCatalogSection> HomeSections => browse.HomeSections;
    public ObservableCollection<MusicCatalogSection> ChannelSections => browse.ChannelSections;
    public ObservableCollection<MusicCatalogSection> SearchOverviewSections => search.OverviewSections;
    internal ObservableCollection<MusicCatalogSection> SearchPlaylistSections => search.PlaylistSections;
    public ObservableCollection<MusicCatalogSection> UtilitySections => browse.UtilitySections;
    public ObservableCollection<MusicCatalogSection> DisplayCatalogSections => browse.DisplayCatalogSections;
    public ObservableCollection<MusicCatalogCard> ChannelTabs => browse.ChannelTabs;
    public ObservableCollection<MusicCatalogCard> VisibleChannelTabs => browse.VisibleChannelTabs;
    public ObservableCollection<MusicCatalogCard> MoreChannelTabs => browse.MoreChannelTabs;
    public ObservableCollection<MusicCatalogTag> CatalogTags => browse.CatalogTags;
    public IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups => browse.CatalogTagGroups;
    public MusicCatalogTag? SelectedCatalogTag => browse.SelectedCatalogTag;

    public bool IsCatalogContent => browse.IsCatalogContent;
    public bool IsCatalogBrowsePage => browse.IsCatalogBrowsePage;
    public bool IsUtilityCatalogSurface => browse.IsUtilityCatalogSurface;
    public bool ShowCatalogLoading => browse.ShowCatalogLoading;
    public string Status => ctx.Status;
    public bool IsCatalogHome => browse.IsCatalogHome;
    public bool IsChannelDirectory => browse.IsChannelDirectory;
    public bool IsVipChannel => browse.IsVipChannel;
    public string CurrentChannelKey => browse.CurrentChannelKey;
    public bool HasMoreChannelTabs => browse.HasMoreChannelTabs;
    public bool HasChannelTabs => browse.HasChannelTabs;
    public bool IsCatalogUtilityPage => browse.IsCatalogUtilityPage;
    public bool IsCatalogLanding => browse.IsCatalogLanding;
    public bool IsChannelLanding => browse.IsChannelLanding;
    public bool IsCatalogPage => browse.IsCatalogPage;
    public bool IsCatalogPlaylist => browse.IsCatalogPlaylist;
    public bool IsCatalogArtist => browse.IsCatalogArtist;
    public bool IsCatalogDetailPage => browse.IsCatalogDetailPage;
    public bool CanCatalogBrowse => browse.CanCatalogBrowse;
    public bool CanCatalogNext => browse.CanCatalogNext;
    public bool CanCatalogPrevious => browse.CanCatalogPrevious;
    public bool CanLoadMoreCatalogPlaylist => browse.CanLoadMoreCatalogPlaylist;
    public bool HasCatalogTags => browse.HasCatalogTags;
    public bool CanCatalogAction => detail.CanCatalogAction;
    public bool CanPlayCatalogPlaylist => detail.CanPlayCatalogPlaylist;
    public bool CanCancelCatalog => ctx.LoadCancellation is not null || browse.HasHomeContinuation || detail.HasActiveAction;
    public string CatalogPageText => browse.CatalogPageText;
    public bool ShowDetailLoading => browse.ShowDetailLoading;
    public string DetailSubtitle => browse.DetailSubtitle;

    internal string CatalogArtwork => browse.CatalogArtwork;
    internal int? CatalogSummaryCount => browse.CatalogSummaryCount;
    internal string CatalogLoadedPlatform => browse.CatalogLoadedPlatform;
    internal string CatalogQuery => browse.CatalogQuery;
    internal CatalogTarget? CatalogLoadedTarget => personal.LoadedTarget;
    internal string HomePlatform => browse.HomePlatform;
    internal string CatalogAccountNotice => browse.CatalogAccountNotice;

    // Personal, chart, FM & daily style state
    public IReadOnlyList<MusicCatalogChoice> CatalogPersonalKinds => personal.CatalogPersonalKinds;
    public MusicCatalogChoice[] CatalogFmScenes => MusicCatalogDispatcher.FmScenes;
    public string CatalogPersonalKind => personal.CatalogPersonalKind;
    public string CatalogDailyBranch { get => personal.CatalogDailyBranch; set => personal.CatalogDailyBranch = value; }
    public string CatalogFmMode { get => personal.CatalogFmMode; set => personal.CatalogFmMode = value; }
    public string CatalogFmScene { get => personal.CatalogFmScene; set => personal.CatalogFmScene = value; }
    public bool IsCatalogPersonal => personal.IsCatalogPersonal;
    public bool IsCatalogDaily => personal.IsCatalogDaily;
    public bool IsCatalogFm => personal.IsCatalogFm;
    public bool IsCatalogFmScene => personal.IsCatalogFmScene;
    public bool IsCatalogBatch => personal.IsCatalogBatch;
    public bool IsCatalogCollection => personal.IsCatalogCollection;
    public bool CanCatalogWy => personal.CanCatalogWy;
    public bool CanCatalogBatchRefresh => personal.CanCatalogBatchRefresh;
    public string CatalogAccountText => personal.CatalogAccountText;

    public MusicCatalogChoice[] CatalogChartKinds => personal.CatalogChartKinds;
    public MusicCatalogChoice[] CatalogChartPeriods => personal.CatalogChartPeriods;
    public string CatalogChartKind { get => personal.CatalogChartKind; set => personal.CatalogChartKind = value; }
    public string CatalogChartPeriod { get => personal.CatalogChartPeriod; set => personal.CatalogChartPeriod = value; }
    public int CatalogChartYear { get => personal.CatalogChartYear; set => personal.CatalogChartYear = value; }
    public bool IsCatalogSalesChart => personal.IsCatalogSalesChart;
    public bool CatalogChartHasPeriods => personal.CatalogChartHasPeriods;
    public bool CatalogChartIsYearly => personal.CatalogChartIsYearly;

    public string DailyOtherBranch => personal.DailyOtherBranch;
    public string DailyOtherBranchTitle => personal.DailyOtherBranchTitle;
    public bool IsDailyStyle => personal.IsDailyStyle;
    public bool IsDailyStyleEditorOpen => personal.IsDailyStyleEditorOpen;
    public bool CanEditDailyStyle => personal.CanEditDailyStyle;
    public bool CanConfirmDailyStyle => personal.CanConfirmDailyStyle;
    public string DailyStyleSelectionText => personal.DailyStyleSelectionText;
    public IReadOnlyList<MusicCatalogTagGroup> DailyStyleGroups => personal.DailyStyleGroups;

    // Network playlists bookmarking state
    public bool ShowsNetworkPlaylistAction => detail.ShowsNetworkPlaylistAction;
    public bool CanSaveNetworkPlaylist => detail.CanSaveNetworkPlaylist;
    public bool CanToggleNetworkPlaylist => detail.CanToggleNetworkPlaylist;
    public bool NetworkPlaylistSaved => detail.NetworkPlaylistSaved;
    public string NetworkPlaylistActionText => detail.NetworkPlaylistActionText;

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
            return detail.PlayCatalogSongAsync("wy", current.ResourceId, () => vipPage.Owns(current));
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
            nameof(CatalogPlatformChoices), nameof(SelectedCatalogPlatform), nameof(CatalogSearchKind),
            nameof(ShowDetailLoading), nameof(DetailSubtitle), nameof(CanLoadMoreCatalogPlaylist)
        })
            Changed(name);
        contentState.NotifyDetailBindings();
        personal.DailyStyleBindings();
        personal.PersonalCatalogBindings();
        detail.NetworkPlaylistBindings();
        search.SearchBindings();
    }

    internal void NetworkPlaylistBindings() => detail.NetworkPlaylistBindings();

    // Browse & lifecycle coordination
    public Task PrepareStartupAsync(CancellationToken cancellationToken = default) => browse.PrepareStartupAsync(cancellationToken);
    internal Task ClearHomeSnapshotAsync(CancellationToken token = default) => browse.ClearHomeSnapshotAsync(token);
    internal Task<(bool HasSnapshot, int PageCount, int SectionCount, int CardCount)> GetHomeSnapshotStatsAsync(CancellationToken token = default) => browse.GetHomeSnapshotStatsAsync(token);
    internal void ClearCatalogAccountNotice() => browse.ClearCatalogAccountNotice();
    internal void CancelStartupForShutdown() => browse.CancelStartupForShutdown();
    internal void OnLoadGenerationAdvanced() => browse.OnLoadGenerationAdvanced();
    internal ValueTask DisposeResourcesAsync() => browse.DisposeResourcesAsync();
    internal void RetireCatalogPreviewSurface() => browse.RetireCatalogPreviewSurface();
    internal void UpdateCatalogVisibleCards(IEnumerable<MusicCatalogCard> cards) => browse.UpdateCatalogVisibleCards(cards);
    internal void OnCatalogAccountObserved(string platform, long observation) => browse.OnCatalogAccountObserved(platform, observation);
    internal void RefreshHomeDisplay() => browse.RefreshHomeDisplay();
    internal void BeginHomeAccountRecovery(bool forceRead) => browse.BeginHomeAccountRecovery(forceRead);
    internal void RetireCatalogSourcePages() => browse.RetireCatalogSourcePages();
    public void RetireCatalogSession() => browse.RetireCatalogSession();
    internal void NotifyRouteChanged(bool sectionsChanged) => browse.NotifyRouteChanged(sectionsChanged);
    internal void RememberCatalogLocation() => browse.RememberCatalogLocation();
    internal void BeginSearchCatalogState(string keyword, string kindLabel) => browse.BeginSearchCatalogState(keyword, kindLabel);
    internal void PublishSearchCatalogMetadata(string keyword, string kindLabel) => browse.PublishSearchCatalogMetadata(keyword, kindLabel);
    internal void ClearCatalogTags() => browse.ClearCatalogTags();
    internal void ClearCatalogSectionsAndTags() => browse.ClearCatalogSectionsAndTags();
    internal Task CatalogNavigateAsync(string route, CancellationToken token) => browse.CatalogNavigateAsync(route, token);
    public Task ActivateCatalogLandingAsync() => browse.ActivateCatalogLandingAsync();
    internal Task ChangeCatalogPlatformAsync(MusicCatalogPlatform platform) => browse.ChangeCatalogPlatformAsync(platform);
    public Task CatalogSearchKindAsync() => search.IsSearchPage ? search.SwitchSearchKindAsync() : Task.CompletedTask;
    public Task CatalogTagAsync(MusicCatalogTag tag) => browse.CatalogTagAsync(tag);
    public Task RefreshCatalogAsync() => browse.RefreshCatalogAsync();
    public Task CatalogPageAsync(int direction) => browse.CatalogPageAsync(direction);
    public Task LoadMoreCatalogPlaylistAsync() => CanLoadMoreCatalogPlaylist ? browse.CatalogPageAsync(1) : Task.CompletedTask;
    internal bool ContainsCatalogCard(MusicCatalogCard card) => browse.ContainsCatalogCard(card);
    internal void SelectCatalogSectionTab(MusicCatalogTab tab) => browse.SelectCatalogSectionTab(tab);
    public void CancelCatalog() => browse.CancelCatalog();
    internal void RefreshSongsFromRepository(IReadOnlySet<string> ids, Func<string, Library.MusicSongRecord?> read,
        long publication, IEnumerable<MusicCatalogCard> cachedSearchCards) =>
        browse.RefreshSongsFromRepository(ids, read, publication, cachedSearchCards);

    // Personal & daily style coordination
    internal bool IsUnsupportedCatalogRoute(string route) => personal.IsUnsupportedCatalogRoute(route);
    internal void RetireDailyStyleEditor() => personal.RetireDailyStyleEditor();
    internal Task OpenCatalogTargetAsync(CatalogTarget target, string? id = null) =>
        personal.OpenCatalogTargetAsync(target, browse.RememberCatalogLocation, browse.RetireCatalogLocation, id);
    internal Task ChangeCatalogChartKindAsync(string kind) =>
        personal.ChangeCatalogChartKindAsync(kind, browse.RememberCatalogLocation, browse.RetireCatalogLocation);
    internal Task ChangeCatalogChartPeriodAsync(string period) =>
        personal.ChangeCatalogChartPeriodAsync(period, browse.RememberCatalogLocation, browse.RetireCatalogLocation);
    internal Task ChangeCatalogChartYearAsync(int year) =>
        personal.ChangeCatalogChartYearAsync(year, browse.RememberCatalogLocation, browse.RetireCatalogLocation);
    public Task ChangeCatalogChartAsync() =>
        personal.ChangeCatalogChartAsync(browse.RememberCatalogLocation, browse.RetireCatalogLocation);
    public Task CatalogOptionsChangedAsync() =>
        personal.CatalogOptionsChangedAsync(route => browse.LoadCatalogAsync(route, "", 1, null, lifetime.Token));
    internal Task ChangeCatalogPersonalKindAsync(MusicCatalogChoice choice) =>
        personal.ChangeCatalogPersonalKindAsync(choice, route => browse.LoadCatalogAsync(route, "", 1, null, lifetime.Token));
    public async Task ReadCatalogAccountAsync()
    {
        if (!CanCatalogBrowse) return;
        await accounts.ReadAccountsAsync();
        if (browse.CatalogLoadedPlatform.Length > 0 && personal.AccountStamp.Length > 0 &&
            personal.AccountStamp != personal.CatalogAccountKey(browse.CatalogLoadedPlatform)) RetireCatalogSession();
        CatalogBindings();
    }
    public Task SwitchDailyBranchAsync(string branch) =>
        personal.SwitchDailyBranchAsync(branch, browse.RememberCatalogLocation, browse.RetireCatalogLocation);
    public Task OpenDailyStyleEditorAsync() => personal.OpenDailyStyleEditorAsync();
    public void ToggleDailyStyleTag(MusicCatalogTag tag) => personal.ToggleDailyStyleTag(tag);
    public void CancelDailyStyleEditor() => personal.RetireDailyStyleEditor();
    public Task ConfirmDailyStyleAsync() => personal.ConfirmDailyStyleAsync(
        () => browse.CurrentPageState,
        browse.RetireCatalogLoad,
        browse.RetireSavedDailyStyleSnapshots,
        browse.ClearCurrentDailyPageState,
        browse.PublishDailyStylePageAsync,
        CatalogBindings);

    // Playlist detail, card actions & network playlists coordination
    public void InvalidateCatalogAction() => catalogActions.Invalidate();
    internal void CommitCatalogPlayback() => detail.CommitCatalogPlayback();
    internal void PublishCatalogActionError(Exception error) => detail.PublishCatalogActionError(error);
    internal Task CatalogBusinessError(string code) => detail.CatalogBusinessError(code);
    internal Library.MusicPlayContextRecord? CaptureCatalogFeedbackContext() => detail.CaptureCatalogFeedbackContext();
    public Task OpenCatalogCardAsync(MusicCatalogCard card) => detail.OpenCatalogCardAsync(card);
    public Task PlayCatalogCardAsync(MusicCatalogCard card) => detail.PlayCatalogCardAsync(card);
    public Task FavoriteCatalogCardAsync(MusicCatalogCard card) => detail.FavoriteCatalogCardAsync(card);
    public Task ToggleCatalogCardFavoriteAsync(MusicCatalogCard card) => detail.ToggleCatalogCardFavoriteAsync(card);
    public Task SimilarCatalogCardAsync(MusicCatalogCard card) => detail.SimilarCatalogCardAsync(card);
    public Task AlbumCatalogCardAsync(MusicCatalogCard card) => detail.AlbumCatalogCardAsync(card);
    public Task QueueCatalogCardAsync(MusicCatalogCard card) => detail.QueueCatalogCardAsync(card);
    public Task HeartbeatFavoritesCatalogCardAsync(MusicCatalogCard card) => detail.HeartbeatFavoritesCatalogCardAsync(card);
    public Task CatalogQueueRecommendationsAsync() => detail.CatalogQueueRecommendationsAsync();
    public Task PlayCatalogPlaylistAsync() => detail.PlayCatalogPlaylistAsync();
    public Task SaveCatalogPlaylistAsync() => detail.SaveCatalogPlaylistAsync();
    public Task ToggleNetworkPlaylistAsync() => detail.ToggleNetworkPlaylistAsync();
    internal PlaylistRow? NetworkPlaylistForCard(MusicCatalogCard card) => detail.NetworkPlaylistForCard(card);
    internal Task SaveNetworkPlaylistAsync(MusicCatalogCard? entry = null) => detail.SaveNetworkPlaylistAsync(entry);
    public Task RemoveNetworkPlaylistAsync(string id) => detail.RemoveNetworkPlaylistAsync(id);
    internal Task RemoveNetworkPlaylistAsync(PlaylistRow reference) => detail.RemoveNetworkPlaylistAsync(reference);
    internal void RequireCatalogContent() => personal.RequireCatalogContent(browse.IsCatalogHome, browse.CatalogLoadedPlatform);
}
