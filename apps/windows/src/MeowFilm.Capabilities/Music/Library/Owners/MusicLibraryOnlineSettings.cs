using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicOnlineSettingsRecord DefaultOnlineSettings = new(0, true);

    private static MusicOnlineSettingsRecord Settings(MusicLibrarySession doc)
    {
        var value = doc.ReadOnlineSettings() ?? DefaultOnlineSettings;
        try { ValidateSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateSettings(MusicOnlineSettingsRecord settings)
    {
        MusicRules.Require(settings.Revision is >= 0 and <= MusicRules.MaxInteger, "CORRUPT_STORAGE");
    }

    private (JsonElement Result, bool Mutate) SettingsCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        if (method == "music.settings.get")
        {
            var current = Settings(doc);
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteOnlineSettings(w, current);
            }), false);
        }
        MusicWireCodec.RequireFields(p, "ifRevision lyrics");
        MusicRules.Require(pending is null, "PENDING_WRITE");
        var settings = Settings(doc);
        MusicRules.Require(settings.Revision == MusicWireCodec.RequireIntProp(p, "ifRevision"), "REVISION_CONFLICT");
        var next = new MusicOnlineSettingsRecord(
            checked(settings.Revision + 1),
            MusicWireCodec.RequireBoolProp(p, "lyrics"));
        ValidateSettings(next);
        doc.SetOnlineSettings(next);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteOnlineSettings(w, next);
        }), true);
    }
}
