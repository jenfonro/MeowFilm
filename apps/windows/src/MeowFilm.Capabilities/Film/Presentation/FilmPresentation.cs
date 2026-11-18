using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Capabilities;

public sealed class FilmScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double progress = value is double number ? Math.Clamp(number, 0, 1) : 0;
        double factor = parameter is string text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        return 1 + progress * factor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class FilmPlayerHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double width && double.IsFinite(width) ? Math.Max(0, width) * 9 / 16 : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
