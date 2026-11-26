using MeowFilm.Desktop;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

// One compositor-owned rotation animation per visible disc; never occupies UI frames or samples media.
public partial class MusicRecordView : UserControl
{
    private const double SpinPeriodSeconds = 24.0;
    [ThreadStatic] private static SolidColorBrush? grooveStrokeBrush;
    private static SolidColorBrush GrooveStrokeBrush => grooveStrokeBrush ??= DesktopVisual.Brush("#3C444547");

    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(nameof(Address),
        typeof(string), typeof(MusicRecordView), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(nameof(Playing),
        typeof(bool), typeof(MusicRecordView), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty TrackIdentityProperty = DependencyProperty.Register(nameof(TrackIdentity),
        typeof(string), typeof(MusicRecordView), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ArtworkIdentityProperty = DependencyProperty.Register(nameof(ArtworkIdentity),
        typeof(string), typeof(MusicRecordView), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty SwipeEnabledProperty = DependencyProperty.Register(nameof(SwipeEnabled),
        typeof(bool), typeof(MusicRecordView), new PropertyMetadata(false, SwipeStateChanged));
    public static readonly DependencyProperty SwipeIdentityProperty = DependencyProperty.Register(nameof(SwipeIdentity),
        typeof(string), typeof(MusicRecordView), new PropertyMetadata("", SwipeStateChanged));

    public string Address { get => (string)GetValue(AddressProperty); set => SetValue(AddressProperty, value); }
    public bool Playing { get => (bool)GetValue(PlayingProperty); set => SetValue(PlayingProperty, value); }
    public string TrackIdentity { get => (string)GetValue(TrackIdentityProperty); set => SetValue(TrackIdentityProperty, value); }
    public string ArtworkIdentity { get => (string)GetValue(ArtworkIdentityProperty); set => SetValue(ArtworkIdentityProperty, value); }
    public bool SwipeEnabled { get => (bool)GetValue(SwipeEnabledProperty); set => SetValue(SwipeEnabledProperty, value); }
    public string SwipeIdentity { get => (string)GetValue(SwipeIdentityProperty); set => SetValue(SwipeIdentityProperty, value); }

    public static readonly DependencyProperty PaletteBrushProperty = DependencyProperty.Register(nameof(PaletteBrush),
        typeof(Brush), typeof(MusicRecordView), new PropertyMetadata(null, PaletteChanged));
    public Brush PaletteBrush => (Brush)GetValue(PaletteBrushProperty);
    public event EventHandler? PaletteUpdated;
    private static void PaletteChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
        => ((MusicRecordView)value).PaletteUpdated?.Invoke(value, EventArgs.Empty);

    private Visual? discVisual;
    private ScalarKeyFrameAnimation? spinAnimation;
    private LinearEasingFunction? spinEasing;
    private float retainedAngle;
    private bool spinning;
    private long spinStartedAt;
    private Storyboard? swipeMotion;
    private Point pointerOrigin;
    private double gestureOrigin;
    private bool pointerPending;
    private string? gestureIdentity;
    private string? pendingIdentity;
    private long swipeGeneration;
    private DesktopWindow? watchedWindow;
    private IDisposable? visibility;
    private double builtDiameter;
    private bool IsVisible => DesktopVisual.IsVisible(this);
    internal bool IsSwiping { get; private set; }
    public event EventHandler<MusicRecordSwipeEventArgs>? SwipeCommitted;

    public MusicRecordView()
    {
        InitializeComponent();
        SetValue(PaletteBrushProperty, Cover.PaletteBrush);
        Cover.RegisterPropertyChangedCallback(ArtworkImage.PaletteBrushProperty, (_, _) => SetValue(PaletteBrushProperty, Cover.PaletteBrush));
        IsTabStop = true;
        PointerPressed += OnPointerDown;
        PointerMoved += OnPointerMove;
        PointerReleased += OnPointerUp;
        PointerCaptureLost += (_, _) => { if (pointerPending) CancelSwipe(); };
        KeyDown += OnRecordKeyDown;
        DiscVisual.SizeChanged += (_, _) => UpdateDiscCenterPoint();
        Loaded += (_, _) =>
        {
            visibility?.Dispose();
            UpdateDiscGeometry();
            visibility = DesktopVisual.ObserveVisibility(this, visible => { if (!visible) CancelSwipe(immediate: true); Sync(); });
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            CancelSwipe(immediate: true); visibility?.Dispose(); visibility = null; Release(); DisposeComposition();
        };
        SizeChanged += (_, e) =>
        {
            UpdateDiscGeometry();
            if (e.PreviousSize.Width > 0 && e.NewSize.Width != e.PreviousSize.Width &&
                (pointerPending || IsSwiping || pendingIdentity is not null || Math.Abs(SwipeTranslation.X) > .5))
                CancelSwipe();
        };
    }

    private void WatchWindow(bool watch)
    {
        if (watch && watchedWindow is null && DesktopWindow.GetWindow(this) is { } w) { watchedWindow = w; w.Deactivated += WindowDeactivated; }
        else if (!watch && watchedWindow is { } old) { watchedWindow = null; old.Deactivated -= WindowDeactivated; }
    }

    private void WindowDeactivated(object? sender, EventArgs e) => CancelSwipe(immediate: true);
    internal Task<IDisposable> PrepareArtworkAsync(CancellationToken cancellationToken) =>
        Cover.PrepareForPresentationAsync(cancellationToken);

    private void UpdateDiscGeometry()
    {
        double diameter = Math.Min(ActualWidth, ActualHeight);
        if (diameter <= 0) return;
        RecordSurface.Width = RecordSurface.Height = diameter;
        double half = diameter * 0.5;
        RecordRoot.CornerRadius = new CornerRadius(half);
        CardClip.SetRadius(RecordRoot, half);
        double sheenInset = Math.Max(0.5, diameter * (2.0 / 300.0));
        DiscSheen.Margin = new Thickness(sheenInset);
        double coverDiameter = diameter * 0.64;
        double coverRadius = coverDiameter * 0.5;
        CoverFrame.Width = CoverFrame.Height = coverDiameter;
        CoverFrame.CornerRadius = new CornerRadius(coverRadius);
        CardClip.SetRadius(CoverFrame, coverRadius);
        UpdateDiscCenterPoint();
        if (Math.Abs(builtDiameter - diameter) < 0.5) return;
        builtDiameter = diameter;
        bool compact = diameter < 160;
        double scale = diameter / 300.0;
        double step = (compact ? 14.0 : 1.7) * scale;
        double thickness = Math.Max(0.5, (compact ? 4.0 : 0.65) * scale);
        Grooves.Children.Clear();
        for (double radius = 100.0 * scale; radius < 148.0 * scale; radius += step)
            Grooves.Children.Add(new Ellipse
            {
                Stroke = GrooveStrokeBrush,
                StrokeThickness = thickness,
                Width = radius * 2,
                Height = radius * 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
    }

    private Visual EnsureDiscVisual()
    {
        if (discVisual is null)
        {
            discVisual = ElementCompositionPreview.GetElementVisual(DiscVisual);
            UpdateDiscCenterPoint();
            discVisual.RotationAngleInDegrees = retainedAngle;
        }
        return discVisual;
    }

    private void UpdateDiscCenterPoint()
    {
        if (discVisual is null) return;
        float cx = (float)(DiscVisual.ActualWidth > 0 ? DiscVisual.ActualWidth * 0.5 : 150.0);
        float cy = (float)(DiscVisual.ActualHeight > 0 ? DiscVisual.ActualHeight * 0.5 : 150.0);
        discVisual.CenterPoint = new Vector3(cx, cy, 0f);
    }

    private float CurrentAngle()
    {
        if (!spinning) return retainedAngle;
        double elapsed = Stopwatch.GetElapsedTime(spinStartedAt).TotalSeconds;
        return (float)((retainedAngle + elapsed / SpinPeriodSeconds * 360.0) % 360.0);
    }

    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var record = (MusicRecordView)value;
        if (record.Cover is null) return;
        if (args.Property == AddressProperty)
            record.Cover.Address = record.Address;
        if (args.Property == ArtworkIdentityProperty)
            record.Cover.DisplayIdentity = record.ArtworkIdentity;
        if (args.Property == TrackIdentityProperty)
        {
            // A committed swipe has already taken the old disc out of view.
            // Artwork updates and media retries do not end a track's posture.
            record.ResetAngle();
        }
        record.Sync();
    }

    private static void SwipeStateChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        var record = (MusicRecordView)value;
        if (e.Property == SwipeIdentityProperty || !record.SwipeEnabled)
            record.CancelSwipe(immediate: true);
    }

    private void Sync()
    {
        if (!IsLoaded || !IsVisible || !DesktopVisual.CanAnimate(this)) { Release(); return; }
        if (Playing) StartSpin();
        else Release();
    }

    private void StartSpin()
    {
        if (spinning) return;
        var visual = EnsureDiscVisual();
        UpdateDiscCenterPoint();
        spinEasing ??= visual.Compositor.CreateLinearEasingFunction();
        spinAnimation?.Dispose();
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, retainedAngle);
        anim.InsertKeyFrame(1f, retainedAngle + 360f, spinEasing);
        anim.Duration = TimeSpan.FromSeconds(SpinPeriodSeconds);
        anim.IterationBehavior = AnimationIterationBehavior.Forever;
        spinAnimation = anim;
        visual.StartAnimation("RotationAngleInDegrees", anim);
        spinStartedAt = Stopwatch.GetTimestamp();
        spinning = true;
    }

    private void Release()
    {
        retainedAngle = CurrentAngle();
        spinning = false;
        if (discVisual is not null)
        {
            discVisual.StopAnimation("RotationAngleInDegrees");
            discVisual.RotationAngleInDegrees = retainedAngle;
        }
        spinAnimation?.Dispose();
        spinAnimation = null;
    }

    private void ResetAngle()
    {
        spinning = false;
        retainedAngle = 0f;
        if (discVisual is not null)
        {
            discVisual.StopAnimation("RotationAngleInDegrees");
            discVisual.RotationAngleInDegrees = 0f;
        }
        spinAnimation?.Dispose();
        spinAnimation = null;
    }

    private void DisposeComposition()
    {
        spinAnimation?.Dispose();
        spinAnimation = null;
        spinEasing?.Dispose();
        spinEasing = null;
        discVisual = null;
    }

    private void OnPointerDown(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSwipe() || !e.GetCurrentPoint(this).IsInContact) return;
        Focus(FocusState.Pointer);
        pointerOrigin = e.GetCurrentPoint(this).Position;
        gestureOrigin = InterruptSwipeMotion(); pointerPending = true; gestureIdentity = SwipeIdentity;
        WatchWindow(true);
    }

    private void OnPointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (!pointerPending) return;
        var delta = e.GetCurrentPoint(this).Position.X - pointerOrigin.X;
        if (!IsSwiping && Math.Abs(delta) < 4) return;
        if (!IsSwiping && !CapturePointer(e.Pointer)) { pointerPending = false; WatchWindow(false); return; }
        IsSwiping = true; e.Handled = true; UpdateSwipeOffset(gestureOrigin + delta);
    }

    private void OnPointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (!pointerPending) return;
        pointerPending = false;
        if (!IsSwiping) { WatchWindow(false); gestureIdentity = null; AnimateSwipe(0, null); return; }
        e.Handled = true; ReleasePointerCapture(e.Pointer); EndSwipe(commit: true);
    }

