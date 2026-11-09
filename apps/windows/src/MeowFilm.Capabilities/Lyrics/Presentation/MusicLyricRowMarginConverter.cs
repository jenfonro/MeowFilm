using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

public sealed class MusicLyricRowMarginConverter : IMultiValueConverter
{
    internal const double ReadingAnchor = .24;
    internal static Thickness EdgeSpace(double height)
    {
        var anchor = ReadingAnchor;
        return new(0, Math.Max(24, height * anchor), 0, Math.Max(24, height * (1 - anchor)));
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 4 || values[0] is not MusicLyricRow row ||
            values[3] is not double height || !double.IsFinite(height) || height <= 0)
            return new Thickness();
        var space = EdgeSpace(height);
        return new Thickness(0, ReferenceEquals(row, values[1]) ? space.Top : 0,
            0, ReferenceEquals(row, values[2]) ? space.Bottom : 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
