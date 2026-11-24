namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Installed by the existing coordinator; called only after the local CAS has left the owner gate.
    internal Func<string, CancellationToken, Task<MusicNetworkPlaylistSyncStatus>>? SynchronizeNetworkPlaylists { get; set; }

    internal static string NetworkPlaylistId(string platform, string remoteId)
    {
        MusicRules.Require(NativeMusicSong.Platforms.Contains(platform) && remoteId.Length > 0, "INVALID_PLAYLIST_ID");
        return "network:" + platform + ":" + remoteId;
    }

    internal static MusicNetworkPlaylistRecord NewNetworkPlaylist(
        MusicLibraryAreaRecord library, string platform, string remoteId, string name, string cover, long count) =>
        new(
            NetworkPlaylistId(platform, remoteId),
            platform,
            remoteId,
            name,
            NativeMusicSong.PublicUrl(cover),
            count,
            checked(library.LibraryRevision + 1),
            true,
            null, null, null, null, null, null, null);

    private static void ValidateNetworkPlaylists(MusicLibraryAreaRecord library)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in library.NetworkPlaylists)
        {
            MusicRules.Require(reference.Platform.Length > 0 && reference.RemoteId.Length > 0 &&
                reference.Name.Length > 0 && reference.Revision is >= 0 and <= MusicRules.MaxInteger &&
                reference.SongCount is >= 0 and <= int.MaxValue);
            MusicRules.Require(reference.PlaylistId == NetworkPlaylistId(reference.Platform, reference.RemoteId) &&
                ids.Add(reference.PlaylistId));
            MusicRules.Require(reference.Cover == NativeMusicSong.PublicUrl(reference.Cover));
            if (reference.SubAccountId is not null || reference.SubPresent is not null)
            {
                MusicRules.Require(reference.SubAccountId is { Length: > 0 } && reference.SubPresent is not null &&
                    SyncPlatforms.Contains(reference.Platform) &&
                    library.Accounts.TryGetValue(reference.Platform, out var acc) && acc == reference.SubAccountId);
            }
            if (reference.IntentAccountId is not null || reference.IntentPresent is not null)
            {
                MusicRules.Require(reference.IntentAccountId is { Length: > 0 } && reference.IntentPresent is { } intentPresent &&
                    SyncPlatforms.Contains(reference.Platform) && intentPresent == reference.Present);
                string account = library.Accounts.TryGetValue(reference.Platform, out var acc) ? acc : "";
                MusicRules.Require(account.Length == 0 || account == reference.IntentAccountId);
            }
        }
    }

    public Task<MusicNetworkPlaylistMutationResult> SaveNetworkPlaylistAsync(string platform, string id, string name,
        string? cover = null, long? count = null, CancellationToken cancellationToken = default) =>
        MutateNetworkPlaylistAsync(doc =>
        {
            MusicRules.Require(platform.Length > 0 && id.Length > 0 && name.Length > 0);
            if (count is { } c) MusicRules.Require(c is >= 0 and <= int.MaxValue);
            var library = LibraryState(doc);
            var references = library.NetworkPlaylists.ToList();
            string key = NetworkPlaylistId(platform, id);
            int existingIndex = references.FindIndex(r => r.PlaylistId == key);
            var before = existingIndex >= 0 ? references[existingIndex] : null;
            bool added = before is null;
            string cleanCover = cover is not null ? NativeMusicSong.PublicUrl(cover) : "";
            var selected = before is null
                ? NewNetworkPlaylist(library, platform, id, name, cleanCover, count ?? 0)
                : before with
                {
                    Present = true,
                    Name = name,
                    Cover = cover is not null ? cleanCover : before.Cover,
                    SongCount = count ?? before.SongCount
                };
            return ApplyNetworkPlaylistMutation(doc, library, references, existingIndex, before, selected, added, platform);
        }, cancellationToken);

    public Task<MusicNetworkPlaylistMutationResult> RemoveNetworkPlaylistAsync(string id, long ifRevision, CancellationToken cancellationToken = default) =>
        MutateNetworkPlaylistAsync(doc =>
        {
            MusicRules.Require(id.Length > 0);
            var library = LibraryState(doc);
            var references = library.NetworkPlaylists.ToList();
            int existingIndex = references.FindIndex(r => r.PlaylistId == id);
            if (existingIndex < 0) throw new MusicLibraryException("COLLECTION_NOT_FOUND");
            var before = references[existingIndex];
            MusicRules.Require(before.Revision == ifRevision, "REVISION_CONFLICT");
            var selected = before with { Present = false };
            return ApplyNetworkPlaylistMutation(doc, library, references, existingIndex, before, selected, false, before.Platform);
        }, cancellationToken);

    private static ((MusicNetworkPlaylistRecord Selected, bool Synchronize) Result, bool Mutate) ApplyNetworkPlaylistMutation(
        MusicLibrarySession doc,
        MusicLibraryAreaRecord library,
        List<MusicNetworkPlaylistRecord> references,
        int existingIndex,
        MusicNetworkPlaylistRecord? before,
        MusicNetworkPlaylistRecord selected,
        bool added,
        string platform)
    {
        var syncState = LibrarySync(doc);
        var platformSettings = SyncPlatforms.Contains(platform) && syncState.Platforms.TryGetValue(platform, out var ps) ? ps : null;
        bool enabled = platformSettings is { Playlists: true };
        string accountId = platformSettings?.AccountId ?? "";
        bool presenceChanged = added || before!.Present != selected.Present;
        if (accountId.Length > 0 && (presenceChanged || selected.SubPresent is { } subPresent && subPresent != selected.Present))
            selected = selected with { IntentAccountId = accountId, IntentPresent = selected.Present };

        bool changed = before != selected;
        if (changed)
        {
            if (!added) selected = selected with { Revision = checked(selected.Revision + 1) };
            bool fenced = syncState.Pending is { } fence && fence.CollectionId == selected.PlaylistId;
            bool hasSub = selected.SubAccountId is not null;
            bool hasIntent = selected.IntentAccountId is not null;
            if (!selected.Present && !hasSub && !hasIntent && !fenced)
            {
                if (existingIndex >= 0) references.RemoveAt(existingIndex);
            }
            else if (existingIndex >= 0)
            {
                references[existingIndex] = selected;
            }
            else
            {
                references.Add(selected);
            }

            long nextRev = checked(library.LibraryRevision + 1);
            doc.SetLibrary(library with
            {
                StateRevision = nextRev,
                LibraryRevision = nextRev,
                NetworkPlaylists = references.ToArray()
            });
        }
        return ((selected, enabled), changed);
    }

    private async Task<MusicNetworkPlaylistMutationResult> MutateNetworkPlaylistAsync(
        Func<MusicLibrarySession, ((MusicNetworkPlaylistRecord Selected, bool Synchronize) Result, bool Mutate)> write,
        CancellationToken token)
    {
        var (selected, synchronize) = await RunLibraryWriteAsync(PendingOwner.NetworkPlaylists, write, token).ConfigureAwait(false);
        var sync = new MusicNetworkPlaylistSyncStatus(true, State: "local-only");
        if (synchronize)
        {
            try
            {
                sync = SynchronizeNetworkPlaylists is { } run
                    ? await run(selected.Platform, token).ConfigureAwait(false)
                    : new MusicNetworkPlaylistSyncStatus(false, Code: "SYNC_UNAVAILABLE");
            }
            catch (Exception error)
            {
                string code = error switch { MusicLibraryException e => e.Code, OperationCanceledException => "CANCELLED", _ => "SYNC_FAILED" };
                sync = new MusicNetworkPlaylistSyncStatus(false, Code: code);
            }
        }
        return new MusicNetworkPlaylistMutationResult(
            MusicCollectionSummaryRecord.FromNetworkPlaylist(selected),
            selected.Present,
            LocalSaved: true,
            sync);
    }
}
