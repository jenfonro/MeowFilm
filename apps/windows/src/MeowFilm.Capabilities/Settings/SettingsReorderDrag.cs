using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal sealed record SettingsReorderMove(ListView List, object Source, object Target);

internal sealed class SettingsReorderDrag
{
    private readonly FrameworkElement viewport;
    private readonly Canvas overlay;
    private readonly DispatcherTimer scrolling = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly SettingsReorderShift shifts = new();
    private readonly UISettings settings = new();
    private ScrollViewer? scroller;
    private Thumb? grip;
    private ListView? list;
    private ListViewItem? sourceContainer;
    private INotifyCollectionChanged? collection;
    private object[] rows = [];
    private Rect[] rowBounds = [];
    private int sourceIndex, destination = -1, generation;
    private Point start, position;
    private double sourceOpacity;
    private bool moved, highContrast;
    private Image? preview;
    internal SettingsReorderDrag(FrameworkElement viewport, Canvas overlay)
    {
        this.viewport = viewport; this.overlay = overlay;
        scrolling.Tick += (_, _) => ScrollTick();
    }
    internal bool IsActive => grip is not null;
    internal int DestinationIndex => destination;
    internal void Begin(Thumb handle, ListView owner, Point point)
    {
        Cancel();
        if (!owner.IsEnabled || !DesktopVisual.IsVisible(owner) ||
            owner.ContainerFromItem(handle.DataContext) is not ListViewItem container) return;
        grip = handle; list = owner; sourceContainer = container;
        rows = owner.Items.Cast<object>().ToArray(); rowBounds = new Rect[rows.Length];
        for (int index = 0; index < rows.Length; index++)
        {
            if (owner.ContainerFromIndex(index) is not ListViewItem row) { Cancel(); return; }
            rowBounds[index] = row.TransformToVisual(owner).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
        }
        sourceIndex = Array.IndexOf(rows, handle.DataContext); start = position = point; sourceOpacity = container.Opacity;
        highContrast = DesktopTheme.GetIsHighContrast(owner);
        collection = owner.ItemsSource as INotifyCollectionChanged;
        if (collection is not null) collection.CollectionChanged += RowsChanged;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(viewport); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) { scroller = scroll; break; }
    }
    internal void Delta(double x, double y) => Update(new(position.X + x, position.Y + y));
    internal Point Position => position;
    internal void Update(Point point)
    {
        position = point;
        if (!Current()) { Cancel(); return; }
        if (!moved && Math.Abs(point.X - start.X) < 4 && Math.Abs(point.Y - start.Y) < 4) return;
        if (!moved)
        {
            moved = true; scrolling.Start();
            _ = CapturePreview(generation);
        }
        var owner = list!; var local = viewport.TransformToVisual(owner).TransformPoint(point);
        destination = -1;
        if (InsideViewport(point) && local.X >= 0 && local.X <= owner.ActualWidth && local.Y >= 0 && local.Y <= owner.ActualHeight)
        {
            int slot = rows.Length;
            double center = local.Y - (start.Y - rowBounds[sourceIndex].Top) + rowBounds[sourceIndex].Height / 2;
            for (int index = 0; index < rows.Length; index++)
                if (center < rowBounds[index].Top + rowBounds[index].Height / 2) { slot = index; break; }
            destination = Math.Clamp(slot - (sourceIndex < slot ? 1 : 0), 0, rows.Length - 1);
        }
        for (int index = 0; index < rows.Length; index++)
            if (owner.ContainerFromIndex(index) is ListViewItem row)
                shifts.Apply(row, index, sourceIndex, destination < 0 ? sourceIndex : destination, rowBounds[sourceIndex].Height,
                    settings.AnimationsEnabled && !highContrast);
        if (preview is not null)
        {
            Canvas.SetLeft(preview, 0);
            Canvas.SetTop(preview, Math.Clamp(point.Y - start.Y + rowBounds[sourceIndex].Top, 0,
                Math.Max(0, viewport.ActualHeight - preview.Height)));
        }
    }
    private bool InsideViewport(Point point)
    {
        for (DependencyObject? parent = viewport; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll)
            {
                var local = viewport.TransformToVisual(scroll).TransformPoint(point);
                if (local.X < 0 || local.Y < 0 || local.X > scroll.ActualWidth || local.Y > scroll.ActualHeight) return false;
            }
        return true;
    }
    private async Task CapturePreview(int current)
    {
        var row = sourceContainer!;
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(row); }
        catch { if (current == generation) Cancel(); return; }
        if (current != generation || !Current()) return;
        preview = new Image { Source = bitmap, Width = row.ActualWidth, Height = row.ActualHeight, IsHitTestVisible = false };
        overlay.Children.Add(preview); row.Opacity = 0; Update(position);
    }
    internal SettingsReorderMove? Complete(Point point, bool cancelled)
    {
        if (!cancelled) Update(point);
        var result = !cancelled && moved && Current() && destination >= 0 && destination != sourceIndex
            ? new SettingsReorderMove(list!, rows[sourceIndex], rows[destination]) : null;
        Reset(); return result;
    }
    internal void Cancel() { var handle = grip; Reset(); if (handle?.IsDragging == true) handle.CancelDrag(); }
    private bool Current() => grip is not null && list is { IsEnabled: true } && DesktopVisual.IsVisible(list) &&
        DesktopTheme.GetIsHighContrast(list) == highContrast && list.Items.Count == rows.Length &&
        list.Items.Cast<object>().Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second));
    private void ScrollTick()
    {
        if (!Current()) { Cancel(); return; }
        if (scroller is null) return;
        var local = viewport.TransformToVisual(scroller).TransformPoint(position);
        double next = ScrollOffset(scroller.VerticalOffset, scroller.ScrollableHeight, scroller.ViewportHeight, local.Y);
        double delta = next - scroller.VerticalOffset;
        if (delta == 0) return;
        scroller.ChangeView(null, next, null, true); Update(new(position.X, position.Y + delta));
    }
    internal static double ScrollOffset(double current, double maximum, double height, double y) =>
        y < 28 ? Math.Max(0, current - 12) : y > height - 28 ? Math.Min(maximum, current + 12) : current;
    internal static object? KeyboardTarget(ListView list, object source, int direction)
    {
        int from = list.Items.IndexOf(source), to = from + direction;
        return list.IsEnabled && from >= 0 && direction is -1 or 1 && to >= 0 && to < list.Items.Count ? list.Items[to] : null;
    }
    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Cancel();
    private void Reset()
    {
        generation++; scrolling.Stop();
        if (collection is not null) collection.CollectionChanged -= RowsChanged;
        if (sourceContainer is not null) sourceContainer.Opacity = sourceOpacity;
        shifts.Clear(); overlay.Children.Clear();
        grip = null; list = null; sourceContainer = null; collection = null; rows = []; rowBounds = [];
        scroller = null; moved = false; destination = -1; preview = null;
    }
}
