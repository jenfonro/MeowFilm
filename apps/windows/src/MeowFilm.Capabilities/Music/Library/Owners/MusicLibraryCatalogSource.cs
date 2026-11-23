using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string CatalogSourceArea = "native-catalog-source";
    private JsonObject CatalogSource(MusicLibraryData doc)
    {
        var value = Area(doc, CatalogSourceArea) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("platform", "wy"));
        try { ValidateCatalogSource(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateCatalogSource(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision platform", "schema revision platform");
        LJ.Require(LJ.I(settings["schema"]) == 1);
        _ = Revision(settings);
        LJ.Require(LJ.S(settings["platform"]) is "wy" or "kg");
    }

    private (JsonObject Result, bool Mutate) CatalogSourceCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        LJ.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var settings = method == "music.catalogSource.settings.get" ? CatalogSource(doc) :
            OverwriteSettings(doc, CatalogSourceArea, 1, LJ.Obj(("schema", 1), ("revision", 0), ("platform", "wy")));
        if (method == "music.catalogSource.settings.get")
        {
            return (LJ.Obj(("settings", settings)), false);
        }

        LJ.Require(method == "music.catalogSource.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision platform", "ifRevision platform");
        long expected = LJ.I(p["ifRevision"]);
        string platform = LJ.S(p["platform"]);
        LJ.Require(platform is "wy" or "kg");
        LJ.Require(expected == Revision(settings), "REVISION_CONFLICT");
        if (doc.Select(CatalogSourceArea, "schema") is null && JsonNode.DeepEquals(settings["platform"], JsonValue.Create(platform)) ||
            doc.Matches(CatalogSourceArea, [], [doc.Scope], LJ.Obj(("platform", platform)))) return (LJ.Obj(("settings", LJ.Merge(settings, LJ.Obj(("platform", platform))))), false);

        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(settings) + 1)), ("platform", platform));
        Set(doc, CatalogSourceArea, next, partial: true);
        return (LJ.Obj(("settings", next)), true);
    }
}
