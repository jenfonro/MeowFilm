namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicDeviceSettingsRecord DefaultDeviceSettings = new(0, 100);

    private static MusicDeviceSettingsRecord DeviceSettings(MusicLibrarySession doc) =>
        doc.ReadDeviceSettings() ?? DefaultDeviceSettings;

    public Task<MusicDeviceSettingsRecord> GetDeviceSettingsAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(DeviceSettings, cancellationToken);

    public Task<MusicDeviceSettingsRecord> SaveDeviceSettingsAsync(
        long ifRevision,
        int volume,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            var saved = DeviceSettings(doc);
            MusicRules.Require(ifRevision == saved.Revision, "REVISION_CONFLICT");
            MusicRules.Require(volume is >= 0 and <= 100);
            if (saved.Volume == volume) return (saved, false);
            var next = new MusicDeviceSettingsRecord(checked(saved.Revision + 1), volume);
            doc.SetDeviceSettings(next);
            return (next, true);
        }, cancellationToken);
}
