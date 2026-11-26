namespace MeowFilm.Capabilities.Library;

/// <summary>Immutable optimistic token issued by one library owner; contains no account credentials.</summary>
public sealed class MusicLibrarySyncSnapshot
{
    internal MusicLibrarySyncSnapshot(MusicLibraryOwner owner, MusicLibraryAreaRecord library, MusicLibrarySyncStateRecord state, long songObservation)
    {
        Owner = owner;
        Library = library;
        State = state;
        SongObservation = songObservation;
    }

    internal MusicLibraryOwner Owner { get; }
    internal (long LibraryRevision, long SyncRevision) Stamp => (Library.LibraryRevision, State.Revision);
    internal long SongObservation { get; }
    public MusicLibraryAreaRecord Library { get; }
    public MusicLibrarySyncStateRecord State { get; }
}

public sealed partial class MusicLibraryOwner
{
    internal static readonly string[] SyncPlatforms = ["wy", "tx", "kg"];

    internal static MusicLibrarySyncStateRecord EmptyLibrarySync()
    {
        var platforms = new Dictionary<string, MusicSyncPlatformRecord>(StringComparer.Ordinal);
        foreach (string platform in SyncPlatforms)
            platforms[platform] = new MusicSyncPlatformRecord(false, false, "");
        return new MusicLibrarySyncStateRecord(0, platforms, null, null, null, "idle", 0, "", []);
    }

    private static MusicLibrarySyncStateRecord LibrarySync(MusicLibrarySession doc) =>
        doc.ReadLibrarySync() ?? EmptyLibrarySync();

    public async Task<MusicLibrarySyncSnapshot> ReadLibrarySyncAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            var candidate = session!.Candidate(false);
            var state = LibrarySync(candidate);
            var library = LibraryState(candidate);
            return new MusicLibrarySyncSnapshot(this, library, state, BeginSongObservation());
        }
        finally { gate.Release(); }
    }

    private MusicLibrarySyncSnapshot SyncSnapshot(MusicLibrarySession data, MusicLibrarySyncStateRecord state, long observation) =>
        new(this, LibraryState(data), state, observation);

    internal MusicSongRecord ReadLibrarySyncSong(string id)
    {
        MusicSongSession? snapshotSession;
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            snapshotSession = visibleSongs;
        }
        return snapshotSession?.Read(id) ?? throw new MusicLibraryException("SONG_NOT_FOUND");
    }

    internal string? TryFindLibrarySyncSource(string key)
    {
        MusicSongSession? snapshotSession;
        lock (repositoryGate)
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            snapshotSession = visibleSongs;
        }
        return snapshotSession?.SourceOwner(key);
    }

    internal string FindLibrarySyncSource(string key) =>
        TryFindLibrarySyncSource(key) ?? throw new MusicLibraryException("SONG_NOT_FOUND");

    private void CheckLibrarySyncLocked(MusicLibrarySyncSnapshot expected)
    {
        MusicRules.Require(ReferenceEquals(expected.Owner, this), "INVALID_SYNC_TOKEN");
        MusicRules.Require(!closed && !preparing, "CLOSED");
        var currentLibrary = LibraryState(session!);
        var currentSync = LibrarySync(session!);
        MusicRules.Require(expected.Stamp == (currentLibrary.LibraryRevision, currentSync.Revision), "SYNC_SUPERSEDED");
    }

    public async Task CheckLibrarySyncAsync(MusicLibrarySyncSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { CheckLibrarySyncLocked(expected); } finally { gate.Release(); }
    }

    internal async Task<MusicLibrarySyncSnapshot> AdoptLibrarySyncSongsAsync(MusicLibrarySyncSnapshot expected,
        IReadOnlyList<(MusicSongRecord Song, bool HasMetadata)> songs, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            CheckLibrarySyncLocked(expected);
            var candidate = session!.Candidate(true);
            var accepted = new Dictionary<(string SongId, string Platform), long>();
            _ = RegisterSongs(candidate, songs.Where(row => row.HasMetadata).Select(row => row.Song),
                authoritative: true, observation: expected.SongObservation, accepted: accepted);
            _ = RegisterSongs(candidate, songs.Where(row => !row.HasMetadata).Select(row => row.Song));
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(candidate, accepted).ConfigureAwait(false);
            return SyncSnapshot(candidate, LibrarySync(candidate), expected.SongObservation);
        }
        finally { gate.Release(); }
    }

    public async Task<MusicLibrarySyncSnapshot> CommitLibrarySyncAsync(MusicLibrarySyncSnapshot expected,
        MusicLibraryAreaRecord nextLibrary, MusicLibrarySyncStateRecord nextState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(nextLibrary);
        ArgumentNullException.ThrowIfNull(nextState);
        bool collectionsChanged = false;
        MusicLibrarySyncSnapshot committed;
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            CheckLibrarySyncLocked(expected);
            MusicRules.Require(nextLibrary.LibraryRevision == expected.Library.LibraryRevision &&
                nextState.Revision == expected.State.Revision, "SYNC_SUPERSEDED");
            var candidate = session!.Candidate(true);
            if (!LibraryAreaEquals(nextLibrary, expected.Library))
            {
                long nextLibRev = checked(nextLibrary.LibraryRevision + 1);
                nextLibrary = nextLibrary with { StateRevision = nextLibRev, LibraryRevision = nextLibRev };
                collectionsChanged = true;
            }
            nextState = nextState with { Revision = checked(nextState.Revision + 1) };
            candidate.SetLibrary(nextLibrary);
            candidate.SetLibrarySync(nextState);
            await CommitAsync(candidate).ConfigureAwait(false);
            committed = SyncSnapshot(candidate, nextState, BeginSongObservation());
        }
        finally { gate.Release(); }
        if (collectionsChanged) NotifyLocalCollectionsChanged();
        return committed;
    }
}
