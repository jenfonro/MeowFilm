using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public sealed partial class NavigationIcon : Grid
{
    public static readonly DependencyProperty PathDataProperty = DependencyProperty.Register(nameof(PathData), typeof(string), typeof(NavigationIcon), new PropertyMetadata(null, PathDataChanged));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(NavigationIcon), new PropertyMetadata("", GlyphChanged));
    public string? PathData { get => (string?)GetValue(PathDataProperty); set => SetValue(PathDataProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public NavigationIcon() => InitializeComponent();

    private static void PathDataChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var icon = (NavigationIcon)owner;
        bool hasPath = !string.IsNullOrEmpty(icon.PathData);
        icon.Outline.Data = hasPath ? DesktopVisual.Geometry(icon.PathData!) : null;
        icon.Outline.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        icon.Symbol.Visibility = hasPath ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void GlyphChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var icon = (NavigationIcon)owner;
        icon.Symbol.Glyph = icon.Glyph;
    }
}
