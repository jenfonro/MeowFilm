using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class MusicSettingsValueConverter : IValueConverter
{
    [ThreadStatic] private static SolidColorBrush? dotOk, dotFailed, dotTransparent, dotUnsupportedOutline, dotDefault;

    public string Kind { get; set; } = "";

    public object Convert(object value, Type targetType, object parameter, string language) => Kind switch
    {
        "RestoreSourceName" => $"恢复{value}的音源",
        "SourceTone" => (value as string) switch
        {
            "normal" => ManagementStatusTone.Success,
            "abnormal" => ManagementStatusTone.Failure,
            _ => ManagementStatusTone.Neutral
        },
        "SourceDot" => (value as string) switch
        {
            "ok" => dotOk ??= new(Windows.UI.Color.FromArgb(255, 34, 163, 90)),
            "failed" => dotFailed ??= new(Windows.UI.Color.FromArgb(255, 229, 72, 77)),
            "unsupported" when parameter as string != "outline" => dotTransparent ??= new(Microsoft.UI.Colors.Transparent),
            "unsupported" => dotUnsupportedOutline ??= new(Windows.UI.Color.FromArgb(255, 206, 209, 216)),
            _ => dotDefault ??= new(Windows.UI.Color.FromArgb(255, 183, 188, 199))
        },
        "QualityName" => (value as string) switch
        {
            "auto" => "自动",
            "128k" => "128 kbps",
            "192k" => "192 kbps",
            "320k" => "320 kbps",
            "flac" => "无损",
            "flac24bit" => "高解析度",
            "master" => "母带",
            _ => value
        },
        "PlatformLogo" => new BitmapImage(new Uri(MusicPlatformInfo.LogoUri(value as string ?? ""))),
        _ => throw new InvalidOperationException("MUSIC_SETTINGS_CONVERTER_KIND")
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
