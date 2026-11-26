namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicCatalogSourceRecord DefaultCatalogSource = new(0, "wy");

    private static MusicCatalogSourceRecord CatalogSource(MusicLibrarySession doc) =>
        doc.ReadCatalogSource() ?? DefaultCatalogSource;

    public Task<MusicCatalogSourceRecord> GetCatalogSourceAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(CatalogSource, cancellationToken);

    public async Task<MusicCatalogSourceRecord> SaveCatalogSourceAsync(
        long ifRevision,
        string platform,
        CancellationToken cancellationToken = default)
    {
        bool mutated = false;
        var saved = await RunLibraryWriteAsync(doc =>
        {
            var settings = CatalogSource(doc);
            MusicRules.Require(platform is "wy" or "kg");
            MusicRules.Require(ifRevision == settings.Revision, "REVISION_CONFLICT");
            if (settings.Platform == platform) return (settings, false);
            var next = new MusicCatalogSourceRecord(checked(settings.Revision + 1), platform);
            doc.SetCatalogSource(next);
            return (next, true);
        }, cancellationToken, onCommitted: () => mutated = true).ConfigureAwait(false);
        if (mutated) InvokeSafe(CatalogSourceChanged, saved);
        return saved;
    }
}
