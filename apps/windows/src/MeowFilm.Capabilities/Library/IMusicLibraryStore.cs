namespace MeowFilm.Capabilities.Library;

/// <summary>Monotonic durable revision for a music library scope.</summary>
public readonly record struct MusicLibraryRevision(long Generation)
{
    public static MusicLibraryRevision Empty => new(0);
}

public sealed record MusicSongMutation(
    MusicSongRecord Record,
    MusicSongRecord? Previous,
    string MatchKey,
    string SearchQuery);

public sealed record MusicHistoryPlaylistMutation(
    string PlaylistId,
    MusicHistoryPlaylistRecord? Previous,
    MusicHistoryPlaylistRecord? Next,
    int Ordinal,
    bool Insert = false);

public sealed record MusicHistoryDayMutation(
    string DayDate,
    MusicHistoryDayRecord? Previous,
    MusicHistoryDayRecord? Next,
    int Ordinal,
    bool Insert = false);

public sealed record MusicSourceExclusionMutation(
    string ExclusionId,
    MusicSourceExclusionRecord? Next,
    int Ordinal);

public sealed record MusicLocalLyricMutation(
    string SongId,
    MusicLocalLyricRecord? Next,
    int Ordinal);

public sealed class MusicLibraryCommit
{
    public IReadOnlyList<MusicSongMutation> Songs { get; init; } = [];
    public (MusicLibraryAreaRecord? Previous, MusicLibraryAreaRecord Next)? Library { get; init; }
    public (MusicQueueStateRecord? Previous, MusicQueueStateRecord Next)? Queue { get; init; }
    public MusicHistoryAreaRecord? HistoryArea { get; init; }
    public IReadOnlyList<MusicHistoryPlaylistMutation> HistoryPlaylists { get; init; } = [];
    public IReadOnlyList<MusicHistoryDayMutation> HistoryDays { get; init; } = [];
    public MusicPlaybackModeRecord? PlaybackMode { get; init; }
    public MusicOnlineSettingsRecord? OnlineSettings { get; init; }
    public MusicDeviceSettingsRecord? DeviceSettings { get; init; }
    public MusicAppearanceRecord? Appearance { get; init; }
    public MusicCatalogSourceRecord? CatalogSource { get; init; }
    public MusicDesktopLyricsRecord? DesktopLyrics { get; init; }
    public MusicQualityPolicyRecord? QualityPolicy { get; init; }
    public MusicOfficialSourcesRecord? OfficialSources { get; init; }
    public MusicMetadataPolicyRecord? MetadataPolicy { get; init; }
    public MusicSearchPolicyRecord? SearchPolicy { get; init; }
    public MusicSearchHistoryRecord? SearchHistory { get; init; }
    public long? SourceExclusionsRevision { get; init; }
    public MusicSourceExclusionRecord[]? ReplaceSourceExclusions { get; init; }
    public IReadOnlyList<MusicSourceExclusionMutation> SourceExclusions { get; init; } = [];
    public MusicLocalLyricRecord[]? ReplaceLocalLyrics { get; init; }
    public IReadOnlyList<MusicLocalLyricMutation> LocalLyrics { get; init; } = [];
    public MusicLibrarySyncStateRecord? LibrarySync { get; init; }

    public bool IsEmpty =>
        Songs.Count == 0 &&
        Library is null &&
        Queue is null &&
        HistoryArea is null &&
        HistoryPlaylists.Count == 0 &&
        HistoryDays.Count == 0 &&
        PlaybackMode is null &&
        OnlineSettings is null &&
        DeviceSettings is null &&
        Appearance is null &&
        CatalogSource is null &&
        DesktopLyrics is null &&
        QualityPolicy is null &&
        OfficialSources is null &&
        MetadataPolicy is null &&
        SearchPolicy is null &&
        SearchHistory is null &&
        SourceExclusionsRevision is null &&
        ReplaceSourceExclusions is null &&
        SourceExclusions.Count == 0 &&
        ReplaceLocalLyrics is null &&
        LocalLyrics.Count == 0 &&
        LibrarySync is null;
}

