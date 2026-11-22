using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

public sealed class NovelBookGrid : Panel
{
    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns), typeof(int), typeof(NovelBookGrid),
        new PropertyMetadata(6, (d, _) => ((NovelBookGrid)d).InvalidateMeasure()));

    public static readonly DependencyProperty MinimumWidthProperty = DependencyProperty.Register(
        nameof(MinimumWidth), typeof(double), typeof(NovelBookGrid),
        new PropertyMetadata(138d, (d, _) => ((NovelBookGrid)d).InvalidateMeasure()));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(NovelBookGrid),
        new PropertyMetadata(16d, (d, _) => ((NovelBookGrid)d).InvalidateMeasure()));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(NovelBookGrid),
        new PropertyMetadata(22d, (d, _) => ((NovelBookGrid)d).InvalidateMeasure()));

    private int columns = 1;
    private double itemWidth;
    private readonly List<double> rowHeights = [];

    public int MaximumColumns { get => (int)GetValue(MaximumColumnsProperty); set => SetValue(MaximumColumnsProperty, value); }
    public double MinimumWidth { get => (double)GetValue(MinimumWidthProperty); set => SetValue(MinimumWidthProperty, value); }
    public double ColumnGap { get => (double)GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }

    public NovelBookGrid() { }

    protected override Size MeasureOverride(Size availableSize)
    {
        int maxColumns = Math.Max(1, MaximumColumns);
        double minWidth = Math.Max(1, MinimumWidth);
        double colGap = Math.Max(0, ColumnGap);
        double rowGap = Math.Max(0, RowGap);

        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : minWidth;
        columns = Math.Min(maxColumns, Math.Max(1, (int)((width + colGap) / (minWidth + colGap))));
        itemWidth = Math.Max(0, (width - (columns - 1) * colGap) / columns);
        rowHeights.Clear();
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            int row = i / columns;
            if (row == rowHeights.Count) rowHeights.Add(0);
            rowHeights[row] = Math.Max(rowHeights[row], Children[i].DesiredSize.Height);
        }
        return new Size(width, rowHeights.Sum() + Math.Max(0, rowHeights.Count - 1) * rowGap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double colGap = Math.Max(0, ColumnGap);
        double rowGap = Math.Max(0, RowGap);
        double top = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            int row = i / columns, column = i % columns;
            if (column == 0 && row > 0) top += rowHeights[row - 1] + rowGap;
            Children[i].Arrange(new Rect(column * (itemWidth + colGap), top, itemWidth, rowHeights[row]));
        }
        return finalSize;
    }
}
