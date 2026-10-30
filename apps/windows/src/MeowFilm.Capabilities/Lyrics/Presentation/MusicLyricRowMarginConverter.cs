using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

public sealed class MusicLyricRowMarginConverter : IMultiValueConverter
{
    internal static double ReadingAnchor(bool playing) => playing ? .24 : .35;
    internal static Thickness EdgeSpace(double height, bool playing)
    {
        var anchor = ReadingAnchor(playing);
        return new(0, Math.Max(24, height * anchor), 0, Math.Max(24, height * (1 - anchor)));
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 5 || values[0] is not MusicLyricRow row ||
            values[3] is not double height || values[4] is not bool playing || !double.IsFinite(height) || height <= 0)
            return new Thickness();
        var space = EdgeSpace(height, playing);
        return new Thickness(0, ReferenceEquals(row, values[1]) ? space.Top : 0,
            0, ReferenceEquals(row, values[2]) ? space.Bottom : 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
