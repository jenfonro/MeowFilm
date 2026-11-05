using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

// Shelf cards share a pitch, including their trailing margin. The full extent and
// target range are known before generating any containers, even on a distant restore.
public sealed class ShelfVirtualizingPanel : VirtualizingPanel, IScrollInfo
{
    private Shelf? shelf;
    private double pitch = 168;
    private double gap = 16;
    private int columns = 1;
    private ScrollViewer? scrollOwner;

    protected override bool HasLogicalOrientation => true;
    protected override Orientation LogicalOrientation => Orientation.Horizontal;
    internal double MaximumOffset { get; private set; }
    public double HorizontalOffset { get; private set; }
    public double VerticalOffset => 0;
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public ScrollViewer ScrollOwner
    {
        get => scrollOwner!;
        set
        {
            scrollOwner = value;
            scrollOwner?.InvalidateScrollInfo();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        shelf ??= FindShelf();
        // Hidden retained views keep their containers and last visible geometry.
        // Shelf invalidates measure on return, applying the saved offset and any
        // viewport change together rather than consuming a restore while hidden.
        if (shelf is { IsLoaded: true, IsVisible: false }) return DesiredSize;
        var items = ItemsControl.GetItemsOwner(this);
        var itemCount = items?.Items.Count ?? 0;
        var (target, spacing) = shelf?.LayoutMetrics() ?? (150d, 16d);
        var viewport = double.IsInfinity(availableSize.Width) ? target + spacing : availableSize.Width;
        var (count, nextPitch) = Shelf.MeasureCards(viewport, target, spacing,
            banner: shelf?.DataContext is MusicCatalogSection { IsBannerSection: true });
        var maximum = Math.Max(0, itemCount - count) * nextPitch;
        var offset = shelf?.PrepareCardLayout(this, viewport, count, nextPitch, maximum) ??
            Math.Clamp(HorizontalOffset, 0, maximum);
        var previous = (ExtentWidth, ExtentHeight, ViewportWidth, ViewportHeight, HorizontalOffset);
        pitch = nextPitch;
        gap = spacing;
        columns = count;
        MaximumOffset = maximum;
        HorizontalOffset = offset;
        ViewportWidth = viewport;
        ExtentWidth = itemCount * pitch;

        // One viewport on each side matches the shelf's existing cache policy.
        var first = Math.Max(0, (int)Math.Floor(offset / pitch) - count);
        var last = Math.Min(itemCount - 1, (int)Math.Ceiling((offset + viewport) / pitch) + count - 1);
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            ((IRecyclingItemContainerGenerator)generator).Recycle(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
        double height = 0;
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                    if (newlyRealized || childIndex >= children.Count || !ReferenceEquals(children[childIndex], child))
                    {
                        InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(pitch, availableSize.Height));
                    height = Math.Max(height, child.DesiredSize.Height);
                }
        }
        ExtentHeight = height;
        ViewportHeight = double.IsInfinity(availableSize.Height) ? height : availableSize.Height;
        if (previous != (ExtentWidth, ExtentHeight, ViewportWidth, ViewportHeight, HorizontalOffset))
            scrollOwner?.InvalidateScrollInfo();
        return new Size(Math.Min(viewport, ExtentWidth), Math.Min(height, ViewportHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var child = InternalChildren[childIndex];
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            child.Arrange(new Rect(index * pitch - HorizontalOffset, 0, pitch,
                Math.Max(finalSize.Height, child.DesiredSize.Height)));
        }
        shelf?.CompleteCardLayout(this);
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The generator has already updated its item map; remove the corresponding
        // visuals before the next measure generates the new viewport.
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
        }
    }

    internal void ScrollTo(double offset)
    {
        if (double.IsNaN(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        offset = Math.Clamp(offset, 0, MaximumOffset);
        if (HorizontalOffset == offset) return;
        HorizontalOffset = offset;
        InvalidateMeasure();
        scrollOwner?.InvalidateScrollInfo();
    }

    public void SetHorizontalOffset(double offset)
    {
        var previous = HorizontalOffset;
        ScrollTo(offset);
        if (previous != HorizontalOffset) shelf?.OnNativeScroll();
    }
    public void SetVerticalOffset(double offset) { }
    // Retain WPF's pixel-scroll line and wheel increments. Shelf owns card paging.
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 16);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 16);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - 48);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + 48);
    public void LineUp() { }
    public void LineDown() { }
    public void PageUp() { }
    public void PageDown() { }
    public void MouseWheelUp() { }
    public void MouseWheelDown() { }
    protected override void BringIndexIntoView(int index)
    {
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        var left = index * pitch;
        var right = left + pitch - gap;
        if (left < HorizontalOffset) SetHorizontalOffset(left);
        else if (right > HorizontalOffset + ViewportWidth)
            SetHorizontalOffset((index - columns + 1) * pitch);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual == this || !IsAncestorOf(visual)) return Rect.Empty;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var previous = HorizontalOffset;
        if (bounds.Left < 0) SetHorizontalOffset(previous + bounds.Left);
        else if (bounds.Right > ViewportWidth)
            SetHorizontalOffset(previous + bounds.Right - ViewportWidth);
        bounds.Offset(previous - HorizontalOffset, 0);
        bounds.Intersect(new Rect(0, 0, ViewportWidth, ViewportHeight));
        return bounds;
    }

    private Shelf? FindShelf()
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Shelf owner) return owner;
        return null;
    }
}
