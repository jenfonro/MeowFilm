using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Desktop;

public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool visible = value is true || value is string { Length: > 0 } || value is int count && count > 0;
        if (parameter as string == "Not") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
