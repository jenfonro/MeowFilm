using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

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

    protected override Size MeasureOverride(Size availableSize)
    {
        var measured = base.MeasureOverride(availableSize);
        if (!double.IsFinite(availableSize.Width) || availableSize.Width <= 0) return measured;
        double scale = XamlRoot?.RasterizationScale ?? 1d;
        double Round(double value) => UseLayoutRounding ? Math.Floor(value * scale + 0.5) / scale : value;
        var (target, gap) = Cards.LayoutMetrics();
        var (_, pitch) = Shelf.MeasureCards(Math.Max(0, availableSize.Width - Cards.Margin.Left - Cards.Margin.Right), target, gap);
        double cardWidth = Round(Math.Clamp(Round(Math.Max(1, pitch)) - Round(gap), 0, 176));
        return new(measured.Width, Math.Max(measured.Height, Round(cardWidth * 1.5) + Round(9) + Round(21)));
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
