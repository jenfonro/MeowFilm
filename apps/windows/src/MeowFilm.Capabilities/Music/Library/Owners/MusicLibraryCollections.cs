using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private JsonObject[] Songs(JsonObject p, bool empty)
    {
        var a = p["songs"] is null && empty ? new JsonArray() : LJ.Array(p["songs"]);
        LJ.Require(empty || a.Count > 0, "INVALID_REQUEST");
        var songs = a.Select(value => LJ.Object(value, "INVALID_SONG")).ToArray();
        return songs;
    }
    private (JsonObject Result, bool Mutate) CollectionCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        var state = LibraryState(doc); var library = LJ.Object(state["library"]); var collections = LJ.Array(library["collections"]);
        JsonObject AtRevision()
        {
            LJ.Require(!NetworkPlaylists(library).OfType<JsonObject>().Any(r => LJ.S(r["id"]) == LJ.S(p["id"])), "READ_ONLY");
            var c = FindCollection(doc, library, LJ.Text(p["id"]));
            LJ.Require(Revision(c) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT"); return c;
        }
        JsonObject DetailSong(string id) => SongReference(doc, JsonValue.Create(id));
        if (method == "music.library.list")
        {
            var items = collections.OfType<JsonObject>().Where(c => !LJ.B(c["deleted"])).Select(CollectionView)
                .Concat(NetworkPlaylists(library).OfType<JsonObject>().Where(r => LJ.B(r["present"])).Select(NetworkPlaylistView));
            return (LJ.Merge(LJ.Page(items, p), LJ.Obj(("revision", library["revision"]))), false);
        }
        if (method == "music.library.detail")
        {
            LJ.Fields(p, "id offset limit", "id");
            LJ.Require(!NetworkPlaylists(library).OfType<JsonObject>().Any(r => LJ.S(r["id"]) == LJ.S(p["id"])), "READ_ONLY");
            var c = FindCollection(doc, library, LJ.Text(p["id"]));
            return (LJ.Merge(LJ.Page(Order(c), p, DetailSong, "songs"), LJ.Obj(("collection", CollectionView(c)))), false);
        }
        if (method == "music.library.membership")
        {
            LJ.Fields(p, "id song", "id song");
            var c = FindCollection(doc, library, LJ.Text(p["id"]));
            var song = NativeMusicSong.Normalize(p["song"]);
            string? member = doc.Songs.Find(song, Order(c).ToHashSet(StringComparer.Ordinal));
            return (LJ.Obj(("present", member is not null), ("songId", member), ("revision", Revision(c))), false);
        }
        if (method == "music.library.search")
        {
            LJ.Fields(p, "query offset limit", "query"); string query = LJ.Text(p["query"], null).ToLowerInvariant();
            var matches = collections.OfType<JsonObject>().Where(c => !LJ.B(c["deleted"])).SelectMany(Order).Distinct()
                .Select(DetailSong)
                .Where(s => new[] { "title", "artist", "album" }.Any(f => LJ.S(s[f]).Contains(query, StringComparison.OrdinalIgnoreCase))).Select(NativeMusicSong.Normalize);
            return (LJ.Merge(LJ.Page(matches, p), LJ.Obj(("revision", library["revision"]))), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE");
        JsonObject result;
        if (method == "music.library.create")
        {
            LJ.Fields(p, "name songs", "name"); string name = LJ.Text(p["name"], null); var songs = Songs(p, true);
            long next = LJ.I(library["nextId"], 1); LJ.Require(next < LJ.MaxInteger, "LIMIT");
            LJ.Require(!collections.OfType<JsonObject>().Any(c => LJ.S(c["id"]) == "playlist:" + next), "CORRUPT_LIBRARY");
            var c = FreshCollection("playlist:" + next, name, "playlist"); library["nextId"] = next + 1;
            var order = RegisterSongs(doc, songs).Select(song => LJ.S(song["songId"])).Distinct(StringComparer.Ordinal).ToArray();
            c["order"] = LJ.Arr(order); c["manualOrder"] = true; c["locallyCreated"] = true; c["revision"] = 1;
            foreach (string id in order) LJ.Object(c["intents"])[id] = LJ.Obj(("present", true), ("pending", true), ("origin", "local"));
            collections.Add(c); result = LJ.Obj(("collection", CollectionView(c)));
        }
        else if (method == "music.library.remove")
        {
            LJ.Fields(p, "id ifRevision", "id ifRevision"); var c = AtRevision();
            LJ.Require(LJ.S(c["kind"]) != "favorites", "FAVORITES_PROTECTED"); c["deleted"] = true; Increment(c);
            result = LJ.Obj(("deleted", true));
        }
        else
        {
            LJ.Require(method is "music.library.edit" or "music.library.addSongs" or
                "music.library.removeSong" or "music.library.reorderSong");
            LJ.Fields(p, "id ifRevision", "id ifRevision"); var c = AtRevision(); var before = Order(c); string[] next = before;
            if (method == "music.library.edit") LJ.Require(p.ContainsKey("name") || p.ContainsKey("songs") || p.ContainsKey("order"));
            if ((method is "music.library.edit" or "music.library.addSongs") && p.ContainsKey("songs"))
            {
                var added = RegisterSongs(doc, Songs(p, method != "music.library.addSongs"))
                    .Select(song => LJ.S(song["songId"]));
                next = (method == "music.library.addSongs" ? before.Concat(added) : added)
                    .Distinct(StringComparer.Ordinal).ToArray();
            }
            else LJ.Require(method != "music.library.addSongs");
            if (method == "music.library.removeSong")
            {
                string id = LJ.Text(p["songId"]); int index = System.Array.IndexOf(before, id); LJ.Require(index >= 0, "SONG_NOT_FOUND");
                next = before.Where(s => s != id).ToArray();
            }
            if (method == "music.library.reorderSong")
            {
                string id = LJ.Text(p["songId"]);
                string targetId = LJ.Text(p["targetId"]);
                LJ.Require(id != targetId && LJ.IsBool(p["after"]), "INVALID_ORDER");
                var order = before.ToList();
                int sourceIndex = order.IndexOf(id);
                int targetIndex = order.IndexOf(targetId);
                LJ.Require(sourceIndex >= 0 && targetIndex >= 0, "SONG_NOT_FOUND");
                order.RemoveAt(sourceIndex);
                targetIndex = order.IndexOf(targetId);
                order.Insert(targetIndex + (LJ.B(p["after"]) ? 1 : 0), id);
                next = order.ToArray();
            }
            if (method == "music.library.edit" && p["order"] is not null)
            {
                var order = LJ.Array(p["order"]).Select(n => LJ.Text(n)).ToArray();
                LJ.Require(order.Length == next.Length && order.Distinct().Count() == next.Length && order.All(next.Contains), "INVALID_ORDER"); next = order;
            }
            if (method == "music.library.edit" && p["name"] is not null) { LJ.Require(LJ.S(c["kind"]) != "favorites", "INVALID_NAME"); c["name"] = LJ.Text(p["name"], null); }
            foreach (string id in before.Concat(next).Distinct())
                if (before.Contains(id) != next.Contains(id))
                {
                    var intents = LJ.Object(c["intents"]); bool present = next.Contains(id);
                    // An unbound local removal must not become a future account's delete.
                    if (!present && LJ.Array(c["bindings"]).Count == 0) intents.Remove(id);
                    else intents[id] = LJ.Obj(("present", present), ("pending", true), ("origin", "local"));
                }
            c["order"] = LJ.Arr(next); c["manualOrder"] = true; Increment(c);
            result = LJ.Obj(("collection", CollectionView(c)));
        }
        Increment(state); Increment(library); Set(doc, "library", state);
        return (result, true);
    }
}
