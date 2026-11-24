namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicCatalogSourceRecord DefaultCatalogSource = new(0, "wy");

    private static MusicCatalogSourceRecord CatalogSource(MusicLibrarySession doc)
    {
        var value = doc.ReadCatalogSource() ?? DefaultCatalogSource;
        try { ValidateCatalogSource(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateCatalogSource(MusicCatalogSourceRecord settings)
    {
        MusicRules.Require(settings.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(settings.Platform is "wy" or "kg");
    }

    public Task<MusicCatalogSourceRecord> GetCatalogSourceAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            return CatalogSource(doc);
        }, cancellationToken, authoritativeFor: PendingOwner.CatalogSource);

    public Task<MusicCatalogSourceRecord> SaveCatalogSourceAsync(
        long ifRevision,
        string platform,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.CatalogSource, doc =>
        {
            MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
            var settings = CatalogSource(doc);
            MusicRules.Require(platform is "wy" or "kg");
            MusicRules.Require(ifRevision == settings.Revision, "REVISION_CONFLICT");
            if (settings.Platform == platform) return (settings, false);
            var next = new MusicCatalogSourceRecord(checked(settings.Revision + 1), platform);
            doc.SetCatalogSource(next);
            return (next, true);
        }, cancellationToken);
}
