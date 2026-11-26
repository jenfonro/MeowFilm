namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicAppearanceRecord DefaultAppearance =
        new(0, "grid", "starlight", "song", "lilac");

    private static MusicAppearanceRecord Appearance(MusicLibrarySession doc) =>
        doc.ReadAppearance() ?? DefaultAppearance;

    private static void ValidateAppearance(MusicAppearanceRecord value)
    {
        MusicRules.Require(value.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(value.PlaylistView is "grid" or "list" &&
            value.PlayerTheme == "starlight" &&
            value.ColorMode is "song" or "fixed" &&
            MusicAppearancePalette.IsValid(value.ColorPalette));
    }

    public Task<MusicAppearanceRecord> GetAppearanceAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(Appearance, cancellationToken);

    public async Task<MusicAppearanceRecord> SaveAppearanceAsync(
        long ifRevision,
        string playlistView,
        string playerTheme,
        string colorMode,
        string colorPalette,
        CancellationToken cancellationToken = default)
    {
        bool mutated = false;
        var saved = await RunLibraryWriteAsync(doc =>
        {
            var current = Appearance(doc);
            MusicRules.Require(ifRevision == current.Revision, "REVISION_CONFLICT");
            var next = new MusicAppearanceRecord(
                checked(current.Revision + 1),
                playlistView,
                playerTheme,
                colorMode,
                colorPalette);
            ValidateAppearance(next);
            doc.SetAppearance(next);
            return (next, true);
        }, cancellationToken, onCommitted: () => mutated = true).ConfigureAwait(false);
        if (mutated) InvokeSafe(AppearanceChanged, saved);
        return saved;
    }
}
