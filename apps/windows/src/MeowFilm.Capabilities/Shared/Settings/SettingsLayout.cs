using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Desktop;

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
    private static void Unloaded(object sender, RoutedEventArgs e) => ((FrameworkElement)sender).SizeChanged -= SizeChanged;
    private static void SizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);
    private static void Update(FrameworkElement element)
    {
        if (GetMode(element) != SettingsLayoutMode.Pair || element is not Grid { ColumnDefinitions.Count: 3 } grid || grid.Children.Count < 2) return;
        bool stacked = element.ActualWidth < 544;
        grid.ColumnDefinitions[1].Width = new(stacked ? 0 : 24);
        grid.ColumnDefinitions[2].Width = new(stacked ? 0 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        var right = (FrameworkElement)grid.Children[1];
        Grid.SetColumn(right, stacked ? 0 : 2); Grid.SetRow(right, stacked ? 1 : 0);
        right.Margin = new(0, stacked ? 18 : 0, 0, 0);
    }
}

public sealed class SettingsValueConverter : IValueConverter
{
    public string Kind { get; set; } = "";
    public object Convert(object value, Type targetType, object parameter, string language) => Kind switch
    {
        "Not" => value is not true,
        "PrefixName" => $"{parameter}{value}",
        "RestoreSourceName" => $"恢复{value}的音源",
        "SourceTone" => (value as string) switch
        {
            "normal" => ManagementStatusTone.Success,
            "abnormal" => ManagementStatusTone.Failure,
            _ => ManagementStatusTone.Neutral
        },
        "SourceDot" => new SolidColorBrush((value as string) switch
        {
            "ok" => Windows.UI.Color.FromArgb(255, 34, 163, 90),
            "failed" => Windows.UI.Color.FromArgb(255, 229, 72, 77),
            "unsupported" when parameter as string != "outline" => Microsoft.UI.Colors.Transparent,
            "unsupported" => Windows.UI.Color.FromArgb(255, 206, 209, 216),
            _ => Windows.UI.Color.FromArgb(255, 183, 188, 199)
        }),
        "QualityName" => (value as string) switch { "auto" => "自动", "128k" => "128 kbps", "192k" => "192 kbps", "320k" => "320 kbps", "flac" => "无损", "flac24bit" => "高解析度", "master" => "母带", _ => value },
        "PlatformLogo" => new BitmapImage(new Uri("ms-appx:///MeowFilm.Capabilities/Music/Accounts/Presentation/Assets/" + ((value as string) switch
        { "tx" => "qq-music.png", "kg" => "kugou-music.png", "kw" => "kuwo-music.png", "mg" => "migu-music.png", _ => "netease-music.png" }))),
        _ => throw new InvalidOperationException("SETTINGS_CONVERTER_KIND")
    };
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
