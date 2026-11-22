using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

/// <summary>Isolated catalog, discovery, library detail, and music search state slice.</summary>
public sealed class MusicCatalogState(MusicCapability owner) : INotifyPropertyChanged
{
    internal MusicCapability Owner { get; } = owner;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public MusicContentPage ContentPage => Owner.ContentPage;
    public string Heading => Owner.Heading;
    public string Status => Owner.Status;
    public string SearchStatus => Owner.SearchStatus;
    public string CountText => Owner.CountText;
    public string PageText => Owner.PageText;
    public string DetailArtwork => Owner.DetailArtwork;
    public string DetailArtworkName => Owner.DetailArtworkName;
    public string DetailCountText => Owner.DetailCountText;
    public string DetailSectionTitle => Owner.DetailSectionTitle;
    public string DetailSubtitle => Owner.DetailSubtitle;
    public string NetworkPlaylistActionText => Owner.NetworkPlaylistActionText;
    public string DailyOtherBranch => Owner.DailyOtherBranch;
    public string DailyOtherBranchTitle => Owner.DailyOtherBranchTitle;
    public string DailyStyleSelectionText => Owner.DailyStyleSelectionText;
    public string? HistoryDate => Owner.HistoryDate;
    public string SearchTab => Owner.SearchTab;
    public string CurrentChannelKey => Owner.CurrentChannelKey;

    public bool CanEdit => Owner.CanEdit;
    public bool CanDeleteCollection => Owner.CanDeleteCollection;
    public bool CanReorderPlaylist => Owner.CanReorderPlaylist;
    public bool CanLoadMoreCollection => Owner.CanLoadMoreCollection;
    public bool CanPreviousPage => Owner.CanPreviousPage;
    public bool CanNextPage => Owner.CanNextPage;
    public bool CanPlayContentDetail => Owner.CanPlayContentDetail;
    public bool CanPlayCatalogPlaylist => Owner.CanPlayCatalogPlaylist;
    public bool CanToggleNetworkPlaylist => Owner.CanToggleNetworkPlaylist;
    public bool CanEditDailyStyle => Owner.CanEditDailyStyle;
    public bool CanConfirmDailyStyle => Owner.CanConfirmDailyStyle;
    public bool IsDailyStyleEditorOpen => Owner.IsDailyStyleEditorOpen;
    public bool IsCollectionPage => Owner.IsCollectionPage;
    public bool IsContentDetailPage => Owner.IsContentDetailPage;
    public bool IsCatalogDetailPage => Owner.IsCatalogDetailPage;
    public bool IsCatalogDaily => Owner.IsCatalogDaily;
    public bool IsDailyStyle => Owner.IsDailyStyle;
    public bool IsHistory => Owner.IsHistory;
    public bool IsSearchPage => Owner.IsSearchPage;
    public bool ShowDetailLoading => Owner.ShowDetailLoading;
    public bool ShowSongPager => Owner.ShowSongPager;
    public bool ShowsNetworkPlaylistAction => Owner.ShowsNetworkPlaylistAction;
    public bool ShowCatalogLoading => Owner.ShowCatalogLoading;
    public bool ShowSearchLoading => Owner.ShowSearchLoading;
    public bool SearchSongsLoading => Owner.SearchSongsLoading;
    public bool SearchPlaylistsLoading => Owner.SearchPlaylistsLoading;

    public SongRow? CatalogSelectedSong { get => Owner.CatalogSelectedSong; set => Owner.CatalogSelectedSong = value; }
    public SongRow? SearchSelectedSong { get => Owner.SearchSelectedSong; set => Owner.SearchSelectedSong = value; }
    public ObservableCollection<SongRow> CatalogSongsSource => Owner.CatalogSongsSource;
    public ObservableCollection<SongRow> SearchSongsSource => Owner.SearchSongsSource;
    public ObservableCollection<string> HistoryDates => Owner.HistoryDates;
    public ObservableCollection<ArchivedPlaylist> Archives => Owner.Archives;
    public IReadOnlyList<MusicCatalogTagGroup> DailyStyleGroups => Owner.DailyStyleGroups;

