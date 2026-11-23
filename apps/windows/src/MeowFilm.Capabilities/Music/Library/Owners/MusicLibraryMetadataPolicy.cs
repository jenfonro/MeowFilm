using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal static readonly string[] MetadataPlatforms = ["wy", "tx", "kg", "kw", "mg"];
    private static readonly MusicMetadataPolicyRecord DefaultMetadataPolicy =
        new(0, MetadataPlatforms, MetadataPlatforms);

    private static string[] MetadataOrder(MusicMetadataPolicyRecord policy) =>
        policy.Platforms.Concat(policy.Enabled.Except(policy.Platforms, StringComparer.Ordinal)).ToArray();

    private static MusicMetadataPolicyRecord MetadataPolicy(MusicLibrarySession doc)
    {
        var value = doc.ReadMetadataPolicy() ?? DefaultMetadataPolicy;
        try { ValidateMetadataPolicy(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateMetadataPolicy(MusicMetadataPolicyRecord policy)
    {
        MusicRules.Require(policy.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(policy.Platforms.Distinct(StringComparer.Ordinal).Count() == policy.Platforms.Length &&
            policy.Platforms.All(MetadataPlatforms.Contains));
        MusicRules.Require(policy.Enabled.Distinct(StringComparer.Ordinal).Count() == policy.Enabled.Length &&
            policy.Enabled.All(MetadataPlatforms.Contains));
    }

    private (JsonElement Result, bool Mutate) MetadataPolicyCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        // Unknown CAS may already have disabled a platform. No consumer may use
        // the old in-memory policy until the single owner reconciles its receipt.
        MusicRules.Require(pending is null, "PENDING_WRITE");
        var policy = MetadataPolicy(doc);
        if (method == "music.metadata.settings.get")
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteMetadataPolicy(w, policy);
            }), false);
        }
        MusicRules.Require(method == "music.metadata.settings.save", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, "ifRevision platforms enabled");
        MusicRules.Require(MusicWireCodec.RequireIntProp(p, "ifRevision") == policy.Revision, "REVISION_CONFLICT");
        var next = new MusicMetadataPolicyRecord(
            checked(policy.Revision + 1),
            MusicWireCodec.RequireIdArrayProp(p, "platforms", 8),
            MusicWireCodec.RequireIdArrayProp(p, "enabled", 8));
        ValidateMetadataPolicy(next);
        doc.SetMetadataPolicy(next);
        ApplyArtworkPolicy(doc, force: true);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteMetadataPolicy(w, next);
        }), true);
    }

    private static bool MetadataSourceEnabled(MusicLibrarySession doc, MusicSongRecord song, string sourceKey)
    {
        var platform = song.Sources.FirstOrDefault(s => s.SourceKey == sourceKey)?.Platform;
        return platform is not null && MetadataPolicy(doc).Enabled.Contains(platform, StringComparer.Ordinal);
    }
}
