using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static MusicSongRecord[] ReadSongsParam(JsonElement p, bool empty)
    {
        if (!MusicWireCodec.TryGet(p, "songs", out var a) || a.ValueKind == JsonValueKind.Null)
        {
            MusicRules.Require(empty, "INVALID_REQUEST");
            return [];
        }
        MusicRules.Require(a.ValueKind == JsonValueKind.Array, "INVALID_REQUEST");
        int count = a.GetArrayLength();
        MusicRules.Require(empty || count > 0, "INVALID_REQUEST");
        var songs = new MusicSongRecord[count];
        int i = 0;
        foreach (var item in a.EnumerateArray())
            songs[i++] = MusicWireCodec.ReadSong(item, "INVALID_SONG");
        return songs;
    }

    private (JsonElement Result, bool Mutate) CollectionCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        var library = LibraryState(doc);

        MusicCollectionRecord AtRevision()
        {
            string id = MusicWireCodec.RequireIdProp(p, "id");
            MusicRules.Require(!library.NetworkPlaylists.Any(r => r.PlaylistId == id), "READ_ONLY");
            var c = FindCollection(doc, library, id);
            MusicRules.Require(c.Revision == MusicWireCodec.RequireIntProp(p, "ifRevision"), "REVISION_CONFLICT");
            return c;
        }

        if (method == "music.library.list")
        {
            var visibleCollections = library.Collections.Where(c => !c.Deleted).ToArray();
            var visibleNetwork = library.NetworkPlaylists.Where(r => r.Present).ToArray();
            var (offset, limit) = MusicWireCodec.ReadPageArgs(p);
            int total = visibleCollections.Length + visibleNetwork.Length;
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("items");
                w.WriteStartArray();
                int end = (int)Math.Min(total, offset + limit);
                for (int i = (int)offset; i < end; i++)
                {
                    if (i < visibleCollections.Length)
                        MusicWireCodec.WriteCollectionView(w, visibleCollections[i]);
                    else
                        MusicWireCodec.WriteNetworkPlaylistView(w, visibleNetwork[i - visibleCollections.Length]);
                }
                w.WriteEndArray();
                w.WriteNumber("total", total);
                w.WriteNumber("offset", offset);
                w.WriteNumber("limit", limit);
                w.WriteNumber("revision", library.LibraryRevision);
            }), false);
        }

        if (method == "music.library.detail")
        {
            MusicWireCodec.RequireFields(p, "id");
            string id = MusicWireCodec.RequireIdProp(p, "id");
            MusicRules.Require(!library.NetworkPlaylists.Any(r => r.PlaylistId == id), "READ_ONLY");
            var c = FindCollection(doc, library, id);
            return (MusicWireCodec.Ok(w =>
            {
                MusicWireCodec.WritePage(w, c.Order, p, (writer, songId) =>
                    MusicWireCodec.WriteSong(writer, SongReference(doc, songId)), "songs");
                w.WritePropertyName("collection");
                MusicWireCodec.WriteCollectionView(w, c);
            }), false);
        }

        if (method == "music.library.membership")
        {
            MusicWireCodec.RequireFields(p, "id song");
            var c = FindCollection(doc, library, MusicWireCodec.RequireIdProp(p, "id"));
            var song = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(p.GetProperty("song")));
            string? member = doc.Songs.Find(song, c.Order.ToHashSet(StringComparer.Ordinal));
            return (MusicWireCodec.Ok(w =>
            {
                w.WriteBoolean("present", member is not null);
                if (member is not null) w.WriteString("songId", member); else w.WriteNull("songId");
                w.WriteNumber("revision", c.Revision);
            }), false);
        }

        if (method == "music.library.search")
        {
            MusicWireCodec.RequireFields(p, "query");
            string query = MusicWireCodec.RequireIdProp(p, "query", null).ToLowerInvariant();
            var matches = library.Collections.Where(c => !c.Deleted)
                .SelectMany(c => c.Order)
                .Distinct(StringComparer.Ordinal)
                .Select(songId => SongReference(doc, songId))
                .Where(s =>
                    s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    s.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    s.Album.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(NativeMusicSong.Normalize)
                .ToArray();
            return (MusicWireCodec.Ok(w =>
            {
                MusicWireCodec.WritePage(w, matches, p, MusicWireCodec.WriteSong);
                w.WriteNumber("revision", library.LibraryRevision);
            }), false);
        }

        MusicRules.Require(pending is null, "PENDING_WRITE");
        var collections = library.Collections.ToList();
        long nextId = library.NextId;
        JsonElement result;

        if (method == "music.library.create")
        {
            MusicWireCodec.RequireFields(p, "name");
            string name = MusicWireCodec.RequireIdProp(p, "name", null);
            var songs = ReadSongsParam(p, empty: true);
            MusicRules.Require(nextId < MusicRules.MaxInteger, "LIMIT");
            string newId = "playlist:" + nextId;
            MusicRules.Require(!collections.Any(c => c.CollectionId == newId), "CORRUPT_LIBRARY");
            nextId++;
            var order = RegisterSongs(doc, songs).Select(s => s.SongId).Distinct(StringComparer.Ordinal).ToArray();
            var intents = new Dictionary<string, MusicCollectionIntentRecord>(order.Length, StringComparer.Ordinal);
            foreach (string songId in order)
                intents[songId] = new MusicCollectionIntentRecord(true, true, "local", null);
            var created = new MusicCollectionRecord(newId, name, "playlist", 1, true, false, true, order, [], intents);
            collections.Add(created);
            result = MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("collection");
                MusicWireCodec.WriteCollectionView(w, created);
            });
        }
        else if (method == "music.library.remove")
        {
            MusicWireCodec.RequireFields(p, "id ifRevision");
            var c = AtRevision();
            MusicRules.Require(c.Kind != "favorites", "FAVORITES_PROTECTED");
            var updated = c with { Deleted = true, Revision = checked(c.Revision + 1) };
            int idx = collections.FindIndex(x => x.CollectionId == c.CollectionId);
            collections[idx] = updated;
            result = MusicWireCodec.Ok(w => w.WriteBoolean("deleted", true));
        }
        else
        {
            MusicRules.Require(method is "music.library.edit" or "music.library.addSongs" or
                "music.library.removeSong" or "music.library.reorderSong");
            MusicWireCodec.RequireFields(p, "id ifRevision");
            var c = AtRevision();
            var before = c.Order;
            string[] next = before;
            string nextName = c.Name;

            if (method == "music.library.edit")
                MusicRules.Require(MusicWireCodec.Has(p, "name") || MusicWireCodec.Has(p, "songs") || MusicWireCodec.Has(p, "order"));

            if ((method is "music.library.edit" or "music.library.addSongs") && MusicWireCodec.Has(p, "songs"))
            {
                var added = RegisterSongs(doc, ReadSongsParam(p, method != "music.library.addSongs"))
                    .Select(s => s.SongId);
                next = (method == "music.library.addSongs" ? before.Concat(added) : added)
                    .Distinct(StringComparer.Ordinal).ToArray();
            }
            else MusicRules.Require(method != "music.library.addSongs");

            if (method == "music.library.removeSong")
            {
                string id = MusicWireCodec.RequireIdProp(p, "songId");
                int index = Array.IndexOf(before, id);
                MusicRules.Require(index >= 0, "SONG_NOT_FOUND");
                next = before.Where(s => s != id).ToArray();
            }

            if (method == "music.library.reorderSong")
            {
                string id = MusicWireCodec.RequireIdProp(p, "songId");
                string targetId = MusicWireCodec.RequireIdProp(p, "targetId");
                bool after = MusicWireCodec.RequireBoolProp(p, "after", "INVALID_ORDER");
                MusicRules.Require(id != targetId, "INVALID_ORDER");
                var order = before.ToList();
                int sourceIndex = order.IndexOf(id);
                int targetIndex = order.IndexOf(targetId);
                MusicRules.Require(sourceIndex >= 0 && targetIndex >= 0, "SONG_NOT_FOUND");
                order.RemoveAt(sourceIndex);
                targetIndex = order.IndexOf(targetId);
                order.Insert(targetIndex + (after ? 1 : 0), id);
                next = order.ToArray();
            }

            if (method == "music.library.edit" && MusicWireCodec.HasNonNull(p, "order"))
            {
                var order = MusicWireCodec.RequireIdArrayProp(p, "order");
                MusicRules.Require(order.Length == next.Length &&
                    order.Distinct(StringComparer.Ordinal).Count() == next.Length &&
                    order.All(next.Contains), "INVALID_ORDER");
                next = order;
            }

            if (method == "music.library.edit" && MusicWireCodec.HasNonNull(p, "name"))
            {
                MusicRules.Require(c.Kind != "favorites", "INVALID_NAME");
                nextName = MusicWireCodec.RequireIdProp(p, "name", null);
            }

            var beforeSet = before.ToHashSet(StringComparer.Ordinal);
            var nextSet = next.ToHashSet(StringComparer.Ordinal);
            var intents = new Dictionary<string, MusicCollectionIntentRecord>(c.Intents, StringComparer.Ordinal);
            foreach (string id in before.Concat(next).Distinct(StringComparer.Ordinal))
            {
                if (beforeSet.Contains(id) != nextSet.Contains(id))
                {
                    bool present = nextSet.Contains(id);
                    // An unbound local removal must not become a future account's delete.
                    if (!present && c.Bindings.Length == 0) intents.Remove(id);
                    else intents[id] = new MusicCollectionIntentRecord(present, true, "local", null);
                }
            }

            var updated = c with
            {
                Name = nextName,
                Order = next,
                ManualOrder = true,
                Revision = checked(c.Revision + 1),
                Intents = intents
            };
            int idx = collections.FindIndex(x => x.CollectionId == c.CollectionId);
            collections[idx] = updated;
            result = MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("collection");
                MusicWireCodec.WriteCollectionView(w, updated);
            });
        }

        long nextLibRev = checked(library.LibraryRevision + 1);
        var nextLibrary = library with
        {
            StateRevision = nextLibRev,
            LibraryRevision = nextLibRev,
            NextId = nextId,
            Collections = collections.ToArray()
        };
        doc.SetLibrary(nextLibrary);
        return (result, true);
    }
}
