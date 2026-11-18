using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace MeowFilm.Capabilities;

public sealed class MusicCatalogSectionView : UserControl
{
    private MusicCapability? owner;
    private MusicCatalogSection? displayed;
    private MusicCatalogSection? observed;
    private IReadOnlyList<MusicCatalogCard>? displayedItems;
    private string layout = "";
    private readonly StackPanel stack = new() { Margin = new Thickness(0, 0, 0, 32) };
    private readonly Grid heading = new() { Margin = new Thickness(0, 0, 0, 18), MinHeight = 24 };
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressRing progress = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0) };
    private readonly MusicCatalogSkeleton skeleton = new();
    private readonly ItemsControl tabs = new() { Margin = new Thickness(0, 0, 0, 12) };
    public MusicCatalogSectionView()
    {
        stack.Children.Add(heading); stack.Children.Add(tabs); stack.Children.Add(skeleton); stack.Children.Add(body); Content = stack;
        tabs.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:MusicWrapPanel/></ItemsPanelTemplate>");
        DataContextChanged += (_, _) => ObserveSection();
        Loaded += (_, _) =>
        {
            for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is MusicCatalogView view) { owner = view.Owner; break; }
            if (owner is not null) owner.PropertyChanged += Changed;
            ObserveSection();
        };
        Unloaded += (_, _) => { if (owner is not null) owner.PropertyChanged -= Changed; owner = null; if (observed is not null) observed.PropertyChanged -= SectionChanged; observed = null; };
    }
    private void ObserveSection()
    {
        if (observed is not null) observed.PropertyChanged -= SectionChanged;
        observed = DataContext as MusicCatalogSection;
        if (observed is not null) observed.PropertyChanged += SectionChanged;
        Refresh();
    }
    private void SectionChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MusicCapability.PlaylistListView) or nameof(MusicCapability.SearchSongsLoading) or nameof(MusicCapability.SearchPlaylistsLoading) or nameof(MusicCapability.CanCatalogBrowse)) Refresh(); }
    private static DataTemplate CardTemplate(MusicCatalogCardLayout presentation) => (DataTemplate)XamlReader.Load(
        $"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:MusicCatalogCardView Presentation='{presentation}'/></DataTemplate>");
    private void Refresh()
    {
        if (DataContext is not MusicCatalogSection section || owner is null) return;
        bool loading = section.IsLoading || owner.IsSearchOverview && (section.Title == "单曲" ? owner.SearchSongsLoading : section.Title == "歌单" && owner.SearchPlaylistsLoading);
        progress.IsActive = loading; progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        skeleton.Visibility = section.Items.Count == 0 && loading ? Visibility.Visible : Visibility.Collapsed;
        string nextLayout = section.IsGridSection ? "grid" : section.IsArtistUpdatesSection ? "rows" : section.IsAlbumChart ? "album" : section.IsSongShelfSection ? "songShelf" : section.IsRankingPreviewSection ? "ranking" : section.IsSongSection ? "songs" : section.IsArtistSection ? "artists" : section.IsPlaylistSection && owner.PlaylistListView && !owner.IsCatalogLanding ? "rows" : "shelf";
        if (ReferenceEquals(displayed, section) && ReferenceEquals(displayedItems, section.Items) && layout == nextLayout) return;
        displayed = section; displayedItems = section.Items; layout = nextLayout;
        tabs.Visibility = section.HasTabs ? Visibility.Visible : Visibility.Collapsed;
        tabs.Items.Clear();
        foreach (var tab in section.Tabs ?? [])
        {
            var button = new Button { Content = tab.Title, Tag = tab, Margin = new Thickness(0, 0, 8, 0), FontWeight = Microsoft.UI.Text.FontWeights.Normal };
            AutomationProperties.SetItemStatus(button, section.SelectedTab == tab.Id ? "已选中" : "");
            button.Click += (_, _) => owner?.SelectCatalogSectionTab(tab);
            tabs.Items.Add(button);
        }
        if (progress.Parent is Panel previousTitle) previousTitle.Children.Remove(progress);
        heading.Children.Clear(); heading.ColumnDefinitions.Clear(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Visibility = section.Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var title = new StackPanel { Orientation = Orientation.Horizontal }; title.Children.Add(new TextBlock { Text = section.Title, FontSize = 18, VerticalAlignment = VerticalAlignment.Center }); title.Children.Add(progress);
        if (section.Action is { CanOpen: true } action)
        { var more = new Button { Content = "›", Padding = new Thickness(6, 0, 6, 0) }; more.Click += async (_, _) => await owner.OpenCatalogCardAsync(action); title.Children.Add(more); }
        heading.Children.Add(title);
        if (owner.IsCatalogHome || owner.IsSearchOverview)
        {
            var more = new Button { Content = owner.IsCatalogHome ? "↻" : "查看更多", FontWeight = Microsoft.UI.Text.FontWeights.Normal, IsEnabled = owner.CanCatalogBrowse };
            if (owner.IsCatalogHome)
            {
                more.Width = 28; more.Height = 24; more.MinHeight = 0; more.MinWidth = 0; more.Padding = new Thickness(0);
                more.Background = MusicVisuals.Brush("#00000000"); more.BorderThickness = new Thickness(0); more.Foreground = MusicVisuals.Brush("#8B9096");
                var canvas = new Canvas { Width = 20, Height = 20 };
                var path = (Path)XamlReader.Load("<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='M16.5,6 A7.5,7.5 0 1 0 17.2,12 M16.5,1.5 L16.5,6 12,6' StrokeThickness='1.2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>");
                path.Stroke = more.Foreground; canvas.Children.Add(path); more.Content = canvas;
                AutomationProperties.SetName(more, "刷新推荐");
            }
            else AutomationProperties.SetName(more, "查看更多" + section.Title);
            more.Click += async (_, _) => { if (owner.IsCatalogHome) await owner.RefreshCatalogAsync(); else await owner.OpenSearchTabAsync(section.Title == "歌单" ? "playlists" : "songs"); };
            Grid.SetColumn(more, 1); heading.Children.Add(more);
        }
        if (layout is "shelf" or "songShelf")
        {
            DataTemplate template = section.IsEntrySection
                ? (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:EntryCard/></DataTemplate>")
                : CardTemplate(section.IsBannerSection ? MusicCatalogCardLayout.Banner : section.IsRankingCoverSection ? MusicCatalogCardLayout.RankCover : MusicCatalogCardLayout.Playlist);
            if (layout == "songShelf")
                template = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><ItemsControl Height='252' ItemsSource='{Binding Items}'><ItemsControl.ItemTemplate><DataTemplate><local:MusicCatalogCardView Presentation='Song'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl></DataTemplate>");
            var shelf = new Shelf { DataContext = section, CardTemplate = template, MinimumCardWidth = layout == "songShelf" ? 400 : section.IsEntrySection ? 150 : section.IsRankingCoverSection ? 120 : 124 };
            if (layout == "songShelf") shelf.ItemsSource = section.SongColumns;
            if (section.IsEntrySection) shelf.Loaded += (_, _) => AttachEntries(shelf);
            body.Content = shelf;
        }
        else
        {
            var items = new ItemsControl { ItemsSource = section.Items, ItemTemplate = CardTemplate(layout switch { "album" => MusicCatalogCardLayout.AlbumRank, "ranking" => MusicCatalogCardLayout.Ranking, "songs" => MusicCatalogCardLayout.Song, "artists" => MusicCatalogCardLayout.Artist, "grid" => MusicCatalogCardLayout.Playlist, _ => MusicCatalogCardLayout.Row }) };
            if (layout == "grid")
                items.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:AdaptiveCardPanel MinimumCardWidth='124' MaximumCardWidth='168' RowGap='16'/></ItemsPanelTemplate>");
            else if (layout is "ranking" or "songs" or "artists")
                items.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load($"<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:MeowFilm.Capabilities'><local:AdaptiveCardPanel MinimumCardWidth='{(layout == "artists" ? 150 : 400)}' MaximumCardWidth='{(layout == "artists" ? "200" : "Infinity")}' MaximumColumns='{(layout == "artists" ? 4 : 2)}' CardGap='20' RowGap='20'/></ItemsPanelTemplate>");
            body.Content = items;
        }
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
