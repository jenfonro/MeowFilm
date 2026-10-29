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
/// independent of the 2/5 DIP rail; no invisible thumb or parent border splits it.
/// A gesture is a transaction, never a stream of media seeks.
/// </summary>
public sealed class SeekBar : Slider
{
    public static readonly DependencyProperty ChorusSecondsProperty = DependencyProperty.Register(
        nameof(ChorusSeconds), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(
        nameof(PlayedBrush), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RailAtTopProperty = DependencyProperty.Register(
        nameof(RailAtTop), typeof(bool), typeof(SeekBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ExpansionProperty = DependencyProperty.Register(
        "Expansion", typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly ToolTip preview = new()
    {
        Placement = PlacementMode.Relative, StaysOpen = true,
        Background = Brushes.Black, Foreground = Brushes.White,
        BorderThickness = new Thickness(0), Padding = new Thickness(10, 5, 10, 5),
        IsHitTestVisible = false
    };
    private bool pointerGesture;
    private double pointerX;
    public double ChorusSeconds { get => (double)GetValue(ChorusSecondsProperty); set => SetValue(ChorusSecondsProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public bool RailAtTop { get => (bool)GetValue(RailAtTopProperty); set => SetValue(RailAtTopProperty, value); }
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
        Loaded += (_, _) => preview.Style = TryFindResource("Capability.SeekPreview") as Style;
        Unloaded += (_, _) => { CancelScrub(); preview.IsOpen = false; BeginAnimation(ExpansionProperty, null); };
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
        Brush rail = highContrast ? SystemColors.WindowTextBrush : NormalRail;
        double thickness = 2 + expansion * 3, y = RailAtTop ? 1 : height / 2;
        double end = Fraction(Value) * width;
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
            double radius = 4 * expansion;
            dc.DrawEllipse(highContrast ? SystemColors.HighlightBrush : active,
                null, new(Math.Clamp(end, radius, Math.Max(radius, width - radius)), y), radius, radius);
        }
    }

    private static readonly Brush NormalActive = Frozen("#FC3B5A"), NormalRail = Frozen("#0D000000");
    private static Brush Frozen(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    private double Fraction(double value) => Maximum <= Minimum ? 0 : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
    internal double ValueAt(double x) => Minimum + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (Maximum - Minimum);

    protected override void OnValueChanged(double oldValue, double newValue)
    { base.OnValueChanged(oldValue, newValue); InvalidateVisual(); if (preview.IsOpen) UpdatePreview(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty ||
            e.Property == MaximumProperty || e.Property == MinimumProperty) InvalidateVisual();
        if (e.Property == IsKeyboardFocusWithinProperty || e.Property == DesktopTheme.IsHighContrastProperty) UpdateExpansion();
    }
    private void UpdateExpansion()
    {
        double target = IsEnabled && (IsMouseOver || IsKeyboardFocusWithin || IsScrubbing) ? 1 : 0;
        if (!SystemParameters.ClientAreaAnimation || DesktopTheme.GetIsHighContrast(this))
        { BeginAnimation(ExpansionProperty, null); SetValue(ExpansionProperty, target); }
        else BeginAnimation(ExpansionProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        if (!IsEnabled) preview.IsOpen = false;
    }
    protected override void OnMouseEnter(MouseEventArgs e)
    { base.OnMouseEnter(e); pointerX = e.GetPosition(this).X; UpdateExpansion(); UpdatePreview(); }
    protected override void OnMouseLeave(MouseEventArgs e)
    { base.OnMouseLeave(e); if (!IsScrubbing) preview.IsOpen = false; UpdateExpansion(); }
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
        preview.IsOpen = false; UpdateExpansion();
    }
    internal void CancelScrub() => EndScrub(commit: false);
    private void UpdatePreview()
    {
        if (!IsEnabled || !IsLoaded || (!IsMouseOver && !IsScrubbing)) { preview.IsOpen = false; return; }
        bool highContrast = DesktopTheme.GetIsHighContrast(this);
        preview.Background = highContrast ? SystemColors.WindowBrush : Brushes.Black;
        preview.Foreground = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
        preview.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brushes.Transparent;
        preview.BorderThickness = new Thickness(highContrast ? 1 : 0);
        double seconds = IsScrubbing ? Value : ValueAt(pointerX);
        preview.Content = Clock(seconds) + " / " + Clock(Maximum);
        double x = IsScrubbing ? Fraction(Value) * ActualWidth : pointerX;
        preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
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
