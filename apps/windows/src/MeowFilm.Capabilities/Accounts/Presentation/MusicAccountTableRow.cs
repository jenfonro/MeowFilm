namespace MeowFilm.Capabilities;

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
