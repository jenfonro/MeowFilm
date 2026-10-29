using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

// Presentation-only rail. Paging never changes platform, selection, or playback.
public partial class Shelf : UserControl
{
    public static readonly DependencyProperty CardTemplateProperty = DependencyProperty.Register(nameof(CardTemplate), typeof(DataTemplate), typeof(Shelf));
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(Shelf), new PropertyMetadata(168d));
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(nameof(MinimumCardWidth), typeof(double), typeof(Shelf),
        new PropertyMetadata(150d, (value, _) => ((Shelf)value).UpdateCardWidth()));
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(Shelf),
        new PropertyMetadata(0d, (value, args) => ((Shelf)value).ApplyAnimatedOffset((double)args.NewValue)));
    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    public DataTemplate CardTemplate { get => (DataTemplate)GetValue(CardTemplateProperty); set => SetValue(CardTemplateProperty, value); }
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    private readonly DispatcherTimer settle;
    private ScrollViewer? rail;
    private Point pointerOrigin;
    private double dragOrigin;
    private double? settlingTarget;
    private bool pointerPending, dragging, animating, restoring;
    private long motionGeneration;
    private Window? ownerWindow;
    internal ScrollViewer? ScrollHost => rail;

    public Shelf()
    {
        InitializeComponent();
        settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(130) };
        settle.Tick += (_, _) => { settle.Stop(); if (!dragging && !animating) SnapToNearest(); };
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
            AttachScrollHost();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
            DetachScrollHost();
        };
        DataContextChanged += (_, e) =>
        {
            CommitSettledOffset(e.OldValue as MusicCatalogSection);
            CancelPointerInput();
            StopMotion();
            UpdateCardWidth();
            ScheduleRestore();
        };
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCardWidth();
        if (!IsLoaded) return;
        if (animating) StopMotion();
        if (!dragging) ScheduleSnap();
    }
    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) &&
            e.PropertyName != nameof(SystemParameters.HighContrast))
            return;
        if (animating) StopMotion();
        if (!dragging) SnapToNearest();
    }
    private void UpdateCardWidth()
    {
        double target = DataContext is MusicCatalogSection { IsBannerSection: true } ? 260 : DataContext is MusicCatalogSection { IsEntrySection: true } ? 150 : MinimumCardWidth;
        double gap = DataContext is MusicCatalogSection { IsBannerSection: true } ? 20 : 16;
        int count = Math.Max(1, (int)Math.Floor((ActualWidth + gap) / (target + gap)));
        CardWidth = Math.Max(1, (ActualWidth + gap) / count);
    }
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (rail is null) return;
            StopMotion();
            rail.ScrollToHorizontalOffset(rail.HorizontalOffset - e.Delta);
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
        CancelPointerInput();
        restoring = false;
        StopMotion();
        if (rail is not null) rail.ScrollChanged -= OnScrollChanged;
        rail = null;
        if (ownerWindow is not null) ownerWindow.Deactivated -= OwnerDeactivated;
        ownerWindow = null;
    }
    private void ScheduleRestore()
    {
        if (!IsLoaded) return;
        restoring = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || rail is null) { restoring = false; return; }
            var offset = DataContext is MusicCatalogSection section ? section.HorizontalOffset : 0;
            StopMotion();
            rail.ScrollToHorizontalOffset(Math.Clamp(offset, 0, rail.ScrollableWidth));
            SetValue(AnimatedOffsetProperty, rail.HorizontalOffset);
            restoring = false;
            UpdateButtons();
        }));
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateButtons();
        if (!restoring && !dragging && !animating && Math.Abs(e.HorizontalChange) > .01) ScheduleSnap();
    }
    private void ScheduleSnap()
    {
        settle.Stop();
        settle.Start();
    }
    private void Page(int direction)
    {
        if (rail is null || direction is not (-1 or 1)) return;
        var page = Math.Max(CardWidth, Math.Floor(rail.ViewportWidth / Math.Max(1, CardWidth)) * CardWidth);
        AnimateTo(rail.HorizontalOffset + direction * page);
    }
    private void SnapToNearest()
    {
        if (rail is null) return;
        AnimateTo(NearestOffset(rail.HorizontalOffset));
    }
    private void AnimateTo(double target)
    {
        if (rail is null) return;
        target = Math.Clamp(target, 0, rail.ScrollableWidth);
        var from = StopMotion();
        var generation = motionGeneration;
        settlingTarget = target;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(target - from) < .5)
        {
            rail.ScrollToHorizontalOffset(target);
            SetValue(AnimatedOffsetProperty, target);
            CommitOffset();
            settlingTarget = null;
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
            rail.ScrollToHorizontalOffset(target);
            animating = false;
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
        var offset = rail?.HorizontalOffset ?? 0;
        BeginAnimation(AnimatedOffsetProperty, null);
        SetValue(AnimatedOffsetProperty, offset);
        settlingTarget = null;
        animating = false;
        return offset;
    }
    private void ApplyAnimatedOffset(double offset)
    {
        if (rail is not null) rail.ScrollToHorizontalOffset(Math.Clamp(offset, 0, rail.ScrollableWidth));
    }
    private void CommitOffset()
    {
        CommitOffset(DataContext as MusicCatalogSection);
    }
    private void CommitOffset(MusicCatalogSection? section)
    {
        if (rail is not null && section is not null) section.HorizontalOffset = rail.HorizontalOffset;
    }
    private void CommitSettledOffset(MusicCatalogSection? section)
    {
        if (rail is null || section is null || pointerPending || dragging || IsMouseCaptured ||
            AreAnyTouchesCapturedWithin)
            return;
        section.HorizontalOffset = Math.Clamp(settlingTarget ?? NearestOffset(rail.HorizontalOffset), 0, rail.ScrollableWidth);
    }
    private double NearestOffset(double offset)
    {
        var pitch = Math.Max(1, CardWidth);
        return Math.Clamp(Math.Round(offset / pitch, MidpointRounding.AwayFromZero) * pitch, 0, rail?.ScrollableWidth ?? 0);
    }
    private void UpdateButtons()
    {
        if (rail is null || Previous is null || Next is null) return;
        Previous.Visibility = Next.Visibility = rail.ScrollableWidth > .5 ? Visibility.Visible : Visibility.Collapsed;
        Previous.IsEnabled = rail.HorizontalOffset > .5;
        Next.IsEnabled = rail.ScrollableWidth - rail.HorizontalOffset > .5;
    }
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (rail is null || e.ChangedButton != MouseButton.Left) return;
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
        rail.ScrollToHorizontalOffset(Math.Clamp(dragOrigin - delta, 0, rail.ScrollableWidth));
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
    private void OnPreviewTouchDown(object sender, TouchEventArgs e) => StopMotion();
    private void OwnerDeactivated(object? sender, EventArgs e)
    {
        CancelPointerInput();
        SnapToNearest();
    }
    private void CancelPointerInput()
    {
        pointerPending = dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        var handled = true;
        switch (e.Key)
        {
            case Key.Left: Page(-1); break;
            case Key.Right: Page(1); break;
            case Key.PageUp: Page(-1); break;
            case Key.PageDown: Page(1); break;
            case Key.Home: AnimateTo(0); break;
            case Key.End: if (rail is not null) AnimateTo(rail.ScrollableWidth); break;
            default: handled = false; break;
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
