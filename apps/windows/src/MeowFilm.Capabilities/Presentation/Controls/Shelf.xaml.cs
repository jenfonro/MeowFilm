using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

// Presentation-only rail. Paging never changes platform, selection, or playback.
public partial class Shelf : UserControl
{
    public static readonly DependencyProperty CardTemplateProperty = DependencyProperty.Register(nameof(CardTemplate), typeof(DataTemplate), typeof(Shelf));
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(Shelf),
        new PropertyMetadata(null, (value, _) => ((Shelf)value).OnItemsSourceChanged()));
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(Shelf), new PropertyMetadata(168d));
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(nameof(MinimumCardWidth), typeof(double), typeof(Shelf),
        new PropertyMetadata(150d, (value, _) => ((Shelf)value).InvalidateCardLayout()));
    public static readonly DependencyProperty NavigationButtonStyleProperty = DependencyProperty.Register(
        nameof(NavigationButtonStyle), typeof(Style), typeof(Shelf));
    public static readonly DependencyProperty HorizontalOffsetProperty = DependencyProperty.Register(
        nameof(HorizontalOffset), typeof(double), typeof(Shelf),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (value, _) =>
            {
                var shelf = (Shelf)value;
                if (shelf.committingOffset) return;
                shelf.CancelPointerInput();
                shelf.StopMotion();
                shelf.RequestRestore();
            }));
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(Shelf),
        new PropertyMetadata(0d, (value, args) => ((Shelf)value).ApplyAnimatedOffset((double)args.NewValue)));
    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public DataTemplate CardTemplate { get => (DataTemplate)GetValue(CardTemplateProperty); set => SetValue(CardTemplateProperty, value); }
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public Style? NavigationButtonStyle { get => (Style?)GetValue(NavigationButtonStyleProperty); set => SetValue(NavigationButtonStyleProperty, value); }
    public double HorizontalOffset { get => (double)GetValue(HorizontalOffsetProperty); set => SetValue(HorizontalOffsetProperty, value); }
    private readonly DispatcherTimer settle;
    private ScrollViewer? rail;
    private ShelfVirtualizingPanel? scrollPanel;
    private MusicCatalogSection? offsetSection;
    private Window? ownerWindow;
    private Point pointerOrigin;
    private double dragOrigin;
    private double? settlingTarget;
    private int visibleCardCount = 1;
    private bool presentationActive;
    private bool pointerPending, dragging, animating, restorePending, commitLayoutOffset;
    private bool usesDataContextItems = true, settingDataContextItems, committingOffset;
    private long motionGeneration;
    internal ScrollViewer? ScrollHost => rail;
    internal double MaximumOffset => scrollPanel?.MaximumOffset ?? LogicalMaxOffset();
    internal double PresentedOffset => scrollPanel?.HorizontalOffset ?? rail?.HorizontalOffset ?? 0;
    internal bool IsSettled => !animating;

    public Shelf()
    {
        InitializeComponent();
        settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(130) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (!presentationActive) return;
            if (AreAnyTouchesCapturedWithin)
            {
                settle.Start();
                return;
            }
            if (!restorePending && !dragging && !animating) SnapToNearest();
        };
        Loaded += (_, _) => UpdatePresentation();
        Unloaded += (_, _) => StopPresentation();
        IsVisibleChanged += (_, _) => UpdatePresentation();
        DataContextChanged += (_, e) =>
        {
            CommitSettledOffset(e.OldValue as MusicCatalogSection);
            offsetSection = e.NewValue as MusicCatalogSection;
            if (offsetSection is null && !BindingOperations.IsDataBound(this, HorizontalOffsetProperty))
                StoreOffset(0);
            if (usesDataContextItems && !BindingOperations.IsDataBound(this, ItemsSourceProperty))
            {
                settingDataContextItems = true;
                try { SetCurrentValue(ItemsSourceProperty, (e.NewValue as MusicCatalogSection)?.Items); }
                finally { settingDataContextItems = false; }
            }
            else usesDataContextItems = false;
            CancelPointerInput();
            StopMotion();
            RequestRestore();
        };
    }
    private void UpdatePresentation()
    {
        if (!IsLoaded || !IsVisible) { StopPresentation(); return; }
        if (presentationActive) return;
        presentationActive = true;
        SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
        AttachScrollHost();
    }
    private void StopPresentation()
    {
        if (!presentationActive) return;
        presentationActive = false;
        SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
        DetachScrollHost();
    }
    private void OnItemsSourceChanged()
    {
        if (settingDataContextItems) return;
        usesDataContextItems = false;
        // A bound ItemsSource can reevaluate before DataContextChanged. That event
        // owns committing the previous section before accepting the new restore.
        if (!ReferenceEquals(offsetSection, DataContext as MusicCatalogSection)) return;
        CancelPointerInput();
        StopMotion();
        RequestRestore();
    }
    private void InvalidateCardLayout()
    {
        scrollPanel?.InvalidateMeasure();
        InvalidateMeasure();
    }
    internal (double Target, double Gap) LayoutMetrics() =>
        DataContext is MusicCatalogSection { IsBannerSection: true } ? (260, 20) :
        DataContext is MusicCatalogSection { IsEntrySection: true } ? (150, 16) :
        (MinimumCardWidth, 16);
    internal static (int Count, double Width) MeasureCards(double width, double target, double gap, bool banner = false)
    {
        var count = banner ? 2 : Math.Max(1, (int)Math.Floor((width + gap) / (target + gap)));
        return (count, Math.Max(1, (width + gap) / count));
    }

    // Called during the panel's measure, before any card is measured at the new pitch.
    internal double PrepareCardLayout(ShelfVirtualizingPanel panel, double viewport, int count, double pitch, double maximum)
    {
        scrollPanel = panel;
        var changed = visibleCardCount != count || CardWidth != pitch;
        var target = restorePending ? offsetSection?.HorizontalOffset ?? HorizontalOffset : panel.HorizontalOffset;
        var resizing = panel.ViewportWidth > 0 && panel.ViewportWidth != viewport;
        if (changed && (!restorePending || resizing))
        {
            if (!restorePending) target = settlingTarget ?? target;
            var previousCount = visibleCardCount;
            var previousPitch = CardWidth;
            if (restorePending)
            {
                // A new section can have a different card shape at the previous
                // viewport; its saved pixels must not use the outgoing pitch.
                var (minimum, gap) = LayoutMetrics();
                (previousCount, previousPitch) = MeasureCards(panel.ViewportWidth, minimum, gap,
                    banner: offsetSection is { IsBannerSection: true });
            }
            var previousMaximum = restorePending
                ? Math.Max(0, ItemsHost.Items.Count - previousCount) * previousPitch : panel.MaximumOffset;
            if (offsetSection is { IsBannerSection: true })
                target = NearestOffset(target, previousMaximum, previousPitch);
            var atEnd = previousMaximum > .5 && previousMaximum - target <= .5;
            target = atEnd ? maximum : Math.Round(target / previousPitch, MidpointRounding.AwayFromZero) * pitch;
        }
        if (changed)
        {
            CancelPointerInput();
            StopMotion();
        }
        target = Math.Clamp(target, 0, maximum);
        commitLayoutOffset |= changed || restorePending ||
            Math.Abs(target - panel.HorizontalOffset) > .01 && !animating;
        restorePending = false;
        visibleCardCount = count;
        CardWidth = pitch;
        return target;
    }
    internal void CompleteCardLayout(ShelfVirtualizingPanel panel)
    {
        if (!presentationActive || !ReferenceEquals(scrollPanel, panel)) return;
        if (commitLayoutOffset)
        {
            commitLayoutOffset = false;
            CommitOffset();
        }
        UpdateButtons();
    }
    internal void OnNativeScroll()
    {
        if (!restorePending && !dragging && !animating) ScheduleSnap();
    }
    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) &&
            e.PropertyName != nameof(SystemParameters.HighContrast))
            return;
        if (animating) StopMotion();
        if (!restorePending && !dragging) SnapToNearest();
        PageIndicator.InvalidateVisual();
    }
    private double LogicalMaxOffset(MusicCatalogSection? section = null)
    {
        var itemCount = section is { IsSongShelfSection: true }
            ? (section.Items.Count + 2) / 3
            : section?.Items.Count ?? ItemsHost.Items.Count;
        return Math.Max(0, itemCount - visibleCardCount) * CardWidth;
    }
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!presentationActive) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (scrollPanel is null) return;
            restorePending = false;
            StopMotion();
            scrollPanel.SetHorizontalOffset(PresentedOffset - e.Delta);
            e.Handled = true;
            return;
        }
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer outer)
            {
                // Re-route once: native ScrollViewer owns wheel line/page settings.
                outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    { RoutedEvent = Mouse.MouseWheelEvent, Source = outer });
                e.Handled = true;
                return;
            }
    }
    private void Previous_Click(object sender, RoutedEventArgs e) => Page(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Page(1);
    private void AttachScrollHost()
    {
        ownerWindow = Window.GetWindow(this);
        if (ownerWindow is not null) ownerWindow.Deactivated += OwnerDeactivated;
        ItemsHost.ApplyTemplate();
        if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        rail = ItemsHost.Template.FindName("PART_ScrollViewer", ItemsHost) as ScrollViewer;
        if (rail is not null) rail.ScrollChanged += OnScrollChanged;
        scrollPanel = VisualDescendant<ShelfVirtualizingPanel>(ItemsHost);
        RequestRestore();
    }
    private void DetachScrollHost()
    {
        settle.Stop();
        if (DataContext is MusicCatalogSection section) CommitSettledOffset(section);
        else if (!restorePending && !pointerPending && !dragging && !AreAnyTouchesCapturedWithin)
            StoreOffset(Math.Clamp(settlingTarget ?? NearestOffset(PresentedOffset, MaximumOffset, CardWidth),
                0, MaximumOffset));
        CancelPointerInput();
        StopMotion();
        restorePending = true;
        commitLayoutOffset = false;
        if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        rail = null;
        if (ownerWindow is not null) ownerWindow.Deactivated -= OwnerDeactivated;
        ownerWindow = null;
    }
    private void RequestRestore()
    {
        restorePending = true;
        InvalidateCardLayout();
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateButtons();
    private void ScheduleSnap()
    {
        if (!presentationActive || restorePending) return;
        settle.Stop();
        settle.Start();
    }
    private bool Page(int direction)
    {
        if (!presentationActive) return false;
        if (PageTarget(direction) is not { } target) return false;
        AnimateTo(target);
        return true;
    }
    private void SnapToNearest()
    {
        if (!presentationActive || scrollPanel is null || restorePending) return;
        if (AreAnyTouchesCapturedWithin)
        {
            ScheduleSnap();
            return;
        }
        AnimateTo(NearestOffset(PresentedOffset, MaximumOffset, CardWidth));
    }
    private void AnimateTo(double target)
    {
        if (scrollPanel is null) return;
        restorePending = false;
        target = Math.Clamp(target, 0, MaximumOffset);
        var from = StopMotion();
        var generation = motionGeneration;
        settlingTarget = target;
        UpdateButtons();
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(target - from) < .5)
        {
            scrollPanel.ScrollTo(target);
            CommitOffset();
            settlingTarget = null;
            UpdateButtons();
            return;
        }
        SetValue(AnimatedOffsetProperty, from);
        animating = true;
        var duration = TimeSpan.FromMilliseconds(260 * Math.Min(1, Math.Abs(target - from) / Math.Max(1, scrollPanel.ViewportWidth)));
        var animation = new DoubleAnimation(from, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (generation != motionGeneration || scrollPanel is null) return;
            animating = false;
            BeginAnimation(AnimatedOffsetProperty, null);
            scrollPanel.ScrollTo(target);
            CommitOffset();
            settlingTarget = null;
            UpdateButtons();
        };
        BeginAnimation(AnimatedOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private double StopMotion()
    {
        motionGeneration++;
        settle?.Stop();
        var offset = PresentedOffset;
        animating = false;
        BeginAnimation(AnimatedOffsetProperty, null);
        settlingTarget = null;
        return offset;
    }
    private void ApplyAnimatedOffset(double offset)
    {
        if (animating) scrollPanel?.ScrollTo(offset);
    }
    private static T? VisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (VisualDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void CommitOffset()
    {
        if (DataContext is MusicCatalogSection section) section.HorizontalOffset = PresentedOffset;
        else StoreOffset(PresentedOffset);
    }
    private void StoreOffset(double offset)
    {
        committingOffset = true;
        try { SetCurrentValue(HorizontalOffsetProperty, offset); }
        finally { committingOffset = false; }
    }
    internal void ScrollToIndex(int index)
    {
        CancelPointerInput();
        StopMotion();
        StoreOffset(Math.Max(0, index) * CardWidth);
        RequestRestore();
    }
    private void CommitSettledOffset(MusicCatalogSection? section)
    {
        if (scrollPanel is null || section is null || restorePending) return;
        if (pointerPending || dragging || IsMouseCaptured || AreAnyTouchesCapturedWithin) return;
        var maximum = LogicalMaxOffset(section);
        section.HorizontalOffset = Math.Clamp(
            settlingTarget ?? NearestOffset(PresentedOffset, maximum, CardWidth), 0, maximum);
    }
    private double NearestOffset(double offset, double maximum, double pitch)
    {
        // DataContext may already be the incoming section while the outgoing
        // section still owns the visible geometry and its settled offset.
        if (offsetSection is { IsBannerSection: true })
        {
            var page = 2 * pitch;
            var before = Math.Floor(offset / page) * page;
            var after = Math.Min(maximum, before + page);
            return Math.Clamp(offset - before < after - offset ? before : after, 0, maximum);
        }
        return Math.Clamp(Math.Round(offset / pitch, MidpointRounding.AwayFromZero) * pitch, 0, maximum);
    }
    private void UpdateButtons()
    {
        if (rail is null || Previous is null || Next is null) return;
        var previous = PageTarget(-1).HasValue;
        var next = PageTarget(1).HasValue;
        Previous.Visibility = previous ? Visibility.Visible : Visibility.Collapsed;
        Next.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
        Previous.IsEnabled = previous;
        Next.IsEnabled = next;
        UpdatePageIndicator();
    }
    private void UpdatePageIndicator()
    {
        var maximum = MaximumOffset;
        if (DataContext is not MusicCatalogSection { IsBannerSection: true } || maximum <= .5)
        {
            PageIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        double page = visibleCardCount * CardWidth;
        double offset = Math.Clamp(PresentedOffset, 0, maximum);
        // Count from items, not floating-point division of pixel extents. The
        // final interval may be shorter than a page when the item count is odd.
        int pages = (ItemsHost.Items.Count + visibleCardCount - 1) / visibleCardCount;
        double lastStart = (pages - 2) * page;
        PageIndicator.PageCount = pages;
        PageIndicator.Position = offset > lastStart
            ? pages - 2 + (offset - lastStart) / (maximum - lastStart) : offset / page;
        PageIndicator.Visibility = Visibility.Visible;
    }
    private double? PageTarget(int direction)
    {
        if (scrollPanel is null || restorePending || direction is not (-1 or 1)) return null;
        var maximum = MaximumOffset;
        var offset = Math.Clamp(settlingTarget ?? PresentedOffset, 0, maximum);
        var aligned = NearestOffset(offset, maximum, CardWidth);
        var page = visibleCardCount * CardWidth;
        var target = direction < 0 && maximum - offset <= .5
            ? Math.Max(0, Math.Floor(Math.Max(0, maximum - .5) / page) * page)
            : Math.Clamp(aligned + direction * page, 0, maximum);
        return Math.Abs(target - offset) > .5 ? target : null;
    }
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (rail is null || e.ChangedButton != MouseButton.Left) return;
        restorePending = false;
        pointerOrigin = e.GetPosition(this);
        dragOrigin = PresentedOffset;
        pointerPending = true;
    }
    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!pointerPending || scrollPanel is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this).X - pointerOrigin.X;
        if (!dragging && Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance) return;
        if (!dragging && !CaptureMouse()) { pointerPending = false; return; }
        if (!dragging) dragOrigin = StopMotion();
        dragging = true;
        scrollPanel.ScrollTo(dragOrigin - delta);
        e.Handled = true;
    }
    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!pointerPending) return;
        pointerPending = false;
        if (!dragging)
        {
            if (!animating && !settle.IsEnabled) SnapToNearest();
            return;
        }
        dragging = false;
        e.Handled = true;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SnapToNearest();
    }
    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        pointerPending = dragging = false;
        SnapToNearest();
    }
    private void OnPreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (!presentationActive) return;
        restorePending = false;
        StopMotion();
    }
    private void OnPreviewTouchUp(object sender, TouchEventArgs e) => ScheduleSnap();
    private void OnLostTouchCapture(object sender, TouchEventArgs e) => ScheduleSnap();
    private void OwnerDeactivated(object? sender, EventArgs e)
    {
        CancelPointerInput();
        if (!restorePending && !AreAnyTouchesCapturedWithin) SnapToNearest();
    }
    private void CancelPointerInput()
    {
        pointerPending = dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!presentationActive || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        var handled = false;
        switch (e.Key)
        {
            case Key.Left: handled = Page(-1); break;
            case Key.Right: handled = Page(1); break;
            case Key.PageUp: handled = Page(-1); break;
            case Key.PageDown: handled = Page(1); break;
            case Key.Home when (settlingTarget ?? PresentedOffset) > .5:
                AnimateTo(0); handled = true; break;
            case Key.End when scrollPanel is not null && MaximumOffset - (settlingTarget ?? PresentedOffset) > .5:
                AnimateTo(MaximumOffset); handled = true; break;
        }
        if (handled) e.Handled = true;
    }
}

public sealed class ColumnsConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is double width ? Math.Clamp((int)((width + 20) / 420), 1, 2) : 2;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

// Native clipping for the whole card, including the image and colored caption.
public static class CardClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(double),
        typeof(CardClip), new PropertyMetadata(0d, Changed));
    public static double GetRadius(DependencyObject value) => (double)value.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject value, double radius) => value.SetValue(RadiusProperty, radius);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not FrameworkElement element) return;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Clip(element);
    }
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Clip((FrameworkElement)sender);
    private static void Clip(FrameworkElement e) =>
        e.Clip = new RectangleGeometry(new Rect(0, 0, e.ActualWidth, e.ActualHeight), GetRadius(e), GetRadius(e));
}
public sealed class BannerHeightConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        values.Length == 2 && values[0] is double width && width > 0 && values[1] is double ratio && ratio > 0
            ? width / ratio : 160d; // An unloaded placeholder only; decoded dimensions own the final geometry.
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
