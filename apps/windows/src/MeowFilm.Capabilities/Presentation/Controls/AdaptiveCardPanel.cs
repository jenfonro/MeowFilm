using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

// Native card layout: only artwork changes size; text keeps its typography.
public class AdaptiveCardPanel : Panel
{
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(
        nameof(MinimumCardWidth), typeof(double), typeof(AdaptiveCardPanel),
        new FrameworkPropertyMetadata(144d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaximumCardWidthProperty = DependencyProperty.Register(
        nameof(MaximumCardWidth), typeof(double), typeof(AdaptiveCardPanel),
        new FrameworkPropertyMetadata(176d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty CardGapProperty = DependencyProperty.Register(
        nameof(CardGap), typeof(double), typeof(AdaptiveCardPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(AdaptiveCardPanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public double MaximumCardWidth { get => (double)GetValue(MaximumCardWidthProperty); set => SetValue(MaximumCardWidthProperty, value); }
    public double CardGap { get => (double)GetValue(CardGapProperty); set => SetValue(CardGapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    private int columns = 1;
    private double pitch, rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? MaximumCardWidth : availableSize.Width;
        (columns, pitch) = Shelf.MeasureCards(width, MinimumCardWidth, CardGap);
        rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new(Math.Max(0, Math.Min(MaximumCardWidth, pitch - CardGap)), double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        int rows = (InternalChildren.Count + columns - 1) / columns;
        return new(width, rows == 0 ? 0 : rows * (rowHeight + RowGap) - RowGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int index = 0; index < InternalChildren.Count; index++)
            InternalChildren[index].Arrange(new(index % columns * pitch, index / columns * (rowHeight + RowGap),
                Math.Max(0, Math.Min(MaximumCardWidth, pitch - CardGap)), rowHeight));
        return finalSize;
    }
}
