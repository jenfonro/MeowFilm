using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Directional transport feedback; never changes queue or playback state.</summary>
public sealed class SkipButton : Button
{
    public static readonly DependencyProperty NextProperty = DependencyProperty.Register(nameof(Next), typeof(bool),
        typeof(SkipButton), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double),
        typeof(SkipButton), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Geometry Arrow = Geometry.Parse("M0,0 L12,8 L0,16 Z");
    public bool Next { get => (bool)GetValue(NextProperty); set => SetValue(NextProperty, value); }
    public SkipButton()
    {
        Template = new ControlTemplate(typeof(Button));
        Width = Height = 36;
        Unloaded += (_, _) => ResetMotion();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) ResetMotion(); InvalidateVisual(); };
    }
    private void ResetMotion() { BeginAnimation(PhaseProperty, null); SetValue(PhaseProperty, 0d); }
    private void Animate()
    {
        ResetMotion();
        if (!IsEnabled || !SystemParameters.ClientAreaAnimation || DesktopTheme.GetIsHighContrast(this)) return;
        BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Animate(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); ResetMotion(); }
    protected override void OnClick() { Animate(); base.OnClick(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsPressedProperty || e.Property == ForegroundProperty ||
            e.Property == DesktopTheme.IsHighContrastProperty) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var brush = DesktopTheme.GetIsHighContrast(this) ? SystemColors.WindowTextBrush : Foreground;
        dc.PushOpacity(IsEnabled ? 1 : .4);
        double scale = IsPressed ? .9 : 1;
        dc.PushTransform(new ScaleTransform(scale, scale, ActualWidth / 2, ActualHeight / 2));
        dc.PushTransform(new TranslateTransform((ActualWidth - 16) / 2, (ActualHeight - 16) / 2));
        if (!Next) dc.PushTransform(new ScaleTransform(-1, 1, 8, 8));
        dc.DrawRectangle(brush, null, new Rect(13, 0, 3, 16));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 12, 16)));
        double x = (double)GetValue(PhaseProperty) * 16;
        dc.PushTransform(new TranslateTransform(x, 0)); dc.DrawGeometry(brush, null, Arrow); dc.Pop();
        dc.PushTransform(new TranslateTransform(x - 16, 0)); dc.DrawGeometry(brush, null, Arrow); dc.Pop();
        dc.Pop();
        if (!Next) dc.Pop();
        dc.Pop(); dc.Pop(); dc.Pop();
    }
}
