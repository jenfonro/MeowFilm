using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

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

public sealed record MusicRecommendationCheckpointMutation(
    string CheckpointId,
    MusicRecommendationCheckpointRecord? Next);

public readonly record struct MusicRecommendationSessionUpdate(
    bool HasValue,
    MusicRecommendationSessionRecord? Value);

public sealed class MusicLibraryCommit
{
    public IReadOnlyList<MusicSongMutation> Songs { get; init; } = [];
    public (MusicLibraryAreaRecord? Previous, MusicLibraryAreaRecord Next)? Library { get; init; }
    public (MusicQueueStateRecord? Previous, MusicQueueStateRecord Next)? Queue { get; init; }
    public IReadOnlyList<MusicHistoryPlaylistMutation> HistoryPlaylists { get; init; } = [];
    public IReadOnlyList<MusicHistoryDayMutation> HistoryDays { get; init; } = [];
    public MusicPlaybackModeRecord? PlaybackMode { get; init; }
    public MusicDeviceSettingsRecord? DeviceSettings { get; init; }
    public MusicAppearanceRecord? Appearance { get; init; }
    public MusicCatalogSourceRecord? CatalogSource { get; init; }
    public MusicDesktopLyricsRecord? DesktopLyrics { get; init; }
    public (MusicQualityPolicyRecord? Previous, MusicQualityPolicyRecord Next)? QualityPolicy { get; init; }
    public (MusicOfficialSourcesRecord? Previous, MusicOfficialSourcesRecord Next)? OfficialSources { get; init; }
    public (MusicMetadataPolicyRecord? Previous, MusicMetadataPolicyRecord Next)? MetadataPolicy { get; init; }
    public (MusicSearchPolicyRecord? Previous, MusicSearchPolicyRecord Next)? SearchPolicy { get; init; }
    public (MusicSearchHistoryRecord? Previous, MusicSearchHistoryRecord Next)? SearchHistory { get; init; }
    public long? SourceExclusionsRevision { get; init; }
    public IReadOnlyList<MusicSourceExclusionMutation> SourceExclusions { get; init; } = [];
    public IReadOnlyList<MusicLocalLyricMutation> LocalLyrics { get; init; } = [];
    public (MusicLibrarySyncStateRecord? Previous, MusicLibrarySyncStateRecord Next)? LibrarySync { get; init; }
    public IReadOnlyList<MusicRecommendationCheckpointMutation> RecommendationCheckpoints { get; init; } = [];
    public MusicRecommendationSessionUpdate RecommendationSession { get; init; }

    public bool IsEmpty =>
        Songs.Count == 0 &&
        Library is null &&
        Queue is null &&
        HistoryPlaylists.Count == 0 &&
        HistoryDays.Count == 0 &&
        PlaybackMode is null &&
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
        SourceExclusions.Count == 0 &&
        LocalLyrics.Count == 0 &&
        LibrarySync is null &&
        RecommendationCheckpoints.Count == 0 &&
        !RecommendationSession.HasValue;
}

/// <summary>The music owner borrows the App data database; typed rows and owner revision commit together.</summary>
public sealed class MusicLibraryDatabase : IAsyncDisposable
{
    private const long MaxSafeInteger = 9_007_199_254_740_991L;
    private readonly IStorageDatabase database;
    internal IStorageDatabase Database => database;
    private readonly object admission = new();
    private string? activeScope;
    private bool closed;
    private Task? disposal;

    public MusicLibraryDatabase(IStorageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        this.database = database;
    }

    private static string OwnerKey(string scope) => "music.library:" + scope;

    private void Check(string? scope = null)
    {
        lock (admission)
        {
            if (closed) throw new StorageException(StorageError.Closed);
            if (scope is null) return;
            if (!IsValidScope(scope)) throw new StorageException(StorageError.InvalidRequest);
            if (activeScope is not null && activeScope != scope) throw new StorageException(StorageError.Conflict);
            activeScope = scope;
        }
    }

    private static bool IsValidScope(string scope)
    {
        if (scope.Length is 0 or > 128) return false;
        foreach (char c in scope)
            if (c < 32 || c == 127) return false;
        return scope.Trim() == scope;
    }

    private static MusicLibraryRevision ReadRevision(DatabaseSession db, string scope)
    {
        var rev = db.Revision(OwnerKey(scope));
        if (rev.Revision is < 0 or > MaxSafeInteger)
            throw new StorageException(StorageError.Corrupt);
        return new MusicLibraryRevision(rev.Revision);
    }

