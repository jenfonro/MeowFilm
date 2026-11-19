using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class SearchResultsView : UserControl, IDesktopHeaderSource
{
    private readonly SearchResultsPage page;
    private bool cardSizeUpdateQueued;
    private int filmColumns = 1;
    private IDisposable? filmVisibility;
    private FilmGeometryAnchor? hiddenAnchor;
    internal ScrollViewer? FilmScroll => MusicVisuals.Descendant<ScrollViewer>(FilmItems);
    internal SearchResultsView(SearchResultsPage page)
    {
        this.page = page;
        InitializeComponent();
        DataContext = page;
        Loaded += (_, _) =>
        {
            filmVisibility = DesktopVisual.ObserveVisibility(FilmItems, FilmVisibilityChanged);
            SizeFilmCards();
        };
        Unloaded += (_, _) => { filmVisibility?.Dispose(); filmVisibility = null; hiddenAnchor = null; };
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
    private FrameworkElement? novelLayout;
    private MusicWrapPanel? novelBooks, novelStatus;
    private TextBlock? novelState;
    private Button? novelMore, novelCancel;
    private NovelBook[] shownNovelBooks = [];
    internal void PublishNovel()
    {
        if (page.SelectedTab != "novel" || page.Novel is not { } module) return;
        if (novelLayout is null)
        {
            var layout = new StackPanel(); novelStatus = new MusicWrapPanel(); novelBooks = new MusicWrapPanel(); novelState = NovelUi.Text("", muted: true);
            novelMore = NovelUi.Button("加载更多", () => _ = page.LoadMoreNovelAsync());
            novelCancel = NovelUi.Button("取消", module.Search.Cancel);
            layout.Children.Add(novelStatus); layout.Children.Add(novelBooks); layout.Children.Add(novelState);
            layout.Children.Add(NovelUi.Bar(novelMore, novelCancel)); novelLayout = layout; NovelScroll.Content = layout;
        }
        novelStatus!.Children.Clear();
        foreach (var source in module.Search.States) novelStatus.Children.Add(NovelUi.Text(source.Source.Name + " · " + source.Status, 12, true));
        if (!shownNovelBooks.SequenceEqual(module.Search.Books))
        {
            shownNovelBooks = module.Search.Books.ToArray(); novelBooks!.Children.Clear();
            foreach (var book in shownNovelBooks)
            {
                var card = new StackPanel { Width = 148, Margin = new(0, 0, 24, 24) };
                card.Children.Add(new NovelImage(module.Sources, book) { Width = 148, Height = 200, Margin = new(0, 0, 0, 10) });
                card.Children.Add(NovelUi.Text(book.Name, 15)); card.Children.Add(NovelUi.Text(book.Author, 12, true));
                var button = NovelUi.Button(book.Name, () => module.RunUi(() => page.OpenNovelAsync(book)));
                button.Content = card; button.Padding = new(0); novelBooks.Children.Add(button);
            }
        }
        novelState!.Text = module.Search.Status; novelMore!.IsEnabled = module.Search.CanLoadMore; novelCancel!.IsEnabled = module.Search.Loading;
    }
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
                ? new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().UIElementColor(button.IsEnabled
                    ? Windows.UI.ViewManagement.UIElementType.WindowText : Windows.UI.ViewManagement.UIElementType.GrayText))
                : (Brush)DesktopVisual.Resource(button, !button.IsEnabled ? "MeowFilm.Brush.Disabled"
                    : tab.Selected ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Muted");
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
    private async void More_Click(object sender, RoutedEventArgs e) => await page.LoadMoreAsync();
    private async void Card_Activated(object? sender, FilmCardActivatedEventArgs e) => await page.OpenCardAsync(e.Item);
}
