using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static JsonObject[] Songs(JsonObject p, bool empty, bool full = false)
    {
        var a = p["songs"] is null && empty ? new JsonArray() : LJ.Array(p["songs"]);
        LJ.Require((full || a.Count <= 1000) && (empty || a.Count > 0), "LIMIT");
        return a.Select(NativeMusicSong.Normalize).ToArray();
    }
    internal static NativeMusicSongGroups IndexLibrarySongs(JsonObject library, CancellationToken token = default)
    {
        var groups = new NativeMusicSongGroups(token);
        foreach (var pair in LJ.Object(library["songs"])) groups.Import(LJ.Object(pair.Value));
        return groups;
    }
    private static string[] AddSongs(JsonObject library, IEnumerable<JsonObject> input, NativeMusicSongGroups? groups = null)
    {
        var store = LJ.Object(library["songs"]); var ids = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var raw in input)
        {
            var incoming = new NativeMusicSong.Recordings(NativeMusicSong.Normalize(raw));
            groups ??= IndexLibrarySongs(library);
            var owners = incoming.Sources.Select(source => groups.NativeOwner(LJ.S(source["sourceKey"])))
                .OfType<NativeMusicSong.Recordings>().Distinct().ToArray();
            // A native recording has exactly one library owner; do not silently rebind collections.
            LJ.Require(owners.Length <= 1 && owners.All(owner => owner.CanMerge(incoming)), "NATIVE_ID_CONFLICT");
            var group = groups.Add(incoming, preferIncoming: true);
            store[group.Id] = group.ToSong();
            if (ids.Add(group.Id)) ordered.Add(group.Id);
        }
        return ordered.ToArray();
    }
    private (JsonObject Result, bool Mutate) CollectionCommand(string method, JsonObject p, JsonObject doc, bool full)
    {
        var state = LibraryState(doc); var library = LJ.Object(state["library"]); var collections = LJ.Array(library["collections"]);
        JsonObject AtRevision()
        {
            var c = FindCollection(library, LJ.Text(p["id"]));
            LJ.Require(Revision(c) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT"); return c;
        }
        JsonObject DetailSong(string id) => NativeMusicSong.Normalize(LJ.Object(library["songs"])[id]);
        if (method == "music.library.list")
        {
            LJ.Fields(p, "offset limit");
            return (LJ.Merge(LJ.Page(collections.OfType<JsonObject>().Where(c => !LJ.B(c["deleted"])).Select(CollectionView), p), LJ.Obj(("revision", library["revision"]))), false);
        }
        if (method == "music.library.detail")
        {
            LJ.Fields(p, "id offset limit", "id"); var c = FindCollection(library, LJ.Text(p["id"]));
            return (LJ.Merge(LJ.Page(Order(c).Select(DetailSong), p, "songs"), LJ.Obj(("collection", CollectionView(c)))), false);
        }
        if (method == "music.library.membership")
        {
            LJ.Fields(p, "id song", "id song");
            var c = FindCollection(library, LJ.Text(p["id"]));
            var song = new NativeMusicSong.Recordings(NativeMusicSong.Normalize(p["song"]));
            var store = LJ.Object(library["songs"]); var groups = new NativeMusicSongGroups();
            foreach (string id in Order(c)) groups.Import(LJ.Object(store[id]));
            string? member = groups.Find(song)?.Id;
            return (LJ.Obj(("present", member is not null), ("songId", member), ("revision", Revision(c))), false);
        }
        if (method == "music.library.search")
        {
            LJ.Fields(p, "query offset limit", "query"); string query = LJ.Text(p["query"], 200).ToLowerInvariant();
            var matches = collections.OfType<JsonObject>().Where(c => !LJ.B(c["deleted"])).SelectMany(Order).Distinct()
                .Select(id => LJ.Object(library["songs"])[id]).OfType<JsonObject>()
                .Where(s => new[] { "title", "artist", "album" }.Any(f => LJ.S(s[f]).Contains(query, StringComparison.OrdinalIgnoreCase))).Select(NativeMusicSong.Normalize);
            return (LJ.Merge(LJ.Page(matches, p), LJ.Obj(("revision", library["revision"]))), false);
        }
        LJ.Require(pending is null, "PENDING_WRITE");
        JsonObject result;
        if (method == "music.library.create")
        {
            LJ.Fields(p, "name songs", "name"); string name = LJ.Text(p["name"], 200); var songs = Songs(p, true, full);
            long next = LJ.I(library["nextId"], 1); LJ.Require(next < LJ.MaxInteger, "LIMIT");
            var c = FreshCollection("playlist:" + next, name, "playlist"); library["nextId"] = next + 1;
            var order = AddSongs(library, songs); c["order"] = LJ.Arr(order); c["manualOrder"] = true; c["locallyCreated"] = true; c["revision"] = 1;
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
            string allowed = method switch
            {
                "music.library.edit" => "id ifRevision name songs order",
                "music.library.addSongs" => "id ifRevision songs",
                "music.library.removeSong" => "id ifRevision songId",
                "music.library.reorderSong" => "id ifRevision songId targetId after",
                _ => throw new MusicLibraryException("INVALID_REQUEST")
            };
            LJ.Fields(p, allowed, "id ifRevision"); var c = AtRevision(); var before = Order(c); string[] next = before;
            if (method == "music.library.edit") LJ.Require(p.ContainsKey("name") || p.ContainsKey("songs") || p.ContainsKey("order"));
            if (p.ContainsKey("songs")) next = AddSongs(library, method == "music.library.addSongs" ?
                before.Select(id => LJ.Object(LJ.Object(library["songs"])[id])).Concat(Songs(p, false, full)) : Songs(p, true, full));
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
            if (p["order"] is not null)
            {
                var order = LJ.Array(p["order"]).Select(n => LJ.Text(n)).ToArray();
                LJ.Require(order.Length == next.Length && order.Distinct().Count() == next.Length && order.All(next.Contains), "INVALID_ORDER"); next = order;
            }
            if (p["name"] is not null) { LJ.Require(LJ.S(c["kind"]) != "favorites", "INVALID_NAME"); c["name"] = LJ.Text(p["name"], 200); }
            foreach (string id in before.Concat(next).Distinct())
                if (before.Contains(id) != next.Contains(id)) LJ.Object(c["intents"])[id] = LJ.Obj(("present", next.Contains(id)), ("pending", true), ("origin", "local"));
            c["order"] = LJ.Arr(next); c["manualOrder"] = true; Increment(c);
            result = LJ.Obj(("collection", CollectionView(c)));
        }
        Increment(state); Increment(library); Set(doc, "library", state);
        return (result, true);
    }
}
