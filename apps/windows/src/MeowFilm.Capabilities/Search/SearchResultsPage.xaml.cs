using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class SearchResultsView : UserControl, IDesktopHeaderSource
{
    private readonly SearchResultsPage page;
    internal ScrollViewer? FilmScroll => MusicVisuals.Descendant<ScrollViewer>(FilmItems);
    internal SearchResultsView(SearchResultsPage page)
    {
        this.page = page;
        InitializeComponent();
        DataContext = page;
        Loaded += (_, _) => SizeFilmCards();
    }
    private void Film_SizeChanged(object sender, SizeChangedEventArgs e) => SizeFilmCards();
    private void Film_ContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (!e.InRecycleQueue) e.RegisterUpdateCallback((_, _) => SizeFilmCards());
    }
    private void SizeFilmCards()
    {
        if (FilmItems.ItemsPanelRoot is not ItemsWrapGrid panel || FilmItems.ActualWidth <= 0) return;
        var (_, pitch) = Shelf.MeasureCards(FilmItems.ActualWidth, 180, 36);
        double width = Math.Max(0, pitch - 36);
        panel.ItemWidth = pitch;
        // Measure realized native cards; do not create a second image/visual tree for sizing.
        double height = 0;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (MusicVisuals.Descendant<FilmCard>(panel.Children[i]) is not { } card) continue;
            card.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, card.DesiredSize.Height + 36);
        }
        if (height > 0 && panel.ItemHeight != height) panel.ItemHeight = height;
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
    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab }) await page.OpenTabAsync(tab);
    }
    private async void More_Click(object sender, RoutedEventArgs e) => await page.LoadMoreAsync();
    private async void Card_Activated(object? sender, FilmCardActivatedEventArgs e) => await page.OpenCardAsync(e.Item);
}