    private void OnRecordKeyDown(object sender, KeyRoutedEventArgs e)
    { if (Navigate(e.Key)) e.Handled = true; }

    internal bool Navigate(VirtualKey key)
    {
        if (key is VirtualKey.Left or VirtualKey.Right && CanSwipe())
        {
            BeginSwipe(ActualWidth / 2);
            UpdateSwipe(key == VirtualKey.Right ? 0 : ActualWidth); EndSwipe(commit: true);
            return true;
        }
        if (key == VirtualKey.Escape && (IsSwiping || pendingIdentity is not null))
        { CancelSwipe(); return true; }
        return false;
    }

    internal void BeginSwipe(double pointerX)
    {
        if (!CanSwipe()) return;
        pointerOrigin = new(pointerX, 0);
        gestureOrigin = InterruptSwipeMotion();
        pointerPending = false;
        IsSwiping = true;
        gestureIdentity = SwipeIdentity;
        WatchWindow(true);
    }

    internal void UpdateSwipe(double pointerX)
    {
        if (IsSwiping) UpdateSwipeOffset(gestureOrigin + pointerX - pointerOrigin.X);
    }

    internal void EndSwipe(bool commit)
    {
        if (!IsSwiping) return;
        IsSwiping = pointerPending = false;
        WatchWindow(false);
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
        WatchWindow(false);
        gestureIdentity = pendingIdentity = null;
        ReleasePointerCaptures();
        if (immediate)
        {
            swipeGeneration++;
            swipeMotion?.Stop(); swipeMotion = null;
            SwipeTranslation.X = 0;
        }
        else AnimateSwipe(0, null);
    }

