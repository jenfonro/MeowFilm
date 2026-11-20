using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmShelf : UserControl
{
    internal ScrollViewer? ScrollHost => Cards.ScrollHost;
    internal bool IsSettled => Cards.IsSettled;

    public event EventHandler<FilmCardActivatedEventArgs>? ItemActivated;
    public event EventHandler? RetryRequested;

    public FilmShelf()
    {
        InitializeComponent();
        Cards.SetArtworkItemKeySelector(item => ((FilmCardItem)item).Id);
        Cards.SetArtworkItemResourcesSelector(item => FilmPlaybackPoster.ArtworkResources(((FilmCardItem)item).Poster));
    }

    private void Card_Activated(object? sender, FilmCardActivatedEventArgs e) =>
        ItemActivated?.Invoke(this, e);

    private void Retry_Click(object sender, RoutedEventArgs e)
    {

        RetryRequested?.Invoke(this, EventArgs.Empty);
    }
}
