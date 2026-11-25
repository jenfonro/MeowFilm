using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// The gesture commits one seek. Composition animations advance only the visual rail and never Value.
public sealed class SeekBar : Slider
{
    private const double UpperEnvelope = 43;
    private const double DriftRecalibrateThresholdSeconds = 0.25;
    [ThreadStatic] private static SolidColorBrush? defaultPlayedBrush, defaultRailBrush, headStrokeBrush, previewForegroundBrush;
    [ThreadStatic] private static FontFamily? previewFontFamily;
    private static SolidColorBrush DefaultPlayedBrush => defaultPlayedBrush ??= DesktopVisual.Brush("#808080");
    private static SolidColorBrush DefaultRailBrush => defaultRailBrush ??= DesktopVisual.Brush("#0D000000");
    private static SolidColorBrush DefaultHeadBrush => DesktopVisual.WhiteBrush;
    private static SolidColorBrush TransparentBrush => DesktopVisual.TransparentBrush;
    private static SolidColorBrush HeadStrokeBrush => headStrokeBrush ??= new(Color.FromArgb(51, 0, 0, 0));
    private static SolidColorBrush PreviewForegroundBrush => previewForegroundBrush ??= new(Microsoft.UI.Colors.Black);
    private static SolidColorBrush PreviewBackgroundBrush => DesktopVisual.WhiteBrush;
    private static FontFamily PreviewFontFamily => previewFontFamily ??= new("Segoe UI");

    public static readonly DependencyProperty ChorusSecondsProperty = VisualProperty(nameof(ChorusSeconds), typeof(double), double.NaN);
    public static readonly DependencyProperty PlayedBrushProperty = VisualProperty(nameof(PlayedBrush), typeof(Brush), null);
    public static readonly DependencyProperty RailBrushProperty = VisualProperty(nameof(RailBrush), typeof(Brush), null);
    public static readonly DependencyProperty RailAtTopProperty = VisualProperty(nameof(RailAtTop), typeof(bool), false);
    public static readonly DependencyProperty CacheFractionProperty = DependencyProperty.Register(nameof(CacheFraction), typeof(double), typeof(SeekBar),
        new PropertyMetadata(0d, (owner, _) => ((SeekBar)owner).UpdateCacheWidth()));
    public static readonly DependencyProperty IsPlayingProperty = Property(nameof(IsPlaying), typeof(bool), false);
    public static readonly DependencyProperty PlaybackRateProperty = Property(nameof(PlaybackRate), typeof(double?), null);
    public static readonly DependencyProperty PresentationKeyProperty = DependencyProperty.Register(nameof(PresentationKey), typeof(string), typeof(SeekBar),
        new PropertyMetadata("", (owner, _) => { var bar = (SeekBar)owner; if (!bar.CancelScrub()) bar.RefreshFull(resample: true); }));

    private static DependencyProperty Property(string name, Type type, object? value) => DependencyProperty.Register(name, type, typeof(SeekBar),
        new PropertyMetadata(value, (owner, _) => ((SeekBar)owner).RefreshFull(resample: true)));

    private static DependencyProperty VisualProperty(string name, Type type, object? value) => DependencyProperty.Register(name, type, typeof(SeekBar),
        new PropertyMetadata(value, (owner, _) => ((SeekBar)owner).RefreshFull(resample: false)));

