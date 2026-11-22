using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Collections.Specialized;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class SearchResultsView : UserControl, IDesktopHeaderSource
{
    private readonly SearchResultsPage page;
    public SearchResultsPage Page => page;
    private bool cardSizeUpdateQueued;
    private int filmColumns = 1;
    private IDisposable? filmVisibility;
    private FilmGeometryAnchor? hiddenAnchor;
    internal ScrollViewer? FilmScroll => MusicVisuals.Descendant<ScrollViewer>(FilmItems);
    internal SearchResultsView(SearchResultsPage page)
    {
        this.page = page;
        InitializeComponent();
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemKeySelector(FilmItems, item => ((FilmSearchCardItem)item).Id);
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemResourcesSelector(FilmItems,
            item => FilmPlaybackPoster.ArtworkResources(((FilmSearchCardItem)item).Poster));
        DataContext = page;
        Loaded += (_, _) =>
        {
            filmVisibility = DesktopVisual.ObserveVisibility(FilmItems, FilmVisibilityChanged);
            SizeFilmCards();
            if (page.Novel is { } module)
            {
                module.Search.Books.CollectionChanged += NovelBooksChanged;
                module.Search.States.CollectionChanged += NovelStatesChanged;
                novelBooksDirty = novelStatesDirty = true; PublishNovel();
            }
        };
        Unloaded += (_, _) =>
        {
            filmVisibility?.Dispose(); filmVisibility = null; hiddenAnchor = null;
            if (page.Novel is { } module)
            {
                module.Search.Books.CollectionChanged -= NovelBooksChanged;
                module.Search.States.CollectionChanged -= NovelStatesChanged;
            }
            novelBooksDirty = novelStatesDirty = true;
        };
    }
    private void FilmVisibilityChanged(bool visible)
    {
        if (visible) { SizeFilmCards(); return; }
        if (FilmItems.ItemsPanelRoot is not ItemsWrapGrid panel || FilmScroll is not { } scroll ||
            FilmItems.ItemsSource is not { } source || !double.IsFinite(panel.ItemHeight) || panel.ItemHeight <= 0) return;
        hiddenAnchor = new(source, filmColumns, scroll.VerticalOffset / panel.ItemHeight,
            scroll.VerticalOffset > 0 && Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset) < .5);
    }
    internal void RestoreFilmScroll(double offset)
    {
        hiddenAnchor = null;
        FilmScroll?.ChangeView(null, offset, null, true);
    }
    private void Film_SizeChanged(object sender, SizeChangedEventArgs e) => SizeFilmCards();
    private void Film_ContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue) e.RegisterUpdateCallback((_, _) => SizeFilmCards());
    }
    private void SizeFilmCards()
    {
        if (cardSizeUpdateQueued) return;
        cardSizeUpdateQueued = DispatcherQueue.TryEnqueue(UpdateFilmCardSize);
    }
    private void UpdateFilmCardSize()
    {
        cardSizeUpdateQueued = false;
        if (!IsLoaded || !DesktopVisual.IsVisible(FilmItems)) return;
        if (hiddenAnchor is not null) FilmItems.UpdateLayout();
        if (FilmItems.ItemsPanelRoot is not ItemsWrapGrid panel || FilmItems.ActualWidth <= 0) return;
        // The native cell owns the trailing gap; the grid margin returns the last gap
        // to the page. Its columns must fit the viewport, not grow it by another gap.
        var (columns, pitch) = Shelf.MeasureCards(Math.Max(0, FilmItems.ActualWidth - 36), 180, 36);
        double width = Math.Max(0, pitch - 36);
        double height = panel.ItemWidth == pitch && double.IsFinite(panel.ItemHeight) ? panel.ItemHeight : 0;
        // Measure outside the layout callback, using the real card's typography and
        // optional subtitle. A fixed estimate changes both the row gap and extent.
        foreach (var child in panel.Children)
        {
            int index = FilmItems.IndexFromContainer(child);
            if (index < 0 || index < panel.FirstCacheIndex || index > panel.LastCacheIndex) continue;
            if (MusicVisuals.Descendant<FilmCard>(child) is not { } card) continue;
            card.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, card.DesiredSize.Height + 36);
        }
        if (height == 0)
        {
            if (panel.ItemWidth != pitch) panel.ItemWidth = pitch;
            return;
        }
        var hidden = hiddenAnchor;
        hiddenAnchor = null;
        if (panel.ItemWidth == pitch && panel.ItemHeight == height && filmColumns == columns) return;
        var scroll = FilmScroll!;
        double row = double.IsFinite(panel.ItemHeight) && panel.ItemHeight > 0 ? scroll.VerticalOffset / panel.ItemHeight : 0;
        int anchor = (int)Math.Floor(row) * filmColumns;
        bool atEnd = scroll.VerticalOffset > 0 && Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset) < .5;
        if (hidden is not null && ReferenceEquals(hidden.Source, FilmItems.ItemsSource))
        {
            row = hidden.Row; anchor = (int)Math.Floor(row) * hidden.Columns; atEnd = hidden.AtEnd;
        }
        if (panel.ItemWidth != pitch) panel.ItemWidth = pitch;
        if (panel.ItemHeight != height) panel.ItemHeight = height;
        filmColumns = columns;
        FilmItems.UpdateLayout();
        double offset = atEnd ? scroll.ScrollableHeight : (anchor / columns + row - Math.Floor(row)) * height;
        scroll.ChangeView(null, offset, null, true);
    }
    public DesktopHeaderContribution Header => new("") { SearchTargetId = page.SelectedTarget, SearchContextId = page.SelectedTab };
    public event EventHandler? HeaderChanged;
    internal void Present()
    {
        bool film = page.SelectedTab == "film";
        bool novel = page.SelectedTab == "novel";
        if (film || novel) MusicHost.Content = null;
        else if (!ReferenceEquals(MusicHost.Content, page.Music.View))
        {
            page.Music.Presentation.Page.ProjectSearchResults(true);
            MusicHost.Content = page.Music.View;
        }
        MusicHost.Visibility = film || novel ? Visibility.Collapsed : Visibility.Visible;
        FilmBody.Visibility = film ? Visibility.Visible : Visibility.Collapsed;
        FilmTools.Visibility = FilmBody.Visibility;
        NovelScroll.Visibility = novel ? Visibility.Visible : Visibility.Collapsed;
        if (novel && page.Novel is { } module) { module.SearchWebOwner = this; PublishNovel(); }
        HeaderChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool novelBooksDirty = true, novelStatesDirty = true;
    private readonly Dictionary<string, TextBlock> novelStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NovelSearchCard> novelCards = new(StringComparer.Ordinal);
    private void NovelBooksChanged(object? sender, NotifyCollectionChangedEventArgs args) => novelBooksDirty = true;
    private void NovelStatesChanged(object? sender, NotifyCollectionChangedEventArgs args) => novelStatesDirty = true;
    private static void PlaceNovelChild(Panel panel, UIElement child, int index)
    {
        if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], child)) return;
        panel.Children.Remove(child); panel.Children.Insert(index, child);
    }
    private Style? SharedStyle(string key) => DesktopVisual.Resource(this, key) as Style;
    internal void PublishNovel()
    {
        if (page.SelectedTab != "novel" || page.Novel is not { } module) return;
        if (novelStatesDirty)
        {
            novelStatesDirty = false;
            var ids = module.Search.States.Select(state => state.Source.InstallationId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in novelStatuses.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                NovelStatus.Children.Remove(novelStatuses[id]); novelStatuses.Remove(id);
            }
            for (int index = 0; index < module.Search.States.Count; index++)
            {
                var source = module.Search.States[index];
                if (!novelStatuses.TryGetValue(source.Source.InstallationId, out var text))
                {
                    text = new TextBlock
                    {
                        Style = SharedStyle("MeowFilm.MutedText"),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                        Margin = new Thickness(0, 0, 12, 8)
                    };
                    novelStatuses.Add(source.Source.InstallationId, text);
                }
                string value = source.Source.Name + " · " + source.Status;
                if (text.Text != value) text.Text = value;
                PlaceNovelChild(NovelStatus, text, index);
            }
        }
        if (novelBooksDirty)
        {
            novelBooksDirty = false;
            var ids = module.Search.Books.Select(book => book.SourceIdentity).ToHashSet(StringComparer.Ordinal);
            foreach (var id in novelCards.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                NovelBooks.Children.Remove(novelCards[id].Button); novelCards.Remove(id);
            }
            for (int index = 0; index < module.Search.Books.Count; index++)
            {
                var book = module.Search.Books[index];
                if (!novelCards.TryGetValue(book.SourceIdentity, out var card))
                {
                    card = new(this, module, page, book); novelCards.Add(book.SourceIdentity, card);
                }
                else card.Update(module, book);
                PlaceNovelChild(NovelBooks, card.Button, index);
            }
        }
        NovelState.Text = module.Search.Status;
        NovelMoreButton.IsEnabled = module.Search.CanLoadMore;
        NovelCancelButton.IsEnabled = module.Search.Loading;
    }
    private sealed class NovelSearchCard
    {
        internal Button Button { get; }
        private readonly StackPanel content = new() { Width = 148, Margin = new(0, 0, 24, 24) };
        private readonly TextBlock title;
        private readonly TextBlock author;
        private NovelImage image;
        private NovelCapability module;
        private NovelBook book;
        internal NovelSearchCard(SearchResultsView ownerView, NovelCapability module, SearchResultsPage page, NovelBook book)
        {
            this.module = module; this.book = book;
            title = new TextBlock
            {
                Style = ownerView.SharedStyle("MeowFilm.Text"),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            author = new TextBlock
            {
                Style = ownerView.SharedStyle("MeowFilm.MutedText"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            image = Cover(module, book); content.Children.Add(image); content.Children.Add(title); content.Children.Add(author);
            Button = new Button
            {
                Style = ownerView.SharedStyle("MeowFilm.QuietButton"),
                Content = content,
                Padding = new Thickness(0),
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            Button.Click += (_, _) => this.module.RunUi(() => page.OpenNovelAsync(this.book));
            Update(module, book);
        }
        private static NovelImage Cover(NovelCapability module, NovelBook book) => new(module.Sources, book)
            { Width = 148, Height = 200, Margin = new(0, 0, 0, 10) };
        private static (string Source, long Revision, string Url, string Cover) CoverIdentity(NovelBook book) =>
            (book.InstallationId, book.Revision, book.BookUrl, book.CoverUrl);
        internal void Update(NovelCapability owner, NovelBook next)
        {
            if (!ReferenceEquals(module.Sources, owner.Sources) || CoverIdentity(book) != CoverIdentity(next))
            {
                content.Children.Remove(image); image = Cover(owner, next); content.Children.Insert(0, image);
            }
            module = owner; book = next;
            if (title.Text != next.Name) title.Text = next.Name;
            if (author.Text != next.Author) author.Text = next.Author;
            AutomationProperties.SetName(Button, next.Name); AutomationProperties.SetName(image, next.Name + "封面");
        }
    }
    private void NovelMore_Click(object sender, RoutedEventArgs e) => _ = page.LoadMoreNovelAsync();
    private void NovelCancel_Click(object sender, RoutedEventArgs e) => page.Novel?.Search.Cancel();
    internal void ReleaseMusic()
    {
        MusicHost.Content = null;
        page.Music.Presentation.Page.ProjectSearchResults(false);
    }
    private void Tab_Loaded(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var tab = (SearchResultTab)button.DataContext;
        button.ApplyTemplate();
        var indicator = (Border)((FrameworkElement)VisualTreeHelper.GetChild(button, 0)).FindName("SelectionIndicator");
        AutomationProperties.SetName(button, tab.Label + "搜索结果");
        void Refresh()
        {
            bool contrast = DesktopTheme.GetIsHighContrast(button);
            button.Foreground = contrast
                ? SystemAppearance.GetElementBrush(button.IsEnabled
                    ? Windows.UI.ViewManagement.UIElementType.WindowText : Windows.UI.ViewManagement.UIElementType.GrayText)
                : (Brush)DesktopVisual.Resource(button, !button.IsEnabled ? "MeowFilm.Brush.Disabled"
                    : tab.Selected ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Muted");
            indicator.Visibility = tab.Selected ? Visibility.Visible : Visibility.Collapsed;
            indicator.Background = contrast ? button.Foreground : (Brush)DesktopVisual.Resource(button, "MeowFilm.Brush.Accent");
        }
        void Changed(object? owner, PropertyChangedEventArgs args) => Refresh();
        tab.PropertyChanged += Changed;
        long enabled = button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Refresh());
        var theme = DesktopTheme.Observe(button, Refresh);
        void Unloaded(object owner, RoutedEventArgs args)
        {
            tab.PropertyChanged -= Changed;
            button.UnregisterPropertyChangedCallback(IsEnabledProperty, enabled);
            theme.Dispose(); button.Unloaded -= Unloaded;
        }
        button.Unloaded += Unloaded;
    }
    private sealed record FilmGeometryAnchor(object Source, int Columns, double Row, bool AtEnd);
    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab }) await page.OpenTabAsync(tab);
    }
    private void RawList_Click(object sender, RoutedEventArgs e) => page.RawListMode = RawListToggle.IsChecked == true;
    private async void More_Click(object sender, RoutedEventArgs e) => await page.LoadMoreAsync();
    private async void Card_Activated(object? sender, FilmCardActivatedEventArgs e) => await page.OpenCardAsync(e.Item);
}
