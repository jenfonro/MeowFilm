using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private JsonObject Settings(MusicLibraryData doc) => Value(doc, "online-settings") as JsonObject ??
        LJ.Obj(("schema", 3), ("revision", 0), ("lyrics", true));
    private static void ValidateSettings(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision lyrics", "schema revision lyrics");
        LJ.Require(settings["schema"] is JsonValue schema && schema.TryGetValue<int>(out var version) &&
            version == 3 && LJ.IsBool(settings["lyrics"]), "CORRUPT_STORAGE");
        _ = Revision(settings);
    }
    private (JsonObject Result, bool Mutate) SettingsCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        if (method == "music.settings.get") { LJ.Fields(p, ""); return (LJ.Obj(("settings", Settings(doc))), false); }
        LJ.Fields(p, "ifRevision lyrics", "ifRevision lyrics");
        LJ.Require(pending is null, "PENDING_WRITE"); var settings = Settings(doc);
        LJ.Require(Revision(settings) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 3), ("revision", Revision(settings) + 1), ("lyrics", p["lyrics"]));
        ValidateSettings(next);
        Set(doc, "online-settings", next); return (LJ.Obj(("settings", next)), true);
    }

}
