namespace MeowFilm.Capabilities.Library;

/// <summary>
/// Unified 2-tier reversible source exclusions on MusicLibraryOwner, delegating rules to
/// <see cref="MusicSourceExclusionsOwner"/>:
///   Tier 1: provider (official or script UUID) × platform
///   Tier 2: per-recording sourceKey ("recording:" + sourceKey)
/// Only an explicit restore removes a record; expiry, cache clearing and script updates never do.
/// </summary>
public sealed partial class MusicLibraryOwner
{
    internal const string OfficialProvider = MusicSourceExclusionsOwner.OfficialProvider;

    public Task<MusicSourceExclusionListResult> ListSourceExclusionsAsync(
        string? songId = null,
        MusicSourceExclusionTier? tier = null,
        CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(
            doc => MusicSourceExclusionsOwner.List(doc, songId, tier),
            cancellationToken,
            authoritativeFor: PendingOwner.SourceExclusions);

    public Task<MusicSourceExclusionChangeResult> AddSourceExclusionAsync(
        string songId,
        string provider,
        string platform,
        string providerName,
        string title,
        string artist,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(
            PendingOwner.SourceExclusions,
            doc => MusicSourceExclusionsOwner.Mutate(doc, add: true, songId, provider, platform, providerName, title, artist),
            cancellationToken);

    public Task<MusicSourceExclusionChangeResult> RemoveSourceExclusionAsync(
        string songId,
        string provider,
        string platform,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(
            PendingOwner.SourceExclusions,
            doc => MusicSourceExclusionsOwner.Mutate(doc, add: false, songId, provider, platform),
            cancellationToken);

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
        RunLibraryReadAsync(
            MusicSourceExclusionsOwner.ReadSyncSnapshot,
            token,
            authoritativeFor: PendingOwner.SourceExclusions);
}
