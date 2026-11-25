namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicOnlineSettingsRecord DefaultOnlineSettings = new(0, true);

    private static MusicOnlineSettingsRecord Settings(MusicLibrarySession doc) =>
        doc.ReadOnlineSettings() ?? DefaultOnlineSettings;

    public Task<MusicOnlineSettingsRecord> GetOnlineSettingsAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(Settings, cancellationToken);
}
