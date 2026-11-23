using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace MeowFilm.Desktop;

public sealed class NavigationIcon : Grid
{
    public static readonly DependencyProperty PathDataProperty = DependencyProperty.Register(nameof(PathData), typeof(string), typeof(NavigationIcon), new PropertyMetadata(null, PathDataChanged));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(NavigationIcon), new PropertyMetadata("", GlyphChanged));
    public string? PathData { get => (string?)GetValue(PathDataProperty); set => SetValue(PathDataProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    private readonly Path outline = new() { Stretch = Stretch.Uniform, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
    private readonly FontIcon symbol = new() { FontSize = 16 };
    public NavigationIcon()
    {
        Children.Add(outline); Children.Add(symbol);
        outline.UseResource(Shape.StrokeProperty, "MeowFilm.Brush.Text");
        symbol.UseResource(IconElement.ForegroundProperty, "MeowFilm.Brush.Text");
    }
    private static void PathDataChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var icon = (NavigationIcon)owner;
        bool hasPath = !string.IsNullOrEmpty(icon.PathData);
        icon.outline.Data = hasPath ? DesktopVisual.Geometry(icon.PathData!) : null;
        icon.outline.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        icon.symbol.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
    }
    private static void GlyphChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var icon = (NavigationIcon)owner;
        icon.symbol.Glyph = icon.Glyph;
    }
}
