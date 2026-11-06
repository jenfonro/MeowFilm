using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private JsonObject Settings(JsonObject doc) => Value(doc, "online-settings") as JsonObject ??
        LJ.Obj(("schema", 3), ("revision", 0), ("lyrics", true));
    private static void ValidateSettings(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision lyrics", "schema revision lyrics");
        LJ.Require(LJ.I(settings["schema"]) == 3 && LJ.IsBool(settings["lyrics"]), "CORRUPT_STORAGE");
        _ = Revision(settings);
    }
    private (JsonObject Result, bool Mutate) SettingsCommand(string method, JsonObject p, JsonObject doc)
    {
        if (method == "music.settings.get") { LJ.Fields(p, ""); return (LJ.Obj(("settings", Settings(doc))), false); }
        LJ.Fields(p, "ifRevision lyrics", "ifRevision lyrics");
        LJ.Require(pending is null, "PENDING_WRITE"); var settings = Settings(doc);
        LJ.Require(Revision(settings) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 3), ("revision", Revision(settings) + 1), ("lyrics", p["lyrics"]));
        ValidateSettings(next);
        Set(doc, "online-settings", next); return (LJ.Obj(("settings", next)), true);
    }

    /// <summary>
    /// Script installations and their private data moved to the shared music source module.
    /// Earlier native entries are neither read, migrated nor deleted; they are carried
    /// through whole-document writes unchanged so playlists and settings stay readable.
    /// </summary>
    private bool IsRetiredArea(string encoded)
    {
        if (encoded == Key("lx-installations")) return true;
        try
        {
            return JsonNode.Parse(encoded) is JsonArray { Count: 5 } key && LJ.S(key[0]) == "meowfilm" &&
                LJ.S(key[1]) == "music" && LJ.N(key[2]) == 1 && LJ.S(key[3]) == scope &&
                JsonNode.Parse(LJ.S(key[4])) is JsonArray { Count: 3 } area && LJ.S(area[0]) == "lx-data";
        }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
