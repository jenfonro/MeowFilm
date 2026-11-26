namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal static readonly string[] MetadataPlatforms = ["wy", "tx", "kg", "kw", "mg"];
    private static readonly MusicMetadataPolicyRecord DefaultMetadataPolicy =
        new(0, MetadataPlatforms, MetadataPlatforms);

    private static string[] MetadataOrder(MusicMetadataPolicyRecord policy) =>
        policy.Platforms.Concat(policy.Enabled.Except(policy.Platforms, StringComparer.Ordinal)).ToArray();

    private static MusicMetadataPolicyRecord MetadataPolicy(MusicLibrarySession doc) =>
        doc.ReadMetadataPolicy() ?? DefaultMetadataPolicy;

    private static void ValidateMetadataPolicy(MusicMetadataPolicyRecord policy)
    {
        MusicRules.Require(policy.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(policy.Platforms.Distinct(StringComparer.Ordinal).Count() == policy.Platforms.Length &&
            policy.Platforms.All(MetadataPlatforms.Contains));
        MusicRules.Require(policy.Enabled.Distinct(StringComparer.Ordinal).Count() == policy.Enabled.Length &&
            policy.Enabled.All(MetadataPlatforms.Contains));
    }

    public Task<MusicMetadataPolicyRecord> GetMetadataPolicyAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(MetadataPolicy, cancellationToken);

    public async Task<MusicMetadataPolicyRecord> SaveMetadataPolicyAsync(
        long ifRevision,
        string[] platforms,
        string[] enabled,
        CancellationToken cancellationToken = default)
    {
        bool mutated = false;
        var saved = await RunLibraryWriteAsync(doc =>
        {
            var policy = MetadataPolicy(doc);
            MusicRules.Require(ifRevision == policy.Revision, "REVISION_CONFLICT");
            var next = new MusicMetadataPolicyRecord(
                checked(policy.Revision + 1),
                platforms.ToArray(),
                enabled.ToArray());
            ValidateMetadataPolicy(next);
            doc.SetMetadataPolicy(next);
            ApplyArtworkPolicy(doc, force: true);
            return (next, true);
        }, cancellationToken, onCommitted: () => mutated = true).ConfigureAwait(false);
        if (mutated) InvokeSafe(MetadataPolicyChanged, saved);
        return saved;
    }

    private static bool MetadataSourceEnabled(MusicLibrarySession doc, MusicSongRecord song, string sourceKey)
    {
        var platform = song.Sources.FirstOrDefault(s => s.SourceKey == sourceKey)?.Platform;
        return platform is not null && MetadataPolicy(doc).Enabled.Contains(platform, StringComparer.Ordinal);
    }
}
