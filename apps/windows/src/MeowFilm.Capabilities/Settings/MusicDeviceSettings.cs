using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private long deviceSettingsRevision, volumeEdit;
    private int savedVolume = 100;
    private bool deviceSettingsLoaded;
    private void AcceptDeviceSettings(JsonElement reply)
    {
        var value = reply.GetProperty("settings");
        deviceSettingsRevision = MusicDto.Integer(value, "revision");
        savedVolume = checked((int)MusicDto.Integer(value, "volume"));
        deviceSettingsLoaded = true;
    }
    private async Task ReadDeviceSettings(CancellationToken token)
    {
        long edit = volumeEdit;
        var reply = await QualityCall("music.device.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        AcceptDeviceSettings(reply);
        if (edit == volumeEdit && (playback.OwnerId is null || playback.OwnerId == Id))
        { playback.Volume = savedVolume; Changed(nameof(Volume)); }
    }
    public Task SaveVolumeAsync() => RunAsync(() => SaveVolumeCore(lifetime.Token));
    private async Task SaveVolumeCore(CancellationToken token)
    {
        if (!deviceSettingsLoaded || volumeEdit == 0 || savedVolume == playback.Volume) return;
        // Runs in the existing command lane (or after its shutdown drain). Not per slider tick.
        AcceptDeviceSettings(await QualityCall("music.device.settings.save",
            new { ifRevision = deviceSettingsRevision, volume = playback.Volume }, token));
    }
}