    private bool CanSwipe() => IsLoaded && IsVisible && SwipeEnabled && pendingIdentity is null &&
        !string.IsNullOrEmpty(SwipeIdentity);

    private void UpdateSwipeOffset(double value)
    {
        var limit = Math.Max(1, ActualWidth);
        SwipeTranslation.X = Math.Clamp(value, -limit, limit);
    }

    private double InterruptSwipeMotion()
    {
        swipeGeneration++;
        var offset = SwipeTranslation.X;
        swipeMotion?.Stop(); swipeMotion = null;
        SwipeTranslation.X = offset;
        return offset;
    }

    private void AnimateSwipe(double target, Action? completed)
    {
        var from = InterruptSwipeMotion();
        var generation = swipeGeneration;
        if (!DesktopVisual.CanAnimate(this) || Math.Abs(target - from) < .5)
        {
            SwipeTranslation.X = target;
            completed?.Invoke();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(260 * Math.Min(1, Math.Abs(target - from) / Math.Max(1, ActualWidth)));
        swipeMotion = DesktopVisual.AnimateDouble(SwipeTranslation, "X", from, target, duration, () =>
        {
            if (generation != swipeGeneration) return;
            swipeMotion?.Stop(); swipeMotion = null;
            SwipeTranslation.X = target; completed?.Invoke();
        });
    }
}

public sealed class MusicRecordSwipeEventArgs(int direction, string identity) : EventArgs
{
    public int Direction { get; } = direction;
    public string Identity { get; } = identity;
}