    public double ChorusSeconds { get => (double)GetValue(ChorusSecondsProperty); set => SetValue(ChorusSecondsProperty, value); }
    public Brush? PlayedBrush { get => (Brush?)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush? RailBrush { get => (Brush?)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }
    public bool RailAtTop { get => (bool)GetValue(RailAtTopProperty); set => SetValue(RailAtTopProperty, value); }
    public double CacheFraction { get => (double)GetValue(CacheFractionProperty); set => SetValue(CacheFractionProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }

    private Canvas? surface;
    private readonly Rectangle rail = new() { Height = 2 }, cache = new() { Width = 0, Height = 2, Opacity = .28 },
        played = new() { Height = 2 }, shade = new() { Height = 40 },
        lowerHit = new() { Height = 16, Fill = TransparentBrush };
    private readonly Ellipse marker = new() { Width = 4, Height = 4 },
        head = new() { Width = 14, Height = 14, Stroke = HeadStrokeBrush, StrokeThickness = 1 };
    private readonly Visual railVisual, cacheVisual, playedVisual, headVisual, shadeVisual;
    private LinearEasingFunction? linearEasing;
    private ScalarKeyFrameAnimation? playedScaleAnimation, headTranslationAnimation;
    private bool progressAnimating, previewTicking;
    private double animStartFraction, animRate, animMin, animMax, animWidth;
    private long sampledAt, hoverAnimUntil;
    private float currentHoverScaleY = 1f, currentHeadOpacity, currentShadeOpacity;
    private Color? cachedShadeTone;
    private LinearGradientBrush? cachedShadeBrush;
    private double lastRenderedFraction = -1, lastRenderedHeadX = -1, lastLayoutWidth = -1, lastLayoutHeight = -1;
    private bool? lastRailAtTop;
    private IDisposable? visibility;
    private readonly TextBlock previewText = new()
    {
        FontSize = 12,
        FontFamily = PreviewFontFamily,
        Foreground = PreviewForegroundBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border preview = new()
    {
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(12, 5, 12, 5),
        Background = PreviewBackgroundBrush,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private bool pointerGesture, pointerWithin, hoverExpanded, hoverShade;
    private long lastPreviewSec = -1, lastPreviewMaxSec = -1;
    private int lastPreviewLength;
    private Size lastPreviewSize;
    private double lastPreviewLeft = -1, lastPreviewTop = double.NaN;

    internal bool IsScrubbing { get; private set; }
    internal double DisplayValue => progressAnimating
        ? Math.Min(Maximum, Minimum + animStartFraction * (Maximum - Minimum) + Stopwatch.GetElapsedTime(sampledAt).TotalSeconds * animRate)
        : Value;
    internal double RailOrigin => RailAtTop ? UpperEnvelope : 0;

    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCommitted;
    public event EventHandler? ScrubCancelled;

    public SeekBar()
    {
        if (Application.Current?.Resources.TryGetValue("MeowFilm.SeekBarTemplate", out var template) == true &&
            template is ControlTemplate controlTemplate)
            Template = controlTemplate;
        MinHeight = 0; Height = 16; SmallChange = 5; LargeChange = 15; IsTabStop = true;
        Typography.SetNumeralAlignment(previewText, FontNumeralAlignment.Tabular);
        ElementCompositionPreview.SetIsTranslationEnabled(head, true);
        railVisual = ElementCompositionPreview.GetElementVisual(rail);
        cacheVisual = ElementCompositionPreview.GetElementVisual(cache);
        playedVisual = ElementCompositionPreview.GetElementVisual(played);
        headVisual = ElementCompositionPreview.GetElementVisual(head);
        shadeVisual = ElementCompositionPreview.GetElementVisual(shade);
        railVisual.CenterPoint = cacheVisual.CenterPoint = playedVisual.CenterPoint = new Vector3(0f, 1f, 0f);
        headVisual.Opacity = 0f;
        shadeVisual.Opacity = 0f;
        headVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        cache.IsHitTestVisible = played.IsHitTestVisible = marker.IsHitTestVisible = false;
        preview.Child = previewText;
        Canvas.SetZIndex(preview, 1);

        ValueChanged += (_, _) => OnValueChanged();
        SizeChanged += (_, _) => RefreshFull(resample: false);
        Loaded += (_, _) =>
        {
            ResetPreviewCache();
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, visible =>
            {
                if (!visible) { pointerWithin = false; CancelScrub(); StopProgressAnimation(); RefreshHover(false, false, false); ClosePreview(); }
                RefreshFull(resample: false);
            });
            RefreshFull(resample: true);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            visibility?.Dispose(); visibility = null;
            pointerWithin = false; CancelScrub(); StopProgressAnimation(); RefreshHover(false, false, false); ClosePreview();
            DisposeAnimations();
        };
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => { if (!(!IsEnabled && CancelScrub())) RefreshFull(resample: false); });
        RegisterPropertyChangedCallback(MaximumProperty, (_, _) => RefreshFull(resample: true));
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => RefreshFull(resample: false));
        ActualThemeChanged += (_, _) => RefreshFull(resample: false);
        GotFocus += (_, _) => { if (!pointerGesture) RefreshFull(resample: false); };
        LostFocus += (_, _) => { if (!CancelScrub()) RefreshFull(resample: false); };
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e) => SetPointerWithin(true);
    protected override void OnPointerExited(PointerRoutedEventArgs e) => SetPointerWithin(false);
    protected override void OnPointerMoved(PointerRoutedEventArgs e) { if (pointerGesture && IsScrubbing) UpdateScrub(ValueAt(e.GetCurrentPoint(this).Position.X)); }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) { if (pointerGesture) CancelScrub(); }
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e) { }
    protected override void OnKeyDown(KeyRoutedEventArgs e) => e.Handled |= HandleSeekKey(e.Key, true, e.KeyStatus.WasKeyDown);
    protected override void OnKeyUp(KeyRoutedEventArgs e) => e.Handled |= HandleSeekKey(e.Key, false);

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!IsEnabled || !e.GetCurrentPoint(this).IsInContact || !CapturePointer(e.Pointer)) return;
        e.Handled = true; pointerGesture = true; Focus(FocusState.Pointer); BeginScrub();
        UpdateScrub(ValueAt(e.GetCurrentPoint(this).Position.X));
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (!pointerGesture) return;
        e.Handled = true; UpdateScrub(ValueAt(e.GetCurrentPoint(this).Position.X)); EndScrub(true);
    }

    protected override void OnApplyTemplate()
    {
        surface?.Children.Clear();
        base.OnApplyTemplate();
        surface = GetTemplateChild("RailCanvas") as Canvas;
        if (surface is null) return;
        surface.Children.Add(lowerHit);
        surface.Children.Add(shade);
        surface.Children.Add(rail);
        surface.Children.Add(cache);
        surface.Children.Add(played);
        surface.Children.Add(marker);
        surface.Children.Add(head);
        surface.Children.Add(preview);
        lastLayoutWidth = lastLayoutHeight = -1;
        ResetPreviewCache();
        lastRailAtTop = null;
        RefreshFull(resample: true);
    }

    private void ResetPreviewCache()
    {
        lastPreviewSec = lastPreviewMaxSec = -1;
        lastPreviewLength = 0;
        lastPreviewLeft = -1;
        lastPreviewTop = double.NaN;
    }

    private double Fraction(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
    private static double HeadX(double fraction, double width) => Math.Clamp(fraction * width, 7.5, Math.Max(7.5, width - 7.5)) - 7;
    internal double ValueAt(double x) => Minimum + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (Maximum - Minimum);

    private bool CanKeepProgressAnimation(double width, bool visible) =>
        progressAnimating && !IsScrubbing && visible && IsPlaying && width > 0 &&
        DesktopVisual.CanAnimate(this) && !string.IsNullOrEmpty(PresentationKey) &&
        Math.Abs(animWidth - width) < 0.5 &&
        Math.Abs(animMin - Minimum) < 1e-6 &&
        Math.Abs(animMax - Maximum) < 1e-6 &&
        PlaybackRate is { } rate && Math.Abs(rate - animRate) < 1e-6;

    private void UpdateCacheWidth()
    {
        if (surface is null) return;
        double width = Math.Max(0, ActualWidth);
        double cacheWidth = width * (double.IsFinite(CacheFraction) ? Math.Clamp(CacheFraction, 0, 1) : 0);
        if (Math.Abs(cache.Width - cacheWidth) > 0.1) cache.Width = cacheWidth;
    }

    private void OnValueChanged()
    {
        if (surface is null) return;
        double width = Math.Max(0, ActualWidth);
        bool visible = DesktopVisual.IsVisible(this);
        if (CanKeepProgressAnimation(width, visible) &&
            Math.Abs(Value - DisplayValue) < DriftRecalibrateThresholdSeconds)
        {
            if (pointerWithin && !previewTicking) UpdatePreview();
            return;
        }
        SyncProgressVisuals(Value, width, visible);
        if (IsEnabled && (pointerWithin || IsScrubbing)) UpdatePreview(Value);
    }

    private void RefreshFull(bool resample)
    {
        double targetHeight = RailAtTop ? UpperEnvelope + 16 : 16;
        if (Math.Abs(Height - targetHeight) > 0.1) Height = targetHeight;
        if (surface is null) return;
        double display = resample ? Value : DisplayValue;
        double width = Math.Max(0, ActualWidth), height = ActualHeight;
        double y = RailAtTop ? UpperEnvelope + 1 : height / 2;
        bool visible = DesktopVisual.IsVisible(this);
        bool expanded = IsEnabled && visible && (pointerWithin || IsScrubbing || FocusState != FocusState.Unfocused);
        bool interactive = IsEnabled && visible && (pointerWithin || IsScrubbing);
        bool highContrast = DesktopTheme.GetIsHighContrast(this);

        var active = highContrast
            ? SystemAppearance.GetElementBrush(global::Windows.UI.ViewManagement.UIElementType.Highlight)
            : PlayedBrush ?? DefaultPlayedBrush;
        var railBrush = highContrast
            ? SystemAppearance.GetElementBrush(global::Windows.UI.ViewManagement.UIElementType.WindowText)
            : RailBrush ?? DefaultRailBrush;
        var headFill = highContrast
            ? SystemAppearance.GetElementBrush(global::Windows.UI.ViewManagement.UIElementType.HighlightText)
            : DefaultHeadBrush;

        if (!ReferenceEquals(rail.Fill, railBrush)) rail.Fill = railBrush;
        if (!ReferenceEquals(cache.Fill, active)) cache.Fill = active;
        if (!ReferenceEquals(played.Fill, active)) played.Fill = active;
        if (!ReferenceEquals(head.Fill, headFill)) head.Fill = headFill;
        if (!ReferenceEquals(marker.Fill, headFill)) marker.Fill = headFill;

        bool geometryChanged = Math.Abs(lastLayoutWidth - width) > 0.1 ||
            Math.Abs(lastLayoutHeight - height) > 0.1 ||
            lastRailAtTop != RailAtTop;
        if (geometryChanged)
        {
            lastLayoutWidth = width;
            lastLayoutHeight = height;
            lastRailAtTop = RailAtTop;
            rail.Width = lowerHit.Width = shade.Width = played.Width = width;
            double barTop = y - 1;
            Canvas.SetTop(rail, barTop);
            Canvas.SetTop(cache, barTop);
            Canvas.SetTop(played, barTop);
            Canvas.SetTop(shade, y - 4 - 40);
            Canvas.SetTop(head, y - 7);
            Canvas.SetTop(marker, y - 2);
            lastRenderedFraction = -1;
            lastRenderedHeadX = -1;
            lastPreviewLeft = -1;
        }

        double hitTop = RailAtTop && interactive ? RailOrigin - 3 : RailOrigin;
        double hitHeight = RailOrigin + 16 - hitTop;
        if (Math.Abs(lowerHit.Height - hitHeight) > 0.1) lowerHit.Height = hitHeight;
        if (Math.Abs(Canvas.GetTop(lowerHit) - hitTop) > 0.1) Canvas.SetTop(lowerHit, hitTop);

        rail.IsHitTestVisible = head.IsHitTestVisible = shade.IsHitTestVisible = interactive;
        var shadeVisibility = RailAtTop && !highContrast ? Visibility.Visible : Visibility.Collapsed;
        if (shade.Visibility != shadeVisibility) shade.Visibility = shadeVisibility;
        if (shadeVisibility == Visibility.Visible)
        {
            var tone = (PlayedBrush as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray;
            if (cachedShadeTone != tone || cachedShadeBrush is null)
            {
                cachedShadeTone = tone;
                cachedShadeBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    [
                        new GradientStop { Color = Color.FromArgb(0, tone.R, tone.G, tone.B) },
                        new GradientStop { Color = Color.FromArgb(36, tone.R, tone.G, tone.B), Offset = 1 }
                    ]
                };
            }
            if (!ReferenceEquals(shade.Fill, cachedShadeBrush)) shade.Fill = cachedShadeBrush;
        }

        UpdateCacheWidth();

        bool showMarker = double.IsFinite(ChorusSeconds) && ChorusSeconds >= Minimum && ChorusSeconds < Maximum;
        var markerVis = showMarker ? Visibility.Visible : Visibility.Collapsed;
        if (marker.Visibility != markerVis) marker.Visibility = markerVis;
        if (showMarker)
        {
            double markerLeft = Math.Clamp(Fraction(ChorusSeconds) * width - 2, 0, Math.Max(0, width - 4));
            if (Math.Abs(Canvas.GetLeft(marker) - markerLeft) > 0.1) Canvas.SetLeft(marker, markerLeft);
        }

        if (resample || geometryChanged || !CanKeepProgressAnimation(width, visible))
            SyncProgressVisuals(display, width, visible);
        RefreshHover(expanded, RailAtTop && interactive && !highContrast, visible && IsEnabled && !highContrast);
        if (interactive) UpdatePreview(display);
        else ClosePreview();
    }

    private void SyncProgressVisuals(double display, double width, bool visible)
    {
        StopProgressAnimation();
        double fraction = Fraction(display);
        double headX = HeadX(fraction, width);
        if (Math.Abs(lastRenderedFraction - fraction) > 1e-5)
        {
            if (Stopwatch.GetTimestamp() < hoverAnimUntil)
                StartHoverScalar(playedVisual.Compositor, playedVisual, "Scale.X", (float)fraction, (float)fraction, TimeSpan.FromMilliseconds(1));
            else
                playedVisual.Scale = new Vector3((float)fraction, currentHoverScaleY, 1f);
            lastRenderedFraction = fraction;
        }
        if (Math.Abs(lastRenderedHeadX - headX) > 0.05)
        {
            headVisual.Properties.InsertVector3("Translation", new Vector3((float)headX, 0f, 0f));
            lastRenderedHeadX = headX;
        }

        if (IsScrubbing || !visible || !IsPlaying || width <= 0 || !DesktopVisual.CanAnimate(this) ||
            string.IsNullOrEmpty(PresentationKey) || PlaybackRate is not { } rate || !double.IsFinite(rate) ||
            rate <= 0 || Maximum <= display)
            return;

        double remainingSeconds = (Maximum - display) / rate;
        if (remainingSeconds <= 0.001) return;

        var compositor = playedVisual.Compositor;
        linearEasing ??= compositor.CreateLinearEasingFunction();
        var duration = TimeSpan.FromSeconds(remainingSeconds);

        playedScaleAnimation?.Dispose();
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0f, (float)fraction);
        scaleAnim.InsertKeyFrame(1f, 1f, linearEasing);
        scaleAnim.Duration = duration;
        playedScaleAnimation = scaleAnim;

        double endHeadX = HeadX(1.0, width);
        headTranslationAnimation?.Dispose();
        var headAnim = compositor.CreateScalarKeyFrameAnimation();
        headAnim.InsertKeyFrame(0f, (float)headX);
        headAnim.InsertKeyFrame(1f, (float)endHeadX, linearEasing);
        headAnim.Duration = duration;
        headTranslationAnimation = headAnim;

        playedVisual.StartAnimation("Scale.X", scaleAnim);
        headVisual.StartAnimation("Translation.X", headAnim);
        animStartFraction = fraction;
        animRate = rate;
        animMin = Minimum;
        animMax = Maximum;
        animWidth = width;
        sampledAt = Stopwatch.GetTimestamp();
        progressAnimating = true;
        lastRenderedFraction = -1;
        lastRenderedHeadX = -1;
    }

    private void StopProgressAnimation()
    {
        if (!progressAnimating) return;
        progressAnimating = false;
        playedVisual.StopAnimation("Scale.X");
        headVisual.StopAnimation("Translation.X");
        playedScaleAnimation?.Dispose();
        playedScaleAnimation = null;
        headTranslationAnimation?.Dispose();
        headTranslationAnimation = null;
    }

    private void DisposeAnimations()
    {
        playedScaleAnimation?.Dispose();
        playedScaleAnimation = null;
        headTranslationAnimation?.Dispose();
        headTranslationAnimation = null;
        linearEasing?.Dispose();
        linearEasing = null;
    }

    internal void SetPointerWithin(bool within)
    {
        if (pointerWithin == within) return;
        pointerWithin = within;
        RefreshFull(resample: false);
    }

    private void RefreshHover(bool expanded, bool showShade, bool allowAnimation)
    {
        bool animate = allowAnimation && DesktopVisual.CanAnimate(this);
        if (hoverExpanded == expanded && hoverShade == showShade && (animate || Stopwatch.GetTimestamp() >= hoverAnimUntil))
            return;

        float fromScale = currentHoverScaleY, toScale = expanded ? 4f : 1f;
        float fromHead = currentHeadOpacity, toHead = expanded ? 1f : 0f;
        float fromShade = currentShadeOpacity, toShade = showShade ? 1f : 0f;
        hoverExpanded = expanded;
        hoverShade = showShade;
        currentHoverScaleY = toScale;
        currentHeadOpacity = toHead;
        currentShadeOpacity = toShade;

        if (!animate)
        {
            hoverAnimUntil = 0;
            railVisual.StopAnimation("Scale.Y");
            cacheVisual.StopAnimation("Scale.Y");
            playedVisual.StopAnimation("Scale.Y");
            headVisual.StopAnimation("Opacity");
            shadeVisual.StopAnimation("Opacity");
            railVisual.Scale = new Vector3(1f, toScale, 1f);
            cacheVisual.Scale = new Vector3(1f, toScale, 1f);
            if (!progressAnimating)
                playedVisual.Scale = new Vector3((float)Fraction(DisplayValue), toScale, 1f);
            headVisual.Opacity = toHead;
            shadeVisual.Opacity = toShade;
            return;
        }

        var compositor = railVisual.Compositor;
        var duration = TimeSpan.FromMilliseconds(120);
        StartHoverScalar(compositor, railVisual, "Scale.Y", fromScale, toScale, duration);
        StartHoverScalar(compositor, cacheVisual, "Scale.Y", fromScale, toScale, duration);
        StartHoverScalar(compositor, playedVisual, "Scale.Y", fromScale, toScale, duration);
        StartHoverScalar(compositor, headVisual, "Opacity", fromHead, toHead, duration);
        StartHoverScalar(compositor, shadeVisual, "Opacity", fromShade, toShade, duration);
        hoverAnimUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.12);
    }

    private static void StartHoverScalar(Compositor compositor, Visual visual, string property, float from, float to, TimeSpan duration)
    {
        using var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to);
        animation.Duration = duration;
        visual.StartAnimation(property, animation);
    }

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
        }); return true;
    }

    private static bool IsSeekKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageDown or VirtualKey.PageUp;

    internal void BeginScrub()
    { if (IsScrubbing || !IsEnabled) return; IsScrubbing = true; ScrubStarted?.Invoke(this, EventArgs.Empty); RefreshFull(resample: true); }

    internal void UpdateScrub(double value) { if (IsScrubbing && double.IsFinite(value)) Value = Math.Clamp(value, Minimum, Maximum); }

    internal bool EndScrub(bool commit)
    {
        if (!IsScrubbing) return false;
        IsScrubbing = pointerGesture = false; ReleasePointerCaptures();
        if (commit && IsEnabled) ScrubCommitted?.Invoke(this, EventArgs.Empty); else ScrubCancelled?.Invoke(this, EventArgs.Empty);
        ClosePreview(); RefreshFull(resample: true);
        return true;
    }

    internal bool CancelScrub() => EndScrub(false);

    private void SyncPreviewTick(bool active)
    {
        if (previewTicking == active) return;
        previewTicking = active;
        if (active) CompositionTarget.Rendering += OnPreviewRendering;
        else CompositionTarget.Rendering -= OnPreviewRendering;
    }

    private void OnPreviewRendering(object? sender, object e) => UpdatePreview();

    private void ClosePreview()
    {
        SyncPreviewTick(false);
        preview.Visibility = Visibility.Collapsed;
    }

    private void UpdatePreview(double? display = null)
    {
        if (!IsEnabled || !DesktopVisual.IsVisible(this) || (!pointerWithin && !IsScrubbing)) { ClosePreview(); return; }
        SyncPreviewTick(progressAnimating && !IsScrubbing);
        double width = Math.Max(0, ActualWidth);
        double value = IsScrubbing ? Value : display ?? DisplayValue;
        long sec = Math.Max(0, (long)Math.Floor(value)), maxSec = Math.Max(0, (long)Math.Floor(Maximum));
        if (preview.Visibility != Visibility.Visible) preview.Visibility = Visibility.Visible;
        if (sec != lastPreviewSec || maxSec != lastPreviewMaxSec || lastPreviewSize.Width <= 0)
        {
            lastPreviewSec = sec;
            lastPreviewMaxSec = maxSec;
            string text = $"{sec / 60:00}:{sec % 60:00} / {maxSec / 60:00}:{maxSec % 60:00}";
            previewText.Text = text;
            if (lastPreviewLength != text.Length || lastPreviewSize.Width <= 0)
            {
                lastPreviewLength = text.Length;
                preview.ClearValue(WidthProperty);
                preview.ClearValue(HeightProperty);
                preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                lastPreviewSize = new Size(Math.Ceiling(preview.DesiredSize.Width), Math.Ceiling(preview.DesiredSize.Height));
                preview.Width = lastPreviewSize.Width;
                preview.Height = lastPreviewSize.Height;
            }
        }
        double scale = XamlRoot?.RasterizationScale ?? 1d;
        var size = lastPreviewSize;
        double center = Math.Clamp(Fraction(value) * width, 7.5, Math.Max(7.5, width - 7.5));
        double left = Math.Floor(Math.Clamp(center - size.Width / 2, 0, Math.Max(0, width - size.Width)) * scale + 0.5) / scale;
        double top = RailOrigin - size.Height - 8;
        if (Math.Abs(lastPreviewLeft - left) > 1e-4) { lastPreviewLeft = left; Canvas.SetLeft(preview, left); }
        if (double.IsNaN(lastPreviewTop) || Math.Abs(lastPreviewTop - top) > 0.1) { lastPreviewTop = top; Canvas.SetTop(preview, top); }
    }

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
