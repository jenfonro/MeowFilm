using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string AppearanceArea = "native-appearance";
    private JsonObject Appearance(MusicLibraryData doc)
    {
        var value = Area(doc, AppearanceArea) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("playlistView", "grid"),
            ("playerTheme", "starlight"), ("colorMode", "song"), ("colorPalette", "lilac"));
        try { ValidateAppearance(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateAppearance(JsonObject value)
    {
        LJ.Fields(value, "schema revision playlistView playerTheme colorMode colorPalette");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        LJ.Require(LJ.S(value["playlistView"]) is "grid" or "list" &&
            LJ.S(value["playerTheme"]) == "starlight" &&
            LJ.S(value["colorMode"]) is "song" or "fixed" &&
            MusicAppearancePalette.IsValid(LJ.S(value["colorPalette"])));
    }

    private (JsonObject Result, bool Mutate) AppearanceCommand(
        string method, JsonObject p, MusicLibraryData doc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = method == "music.appearance.get" ? Appearance(doc) :
            OverwriteSettings(doc, AppearanceArea, 1, LJ.Obj(("schema", 1), ("revision", 0)));
        if (method == "music.appearance.get")
        {
            return (LJ.Obj(("settings", saved)), false);
        }
        LJ.Require(method == "music.appearance.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision playlistView playerTheme colorMode colorPalette");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(saved), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(saved) + 1)),
            ("playlistView", p["playlistView"]), ("playerTheme", p["playerTheme"]),
            ("colorMode", p["colorMode"]), ("colorPalette", p["colorPalette"]));
        ValidateAppearance(next);
        Set(doc, AppearanceArea, next, partial: true);
        return (LJ.Obj(("settings", next)), true);
    }
}
