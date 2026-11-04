using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        new PropertyMetadata(150d, (value, _) => ((Shelf)value).UpdateCardWidth()));
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(Shelf),
        new PropertyMetadata(0d, (value, args) => ((Shelf)value).ApplyAnimatedOffset((double)args.NewValue)));
    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public DataTemplate CardTemplate { get => (DataTemplate)GetValue(CardTemplateProperty); set => SetValue(CardTemplateProperty, value); }
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    private readonly DispatcherTimer settle;
    private ScrollViewer? rail;
    private Point pointerOrigin;
    private double dragOrigin;
    private double? settlingTarget, restoreOffset;
    private double? pendingCardWidth;
    private int? resizeAnchor;
    private int? pendingVisibleCardCount;
    private int visibleCardCount = 1;
    private bool resizeAtEnd, applyingRestore, applyingSeek;
    private bool presentationActive;
    private bool pointerPending, dragging, animating, restoring, seeking;
    private bool usesDataContextItems = true, settingDataContextItems;
    private int restoreAttempts, restoreStablePasses, seekAttempts;
    private long motionGeneration, restoreGeneration, seekGeneration;
    private DispatcherOperation? restoreOperation, seekOperation;
    private DispatcherTimer? restoreRetry, seekRetry;
    private Window? ownerWindow;
    private VirtualizingStackPanel? scrollPanel;
    private TranslateTransform? resizeCompensation;
    internal ScrollViewer? ScrollHost => rail;
    internal double MaximumOffset => LogicalMaxOffset();
    internal double PresentedOffset => (rail?.HorizontalOffset ?? 0) - (resizeCompensation?.X ?? 0);

    public Shelf()
    {
        InitializeComponent();
        settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(130) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (AreAnyTouchesCapturedWithin)
            {
                settle.Start();
                return;
            }
            if (!restoring && !dragging && !animating) SnapToNearest();
        };
        Loaded += (_, _) => UpdatePresentation();
        Unloaded += (_, _) => StopPresentation();
        IsVisibleChanged += (_, _) => UpdatePresentation();
        DataContextChanged += (_, e) =>
        {
            CommitSettledOffset(e.OldValue as MusicCatalogSection);
            if (usesDataContextItems && !BindingOperations.IsDataBound(this, ItemsSourceProperty))
            {
                settingDataContextItems = true;
                try { SetCurrentValue(ItemsSourceProperty, (e.NewValue as MusicCatalogSection)?.Items); }
                finally { settingDataContextItems = false; }
            }
            else usesDataContextItems = false;
            CancelRestore();
            CancelPointerInput();
            StopMotion();
            if (presentationActive) UpdateCardWidth();
            ScheduleRestore();
        };
    }
    private void UpdatePresentation()
    {
        if (!IsLoaded || !IsVisible) { StopPresentation(); return; }
        if (presentationActive) return;
        presentationActive = true;
        SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
        UpdateCardWidth();
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
        if (!IsLoaded || rail is null) return;
        CancelPointerInput();
        StopMotion();
        ScheduleRestore();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible) return;
        if (!IsLoaded || rail is null)
        {
            UpdateCardWidth();
            return;
        }
        var anchor = resizeAnchor ?? LogicalAnchor();
        var atEnd = resizeAnchor.HasValue ? resizeAtEnd : IsAtEnd();
        var (count, width) = CardLayout();
        if (visibleCardCount == count && Math.Abs(CardWidth - width) < .01)
        {
            UpdateButtons();
            return;
        }
        CancelPointerInput();
        StopMotion();
        CancelRestore();
        resizeAnchor = anchor;
        resizeAtEnd = atEnd;
        pendingVisibleCardCount = count;
        pendingCardWidth = width;
        BeginRestore();
    }
    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) &&
            e.PropertyName != nameof(SystemParameters.HighContrast))
            return;
        if (animating) StopMotion();
        if (!restoring && !dragging) SnapToNearest();
    }
    private bool UpdateCardWidth()
    {
        var (count, width) = CardLayout();
        var changed = visibleCardCount != count || Math.Abs(CardWidth - width) >= .01;
        visibleCardCount = count;
        if (!changed) return false;
        CardWidth = width;
        return true;
    }
    private (int Count, double Width) CardLayout()
    {
        var (target, gap) = LayoutMetrics();
        var count = Math.Max(1, (int)Math.Floor((ActualWidth + gap) / (target + gap)));
        return (count, Math.Max(1, (ActualWidth + gap) / count));
    }
    private (double Target, double Gap) LayoutMetrics() =>
        DataContext is MusicCatalogSection { IsBannerSection: true } ? (260, 20) :
        DataContext is MusicCatalogSection { IsEntrySection: true } ? (150, 16) :
        (MinimumCardWidth, 16);
    private int VisibleCardCount() => visibleCardCount;
    private double LogicalMaxOffset(MusicCatalogSection? section = null)
    {
        var itemCount = section is { IsSongShelfSection: true }
            ? (section.Items.Count + 2) / 3
            : section?.Items.Count ?? ItemsHost.Items.Count;
        var hidden = Math.Max(0, itemCount - VisibleCardCount());
        return hidden * Math.Max(1, CardWidth);
    }
    private double MaxOffset()
    {
        if (rail is null) return 0;
        return Math.Min(rail.ScrollableWidth, LogicalMaxOffset());
    }
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!presentationActive) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (rail is null) return;
            if (restoring) CancelRestore();
            StopMotion();
            rail.ScrollToHorizontalOffset(Math.Clamp(rail.HorizontalOffset - e.Delta, 0, MaxOffset()));
            ScheduleSnap();
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
        var next = ItemsHost.Template.FindName("PART_ScrollViewer", ItemsHost) as ScrollViewer;
        if (ReferenceEquals(next, rail)) { ScheduleRestore(); return; }
        if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        rail = next;
        if (rail is not null) rail.ScrollChanged += OnScrollChanged;
        ScheduleRestore();
    }
    private void DetachScrollHost()
    {
        settle.Stop();
        CommitSettledOffset(DataContext as MusicCatalogSection);
        CancelRestore();
        CancelPointerInput();
        StopMotion();
        if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        rail = null;
        scrollPanel = null;
        if (ownerWindow is not null) ownerWindow.Deactivated -= OwnerDeactivated;
        ownerWindow = null;
    }
    private void ScheduleRestore()
    {
        if (!presentationActive || rail is null) return;
        CancelRestore();
        restoreOffset = DataContext is MusicCatalogSection section ? section.HorizontalOffset : 0;
        BeginRestore();
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (seeking && !applyingSeek)
            ApplySeekPosition(realizeTarget: false);
        else if (restoring && !applyingRestore &&
            (Math.Abs(e.HorizontalChange) > .01 || Math.Abs(e.ExtentWidthChange) > .01 ||
                Math.Abs(e.ViewportWidthChange) > .01))
            ApplyRestorePosition(realizeTarget: false);
        if (!restoring && rail is not null && rail.HorizontalOffset > MaxOffset() + .5)
        {
            rail.ScrollToHorizontalOffset(MaxOffset());
            return;
        }
        UpdateButtons();
        if (!restoring && !dragging && !animating && Math.Abs(e.HorizontalChange) > .01) ScheduleSnap();
    }
    private void ScheduleSnap()
    {
        if (!presentationActive || restoring || seeking) return;
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
        if (rail is null || restoring || seeking) return;
        if (AreAnyTouchesCapturedWithin)
        {
            ScheduleSnap();
            return;
        }
        AnimateTo(NearestOffset(rail.HorizontalOffset));
    }
    private void AnimateTo(double target)
    {
        if (rail is null) return;
        target = Math.Clamp(target, 0, LogicalMaxOffset());
        var from = StopMotion();
        if (target > MaxOffset() + .5)
        {
            BeginSeek(target);
            return;
        }
        var generation = motionGeneration;
        settlingTarget = target;
        UpdateButtons();
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(target - from) < .5)
        {
            rail.ScrollToHorizontalOffset(target);
            SetValue(AnimatedOffsetProperty, target);
            SetPhysicalOffset(target, updateLayout: true);
            if (Math.Abs(rail.HorizontalOffset - target) > .5)
            {
                BeginSeek(target);
                return;
            }
            CommitOffset();
            settlingTarget = null;
            UpdateButtons();
            return;
        }
        animating = true;
        var duration = TimeSpan.FromMilliseconds(260 * Math.Min(1, Math.Abs(target - from) / Math.Max(1, rail.ViewportWidth)));
        var animation = new DoubleAnimation(from, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (generation != motionGeneration || rail is null) return;
            BeginAnimation(AnimatedOffsetProperty, null);
            SetValue(AnimatedOffsetProperty, target);
            SetPhysicalOffset(target, updateLayout: true);
            animating = false;
            if (Math.Abs(rail.HorizontalOffset - target) > .5)
            {
                BeginSeek(target);
                return;
            }
            CommitOffset();
            settlingTarget = null;
            UpdateButtons();
        };
        BeginAnimation(AnimatedOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private double StopMotion()
    {
        motionGeneration++;
        settle.Stop();
        CancelSeek();
        var offset = rail is null ? 0 : Math.Clamp(rail.HorizontalOffset, 0, MaxOffset());
        BeginAnimation(AnimatedOffsetProperty, null);
        SetValue(AnimatedOffsetProperty, offset);
        rail?.ScrollToHorizontalOffset(offset);
        settlingTarget = null;
        animating = false;
        return offset;
    }
    private void ApplyAnimatedOffset(double offset)
    {
        SetPhysicalOffset(Math.Clamp(offset, 0, MaxOffset()), updateLayout: false);
    }
    private void SetPhysicalOffset(double offset, bool updateLayout)
    {
        if (rail is null) return;
        rail.ScrollToHorizontalOffset(offset);
        if (ScrollPanel() is IScrollInfo scrollInfo) scrollInfo.SetHorizontalOffset(offset);
        if (updateLayout) ItemsHost.UpdateLayout();
    }
    private VirtualizingStackPanel? ScrollPanel()
    {
        if (scrollPanel is not null) return scrollPanel;
        return scrollPanel = VisualDescendant<VirtualizingStackPanel>(ItemsHost);
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
        CommitOffset(DataContext as MusicCatalogSection);
    }
    private void CommitOffset(MusicCatalogSection? section)
    {
        if (rail is not null && section is not null)
            section.HorizontalOffset = Math.Clamp(rail.HorizontalOffset, 0, MaxOffset());
    }
    private void CommitSettledOffset(MusicCatalogSection? section)
    {
        if (rail is null || section is null) return;
        if (restoring)
        {
            if (resizeAnchor is { } anchor)
                section.HorizontalOffset = resizeAtEnd
                    ? LogicalMaxOffset(section)
                    : Math.Clamp(anchor * CardWidth, 0, LogicalMaxOffset(section));
            return;
        }
        if (pointerPending || dragging || IsMouseCaptured ||
            AreAnyTouchesCapturedWithin)
            return;
        var maximum = LogicalMaxOffset(section);
        section.HorizontalOffset = Math.Clamp(
            settlingTarget ?? NearestOffset(rail.HorizontalOffset, maximum), 0, maximum);
    }
    private double NearestOffset(double offset) => NearestOffset(offset, MaxOffset());
    private double NearestOffset(double offset, double maximum)
    {
        var pitch = Math.Max(1, CardWidth);
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
    }
    private double? PageTarget(int direction)
    {
        if (rail is null || restoring || seeking || direction is not (-1 or 1)) return null;
        var maximum = LogicalMaxOffset();
        var offset = Math.Clamp(settlingTarget ?? rail.HorizontalOffset, 0, maximum);
        var aligned = NearestOffset(offset, maximum);
        var page = VisibleCardCount() * Math.Max(1, CardWidth);
        var target = direction < 0 && maximum - offset <= .5
            ? Math.Max(0, Math.Floor(Math.Max(0, maximum - .5) / page) * page)
            : Math.Clamp(aligned + direction * page, 0, maximum);
        return Math.Abs(target - offset) > .5 ? target : null;
    }
    private int LogicalAnchor()
    {
        var offset = settlingTarget ??
            (restoring && DataContext is MusicCatalogSection section ? section.HorizontalOffset : rail?.HorizontalOffset ?? 0);
        return Math.Max(0, (int)Math.Round(offset / Math.Max(1, CardWidth), MidpointRounding.AwayFromZero));
    }
    private bool IsAtEnd()
    {
        var maximum = LogicalMaxOffset(DataContext as MusicCatalogSection);
        if (rail is null || maximum <= .5) return false;
        var offset = settlingTarget ??
            (restoring && DataContext is MusicCatalogSection section ? section.HorizontalOffset : rail.HorizontalOffset);
        return maximum - Math.Clamp(offset, 0, maximum) <= .5;
    }
    private void BeginRestore()
    {
        if (!IsLoaded || rail is null) return;
        restoring = true;
        restoreAttempts = 0;
        restoreStablePasses = 0;
        ApplyRestorePosition(realizeTarget: false);
        QueueRestorePass(++restoreGeneration);
    }
    private void QueueRestorePass(long generation)
    {
        restoreOperation?.Abort();
        restoreOperation = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (generation != restoreGeneration || !restoring || rail is null || !IsLoaded) return;
            restoreOperation = null;
            CommitPendingCardLayout();
            ItemsHost.UpdateLayout();
            var stable = ApplyRestorePosition(realizeTarget: true);
            if (resizeAnchor is null && stable)
            {
                CompleteRestore();
                return;
            }
            ScheduleRestoreRetry(generation);
        }));
    }
    private void CommitPendingCardLayout()
    {
        if (pendingCardWidth is not { } width || pendingVisibleCardCount is not { } count) return;
        if (ItemsHost.Items.Count > 0)
        {
            if (resizeAtEnd) rail?.ScrollToRightEnd();
            else
            {
                var first = Math.Max(0, resizeAnchor ?? 0);
                var index = Math.Min(ItemsHost.Items.Count - 1, first + count - 1);
                if (ScrollPanel() is { } panel) panel.BringIndexIntoViewPublic(index);
                else ItemsHost.ScrollIntoView(ItemsHost.Items[index]);
            }
            ItemsHost.UpdateLayout();
        }
        visibleCardCount = count;
        CardWidth = width;
        UpdateResizeCompensation(RestoreTarget());
        pendingVisibleCardCount = null;
        pendingCardWidth = null;
    }
    private void ScheduleRestoreRetry(long generation)
    {
        restoreRetry?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        restoreRetry = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(restoreRetry, timer)) restoreRetry = null;
            if (generation != restoreGeneration || !restoring || rail is null || !IsLoaded) return;
            ItemsHost.UpdateLayout();
            var stable = ApplyRestorePosition(realizeTarget: true);
            restoreStablePasses = stable ? restoreStablePasses + 1 : 0;
            if (restoreStablePasses >= 2 || ++restoreAttempts >= 12)
            {
                CompleteRestore();
                return;
            }
            ScheduleRestoreRetry(generation);
        };
        timer.Start();
    }
    private double RestoreTarget()
    {
        var desired = resizeAnchor is { } anchor
            ? resizeAtEnd ? LogicalMaxOffset() : anchor * CardWidth
            : restoreOffset ?? 0;
        return Math.Clamp(desired, 0, LogicalMaxOffset());
    }
    private bool ApplyRestorePosition(bool realizeTarget)
    {
        if (!restoring || applyingRestore || rail is null) return false;
        applyingRestore = true;
        try
        {
            var desired = RestoreTarget();
            if (realizeTarget) EnsureOffsetAvailable(desired);
            var available = MaxOffset();
            var target = desired <= available + .5
                ? desired
                : Math.Clamp(Math.Floor(available / Math.Max(1, CardWidth)) * CardWidth, 0, available);
            SetValue(AnimatedOffsetProperty, target);
            SetPhysicalOffset(target, updateLayout: realizeTarget);
            UpdateResizeCompensation(desired);
            return Math.Abs(desired - target) <= .5 &&
                Math.Abs(rail.HorizontalOffset - target) <= .5;
        }
        finally { applyingRestore = false; }
    }
    private bool EnsureOffsetAvailable(double target)
    {
        if (rail is null || target <= MaxOffset() + .5) return true;
        if (ItemsHost.Items.Count == 0) return target <= .5;
        if (LogicalMaxOffset() - target <= .5)
        {
            rail.ScrollToRightEnd();
            ItemsHost.UpdateLayout();
            return target <= MaxOffset() + .5;
        }
        var first = Math.Max(0, (int)Math.Floor(target / Math.Max(1, CardWidth)));
        var index = Math.Min(ItemsHost.Items.Count - 1, first + VisibleCardCount() - 1);
        for (var attempt = 0; attempt < 3 && target > MaxOffset() + .5; attempt++)
        {
            if (ScrollPanel() is { } panel) panel.BringIndexIntoViewPublic(index);
            else ItemsHost.ScrollIntoView(ItemsHost.Items[index]);
            ItemsHost.UpdateLayout();
        }
        return target <= MaxOffset() + .5;
    }
    private void UpdateResizeCompensation(double target)
    {
        if (resizeAnchor is null || rail is null || ScrollPanel() is not { } panel)
        {
            ClearResizeCompensation();
            return;
        }
        var shift = rail.HorizontalOffset - target;
        resizeCompensation ??= new TranslateTransform();
        resizeCompensation.X = Math.Abs(shift) <= .5 ? 0 : shift;
        panel.RenderTransform = resizeCompensation;
    }
    private void ClearResizeCompensation()
    {
        if (scrollPanel is not null && ReferenceEquals(scrollPanel.RenderTransform, resizeCompensation))
            scrollPanel.RenderTransform = Transform.Identity;
        resizeCompensation = null;
    }
    private void BeginSeek(double target)
    {
        settlingTarget = target;
        seeking = true;
        seekAttempts = 0;
        UpdateButtons();
        ApplySeekPosition(realizeTarget: false);
        QueueSeekPass(++seekGeneration);
    }
    private void QueueSeekPass(long generation)
    {
        seekOperation?.Abort();
        seekOperation = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (generation != seekGeneration || !seeking || rail is null || !IsLoaded) return;
            seekOperation = null;
            ItemsHost.UpdateLayout();
            ApplySeekPosition(realizeTarget: true);
            ScheduleSeekRetry(generation);
        }));
    }
    private void ScheduleSeekRetry(long generation)
    {
        seekRetry?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        seekRetry = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(seekRetry, timer)) seekRetry = null;
            if (generation != seekGeneration || !seeking || rail is null || !IsLoaded) return;
            ItemsHost.UpdateLayout();
            if (!ApplySeekPosition(realizeTarget: true) && ++seekAttempts < 12)
            {
                ScheduleSeekRetry(generation);
                return;
            }
            seeking = false;
            seekAttempts = 0;
            CommitOffset();
            settlingTarget = null;
            UpdateButtons();
        };
        timer.Start();
    }
    private bool ApplySeekPosition(bool realizeTarget)
    {
        if (!seeking || applyingSeek || rail is null || settlingTarget is not { } desired) return false;
        applyingSeek = true;
        try
        {
            if (realizeTarget) EnsureOffsetAvailable(desired);
            var available = MaxOffset();
            var target = desired <= available + .5
                ? desired
                : Math.Clamp(Math.Floor(available / Math.Max(1, CardWidth)) * CardWidth, 0, available);
            SetValue(AnimatedOffsetProperty, target);
            SetPhysicalOffset(target, updateLayout: realizeTarget);
            return Math.Abs(desired - target) <= .5 &&
                Math.Abs(rail.HorizontalOffset - target) <= .5;
        }
        finally { applyingSeek = false; }
    }
    private void CancelSeek()
    {
        seekGeneration++;
        seekOperation?.Abort();
        seekOperation = null;
        seekRetry?.Stop();
        seekRetry = null;
        seekAttempts = 0;
        applyingSeek = false;
        seeking = false;
        settlingTarget = null;
    }
    private void CompleteRestore()
    {
        if (!restoring || rail is null) return;
        if (!ApplyRestorePosition(realizeTarget: true))
        {
            var available = MaxOffset();
            var pitch = Math.Max(1, CardWidth);
            var fallback = Math.Clamp(Math.Floor(Math.Clamp(rail.HorizontalOffset, 0, available) / pitch) * pitch,
                0, available);
            SetValue(AnimatedOffsetProperty, fallback);
            SetPhysicalOffset(fallback, updateLayout: true);
        }
        restoreOffset = null;
        resizeAnchor = null;
        resizeAtEnd = false;
        pendingVisibleCardCount = null;
        pendingCardWidth = null;
        restoreAttempts = 0;
        restoreStablePasses = 0;
        restoring = false;
        ClearResizeCompensation();
        CommitOffset();
        UpdateButtons();
    }
    private void CancelRestore()
    {
        restoreGeneration++;
        restoreOperation?.Abort();
        restoreOperation = null;
        restoreRetry?.Stop();
        restoreRetry = null;
        restoreOffset = null;
        resizeAnchor = null;
        resizeAtEnd = false;
        pendingVisibleCardCount = null;
        pendingCardWidth = null;
        applyingRestore = false;
        restoreAttempts = 0;
        restoreStablePasses = 0;
        restoring = false;
        ClearResizeCompensation();
    }
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (rail is null || e.ChangedButton != MouseButton.Left) return;
        if (restoring) CancelRestore();
        pointerOrigin = e.GetPosition(this);
        dragOrigin = rail.HorizontalOffset;
        pointerPending = true;
    }
    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!pointerPending || rail is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this).X - pointerOrigin.X;
        if (!dragging && Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance) return;
        if (!dragging && !CaptureMouse()) { pointerPending = false; return; }
        if (!dragging) dragOrigin = StopMotion();
        dragging = true;
        rail.ScrollToHorizontalOffset(Math.Clamp(dragOrigin - delta, 0, MaxOffset()));
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
        if (restoring) CancelRestore();
        StopMotion();
    }
    private void OnPreviewTouchUp(object sender, TouchEventArgs e) => ScheduleSnap();
    private void OnLostTouchCapture(object sender, TouchEventArgs e) => ScheduleSnap();
    private void OwnerDeactivated(object? sender, EventArgs e)
    {
        CancelPointerInput();
        if (!restoring && !AreAnyTouchesCapturedWithin) SnapToNearest();
    }
    private void CancelPointerInput()
    {
        pointerPending = dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        var handled = false;
        switch (e.Key)
        {
            case Key.Left: handled = Page(-1); break;
            case Key.Right: handled = Page(1); break;
            case Key.PageUp: handled = Page(-1); break;
            case Key.PageDown: handled = Page(1); break;
            case Key.Home when (settlingTarget ?? rail?.HorizontalOffset ?? 0) > .5:
                AnimateTo(0); handled = true; break;
            case Key.End when rail is not null &&
                LogicalMaxOffset() - (settlingTarget ?? rail.HorizontalOffset) > .5:
                AnimateTo(LogicalMaxOffset()); handled = true; break;
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
public sealed class BannerHeightConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is double width ? width * .56 : 180d;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
