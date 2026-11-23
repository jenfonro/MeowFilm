using System.Text.Json;

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

    private Task<(JsonElement Result, bool Mutate)> AppearanceCommand(
        string method, JsonElement p, MusicLibrarySession doc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        MusicRules.Require(pending is null, "PENDING_WRITE");
        var saved = Appearance(doc);
        if (method == "music.appearance.get")
        {
            return Task.FromResult((MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteAppearance(w, saved);
            }), false));
        }
        MusicRules.Require(method == "music.appearance.save", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, "ifRevision playlistView playerTheme colorMode colorPalette");
        MusicRules.Require(MusicWireCodec.RequireIntProp(p, "ifRevision") == saved.Revision, "REVISION_CONFLICT");
        var next = new MusicAppearanceRecord(
            checked(saved.Revision + 1),
            MusicWireCodec.RequireStrProp(p, "playlistView"),
            MusicWireCodec.RequireStrProp(p, "playerTheme"),
            MusicWireCodec.RequireStrProp(p, "colorMode"),
            MusicWireCodec.RequireStrProp(p, "colorPalette"));
        ValidateAppearance(next);
        doc.SetAppearance(next);
        return Task.FromResult((MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteAppearance(w, next);
        }), true));
    }
}
