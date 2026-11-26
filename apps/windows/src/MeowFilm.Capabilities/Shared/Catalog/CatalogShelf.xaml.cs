using MeowFilm.Desktop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

public partial class CatalogShelf : UserControl
{
    private readonly DesktopViewport viewport;
    private object? boundContext;
    public CatalogHomeSection? Section => DataContext as CatalogHomeSection;

    public event EventHandler<CatalogCardActivatedEventArgs>? ItemActivated;
    public event EventHandler? RetryRequested;
    public event EventHandler? Admitted;

    public CatalogShelf()
    {
        InitializeComponent();
        viewport = new DesktopViewport(this, OnViewportChanged, active: false);
        DataContextChanged += (_, _) => SyncBindings();
        Loaded += (_, _) => { SyncBindings(); viewport.Sync(Section is not null); NotifyIfAdmitted(); };
        Unloaded += (_, _) => { if (IsLoaded) return; viewport.Reset(active: false); };
        Cards.SetArtworkItemKeySelector(item => ((ICatalogCardDisplay)item).Id);
        Cards.SetArtworkItemResourcesSelector(item =>
            item is ICatalogCardDisplay { NovelBook: null, Poster.Length: > 0 } card
                ? FilmPlaybackPoster.ArtworkResources(card.Poster)
                : []);
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
        if (!ReferenceEquals(boundContext, DataContext))
        {
            boundContext = DataContext;
            Bindings.Update();
            viewport.Reset(IsLoaded && Section is not null);
        }
        NotifyIfAdmitted();
    }

    private void OnViewportChanged() => NotifyIfAdmitted();

    private void NotifyIfAdmitted()
    {
        if (viewport.Admitted && viewport.IsVisible && Section is not null)
            Admitted?.Invoke(this, EventArgs.Empty);
    }

    private void Card_Activated(object? sender, CatalogCardActivatedEventArgs e) =>
        ItemActivated?.Invoke(this, e);

    private void Retry_Click(object sender, RoutedEventArgs e) =>
        RetryRequested?.Invoke(this, EventArgs.Empty);
}
