using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly string[] CurrentAreas =
    [
        "library", LibrarySyncArea, "queue", "history", "online-settings",
        CatalogSourceArea, OfficialSourcesArea, QualityPolicyArea, MetadataPolicyArea, AppearanceArea, DeviceSettingsArea,
        SourceExclusionsArea,
        SearchPolicyArea, SearchHistoryArea, LocalLyricsArea, "playback-settings"
    ];

    private static JsonObject FreshCollection(string id, string name, string kind) =>
        LJ.Obj(("id", id), ("name", name), ("kind", kind), ("order", new JsonArray()), ("revision", 0), ("manualOrder", false),
            ("bindings", new JsonArray()), ("intents", new JsonObject()));
    private JsonObject EmptyLibrary() => LJ.Obj(("version", 2), ("ownerId", scope), ("revision", 0), ("nextId", 1),
        ("collections", new JsonArray(FreshCollection("favorites", "我的收藏", "favorites"))), ("songs", new JsonObject()),
        ("accounts", new JsonObject()), ("networkPlaylists", new JsonArray()));
    private JsonObject LibraryState(JsonObject doc) => Value(doc, "library") as JsonObject ??
        LJ.Obj(("schema", 2), ("scope", scope), ("revision", 0), ("library", EmptyLibrary()));
    private static JsonObject FindCollection(JsonObject library, string id) => LJ.Array(library["collections"]).OfType<JsonObject>()
        .FirstOrDefault(c => LJ.S(c["id"]) == id && !LJ.B(c["deleted"])) ?? throw new MusicLibraryException("COLLECTION_NOT_FOUND");
    private static JsonObject CollectionView(JsonObject c) => LJ.Obj(("id", c["id"]), ("name", c["name"]), ("kind", c["kind"]),
        ("revision", c["revision"]), ("count", LJ.Array(c["order"]).Count), ("manualOrder", LJ.B(c["manualOrder"])));
    private static string[] Order(JsonObject c) => LJ.Array(c["order"]).Select(n => LJ.S(n)).ToArray();
    private static long Revision(JsonObject o) => LJ.I(o["revision"]);
    private static void Increment(JsonObject o) => o["revision"] = checked(Revision(o) + 1);
    private bool TryCurrentAreaKey(string encoded, out string area)
    {
        foreach (string candidate in CurrentAreas)
            if (encoded == Key(candidate)) { area = candidate; return true; }
        area = "";
        return false;
    }

    private void ValidateDocument(JsonObject doc)
    {
        try
        {
            LJ.Fields(doc, "version entries", "version entries");
            LJ.Require(LJ.I(doc["version"]) == 1, "CORRUPT_STORAGE");
            var entries = LJ.Array(doc["entries"]);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in entries)
            {
                var entry = LJ.Object(n); LJ.Fields(entry, "key value", "key value");
                string key = LJ.S(entry["key"]); LJ.Require(key.Length is > 0 and <= 8192 && keys.Add(key), "CORRUPT_STORAGE");
                if (TryCurrentAreaKey(key, out _) || IsRetiredArea(key)) continue;
                throw new MusicLibraryException("CORRUPT_STORAGE");
            }
            JsonObject? currentLibrary = null;
            if (HasValue(doc, "library"))
            {
                var state = LJ.Object(Value(doc, "library"), "CORRUPT_LIBRARY");
                LJ.Fields(state, "schema scope revision library", "schema scope revision library");
                LJ.Require(LJ.I(state["schema"]) == 2 && LJ.S(state["scope"]) == scope, "CORRUPT_LIBRARY");
                long wrapperRevision = Revision(state);
                currentLibrary = ValidateLibrary(LJ.Object(state["library"]), scope);
                LJ.Require(wrapperRevision == Revision(currentLibrary), "CORRUPT_LIBRARY");
            }
            if (HasValue(doc, LibrarySyncArea))
            {
                var sync = LJ.Object(Value(doc, LibrarySyncArea), "CORRUPT_SYNC");
                ValidateLibrarySync(sync);
                LJ.Require(currentLibrary is not null, "CORRUPT_SYNC");
                ValidateLibrarySyncRelations(sync, currentLibrary);
            }
            if (HasValue(doc, "queue")) ValidateQueue(LJ.Object(Value(doc, "queue"), "CORRUPT_QUEUE"), true);
            if (HasValue(doc, "history")) ValidateHistory(LJ.Object(Value(doc, "history"), "CORRUPT_HISTORY"));
            if (HasValue(doc, "online-settings"))
            {
                var settings = LJ.Object(Value(doc, "online-settings"), "CORRUPT_STORAGE");
                ValidateSettings(settings);
            }
            if (HasValue(doc, CatalogSourceArea))
                ValidateCatalogSource(LJ.Object(Value(doc, CatalogSourceArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, OfficialSourcesArea))
                ValidateOfficialSources(LJ.Object(Value(doc, OfficialSourcesArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, QualityPolicyArea)) ValidateQualityPolicy(LJ.Object(Value(doc, QualityPolicyArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, MetadataPolicyArea)) ValidateMetadataPolicy(LJ.Object(Value(doc, MetadataPolicyArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, AppearanceArea)) ValidateAppearance(LJ.Object(Value(doc, AppearanceArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, DeviceSettingsArea)) ValidateDeviceSettings(LJ.Object(Value(doc, DeviceSettingsArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, SourceExclusionsArea))
                ValidateSourceExclusions(LJ.Object(Value(doc, SourceExclusionsArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, SearchPolicyArea)) ValidateSearchPolicy(LJ.Object(Value(doc, SearchPolicyArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, SearchHistoryArea)) ValidateSearchHistory(LJ.Object(Value(doc, SearchHistoryArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, LocalLyricsArea)) ValidateLocalLyrics(LJ.Object(Value(doc, LocalLyricsArea), "CORRUPT_STORAGE"));
            if (HasValue(doc, "playback-settings"))
            {
                var mode = LJ.Object(Value(doc, "playback-settings"), "CORRUPT_STORAGE"); LJ.Fields(mode, "schema mode", "schema mode");
                LJ.Require(LJ.I(mode["schema"]) == 1, "CORRUPT_STORAGE"); _ = LJ.I(mode["mode"], 1, 3);
            }
        }
        catch (MusicLibraryException e) when (e.Code == "INVALID_REQUEST") { throw new MusicLibraryException("CORRUPT_STORAGE"); }
    }
    private static JsonObject ValidateLibrary(JsonObject raw, string owner)
    {
        try
        {
            LJ.Fields(raw, "version ownerId revision nextId collections songs accounts networkPlaylists",
                "version ownerId revision nextId collections songs accounts networkPlaylists");
            LJ.Require(LJ.I(raw["version"]) == 2 && LJ.S(raw["ownerId"]) == owner && LJ.Id(owner), "CORRUPT_LIBRARY");
            _ = Revision(raw); long next = LJ.I(raw["nextId"], 1);
            var songs = LJ.Object(raw["songs"]); var collections = LJ.Array(raw["collections"]); var accounts = LJ.Object(raw["accounts"]);
            LJ.Require(songs.Count <= 100000 && collections.Count <= 10000, "LIMIT");
            var result = (JsonObject)raw.DeepClone(); var cleaned = new JsonObject();
            var nativeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in songs)
            {
                var song = LJ.Object(pair.Value, "INVALID_SONG");
                LJ.Require(LJ.Id(pair.Key) && LJ.S(song["songId"]) == pair.Key, "INVALID_SONG_ID");
                var normalized = NativeMusicSong.Normalize(song);
                LJ.Require(JsonNode.DeepEquals(song, normalized), "INVALID_SONG");
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
                var c = LJ.Object(n); LJ.Fields(c, "id kind name order revision manualOrder bindings intents deleted locallyCreated",
                    "id kind name order revision manualOrder bindings intents");
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
                    LJ.Fields(b, "platform accountId id writable baseline map name",
                        "platform accountId id writable baseline map name");
                    string platform = LJ.Text(b["platform"]), account = LJ.Text(b["accountId"]); _ = LJ.Text(b["id"]);
                    string key = LJ.Stringify(new JsonArray(platform, account, LJ.S(b["id"])));
                    var baseline = LJ.Array(b["baseline"]); var map = LJ.Object(b["map"]);
                    LJ.Require(SyncPlatforms.Contains(platform) && bound.Add(key) && remote.Add(key) && LJ.IsBool(b["writable"]) && LJ.B(b["writable"]) &&
                        baseline.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out string? nativeId) && LJ.Id(nativeId)) &&
                        (platform == "kg" || baseline.Select(value => LJ.S(value)).Distinct(StringComparer.Ordinal).Count() == baseline.Count),
                        "INVALID_BINDING");
                    _ = LJ.Text(b["name"], 200);
                    LJ.Require(LJ.S(accounts[platform]) == account, "ACCOUNT_MISMATCH");
                    LJ.Require(map.All(p => LJ.Id(p.Key) && p.Value is JsonValue scalar &&
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
                scalar.TryGetValue<string>(out string? account) && LJ.Id(account, 64)), "INVALID_ACCOUNT");
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
