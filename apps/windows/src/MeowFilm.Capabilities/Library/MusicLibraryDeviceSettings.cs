using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string DeviceSettingsArea = "native-device-settings";
    private JsonObject DeviceSettings(JsonObject doc)
    {
        var value = Area(doc, DeviceSettingsArea) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("volume", 100));
        try { ValidateDeviceSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }
    private static void ValidateDeviceSettings(JsonObject value)
    {
        LJ.Fields(value, "schema revision volume", "schema revision volume");
        LJ.Require(LJ.I(value["schema"]) == 1); _ = Revision(value); _ = LJ.I(value["volume"], 0, 100);
    }
    private (JsonObject Result, bool Mutate) DeviceSettingsCommand(string method, JsonObject p, JsonObject doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = DeviceSettings(doc);
        if (method == "music.device.settings.get") { return (LJ.Obj(("settings", saved)), false); }
        LJ.Require(method == "music.device.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision volume", "ifRevision volume");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(saved), "REVISION_CONFLICT");
        int volume = checked((int)LJ.I(p["volume"], 0, 100));
        if (volume == LJ.I(saved["volume"])) return (LJ.Obj(("settings", saved)), false);
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(saved) + 1)), ("volume", volume));
        Set(doc, DeviceSettingsArea, next);
        return (LJ.Obj(("settings", next)), true);
    }
}
