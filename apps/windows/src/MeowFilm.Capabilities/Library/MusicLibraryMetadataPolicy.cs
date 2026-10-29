using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string MetadataPolicyArea = "native-metadata-policy";
    internal static readonly string[] MetadataPlatforms = ["wy", "tx", "kg", "kw", "mg"];
    private JsonObject MetadataPolicy(JsonObject doc) => Value(doc, MetadataPolicyArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("platforms", LJ.Arr(MetadataPlatforms)),
            ("enabled", LJ.Arr(MetadataPlatforms)));

    private static void ValidateMetadataPolicy(JsonObject policy)
    {
        LJ.Fields(policy, "schema revision platforms enabled", "schema revision platforms enabled");
        LJ.Require(LJ.I(policy["schema"]) == 1);
        _ = Revision(policy);
        var order = LJ.Array(policy["platforms"]).Select(value => LJ.Text(value, 8)).ToArray();
        var enabled = LJ.Array(policy["enabled"]).Select(value => LJ.Text(value, 8)).ToArray();
        LJ.Require(order.Length == MetadataPlatforms.Length && order.Distinct(StringComparer.Ordinal).Count() == order.Length &&
            order.All(MetadataPlatforms.Contains));
        LJ.Require(enabled.Length <= order.Length && enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(MetadataPlatforms.Contains));
    }

    private (JsonObject Result, bool Mutate) MetadataPolicyCommand(string method, JsonObject p, JsonObject doc)
    {
        // Unknown CAS may already have disabled a platform. No consumer may use
        // the old in-memory policy until the single owner reconciles its receipt.
        LJ.Require(pending is null, "PENDING_WRITE");
        var policy = MetadataPolicy(doc);
        if (method == "music.metadata.settings.get")
        {
            LJ.Fields(p, "");
            return (LJ.Obj(("settings", policy)), false);
        }
        LJ.Require(method == "music.metadata.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision platforms enabled", "ifRevision platforms enabled");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(policy), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(policy) + 1)),
            ("platforms", p["platforms"]), ("enabled", p["enabled"]));
        ValidateMetadataPolicy(next);
        Set(doc, MetadataPolicyArea, next);
        return (LJ.Obj(("settings", next)), true);
    }

    private bool MetadataSourceEnabled(JsonObject doc, JsonObject song, string sourceKey)
    {
        var platform = LJ.Array(song["sources"]).OfType<JsonObject>()
            .FirstOrDefault(source => LJ.S(source["sourceKey"]) == sourceKey)?["source"];
        return platform is not null && LJ.Array(MetadataPolicy(doc)["enabled"]).Any(item => LJ.S(item) == LJ.S(platform));
    }
}
