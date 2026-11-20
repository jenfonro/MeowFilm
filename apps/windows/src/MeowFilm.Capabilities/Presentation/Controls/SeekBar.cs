using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.Foundation;
using Windows.UI;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// The gesture commits one seek. Animation advances only the visual rail and never Value.
public sealed class SeekBar : Slider
{
    private const double UpperEnvelope = 43;
    public static readonly DependencyProperty ChorusSecondsProperty = Property(nameof(ChorusSeconds), typeof(double), double.NaN);
    public static readonly DependencyProperty PlayedBrushProperty = Property(nameof(PlayedBrush), typeof(Brush), null);
    public static readonly DependencyProperty RailBrushProperty = Property(nameof(RailBrush), typeof(Brush), null);
    public static readonly DependencyProperty RailAtTopProperty = Property(nameof(RailAtTop), typeof(bool), false);
    public static readonly DependencyProperty CacheFractionProperty = Property(nameof(CacheFraction), typeof(double), 0d);
    public static readonly DependencyProperty IsPlayingProperty = Property(nameof(IsPlaying), typeof(bool), false);
    public static readonly DependencyProperty PlaybackRateProperty = Property(nameof(PlaybackRate), typeof(double?), null);
    public static readonly DependencyProperty PresentationKeyProperty = DependencyProperty.Register(nameof(PresentationKey), typeof(string), typeof(SeekBar),
        new PropertyMetadata("", (owner, _) => { ((SeekBar)owner).CancelScrub(); ((SeekBar)owner).Refresh(); }));
    private static DependencyProperty Property(string name, Type type, object? value) => DependencyProperty.Register(name, type, typeof(SeekBar),
        new PropertyMetadata(value, (owner, _) => ((SeekBar)owner).Refresh()));
    public double ChorusSeconds { get => (double)GetValue(ChorusSecondsProperty); set => SetValue(ChorusSecondsProperty, value); }
    public Brush? PlayedBrush { get => (Brush?)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush? RailBrush { get => (Brush?)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }
    public bool RailAtTop { get => (bool)GetValue(RailAtTopProperty); set => SetValue(RailAtTopProperty, value); }
    public double CacheFraction { get => (double)GetValue(CacheFractionProperty); set => SetValue(CacheFractionProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }
    private Canvas? surface;
    private readonly Rectangle rail = new(), cache = new(), played = new(), shade = new() { Opacity = 0 },
        lowerHit = new() { Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Ellipse marker = new() { Width = 4, Height = 4 }, head = new() { Width = 14, Height = 14, Opacity = 0 };
    private readonly ScaleTransform scale = new();
    private readonly ScaleTransform railScale = new(), cacheScale = new();
    private readonly TranslateTransform headPosition = new();
    private Storyboard? progressAnimation, headAnimation, hoverAnimation;
    private IDisposable? visibility;
    private readonly TextBlock previewText = new() { FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) };
    private readonly Border preview = new() { CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 5, 12, 5),
        Background = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private bool pointerGesture, pointerWithin;
    private bool hoverExpanded, hoverShade;
    private double pointerX;
    private long sampledAt;
    internal bool IsScrubbing { get; private set; }
    internal double DisplayValue => progressAnimation is not null && PlaybackRate is { } rate
        ? Math.Min(Maximum, Minimum + ((DoubleAnimation)progressAnimation.Children[0]).From!.Value *
            (Maximum - Minimum) + Stopwatch.GetElapsedTime(sampledAt).TotalSeconds * rate) : Value;
    internal double RailOrigin => RailAtTop ? UpperEnvelope : 0;
    internal bool PointerWithin => pointerWithin;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCommitted;
    public event EventHandler? ScrubCancelled;
    public SeekBar()
    {
        Template = (ControlTemplate)XamlReader.Load("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'><Canvas x:Name='RailCanvas'/></ControlTemplate>");
        MinHeight = 0; Height = 16; SmallChange = 5; LargeChange = 15; IsTabStop = true;
        played.RenderTransform = scale; rail.RenderTransform = railScale; cache.RenderTransform = cacheScale;
        foreach (var rectangle in new[] { rail, cache, played }) rectangle.RenderTransformOrigin = new Point(.5, .5);
        played.RenderTransformOrigin = new Point(0, .5);
        head.RenderTransform = headPosition;
        preview.Child = previewText; preview.RenderTransform = headPosition;
        Canvas.SetZIndex(preview, 1);
        head.Stroke = new SolidColorBrush(Color.FromArgb(51, 0, 0, 0)); head.StrokeThickness = 1;
        ValueChanged += (_, _) => Refresh(); SizeChanged += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                if (!visible) { pointerWithin = false; CancelScrub(); StopAnimation(); StopHover(); ClosePreview(); }
                Refresh(false);
            });
            Refresh();
        };
        Unloaded += (_, _) =>
        { visibility?.Dispose(); visibility = null; pointerWithin = false; CancelScrub(); StopAnimation(); StopHover(); ClosePreview(); };
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => { if (!IsEnabled) CancelScrub(); Refresh(); });
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        { if (Visibility != Visibility.Visible) { pointerWithin = false; CancelScrub(); ClosePreview(); } Refresh(); });
        RegisterPropertyChangedCallback(MaximumProperty, (_, _) => Refresh());
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Refresh(false));
        ActualThemeChanged += (_, _) => Refresh(false);
        PointerEntered += (_, _) => SetPointerWithin(true);
        PointerExited += (_, _) => SetPointerWithin(false);
        PointerPressed += (_, e) =>
        {
            if (!IsEnabled || !e.GetCurrentPoint(this).IsInContact || !CapturePointer(e.Pointer)) return;
            e.Handled = true; Focus(FocusState.Pointer); pointerGesture = true; BeginScrub();
            pointerX = e.GetCurrentPoint(this).Position.X;
            UpdateScrub(ValueAt(pointerX)); UpdatePreview();
        };
        PointerMoved += (_, e) =>
        {
            pointerX = e.GetCurrentPoint(this).Position.X;
            if (pointerGesture && IsScrubbing) UpdateScrub(ValueAt(pointerX));
            if (pointerWithin || IsScrubbing) UpdatePreview();
        };
        PointerReleased += (_, e) =>
        {
            if (!pointerGesture) return;
            e.Handled = true; UpdateScrub(ValueAt(e.GetCurrentPoint(this).Position.X)); EndScrub(true);
        };
        PointerCaptureLost += (_, _) => { if (pointerGesture) CancelScrub(); };
        KeyDown += OnSeekKeyDown; KeyUp += OnSeekKeyUp;
        GotFocus += (_, _) => Refresh(false);
        LostFocus += (_, _) => { CancelScrub(); Refresh(false); };
    }
    protected override void OnApplyTemplate()
    {
        surface?.Children.Clear();
        base.OnApplyTemplate(); surface = GetTemplateChild("RailCanvas") as Canvas;
        if (surface is null) return;
        foreach (var element in new FrameworkElement[] { lowerHit, shade, rail, cache, played, marker, head, preview }) surface.Children.Add(element);
        Refresh();
    }
    private double Fraction(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
    internal double ValueAt(double x) => Minimum + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (Maximum - Minimum);
    private void StopAnimation()
    {
        progressAnimation?.Stop(); headAnimation?.Stop();
        progressAnimation = headAnimation = null;
    }
    private void StopHover()
    {
        hoverAnimation?.Stop(); hoverAnimation = null; hoverExpanded = hoverShade = false;
        railScale.ScaleY = cacheScale.ScaleY = scale.ScaleY = 1; head.Opacity = shade.Opacity = 0;
    }
    internal void SetPointerWithin(bool within)
    {
        pointerWithin = within;
        if (!within && !IsScrubbing) ClosePreview();
        Refresh(false);
        if (within) UpdatePreview();
    }
    private void Refresh(bool resample = true)
    {
        Height = RailAtTop ? UpperEnvelope + 16 : 16;
        if (surface is null) return;
        double display = resample ? Value : DisplayValue;
        StopAnimation();
        double width = Math.Max(0, ActualWidth), y = RailAtTop ? UpperEnvelope + 1 : ActualHeight / 2;
        bool visible = DesktopVisual.IsVisible(this);
        bool expanded = IsEnabled && visible && (pointerWithin || IsScrubbing || FocusState != FocusState.Unfocused);
        bool interactive = IsEnabled && visible && (pointerWithin || IsScrubbing);
        bool highContrast = DesktopTheme.GetIsHighContrast(this);
        var settings = new global::Windows.UI.ViewManagement.UISettings();
        var active = highContrast ? new SolidColorBrush(settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Highlight)) :
            PlayedBrush ?? MusicVisuals.Brush("#808080");
        rail.Fill = highContrast ? new SolidColorBrush(settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText)) :
            RailBrush ?? MusicVisuals.Brush("#0D000000");
        rail.Width = lowerHit.Width = shade.Width = width;
        double hitTop = RailAtTop && interactive ? RailOrigin - 3 : RailOrigin;
        lowerHit.Height = RailOrigin + 16 - hitTop; Canvas.SetTop(lowerHit, hitTop);
        rail.IsHitTestVisible = head.IsHitTestVisible = interactive;
        cache.IsHitTestVisible = played.IsHitTestVisible = marker.IsHitTestVisible = false;
        shade.Visibility = RailAtTop && !highContrast ? Visibility.Visible : Visibility.Collapsed;
        shade.IsHitTestVisible = interactive;
        shade.Height = 40; Canvas.SetTop(shade, y - 4 - 40);
        var tone = (PlayedBrush as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray;
        shade.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(), EndPoint = new Point(0, 1), GradientStops =
            [new GradientStop { Color = Color.FromArgb(0, tone.R, tone.G, tone.B) },
             new GradientStop { Color = Color.FromArgb(36, tone.R, tone.G, tone.B), Offset = 1 }]
        };
        cache.Fill = active; cache.Opacity = .28; cache.Width = width * (double.IsFinite(CacheFraction) ? Math.Clamp(CacheFraction, 0, 1) : 0);
        played.Fill = active; played.Width = width; scale.ScaleX = Fraction(display);
        foreach (var rectangle in new[] { rail, cache, played }) { rectangle.Height = 2; Canvas.SetTop(rectangle, y - 1); }
        head.Fill = marker.Fill = highContrast ?
            new SolidColorBrush(settings.UIElementColor(global::Windows.UI.ViewManagement.UIElementType.HighlightText)) : MusicVisuals.Brush("#FFFFFF");
        Canvas.SetTop(head, y - 7); headPosition.X = Math.Clamp(Fraction(display) * width, 7.5, Math.Max(7.5, width - 7.5)) - 7;
        marker.Visibility = double.IsFinite(ChorusSeconds) && ChorusSeconds >= Minimum && ChorusSeconds < Maximum ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(marker, double.IsFinite(ChorusSeconds) ? Math.Clamp(Fraction(ChorusSeconds) * width - 2, 0, Math.Max(0, width - 4)) : 0);
        Canvas.SetTop(marker, y - 2);
        RefreshHover(expanded, RailAtTop && interactive && !highContrast, visible && IsEnabled && !highContrast);
        if (interactive) UpdatePreview(display);
        if (IsScrubbing || !visible || !IsPlaying || !MusicVisuals.CanAnimate(this) ||
            string.IsNullOrEmpty(PresentationKey) || PlaybackRate is not { } rate || !double.IsFinite(rate) || rate <= 0 || Maximum <= Value) return;
        sampledAt = Stopwatch.GetTimestamp();
        var duration = TimeSpan.FromSeconds((Maximum - display) / rate);
        progressAnimation = Linear(scale, "ScaleX", Fraction(display), 1, duration);
        headAnimation = Linear(headPosition, "X", headPosition.X, Math.Max(.5, width - 14.5), duration);
    }
    private void RefreshHover(bool expanded, bool showShade, bool allowAnimation)
    {
        bool animate = allowAnimation && MusicVisuals.CanAnimate(this);
        if (hoverExpanded == expanded && hoverShade == showShade && (animate || hoverAnimation is null)) return;
        double fromScale = railScale.ScaleY, fromHead = head.Opacity, fromShade = shade.Opacity;
        hoverAnimation?.Stop(); hoverAnimation = null; hoverExpanded = expanded; hoverShade = showShade;
        railScale.ScaleY = cacheScale.ScaleY = scale.ScaleY = expanded ? 4 : 1;
        head.Opacity = expanded ? 1 : 0; shade.Opacity = showShade ? 1 : 0;
        if (!animate) return;
        var storyboard = new Storyboard();
        void Add(DependencyObject target, string path, double from, double to)
        {
            var animation = new DoubleAnimation
            { From = from, To = to, Duration = TimeSpan.FromMilliseconds(120), EnableDependentAnimation = true };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, path);
            storyboard.Children.Add(animation);
        }
        foreach (var transform in new[] { railScale, cacheScale, scale }) Add(transform, "ScaleY", fromScale, expanded ? 4 : 1);
        Add(head, "Opacity", fromHead, expanded ? 1 : 0); Add(shade, "Opacity", fromShade, showShade ? 1 : 0);
        storyboard.Completed += (_, _) => { if (ReferenceEquals(hoverAnimation, storyboard)) hoverAnimation = null; };
        hoverAnimation = storyboard; storyboard.Begin();
    }
    private static Storyboard Linear(DependencyObject target, string path, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, path);
        var storyboard = new Storyboard(); storyboard.Children.Add(animation); storyboard.Begin(); return storyboard;
    }
    private void OnSeekKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled |= HandleSeekKey(e.Key, true, e.KeyStatus.WasKeyDown);
    }
    private void OnSeekKeyUp(object sender, KeyRoutedEventArgs e) => e.Handled |= HandleSeekKey(e.Key, false);
    internal bool HandleSeekKey(VirtualKey key, bool down, bool repeated = false)
    {
        if (down && key == VirtualKey.Escape && IsScrubbing) { CancelScrub(); return true; }
        if (!IsEnabled || !IsSeekKey(key)) return false;
        if (!down) { if (IsScrubbing && !pointerGesture) EndScrub(true); return true; }
        if (repeated && !IsScrubbing) return true;
        BeginScrub(); UpdateScrub(key switch
        {
            VirtualKey.Home => Minimum, VirtualKey.End => Maximum,
            VirtualKey.Left or VirtualKey.Down => Value - SmallChange,
            VirtualKey.Right or VirtualKey.Up => Value + SmallChange,
            VirtualKey.PageDown => Value - LargeChange, _ => Value + LargeChange
        }); UpdatePreview(); return true;
    }
    internal bool HasActiveAnimation => new[] { progressAnimation, headAnimation, hoverAnimation }
        .Any(animation => animation?.GetCurrentState() == ClockState.Active);
    private static bool IsSeekKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageDown or VirtualKey.PageUp;
    internal void BeginScrub()
    { if (IsScrubbing || !IsEnabled) return; IsScrubbing = true; ScrubStarted?.Invoke(this, EventArgs.Empty); Refresh(); }
    internal void UpdateScrub(double value) { if (IsScrubbing && double.IsFinite(value)) Value = Math.Clamp(value, Minimum, Maximum); }
    internal void EndScrub(bool commit)
    {
        if (!IsScrubbing) return;
        IsScrubbing = pointerGesture = false; ReleasePointerCaptures();
        if (commit && IsEnabled) ScrubCommitted?.Invoke(this, EventArgs.Empty); else ScrubCancelled?.Invoke(this, EventArgs.Empty);
        ClosePreview(); Refresh();
    }
    internal void CancelScrub() => EndScrub(false);
    private void ClosePreview() => preview.Visibility = Visibility.Collapsed;
    private void UpdatePreview(double? display = null)
    {
        if (!IsEnabled || !DesktopVisual.IsVisible(this) || (!pointerWithin && !IsScrubbing)) { ClosePreview(); return; }
        double value = IsScrubbing ? Value : display ?? DisplayValue;
        string text = Clock(value) + " / " + Clock(Maximum);
        if (previewText.Text != text) previewText.Text = text;
        preview.Visibility = Visibility.Visible;
        preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = preview.DesiredSize;
        double center = Math.Clamp(Fraction(value) * ActualWidth, 7.5, Math.Max(7.5, ActualWidth - 7.5));
        double left = Math.Clamp(center - size.Width / 2, 0, Math.Max(0, ActualWidth - size.Width));
        Canvas.SetLeft(preview, left - (center - 7));
        Canvas.SetTop(preview, RailOrigin - size.Height - 8);
    }
    private static string Clock(double value) { long seconds = Math.Max(0, (long)Math.Floor(value)); return $"{seconds / 60:00}:{seconds % 60:00}"; }
    protected override AutomationPeer OnCreateAutomationPeer() => new SeekPeer(this);
    private sealed class SeekPeer(SeekBar owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(SeekBar);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);
        bool IRangeValueProvider.IsReadOnly => !owner.IsEnabled;
        double IRangeValueProvider.LargeChange => owner.LargeChange;
        double IRangeValueProvider.SmallChange => owner.SmallChange;
        double IRangeValueProvider.Minimum => owner.Minimum;
        double IRangeValueProvider.Maximum => owner.Maximum;
        double IRangeValueProvider.Value => owner.Value;
        void IRangeValueProvider.SetValue(double value)
        {
            if (!owner.IsEnabled) throw new InvalidOperationException("The seek control is disabled.");
            if (!double.IsFinite(value) || value < owner.Minimum || value > owner.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.BeginScrub(); owner.UpdateScrub(value); owner.EndScrub(true);
        }
    }
}
