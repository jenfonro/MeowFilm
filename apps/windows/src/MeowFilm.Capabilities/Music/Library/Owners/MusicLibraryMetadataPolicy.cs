using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string MetadataPolicyArea = "native-metadata-policy";
    internal static readonly string[] MetadataPlatforms = ["wy", "tx", "kg", "kw", "mg"];
    private static string[] MetadataOrder(JsonObject policy)
    {
        var order = LJ.Array(policy["platforms"]).Select(value => LJ.S(value)).ToArray();
        return order.Concat(LJ.Array(policy["enabled"]).Select(value => LJ.S(value)).Except(order)).ToArray();
    }
    private JsonObject MetadataPolicy(MusicLibraryData doc)
    {
        var value = Area(doc, MetadataPolicyArea) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("platforms", LJ.Arr(MetadataPlatforms)),
            ("enabled", LJ.Arr(MetadataPlatforms)));
        try { ValidateMetadataPolicy(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateMetadataPolicy(JsonObject policy)
    {
        LJ.Fields(policy, "schema revision platforms enabled");
        LJ.Require(LJ.I(policy["schema"]) == 1);
        _ = Revision(policy);
        var order = LJ.Array(policy["platforms"]).Select(value => LJ.Text(value, 8)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.Text(value, 8)).ToArray();
        LJ.Require(order.Distinct(StringComparer.Ordinal).Count() == order.Length &&
            order.All(MetadataPlatforms.Contains));
        LJ.Require(enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(MetadataPlatforms.Contains));
    }

    private (JsonObject Result, bool Mutate) MetadataPolicyCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        // Unknown CAS may already have disabled a platform. No consumer may use
        // the old in-memory policy until the single owner reconciles its receipt.
        LJ.Require(pending is null, "PENDING_WRITE");
        var policy = method == "music.metadata.settings.get" ? MetadataPolicy(doc) :
            OverwriteSettings(doc, MetadataPolicyArea, 1, LJ.Obj(("schema", 1), ("revision", 0)));
        if (method == "music.metadata.settings.get")
        {
            return (LJ.Obj(("settings", policy)), false);
        }
        LJ.Require(method == "music.metadata.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision platforms enabled");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(policy), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(policy) + 1)),
            ("platforms", p["platforms"]), ("enabled", p["enabled"]));
        ValidateMetadataPolicy(next);
        Set(doc, MetadataPolicyArea, next, partial: true);
        ApplyArtworkPolicy(doc, force: true);
        return (LJ.Obj(("settings", next)), true);
    }

    private bool MetadataSourceEnabled(MusicLibraryData doc, JsonObject song, string sourceKey)
    {
        var platform = LJ.Array(song["sources"]).OfType<JsonObject>()
            .FirstOrDefault(source => LJ.S(source["sourceKey"]) == sourceKey)?["source"];
        return platform is not null && LJ.Array(MetadataPolicy(doc)["enabled"]).Any(item => LJ.S(item) == LJ.S(platform));
    }
}
