using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

// The originating card owns IsBusy. This view owns only the visible animation clock.
public sealed class CardPlayGlyph : Control
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy),
        typeof(bool), typeof(CardPlayGlyph), new PropertyMetadata(false, Changed));
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    private readonly RotateTransform rotation = new();
    public CardPlayGlyph()
    {
        Width = 24; Height = 28;
        RenderTransformOrigin = new(.5, .5); RenderTransform = rotation;
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SystemChanged; Sync(); };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        IsVisibleChanged += (_, _) => Sync();
    }
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs _) => ((CardPlayGlyph)value).Sync();
    private void SystemChanged(object? sender, PropertyChangedEventArgs e) => Sync();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DesktopTheme.IsHighContrastProperty) Sync();
    }
    private void Sync()
    {
        rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = 0;
        if (IsBusy && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation && !DesktopTheme.GetIsHighContrast(this))
            rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
        InvalidateVisual();
    }
    private static readonly Geometry Play = Geometry.Parse("M 4,3 L 22,14 L 4,25 Z").GetAsFrozen() as Geometry ?? Geometry.Empty;
    private static readonly Geometry Spinner = Geometry.Parse("M 12,4 A 10,10 0 1 1 2,14").GetAsFrozen() as Geometry ?? Geometry.Empty;
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (IsBusy) drawingContext.DrawGeometry(null, new Pen(Foreground, 2.5)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Spinner);
        else drawingContext.DrawGeometry(Foreground, null, Play);
    }
}
