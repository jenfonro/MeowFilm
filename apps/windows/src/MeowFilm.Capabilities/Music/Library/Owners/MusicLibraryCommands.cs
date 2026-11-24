namespace MeowFilm.Capabilities.Library;

public sealed record MusicQualitySettingsResult(MusicQualityPolicyRecord Settings, string Signature);

public sealed record MusicOfficialCheckResult(
    string Platform,
    string State,
    string? Code,
    string Quality,
    string Sample);

public sealed record MusicOfficialCandidateItem(
    string Platform,
    string SourceKey,
    string Scope,
    long Generation);

public sealed record MusicOfficialCandidatesResult(
    MusicSongRecord Song,
    MusicOfficialCandidateItem[] Candidates,
    string QualityPolicySignature,
    bool AuthorizationRequired);

public sealed record MusicOfficialCacheContextResult(
    string Platform,
    string SourceKey,
    string SongId,
    string Scope,
    long Generation,
    string QualityCacheSignature,
    string QualityPolicySignature);

public sealed record MusicSongCompletionFailure(string Platform, string Code);

public sealed record MusicSongCompletionResult(MusicSongRecord Song, MusicSongCompletionFailure[] Failures);

// ---- B3: Library Collections & Network Playlists ----

public sealed record MusicCollectionSummaryRecord(
    string Id,
    string Name,
    string Kind,
    long Revision,
    int Count,
    bool ManualOrder = false,
    string Platform = "",
    string RemoteId = "",
    string Cover = "")
{
    internal static MusicCollectionSummaryRecord FromCollection(MusicCollectionRecord c) =>
        new(c.CollectionId, c.Name, c.Kind, c.Revision, c.Order.Length, c.ManualOrder);

    internal static MusicCollectionSummaryRecord FromNetworkPlaylist(MusicNetworkPlaylistRecord r) =>
        new(r.PlaylistId, r.Name, "network", r.Revision, checked((int)r.SongCount), false, r.Platform, r.RemoteId, r.Cover);
}

public sealed record MusicCollectionsPage(
    MusicCollectionSummaryRecord[] Items,
    int Total,
    long Offset,
    int Limit,
    long Revision);

public sealed record MusicCollectionDetail(
    MusicCollectionSummaryRecord Collection,
    MusicSongRecord[] Songs,
    int Total,
    long Offset,
    int Limit);

public sealed record MusicCollectionMembership(bool Present, string? SongId, long Revision);

public sealed record MusicLibrarySearchPage(
    MusicSongRecord[] Items,
    int Total,
    long Offset,
    int Limit,
    long Revision);

public sealed record MusicNetworkPlaylistSyncStatus(
    bool Ok,
    string State = "",
    string Code = "",
    string Phase = "",
    string? FirstIssueCode = null);

public sealed record MusicNetworkPlaylistMutationResult(
    MusicCollectionSummaryRecord Collection,
    bool Present,
    bool LocalSaved,
    MusicNetworkPlaylistSyncStatus Sync);

// ---- B4: Queue & History ----

public interface IMusicQueuePosition
{
    string? CurrentId { get; }
    long Version { get; }
    int Mode { get; }
}

public sealed record MusicQueuePositionResult(string? CurrentId, long Version, int Mode) : IMusicQueuePosition;

public sealed record MusicQueueEntryView(
    string EntryId,
    string SongId,
    MusicSongRecord Song,
    MusicPlayContextRecord Context);

public sealed record MusicQueuePageResult(
    MusicQueueEntryView[] Entries,
    int Total,
    long Offset,
    int Limit,
    string? CurrentId,
    long Version,
    int Mode,
    MusicQueueEntryView? Current,
    MusicQueueSourceRecord? Source,
    bool HasPriority,
    string PersistenceState) : IMusicQueuePosition;

public sealed record MusicQueuePeekResult(
    string? CurrentId,
    long Version,
    int Mode,
    MusicQueueEntryView? Next) : IMusicQueuePosition;

public sealed record MusicQueueAppendResult(
    string[] EntryIds,
    string? CurrentId,
    long Version,
    int Mode) : IMusicQueuePosition;

public sealed record MusicQueueRemoveResult(
    bool WasCurrent,
    string? NextId,
    string? CurrentId,
    long Version,
    int Mode) : IMusicQueuePosition;

public sealed record MusicQueueStepResult(
    string? TargetId,
    string? CurrentId,
    long Version,
    int Mode) : IMusicQueuePosition;

public sealed record MusicHistoryPlaylistSummary(
    string Id,
    string Title,
    long PlayedAtMs,
    int Count,
    string? CurrentId);

public sealed record MusicHistoryDaySummary(
    string Id,
    string Date,
    int Count);

public sealed record MusicHistoryPlaylistsPage(
    MusicHistoryPlaylistSummary[] Items,
    int Total,
    long Offset,
    int Limit);

public sealed record MusicHistoryDaysPage(
    MusicHistoryDaySummary[] Items,
    int Total,
    long Offset,
    int Limit);

public sealed record MusicHistoryDaySongView(
    MusicSongRecord Song,
    MusicPlayContextRecord Context,
    long PlayedAtMs);

public sealed record MusicHistoryDayDetail(
    string Id,
    string Date,
    MusicHistoryDaySongView[] Items,
    int Total,
    long Offset,
    int Limit);

// ---- B5: Lyrics ----

public sealed record MusicLyricLookupResult(
    string State,
    MusicLyricLineRecord[] Lines,
    string? SourceKey = null,
    bool Available = false);
