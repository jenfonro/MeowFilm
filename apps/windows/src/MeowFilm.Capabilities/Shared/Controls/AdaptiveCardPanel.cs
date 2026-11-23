using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

// Native card layout: only artwork changes size; text keeps its typography.
public class AdaptiveCardPanel : Panel
{
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(
        nameof(MinimumCardWidth), typeof(double), typeof(AdaptiveCardPanel),
        new PropertyMetadata(144d, (owner, _) => ((UIElement)owner).InvalidateMeasure()));
    public static readonly DependencyProperty MaximumCardWidthProperty = DependencyProperty.Register(
        nameof(MaximumCardWidth), typeof(double), typeof(AdaptiveCardPanel),
        new PropertyMetadata(176d, (owner, _) => ((UIElement)owner).InvalidateMeasure()));
    public static readonly DependencyProperty CardGapProperty = DependencyProperty.Register(
        nameof(CardGap), typeof(double), typeof(AdaptiveCardPanel),
        new PropertyMetadata(16d, (owner, _) => ((UIElement)owner).InvalidateMeasure()));
    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(AdaptiveCardPanel),
        new PropertyMetadata(24d, (owner, _) => ((UIElement)owner).InvalidateMeasure()));
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(AdaptiveCardPanel),
        new PropertyMetadata(int.MaxValue, (owner, _) => ((UIElement)owner).InvalidateMeasure()));

    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public double MaximumCardWidth { get => (double)GetValue(MaximumCardWidthProperty); set => SetValue(MaximumCardWidthProperty, value); }
    public double CardGap { get => (double)GetValue(CardGapProperty); set => SetValue(CardGapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    private int columns = 1;
    private double pitch, rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? MaximumCardWidth : availableSize.Width;
        (columns, pitch) = Shelf.MeasureCards(width, MinimumCardWidth, CardGap);
        if (columns > MaximumColumns) { columns = MaximumColumns; pitch = (width + CardGap) / columns; }
        rowHeight = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new(Math.Max(0, Math.Min(MaximumCardWidth, pitch - CardGap)), double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        int rows = (Children.Count + columns - 1) / columns;
        return new(width, rows == 0 ? 0 : rows * (rowHeight + RowGap) - RowGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int index = 0; index < Children.Count; index++)
            Children[index].Arrange(new(index % columns * pitch, index / columns * (rowHeight + RowGap),
                Math.Max(0, Math.Min(MaximumCardWidth, pitch - CardGap)), rowHeight));
        return finalSize;
    }
}
