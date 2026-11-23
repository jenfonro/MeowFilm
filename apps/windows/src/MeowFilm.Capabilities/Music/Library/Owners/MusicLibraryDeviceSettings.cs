using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicDeviceSettingsRecord DefaultDeviceSettings = new(0, 100);

    private static MusicDeviceSettingsRecord DeviceSettings(MusicLibrarySession doc)
    {
        var value = doc.ReadDeviceSettings() ?? DefaultDeviceSettings;
        try { ValidateDeviceSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateDeviceSettings(MusicDeviceSettingsRecord value)
    {
        MusicRules.Require(value.Revision is >= 0 and <= MusicRules.MaxInteger && value.Volume is >= 0 and <= 100);
    }

    private (JsonElement Result, bool Mutate) DeviceSettingsCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        MusicRules.Require(pending is null, "PENDING_WRITE");
        var saved = DeviceSettings(doc);
        if (method == "music.device.settings.get")
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteDeviceSettings(w, saved);
            }), false);
        }
        MusicRules.Require(method == "music.device.settings.save", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, "ifRevision volume");
        MusicRules.Require(MusicWireCodec.RequireIntProp(p, "ifRevision") == saved.Revision, "REVISION_CONFLICT");
        int volume = checked((int)MusicWireCodec.RequireIntProp(p, "volume", 0, 100));
        if (saved.Volume == volume)
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteDeviceSettings(w, saved);
            }), false);
        }
        var next = new MusicDeviceSettingsRecord(checked(saved.Revision + 1), volume);
        doc.SetDeviceSettings(next);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteDeviceSettings(w, next);
        }), true);
    }
}
