namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicOnlineSettingsRecord DefaultOnlineSettings = new(0, true);

    private static MusicOnlineSettingsRecord Settings(MusicLibrarySession doc)
    {
        var value = doc.ReadOnlineSettings() ?? DefaultOnlineSettings;
        try { ValidateSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateSettings(MusicOnlineSettingsRecord settings)
    {
        MusicRules.Require(settings.Revision is >= 0 and <= MusicRules.MaxInteger, "CORRUPT_STORAGE");
    }

    public Task<MusicOnlineSettingsRecord> GetOnlineSettingsAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(Settings, cancellationToken);
}
