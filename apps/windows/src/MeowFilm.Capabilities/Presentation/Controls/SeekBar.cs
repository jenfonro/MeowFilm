using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>
/// Native Slider range/automation with one continuous painted rail. The hit area is
/// independent of the painted rail; no invisible thumb or parent border splits it.
/// A gesture is a transaction, never a stream of media seeks.
/// </summary>
public sealed class SeekBar : Slider
{
    public static readonly DependencyProperty ChorusSecondsProperty = DependencyProperty.Register(
        nameof(ChorusSeconds), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, PlayedBrushChanged));
    public static readonly DependencyProperty RailBrushProperty = DependencyProperty.Register(
        nameof(RailBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Frozen("#0D000000"), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RailAtTopProperty = DependencyProperty.Register(
        nameof(RailAtTop), typeof(bool), typeof(SeekBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ExpansionProperty = DependencyProperty.Register(
        "Expansion", typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty HoverShadeOpacityProperty = DependencyProperty.Register(
        "HoverShadeOpacity", typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(SeekBar), new PropertyMetadata(false, PlaybackChanged));
    public static readonly DependencyProperty PlaybackRateProperty = DependencyProperty.Register(
        nameof(PlaybackRate), typeof(double?), typeof(SeekBar), new PropertyMetadata(null, PlaybackChanged));
    public static readonly DependencyProperty PresentationKeyProperty = DependencyProperty.Register(
        nameof(PresentationKey), typeof(string), typeof(SeekBar), new PropertyMetadata("", PlaybackChanged));
    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, DisplayChanged));
    private readonly ToolTip preview = new()
    {
        Placement = PlacementMode.Relative, StaysOpen = true,
        Background = Brushes.White, Foreground = Brushes.Black,
        BorderThickness = new Thickness(0), Padding = new Thickness(12, 5, 12, 5),
        IsHitTestVisible = false
    };
    private bool pointerGesture, pointerWithin;
    private static readonly Pen HeadOutline = (Pen)new Pen(Frozen("#33000000"), 1).GetAsFrozen();
    private long previewSecond = -1, previewDuration = -1;
    private Brush hoverShade = CreateHoverShade(Brushes.Gray);
    private double pointerX;
    public double ChorusSeconds { get => (double)GetValue(ChorusSecondsProperty); set => SetValue(ChorusSecondsProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush RailBrush { get => (Brush)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }
    public bool RailAtTop { get => (bool)GetValue(RailAtTopProperty); set => SetValue(RailAtTopProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double? PlaybackRate { get => (double?)GetValue(PlaybackRateProperty); set => SetValue(PlaybackRateProperty, value); }
    public string PresentationKey { get => (string)GetValue(PresentationKeyProperty); set => SetValue(PresentationKeyProperty, value); }
    internal double DisplayValue => (double)GetValue(DisplayValueProperty);
    internal bool IsScrubbing { get; private set; }
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCommitted;
    public event EventHandler? ScrubCancelled;

    public SeekBar()
    {
        // An explicit empty template prevents the OS theme's track/ticks/focus rectangle
        // from painting behind our rail. Slider still supplies native range automation.
        Template = new ControlTemplate(typeof(Slider));
        FocusVisualStyle = null;
        Focusable = true;
        MinHeight = 0;
        Height = 8;
        SmallChange = 5;
        LargeChange = 15;
        preview.PlacementTarget = this;
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
            preview.Style = TryFindResource("Capability.SeekPreview") as Style;
            SynchronizePlayback();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
            pointerWithin = false; CancelScrub(); preview.IsOpen = false;
            BeginAnimation(ExpansionProperty, null); SetValue(ExpansionProperty, 0d);
            BeginAnimation(HoverShadeOpacityProperty, null); SetValue(HoverShadeOpacityProperty, 0d);
            BeginAnimation(DisplayValueProperty, null);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                pointerWithin = false; CancelScrub(); preview.IsOpen = false;
                BeginAnimation(ExpansionProperty, null); SetValue(ExpansionProperty, 0d);
                BeginAnimation(HoverShadeOpacityProperty, null); SetValue(HoverShadeOpacityProperty, 0d);
            }
            SynchronizePlayback();
        };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelScrub(); UpdateExpansion(); };
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        bool highContrast = DesktopTheme.GetIsHighContrast(this);
        Brush active = highContrast ? SystemColors.HighlightBrush : PlayedBrush;
        double expansion = Math.Clamp((double)GetValue(ExpansionProperty), 0, 1);
        double shadeOpacity = Math.Clamp((double)GetValue(HoverShadeOpacityProperty), 0, 1);
        Brush rail = highContrast ? SystemColors.WindowTextBrush : RailBrush;
        double thickness = 2 + expansion * 6, y = RailAtTop ? 1 : height / 2;
        if (RailAtTop && shadeOpacity > 0 && !highContrast)
        {
            dc.PushOpacity(shadeOpacity);
            dc.DrawRectangle(hoverShade, null, new Rect(0, y - thickness / 2 - 40, width, 40));
            dc.Pop();
        }
        double end = Fraction(DisplayValue) * width;
        dc.PushOpacity(1);
        dc.DrawRectangle(rail, null, new Rect(0, y - thickness / 2, width, thickness));
        dc.Pop();
        if (end > 0) dc.DrawRectangle(active, null, new Rect(0, y - thickness / 2, end, thickness));
        if (double.IsFinite(ChorusSeconds) && ChorusSeconds >= Minimum && ChorusSeconds < Maximum)
        {
            double marker = Math.Clamp(Fraction(ChorusSeconds) * width, 2, Math.Max(2, width - 2));
            dc.DrawEllipse(highContrast ? SystemColors.HighlightTextBrush : Brushes.White, null, new(marker, y), 2, 2);
        }
        if (expansion > 0 && IsEnabled)
        {
            double radius = 7 * expansion, inset = radius + .5;
            dc.DrawEllipse(highContrast ? SystemColors.HighlightTextBrush : Brushes.White,
                highContrast ? null : HeadOutline,
                new(Math.Clamp(end, inset, Math.Max(inset, width - inset)), y), radius, radius);
        }
    }

    // Decorative drawing above the footer must not keep MouseEnter active or
    // intercept page clicks after the pointer leaves the actual seek surface.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new Rect(RenderSize).Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;

    private static void PlayedBrushChanged(DependencyObject value, DependencyPropertyChangedEventArgs _) =>
        ((SeekBar)value).hoverShade = CreateHoverShade(((SeekBar)value).PlayedBrush);
    private static Brush CreateHoverShade(Brush brush)
    {
        var color = brush is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var gradient = new LinearGradientBrush(Color.FromArgb(0, color.R, color.G, color.B),
            Color.FromArgb(36, color.R, color.G, color.B), new Point(0, 0), new Point(0, 1));
        gradient.Freeze(); return gradient;
    }
    private static void DisplayChanged(DependencyObject value, DependencyPropertyChangedEventArgs _)
    {
        var bar = (SeekBar)value;
        if (bar.preview.IsOpen) bar.UpdatePreview();
    }
    private static Brush Frozen(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private double Fraction(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
    internal double ValueAt(double x) => Minimum + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (Maximum - Minimum);

    protected override void OnValueChanged(double oldValue, double newValue)
    { base.OnValueChanged(oldValue, newValue); SynchronizePlayback(); if (preview.IsOpen) UpdatePreview(); }
    private static void PlaybackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SeekBar)d;
        if (e.Property == PresentationKeyProperty) bar.CancelScrub();
        bar.SynchronizePlayback();
    }
    private void SynchronizePlayback()
    {
        BeginAnimation(DisplayValueProperty, null);
        SetValue(DisplayValueProperty, Value);
        if (IsScrubbing || !IsLoaded || !IsVisible || !IsPlaying ||
            string.IsNullOrEmpty(PresentationKey) ||
            !SystemParameters.ClientAreaAnimation || DesktopTheme.GetIsHighContrast(this) ||
            PlaybackRate is not { } rate || !double.IsFinite(rate) || rate <= 0 ||
            !double.IsFinite(Maximum) || Maximum <= Value) return;
        // Animate only the painted rail, never Slider.Value or application state.
        BeginAnimation(DisplayValueProperty, new DoubleAnimation(Value, Maximum,
            TimeSpan.FromSeconds((Maximum - Value) / rate)) { FillBehavior = FillBehavior.HoldEnd });
    }
    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))
        { SynchronizePlayback(); UpdateExpansion(); }
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty ||
            e.Property == MaximumProperty || e.Property == MinimumProperty) SynchronizePlayback();
        if (e.Property == IsKeyboardFocusWithinProperty || e.Property == DesktopTheme.IsHighContrastProperty) UpdateExpansion();
        // The inherited theme property does not carry AffectsRender. Highlight
        // values can remain unchanged while every painted color needs replacing.
        if (e.Property == DesktopTheme.IsHighContrastProperty) InvalidateVisual();
    }
    private void UpdateExpansion()
    {
        AnimateHighlight(ExpansionProperty, IsEnabled && (pointerWithin || IsKeyboardFocusWithin || IsScrubbing) ? 1 : 0);
        // Focus keeps the keyboard control visible, but must not leave a colored
        // veil over page content after a pointer seek has finished.
        AnimateHighlight(HoverShadeOpacityProperty, IsEnabled && (pointerWithin || IsScrubbing) ? 1 : 0);
        if (!IsEnabled) preview.IsOpen = false;
    }
    private void AnimateHighlight(DependencyProperty property, double target)
    {
        if (!IsLoaded || !IsVisible) target = 0;
        if (!IsLoaded || !IsVisible || !IsEnabled || !SystemParameters.ClientAreaAnimation || DesktopTheme.GetIsHighContrast(this))
        { BeginAnimation(property, null); SetValue(property, target); }
        else BeginAnimation(property, new DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    protected override void OnMouseEnter(MouseEventArgs e)
    { base.OnMouseEnter(e); pointerWithin = true; UpdateExpansion(); UpdatePreview(); }
    protected override void OnMouseLeave(MouseEventArgs e)
    { base.OnMouseLeave(e); pointerWithin = false; if (!IsScrubbing) preview.IsOpen = false; UpdateExpansion(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); pointerX = e.GetPosition(this).X;
        if (pointerGesture && IsScrubbing) UpdateScrub(ValueAt(pointerX));
        UpdatePreview();
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        e.Handled = true; Focus();
        if (!CaptureMouse()) return;
        pointerGesture = true; pointerX = e.GetPosition(this).X;
        BeginScrub(); UpdateScrub(ValueAt(pointerX)); UpdatePreview();
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!pointerGesture || !IsScrubbing) return;
        pointerX = e.GetPosition(this).X;
        UpdateScrub(ValueAt(pointerX));
        EndScrub(commit: true);
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); if (pointerGesture) CancelScrub(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    { base.OnGotKeyboardFocus(e); UpdateExpansion(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    { base.OnLostKeyboardFocus(e); CancelScrub(); UpdateExpansion(); }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsScrubbing) { e.Handled = true; CancelScrub(); return; }
        if (!IsEnabled || !IsSeekKey(e.Key)) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        if (e.IsRepeat && !IsScrubbing) return; // A cancelled old-key gesture cannot resume on a new track.
        BeginScrub();
        UpdateScrub(e.Key switch
        {
            Key.Home => Minimum, Key.End => Maximum,
            Key.Left or Key.Down => Value - SmallChange,
            Key.Right or Key.Up => Value + SmallChange,
            Key.PageDown => Value - LargeChange, _ => Value + LargeChange
        });
        UpdatePreview();
    }
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsSeekKey(e.Key)) { base.OnPreviewKeyUp(e); return; }
        e.Handled = true; if (IsScrubbing && !pointerGesture) EndScrub(commit: true);
    }
    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageDown or Key.PageUp;
    internal void BeginScrub()
    {
        if (IsScrubbing || !IsEnabled) return;
        IsScrubbing = true;
        SynchronizePlayback();
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        UpdateExpansion();
    }
    internal void UpdateScrub(double value)
    {
        if (IsScrubbing && double.IsFinite(value)) SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }
    internal void EndScrub(bool commit)
    {
        if (!IsScrubbing) return;
        IsScrubbing = pointerGesture = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // Subscribers read the final value before restoring its playback binding.
        if (commit && IsEnabled) ScrubCommitted?.Invoke(this, EventArgs.Empty);
        else ScrubCancelled?.Invoke(this, EventArgs.Empty);
        SynchronizePlayback();
        preview.IsOpen = false; UpdateExpansion();
    }
    internal void CancelScrub() => EndScrub(commit: false);
    private void UpdatePreview()
    {
        if (!IsEnabled || !IsLoaded || !IsVisible || (!pointerWithin && !IsScrubbing)) { preview.IsOpen = false; return; }
        bool highContrast = DesktopTheme.GetIsHighContrast(this);
        preview.Background = highContrast ? SystemColors.WindowBrush : Brushes.White;
        preview.Foreground = highContrast ? SystemColors.WindowTextBrush : Brushes.Black;
        preview.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brushes.Transparent;
        preview.BorderThickness = new Thickness(highContrast ? 1 : 0);
        double seconds = IsScrubbing ? Value : DisplayValue;
        long second = (long)Math.Floor(seconds), duration = (long)Math.Floor(Maximum);
        if (previewSecond != second || previewDuration != duration)
        {
            previewSecond = second; previewDuration = duration;
            preview.Content = Clock(seconds) + " / " + Clock(Maximum);
        }
        double x = Fraction(seconds) * ActualWidth;
        if (!preview.IsMeasureValid) preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = preview.DesiredSize;
        preview.HorizontalOffset = Math.Clamp(x - size.Width / 2, 0, Math.Max(0, ActualWidth - size.Width));
        preview.VerticalOffset = -size.Height - 8;
        preview.IsOpen = true;
    }
    private static string Clock(double value)
    {
        var seconds = Math.Max(0, (long)Math.Floor(value));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new SeekPeer(this);
    private sealed class SeekPeer(SeekBar owner) : SliderAutomationPeer(owner), IRangeValueProvider
    {
        void IRangeValueProvider.SetValue(double value)
        {
            if (!owner.IsEnabled) throw new System.Windows.Automation.ElementNotEnabledException();
            if (!double.IsFinite(value) || value < owner.Minimum || value > owner.Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            owner.BeginScrub(); owner.UpdateScrub(value); owner.EndScrub(commit: true);
        }
    }
}
