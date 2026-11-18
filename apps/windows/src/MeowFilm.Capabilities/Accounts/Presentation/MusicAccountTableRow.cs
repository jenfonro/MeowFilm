namespace MeowFilm.Capabilities;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class MusicAccountTableRow
{
    internal MusicAccountTableRow(MusicPlatformSettingsRow source)
    {
        Source = source;
        Platform = source.Platform;
        Name = source.Name;
    }

    internal MusicAccountTableRow(string platform, string name)
    {
        Platform = platform;
        Name = name;
    }

    public MusicPlatformSettingsRow? Source { get; }
    public string Platform { get; }
    public string Name { get; }
    public bool IsPublicCatalog => Source is null;
}

public sealed class AccountAccessibleNameConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => $"{value}{parameter}";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
