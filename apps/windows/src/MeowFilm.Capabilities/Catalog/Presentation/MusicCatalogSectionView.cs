using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using MeowFilm.Desktop.Artwork;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace MeowFilm.Capabilities;

public sealed class MusicCatalogSectionView : UserControl
{
    private MusicCapability? owner;
    private MusicCatalogSection? observed;
    private MusicCatalogSectionDisplay? display;
    private IReadOnlyList<MusicCatalogCard>? displayedItems;
    private IReadOnlyList<MusicCatalogTab>? displayedTabs;
    private readonly ObservableCollection<MusicCatalogCard> cards = [];
    private string layout = "";
    private readonly StackPanel stack = new() { Margin = new Thickness(0, 0, 0, 32) };
    private readonly Grid heading = new() { Margin = new Thickness(0, 0, 0, 18), MinHeight = 24 };
    private readonly TextBlock titleText = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button actionButton = new() { Content = "›", Padding = new Thickness(6, 0, 6, 0) };
    private readonly Button more = new() { Content = "查看更多", FontWeight = Microsoft.UI.Text.FontWeights.Normal };
    private readonly Button refresh = new() { Width = 28, Height = 24, MinHeight = 0, MinWidth = 0,
        Padding = new Thickness(0), Background = MusicVisuals.Brush("#00000000"), BorderThickness = new Thickness(0), Foreground = MusicVisuals.Brush("#8B9096") };
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressRing progress = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0),
        IsActive = false, Visibility = Visibility.Collapsed };
    private readonly ItemsControl tabs = new() { Margin = new Thickness(0, 0, 0, 12) };
    public MusicCatalogSectionView()
    {
        heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(titleText); title.Children.Add(progress); title.Children.Add(actionButton); heading.Children.Add(title);
        Grid.SetColumn(more, 1); Grid.SetColumn(refresh, 1); heading.Children.Add(more); heading.Children.Add(refresh);
        var canvas = new Canvas { Width = 20, Height = 20 };
        var path = new Path
        {
            Data = MusicVisuals.Geometry("M16.5,6 A7.5,7.5 0 1 0 17.2,12 M16.5,1.5 L16.5,6 12,6"),
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stroke = refresh.Foreground
        };
        canvas.Children.Add(path); refresh.Content = canvas;
        AutomationProperties.SetName(refresh, "刷新推荐");
        refresh.Click += async (_, _) => { if (owner is not null) await owner.RefreshCatalogAsync(); };
        more.Click += async (_, _) => { if (owner is not null) await owner.OpenSearchTabAsync(observed?.Title == "歌单" ? "playlists" : "songs"); };
        actionButton.Click += async (_, _) => { if (owner is not null && observed?.Action is { CanOpen: true } action) await owner.OpenCatalogCardAsync(action); };
        stack.Children.Add(heading); stack.Children.Add(tabs); stack.Children.Add(body); Content = stack;
        tabs.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:desktop='using:MeowFilm.Desktop'><desktop:WrapPanel/></ItemsPanelTemplate>");
        DataContextChanged += (_, _) => ObserveDisplay();
        Loaded += (_, _) =>
        {
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicCatalogView view) { owner = view.Owner; break; }
            if (owner is not null) owner.CatalogState.PropertyChanged += Changed;
            ObserveDisplay();
        };
        Unloaded += (_, _) =>
        {
            if (owner is not null) owner.CatalogState.PropertyChanged -= Changed; owner = null;
            if (display is not null) display.PropertyChanged -= DisplayChanged; display = null;
            if (observed is not null) observed.PropertyChanged -= SectionChanged; observed = null;
        };
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
        if (e.PropertyName == nameof(MusicCatalogSection.HorizontalOffset) && body.Content is Shelf shelf)
            shelf.HorizontalOffset = observed?.HorizontalOffset ?? 0;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCapability.PlaylistListView)) Refresh();
        else if (e.PropertyName is nameof(MusicCapability.SearchSongsLoading) or nameof(MusicCapability.SearchPlaylistsLoading) or
            nameof(MusicCapability.CanCatalogBrowse) or nameof(MusicCapability.IsCatalogHome) or nameof(MusicCapability.IsSearchOverview)) RefreshStatus();
    }
    private static DataTemplate CardTemplate(MusicCatalogCardLayout presentation) => (DataTemplate)XamlReader.Load(
        $"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:MusicCatalogCardView Presentation='{presentation}'/></DataTemplate>");
    private void RefreshStatus()
    {
        if (observed is not { } section || owner is null) return;
        bool loading = section.IsLoading || owner.IsSearchOverview && (section.Title == "单曲" ? owner.SearchSongsLoading : section.Title == "歌单" && owner.SearchPlaylistsLoading);
        progress.IsActive = loading; progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        refresh.Visibility = owner.IsCatalogHome ? Visibility.Visible : Visibility.Collapsed;
        more.Visibility = owner.IsSearchOverview ? Visibility.Visible : Visibility.Collapsed;
        more.IsEnabled = refresh.IsEnabled = owner.CanCatalogBrowse;
    }
    private void Refresh()
    {
        if (observed is not { } section || owner is null) return;
        RefreshStatus();
        titleText.Text = section.Title; heading.Visibility = section.Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        actionButton.Visibility = section.Action is { CanOpen: true } ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(more, "查看更多" + section.Title);
        tabs.Visibility = section.HasTabs ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(displayedTabs, section.Tabs))
        {
            displayedTabs = section.Tabs;
            var nextTabs = section.Tabs ?? [];
            for (int index = 0; index < nextTabs.Count; index++)
            {
                var tab = nextTabs[index];
                if (index == tabs.Items.Count)
                {
                    var button = new Button { Margin = new Thickness(0, 0, 8, 0), FontWeight = Microsoft.UI.Text.FontWeights.Normal };
                    button.Click += (_, _) => { if (button.Tag is MusicCatalogTab selected) owner?.SelectCatalogSectionTab(selected); };
                    tabs.Items.Add(button);
                }
                var item = (Button)tabs.Items[index]; item.Tag = tab; item.Content = tab.Title;
            }
            while (tabs.Items.Count > nextTabs.Count) tabs.Items.RemoveAt(tabs.Items.Count - 1);
        }
        foreach (Button button in tabs.Items)
            AutomationProperties.SetItemStatus(button, button.Tag is MusicCatalogTab tab && section.SelectedTab == tab.Id ? "已选中" : "");
        string nextLayout = section.IsGridSection ? "grid" : section.IsArtistUpdatesSection ? "rows" : section.IsAlbumChart ? "album" : section.IsSongShelfSection ? "songShelf" : section.IsRankingPreviewSection ? "ranking" : section.IsSongSection ? "songs" : section.IsArtistSection ? "artists" : section.IsPlaylistSection && owner.PlaylistListView && !owner.IsCatalogLanding ? "rows" : section.IsEntrySection ? "entries" : section.IsBannerSection ? "banner" : section.IsRankingCoverSection ? "rankCover" : "shelf";
        if (layout != nextLayout)
        {
            layout = nextLayout; displayedItems = null;
            if (layout is "shelf" or "songShelf" or "entries" or "banner" or "rankCover")
            {
                DataTemplate template = section.IsEntrySection
                    ? (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:EntryCard/></DataTemplate>")
                    : CardTemplate(section.IsBannerSection ? MusicCatalogCardLayout.Banner : section.IsRankingCoverSection ? MusicCatalogCardLayout.RankCover : MusicCatalogCardLayout.Playlist);
                if (layout == "songShelf") template = CardTemplate(MusicCatalogCardLayout.Song);
                var shelf = new Shelf { RowsPerColumn = layout == "songShelf" ? 3 : 1, ItemsSource = cards, DataContext = section,
                    CardTemplate = template, MinimumCardWidth = layout == "songShelf" ? 400 : section.IsEntrySection ? 150 : section.IsRankingCoverSection ? 120 : 124 };
                shelf.SetArtworkItemKeySelector(item => ((MusicCatalogCard)item).Key);
                shelf.SetArtworkItemResourcesSelector(item => MusicCatalogCardView.ArtworkResources((MusicCatalogCard)item));
                if (section.IsEntrySection) shelf.Loaded += (_, _) => AttachEntries(shelf);
                body.Content = shelf;
            }
            else
            {
                var items = new ItemsControl { ItemsSource = cards, ItemTemplate = CardTemplate(layout switch { "album" => MusicCatalogCardLayout.AlbumRank, "ranking" => MusicCatalogCardLayout.Ranking, "songs" => MusicCatalogCardLayout.Song, "artists" => MusicCatalogCardLayout.Artist, "grid" => MusicCatalogCardLayout.Playlist, _ => MusicCatalogCardLayout.Row }) };
                if (layout == "grid")
                    items.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:AdaptiveCardPanel MinimumCardWidth='124' MaximumCardWidth='168' RowGap='16'/></ItemsPanelTemplate>");
                else if (layout is "ranking" or "songs" or "artists")
                    items.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load($"<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:AdaptiveCardPanel MinimumCardWidth='{(layout == "artists" ? 150 : 400)}' MaximumCardWidth='{(layout == "artists" ? "200" : "Infinity")}' MaximumColumns='{(layout == "artists" ? 4 : 2)}' CardGap='20' RowGap='20'/></ItemsPanelTemplate>");
                body.Content = items;
            }
        }
        if (!ReferenceEquals(displayedItems, section.Items))
        {
            displayedItems = section.Items;
            ReconcileCards(cards, section.Items);
        }
        if (body.Content is Shelf current) current.DataContext = section;
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
    private void AttachEntries(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is EntryCard entry) { entry.OpenRequested -= EntryOpen; entry.OpenRequested += EntryOpen; entry.PlayRequested -= EntryPlay; entry.PlayRequested += EntryPlay; }
            AttachEntries(child);
        }
        if (parent is Shelf shelf && MusicVisuals.Descendant<ListView>(shelf) is { } list)
        {
            list.ContainerContentChanging -= EntriesChanged;
            list.ContainerContentChanging += EntriesChanged;
        }
    }
    private void EntriesChanged(ListViewBase list, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue) list.DispatcherQueue.TryEnqueue(() => AttachEntries(e.ItemContainer));
    }
    private async void EntryOpen(object sender, RoutedEventArgs e) { if (owner is not null && sender is EntryCard { DataContext: MusicCatalogCard card }) await owner.OpenCatalogCardAsync(card); }
    private async void EntryPlay(object sender, RoutedEventArgs e) { if (owner is not null && sender is EntryCard { DataContext: MusicCatalogCard card }) await owner.PlayCatalogCardAsync(card); }
}
