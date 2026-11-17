using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static JsonObject FreshCollection(string id, string name, string kind) =>
        LJ.Obj(("id", id), ("name", name), ("kind", kind), ("order", new JsonArray()), ("revision", 0), ("manualOrder", false),
            ("bindings", new JsonArray()), ("intents", new JsonObject()));
    private JsonObject EmptyLibrary() => LJ.Obj(("version", 2), ("ownerId", scope), ("revision", 0), ("nextId", 1),
        ("collections", new JsonArray(FreshCollection("favorites", "我的收藏", "favorites"))), ("songs", new JsonObject()),
        ("accounts", new JsonObject()), ("networkPlaylists", new JsonArray()));
    private JsonObject LibraryState(JsonObject doc)
    {
        try
        {
            var state = Area(doc, "library") ?? LJ.Obj(("schema", 2), ("scope", scope), ("revision", 0), ("library", EmptyLibrary()));
            var library = LJ.Object(state["library"], "CORRUPT_LIBRARY");
            LJ.Require(LJ.I(state["schema"]) == 2 && LJ.S(state["scope"]) == scope &&
                LJ.I(library["version"]) == 2 && LJ.S(library["ownerId"]) == scope &&
                Revision(state) == Revision(library), "CORRUPT_LIBRARY");
            return state;
        }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_LIBRARY"); }
    }
    private static JsonObject FindCollection(JsonObject library, string id)
    {
        var matches = LJ.Array(library["collections"]).OfType<JsonObject>()
            .Where(c => LJ.S(c["id"]) == id && !LJ.B(c["deleted"])).ToArray();
        LJ.Require(matches.Length <= 1, "CORRUPT_LIBRARY");
        var collection = matches.SingleOrDefault() ?? throw new MusicLibraryException("COLLECTION_NOT_FOUND");
        var order = Order(collection);
        var songs = LJ.Object(library["songs"], "CORRUPT_LIBRARY");
        LJ.Require(order.Distinct(StringComparer.Ordinal).Count() == order.Length &&
            order.All(songId => songs.ContainsKey(songId)), "CORRUPT_LIBRARY");
        return collection;
    }
    private static JsonObject CollectionView(JsonObject c) => LJ.Obj(("id", c["id"]), ("name", c["name"]), ("kind", c["kind"]),
        ("revision", c["revision"]), ("count", LJ.Array(c["order"]).Count), ("manualOrder", LJ.B(c["manualOrder"])));
    private static string[] Order(JsonObject c) => LJ.Array(c["order"]).Select(n => LJ.S(n)).ToArray();
    private static long Revision(JsonObject o) => LJ.I(o["revision"]);
    private static void Increment(JsonObject o) => o["revision"] = checked(Revision(o) + 1);
    private static void ReadDocument(JsonObject doc)
    {
        try { LJ.Require(LJ.I(doc["version"]) == 1 && doc["entries"] is JsonArray, "CORRUPT_STORAGE"); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
    }
    private static JsonObject ValidateLibrary(JsonObject raw, string owner)
    {
        try
        {
            LJ.Fields(raw, "version ownerId revision nextId collections songs accounts networkPlaylists", "version ownerId revision nextId collections songs accounts networkPlaylists");
            LJ.Require(LJ.I(raw["version"]) == 2 && LJ.S(raw["ownerId"]) == owner && LJ.Id(owner), "CORRUPT_LIBRARY");
            _ = Revision(raw); long next = LJ.I(raw["nextId"], 1);
            var songs = LJ.Object(raw["songs"]); var collections = LJ.Array(raw["collections"]); var accounts = LJ.Object(raw["accounts"]);
            var result = (JsonObject)raw.DeepClone(); var cleaned = new JsonObject();
            var nativeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in songs)
            {
                var song = LJ.Object(pair.Value, "INVALID_SONG");
                LJ.Require(Guid.TryParseExact(pair.Key, "D", out _) && LJ.S(song["songId"]) == pair.Key, "INVALID_SONG_ID");
                foreach (var source in LJ.Array(song["sources"]).OfType<JsonObject>())
                {
                    string key = LJ.S(source["sourceKey"]);
                    LJ.Require(!nativeOwners.TryGetValue(key, out string? existing) || existing == pair.Key, "NATIVE_ID_CONFLICT"); nativeOwners[key] = pair.Key;
                }
                cleaned[pair.Key] = song.DeepClone();
            }
            var ids = new HashSet<string>(); var remote = new HashSet<string>();
            long greatestPlaylist = 0;
            foreach (var n in collections)
            {
                var c = LJ.Object(n); LJ.Fields(c, "id kind name order revision manualOrder bindings intents deleted locallyCreated", "id kind name order revision manualOrder bindings intents");
                string id = LJ.Text(c["id"]), kind = LJ.S(c["kind"]); _ = LJ.Text(c["name"], 200); _ = Revision(c);
                var order = LJ.Array(c["order"]); var binds = LJ.Array(c["bindings"]); var intents = LJ.Object(c["intents"]);
                LJ.Require(ids.Add(id) && kind is "favorites" or "playlist" && LJ.IsBool(c["manualOrder"]) &&
                    order.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out string? songId) && songs.ContainsKey(songId)) &&
                    order.Select(value => LJ.S(value)).Distinct(StringComparer.Ordinal).Count() == order.Count, "INVALID_COLLECTION");
                LJ.Require(kind != "favorites" || id == "favorites" &&
                    !c.ContainsKey("deleted") && !c.ContainsKey("locallyCreated"), "INVALID_FAVORITES");
                foreach (string field in new[] { "deleted", "locallyCreated" })
                    LJ.Require(!c.ContainsKey(field) || LJ.IsBool(c[field]) && LJ.B(c[field]), "INVALID_COLLECTION");
                if (kind == "playlist")
                {
                    long number = 0;
                    LJ.Require(id.StartsWith("playlist:", StringComparison.Ordinal) &&
                        long.TryParse(id[9..], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out number) &&
                        number > 0 && number.ToString(System.Globalization.CultureInfo.InvariantCulture) == id[9..],
                        "INVALID_COLLECTION");
                    greatestPlaylist = Math.Max(greatestPlaylist, number);
                }
                var bound = new HashSet<string>();
                foreach (var b in binds.OfType<JsonObject>())
                {
                    LJ.Fields(b, "platform accountId id writable baseline map name", "platform accountId id writable baseline map name");
                    string platform = LJ.Text(b["platform"]), account = NetworkPlaylistText(b["accountId"]); _ = NetworkPlaylistText(b["id"]);
                    string key = LJ.Stringify(new JsonArray(platform, account, LJ.S(b["id"])));
                    var baseline = LJ.Array(b["baseline"]); var map = LJ.Object(b["map"]);
                    LJ.Require(SyncPlatforms.Contains(platform) && bound.Add(key) && remote.Add(key) && LJ.IsBool(b["writable"]) && LJ.B(b["writable"]) &&
                        baseline.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out string? nativeId) && nativeId is { Length: > 0 }) &&
                        (platform == "kg" || baseline.Select(value => LJ.S(value)).Distinct(StringComparer.Ordinal).Count() == baseline.Count),
                        "INVALID_BINDING");
                    _ = NetworkPlaylistText(b["name"]);
                    LJ.Require(LJ.S(accounts[platform]) == account, "ACCOUNT_MISMATCH");
                    LJ.Require(map.All(p => p.Key.Length > 0 && p.Value is JsonValue scalar &&
                        scalar.TryGetValue<string>(out string? songId) && songs.ContainsKey(songId)), "INVALID_BINDING");
                }
                LJ.Require(binds.All(b => b is JsonObject), "INVALID_BINDING");
                foreach (var intent in intents)
                {
                    var i = LJ.Object(intent.Value); LJ.Fields(i, "present pending origin acknowledged", "present pending origin");
                    JsonArray? acknowledged = null;
                    if (i.ContainsKey("acknowledged"))
                        acknowledged = LJ.Array(i["acknowledged"], "INVALID_INTENT");
                    LJ.Require(songs.ContainsKey(intent.Key) && LJ.IsBool(i["present"]) && LJ.IsBool(i["pending"]) &&
                        LJ.S(i["origin"]) is "local" or "remote" &&
                        (acknowledged is null || acknowledged.All(value => value is JsonValue scalar &&
                            scalar.TryGetValue<string>(out string? binding) && bound.Contains(binding)) &&
                            acknowledged.Select(value => LJ.S(value)).Distinct(StringComparer.Ordinal).Count() == acknowledged.Count),
                        "INVALID_INTENT");
                }
            }
            LJ.Require(collections.OfType<JsonObject>().Count(c => LJ.S(c["kind"]) == "favorites") == 1, "INVALID_FAVORITES");
            LJ.Require(collections.All(c => c is JsonObject) && next > greatestPlaylist, "INVALID_NEXT_ID");
            LJ.Require(accounts.All(p => SyncPlatforms.Contains(p.Key) && p.Value is JsonValue scalar &&
                scalar.TryGetValue<string>(out string? account) && account is { Length: > 0 }), "INVALID_ACCOUNT");
            ValidateNetworkPlaylists(raw);
            result["songs"] = cleaned;
            return result;
        }
        catch (MusicLibraryException error)
        {
            throw new MusicLibraryException(error.Code == "LIMIT" ? "LIMIT" : "CORRUPT_LIBRARY");
        }
    }
}
