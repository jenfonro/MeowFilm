using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmShelf : UserControl
{
    public FilmHomeSection? Section => DataContext as FilmHomeSection;

    public event EventHandler<FilmCardActivatedEventArgs>? ItemActivated;
    public event EventHandler? RetryRequested;

    public FilmShelf()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bindings?.Update();
        Loaded += (_, _) => Bindings?.Update();
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
