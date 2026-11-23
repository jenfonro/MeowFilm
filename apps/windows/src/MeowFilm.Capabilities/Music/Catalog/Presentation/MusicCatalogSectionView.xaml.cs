using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicCatalogSectionView : UserControl, INotifyPropertyChanged
{
    private MusicCatalogContext? context;
    private MusicCatalogSection? observed;
    private MusicCatalogSectionDisplay? display;
    private IReadOnlyList<MusicCatalogCard>? displayedItems;
    private IReadOnlyList<MusicCatalogTab>? displayedTabs;
    private readonly ObservableCollection<MusicCatalogCard> cards = [];
    private readonly HashSet<MusicCatalogCardView> mountedCards = [];
    private readonly HashSet<EntryCard> mountedEntries = [];
    private string layout = "";

    public string SectionTitle { get; private set; } = "";
    public bool HasHeading => SectionTitle.Length > 0;
    public bool IsSectionLoading { get; private set; }
    public bool ShowActionButton { get; private set; }
    public bool ShowRefreshButton { get; private set; }
    public bool ShowMoreButton { get; private set; }
    public bool CanBrowse { get; private set; }
    public string MoreAccessibleName => "查看更多" + SectionTitle;
    public bool HasTabs { get; private set; }
    public IReadOnlyList<MusicCatalogTab>? SectionTabs { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public MusicCatalogSectionView()
    {
        InitializeComponent();
        ConfigureShelfArtwork(SingleRowShelf);
        ConfigureShelfArtwork(SongColumnShelf);
        DataContextChanged += (_, _) => ObserveDisplay();
        Loaded += (_, _) => ObserveDisplay();
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            DetachOwners();
            if (display is not null) display.PropertyChanged -= DisplayChanged;
            display = null;
            if (observed is not null) observed.PropertyChanged -= SectionChanged;
            observed = null;
            mountedCards.Clear();
            mountedEntries.Clear();
        };
    }

    private static void ConfigureShelfArtwork(Shelf shelf)
    {
        shelf.SetArtworkItemKeySelector(item => ((MusicCatalogCard)item).Key);
        shelf.SetArtworkItemResourcesSelector(item => MusicCatalogCardView.ArtworkResources((MusicCatalogCard)item));
    }

    private void AttachContext(MusicCatalogContext? next)
    {
        if (ReferenceEquals(context, next)) return;
        DetachOwners();
        context = next;
        if (context is not null)
        {
            context.Catalog.PropertyChanged += Changed;
            context.Search.PropertyChanged += Changed;
            context.Settings.PropertyChanged += Changed;
        }
        foreach (var card in mountedCards) card.AttachContext(context);
        foreach (var entry in mountedEntries) entry.AttachContext(context);
    }

    private void DetachOwners()
    {
        if (context is not null)
        {
            context.Catalog.PropertyChanged -= Changed;
            context.Search.PropertyChanged -= Changed;
            context.Settings.PropertyChanged -= Changed;
            context = null;
        }
    }

    private void ObserveDisplay()
    {
        if (display is not null) display.PropertyChanged -= DisplayChanged;
        display = DataContext as MusicCatalogSectionDisplay;
        if (display is not null) display.PropertyChanged += DisplayChanged;
        AttachContext(display?.Context);
        ObserveSection();
    }

    private void DisplayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCatalogSectionDisplay.Context))
            AttachContext(display?.Context);
        ObserveSection();
    }

    private void ObserveSection()
    {
        if (observed is not null) observed.PropertyChanged -= SectionChanged;
        observed = display?.Section ?? DataContext as MusicCatalogSection;
        if (observed is not null)
        {
            observed.PropertyChanged += SectionChanged;
            observed.SyncTabSelection();
        }
        Refresh();
    }

    private void SectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicCatalogSection.Items) or nameof(MusicCatalogSection.SelectedTab)) Refresh();
        if (e.PropertyName == nameof(MusicCatalogSection.IsLoading)) RefreshStatus();
        if (e.PropertyName == nameof(MusicCatalogSection.HorizontalOffset) && observed is { } section)
        {
            if (SingleRowShelf.Visibility == Visibility.Visible) SingleRowShelf.HorizontalOffset = section.HorizontalOffset;
            else if (SongColumnShelf.Visibility == Visibility.Visible) SongColumnShelf.HorizontalOffset = section.HorizontalOffset;
        }
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

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));

    private void RefreshStatus()
    {
        if (observed is not { } section || context is null) return;
        bool loading = section.IsLoading || context.Search.IsSearchOverview &&
            (section.Title == "单曲" ? context.Search.SearchSongsLoading : section.Title == "歌单" && context.Search.SearchPlaylistsLoading);
        bool refresh = context.Catalog.IsCatalogHome;
        bool more = context.Search.IsSearchOverview;
        bool browse = context.Catalog.CanCatalogBrowse;
        if (IsSectionLoading != loading) { IsSectionLoading = loading; Notify(nameof(IsSectionLoading)); }
        if (ShowRefreshButton != refresh) { ShowRefreshButton = refresh; Notify(nameof(ShowRefreshButton)); }
        if (ShowMoreButton != more) { ShowMoreButton = more; Notify(nameof(ShowMoreButton)); }
        if (CanBrowse != browse) { CanBrowse = browse; Notify(nameof(CanBrowse)); }
    }

    private void Refresh()
    {
        if (observed is not { } section || context is null) return;
        RefreshStatus();
        if (SectionTitle != section.Title)
        {
            SectionTitle = section.Title;
            Notify(nameof(SectionTitle));
            Notify(nameof(HasHeading));
            Notify(nameof(MoreAccessibleName));
        }
        bool showAction = section.Action is { CanOpen: true };
        if (ShowActionButton != showAction) { ShowActionButton = showAction; Notify(nameof(ShowActionButton)); }
        if (HasTabs != section.HasTabs) { HasTabs = section.HasTabs; Notify(nameof(HasTabs)); }
        if (!ReferenceEquals(displayedTabs, section.Tabs))
        {
            displayedTabs = section.Tabs;
            SectionTabs = section.Tabs;
            Notify(nameof(SectionTabs));
        }
        section.SyncTabSelection();

        bool playlistListView = context.Settings.PlaylistListView;
        string nextLayout = section.IsGridSection ? "grid"
            : section.IsArtistUpdatesSection ? "rows"
            : section.IsAlbumChart ? "album"
            : section.IsSongShelfSection ? "songShelf"
            : section.IsRankingPreviewSection ? "ranking"
            : section.IsSongSection ? "songs"
            : section.IsArtistSection ? "artists"
            : section.IsPlaylistSection && playlistListView && !context.Catalog.IsCatalogLanding ? "rows"
            : section.IsEntrySection ? "entries"
            : section.IsBannerSection ? "banner"
            : section.IsRankingCoverSection ? "rankCover"
            : "shelf";

        if (layout != nextLayout)
        {
            layout = nextLayout;
            displayedItems = null;
            if (layout == "songShelf")
            {
                SingleRowShelf.ItemsSource = null;
                SingleRowShelf.Visibility = Visibility.Collapsed;
                CardsItems.ItemsSource = null;
                CardsItems.Visibility = Visibility.Collapsed;
                SongColumnShelf.IsBanner = false;
                SongColumnShelf.HorizontalOffset = section.HorizontalOffset;
                SongColumnShelf.Visibility = Visibility.Visible;
                SongColumnShelf.ItemsSource = cards;
            }
            else if (layout is "shelf" or "entries" or "banner" or "rankCover")
            {
                SongColumnShelf.ItemsSource = null;
                SongColumnShelf.Visibility = Visibility.Collapsed;
                CardsItems.ItemsSource = null;
                CardsItems.Visibility = Visibility.Collapsed;
                SingleRowShelf.IsBanner = section.IsBannerSection;
                SingleRowShelf.MinimumCardWidth = section.IsEntrySection ? 150 : section.IsRankingCoverSection ? 120 : 124;
                SingleRowShelf.CardTemplate = section.IsEntrySection
                    ? (DataTemplate)Resources["EntryCardTemplate"]
                    : CardTemplate(section.IsBannerSection ? MusicCatalogCardLayout.Banner
                        : section.IsRankingCoverSection ? MusicCatalogCardLayout.RankCover
                        : MusicCatalogCardLayout.Playlist);
                SingleRowShelf.HorizontalOffset = section.HorizontalOffset;
                SingleRowShelf.Visibility = Visibility.Visible;
                SingleRowShelf.ItemsSource = cards;
            }
            else
            {
                SingleRowShelf.ItemsSource = null;
                SingleRowShelf.Visibility = Visibility.Collapsed;
                SongColumnShelf.ItemsSource = null;
                SongColumnShelf.Visibility = Visibility.Collapsed;
                CardsItems.ItemTemplate = CardTemplate(layout switch
                {
                    "album" => MusicCatalogCardLayout.AlbumRank,
                    "ranking" => MusicCatalogCardLayout.Ranking,
                    "songs" => MusicCatalogCardLayout.Song,
                    "artists" => MusicCatalogCardLayout.Artist,
                    "grid" => MusicCatalogCardLayout.Playlist,
                    _ => MusicCatalogCardLayout.Row
                });
                CardsItems.ItemsPanel = layout switch
                {
                    "grid" => (ItemsPanelTemplate)Resources["GridCardsPanel"],
                    "artists" => (ItemsPanelTemplate)Resources["ArtistCardsPanel"],
                    "ranking" or "songs" => (ItemsPanelTemplate)Resources["TwoColumnCardsPanel"],
                    _ => (ItemsPanelTemplate)Resources["DefaultStackPanel"]
                };
                CardsItems.Visibility = Visibility.Visible;
                CardsItems.ItemsSource = cards;
            }
        }
        else if (layout == "songShelf")
        {
            SongColumnShelf.HorizontalOffset = section.HorizontalOffset;
        }
        else if (layout is "shelf" or "entries" or "banner" or "rankCover")
        {
            SingleRowShelf.HorizontalOffset = section.HorizontalOffset;
        }

        if (!ReferenceEquals(displayedItems, section.Items))
        {
            displayedItems = section.Items;
            ReconcileCards(cards, section.Items);
        }
    }

    private void Shelf_OffsetCommitted(object? sender, double offset)
    {
        if (observed is not null) observed.HorizontalOffset = offset;
    }

    private void CardView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MusicCatalogCardView view) return;
        mountedCards.Add(view);
        view.AttachContext(context);
        view.Unloaded -= CardView_Unloaded;
        view.Unloaded += CardView_Unloaded;
    }

    private void CardView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MusicCatalogCardView view && !view.IsLoaded)
            mountedCards.Remove(view);
    }

    private void EntryCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not EntryCard entry) return;
        mountedEntries.Add(entry);
        entry.AttachContext(context);
        entry.Unloaded -= EntryCard_Unloaded;
        entry.Unloaded += EntryCard_Unloaded;
    }

    private void EntryCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is EntryCard entry && !entry.IsLoaded)
            mountedEntries.Remove(entry);
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
        if (context is not null) await context.Catalog.RefreshCatalogAsync();
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (context is not null) await context.Search.OpenSearchTabAsync(observed?.Title == "歌单" ? "playlists" : "songs");
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (context is not null && observed?.Action is { CanOpen: true } action)
            await context.Catalog.OpenCatalogCardAsync(action);
    }

    private void SectionTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicCatalogTab selected })
            context?.Catalog.SelectCatalogSectionTab(selected);
    }

    private async void EntryOpen(object sender, RoutedEventArgs e)
    {
        if (context is not null && sender is EntryCard { DataContext: MusicCatalogCard card })
            await context.Catalog.OpenCatalogCardAsync(card);
    }

    private async void EntryPlay(object sender, RoutedEventArgs e)
    {
        if (context is not null && sender is EntryCard { DataContext: MusicCatalogCard card })
            await context.Catalog.PlayCatalogCardAsync(card);
    }
}
