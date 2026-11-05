using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

// One native animation clock per visible disc; never starts or samples media.
public partial class MusicRecordView : UserControl
{
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(nameof(Address),
        typeof(string), typeof(MusicRecordView), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(nameof(Playing),
        typeof(bool), typeof(MusicRecordView), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SwipeEnabledProperty = DependencyProperty.Register(nameof(SwipeEnabled),
        typeof(bool), typeof(MusicRecordView), new PropertyMetadata(false, SwipeStateChanged));
    public static readonly DependencyProperty SwipeIdentityProperty = DependencyProperty.Register(nameof(SwipeIdentity),
        typeof(string), typeof(MusicRecordView), new PropertyMetadata("", SwipeStateChanged));
    public string Address { get => (string)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }
    public bool SwipeEnabled { get => (bool)GetValue(SwipeEnabledProperty); set => SetValue(SwipeEnabledProperty, value); }
    public string SwipeIdentity { get => (string)GetValue(SwipeIdentityProperty); set => SetValue(SwipeIdentityProperty, value); }
    public static readonly DependencyProperty PaletteBrushProperty = DependencyProperty.Register(nameof(PaletteBrush),
        typeof(Brush), typeof(MusicRecordView), new PropertyMetadata(Brushes.DimGray, PaletteChanged));
    public Brush PaletteBrush => (Brush)GetValue(PaletteBrushProperty);
    public event EventHandler? PaletteUpdated;
    private static void PaletteChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
        => ((MusicRecordView)value).PaletteUpdated?.Invoke(value, EventArgs.Empty);
    private AnimationClock? clock;
    private double retainedAngle;
    private Point pointerOrigin;
    private double gestureOrigin;
    private bool pointerPending;
    private string? gestureIdentity;
    private string? pendingIdentity;
    private long swipeGeneration;
    private Window? ownerWindow;
    internal bool IsSwiping { get; private set; }
    internal double SwipeOffset => SwipeTranslation.X;
    public event EventHandler<MusicRecordSwipeEventArgs>? SwipeCommitted;

    public MusicRecordView()
    {
        InitializeComponent();
        UpdateGrooves();
        SetBinding(PaletteBrushProperty, new Binding(nameof(ArtworkImage.PaletteBrush)) { Source = Cover, Mode = BindingMode.OneWay });
        Focusable = true;
        IsManipulationEnabled = true;
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += SystemChanged;
            ownerWindow = Window.GetWindow(this);
            if (ownerWindow is not null) ownerWindow.Deactivated += OwnerDeactivated;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            if (ownerWindow is not null) ownerWindow.Deactivated -= OwnerDeactivated;
            ownerWindow = null;
            CancelSwipe(immediate: true);
            Release();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CancelSwipe(immediate: true);
            Sync();
        };
        SizeChanged += (_, e) =>
        {
            UpdateGrooves();
            if (e.PreviousSize.Width > 0 && e.WidthChanged &&
                (pointerPending || IsSwiping || pendingIdentity is not null || Math.Abs(SwipeTranslation.X) > .5))
                CancelSwipe();
        };
    }
    internal Task<IDisposable> PrepareArtworkAsync(CancellationToken cancellationToken) =>
        Cover.PrepareForPresentationAsync(cancellationToken);

    private void UpdateGrooves() => Grooves.Fill = Math.Min(ActualWidth, ActualHeight) < 160
        ? MusicPlayerArtwork.CompactGrooves : MusicPlayerArtwork.Grooves;
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var record = (MusicRecordView)value;
        if (record.Cover is null) return;
        if (args.Property == AddressProperty)
        {
            record.Release(); record.retainedAngle = 0; record.Rotation.Angle = 0;
            record.Cover.Address = record.Address;
        }
        record.Sync();
    }
    private static void SwipeStateChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        var record = (MusicRecordView)value;
        if (e.Property == SwipeIdentityProperty || !record.SwipeEnabled)
            record.CancelSwipe(immediate: true);
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        Sync();
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) CancelSwipe(immediate: true);
    }
    private void Sync()
    {
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
        { Release(); return; }
        if (Playing)
        {
            if (clock is null)
            {
                clock = (AnimationClock)new DoubleAnimation(retainedAngle, retainedAngle + 360, TimeSpan.FromSeconds(24))
                { RepeatBehavior = RepeatBehavior.Forever }.CreateClock(true);
                Rotation.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
            }
            clock.Controller?.Resume();
        }
        else clock?.Controller?.Pause();
    }
    private void Release()
    {
        retainedAngle = Rotation.Angle % 360;
        clock?.Controller?.Remove(); clock = null;
        Rotation.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        Rotation.Angle = retainedAngle;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!CanSwipe()) return;
        Focus();
        pointerOrigin = e.GetPosition(this);
        gestureOrigin = InterruptSwipeMotion();
        pointerPending = true;
        gestureIdentity = SwipeIdentity;
    }
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!pointerPending || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this).X - pointerOrigin.X;
        if (!IsSwiping && Math.Abs(delta) < SystemParameters.MinimumHorizontalDragDistance) return;
        if (!IsSwiping && !CaptureMouse()) { pointerPending = false; return; }
        IsSwiping = true;
        e.Handled = true;
        UpdateSwipeOffset(gestureOrigin + delta);
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!pointerPending) return;
        pointerPending = false;
        if (!IsSwiping)
        {
            gestureIdentity = null;
            if (Math.Abs(SwipeTranslation.X) > .5) AnimateSwipe(0, null);
            return;
        }
        e.Handled = true;
        if (IsMouseCaptured) ReleaseMouseCapture();
        EndSwipe(commit: true);
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (IsSwiping && pointerPending)
        {
            pointerPending = false;
            EndSwipe(commit: false);
        }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.None && e.Key is (Key.Left or Key.Right) && CanSwipe())
        {
            e.Handled = true;
            BeginSwipe(ActualWidth / 2);
            UpdateSwipe(e.Key == Key.Right ? 0 : ActualWidth);
            EndSwipe(commit: true);
            return;
        }
        if (e.Key == Key.Escape && (IsSwiping || pendingIdentity is not null))
        {
            e.Handled = true;
            CancelSwipe();
            return;
        }
        base.OnPreviewKeyDown(e);
    }
    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        if (!CanSwipe()) return;
        e.ManipulationContainer = this;
        BeginSwipe(ActualWidth / 2);
        e.Handled = true;
    }
    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        if (!IsSwiping) return;
        UpdateSwipeOffset(SwipeTranslation.X + e.DeltaManipulation.Translation.X);
        e.Handled = true;
    }
    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        if (!IsSwiping) return;
        EndSwipe(commit: true);
        e.Handled = true;
    }

    internal void BeginSwipe(double pointerX)
    {
        if (!CanSwipe()) return;
        pointerOrigin = new(pointerX, 0);
        gestureOrigin = InterruptSwipeMotion();
        pointerPending = false;
        IsSwiping = true;
        gestureIdentity = SwipeIdentity;
    }
    internal void UpdateSwipe(double pointerX)
    {
        if (IsSwiping) UpdateSwipeOffset(gestureOrigin + pointerX - pointerOrigin.X);
    }
    internal void EndSwipe(bool commit)
    {
        if (!IsSwiping) return;
        IsSwiping = pointerPending = false;
        var identity = gestureIdentity;
        gestureIdentity = null;
        var threshold = Math.Max(48, ActualWidth * .18);
        if (!commit || identity is null || identity != SwipeIdentity || Math.Abs(SwipeTranslation.X) < threshold)
        {
            AnimateSwipe(0, null);
            return;
        }
        var direction = SwipeTranslation.X < 0 ? 1 : -1;
        pendingIdentity = identity;
        AnimateSwipe(direction > 0 ? -Math.Max(1, ActualWidth) : Math.Max(1, ActualWidth), () =>
        {
            if (pendingIdentity == identity && identity == SwipeIdentity)
                SwipeCommitted?.Invoke(this, new(direction, identity));
            else ResolveSwipe(identity);
        });
    }
    internal void ResolveSwipe(string identity)
    {
        if (pendingIdentity != identity) return;
        pendingIdentity = null;
        AnimateSwipe(0, null);
    }
    internal void CancelSwipe(bool immediate = false)
    {
        pointerPending = IsSwiping = false;
        gestureIdentity = pendingIdentity = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (immediate)
        {
            swipeGeneration++;
            SwipeTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            SwipeTranslation.X = 0;
        }
        else AnimateSwipe(0, null);
    }
    private bool CanSwipe() => IsLoaded && IsVisible && SwipeEnabled && pendingIdentity is null &&
        !string.IsNullOrEmpty(SwipeIdentity);
    private void OwnerDeactivated(object? sender, EventArgs e) => CancelSwipe();
    private void UpdateSwipeOffset(double value)
    {
        var limit = Math.Max(1, ActualWidth);
        SwipeTranslation.X = Math.Clamp(value, -limit, limit);
    }
    private double InterruptSwipeMotion()
    {
        swipeGeneration++;
        var offset = SwipeTranslation.X;
        SwipeTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SwipeTranslation.X = offset;
        return offset;
    }
    private void AnimateSwipe(double target, Action? completed)
    {
        var from = InterruptSwipeMotion();
        var generation = swipeGeneration;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast || Math.Abs(target - from) < .5)
        {
            SwipeTranslation.X = target;
            completed?.Invoke();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(260 * Math.Min(1, Math.Abs(target - from) / Math.Max(1, ActualWidth)));
        var animation = new DoubleAnimation(from, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (generation != swipeGeneration) return;
            SwipeTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            SwipeTranslation.X = target;
            completed?.Invoke();
        };
        SwipeTranslation.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}

public sealed class MusicRecordSwipeEventArgs(int direction, string identity) : EventArgs
{
    public int Direction { get; } = direction;
    public string Identity { get; } = identity;
}
