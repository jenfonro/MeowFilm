using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed record SettingsReorderMove(ListBox List, object Source, object Target);

internal sealed class SettingsReorderDrag
{
    private readonly FrameworkElement viewport;
    private readonly DispatcherTimer scrolling;
    private readonly SettingsReorderShift shifts = new();
    private ScrollViewer? scroller;
    private Thumb? grip;
    private ListBox? list;
    private ListBoxItem? sourceContainer;
    private object[] rows = [];
    private Rect[] rowBounds = [];
    private int sourceIndex;
    private object? source;
    private Point start;
    private double sourceOpacity;
    private bool moved, highContrast;
    private int destination = -1;
    private AdornerLayer? layer;
    private PriorityDragAdorner? preview;

    internal SettingsReorderDrag(FrameworkElement viewport)
    {
        this.viewport = viewport;
        scrolling = new(DispatcherPriority.Input, viewport.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(32) };
        scrolling.Tick += ScrollTick;
    }

    internal bool IsActive => grip is not null;
    internal int DestinationIndex => destination;
    internal event Action? StateChanged;

    internal void Begin(Thumb handle, ListBox owner, Point position)
    {
        Cancel();
        if (!owner.IsEnabled || !owner.IsVisible ||
            ItemsControl.ContainerFromElement(owner, handle) is not ListBoxItem container ||
            owner.ItemContainerGenerator.ItemFromContainer(container) is not { } item ||
            item == DependencyProperty.UnsetValue) return;
        grip = handle; list = owner; sourceContainer = container; source = item;
        rows = owner.Items.Cast<object>().ToArray();
        rowBounds = new Rect[rows.Length];
        for (int index = 0; index < rows.Length; index++)
        {
            if (owner.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row)
            { Cancel(); return; }
            rowBounds[index] = new(row.TranslatePoint(new Point(), owner), row.RenderSize);
        }
        sourceIndex = Array.IndexOf(rows, source);
        start = position; sourceOpacity = container.Opacity;
        highContrast = DesktopTheme.GetIsHighContrast(owner);
        ((INotifyCollectionChanged)owner.Items).CollectionChanged += RowsChanged;
        owner.IsEnabledChanged += AvailabilityChanged;
        owner.IsVisibleChanged += AvailabilityChanged;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(viewport); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll)
            {
                scroller = scroll;
                scroller.ScrollChanged += ScrollerChanged;
                break;
            }
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
        StateChanged?.Invoke();
    }

    internal void Update(Point position)
    {
        if (!Current()) { Cancel(); return; }
        if (!moved && Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!moved)
        {
            moved = true;
            layer = AdornerLayer.GetAdornerLayer(viewport);
            if (layer is null) { Cancel(); return; }
            preview = new(viewport, list!, sourceContainer!, highContrast);
            layer.Add(preview);
            sourceContainer!.SetCurrentValue(UIElement.OpacityProperty, 0d);
            if (grip!.IsDragging) scrolling.Start();
        }

        var owner = list!;
        var point = viewport.TranslatePoint(position, owner);
        var visible = VisibleBounds();
        destination = -1;
        if (!visible.IsEmpty && visible.Contains(position) && point.X >= 0 &&
            point.X <= owner.ActualWidth && point.Y >= 0 && point.Y <= owner.ActualHeight)
        {
            int slot = rows.Length;
            double center = point.Y - (viewport.TranslatePoint(start, owner).Y - rowBounds[sourceIndex].Top) +
                rowBounds[sourceIndex].Height / 2;
            for (int index = 0; index < rows.Length; index++)
                if (center < rowBounds[index].Top + rowBounds[index].Height / 2)
                { slot = index; break; }
            destination = Math.Clamp(slot - (sourceIndex < slot ? 1 : 0), 0, rows.Length - 1);
        }
        int target = destination < 0 ? sourceIndex : destination;
        for (int index = 0; index < rows.Length; index++)
            if (owner.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row)
                shifts.Apply(row, index, sourceIndex, target, rowBounds[sourceIndex].Height,
                    SystemParameters.ClientAreaAnimation && !highContrast);
        double sourceTop = owner.TranslatePoint(new Point(0, rowBounds[sourceIndex].Top), viewport).Y;
        preview?.Move(position.Y - (start.Y - sourceTop), visible);
    }

    internal SettingsReorderMove? Complete(Point position, bool cancelled)
    {
        if (!cancelled) Update(position);
        var result = !cancelled && moved && Current() && destination >= 0 && destination != sourceIndex
            ? new SettingsReorderMove(list!, source!, rows[destination]) : null;
        Reset();
        return result;
    }

    internal void Cancel()
    {
        var handle = grip;
        Reset();
        if (handle?.IsDragging == true) handle.CancelDrag();
    }

    private bool Current() => grip is not null && list is not null && list.IsEnabled && list.IsVisible &&
        DesktopTheme.GetIsHighContrast(list) == highContrast && list.Items.Count == rows.Length &&
        list.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second));

    private void ScrollTick(object? sender, EventArgs e)
    {
        if (!Current() || Mouse.LeftButton != MouseButtonState.Pressed) { Cancel(); return; }
        if (scroller is null) return;
        var point = Mouse.GetPosition(viewport);
        var visible = VisibleBounds();
        if (visible.IsEmpty || point.X < visible.Left || point.X > visible.Right) return;
        double offset = ScrollOffset(scroller.VerticalOffset, scroller.ScrollableHeight, visible.Height, point.Y - visible.Top);
        if (offset != scroller.VerticalOffset)
        {
            scroller.ScrollToVerticalOffset(offset);
            scroller.UpdateLayout();
        }
        Update(Mouse.GetPosition(viewport));
    }
    private void ScrollerChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, scroller)) return;
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) { Cancel(); return; }
        if (e.VerticalChange != 0 && grip?.IsDragging == true) Update(Mouse.GetPosition(viewport));
    }
    private void SystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast)) Cancel();
    }

    private Rect VisibleBounds()
    {
        var bounds = new Rect(viewport.RenderSize);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(viewport); parent is Visual;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FrameworkElement element) continue;
            if (element.ClipToBounds || element is ScrollViewer or ScrollContentPresenter)
            {
                var transform = viewport.TransformToAncestor(element).Inverse;
                if (transform is null) return Rect.Empty;
                bounds.Intersect(transform.TransformBounds(new Rect(element.RenderSize)));
            }
            if (bounds.IsEmpty) break;
        }
        return bounds;
    }

    internal static BitmapSource Snapshot(FrameworkElement element)
    {
        var bounds = new Rect(element.RenderSize);
        var offset = VisualTreeHelper.GetOffset(element);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(offset.X, offset.Y, bounds.Width, bounds.Height)
                },
                null, bounds);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    internal static double ScrollOffset(double current, double maximum, double height, double y) =>
        y < 28 ? Math.Max(0, current - 12) : y > height - 28 ? Math.Min(maximum, current + 12) : current;

    internal static object? KeyboardTarget(ListBox list, object source, int direction)
    {
        int from = list.Items.IndexOf(source);
        int to = from + direction;
        return list.IsEnabled && from >= 0 && direction is -1 or 1 && to >= 0 && to < list.Items.Count
            ? list.Items[to] : null;
    }

    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Cancel();
    private void AvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is false) Cancel(); }

    private void Reset()
    {
        scrolling.Stop();
        if (scroller is not null) scroller.ScrollChanged -= ScrollerChanged;
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        if (list is not null)
        {
            ((INotifyCollectionChanged)list.Items).CollectionChanged -= RowsChanged;
            list.IsEnabledChanged -= AvailabilityChanged;
            list.IsVisibleChanged -= AvailabilityChanged;
        }
        sourceContainer?.SetCurrentValue(UIElement.OpacityProperty, sourceOpacity);
        shifts.Clear();
        if (preview is not null) layer?.Remove(preview);
        grip = null; list = null; sourceContainer = null; source = null;
        rows = []; rowBounds = []; scroller = null; moved = false; destination = -1; layer = null; preview = null;
        StateChanged?.Invoke();
    }

    private sealed class PriorityDragAdorner : Adorner
    {
        private readonly VisualCollection visuals;
        private readonly Border card;
        private readonly double left;
        private double top;
        private Rect visible;

        internal PriorityDragAdorner(FrameworkElement viewport, ListBox list, ListBoxItem sourceRow, bool contrast) : base(viewport)
        {
            visuals = new(this);
            IsHitTestVisible = false;
            var background = contrast ? SystemColors.WindowBrush : (Brush)list.FindResource("MeowFilm.Brush.Surface");
            var snapshot = Snapshot(sourceRow);
            visible = new Rect(viewport.RenderSize);
            left = sourceRow.TranslatePoint(new Point(), viewport).X;
            card = new()
            {
                Background = background, BorderBrush = contrast ? SystemColors.WindowTextBrush :
                    (Brush)list.FindResource("Settings.Brush.Control"),
                BorderThickness = new(1), CornerRadius = new(6), Height = Math.Max(1, sourceRow.ActualHeight),
                Width = Math.Max(1, Math.Min(sourceRow.ActualWidth, viewport.ActualWidth)),
                Child = new Image { Source = snapshot, Stretch = Stretch.Fill },
            };
            if (!contrast)
                card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .12 };
            visuals.Add(card);
        }

        internal void Move(double position, Rect visibleBounds)
        {
            top = position; visible = visibleBounds;
            Clip = new RectangleGeometry(visibleBounds.IsEmpty ? new Rect() : visibleBounds);
            InvalidateArrange(); InvalidateVisual();
        }

        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];
        protected override Size MeasureOverride(Size constraint)
        { card.Measure(new(card.Width, card.Height)); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var bounds = visible.IsEmpty ? new Rect(finalSize) : visible;
            double x = Math.Clamp(left, bounds.Left, Math.Max(bounds.Left, bounds.Right - card.DesiredSize.Width));
            double y = Math.Clamp(top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - card.DesiredSize.Height));
            card.Arrange(new Rect(new Point(x, y), card.DesiredSize));
            return finalSize;
        }
    }
}
