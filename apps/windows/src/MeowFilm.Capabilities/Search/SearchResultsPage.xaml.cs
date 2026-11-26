using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class NovelSearchStatusItem(string id, string text) : INotifyPropertyChanged
{
    private string text = text;
    public string Id { get; } = id;
    public string Text
    {
        get => text;
        internal set
        {
            if (text == value) return;
            text = value;
            PropertyChanged?.Invoke(this, new(nameof(Text)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class NovelSearchCardItem(NovelSourceService sources, NovelBook book) : INotifyPropertyChanged
{
    private NovelSourceService sources = sources;
    private NovelBook book = book;
    public string Identity { get; } = book.SourceIdentity;
    public NovelSourceService Sources => sources;
    public NovelBook Book => book;
    public string Name => book.Name;
    public string Author => book.Author;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(NovelSourceService nextSources, NovelBook nextBook)
    {
        bool sourcesChanged = !ReferenceEquals(sources, nextSources);
        bool bookChanged = !ReferenceEquals(book, nextBook);
        string previousName = book.Name;
        string previousAuthor = book.Author;
        sources = nextSources;
        book = nextBook;
        if (sourcesChanged) PropertyChanged?.Invoke(this, new(nameof(Sources)));
        if (bookChanged) PropertyChanged?.Invoke(this, new(nameof(Book)));
        if (previousName != nextBook.Name) PropertyChanged?.Invoke(this, new(nameof(Name)));
        if (previousAuthor != nextBook.Author) PropertyChanged?.Invoke(this, new(nameof(Author)));
    }
}

public partial class SearchResultsView : UserControl, IDesktopHeaderSource
{
    private readonly SearchResultsPage page;
    public SearchResultsPage Page => page;
    public ObservableCollection<NovelSearchStatusItem> NovelStatuses { get; } = [];
    public ObservableCollection<NovelSearchCardItem> NovelCardItems { get; } = [];
    private bool cardSizeUpdateQueued;
    private int filmColumns = 1;
    private IDisposable? filmVisibility;
    private FilmGeometryAnchor? hiddenAnchor;
    private bool novelBooksDirty = true, novelStatesDirty = true;
    internal ScrollViewer? FilmScroll => DesktopVisual.Descendant<ScrollViewer>(FilmItems);

    internal SearchResultsView(SearchResultsPage page)
    {
        this.page = page;
        InitializeComponent();
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemKeySelector(FilmItems, item => ((FilmSearchCardItem)item).Id);
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemResourcesSelector(FilmItems,
            item => FilmPlaybackPoster.ArtworkResources(((FilmSearchCardItem)item).Poster));
        Loaded += (_, _) =>
        {
            page.PropertyChanged -= PageChanged;
            page.PropertyChanged += PageChanged;
            filmVisibility?.Dispose();
            filmVisibility = DesktopVisual.ObserveVisibility(FilmItems, FilmVisibilityChanged);
            SizeFilmCards();
            if (page.Novel is { } module)
            {
                module.Search.Books.CollectionChanged -= NovelBooksChanged;
                module.Search.Books.CollectionChanged += NovelBooksChanged;
                module.Search.States.CollectionChanged -= NovelStatesChanged;
                module.Search.States.CollectionChanged += NovelStatesChanged;
                novelBooksDirty = novelStatesDirty = true;
                PublishNovel();
            }
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            page.PropertyChanged -= PageChanged;
            filmVisibility?.Dispose();
            filmVisibility = null;
            hiddenAnchor = null;
            if (page.Novel is { } module)
            {
                module.Search.Books.CollectionChanged -= NovelBooksChanged;
                module.Search.States.CollectionChanged -= NovelStatesChanged;
            }
            novelBooksDirty = novelStatesDirty = true;
        };
    }

    private void PageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchResultsPage.Error))
            NoticeOutlet.Post(this, page.Error, page);
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
        if (!e.InRecycleQueue)
            e.RegisterUpdateCallback((_, args) => MeasureRealizedFilmCard(args.ItemContainer));
    }

    private void MeasureRealizedFilmCard(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(FilmItems)) return;
        if (hiddenAnchor is not null || FilmItems.ItemsPanelRoot is not ItemsWrapGrid panel || FilmItems.ActualWidth <= 0)
        {
            SizeFilmCards();
            return;
        }
        var (columns, pitch) = Shelf.MeasureCards(Math.Max(0, FilmItems.ActualWidth - 36), 180, 36);
        if (panel.ItemWidth != pitch || filmColumns != columns || !double.IsFinite(panel.ItemHeight) || panel.ItemHeight <= 0)
        {
            SizeFilmCards();
            return;
        }
        if (DesktopVisual.Descendant<FilmCard>(container) is not { } card) return;
        double width = Math.Max(0, pitch - 36);
        card.Measure(new Size(width, double.PositiveInfinity));
        if (card.DesiredSize.Height + 36 > panel.ItemHeight) SizeFilmCards();
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
            if (DesktopVisual.Descendant<FilmCard>(child) is not { } card) continue;
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
        if (FilmScroll is not { } scroll)
        {
            if (panel.ItemWidth != pitch) panel.ItemWidth = pitch;
            if (panel.ItemHeight != height) panel.ItemHeight = height;
            filmColumns = columns;
            return;
        }
        double row = double.IsFinite(panel.ItemHeight) && panel.ItemHeight > 0 ? scroll.VerticalOffset / panel.ItemHeight : 0;
        int anchor = (int)Math.Floor(row) * filmColumns;
        bool atEnd = scroll.VerticalOffset > 0 && Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset) < .5;
        if (hidden is not null && ReferenceEquals(hidden.Source, FilmItems.ItemsSource))
        {
            row = hidden.Row;
            anchor = (int)Math.Floor(row) * hidden.Columns;
            atEnd = hidden.AtEnd;
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
        if (page.Novel is { } module)
        {
            if (novel) { module.SearchWebOwner = this; PublishNovel(); }
            else if (ReferenceEquals(module.SearchWebOwner, this)) module.SearchWebOwner = null;
        }
        HeaderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NovelBooksChanged(object? sender, NotifyCollectionChangedEventArgs args) => novelBooksDirty = true;
    private void NovelStatesChanged(object? sender, NotifyCollectionChangedEventArgs args) => novelStatesDirty = true;

    internal void PublishNovel()
    {
        if (page.SelectedTab != "novel" || page.Novel is not { } module) return;
        if (novelStatesDirty)
        {
            novelStatesDirty = false;
            var incoming = module.Search.States;
            var ids = incoming.Select(state => state.Source.InstallationId).ToHashSet(StringComparer.Ordinal);
            for (int i = NovelStatuses.Count - 1; i >= 0; i--)
                if (!ids.Contains(NovelStatuses[i].Id)) NovelStatuses.RemoveAt(i);
            for (int index = 0; index < incoming.Count; index++)
            {
                var source = incoming[index];
                string id = source.Source.InstallationId;
                string value = source.Source.Name + " · " + source.Status;
                int existing = index;
                while (existing < NovelStatuses.Count && NovelStatuses[existing].Id != id) existing++;
                if (existing == NovelStatuses.Count) NovelStatuses.Insert(index, new(id, value));
                else
                {
                    if (existing != index) NovelStatuses.Move(existing, index);
                    NovelStatuses[index].Text = value;
                }
            }
            while (NovelStatuses.Count > incoming.Count) NovelStatuses.RemoveAt(NovelStatuses.Count - 1);
        }
        if (novelBooksDirty)
        {
            novelBooksDirty = false;
            var incoming = module.Search.Books;
            var ids = incoming.Select(book => book.SourceIdentity).ToHashSet(StringComparer.Ordinal);
            for (int i = NovelCardItems.Count - 1; i >= 0; i--)
                if (!ids.Contains(NovelCardItems[i].Identity)) NovelCardItems.RemoveAt(i);
            for (int index = 0; index < incoming.Count; index++)
            {
                var book = incoming[index];
                int existing = index;
                while (existing < NovelCardItems.Count && NovelCardItems[existing].Identity != book.SourceIdentity) existing++;
                if (existing == NovelCardItems.Count) NovelCardItems.Insert(index, new(module.Sources, book));
                else
                {
                    if (existing != index) NovelCardItems.Move(existing, index);
                    NovelCardItems[index].Update(module.Sources, book);
                }
            }
            while (NovelCardItems.Count > incoming.Count) NovelCardItems.RemoveAt(NovelCardItems.Count - 1);
        }
        NovelState.Text = module.Search.Status;
        NovelMoreButton.IsEnabled = module.Search.CanLoadMore;
        NovelCancelButton.IsEnabled = module.Search.Loading;
    }

    private void NovelCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NovelSearchCardItem item } && page.Novel is { } module)
            module.RunUi(() => page.OpenNovelAsync(item.Book));
    }

    private void NovelMore_Click(object sender, RoutedEventArgs e) => _ = page.LoadMoreNovelAsync();
    private void NovelCancel_Click(object sender, RoutedEventArgs e) => page.Novel?.Search.Cancel();

    internal void ReleaseMusic()
    {
        MusicHost.Content = null;
        page.Music.Presentation.Page.ProjectSearchResults(false);
    }

    private sealed record FilmGeometryAnchor(object Source, int Columns, double Row, bool AtEnd);

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase { Tag: string tab }) await page.OpenTabAsync(tab);
    }

    private void RawList_Click(object sender, RoutedEventArgs e) => page.RawListMode = RawListToggle.IsChecked == true;
    private async void More_Click(object sender, RoutedEventArgs e) => await page.LoadMoreAsync();
    private async void Card_Activated(object? sender, FilmCardActivatedEventArgs e) => await page.OpenCardAsync(e.Item);
}