/// <summary>
/// Strongly-typed persistence repository for the native music owner.
/// One commit atomically writes the supplied typed songs, areas, row mutations and owner revision.
/// </summary>
public interface IMusicLibraryStore : IAsyncDisposable
{
    Task<MusicLibraryRevision> ReadRevisionAsync(string scope, CancellationToken cancellationToken = default);

    MusicLibraryAreaRecord? ReadLibrary(string scope, MusicLibraryRevision revision);
    MusicQueueStateRecord? ReadQueue(string scope, MusicLibraryRevision revision);

    bool HasHistory(string scope, MusicLibraryRevision revision);
    MusicHistoryAreaRecord? ReadHistoryArea(string scope, MusicLibraryRevision revision);
    IReadOnlyList<MusicHistoryPlaylistSummaryRecord> ReadHistoryPlaylistSummaries(string scope, MusicLibraryRevision revision, string? playlistId = null);
    MusicHistoryPlaylistRecord? ReadHistoryPlaylist(string scope, MusicLibraryRevision revision, string playlistId);
    IReadOnlyList<MusicQueueEntryRecord> ReadHistoryPlaylistEntries(string scope, MusicLibraryRevision revision, string playlistId, string? entryId = null);
    IReadOnlyList<MusicHistoryDaySummaryRecord> ReadHistoryDaySummaries(string scope, MusicLibraryRevision revision, string? dayDate = null);
    MusicHistoryDayRecord? ReadHistoryDay(string scope, MusicLibraryRevision revision, string dayDate);
    IReadOnlyList<MusicHistoryDaySongRecord> ReadHistoryDaySongs(string scope, MusicLibraryRevision revision, string dayDate, string? songId = null);

    MusicPlaybackModeRecord? ReadPlaybackMode(string scope, MusicLibraryRevision revision);
    MusicOnlineSettingsRecord? ReadOnlineSettings(string scope, MusicLibraryRevision revision);
    MusicDeviceSettingsRecord? ReadDeviceSettings(string scope, MusicLibraryRevision revision);
    MusicAppearanceRecord? ReadAppearance(string scope, MusicLibraryRevision revision);
    MusicCatalogSourceRecord? ReadCatalogSource(string scope, MusicLibraryRevision revision);
    MusicDesktopLyricsRecord? ReadDesktopLyrics(string scope, MusicLibraryRevision revision);
    MusicQualityPolicyRecord? ReadQualityPolicy(string scope, MusicLibraryRevision revision);
    MusicOfficialSourcesRecord? ReadOfficialSources(string scope, MusicLibraryRevision revision);
    MusicMetadataPolicyRecord? ReadMetadataPolicy(string scope, MusicLibraryRevision revision);
    MusicSearchPolicyRecord? ReadSearchPolicy(string scope, MusicLibraryRevision revision);
    MusicSearchHistoryRecord? ReadSearchHistory(string scope, MusicLibraryRevision revision);
    long? ReadSourceExclusionsRevision(string scope, MusicLibraryRevision revision);
    IReadOnlyList<MusicSourceExclusionRecord> ReadSourceExclusions(string scope, MusicLibraryRevision revision, string? exclusionId = null);
    IReadOnlyList<MusicLocalLyricSummaryRecord> ReadLocalLyricSummaries(string scope, MusicLibraryRevision revision, string? songId = null);
    MusicLocalLyricRecord? ReadLocalLyric(string scope, MusicLibraryRevision revision, string songId);
    MusicLibrarySyncStateRecord? ReadLibrarySync(string scope, MusicLibraryRevision revision);

    MusicSongRecord? ReadSong(string id);
    bool ContainsSong(string id);
    string? FindSource(string sourceKey);
    string? FindLocalFile(string localFileId);
    IReadOnlyList<string> FindCandidates(string matchKey, double baselineDuration);
    IReadOnlyList<string> FindArtwork(string address);
    IReadOnlyList<string> FindQuery(string query);
    IReadOnlyList<string> SongIds();

    Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected, MusicLibraryCommit commit,
        CancellationToken cancellationToken = default);
}
