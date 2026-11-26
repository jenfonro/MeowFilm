namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    internal const string OfficialProvider = MusicSourceExclusionsOwner.OfficialProvider;

    public Task<MusicSourceExclusionListResult> ListSourceExclusionsAsync(
        string? songId = null,
        MusicSourceExclusionTier? tier = null,
        CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc => MusicSourceExclusionsOwner.List(doc, songId, tier), cancellationToken);

    public async Task<MusicSourceExclusionChangeResult> AddSourceExclusionAsync(
        string songId,
        string provider,
        string platform,
        string providerName,
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        var result = await RunLibraryWriteAsync(
            doc => MusicSourceExclusionsOwner.Mutate(doc, add: true, songId, provider, platform, providerName, title, artist),
            cancellationToken).ConfigureAwait(false);
        if (result.Changed) InvokeSafe(SourceExclusionsChanged);
        return result;
    }

    public async Task<MusicSourceExclusionChangeResult> RemoveSourceExclusionAsync(
        string songId,
        string provider,
        string platform,
        CancellationToken cancellationToken = default)
    {
        var result = await RunLibraryWriteAsync(
            doc => MusicSourceExclusionsOwner.Mutate(doc, add: false, songId, provider, platform),
            cancellationToken).ConfigureAwait(false);
        if (result.Changed) InvokeSafe(SourceExclusionsChanged);
        return result;
    }

    public Task<MusicSourceExclusionChangeResult> AddRecordingExclusionAsync(
        string songId,
        string sourceKey,
        string platform,
        string providerName,
        string title,
        string artist,
        CancellationToken cancellationToken = default) =>
        AddSourceExclusionAsync(
            songId,
            MusicSourceExclusionsOwner.MakeRecordingProvider(sourceKey),
            platform,
            providerName,
            title,
            artist,
            cancellationToken);

    public Task<MusicSourceExclusionChangeResult> RemoveRecordingExclusionAsync(
        string songId,
        string sourceKey,
        string platform,
        CancellationToken cancellationToken = default) =>
        RemoveSourceExclusionAsync(
            songId,
            MusicSourceExclusionsOwner.MakeRecordingProvider(sourceKey),
            platform,
            cancellationToken);

    internal Task<(HashSet<(string SongId, string Provider, string Platform)> ProviderPlatforms,
        HashSet<(string SongId, string SourceKey)> RecordingKeys)> ReadSyncExclusionSnapshotAsync(CancellationToken token) =>
        RunLibraryReadAsync(MusicSourceExclusionsOwner.ReadSyncSnapshot, token);
}
