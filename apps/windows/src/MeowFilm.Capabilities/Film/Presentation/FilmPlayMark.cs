using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MeowFilm.Capabilities;

/// <summary>Film card play affordance; its host centers it over the artwork and drives its opacity.</summary>
public sealed class FilmPlayMark : Grid
{
    private static readonly Geometry Triangle = Frozen(Geometry.Parse("M 20,15 L 34,25 L 20,35 Z"));
    private static readonly Brush Active = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private readonly ScaleTransform scale = new();
    private readonly Ellipse ring = new()
    {
        Margin = new Thickness(2), Stroke = Brushes.White, StrokeThickness = .8, Fill = Brushes.Transparent
    };
    private readonly System.Windows.Shapes.Path glyph = new()
    {
        Data = Triangle, Stroke = Brushes.White, StrokeThickness = .8,
        StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent
    };

    public FilmPlayMark()
    {
        Width = 50; Height = 50;
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        RenderTransformOrigin = new Point(.5, .5);
        RenderTransform = scale;
        Children.Add(ring);
        Children.Add(glyph);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Update(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Update(); }

    private void Update()
    {
        bool active = IsMouseOver;
        ring.Fill = glyph.Fill = active ? Active : Brushes.Transparent;
        scale.ScaleX = scale.ScaleY = active ? 1.1 : 1;
    }

    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}

/// <summary>Bottom-weighted artwork scrim shared by film cards; it never takes input.</summary>
public sealed class FilmHoverShade : Border
{
    private static readonly Brush Shade = CreateShade();

    public FilmHoverShade()
    {
        IsHitTestVisible = false;
        Background = Shade;
    }

    private static Brush CreateShade()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(.5, 1), EndPoint = new Point(.5, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xCC, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0x38, 0, 0, 0), .55),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
            }
        };
        brush.Freeze();
        return brush;
    }
}
