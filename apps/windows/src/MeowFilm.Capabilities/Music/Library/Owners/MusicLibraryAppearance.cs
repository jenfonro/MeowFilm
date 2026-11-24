namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicAppearanceRecord DefaultAppearance =
        new(0, "grid", "starlight", "song", "lilac");

    private static MusicAppearanceRecord Appearance(MusicLibrarySession doc)
    {
        var value = doc.ReadAppearance() ?? DefaultAppearance;
        try { ValidateAppearance(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateAppearance(MusicAppearanceRecord value)
    {
        MusicRules.Require(value.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(value.PlaylistView is "grid" or "list" &&
            value.PlayerTheme == "starlight" &&
            value.ColorMode is "song" or "fixed" &&
            MusicAppearancePalette.IsValid(value.ColorPalette));
    }

    public Task<MusicAppearanceRecord> GetAppearanceAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            return Appearance(doc);
        }, cancellationToken, authoritativeFor: PendingOwner.Appearance);

    public Task<MusicAppearanceRecord> SaveAppearanceAsync(
        long ifRevision,
        string playlistView,
        string playerTheme,
        string colorMode,
        string colorPalette,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Appearance, doc =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var saved = Appearance(doc);
            MusicRules.Require(ifRevision == saved.Revision, "REVISION_CONFLICT");
            var next = new MusicAppearanceRecord(
                checked(saved.Revision + 1),
                playlistView,
                playerTheme,
                colorMode,
                colorPalette);
            ValidateAppearance(next);
            doc.SetAppearance(next);
            return (next, true);
        }, cancellationToken);
}
