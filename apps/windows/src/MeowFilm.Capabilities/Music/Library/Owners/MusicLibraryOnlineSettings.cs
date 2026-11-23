using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private JsonObject Settings(MusicLibraryData doc)
    {
        var value = Area(doc, "online-settings") ??
        LJ.Obj(("schema", 1), ("revision", 0), ("lyrics", true));
        try { ValidateSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }
    private static void ValidateSettings(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision lyrics", "schema revision lyrics");
        LJ.Require(LJ.I(settings["schema"]) == 1 && LJ.IsBool(settings["lyrics"]), "CORRUPT_STORAGE");
        _ = Revision(settings);
    }
    private (JsonObject Result, bool Mutate) SettingsCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        if (method == "music.settings.get") { return (LJ.Obj(("settings", Settings(doc))), false); }
        LJ.Fields(p, "ifRevision lyrics", "ifRevision lyrics");
        LJ.Require(pending is null, "PENDING_WRITE"); var settings = OverwriteSettings(doc, "online-settings", 1, LJ.Obj(("schema", 1), ("revision", 0)));
        LJ.Require(Revision(settings) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", Revision(settings) + 1), ("lyrics", p["lyrics"]));
        ValidateSettings(next);
        Set(doc, "online-settings", next, partial: true); return (LJ.Obj(("settings", next)), true);
    }

}
