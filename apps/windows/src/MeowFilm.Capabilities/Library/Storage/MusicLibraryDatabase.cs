using MeowFilm.Platform.Storage;
using static MeowFilm.Capabilities.Library.MusicStorageModelHelpers;

namespace MeowFilm.Capabilities.Library;

/// <summary>The music owner borrows the App data database; typed rows and owner revision commit together.</summary>
public sealed class MusicLibraryDatabase : IMusicLibraryStore
{
    private const long MaxSafeInteger = 9_007_199_254_740_991L;
    private readonly IStorageDatabase database;
    private readonly bool ownsDatabase;
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

    private MusicLibraryDatabase() { database = NativeDatabase.CreateMemory(DatabaseRole.Data); ownsDatabase = true; }
    public static MusicLibraryDatabase CreateMemory() => new();

    private static string OwnerKey(string scope) => "music.library:" + scope;

    private void Check(string? scope = null)
    {
        lock (admission)
        {
            if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (scope is null) return;
            if (!IsValidScope(scope)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            if (activeScope is not null && activeScope != scope) throw new DocumentStoreException(DocumentStoreError.Conflict);
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
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
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
            if (ReadRevision(db, scope) != revision) throw new DocumentStoreException(DocumentStoreError.Conflict);
            return read(db);
        });
    }

    public MusicLibraryAreaRecord? ReadLibrary(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicCollectionsStore.ReadRecord(db, scope));

    public MusicQueueStateRecord? ReadQueue(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicQueueStore.ReadRecord(db, scope));

    public bool HasHistory(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicHistoryStore.Exists(db, scope));

