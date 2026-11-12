using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

public sealed class MusicLyricRowMarginConverter : IMultiValueConverter
{
    internal const double ReadingAnchor = .5;
    internal static Thickness EdgeSpace(double height, double firstHeight, double lastHeight)
    {
        return new(0, Math.Max(0, height * ReadingAnchor - firstHeight / 2),
            0, Math.Max(0, height * (1 - ReadingAnchor) - lastHeight / 2));
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 5 || values[0] is not MusicLyricRow row ||
            values[3] is not double height || !double.IsFinite(height) || height <= 0 ||
            values[4] is not double rowHeight || !double.IsFinite(rowHeight))
            return new Thickness();
        var space = EdgeSpace(height, rowHeight, rowHeight);
        return new Thickness(0, ReferenceEquals(row, values[1]) ? space.Top : 0,
            0, ReferenceEquals(row, values[2]) ? space.Bottom : 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
