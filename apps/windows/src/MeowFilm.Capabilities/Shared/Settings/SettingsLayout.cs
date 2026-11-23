using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MeowFilm.Capabilities;

public enum SettingsLayoutMode { None, Pair }

public static class SettingsLayout
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(SettingsLayoutMode), typeof(SettingsLayout), new PropertyMetadata(SettingsLayoutMode.None, ModeChanged));
    public static SettingsLayoutMode GetMode(DependencyObject element) => (SettingsLayoutMode)element.GetValue(ModeProperty);
    public static void SetMode(DependencyObject element, SettingsLayoutMode value) => element.SetValue(ModeProperty, value);
    private static void ModeChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not FrameworkElement element) return;
        element.Loaded -= Loaded; element.Unloaded -= Unloaded; element.SizeChanged -= SizeChanged;
        if ((SettingsLayoutMode)e.NewValue == SettingsLayoutMode.None) return;
        element.Loaded += Loaded; element.Unloaded += Unloaded; element.SizeChanged += SizeChanged;
        Update(element);
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    { var element = (FrameworkElement)sender; element.SizeChanged -= SizeChanged; element.SizeChanged += SizeChanged; Update(element); }
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.IsLoaded) return;
        element.SizeChanged -= SizeChanged;
    }
    private static void SizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);
    private static void Update(FrameworkElement element)
    {
        if (GetMode(element) != SettingsLayoutMode.Pair || element is not Grid { ColumnDefinitions.Count: 3 } grid || grid.Children.Count < 2) return;
        bool stacked = element.ActualWidth < 544;
        var right = (FrameworkElement)grid.Children[1];
        int targetColumn = stacked ? 0 : 2;
        int targetRow = stacked ? 1 : 0;
        double targetGap = stacked ? 0 : 24;
        if (Grid.GetColumn(right) == targetColumn && Grid.GetRow(right) == targetRow &&
            grid.ColumnDefinitions[1].Width.Value == targetGap &&
            grid.ColumnDefinitions[2].Width.IsStar == !stacked)
            return;
        grid.ColumnDefinitions[1].Width = new(targetGap);
        grid.ColumnDefinitions[2].Width = new(stacked ? 0 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        Grid.SetColumn(right, targetColumn); Grid.SetRow(right, targetRow);
        right.Margin = new(0, stacked ? 18 : 0, 0, 0);
    }
}

public sealed class SettingsValueConverter : IValueConverter
{
    public string Kind { get; set; } = "";
    public object? Convert(object value, Type targetType, object parameter, string language) => Kind switch
    {
        "Not" => value is not true,
        "PrefixName" => $"{parameter}{value}",
        "SuffixName" => $"{value}{parameter}",
        "ImageUri" => value is string { Length: > 0 } uri ? new BitmapImage(new Uri(uri)) : null,
        _ => throw new InvalidOperationException("SETTINGS_CONVERTER_KIND")
    };
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
