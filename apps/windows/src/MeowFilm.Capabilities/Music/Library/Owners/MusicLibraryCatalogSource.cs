using System.Text.Json;

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

    private (JsonElement Result, bool Mutate) CatalogSourceCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        MusicRules.Require(pending is null && recommendationUnknown is null, "PENDING_WRITE");
        var settings = CatalogSource(doc);
        if (method == "music.catalogSource.settings.get")
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteCatalogSource(w, settings);
            }), false);
        }

        MusicRules.Require(method == "music.catalogSource.settings.save", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, "ifRevision platform");
        long expected = MusicWireCodec.RequireIntProp(p, "ifRevision");
        string platform = MusicWireCodec.RequireStrProp(p, "platform");
        MusicRules.Require(platform is "wy" or "kg");
        MusicRules.Require(expected == settings.Revision, "REVISION_CONFLICT");
        if (settings.Platform == platform)
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteCatalogSource(w, settings);
            }), false);
        }

        var next = new MusicCatalogSourceRecord(checked(settings.Revision + 1), platform);
        doc.SetCatalogSource(next);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteCatalogSource(w, next);
        }), true);
    }
}
