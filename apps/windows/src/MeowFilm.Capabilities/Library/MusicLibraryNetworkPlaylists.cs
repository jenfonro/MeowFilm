using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    // Installed by the existing coordinator; called only after the local CAS has left the owner gate.
    internal Func<string, CancellationToken, Task<JsonElement>>? SynchronizeNetworkPlaylists { get; set; }

    internal static string NetworkPlaylistId(string platform, string remoteId)
    {
        LJ.Require(NativeMusicSong.Platforms.Contains(platform) &&
            Regex.IsMatch(remoteId, platform is "wy" or "tx" or "kw" ? "^[1-9][0-9]{0,19}$" : "^[A-Za-z0-9_-]{1,200}$") &&
            (platform != "kg" || !remoteId.StartsWith("id_", StringComparison.Ordinal)), "INVALID_PLAYLIST_ID");
        return "network:" + platform + ":" + remoteId;
    }
    internal static JsonArray NetworkPlaylists(JsonObject library) => LJ.Array(library["networkPlaylists"]);
    internal static JsonObject NetworkPlaylistView(JsonObject reference) => LJ.Obj(
        ("id", reference["id"]), ("kind", "network"), ("platform", reference["platform"]), ("remoteId", reference["remoteId"]),
        ("name", reference["name"]), ("cover", reference["cover"]), ("count", reference["count"]), ("revision", reference["revision"]),
        ("editable", false));
    internal static JsonObject NewNetworkPlaylist(JsonObject library, string platform, string remoteId, string name, string cover, long count) => LJ.Obj(
        ("id", NetworkPlaylistId(platform, remoteId)), ("platform", platform), ("remoteId", remoteId), ("name", name),
        ("cover", NativeMusicSong.PublicUrl(JsonValue.Create(cover))), ("count", count), ("revision", checked(Revision(library) + 1)), ("present", true),
        ("subscription", null), ("intent", null));
    private static void ValidateNetworkPlaylists(JsonObject library)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in NetworkPlaylists(library))
        {
            var reference = LJ.Object(item);
            LJ.Fields(reference, "id platform remoteId name cover count revision present subscription intent",
                "id platform remoteId name cover count revision present subscription intent");
            string platform = LJ.Text(reference["platform"]), remoteId = LJ.Text(reference["remoteId"], 200);
            LJ.Require(LJ.S(reference["id"]) == NetworkPlaylistId(platform, remoteId) && ids.Add(LJ.S(reference["id"])));
            _ = LJ.Text(reference["name"], 200); _ = Revision(reference); _ = LJ.I(reference["count"], 0, int.MaxValue);
            LJ.Require(LJ.IsBool(reference["present"]) && reference["cover"] is JsonValue cover &&
                cover.TryGetValue<string>(out _) && LJ.S(reference["cover"]) == NativeMusicSong.PublicUrl(reference["cover"]));
            if (reference["subscription"] is JsonObject subscription)
            {
                LJ.Fields(subscription, "accountId present locator", "accountId present locator");
                LJ.Require(SyncPlatforms.Contains(platform) && LJ.Text(subscription["accountId"], 64) == LJ.S(library["accounts"]![platform]) &&
                    LJ.IsBool(subscription["present"]));
                var locator = LJ.Object(subscription["locator"]);
                LJ.Fields(locator, platform == "kg" ? "listId" : "", platform == "kg" && LJ.B(subscription["present"]) ? "listId" : "");
                if (locator["listId"] is not null)
                    LJ.Require(platform == "kg" && LJ.B(subscription["present"]) &&
                        Regex.IsMatch(LJ.Text(locator["listId"], 20), "^[1-9][0-9]{0,19}$"));
            }
            else LJ.Require(reference["subscription"] is null);
            if (reference["intent"] is JsonObject intent)
            {
                LJ.Fields(intent, "accountId present", "accountId present");
                LJ.Require(SyncPlatforms.Contains(platform) && LJ.Id(LJ.S(intent["accountId"]), 64) &&
                    LJ.IsBool(intent["present"]) && LJ.B(intent["present"]) == LJ.B(reference["present"]));
                string account = LJ.S(library["accounts"]![platform]);
                LJ.Require(account.Length == 0 || account == LJ.S(intent["accountId"]));
            }
            else LJ.Require(reference["intent"] is null);
        }
    }
    private (JsonObject Result, bool Mutate) NetworkPlaylistCommand(string method, JsonObject p, JsonObject doc)
    {
        var wrapper = LibraryState(doc); var library = LJ.Object(wrapper["library"]); var references = NetworkPlaylists(library);
        if (method == "music.library.network.list")
        {
            LJ.Fields(p, "offset limit");
            return (LJ.Merge(LJ.Page(references.OfType<JsonObject>().Where(r => LJ.B(r["present"])).Select(NetworkPlaylistView), p),
                LJ.Obj(("revision", library["revision"]))), false);
        }
        if (method == "music.library.network.get")
        {
            LJ.Fields(p, "platform id", "platform id");
            string key = NetworkPlaylistId(LJ.Text(p["platform"], 2), LJ.Text(p["id"], 200));
            var reference = references.OfType<JsonObject>().FirstOrDefault(r => LJ.S(r["id"]) == key);
            return (LJ.Obj(("present", reference is not null && LJ.B(reference["present"])),
                ("collection", reference is null ? null : NetworkPlaylistView(reference))), false);
        }
        LJ.Require(method is "music.library.network.save" or "music.library.network.remove", "INVALID_REQUEST");
        JsonObject selected;
        string platform;
        bool added = false;
        JsonNode? before;
        if (method == "music.library.network.save")
        {
            LJ.Fields(p, "platform id name cover count", "platform id name");
            platform = LJ.Text(p["platform"], 2); string id = LJ.Text(p["id"], 200), key = NetworkPlaylistId(platform, id);
            string name = LJ.Text(p["name"], 200);
            string cover = p.ContainsKey("cover") ? NativeMusicSong.PublicUrl(p["cover"]) : "";
            long count = p.ContainsKey("count") ? LJ.I(p["count"], 0, int.MaxValue) : 0;
            var existing = references.OfType<JsonObject>().FirstOrDefault(r => LJ.S(r["id"]) == key);
            selected = existing ?? NewNetworkPlaylist(library, platform, id, name, cover, count);
            before = existing?.DeepClone(); added = existing is null;
            if (added) references.Add(selected);
            selected["present"] = true;
            selected["name"] = name;
            if (p.ContainsKey("cover")) selected["cover"] = cover;
            if (p.ContainsKey("count")) selected["count"] = count;
        }
        else
        {
            LJ.Fields(p, "id ifRevision", "id ifRevision");
            selected = references.OfType<JsonObject>().FirstOrDefault(r => LJ.S(r["id"]) == LJ.Text(p["id"]))
                ?? throw new MusicLibraryException("COLLECTION_NOT_FOUND");
            LJ.Require(Revision(selected) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
            before = selected.DeepClone(); platform = LJ.S(selected["platform"]); selected["present"] = false;
        }
        var settings = SyncPlatforms.Contains(platform) ? LibrarySync(doc)["platforms"]![platform] as JsonObject : null;
        bool enabled = settings is not null && LJ.B(settings["playlists"]);
        string accountId = LJ.S(settings?["accountId"]);
        bool presenceChanged = added || LJ.B(before?["present"]) != LJ.B(selected["present"]);
        if (accountId.Length > 0 && (presenceChanged || selected["subscription"] is JsonObject baseline &&
            LJ.B(baseline["present"]) != LJ.B(selected["present"])))
            selected["intent"] = LJ.Obj(("accountId", accountId), ("present", LJ.B(selected["present"])));
        bool changed = !JsonNode.DeepEquals(before, selected);
        if (changed)
        {
            if (!added) Increment(selected);
            bool fenced = LibrarySync(doc)["pending"] is JsonObject fence && LJ.S(fence["collectionId"]) == LJ.S(selected["id"]);
            if (!LJ.B(selected["present"]) && selected["subscription"] is null && selected["intent"] is null && !fenced)
                references.Remove(selected);
            Increment(wrapper); Increment(library); Set(doc, "library", wrapper);
        }
        return (LJ.Obj(("collection", NetworkPlaylistView(selected)), ("present", LJ.B(selected["present"])),
            ("localSaved", true), ("synchronize", enabled)), changed);
    }
    private async Task<JsonElement> ExecuteNetworkPlaylistMutationAsync(string method, object? parameters, CancellationToken token)
    {
        var local = await ExecuteCoreAsync(method, parameters, token, false).ConfigureAwait(false);
        if (!local.GetProperty("ok").GetBoolean()) return local;
        var result = JsonNode.Parse(local.GetRawText())!.AsObject();
        bool synchronize = LJ.B(result["synchronize"]); result.Remove("synchronize");
        JsonElement sync = JsonSerializer.SerializeToElement(new { ok = true, state = "local-only" });
        if (synchronize)
        {
            try
            {
                sync = SynchronizeNetworkPlaylists is { } run
                    ? await run(LJ.S(result["collection"]!["platform"]), token).ConfigureAwait(false)
                    : JsonSerializer.SerializeToElement(new { ok = false, code = "SYNC_UNAVAILABLE" });
            }
            catch (Exception error)
            {
                string code = error switch { MusicLibraryException e => e.Code, OperationCanceledException => "CANCELLED", _ => "SYNC_FAILED" };
                sync = JsonSerializer.SerializeToElement(new { ok = false, code });
            }
        }
        result["sync"] = JsonNode.Parse(sync.GetRawText());
        return LJ.Element(result);
    }
}
