using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.System;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// Native item panels recycle horizontal columns; the shelf owns only page offsets.
public partial class Shelf : UserControl
{
    public static readonly DependencyProperty CardTemplateProperty = DependencyProperty.Register(nameof(CardTemplate), typeof(DataTemplate), typeof(Shelf), new PropertyMetadata(null));
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(Shelf), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(Shelf), new PropertyMetadata(168d));
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(nameof(MinimumCardWidth), typeof(double), typeof(Shelf), new PropertyMetadata(150d, Changed));
    public static readonly DependencyProperty NavigationButtonStyleProperty = DependencyProperty.Register(nameof(NavigationButtonStyle), typeof(Style), typeof(Shelf), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty HorizontalOffsetProperty = DependencyProperty.Register(nameof(HorizontalOffset), typeof(double), typeof(Shelf), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty IsBannerProperty = DependencyProperty.Register(nameof(IsBanner), typeof(bool), typeof(Shelf), new PropertyMetadata(false, Changed));

    public DataTemplate? CardTemplate { get => (DataTemplate?)GetValue(CardTemplateProperty); set => SetValue(CardTemplateProperty, value); }
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public Style? NavigationButtonStyle { get => (Style?)GetValue(NavigationButtonStyleProperty); set => SetValue(NavigationButtonStyleProperty, value); }
    public double HorizontalOffset { get => (double)GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }
    public bool IsBanner { get => (bool)GetValue(IsBannerProperty); set => SetValue(IsBannerProperty, value); }
    public event EventHandler<double>? HorizontalOffsetCommitted;

    private int rowsPerColumn = 1;
    public int RowsPerColumn
    {
        get => rowsPerColumn;
        set
        {
            if (value is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(value));
            if (IsLoaded) throw new InvalidOperationException("Shelf rows must be selected before mounting.");
            if (rowsPerColumn == value) return;
            rowsPerColumn = value;
            if (value == 3)
            {
                ItemsHost.ItemsPanel = (ItemsPanelTemplate)Resources["ThreeRowPanel"];
                ItemsHost.Height = 252;
            }
            else
            {
                ItemsHost.ItemsPanel = (ItemsPanelTemplate)Resources["SingleRowPanel"];
                ItemsHost.ClearValue(HeightProperty);
            }
        }
    }

    private int ColumnCount => (ItemsHost.Items.Count + RowsPerColumn - 1) / RowsPerColumn;
    private ScrollViewer? rail;
    private int visibleCards = 1;
    private bool committing, restoring;
    private INotifyCollectionChanged? observedItems;
    private double? targetOffset;
    private DesktopWindow? deactivatedWindow;
    private IDisposable? visibility;
    internal ScrollViewer? ScrollHost => rail;

    internal void SetArtworkItemKeySelector(Func<object, object> selector) =>
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemKeySelector(ItemsHost, selector);
    internal void SetArtworkItemResourcesSelector(Func<object, IEnumerable<MeowFilm.Desktop.Artwork.ArtworkItemResource>> selector) =>
        MeowFilm.Desktop.Artwork.ArtworkItemsScope.SetItemResourcesSelector(ItemsHost, selector);

    internal double MaximumOffset => Math.Max(0, (ColumnCount - visibleCards) * CardWidth);
    internal double PresentedOffset => rail?.HorizontalOffset ?? 0;
    internal bool IsSettled { get; private set; } = true;

    private void SetSettled(bool settled)
    {
        IsSettled = settled;
        if (!settled && deactivatedWindow is null && DesktopWindow.GetWindow(this) is { } w) { deactivatedWindow = w; w.Deactivated += Deactivated; }
        else if (settled && deactivatedWindow is { } old) { deactivatedWindow = null; old.Deactivated -= Deactivated; }
    }

    public Shelf()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (rail is not null) rail.ViewChanged -= Scrolled;
            rail = DesktopVisual.Descendant<ScrollViewer>(ItemsHost);
            if (rail is not null) rail.ViewChanged += Scrolled;
            SetSettled(IsSettled);
            ObserveItemsSource();
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                if (visible) { UpdateLayoutMetrics(); Restore(); }
                else
                {
                    CommitOffset();
                    rail?.ChangeView(HorizontalOffset, null, null, true);
                    targetOffset = null; SetSettled(true); UpdateButtons();
                }
            });
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            CommitOffset();
            visibility?.Dispose(); visibility = null;
            UnsubscribeItemsSource();
            if (rail is not null) rail.ViewChanged -= Scrolled;
            SetSettled(true);
            rail = null; targetOffset = null;
        };
        SizeChanged += (_, e) => OnShelfSizeChanged(e);
        ItemsHost.ContainerContentChanging += (_, e) => SizeCard(e.ItemContainer);
        CardMotion.SetEnabled(this, true);
        RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => UpdateArrowOpacity());
        KeyDown += (_, e) =>
        {
            if (DesktopVisual.Modifiers != VirtualKeyModifiers.None) return;
            if (Navigate(e.Key)) e.Handled = true;
        };
    }

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var shelf = (Shelf)owner; if (shelf.ItemsHost is null || shelf.committing) return;
        if (e.Property == NavigationButtonStyleProperty) { shelf.Previous.Style = shelf.Next.Style = shelf.NavigationButtonStyle; return; }
        if (e.Property == ItemsSourceProperty)
        {
            if (shelf.IsLoaded) shelf.ObserveItemsSource();
            else shelf.UnsubscribeItemsSource();
        }
        shelf.UpdateLayoutMetrics(); shelf.Restore();
    }

    private void ObserveItemsSource()
    {
        UnsubscribeItemsSource();
        observedItems = ItemsSource as INotifyCollectionChanged;
        if (observedItems is not null) observedItems.CollectionChanged += ItemsChanged;
    }

    private void UnsubscribeItemsSource()
    {
        if (observedItems is not null) observedItems.CollectionChanged -= ItemsChanged;
        observedItems = null;
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateLayoutMetrics();

    internal (double Target, double Gap) LayoutMetrics() => IsBanner ? (260, 20) : (MinimumCardWidth, 16);

    internal static (int Count, double Width) MeasureCards(double width, double target, double gap, bool banner = false)
    { int count = banner ? 2 : Math.Max(1, (int)Math.Floor((width + gap) / (target + gap))); return (count, Math.Max(1, (width + gap) / count)); }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0) ApplyCardMetrics(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private bool ApplyCardMetrics(double hostWidth)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || hostWidth <= 0) return false;
        var (target, gap) = LayoutMetrics();
        var (count, pitch) = MeasureCards(hostWidth, target, gap, IsBanner);
        if (ItemsHost.ItemsPanelRoot is ItemsWrapGrid grid) grid.ItemWidth = pitch;
        if (count == visibleCards && Math.Abs(pitch - CardWidth) <= 0.01) return false;
        visibleCards = count; CardWidth = pitch;
        for (int i = 0; i < ItemsHost.Items.Count; i++) if (ItemsHost.ContainerFromIndex(i) is ListViewItem item) SizeCard(item);
        return true;
    }

    private void OnShelfSizeChanged(SizeChangedEventArgs e)
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || e.NewSize.Width <= 0) return;
        double hostWidth = ItemsHost.ActualWidth > 0 ? ItemsHost.ActualWidth : e.NewSize.Width;
        ApplyCardMetrics(hostWidth);
        if (e.PreviousSize.Width > 0 && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.01)
        {
            var (target, gap) = LayoutMetrics();
            var (prevCount, prevPitch) = MeasureCards(e.PreviousSize.Width, target, gap, IsBanner);
            double oldMaximum = Math.Max(0, (ColumnCount - prevCount) * prevPitch);
            double old = targetOffset ?? PresentedOffset;
            double next = oldMaximum > .5 && oldMaximum - old < .5 ? MaximumOffset : Math.Round(old / prevPitch) * CardWidth;
            targetOffset = null;
            rail?.ChangeView(Math.Clamp(next, 0, MaximumOffset), null, null, true);
        }
        UpdateButtons();
    }

    private void UpdateLayoutMetrics()
    {
        if (!IsLoaded || !DesktopVisual.IsVisible(this) || ItemsHost.ActualWidth <= 0) return;
        double previous = CardWidth, oldMaximum = MaximumOffset;
        if (ApplyCardMetrics(ItemsHost.ActualWidth))
        {
            double old = targetOffset ?? PresentedOffset;
            double next = oldMaximum > .5 && oldMaximum - old < .5 ? MaximumOffset : Math.Round(old / previous) * CardWidth;
            targetOffset = null;
            rail?.ChangeView(Math.Clamp(next, 0, MaximumOffset), null, null, true);
        }
        UpdateButtons();
    }

    private void SizeCard(SelectorItem item)
    {
        item.Width = Math.Max(1, CardWidth); item.Padding = new Thickness(0, 0, LayoutMetrics().Gap, 0);
        if (RowsPerColumn == 3)
        {
            item.Height = 84;
            if (ItemsHost.ItemsPanelRoot is ItemsWrapGrid grid) grid.ItemWidth = CardWidth;
        }
    }

    private void Restore()
    {
        if (rail is null || !DesktopVisual.IsVisible(this)) return;
        restoring = true; rail.ChangeView(Math.Clamp(HorizontalOffset, 0, MaximumOffset), null, null, true);
        restoring = false; UpdateButtons();
    }

    private void Scrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        SetSettled(!e.IsIntermediate); UpdateButtons();
        if (e.IsIntermediate || restoring) return;
        if (targetOffset is { } target && Math.Abs(PresentedOffset - target) < .5) targetOffset = null;
        if (targetOffset is null) Snap();
    }

    private void Snap()
    {
        double target = NearestOffset(PresentedOffset, MaximumOffset, CardWidth);
        if (Math.Abs(target - PresentedOffset) < .5) { SetSettled(true); CommitOffset(); return; }
        Move(target);
    }

    private double NearestOffset(double offset, double maximum, double pitch)
    {
        if (IsBanner)
        {
            double before = Math.Floor(offset / (2 * pitch)) * (2 * pitch), after = Math.Min(maximum, before + 2 * pitch);
            return Math.Clamp(offset - before < after - offset ? before : after, 0, maximum);
        }
        return Math.Clamp(Math.Round(offset / Math.Max(1, pitch), MidpointRounding.AwayFromZero) * pitch, 0, maximum);
    }

    private void Move(double target)
    {
        if (rail is null) return;
        target = Math.Clamp(target, 0, MaximumOffset);
        if (Math.Abs(PresentedOffset - target) < .5) { targetOffset = null; SetSettled(true); CommitOffset(); return; }
        targetOffset = target; SetSettled(false);
        rail.ChangeView(target, null, null, !DesktopVisual.CanAnimate(this)); UpdateButtons();
    }

    private double? PageTarget(int direction)
    {
        double offset = Math.Clamp(targetOffset ?? PresentedOffset, 0, MaximumOffset), page = visibleCards * CardWidth;
        double target = direction < 0 && MaximumOffset - offset <= .5
            ? Math.Max(0, Math.Floor(Math.Max(0, MaximumOffset - .5) / page) * page)
            : Math.Clamp(NearestOffset(offset, MaximumOffset, CardWidth) + direction * page, 0, MaximumOffset);
        return Math.Abs(target - offset) > .5 ? target : null;
    }

    private bool Page(int direction)
    {
        if (PageTarget(direction) is not { } target) return false;
        Move(target); return true;
    }

    internal bool Navigate(VirtualKey key)
    {
        if (rail is null || !IsEnabled || !DesktopVisual.IsVisible(this)) return false;
        if (key is VirtualKey.Left or VirtualKey.Right or VirtualKey.PageUp or VirtualKey.PageDown)
            return Page(key is VirtualKey.Right or VirtualKey.PageDown ? 1 : -1);
        double offset = targetOffset ?? PresentedOffset;
        if (key == VirtualKey.Home && offset > .5) { Move(0); return true; }
        if (key == VirtualKey.End && MaximumOffset - offset > .5) { Move(MaximumOffset); return true; }
        return false;
    }

    private void Deactivated(object? sender, EventArgs e) => Snap();

    private void CommitOffset()
    {
        if (rail is null || restoring) return;
        double offset = Math.Clamp(targetOffset ?? NearestOffset(PresentedOffset, MaximumOffset, CardWidth), 0, MaximumOffset);
        committing = true; HorizontalOffset = offset; committing = false;
        HorizontalOffsetCommitted?.Invoke(this, offset);
    }

    private void UpdateButtons()
    {
        bool visible = IsEnabled && DesktopVisual.IsVisible(this);
        Previous.IsEnabled = visible && PageTarget(-1).HasValue; Next.IsEnabled = visible && PageTarget(1).HasValue;
        Previous.Visibility = PageTarget(-1).HasValue ? Visibility.Visible : Visibility.Collapsed;
        Next.Visibility = PageTarget(1).HasValue ? Visibility.Visible : Visibility.Collapsed;
        UpdateArrowOpacity();
        if (!IsBanner)
        {
            if (PageIndicator.Visibility != Visibility.Collapsed) PageIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        int pageCount = (int)Math.Ceiling(ColumnCount / (double)visibleCards);
        PageIndicator.PageCount = pageCount;
        double page = visibleCards * CardWidth, lastStart = (pageCount - 2) * page;
        PageIndicator.Position = pageCount > 1 && PresentedOffset > lastStart
            ? pageCount - 2 + (PresentedOffset - lastStart) / (MaximumOffset - lastStart)
            : PresentedOffset / Math.Max(1, page);
        PageIndicator.Visibility = pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateArrowOpacity()
    {
        bool active = CardMotion.GetProgress(this) > 0;
        Previous.Opacity = Next.Opacity = active ? 1 : 0;
        Previous.IsHitTestVisible = Next.IsHitTestVisible = active;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Page(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Page(1);
}
