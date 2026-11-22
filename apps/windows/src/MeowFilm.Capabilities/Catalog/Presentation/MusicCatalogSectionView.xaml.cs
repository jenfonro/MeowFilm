using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicCatalogSectionView : UserControl
{
    private MusicCatalogController? catalog;
    private MusicSearchController? search;
    private MusicSettingsController? settings;
    private MusicCatalogSection? observed;
    private MusicCatalogSectionDisplay? display;
    private IReadOnlyList<MusicCatalogCard>? displayedItems;
    private IReadOnlyList<MusicCatalogTab>? displayedTabs;
    private readonly ObservableCollection<MusicCatalogCard> cards = [];
    private string layout = "";

    public MusicCatalogSectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveDisplay();
        Loaded += (_, _) =>
        {
            MusicCatalogView? found = null;
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicCatalogView view) { found = view; break; }
            var nextCatalog = found?.Catalog;
            var nextSearch = found?.Search;
            var nextSettings = found?.Settings;
            if (!ReferenceEquals(catalog, nextCatalog) || !ReferenceEquals(search, nextSearch) || !ReferenceEquals(settings, nextSettings))
            {
                DetachOwners();
                catalog = nextCatalog;
                search = nextSearch;
                settings = nextSettings;
                if (catalog is not null) catalog.PropertyChanged += Changed;
                if (search is not null) search.PropertyChanged += Changed;
                if (settings is not null) settings.PropertyChanged += Changed;
            }
            ObserveDisplay();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            DetachOwners();
            if (display is not null) display.PropertyChanged -= DisplayChanged; display = null;
            if (observed is not null) observed.PropertyChanged -= SectionChanged; observed = null;
        };
    }

    private void DetachOwners()
    {
        if (catalog is not null) catalog.PropertyChanged -= Changed;
        if (search is not null) search.PropertyChanged -= Changed;
        if (settings is not null) settings.PropertyChanged -= Changed;
        catalog = null;
        search = null;
        settings = null;
    }

    private void ObserveDisplay()
    {
        if (display is not null) display.PropertyChanged -= DisplayChanged;
        display = DataContext as MusicCatalogSectionDisplay;
        if (display is not null) display.PropertyChanged += DisplayChanged;
        ObserveSection();
    }

    private void DisplayChanged(object? sender, PropertyChangedEventArgs e) => ObserveSection();

    private void ObserveSection()
    {
        if (observed is not null) observed.PropertyChanged -= SectionChanged;
        observed = display?.Section ?? DataContext as MusicCatalogSection;
        if (observed is not null) observed.PropertyChanged += SectionChanged;
        Refresh();
    }

    private void SectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicCatalogSection.Items) or nameof(MusicCatalogSection.SelectedTab)) Refresh();
        if (e.PropertyName == nameof(MusicCatalogSection.IsLoading)) RefreshStatus();
        if (e.PropertyName == nameof(MusicCatalogSection.HorizontalOffset) && BodyHost.Content is Shelf shelf)
            shelf.HorizontalOffset = observed?.HorizontalOffset ?? 0;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicSettingsController.PlaylistListView)) Refresh();
        else if (e.PropertyName is nameof(MusicSearchController.SearchSongsLoading) or nameof(MusicSearchController.SearchPlaylistsLoading) or
            nameof(MusicCatalogController.CanCatalogBrowse) or nameof(MusicCatalogController.IsCatalogHome) or nameof(MusicSearchController.IsSearchOverview))
            RefreshStatus();
    }

    private DataTemplate CardTemplate(MusicCatalogCardLayout presentation) =>
        (DataTemplate)Resources["CardTemplate." + presentation];

    private void RefreshStatus()
    {
        if (observed is not { } section || catalog is null || search is null) return;
        bool loading = section.IsLoading || search.IsSearchOverview &&
            (section.Title == "单曲" ? search.SearchSongsLoading : section.Title == "歌单" && search.SearchPlaylistsLoading);
        SectionProgress.IsActive = loading;
        SectionProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = catalog.IsCatalogHome ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = search.IsSearchOverview ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.IsEnabled = RefreshButton.IsEnabled = catalog.CanCatalogBrowse;
    }

    private void Refresh()
    {
        if (observed is not { } section || catalog is null || search is null) return;
        RefreshStatus();
        TitleText.Text = section.Title;
        HeadingGrid.Visibility = section.Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Visibility = section.Action is { CanOpen: true } ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(MoreButton, "查看更多" + section.Title);
        TabsList.Visibility = section.HasTabs ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(displayedTabs, section.Tabs))
        {
            displayedTabs = section.Tabs;
            TabsList.ItemsSource = section.Tabs;
        }
        RefreshTabSelection(section);
        bool playlistListView = settings?.PlaylistListView == true;
        string nextLayout = section.IsGridSection ? "grid"
            : section.IsArtistUpdatesSection ? "rows"
            : section.IsAlbumChart ? "album"
            : section.IsSongShelfSection ? "songShelf"
            : section.IsRankingPreviewSection ? "ranking"
            : section.IsSongSection ? "songs"
            : section.IsArtistSection ? "artists"
            : section.IsPlaylistSection && playlistListView && !catalog.IsCatalogLanding ? "rows"
            : section.IsEntrySection ? "entries"
            : section.IsBannerSection ? "banner"
            : section.IsRankingCoverSection ? "rankCover"
            : "shelf";
        if (layout != nextLayout)
        {
            layout = nextLayout;
            displayedItems = null;
            if (layout is "shelf" or "songShelf" or "entries" or "banner" or "rankCover")
            {
                DataTemplate template = section.IsEntrySection
                    ? (DataTemplate)Resources["EntryCardTemplate"]
                    : layout == "songShelf"
                        ? CardTemplate(MusicCatalogCardLayout.Song)
                        : CardTemplate(section.IsBannerSection ? MusicCatalogCardLayout.Banner
                            : section.IsRankingCoverSection ? MusicCatalogCardLayout.RankCover
                            : MusicCatalogCardLayout.Playlist);
                var shelf = new Shelf
                {
                    RowsPerColumn = layout == "songShelf" ? 3 : 1,
                    ItemsSource = cards,
                    DataContext = section,
                    CardTemplate = template,
                    MinimumCardWidth = layout == "songShelf" ? 400 : section.IsEntrySection ? 150 : section.IsRankingCoverSection ? 120 : 124
                };
                shelf.SetArtworkItemKeySelector(item => ((MusicCatalogCard)item).Key);
                shelf.SetArtworkItemResourcesSelector(item => MusicCatalogCardView.ArtworkResources((MusicCatalogCard)item));
                BodyHost.Content = shelf;
            }
            else
            {
                var items = new ItemsControl
                {
                    ItemsSource = cards,
                    ItemTemplate = CardTemplate(layout switch
                    {
                        "album" => MusicCatalogCardLayout.AlbumRank,
                        "ranking" => MusicCatalogCardLayout.Ranking,
                        "songs" => MusicCatalogCardLayout.Song,
                        "artists" => MusicCatalogCardLayout.Artist,
                        "grid" => MusicCatalogCardLayout.Playlist,
                        _ => MusicCatalogCardLayout.Row
                    })
                };
                if (layout == "grid")
                    items.ItemsPanel = (ItemsPanelTemplate)Resources["GridCardsPanel"];
                else if (layout == "artists")
                    items.ItemsPanel = (ItemsPanelTemplate)Resources["ArtistCardsPanel"];
                else if (layout is "ranking" or "songs")
                    items.ItemsPanel = (ItemsPanelTemplate)Resources["TwoColumnCardsPanel"];
                BodyHost.Content = items;
            }
        }
        if (!ReferenceEquals(displayedItems, section.Items))
        {
            displayedItems = section.Items;
            ReconcileCards(cards, section.Items);
        }
        if (BodyHost.Content is Shelf current) current.DataContext = section;
    }

    private void RefreshTabSelection(MusicCatalogSection section)
    {
        for (int index = 0; index < TabsList.Items.Count; index++)
        {
            if (TabsList.ContainerFromIndex(index) is DependencyObject container &&
                DesktopVisual.Descendant<Button>(container) is { Tag: MusicCatalogTab tab } button)
                AutomationProperties.SetItemStatus(button, section.SelectedTab == tab.Id ? "已选中" : "");
        }
    }

    internal static void ReconcileCards(ObservableCollection<MusicCatalogCard> target, IReadOnlyList<MusicCatalogCard> incoming)
    {
        var keys = incoming.Select(card => card.Key).ToHashSet(StringComparer.Ordinal);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!keys.Contains(target[index].Key)) target.RemoveAt(index);
        for (int index = 0; index < incoming.Count; index++)
        {
            var card = incoming[index];
            if (index < target.Count && ReferenceEquals(target[index], card)) continue;
            int found = index;
            while (found < target.Count && target[found].Key != card.Key) found++;
            if (found == target.Count) target.Insert(index, card);
            else
            {
                if (found != index) target.Move(found, index);
                if (!ReferenceEquals(target[index], card)) target[index] = card;
            }
        }
        while (target.Count > incoming.Count) target.RemoveAt(target.Count - 1);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is not null) await catalog.RefreshCatalogAsync();
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (search is not null) await search.OpenSearchTabAsync(observed?.Title == "歌单" ? "playlists" : "songs");
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is not null && observed?.Action is { CanOpen: true } action)
            await catalog.OpenCatalogCardAsync(action);
    }

    private void SectionTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicCatalogTab selected })
            catalog?.SelectCatalogSectionTab(selected);
    }

    private void SectionTab_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicCatalogTab tab } button)
            AutomationProperties.SetItemStatus(button, observed?.SelectedTab == tab.Id ? "已选中" : "");
    }

    private async void EntryOpen(object sender, RoutedEventArgs e)
    {
        if (catalog is not null && sender is EntryCard { DataContext: MusicCatalogCard card })
            await catalog.OpenCatalogCardAsync(card);
    }

    private async void EntryPlay(object sender, RoutedEventArgs e)
    {
        if (catalog is not null && sender is EntryCard { DataContext: MusicCatalogCard card })
            await catalog.PlayCatalogCardAsync(card);
    }
}
