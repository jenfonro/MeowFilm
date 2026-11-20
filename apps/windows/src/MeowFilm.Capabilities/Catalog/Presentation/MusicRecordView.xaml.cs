using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media.Animation;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Capabilities;

// One native animation clock per visible disc; never starts or samples media.
public partial class MusicRecordView : UserControl
{
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
    private Storyboard? clock;
    private Storyboard? swipeMotion;
    private double retainedAngle;
    private Point pointerOrigin;
    private double gestureOrigin;
    private bool pointerPending;
    private string? gestureIdentity;
    private string? pendingIdentity;
    private long swipeGeneration;
    private MeowFilm.Desktop.DesktopWindow? window;
    private readonly Grid compactGrooves = new(), detailGrooves = new();
    private bool IsVisible => MeowFilm.Desktop.DesktopVisual.IsVisible(this);
    internal bool IsSwiping { get; private set; }
    internal double SwipeOffset => SwipeTranslation.X;
    public event EventHandler<MusicRecordSwipeEventArgs>? SwipeCommitted;

    public MusicRecordView()
    {
        InitializeComponent();
        Grooves.Children.Add(new Ellipse { Fill = MusicVisuals.Brush("#161719"), Width = 300, Height = 300 });
        BuildGrooves(compactGrooves, compact: true); BuildGrooves(detailGrooves, compact: false);
        Grooves.Children.Add(compactGrooves); Grooves.Children.Add(detailGrooves);
        UpdateGrooves();
        SetBinding(PaletteBrushProperty, new Binding { Path = new PropertyPath(nameof(ArtworkImage.PaletteBrush)), Source = Cover, Mode = BindingMode.OneWay });
        IsTabStop = true;
        PointerPressed += OnPointerDown;
        PointerMoved += OnPointerMove;
        PointerReleased += OnPointerUp;
        PointerCaptureLost += (_, _) => { if (pointerPending) CancelSwipe(); };
        KeyDown += OnRecordKeyDown;
        Loaded += (_, _) =>
        {
            window = MeowFilm.Desktop.DesktopWindow.GetWindow(this);
            if (window is not null)
            { window.Deactivated += WindowDeactivated; window.VisibilityChanged += WindowVisibilityChanged; }
            Sync();
        };
        Unloaded += (_, _) =>
        {
            if (window is not null)
            { window.Deactivated -= WindowDeactivated; window.VisibilityChanged -= WindowVisibilityChanged; }
            window = null; CancelSwipe(immediate: true); Release();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (!IsVisible) CancelSwipe(immediate: true);
            Sync();
        });
        SizeChanged += (_, e) =>
        {
            UpdateGrooves();
            if (e.PreviousSize.Width > 0 && e.NewSize.Width != e.PreviousSize.Width &&
                (pointerPending || IsSwiping || pendingIdentity is not null || Math.Abs(SwipeTranslation.X) > .5))
                CancelSwipe();
        };
    }
    private void WindowDeactivated(object? sender, EventArgs e) => CancelSwipe(immediate: true);
    private void WindowVisibilityChanged(object? sender, EventArgs e)
    { if (!IsVisible) CancelSwipe(immediate: true); Sync(); }
    internal Task<IDisposable> PrepareArtworkAsync(CancellationToken cancellationToken) =>
        Cover.PrepareForPresentationAsync(cancellationToken);

    private void UpdateGrooves()
    {
        bool compact = Math.Min(ActualWidth, ActualHeight) < 160;
        compactGrooves.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        detailGrooves.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    private static void BuildGrooves(Grid host, bool compact)
    {
        for (double radius = 100; radius < 148; radius += compact ? 14 : 1.7)
            host.Children.Add(new Ellipse { Stroke = MusicVisuals.Brush("#3C444547"),
                StrokeThickness = compact ? 4 : .65, Width = radius * 2, Height = radius * 2,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
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
            record.Release(); record.retainedAngle = 0; record.DiscRotation.Angle = 0;
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
        if (!IsLoaded || !IsVisible || !MusicVisuals.CanAnimate(this)) { Release(); return; }
        if (Playing)
        {
            if (clock is null) clock = MusicVisuals.AnimateDouble(DiscRotation, "Angle", retainedAngle,
                retainedAngle + 360, TimeSpan.FromSeconds(24), forever: true);
            else clock.Resume();
        }
        else clock?.Pause();
    }
    private void Release()
    {
        retainedAngle = DiscRotation.Angle % 360;
        clock?.Stop(); clock = null; DiscRotation.Angle = retainedAngle;
    }
    private void OnPointerDown(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSwipe() || !e.GetCurrentPoint(this).IsInContact) return;
        Focus(FocusState.Pointer);
        pointerOrigin = e.GetCurrentPoint(this).Position;
        gestureOrigin = InterruptSwipeMotion(); pointerPending = true; gestureIdentity = SwipeIdentity;
    }
    private void OnPointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (!pointerPending) return;
        var delta = e.GetCurrentPoint(this).Position.X - pointerOrigin.X;
        if (!IsSwiping && Math.Abs(delta) < 4) return;
        if (!IsSwiping && !CapturePointer(e.Pointer)) { pointerPending = false; return; }
        IsSwiping = true; e.Handled = true; UpdateSwipeOffset(gestureOrigin + delta);
    }
    private void OnPointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (!pointerPending) return;
        pointerPending = false;
        if (!IsSwiping) { gestureIdentity = null; AnimateSwipe(0, null); return; }
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
        if (!MusicVisuals.CanAnimate(this) || Math.Abs(target - from) < .5)
        {
            SwipeTranslation.X = target;
            completed?.Invoke();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(260 * Math.Min(1, Math.Abs(target - from) / Math.Max(1, ActualWidth)));
        swipeMotion = MusicVisuals.AnimateDouble(SwipeTranslation, "X", from, target, duration, () =>
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
