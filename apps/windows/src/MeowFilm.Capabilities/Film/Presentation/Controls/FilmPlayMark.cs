using MeowFilm.Desktop;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace MeowFilm.Capabilities;

/// <summary>Film card play affordance, in the same composition tree as the artwork.</summary>
public sealed class FilmPlayMark : Grid
{
    public FilmPlayMark()
    {
        Width = Height = 50;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        var scale = new ScaleTransform();
        RenderTransform = scale; RenderTransformOrigin = new Point(.5, .5);
        var ring = new Ellipse { Margin = new Thickness(2), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = .8 };
        var glyph = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = DesktopVisual.Geometry("M20,15 L34,25 L20,35 Z"),
            Stroke = new SolidColorBrush(Colors.White), StrokeThickness = .8
        };
        Children.Add(ring); Children.Add(glyph);
        void Update(bool active)
        {
            ring.Fill = glyph.Fill = new SolidColorBrush(active ? Color.FromArgb(255, 34, 197, 94) : Colors.Transparent);
            scale.ScaleX = scale.ScaleY = active ? 1.1 : 1;
        }
        PointerEntered += (_, _) => Update(true);
        PointerExited += (_, _) => Update(false);
        Update(false);
    }
}

public sealed class FilmHoverShade : Grid
{
    public FilmHoverShade()
    {
        IsHitTestVisible = false;
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(.5, 1), EndPoint = new Point(.5, 0),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0xCC, 0, 0, 0), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x38, 0, 0, 0), Offset = .55 },
                new GradientStop { Color = Colors.Transparent, Offset = 1 }
            }
        };
    }
}
