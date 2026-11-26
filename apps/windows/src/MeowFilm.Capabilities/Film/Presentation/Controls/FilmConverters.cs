using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Capabilities;

public sealed class FilmCurrentStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "当前" : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