    // Catalog & Channels & VIP
    public bool CanCancelCatalog => Owner.CanCancelCatalog;
    public bool CanCatalogAction => Owner.CanCatalogAction;
    public bool CanCatalogBrowse => Owner.CanCatalogBrowse;
    public bool CanCatalogNext => Owner.CanCatalogNext;
    public bool CanCatalogPrevious => Owner.CanCatalogPrevious;
    public bool CanCatalogWy => Owner.CanCatalogWy;
    public string CatalogAccountText => Owner.CatalogAccountText;
    public bool CatalogChartHasPeriods => Owner.CatalogChartHasPeriods;
    public bool CatalogChartIsYearly => Owner.CatalogChartIsYearly;
    public string CatalogChartKind => Owner.CatalogChartKind;
    public MusicCatalogChoice[] CatalogChartKinds => Owner.CatalogChartKinds;
    public string CatalogChartPeriod => Owner.CatalogChartPeriod;
    public MusicCatalogChoice[] CatalogChartPeriods => Owner.CatalogChartPeriods;
    public int CatalogChartYear => Owner.CatalogChartYear;
    public string CatalogPageText => Owner.CatalogPageText;
    public string CatalogPersonalKind => Owner.CatalogPersonalKind;
    public IReadOnlyList<MusicCatalogChoice> CatalogPersonalKinds => Owner.CatalogPersonalKinds;
    public IReadOnlyList<MusicCatalogPlatform> CatalogPlatformChoices => Owner.CatalogPlatformChoices;
    public string CatalogSearchKind => Owner.CatalogSearchKind;
    public string[] CatalogSearchKinds => Owner.CatalogSearchKinds;
    public IReadOnlyList<MusicCatalogTagGroup> CatalogTagGroups => Owner.CatalogTagGroups;
    public MusicCatalogPlatform SelectedCatalogPlatform => Owner.SelectedCatalogPlatform;
    public bool HasCatalogTags => Owner.HasCatalogTags;
    public bool HasChannelTabs => Owner.HasChannelTabs;
    public bool HasMoreChannelTabs => Owner.HasMoreChannelTabs;
    public bool HasSearchSession => Owner.HasSearchSession;
    public bool IsCatalogBatch => Owner.IsCatalogBatch;
    public bool IsCatalogHome => Owner.IsCatalogHome;
    public bool IsCatalogPersonal => Owner.IsCatalogPersonal;
    public bool IsCatalogSalesChart => Owner.IsCatalogSalesChart;
    public bool IsCatalogUtilityPage => Owner.IsCatalogUtilityPage;
    public bool IsSearchOverview => Owner.IsSearchOverview;
    public bool IsUtilityCatalogSurface => Owner.IsUtilityCatalogSurface;
    public ObservableCollection<MusicCatalogCard> VisibleChannelTabs => Owner.VisibleChannelTabs;
    public ObservableCollection<MusicCatalogCard> MoreChannelTabs => Owner.MoreChannelTabs;
    public ObservableCollection<MusicCatalogSection> CatalogSections => Owner.CatalogSections;
    public MusicVipPage VipPage => Owner.VipPage;

    // Discovery
    public bool CanCancelDiscovery => Owner.CanCancelDiscovery;
    public bool CanRotateDiscovery => Owner.CanRotateDiscovery;
    public bool CanUseDiscovery => Owner.CanUseDiscovery;
    public bool HasSearchHistory => Owner.HasSearchHistory;
    public string DiscoveryStatus => Owner.DiscoveryStatus;
    public ObservableCollection<MusicDiscoveryChart> DiscoveryCharts => Owner.DiscoveryCharts;
    public ObservableCollection<MusicDiscoveryFailure> DiscoveryFailures => Owner.DiscoveryFailures;
    public ObservableCollection<MusicDiscoveryKeyword> DiscoveryHot => Owner.DiscoveryHot;
    public ObservableCollection<MusicDiscoveryKeyword> DiscoveryRecommended => Owner.DiscoveryRecommended;
}
