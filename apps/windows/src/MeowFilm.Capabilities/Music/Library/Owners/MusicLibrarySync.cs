using System.Text.Json;

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
        Stamp = ControlStamp(library, state);
    }

    internal static string ControlStamp(MusicLibraryAreaRecord library, MusicLibrarySyncStateRecord state) =>
        MusicRules.CanonicalDigest(MusicNodeSongCodec.Build(w =>
        {
            w.WriteStartArray();
            WriteLibraryStamp(w, library);
            WriteSyncStamp(w, state);
            w.WriteEndArray();
        }));

    private static void WriteLibraryStamp(Utf8JsonWriter w, MusicLibraryAreaRecord lib)
    {
        w.WriteStartObject();
        w.WriteNumber("version", 1);
        w.WriteString("ownerId", lib.OwnerId);
        w.WriteNumber("revision", lib.LibraryRevision);
        w.WriteNumber("nextId", lib.NextId);
        w.WritePropertyName("accounts");
        w.WriteStartObject();
        foreach (var (k, v) in lib.Accounts.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(k, v);
        w.WriteEndObject();
        w.WritePropertyName("collections");
        w.WriteStartArray();
        foreach (var c in lib.Collections)
        {
            w.WriteStartObject();
            w.WriteString("id", c.CollectionId);
            w.WriteString("name", c.Name);
            w.WriteString("kind", c.Kind);
            w.WriteNumber("revision", c.Revision);
            w.WriteBoolean("manualOrder", c.ManualOrder);
            if (c.Deleted) w.WriteBoolean("deleted", true);
            if (c.LocallyCreated) w.WriteBoolean("locallyCreated", true);
            MusicNodeSongCodec.WriteStringArray(w, "order", c.Order);
            w.WritePropertyName("bindings");
            w.WriteStartArray();
            foreach (var b in c.Bindings)
            {
                w.WriteStartObject();
                w.WriteString("platform", b.Platform);
                w.WriteString("accountId", b.AccountId);
                w.WriteString("id", b.RemoteId);
                w.WriteBoolean("writable", b.Writable);
                w.WriteString("name", b.Name);
                MusicNodeSongCodec.WriteStringArray(w, "baseline", b.Baseline);
                w.WritePropertyName("map");
                w.WriteStartObject();
                foreach (var (k, v) in b.Map.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(k, v);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("intents");
            w.WriteStartObject();
            foreach (var (k, intent) in c.Intents.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WritePropertyName(k);
                w.WriteStartObject();
                w.WriteBoolean("present", intent.Present);
                w.WriteBoolean("pending", intent.Pending);
                w.WriteString("origin", intent.Origin);
                if (intent.Acknowledged is { } ack) MusicNodeSongCodec.WriteStringArray(w, "acknowledged", ack);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("networkPlaylists");
        w.WriteStartArray();
        foreach (var r in lib.NetworkPlaylists)
        {
            w.WriteStartObject();
            w.WriteString("id", r.PlaylistId);
            w.WriteString("platform", r.Platform);
            w.WriteString("remoteId", r.RemoteId);
            w.WriteString("name", r.Name);
            w.WriteString("cover", r.Cover);
            w.WriteNumber("count", r.SongCount);
            w.WriteNumber("revision", r.Revision);
            w.WriteBoolean("present", r.Present);
            if (r.SubAccountId is not null)
            {
                w.WritePropertyName("subscription");
                w.WriteStartObject();
                w.WriteString("accountId", r.SubAccountId);
                w.WriteBoolean("present", r.SubPresent == true);
                w.WritePropertyName("locator");
                w.WriteStartObject();
                MusicNodeSongCodec.WriteOptionalString(w, "listId", r.SubListId);
                MusicNodeSongCodec.WriteOptionalString(w, "creatorId", r.SubCreatorId);
                MusicNodeSongCodec.WriteOptionalString(w, "creatorListId", r.SubCreatorListId);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            else w.WriteNull("subscription");
            if (r.IntentAccountId is not null)
            {
                w.WritePropertyName("intent");
                w.WriteStartObject();
                w.WriteString("accountId", r.IntentAccountId);
                w.WriteBoolean("present", r.IntentPresent == true);
                w.WriteEndObject();
            }
            else w.WriteNull("intent");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteSyncStamp(Utf8JsonWriter w, MusicLibrarySyncStateRecord s)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", 1);
        w.WriteNumber("revision", s.Revision);
        w.WritePropertyName("platforms");
        w.WriteStartObject();
        foreach (string plat in MusicLibraryOwner.SyncPlatforms)
        {
            var p = s.Platforms.TryGetValue(plat, out var found) ? found : new MusicSyncPlatformRecord(false, false, "");
            w.WritePropertyName(plat);
            w.WriteStartObject();
            w.WriteBoolean("favorites", p.Favorites);
            w.WriteBoolean("playlists", p.Playlists);
            w.WriteString("accountId", p.AccountId);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WritePropertyName("pending");
        if (s.Pending is { } pen)
        {
            w.WriteStartObject();
            w.WriteString("platform", pen.Platform);
            w.WriteString("accountId", pen.AccountId);
            w.WriteString("collectionId", pen.CollectionId);
            w.WriteString("remoteId", pen.RemoteId);
            w.WriteString("operation", pen.Operation);
            w.WriteString("name", pen.Name);
            if (pen.BeforePresent is { } bp) w.WriteBoolean("beforePresent", bp);
            if (pen.DesiredPresent is { } dp) w.WriteBoolean("desiredPresent", dp);
            MusicNodeSongCodec.WriteOptionalString(w, "locatorListId", pen.LocatorListId);
            MusicNodeSongCodec.WriteOptionalString(w, "locatorCreatorId", pen.LocatorCreatorId);
            MusicNodeSongCodec.WriteOptionalString(w, "locatorCreatorListId", pen.LocatorCreatorListId);
            MusicNodeSongCodec.WriteStringArray(w, "before", pen.Before);
            MusicNodeSongCodec.WriteStringArray(w, "desired", pen.Desired);
            MusicNodeSongCodec.WriteStringArray(w, "ids", pen.Ids);
            MusicNodeSongCodec.WriteStringArray(w, "directory", pen.Directory);
            w.WriteEndObject();
        }
        else w.WriteNullValue();
        w.WritePropertyName("confirmation");
        WriteSyncConfirmation(w, s.Confirmation);
        w.WritePropertyName("approved");
        WriteSyncConfirmation(w, s.Approved);
        w.WritePropertyName("status");
        w.WriteStartObject();
        w.WriteString("phase", s.StatusPhase);
        w.WriteString("code", s.StatusCode);
        w.WriteNumber("at", s.StatusAtMs);
        w.WritePropertyName("issues");
        w.WriteStartArray();
        foreach (var issue in s.Issues)
        {
            w.WriteStartObject();
            w.WriteString("platform", issue.Platform);
            w.WriteString("collectionId", issue.CollectionId);
            w.WriteString("code", issue.Code);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteSyncConfirmation(Utf8JsonWriter w, MusicSyncConfirmationRecord? conf)
    {
        if (conf is null) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        w.WriteString("token", conf.Token);
        w.WriteString("code", conf.Code);
        w.WriteNumber("count", conf.Count);
        w.WriteEndObject();
    }

    internal MusicLibraryOwner Owner { get; }
    internal string Stamp { get; }
    internal long SongObservation { get; }
    public MusicLibraryAreaRecord Library { get; }
    public MusicLibrarySyncStateRecord State { get; }
}

public sealed partial class MusicLibraryOwner
{
    internal static readonly string[] SyncPlatforms = ["wy", "tx", "kg"];
    private static readonly HashSet<string> ConfirmationCodes =
        ["EMPTY_REMOTE", "BULK_REMOTE_DELETE", "DELETE_REMOTE_PLAYLIST", "BULK_LOCAL_DELETE"];

    internal static MusicLibrarySyncStateRecord EmptyLibrarySync()
    {
        var platforms = new Dictionary<string, MusicSyncPlatformRecord>(StringComparer.Ordinal);
        foreach (string platform in SyncPlatforms)
            platforms[platform] = new MusicSyncPlatformRecord(false, false, "");
        return new MusicLibrarySyncStateRecord(0, platforms, null, null, null, "idle", 0, "", []);
    }

    private static MusicLibrarySyncStateRecord LibrarySync(MusicLibrarySession doc)
    {
        try { return doc.ReadLibrarySync() ?? EmptyLibrarySync(); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_SYNC"); }
    }

    private static void ValidateLibrarySync(MusicLibrarySyncStateRecord s)
    {
        try
        {
            MusicRules.Require(s.Revision is >= 0 and <= MusicRules.MaxInteger);
            MusicRules.Require(s.Platforms.Count == SyncPlatforms.Length);
            foreach (var platform in SyncPlatforms)
            {
                MusicRules.Require(s.Platforms.TryGetValue(platform, out var p));
                if (p!.Favorites || p.Playlists) MusicRules.Require(p.AccountId.Length > 0);
            }
            if (s.Pending is { } w)
            {
                MusicRules.Require(SyncPlatforms.Contains(w.Platform) && w.AccountId.Length > 0 && w.Name.Length > 0);
                if (w.Operation is "subscribe" or "unsubscribe")
                {
                    MusicRules.Require(w.BeforePresent is { } bp && w.DesiredPresent is { } dp &&
                        bp != dp && dp == (w.Operation == "subscribe") &&
                        w.CollectionId == NetworkPlaylistId(w.Platform, w.RemoteId));
                }
                else
                {
                    MusicRules.Require(MusicRules.IsValidId(w.CollectionId) && MusicRules.IsValidId(w.Name, null));
                    MusicRules.Require(w.Operation is "create" or "delete" or "rename" or "add" or "remove" or "order");
                    MusicRules.Require(w.Before.All(id => id.Length > 0) &&
                        w.Desired.All(id => id.Length > 0) &&
                        w.Ids.All(id => id.Length > 0) &&
                        w.Directory.All(id => id.Length > 0));
                    MusicRules.Require(w.Operation == "create" ? w.RemoteId.Length == 0 : w.RemoteId.Length > 0);
                }
            }
            static void CheckConfirmation(MusicSyncConfirmationRecord? v)
            {
                if (v is null) return;
                MusicRules.Require(MusicRules.IsLowerHex(v.Token, 64) &&
                    ConfirmationCodes.Contains(v.Code) && v.Count is >= 0 and <= MusicRules.MaxInteger);
            }
            CheckConfirmation(s.Confirmation);
            CheckConfirmation(s.Approved);
            MusicRules.Require(s.Confirmation is null || s.Approved is null);
            MusicRules.Require(s.StatusPhase is "idle" or "pushing" or "synced" or "partial" or "blocked" or "superseded" &&
                s.StatusCode.Length <= 80 && s.StatusAtMs is >= 0 and <= MusicRules.MaxInteger);
            foreach (var issue in s.Issues)
            {
                MusicRules.Require(issue.CollectionId.Length > 0 &&
                    SyncPlatforms.Contains(issue.Platform) &&
                    MusicRules.IsValidId(issue.Code, 80));
            }
            MusicRules.Require(s.StatusPhase switch
            {
                "idle" or "synced" => s.StatusCode.Length == 0 && s.Issues.Length == 0,
                "pushing" => s.StatusCode.Length == 0,
                "partial" => s.StatusCode.Length == 0 && s.Issues.Length > 0,
                "blocked" or "superseded" => s.StatusCode.Length > 0,
                _ => false
            });
            bool awaitingConfirmation = s.StatusPhase == "blocked" && s.StatusCode == "CONFIRMATION_REQUIRED";
            MusicRules.Require((s.Confirmation is not null) == awaitingConfirmation);
            if (awaitingConfirmation) MusicRules.Require(s.Pending is null);
        }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_SYNC"); }
    }

    private static void ValidateLibrarySyncRelations(MusicLibrarySyncStateRecord state, MusicLibraryAreaRecord library)
    {
        try
        {
            var collections = library.Collections.ToDictionary(c => c.CollectionId, StringComparer.Ordinal);
            var references = library.NetworkPlaylists.ToDictionary(r => r.PlaylistId, StringComparer.Ordinal);
            if (state.Pending is { } p)
            {
                MusicRules.Require(
                    state.Platforms.TryGetValue(p.Platform, out var plat) && plat.AccountId == p.AccountId &&
                    library.Accounts.TryGetValue(p.Platform, out var libAcc) && libAcc == p.AccountId);
                if (p.Operation is "subscribe" or "unsubscribe")
                {
                    MusicRules.Require(references.TryGetValue(p.CollectionId, out var reference) &&
                        reference.Platform == p.Platform && reference.RemoteId == p.RemoteId);
                }
                else
                {
                    MusicRules.Require(collections.TryGetValue(p.CollectionId, out var collection));
                    MusicRules.Require(p.Ids.Distinct(StringComparer.Ordinal).Count() == p.Ids.Length &&
                        p.Directory.Distinct(StringComparer.Ordinal).Count() == p.Directory.Length);
                    MusicRules.Require(p.Operation switch
                    {
                        "create" => collection!.Kind == "playlist" && p.RemoteId.Length == 0 &&
                            p.Before.Length == 0 && p.Desired.Length == 0 && p.Ids.Length == 0,
                        "delete" => p.RemoteId.Length > 0 && p.Desired.Length == 0 && p.Ids.Length == 0 && p.Directory.Length == 0,
                        "rename" => collection!.Kind == "playlist" && p.RemoteId.Length > 0 &&
                            p.Desired.Length == 0 && p.Ids.Length == 0 && p.Directory.Length == 0,
                        "add" => p.RemoteId.Length > 0 && p.Ids.Length > 0 && p.Directory.Length == 0 &&
                            p.Ids.All(id => !p.Before.Contains(id, StringComparer.Ordinal)) &&
                            p.Desired.SequenceEqual(p.Before.Concat(p.Ids), StringComparer.Ordinal),
                        "remove" => p.RemoteId.Length > 0 && p.Ids.Length > 0 && p.Directory.Length == 0 &&
                            p.Ids.All(id => p.Before.Contains(id, StringComparer.Ordinal)) &&
                            p.Desired.SequenceEqual(p.Before.Where(id => !p.Ids.Contains(id, StringComparer.Ordinal)), StringComparer.Ordinal),
                        "order" => p.RemoteId.Length > 0 && p.Ids.Length == 0 && p.Directory.Length == 0 &&
                            !p.Desired.SequenceEqual(p.Before, StringComparer.Ordinal) &&
                            p.Desired.OrderBy(id => id, StringComparer.Ordinal)
                                .SequenceEqual(p.Before.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal),
                        _ => false
                    });
                    var accountBindings = collection!.Bindings.Where(binding =>
                        binding.Platform == p.Platform && binding.AccountId == p.AccountId).ToArray();
                    MusicRules.Require(p.Operation == "create"
                        ? accountBindings.Length == 0
                        : accountBindings.Count(binding => binding.RemoteId == p.RemoteId) == 1);
                }
            }
            foreach (var issue in state.Issues)
                MusicRules.Require(collections.ContainsKey(issue.CollectionId) || references.ContainsKey(issue.CollectionId));
        }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_SYNC"); }
    }

    public async Task<MusicLibrarySyncSnapshot> ReadLibrarySyncAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            MusicRules.Require(!closed && !preparing, "CLOSED");
            if (pending is { } attempt && IsAutoReconcilablePending(attempt))
                await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            MusicRules.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            var candidate = session!.Candidate(false);
            var state = LibrarySync(candidate); ValidateLibrarySync(state);
            MusicRules.Require(candidate.ReadLibrarySync() is null || candidate.ReadLibrary() is not null, "CORRUPT_SYNC");
            var library = ValidateLibrary(LibraryState(candidate), scope, candidate.Songs.Contains);
            ValidateLibrarySyncRelations(state, library);
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
        MusicRules.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
        string current = MusicLibrarySyncSnapshot.ControlStamp(LibraryState(session!), LibrarySync(session!));
        MusicRules.Require(expected.Stamp == current, "SYNC_SUPERSEDED");
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
            if (pending is { } attempt && IsAutoReconcilablePending(attempt))
                await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            CheckLibrarySyncLocked(expected);
            var candidate = session!.Candidate(true);
            var accepted = new Dictionary<(string SongId, string Platform), long>();
            _ = RegisterSongs(candidate, songs.Where(row => row.HasMetadata).Select(row => row.Song),
                authoritative: true, observation: expected.SongObservation, accepted: accepted);
            _ = RegisterSongs(candidate, songs.Where(row => !row.HasMetadata).Select(row => row.Song));
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAsync(candidate, PendingOwner.Sync, cancellationToken, accepted).ConfigureAwait(false);
            return SyncSnapshot(candidate, LibrarySync(candidate), expected.SongObservation);
        }
        finally { gate.Release(); }
    }

    /// <summary>Only this owner writes. Unrelated records are not part of the sync transaction.</summary>
    public async Task<MusicLibrarySyncSnapshot> CommitLibrarySyncAsync(MusicLibrarySyncSnapshot expected,
        MusicLibraryAreaRecord nextLibrary, MusicLibrarySyncStateRecord nextState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(nextLibrary);
        ArgumentNullException.ThrowIfNull(nextState);
        await EnterAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (pending is { } attempt && IsAutoReconcilablePending(attempt))
                await ReconcileLockedAsync(cancellationToken).ConfigureAwait(false);
            CheckLibrarySyncLocked(expected);
            MusicRules.Require(nextLibrary.LibraryRevision == expected.Library.LibraryRevision &&
                nextState.Revision == expected.State.Revision, "SYNC_SUPERSEDED");
            var candidate = session!.Candidate(true);
            nextLibrary = ValidateLibrary(nextLibrary, scope, candidate.Songs.Contains);
            ValidateLibrarySync(nextState);
            ValidateLibrarySyncRelations(nextState, nextLibrary);
            if (!LibraryAreaEquals(nextLibrary, expected.Library))
            {
                long nextLibRev = checked(nextLibrary.LibraryRevision + 1);
                nextLibrary = nextLibrary with { StateRevision = nextLibRev, LibraryRevision = nextLibRev };
            }
            nextState = nextState with { Revision = checked(nextState.Revision + 1) };
            candidate.SetLibrary(nextLibrary);
            candidate.SetLibrarySync(nextState);
            await CommitAsync(candidate, PendingOwner.Sync, cancellationToken).ConfigureAwait(false);
            return SyncSnapshot(candidate, nextState, BeginSongObservation());
        }
        finally { gate.Release(); }
    }
}
