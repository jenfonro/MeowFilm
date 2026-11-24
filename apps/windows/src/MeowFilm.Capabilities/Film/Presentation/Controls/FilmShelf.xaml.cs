using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmShelf : UserControl
{
    private object? boundContext;
    public FilmHomeSection? Section => DataContext as FilmHomeSection;

    public event EventHandler<FilmCardActivatedEventArgs>? ItemActivated;
    public event EventHandler? RetryRequested;

    public FilmShelf()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncBindings();
        Loaded += (_, _) => SyncBindings();
        Cards.SetArtworkItemKeySelector(item => ((FilmCardItem)item).Id);
        Cards.SetArtworkItemResourcesSelector(item => FilmPlaybackPoster.ArtworkResources(((FilmCardItem)item).Poster));
    }

    private void SyncBindings()
    {
        if (ReferenceEquals(boundContext, DataContext)) return;
        boundContext = DataContext;
        Bindings.Update();
    }

    private void Card_Activated(object? sender, FilmCardActivatedEventArgs e) =>
        ItemActivated?.Invoke(this, e);

    private void Retry_Click(object sender, RoutedEventArgs e) =>
        RetryRequested?.Invoke(this, EventArgs.Empty);
}
