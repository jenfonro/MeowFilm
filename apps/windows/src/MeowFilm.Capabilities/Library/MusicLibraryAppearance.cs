using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string AppearanceArea = "native-appearance";
    private JsonObject Appearance(JsonObject doc) => Value(doc, AppearanceArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("playlistView", "grid"),
            ("playerTheme", "starlight"), ("colorMode", "song"), ("colorPalette", "lilac"));

    private static void ValidateAppearance(JsonObject value)
    {
        LJ.Fields(value, "schema revision playlistView playerTheme colorMode colorPalette",
            "schema revision playlistView playerTheme colorMode colorPalette");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        LJ.Require(LJ.S(value["playlistView"]) is "grid" or "list" &&
            LJ.S(value["playerTheme"]) == "starlight" &&
            LJ.S(value["colorMode"]) is "song" or "fixed" &&
            MusicAppearancePalette.Options.Any(p => p.Key == LJ.S(value["colorPalette"])));
    }

    private Task<(JsonObject Result, bool Mutate)> AppearanceCommand(
        string method, JsonObject p, JsonObject doc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = Appearance(doc);
        if (method == "music.appearance.get")
        {
            LJ.Fields(p, "");
            return Task.FromResult((LJ.Obj(("settings", saved)), false));
        }
        LJ.Require(method == "music.appearance.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision playlistView playerTheme colorMode colorPalette",
            "ifRevision playlistView playerTheme colorMode colorPalette");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(saved), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(saved) + 1)),
            ("playlistView", p["playlistView"]), ("playerTheme", p["playerTheme"]),
            ("colorMode", p["colorMode"]), ("colorPalette", p["colorPalette"]));
        ValidateAppearance(next);
        Set(doc, AppearanceArea, next);
        return Task.FromResult((LJ.Obj(("settings", next)), true));
    }
}
