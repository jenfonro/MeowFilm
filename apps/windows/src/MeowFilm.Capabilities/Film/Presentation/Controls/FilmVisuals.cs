using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

internal static class FilmVisuals
{
    internal static Geometry Geometry(string path) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
}

public sealed class FilmVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is true || value is string { Length: > 0 };
        if (parameter as string == "Not") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed class FilmEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null or 0 or "" ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FilmCurrentStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "当前" : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class FilmActionNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => $"{parameter} {value}";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
