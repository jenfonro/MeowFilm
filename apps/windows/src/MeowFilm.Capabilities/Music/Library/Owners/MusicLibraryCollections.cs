namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static TOut[] SlicePage<TIn, TOut>(IReadOnlyList<TIn> values, long offset, int limit, Func<TIn, TOut> project)
    {
        MusicRules.Require(offset is >= 0 and <= MusicRules.MaxInteger && limit is >= 1 and <= 100);
        if (offset >= values.Count) return [];
        int start = (int)offset;
        int end = Math.Min(values.Count, start + limit);
        var page = new TOut[end - start];
        for (int i = start; i < end; i++) page[i - start] = project(values[i]);
        return page;
    }

    public Task<MusicCollectionsPage> ListCollectionsAsync(long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            var library = LibraryState(doc);
            var visibleCollections = library.Collections.Where(c => !c.Deleted).ToArray();
            var visibleNetwork = library.NetworkPlaylists.Where(r => r.Present).ToArray();
            MusicRules.Require(offset is >= 0 and <= MusicRules.MaxInteger && limit is >= 1 and <= 100);
            int total = visibleCollections.Length + visibleNetwork.Length;
            int start = (int)Math.Min(total, offset);
            int end = (int)Math.Min(total, offset + limit);
            var items = new MusicCollectionSummaryRecord[Math.Max(0, end - start)];
            for (int i = start; i < end; i++)
            {
                items[i - start] = i < visibleCollections.Length
                    ? MusicCollectionSummaryRecord.FromCollection(visibleCollections[i])
                    : MusicCollectionSummaryRecord.FromNetworkPlaylist(visibleNetwork[i - visibleCollections.Length]);
            }
            return new MusicCollectionsPage(items, total, offset, limit, library.LibraryRevision);
        }, cancellationToken);

    public Task<MusicCollectionDetail> GetCollectionDetailAsync(string id, long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(MusicRules.IsValidId(id));
            var library = LibraryState(doc);
            MusicRules.Require(!library.NetworkPlaylists.Any(r => r.PlaylistId == id), "READ_ONLY");
            var c = FindCollection(doc, library, id);
            var songs = SlicePage(c.Order, offset, limit, songId => SongReference(doc, songId));
            return new MusicCollectionDetail(
                MusicCollectionSummaryRecord.FromCollection(c),
                songs,
                c.Order.Length,
                offset,
                limit);
        }, cancellationToken);

    public Task<MusicCollectionMembership> GetCollectionMembershipAsync(string id, MusicSongRecord song, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(MusicRules.IsValidId(id));
            var library = LibraryState(doc);
            var c = FindCollection(doc, library, id);
            var normalized = NativeMusicSong.Normalize(song);
            string? member = doc.Songs.Find(normalized, c.Order.ToHashSet(StringComparer.Ordinal));
            return new MusicCollectionMembership(member is not null, member, c.Revision);
        }, cancellationToken);

    public Task<MusicLibrarySearchPage> SearchLibraryAsync(string query, long offset = 0, int limit = 50, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(MusicRules.IsValidId(query, null));
            string lowered = query.ToLowerInvariant();
            var library = LibraryState(doc);
            var matches = library.Collections.Where(c => !c.Deleted)
                .SelectMany(c => c.Order)
                .Distinct(StringComparer.Ordinal)
                .Select(songId => SongReference(doc, songId))
                .Where(s =>
                    s.Title.Contains(lowered, StringComparison.OrdinalIgnoreCase) ||
                    s.Artist.Contains(lowered, StringComparison.OrdinalIgnoreCase) ||
                    s.Album.Contains(lowered, StringComparison.OrdinalIgnoreCase))
                .Select(NativeMusicSong.Normalize)
                .ToArray();
            var page = SlicePage(matches, offset, limit, static s => s);
            return new MusicLibrarySearchPage(page, matches.Length, offset, limit, library.LibraryRevision);
        }, cancellationToken);

    public Task<MusicCollectionSummaryRecord> CreatePlaylistAsync(string name, IReadOnlyList<MusicSongRecord>? songs = null, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Library, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            MusicRules.Require(MusicRules.IsValidId(name, null));
            var library = LibraryState(doc);
            var collections = library.Collections.ToList();
            long nextId = library.NextId;
            MusicRules.Require(nextId < MusicRules.MaxInteger, "LIMIT");
            string newId = "playlist:" + nextId;
            MusicRules.Require(!collections.Any(c => c.CollectionId == newId), "CORRUPT_LIBRARY");
            nextId++;
            var order = songs is { Count: > 0 }
                ? RegisterSongs(doc, songs).Select(s => s.SongId).Distinct(StringComparer.Ordinal).ToArray()
                : [];
            var intents = new Dictionary<string, MusicCollectionIntentRecord>(order.Length, StringComparer.Ordinal);
            foreach (string songId in order)
                intents[songId] = new MusicCollectionIntentRecord(true, true, "local", null);
            var created = new MusicCollectionRecord(newId, name, "playlist", 1, true, false, true, order, [], intents);
            collections.Add(created);
            long nextLibRev = checked(library.LibraryRevision + 1);
            doc.SetLibrary(library with
            {
                StateRevision = nextLibRev,
                LibraryRevision = nextLibRev,
                NextId = nextId,
                Collections = collections.ToArray()
            });
            return (MusicCollectionSummaryRecord.FromCollection(created), true);
        }, cancellationToken);

    public Task<bool> RemoveCollectionAsync(string id, long ifRevision, CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.Library, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var library = LibraryState(doc);
            var c = AtCollectionRevision(doc, library, id, ifRevision);
            MusicRules.Require(c.Kind != "favorites", "FAVORITES_PROTECTED");
            var collections = library.Collections.ToList();
            int idx = collections.FindIndex(x => x.CollectionId == c.CollectionId);
            collections[idx] = c with { Deleted = true, Revision = checked(c.Revision + 1) };
            long nextLibRev = checked(library.LibraryRevision + 1);
            doc.SetLibrary(library with
            {
                StateRevision = nextLibRev,
                LibraryRevision = nextLibRev,
                Collections = collections.ToArray()
            });
            return (true, true);
        }, cancellationToken);

    public Task<MusicCollectionSummaryRecord> EditCollectionAsync(string id, long ifRevision, string? name = null,
        IReadOnlyList<MusicSongRecord>? songs = null, string[]? order = null, CancellationToken cancellationToken = default) =>
        MutateCollectionAsync(id, ifRevision, (doc, c) =>
        {
            MusicRules.Require(name is not null || songs is not null || order is not null);
            string nextName = c.Name;
            string[] next = c.Order;
            if (songs is not null)
                next = RegisterSongs(doc, songs).Select(s => s.SongId).Distinct(StringComparer.Ordinal).ToArray();
            if (order is not null)
            {
                MusicRules.Require(order.All(item => MusicRules.IsValidId(item)) &&
                    order.Length == next.Length &&
                    order.Distinct(StringComparer.Ordinal).Count() == next.Length &&
                    order.All(next.Contains), "INVALID_ORDER");
                next = order.ToArray();
            }
            if (name is not null)
            {
                MusicRules.Require(c.Kind != "favorites", "INVALID_NAME");
                MusicRules.Require(MusicRules.IsValidId(name, null));
                nextName = name;
            }
            return (nextName, next, true);
        }, cancellationToken);

    public Task<MusicCollectionSummaryRecord> AddSongsToCollectionAsync(string id, long ifRevision, IReadOnlyList<MusicSongRecord> songs,
        CancellationToken cancellationToken = default) =>
        MutateCollectionAsync(id, ifRevision, (doc, c) =>
        {
            MusicRules.Require(songs is not null && songs.Count > 0, "INVALID_REQUEST");
            var added = RegisterSongs(doc, songs).Select(s => s.SongId);
            var next = c.Order.Concat(added).Distinct(StringComparer.Ordinal).ToArray();
            return (c.Name, next, true);
        }, cancellationToken);

    public Task<MusicCollectionSummaryRecord> RemoveSongFromCollectionAsync(string id, long ifRevision, string songId,
        CancellationToken cancellationToken = default) =>
        MutateCollectionAsync(id, ifRevision, (_, c) =>
        {
            MusicRules.Require(MusicRules.IsValidId(songId));
            int index = Array.IndexOf(c.Order, songId);
            MusicRules.Require(index >= 0, "SONG_NOT_FOUND");
            var next = c.Order.Where(s => s != songId).ToArray();
            return (c.Name, next, true);
        }, cancellationToken);

    public Task<MusicCollectionSummaryRecord> ReorderSongInCollectionAsync(string id, long ifRevision, string songId, string targetId,
        bool after, CancellationToken cancellationToken = default) =>
        MutateCollectionAsync(id, ifRevision, (_, c) =>
        {
            MusicRules.Require(MusicRules.IsValidId(songId) && MusicRules.IsValidId(targetId));
            MusicRules.Require(songId != targetId, "INVALID_ORDER");
            var list = c.Order.ToList();
            int sourceIndex = list.IndexOf(songId);
            int targetIndex = list.IndexOf(targetId);
            MusicRules.Require(sourceIndex >= 0 && targetIndex >= 0, "SONG_NOT_FOUND");
            list.RemoveAt(sourceIndex);
            targetIndex = list.IndexOf(targetId);
            list.Insert(targetIndex + (after ? 1 : 0), songId);
            return (c.Name, list.ToArray(), false);
        }, cancellationToken);

    private static MusicCollectionRecord AtCollectionRevision(MusicLibrarySession doc, MusicLibraryAreaRecord library, string id, long ifRevision)
    {
        MusicRules.Require(MusicRules.IsValidId(id));
        MusicRules.Require(!library.NetworkPlaylists.Any(r => r.PlaylistId == id), "READ_ONLY");
        var c = FindCollection(doc, library, id);
        MusicRules.Require(c.Revision == ifRevision, "REVISION_CONFLICT");
        return c;
    }

    private Task<MusicCollectionSummaryRecord> MutateCollectionAsync(
        string id,
        long ifRevision,
        Func<MusicLibrarySession, MusicCollectionRecord, (string Name, string[] Order, bool UpdateIntents)> transform,
        CancellationToken cancellationToken) =>
        RunLibraryWriteAsync(PendingOwner.Library, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var library = LibraryState(doc);
            var c = AtCollectionRevision(doc, library, id, ifRevision);
            var (nextName, nextOrder, updateIntents) = transform(doc, c);
            IReadOnlyDictionary<string, MusicCollectionIntentRecord> intents = c.Intents;
            if (updateIntents)
            {
                var beforeSet = new HashSet<string>(c.Order, StringComparer.Ordinal);
                var nextSet = new HashSet<string>(nextOrder, StringComparer.Ordinal);
                var mutableIntents = new Dictionary<string, MusicCollectionIntentRecord>(c.Intents, StringComparer.Ordinal);
                bool unbound = c.Bindings.Length == 0;
                foreach (string songId in beforeSet)
                {
                    if (!nextSet.Contains(songId))
                    {
                        // An unbound local removal must not become a future account's delete.
                        if (unbound) mutableIntents.Remove(songId);
                        else mutableIntents[songId] = new MusicCollectionIntentRecord(false, true, "local", null);
                    }
                }
                foreach (string songId in nextSet)
                {
                    if (!beforeSet.Contains(songId))
                        mutableIntents[songId] = new MusicCollectionIntentRecord(true, true, "local", null);
                }
                intents = mutableIntents;
            }
            var updated = c with
            {
                Name = nextName,
                Order = nextOrder,
                ManualOrder = true,
                Revision = checked(c.Revision + 1),
                Intents = intents
            };
            var collections = library.Collections.ToArray();
            int idx = Array.FindIndex(collections, x => x.CollectionId == c.CollectionId);
            collections[idx] = updated;
            long nextLibRev = checked(library.LibraryRevision + 1);
            doc.SetLibrary(library with
            {
                StateRevision = nextLibRev,
                LibraryRevision = nextLibRev,
                Collections = collections
            });
            return (MusicCollectionSummaryRecord.FromCollection(updated), true);
        }, cancellationToken);
}