    public Task<MusicLibraryRevision> ReadRevisionAsync(string scope, CancellationToken cancellationToken = default)
    {
        Check(scope);
        return database.ReadAsync(db => ReadRevision(db, scope), cancellationToken);
    }

    private T AtRevision<T>(string scope, MusicLibraryRevision revision, Func<DatabaseSession, T> read)
    {
        Check(scope);
        return database.ReadSnapshot(db =>
        {
            if (ReadRevision(db, scope) != revision) throw new StorageException(StorageError.Conflict);
            return read(db);
        });
    }

    public MusicLibraryAreaRecord? ReadLibrary(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicCollectionsStore.ReadRecord(db, scope));

    public MusicQueueStateRecord? ReadQueue(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicQueueStore.ReadRecord(db, scope));

    public IReadOnlyList<MusicHistoryPlaylistSummaryRecord> ReadHistoryPlaylistSummaries(
        string scope, MusicLibraryRevision revision, string? playlistId = null) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadPlaylistSummaries(db, scope, playlistId));

    public MusicHistoryPlaylistRecord? ReadHistoryPlaylist(
        string scope, MusicLibraryRevision revision, string playlistId) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadPlaylist(db, scope, playlistId));

    public IReadOnlyList<MusicQueueEntryRecord> ReadHistoryPlaylistEntries(
        string scope, MusicLibraryRevision revision, string playlistId, string? entryId = null) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadPlaylistEntries(db, scope, playlistId, entryId));

    public IReadOnlyList<MusicHistoryDaySummaryRecord> ReadHistoryDaySummaries(
        string scope, MusicLibraryRevision revision, string? dayDate = null) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadDaySummaries(db, scope, dayDate));

    public MusicHistoryDayRecord? ReadHistoryDay(
        string scope, MusicLibraryRevision revision, string dayDate) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadDay(db, scope, dayDate));

    public IReadOnlyList<MusicHistoryDaySongRecord> ReadHistoryDaySongs(
        string scope, MusicLibraryRevision revision, string dayDate, string? songId = null) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadDaySongs(db, scope, dayDate, songId));

    public MusicPlaybackModeRecord? ReadPlaybackMode(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadPlaybackMode(db, scope));

    public MusicOnlineSettingsRecord? ReadOnlineSettings(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadOnlineSettings(db, scope));

    public MusicDeviceSettingsRecord? ReadDeviceSettings(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadDeviceSettings(db, scope));

    public MusicAppearanceRecord? ReadAppearance(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadAppearance(db, scope));

    public MusicCatalogSourceRecord? ReadCatalogSource(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadCatalogSource(db, scope));

    public MusicDesktopLyricsRecord? ReadDesktopLyrics(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadDesktopLyrics(db, scope));

    public MusicQualityPolicyRecord? ReadQualityPolicy(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadQualityPolicy(db, scope));

    public MusicOfficialSourcesRecord? ReadOfficialSources(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadOfficialSources(db, scope));

    public MusicMetadataPolicyRecord? ReadMetadataPolicy(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadMetadataPolicy(db, scope));

    public MusicSearchPolicyRecord? ReadSearchPolicy(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadSearchPolicy(db, scope));

    public MusicSearchHistoryRecord? ReadSearchHistory(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadSearchHistory(db, scope));

    public long? ReadSourceExclusionsRevision(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadSourceExclusionsRevision(db, scope));

    public IReadOnlyList<MusicSourceExclusionRecord> ReadSourceExclusions(
        string scope, MusicLibraryRevision revision, string? exclusionId = null) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadSourceExclusions(db, scope, exclusionId));

    public IReadOnlyList<MusicLocalLyricSummaryRecord> ReadLocalLyricSummaries(
        string scope, MusicLibraryRevision revision, string? songId = null) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadLocalLyricSummaries(db, scope, songId));

    public MusicLocalLyricRecord? ReadLocalLyric(string scope, MusicLibraryRevision revision, string songId) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadLocalLyric(db, scope, songId));

    public MusicLibrarySyncStateRecord? ReadLibrarySync(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsStore.ReadLibrarySync(db, scope));

    public IReadOnlyList<MusicRecommendationCheckpointRecord> ReadRecommendationCheckpoints(
        string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicRecommendationStore.ReadCheckpoints(db, scope));

    public MusicRecommendationSessionRecord? ReadRecommendationSession(
        string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicRecommendationStore.ReadSession(db, scope));

    private T Read<T>(Func<DatabaseSession, T> action) { Check(); return database.Read(action); }

    public MusicSongRecord? ReadSong(string id)
    {
        Check();
        return database.ReadSnapshot(db => MusicSongStore.ReadRecord(db, id));
    }

    public Dictionary<string, MusicSongRecord> ReadSongs(IReadOnlyList<string> ids)
    {
        Check();
        if (ids.Count == 0) return new Dictionary<string, MusicSongRecord>(StringComparer.Ordinal);
        return database.ReadSnapshot(db => MusicSongStore.LoadSongsBatch(db, ids));
    }

    public bool ContainsSong(string id) =>
        Read(db => db.Scalar<long>("SELECT count(*) FROM music_songs WHERE song_id=$id", ("$id", id)) != 0);

    public string? FindSource(string sourceKey) =>
        Read(db => db.Scalar<string>("SELECT song_id FROM music_song_sources WHERE source_key=$value", ("$value", sourceKey)));

    public string? FindLocalFile(string localFileId) =>
        Read(db => localFileId.Length == 0 ? null : db.Scalar<string>(
            "SELECT song_id FROM windows_music_song_local_files WHERE local_file_id=$value", ("$value", localFileId)));

    public IReadOnlyList<string> FindCandidates(string matchKey, double baselineDuration) =>
        Read(db => matchKey.Length == 0 || !double.IsFinite(baselineDuration) || baselineDuration <= 0 ? [] : db.Query("""
            SELECT song_id FROM music_songs WHERE match_key=$matchKey AND baseline_duration BETWEEN $low AND $high
              AND EXISTS (SELECT 1 FROM music_song_sources s WHERE s.song_id=music_songs.song_id) ORDER BY song_id
            """, row => row.GetString(0), ("$matchKey", matchKey), ("$low", baselineDuration - 5.001), ("$high", baselineDuration + 5.001)));

    public IReadOnlyList<string> FindArtwork(string address) =>
        Read(db => address.Length == 0 ? [] : db.Query(
            "SELECT song_id FROM music_songs WHERE cover_url=$address AND cover_url<>'' ORDER BY song_id", row => row.GetString(0), ("$address", address)));

    public IReadOnlyList<string> FindQuery(string query) =>
        Read(db => db.Query(
            "SELECT song_id FROM music_songs WHERE search_query=$query ORDER BY song_id", row => row.GetString(0), ("$query", query)));

    public IReadOnlyList<string> SongIds() =>
        Read(db => db.Query("SELECT song_id FROM music_songs ORDER BY song_id", row => row.GetString(0)));

    public Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected, MusicLibraryCommit commit,
        CancellationToken cancellationToken = default)
    {
        Check(scope);
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.Generation is < 0 or >= MaxSafeInteger)
            throw new StorageException(StorageError.InvalidRequest);

        foreach (var song in commit.Songs)
            ValidateSongRecord(song.Record);

        int bytes = EstimateCommitBytes(commit);
        string owner = OwnerKey(scope);
        var expectedStorageRev = new StorageRevision(expected.Generation);

        return database.WriteAsync(db =>
        {
            if (ReadRevision(db, scope) != expected)
                throw new StorageException(StorageError.Conflict);

            bool pruneContexts = false;

            foreach (var mutation in commit.Songs)
            {
                var next = mutation.Record;
                var previous = mutation.Previous;
                if (next.ContentEquals(previous)) continue;
                if (previous is not null)
                {
                    if (previous.BaselineDuration is not null &&
                        !NullableDoubleEqual(previous.BaselineDuration, next.BaselineDuration))
                        throw new StorageException(StorageError.Conflict);
                    var nextKeys = next.Sources.Select(s => s.SourceKey).ToHashSet(StringComparer.Ordinal);
                    if (previous.Sources.Any(s => !nextKeys.Contains(s.SourceKey)))
                        throw new StorageException(StorageError.Conflict);
                }
                var prevKeys = previous is null ? null : previous.Sources.Select(s => s.SourceKey).ToHashSet(StringComparer.Ordinal);
                foreach (var source in next.Sources)
                {
                    if (prevKeys is not null && prevKeys.Contains(source.SourceKey)) continue;
                    if (db.Scalar<string>("SELECT song_id FROM music_song_sources WHERE source_key=$key", ("$key", source.SourceKey)) is { } sourceOwner &&
                        sourceOwner != next.SongId)
                        throw new StorageException(StorageError.Conflict);
                }
                MusicSongStore.SaveIncremental(db, next, previous, mutation.MatchKey, mutation.SearchQuery);
            }

            if (commit.Library is { Previous: var prevLib, Next: var nextLib })
                MusicCollectionsStore.Save(db, scope, prevLib, nextLib);

            if (commit.Queue is { Previous: var prevQueue, Next: var nextQueue })
                pruneContexts |= MusicQueueStore.Save(db, scope, prevQueue, nextQueue);

            foreach (var pl in commit.HistoryPlaylists)
                pruneContexts |= MusicHistoryStore.ApplyPlaylistMutation(db, scope, pl);

            foreach (var day in commit.HistoryDays)
                pruneContexts |= MusicHistoryStore.ApplyDayMutation(db, scope, day);

            if (commit.PlaybackMode is { } mode)
                MusicSettingsStore.WritePlaybackMode(db, scope, mode);
            if (commit.DeviceSettings is { } device)
                MusicSettingsStore.WriteDeviceSettings(db, scope, device);
            if (commit.Appearance is { } appearance)
                MusicSettingsStore.WriteAppearance(db, scope, appearance);
            if (commit.CatalogSource is { } catalogSource)
                MusicSettingsStore.WriteCatalogSource(db, scope, catalogSource);
            if (commit.DesktopLyrics is { } desktopLyrics)
                MusicSettingsStore.WriteDesktopLyrics(db, scope, desktopLyrics);
            if (commit.QualityPolicy is { Previous: var prevQuality, Next: var qualityPolicy })
                MusicSettingsStore.WriteQualityPolicy(db, scope, prevQuality, qualityPolicy);
            if (commit.OfficialSources is { Previous: var prevOfficial, Next: var officialSources })
                MusicSettingsStore.WriteOfficialSources(db, scope, prevOfficial, officialSources);
            if (commit.MetadataPolicy is { Previous: var prevMetadata, Next: var metadataPolicy })
                MusicSettingsStore.WriteMetadataPolicy(db, scope, prevMetadata, metadataPolicy);
            if (commit.SearchPolicy is { Previous: var prevSearchPolicy, Next: var searchPolicy })
                MusicSettingsStore.WriteSearchPolicy(db, scope, prevSearchPolicy, searchPolicy);
            if (commit.SearchHistory is { Previous: var prevHistory, Next: var nextHistory })
                MusicSettingsStore.WriteSearchHistory(db, scope, prevHistory, nextHistory);

            if (commit.SourceExclusionsRevision is { } exclRev)
                MusicSettingsStore.WriteSourceExclusionsRevision(db, scope, exclRev);
            foreach (var excl in commit.SourceExclusions)
                MusicSettingsStore.ApplySourceExclusionMutation(db, scope, excl);

            foreach (var lyr in commit.LocalLyrics)
                MusicSettingsStore.ApplyLocalLyricMutation(db, scope, lyr);

            if (commit.LibrarySync is { Previous: var prevSync, Next: var syncState })
                MusicSettingsStore.WriteLibrarySync(db, scope, prevSync, syncState);

            foreach (var cp in commit.RecommendationCheckpoints)
                pruneContexts |= MusicRecommendationStore.ApplyCheckpointMutation(db, scope, cp);

            if (commit.RecommendationSession.HasValue)
                MusicRecommendationStore.WriteSession(db, scope, commit.RecommendationSession.Value);

            if (pruneContexts)
                MusicPlayContextStore.PruneOrphans(db);

            var committed = db.CommitRevision(owner, expectedStorageRev);
            return new MusicLibraryRevision(committed.Revision);
        }, cancellationToken, bytes);
    }

    private static void ValidateSongRecord(MusicSongRecord song)
    {
        if (!Guid.TryParseExact(song.SongId, "D", out var parsed) || song.SongId != parsed.ToString("D"))
            throw new StorageException(StorageError.InvalidRequest);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in song.Sources)
        {
            if (string.IsNullOrEmpty(src.SourceKey) || string.IsNullOrEmpty(src.Platform) ||
                src.InfoSource != src.Platform || !keys.Add(src.SourceKey))
                throw new StorageException(StorageError.InvalidRequest);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (admission)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            return new(disposal = database.DrainAsync());
        }
    }
}