    public MusicHistoryAreaRecord? ReadHistoryArea(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicHistoryStore.ReadArea(db, scope));

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
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadPlaybackMode(db, scope));

    public MusicOnlineSettingsRecord? ReadOnlineSettings(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadOnlineSettings(db, scope));

    public MusicDeviceSettingsRecord? ReadDeviceSettings(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadDeviceSettings(db, scope));

    public MusicAppearanceRecord? ReadAppearance(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadAppearance(db, scope));

    public MusicCatalogSourceRecord? ReadCatalogSource(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadCatalogSource(db, scope));

    public MusicDesktopLyricsRecord? ReadDesktopLyrics(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadDesktopLyrics(db, scope));

    public MusicQualityPolicyRecord? ReadQualityPolicy(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadQualityPolicy(db, scope));

    public MusicOfficialSourcesRecord? ReadOfficialSources(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadOfficialSources(db, scope));

    public MusicMetadataPolicyRecord? ReadMetadataPolicy(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadMetadataPolicy(db, scope));

    public MusicSearchPolicyRecord? ReadSearchPolicy(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadSearchPolicy(db, scope));

    public MusicSearchHistoryRecord? ReadSearchHistory(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadSearchHistory(db, scope));

    public long? ReadSourceExclusionsRevision(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadSourceExclusionsRevision(db, scope));

    public IReadOnlyList<MusicSourceExclusionRecord> ReadSourceExclusions(
        string scope, MusicLibraryRevision revision, string? exclusionId = null) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadSourceExclusions(db, scope, exclusionId));

    public IReadOnlyList<MusicLocalLyricSummaryRecord> ReadLocalLyricSummaries(
        string scope, MusicLibraryRevision revision, string? songId = null) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadLocalLyricSummaries(db, scope, songId));

    public MusicLocalLyricRecord? ReadLocalLyric(string scope, MusicLibraryRevision revision, string songId) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadLocalLyric(db, scope, songId));

    public MusicLibrarySyncStateRecord? ReadLibrarySync(string scope, MusicLibraryRevision revision) =>
        AtRevision(scope, revision, db => MusicSettingsAreaStore.ReadLibrarySync(db, scope));

    private T Read<T>(Func<DatabaseSession, T> action) { Check(); return database.Read(action); }

    public MusicSongRecord? ReadSong(string id)
    {
        Check();
        return database.ReadSnapshot(db => MusicSongStore.ReadRecord(db, id));
    }

    public bool ContainsSong(string id) =>
        Read(db => db.Scalar<long>("SELECT count(*) FROM music_songs WHERE song_id=$id", ("$id", id)) != 0);

    public string? FindSource(string sourceKey) =>
        Read(db => db.Scalar<string>("SELECT song_id FROM music_song_sources WHERE source_key=$value", ("$value", sourceKey)));

    public string? FindLocalFile(string localFileId) =>
        Read(db => localFileId.Length == 0 ? null : db.Scalar<string>(
            "SELECT song_id FROM music_songs WHERE local_file_id=$value", ("$value", localFileId)));

    public IReadOnlyList<string> FindCandidates(string matchKey, double baselineDuration) =>
        Read(db => matchKey.Length == 0 || !double.IsFinite(baselineDuration) || baselineDuration <= 0 ? [] : db.Query("""
            SELECT song_id FROM music_songs WHERE match_key=$matchKey AND baseline_duration BETWEEN $low AND $high
              AND EXISTS (SELECT 1 FROM music_song_sources s WHERE s.song_id=music_songs.song_id) ORDER BY song_id
            """, row => row.GetString(0), ("$matchKey", matchKey), ("$low", baselineDuration - 5), ("$high", baselineDuration + 5)));

    public IReadOnlyList<string> FindArtwork(string address) =>
        Read(db => address.Length == 0 ? [] : db.Query(
            "SELECT song_id FROM music_songs WHERE cover_url=$address ORDER BY song_id", row => row.GetString(0), ("$address", address)));

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
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);

        foreach (var song in commit.Songs)
            ValidateSongRecord(song.Record);

        int bytes = EstimateCommitBytes(commit);
        string owner = OwnerKey(scope);
        var expectedStorageRev = new StorageRevision(expected.Generation);

        return database.WriteAsync(db =>
        {
            if (ReadRevision(db, scope) != expected)
                throw new DocumentStoreException(DocumentStoreError.Conflict);

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
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                    var nextKeys = next.Sources.Select(s => s.SourceKey).ToHashSet(StringComparer.Ordinal);
                    if (previous.Sources.Any(s => !nextKeys.Contains(s.SourceKey)))
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                }
                var prevKeys = previous is null ? null : previous.Sources.Select(s => s.SourceKey).ToHashSet(StringComparer.Ordinal);
                foreach (var source in next.Sources)
                {
                    if (prevKeys is not null && prevKeys.Contains(source.SourceKey)) continue;
                    if (db.Scalar<string>("SELECT song_id FROM music_song_sources WHERE source_key=$key", ("$key", source.SourceKey)) is { } sourceOwner &&
                        sourceOwner != next.SongId)
                        throw new DocumentStoreException(DocumentStoreError.Conflict);
                }
                MusicSongStore.SaveIncremental(db, next, previous, mutation.MatchKey, mutation.SearchQuery);
            }

            if (commit.Library is { Previous: var prevLib, Next: var nextLib })
                MusicCollectionsStore.Save(db, scope, prevLib, nextLib);

            if (commit.Queue is { Previous: var prevQueue, Next: var nextQueue })
                pruneContexts |= MusicQueueStore.Save(db, scope, prevQueue, nextQueue);

            if (commit.HistoryArea is { } histArea)
                pruneContexts |= MusicHistoryStore.ReplaceArea(db, scope, histArea);

            foreach (var pl in commit.HistoryPlaylists)
                pruneContexts |= MusicHistoryStore.ApplyPlaylistMutation(db, scope, pl);

            foreach (var day in commit.HistoryDays)
                pruneContexts |= MusicHistoryStore.ApplyDayMutation(db, scope, day);

            if (commit.PlaybackMode is { } mode)
                MusicSettingsAreaStore.WritePlaybackMode(db, scope, mode);
            if (commit.OnlineSettings is { } online)
                MusicSettingsAreaStore.WriteOnlineSettings(db, scope, online);
            if (commit.DeviceSettings is { } device)
                MusicSettingsAreaStore.WriteDeviceSettings(db, scope, device);
            if (commit.Appearance is { } appearance)
                MusicSettingsAreaStore.WriteAppearance(db, scope, appearance);
            if (commit.CatalogSource is { } catalogSource)
                MusicSettingsAreaStore.WriteCatalogSource(db, scope, catalogSource);
            if (commit.DesktopLyrics is { } desktopLyrics)
                MusicSettingsAreaStore.WriteDesktopLyrics(db, scope, desktopLyrics);
            if (commit.QualityPolicy is { } qualityPolicy)
                MusicSettingsAreaStore.WriteQualityPolicy(db, scope, qualityPolicy);
            if (commit.OfficialSources is { } officialSources)
                MusicSettingsAreaStore.WriteOfficialSources(db, scope, officialSources);
            if (commit.MetadataPolicy is { } metadataPolicy)
                MusicSettingsAreaStore.WriteMetadataPolicy(db, scope, metadataPolicy);
            if (commit.SearchPolicy is { } searchPolicy)
                MusicSettingsAreaStore.WriteSearchPolicy(db, scope, searchPolicy);
            if (commit.SearchHistory is { } searchHistory)
                MusicSettingsAreaStore.WriteSearchHistory(db, scope, searchHistory);

            if (commit.SourceExclusionsRevision is { } exclRev)
                MusicSettingsAreaStore.WriteSourceExclusionsRevision(db, scope, exclRev);
            if (commit.ReplaceSourceExclusions is { } allExclusions)
                MusicSettingsAreaStore.ReplaceSourceExclusions(db, scope, allExclusions);
            foreach (var excl in commit.SourceExclusions)
                MusicSettingsAreaStore.ApplySourceExclusionMutation(db, scope, excl);

            if (commit.ReplaceLocalLyrics is { } allLocalLyrics)
                MusicSettingsAreaStore.ReplaceLocalLyrics(db, scope, allLocalLyrics);
            foreach (var lyr in commit.LocalLyrics)
                MusicSettingsAreaStore.ApplyLocalLyricMutation(db, scope, lyr);

            if (commit.LibrarySync is { } syncState)
                MusicSettingsAreaStore.WriteLibrarySync(db, scope, syncState);

            if (pruneContexts)
                MusicPlayContextStore.PruneOrphans(db);

            var committed = db.CommitRevision(owner, expectedStorageRev);
            return new MusicLibraryRevision(committed.Revision);
        }, cancellationToken, bytes);
    }

    private static void ValidateSongRecord(MusicSongRecord song)
    {
        if (!Guid.TryParseExact(song.SongId, "D", out var parsed) || song.SongId != parsed.ToString("D"))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in song.Sources)
        {
            if (string.IsNullOrEmpty(src.SourceKey) || string.IsNullOrEmpty(src.Platform) ||
                src.InfoSource != src.Platform || !keys.Add(src.SourceKey))
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (admission)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            return new(disposal = CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        try { await database.DrainAsync().ConfigureAwait(false); }
        finally
        {
            if (ownsDatabase) await database.DisposeAsync().ConfigureAwait(false);
        }
    }
}
