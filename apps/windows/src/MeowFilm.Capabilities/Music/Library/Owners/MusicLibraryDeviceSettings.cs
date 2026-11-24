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

    public Task<MusicDeviceSettingsRecord> GetDeviceSettingsAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            return DeviceSettings(doc);
        }, cancellationToken);

    public Task<MusicDeviceSettingsRecord> SaveDeviceSettingsAsync(
        long ifRevision,
        int volume,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.DeviceSettings, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var saved = DeviceSettings(doc);
            MusicRules.Require(ifRevision == saved.Revision, "REVISION_CONFLICT");
            MusicRules.Require(volume is >= 0 and <= 100);
            if (saved.Volume == volume) return (saved, false);
            var next = new MusicDeviceSettingsRecord(checked(saved.Revision + 1), volume);
            doc.SetDeviceSettings(next);
            return (next, true);
        }, cancellationToken);
}
