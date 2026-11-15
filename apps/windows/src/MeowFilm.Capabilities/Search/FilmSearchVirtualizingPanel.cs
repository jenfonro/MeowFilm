using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

// Search owns a vertical, uniform card grid. The footer shares its pixel extent,
// but is not an item and never enters the card generator's position map.
public sealed class FilmSearchVirtualizingPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(
        nameof(MinimumCardWidth), typeof(double), typeof(FilmSearchVirtualizingPanel),
        new FrameworkPropertyMetadata(144d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaximumCardWidthProperty = DependencyProperty.Register(
        nameof(MaximumCardWidth), typeof(double), typeof(FilmSearchVirtualizingPanel),
        new FrameworkPropertyMetadata(176d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty CardGapProperty = DependencyProperty.Register(
        nameof(CardGap), typeof(double), typeof(FilmSearchVirtualizingPanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(FilmSearchVirtualizingPanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FooterTemplateProperty = DependencyProperty.Register(
        nameof(FooterTemplate), typeof(DataTemplate), typeof(FilmSearchVirtualizingPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (value, args) => ((FilmSearchVirtualizingPanel)value).footer.ContentTemplate = (DataTemplate?)args.NewValue));

    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public double MaximumCardWidth { get => (double)GetValue(MaximumCardWidthProperty); set => SetValue(MaximumCardWidthProperty, value); }
    public double CardGap { get => (double)GetValue(CardGapProperty); set => SetValue(CardGapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    public DataTemplate? FooterTemplate { get => (DataTemplate?)GetValue(FooterTemplateProperty); set => SetValue(FooterTemplateProperty, value); }

    private readonly ContentPresenter footer;
    private int columns = 1;
    private double pitch, cardWidth, rowHeight, gridHeight;
    private double? requestedOffset;
    private int? requestedIndex;
    private ScrollViewer? scrollOwner;
    private bool footerAttached;

    public FilmSearchVirtualizingPanel()
    {
        ClipToBounds = true;
        footer = new ContentPresenter();
        footer.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(DataContext)) { Source = this });
        IsVisibleChanged += (_, _) => { if (IsVisible) InvalidateMeasure(); };
    }

    protected override int VisualChildrenCount => base.VisualChildrenCount + (footerAttached ? 1 : 0);
    protected override Visual GetVisualChild(int index) =>
        index == base.VisualChildrenCount && footerAttached ? footer : base.GetVisualChild(index);
    protected override IEnumerator LogicalChildren =>
        (footerAttached ? new[] { footer } : Array.Empty<ContentPresenter>()).GetEnumerator();
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public ScrollViewer ScrollOwner
    {
        get => scrollOwner!;
        set { scrollOwner = value; scrollOwner?.InvalidateScrollInfo(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (IsLoaded && !IsVisible) return DesiredSize;
        // ItemsPresenter requires an empty panel while applying its template.
        // Attach the non-item visual only when that step has completed.
        if (!footerAttached)
        {
            footerAttached = true;
            AddLogicalChild(footer);
            AddVisualChild(footer);
        }
        var previous = (ExtentWidth, ExtentHeight, ViewportWidth, ViewportHeight, VerticalOffset);
        int count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        double width = double.IsInfinity(availableSize.Width) ? MaximumCardWidth : availableSize.Width;
        ViewportWidth = width;
        ViewportHeight = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        footer.Measure(new Size(width, double.PositiveInfinity));

        var (nextColumns, nextPitch) = Shelf.MeasureCards(width, MinimumCardWidth, CardGap);
        double nextWidth = Math.Max(0, Math.Min(MaximumCardWidth, nextPitch - CardGap));
        double target = requestedOffset ?? VerticalOffset;
        bool explicitOffset = requestedOffset.HasValue;
        requestedOffset = null;
        double priorStride = rowHeight + RowGap;
        int anchor = rowHeight > 0 ? (int)Math.Floor(VerticalOffset / priorStride) * columns : 0;
        double fraction = rowHeight > 0 ? VerticalOffset / priorStride - Math.Floor(VerticalOffset / priorStride) : 0;
        bool geometryChanged = cardWidth != nextWidth || columns != nextColumns;
        bool atEnd = !explicitOffset && (geometryChanged || previous.ViewportHeight != ViewportHeight) &&
            previous.ExtentHeight > previous.ViewportHeight && VerticalOffset > 0 &&
            Math.Abs(VerticalOffset - (previous.ExtentHeight - previous.ViewportHeight)) < .5;
        columns = nextColumns;
        pitch = nextPitch;
        cardWidth = nextWidth;

        if (count == 0) rowHeight = 0;
        else if (geometryChanged || rowHeight == 0)
        {
            // One real card supplies the row height; a distant restore does not
            // first create all preceding rows. Typography stays in FilmCard.
            var sample = ContainerAt(Math.Min(anchor, count - 1));
            sample.Measure(new Size(cardWidth, double.PositiveInfinity));
            rowHeight = sample.DesiredSize.Height;
            if (!explicitOffset && geometryChanged)
                target = Math.Floor((double)anchor / columns) * (rowHeight + RowGap) + fraction * (rowHeight + RowGap);
        }

        int rows = (count + columns - 1) / columns;
        int? bringIntoView = requestedIndex;
        requestedIndex = null;
        while (true)
        {
            gridHeight = rows == 0 ? 0 : rows * (rowHeight + RowGap) - RowGap;
            ExtentWidth = width;
            ExtentHeight = gridHeight + footer.DesiredSize.Height;
            VerticalOffset = Math.Clamp(atEnd ? ExtentHeight : target, 0, Math.Max(0, ExtentHeight - ViewportHeight));
            if (bringIntoView is { } index && count > 0)
            {
                double top = Math.Min(index, count - 1) / columns * (rowHeight + RowGap);
                VerticalOffset = VisibleOffset(top, top + rowHeight);
            }
            var range = RealizedRange(count, rows);
            RecycleOutside(range.First, range.Last);
            double measuredHeight = MeasureRange(range.First, range.Last);
            if (measuredHeight <= rowHeight) break;

            // A newly encountered subtitle may increase the shared row height.
            // Keep the current row/fraction, rather than jumping to another film.
            double row = VerticalOffset / (rowHeight + RowGap);
            rowHeight = measuredHeight;
            target = row * (rowHeight + RowGap);
        }
        if (previous != (ExtentWidth, ExtentHeight, ViewportWidth, ViewportHeight, VerticalOffset))
            scrollOwner?.InvalidateScrollInfo();
        return new Size(width, Math.Min(ExtentHeight, ViewportHeight));
    }

    private (int First, int Last) RealizedRange(int count, int rows)
    {
        if (count == 0 || ViewportHeight == 0) return (0, -1);
        double stride = rowHeight + RowGap;
        int firstRow = Math.Max(0, Math.Min(rows - 1, (int)Math.Floor(VerticalOffset / stride)) - 1);
        int lastRow = Math.Min(rows - 1, (int)Math.Ceiling((VerticalOffset + ViewportHeight) / stride));
        return (firstRow * columns, Math.Min(count - 1, (lastRow + 1) * columns - 1));
    }

    private UIElement ContainerAt(int index)
    {
        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(index);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            var child = (UIElement)generator.GenerateNext(out bool newlyRealized);
            if (newlyRealized)
            {
                InsertInternalChild(childIndex, child);
                generator.PrepareItemContainer(child);
            }
            return child;
        }
    }

    private void RecycleOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (int childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            ((IRecyclingItemContainerGenerator)generator).Recycle(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private double MeasureRange(int first, int last)
    {
        if (last < first) return 0;
        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        double height = 0;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
            for (int index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out bool newlyRealized);
                if (newlyRealized || childIndex >= InternalChildren.Count || !ReferenceEquals(InternalChildren[childIndex], child))
                {
                    InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(cardWidth, double.PositiveInfinity));
                height = Math.Max(height, child.DesiredSize.Height);
            }
        return height;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (int childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            InternalChildren[childIndex].Arrange(new Rect(index % columns * pitch,
                index / columns * (rowHeight + RowGap) - VerticalOffset, cardWidth, rowHeight));
        }
        footer.Arrange(new Rect(0, gridHeight - VerticalOffset, ViewportWidth, footer.DesiredSize.Height));
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Replace:
                if (args.ItemUICount > 0)
                {
                    ItemContainerGenerator.Remove(args.Position, args.ItemUICount);
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                }
                break;
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
                rowHeight = 0;
                break;
        }
        InvalidateMeasure();
    }

    private double VisibleOffset(double top, double bottom)
    {
        double offset = VerticalOffset;
        if (top < offset) offset = top;
        else if (bottom > offset + ViewportHeight) offset = bottom - ViewportHeight;
        return Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
    }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        requestedOffset = offset;
        requestedIndex = null;
        InvalidateMeasure();
    }
    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset((requestedOffset ?? VerticalOffset) - 16);
    public void LineDown() => SetVerticalOffset((requestedOffset ?? VerticalOffset) + 16);
    public void MouseWheelUp() => SetVerticalOffset((requestedOffset ?? VerticalOffset) - 48);
    public void MouseWheelDown() => SetVerticalOffset((requestedOffset ?? VerticalOffset) + 48);
    public void PageUp() => SetVerticalOffset((requestedOffset ?? VerticalOffset) - ViewportHeight);
    public void PageDown() => SetVerticalOffset((requestedOffset ?? VerticalOffset) + ViewportHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    protected override void BringIndexIntoView(int index)
    {
        int count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        requestedIndex = index;
        requestedOffset = null;
        InvalidateMeasure();
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual == this || !IsAncestorOf(visual)) return Rect.Empty;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        double offset = VisibleOffset(bounds.Top + VerticalOffset, bounds.Bottom + VerticalOffset);
        bounds.Offset(0, VerticalOffset - offset);
        SetVerticalOffset(offset);
        bounds.Intersect(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return bounds;
    }
}
