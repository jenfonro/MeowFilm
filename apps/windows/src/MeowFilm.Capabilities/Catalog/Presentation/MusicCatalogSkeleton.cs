using System.Windows;
using System.Windows.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// A first-load template at the same scale as the real shelves, not predicted data.
public sealed class MusicCatalogSkeleton : Panel
{
    public static readonly DependencyProperty IsHomeProperty = DependencyProperty.Register(nameof(IsHome),
        typeof(bool), typeof(MusicCatalogSkeleton), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool IsHome { get => (bool)GetValue(IsHomeProperty); set => SetValue(IsHomeProperty, value); }
    private readonly List<Rect> bounds = [];

    public MusicCatalogSkeleton() { IsHitTestVisible = false; ClipToBounds = true; }

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
            const double height = 160; // Unknown until artwork publishes its intrinsic ratio.
            for (int i = 0; i < 2; i++) bounds.Add(new(i * pitch, y, cardWidth, height));
            y += height + 56;
        }
        bounds.Add(new(0, y, Math.Min(132, width), 20));
        y += 42;
        y = Cards(width, y, 124, footer: false);
        while (Children.Count > bounds.Count) Children.RemoveAt(Children.Count - 1);
        while (Children.Count < bounds.Count) Children.Add(new LoadingSkeleton { CornerRadius = new(8) });
        for (int i = 0; i < bounds.Count; i++) Children[i].Measure(bounds[i].Size);
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
        for (int i = 0; i < bounds.Count; i++) Children[i].Arrange(bounds[i]);
        return finalSize;
    }
}
