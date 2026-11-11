using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class SearchResultsView : UserControl, IDesktopHeaderSource
{
    private readonly SearchResultsPage page;
    internal SearchResultsView(SearchResultsPage page)
    {
        this.page = page;
        InitializeComponent();
        DataContext = page;
    }
    public DesktopHeaderContribution Header => new("") { SearchTargetId = page.SelectedTarget, SearchContextId = page.SelectedTab };
    public event EventHandler? HeaderChanged;
    internal void Present()
    {
        bool film = page.SelectedTab == "film";
        if (film) MusicHost.Content = null;
        else if (!ReferenceEquals(MusicHost.Content, page.Music.View))
        {
            page.Music.Presentation.Page.ProjectSearchResults(true);
            MusicHost.Content = page.Music.View;
        }
        MusicHost.Visibility = film ? Visibility.Collapsed : Visibility.Visible;
        FilmBody.Visibility = film ? Visibility.Visible : Visibility.Collapsed;
        FilmTools.Visibility = FilmBody.Visibility;
        HeaderChanged?.Invoke(this, EventArgs.Empty);
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
