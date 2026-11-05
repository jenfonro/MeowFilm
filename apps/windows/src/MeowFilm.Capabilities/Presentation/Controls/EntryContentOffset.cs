using System.Globalization;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

// The revealed rows occupy the space lifted out of the footer, so their bottom
// stays at the same inset for every card width and caption line count.
public sealed class EntryContentOffset : IMultiValueConverter
{
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture) =>
        values.Length == 3 && values[0] is double content && values[1] is double caption && values[2] is double progress
            ? -Math.Max(0, content - caption) * progress : 0d;
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
