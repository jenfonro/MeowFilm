using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string CatalogSourceArea = "native-catalog-source";
    private JsonObject CatalogSource(JsonObject doc) => Value(doc, CatalogSourceArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("platform", "wy"));

    private static void ValidateCatalogSource(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision platform", "schema revision platform");
        LJ.Require(LJ.I(settings["schema"]) == 1);
        _ = Revision(settings);
        LJ.Require(LJ.S(settings["platform"]) is "wy" or "kg");
    }

    private (JsonObject Result, bool Mutate) CatalogSourceCommand(string method, JsonObject p, JsonObject doc)
    {
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var settings = CatalogSource(doc);
        if (method == "music.catalogSource.settings.get")
        {
            LJ.Fields(p, "");
            return (LJ.Obj(("settings", settings)), false);
        }

        LJ.Require(method == "music.catalogSource.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision platform", "ifRevision platform");
        long expected = LJ.I(p["ifRevision"]);
        string platform = LJ.S(p["platform"]);
        LJ.Require(platform is "wy" or "kg");
        LJ.Require(expected == Revision(settings), "REVISION_CONFLICT");
        if (platform == LJ.S(settings["platform"])) return (LJ.Obj(("settings", settings)), false);

        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(settings) + 1)), ("platform", platform));
        Set(doc, CatalogSourceArea, next);
        return (LJ.Obj(("settings", next)), true);
    }
}
