using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// A first-load template at the same scale as the real shelves, not predicted data.
public sealed class MusicCatalogSkeleton : Panel
{
    public static readonly DependencyProperty IsHomeProperty = DependencyProperty.Register(nameof(IsHome),
        typeof(bool), typeof(MusicCatalogSkeleton), new PropertyMetadata(false, (owner, _) =>
        {
            var skeleton = (MusicCatalogSkeleton)owner;
            skeleton.QueuePlaceholders();
            skeleton.InvalidateMeasure();
        }));
    public bool IsHome { get => (bool)GetValue(IsHomeProperty); set => SetValue(IsHomeProperty, value); }
    private readonly List<Rect> bounds = [];
    private bool placeholdersQueued;

    public MusicCatalogSkeleton()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => QueuePlaceholders();
        SizeChanged += (_, _) => QueuePlaceholders();
    }

    private void QueuePlaceholders()
    {
        if (placeholdersQueued) return;
        placeholdersQueued = DispatcherQueue.TryEnqueue(() =>
        {
            placeholdersQueued = false;
            UpdatePlaceholders();
        });
    }

    private void UpdatePlaceholders()
    {
        if (!IsLoaded || ActualWidth <= 0) return;
        var (covers, _) = Shelf.MeasureCards(ActualWidth, 124, 16);
        int lead = IsHome ? Shelf.MeasureCards(ActualWidth, 150, 16).Count : 2;
        int count = lead + 1 + 3 * covers;
        // WinUI locks the child collection during measure and arrange.
        while (Children.Count > count) Children.RemoveAt(Children.Count - 1);
        while (Children.Count < count) Children.Add(new LoadingSkeleton { CornerRadius = new(8) });
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? Math.Max(0, available.Width) : 0;
        bounds.Clear();
        double y = 0;
        if (IsHome)
            y = Cards(width, y, 150, footer: true) + 32;
        else
        {
            var (_, pitch) = Shelf.MeasureCards(width, 260, 20, banner: true);
            var cardWidth = Math.Max(0, pitch - 20);
            double height = cardWidth / 2.56;
            for (int i = 0; i < 2; i++) bounds.Add(new(i * pitch, y, cardWidth, height));
            y += height + 56;
        }
        bounds.Add(new(0, y, Math.Min(132, width), 20));
        y += 42;
        y = Cards(width, y, 124, footer: false);
        for (int i = 0; i < Children.Count; i++)
            Children[i].Measure(i < bounds.Count ? new(bounds[i].Width, bounds[i].Height) : new Size());
        return new(width, y);
    }

    private double Cards(double width, double y, double minimum, bool footer)
    {
        var (count, pitch) = Shelf.MeasureCards(width, minimum, 16);
        double cardWidth = Math.Max(0, Math.Min(168, pitch - 16));
        for (int i = 0; i < count; i++)
        {
            bounds.Add(new(i * pitch, y, cardWidth, cardWidth + (footer ? 48 : 0)));
            if (!footer)
            {
                bounds.Add(new(i * pitch, y + cardWidth + 8, cardWidth * .88, 16));
                bounds.Add(new(i * pitch, y + cardWidth + 30, cardWidth * .6, 12));
            }
        }
        return y + cardWidth + 48;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i].Arrange(i < bounds.Count ? bounds[i] : new Rect());
        return finalSize;
    }
}
