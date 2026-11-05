using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Files;

namespace MeowFilm.Capabilities.Library;

public sealed record MusicSourceMetadataSnapshot(JsonElement State, string Sha256);
public sealed record MusicSourceScriptSnapshot(JsonElement Sites, string Sha256);

public sealed partial class MusicLibraryOwner
{
    internal const int MaximumScriptSites = 32;
    internal static bool IsScriptIdentifier(string value) => value.Length is > 0 and <= 128 &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout);
    private JsonObject SourceState(JsonObject doc) => Value(doc, "lx-installations") as JsonObject ??
        LJ.Obj(("schema", 4), ("revision", 0), ("installations", new JsonArray()));
    private JsonObject Settings(JsonObject doc) => Value(doc, "online-settings") as JsonObject ??
        LJ.Obj(("schema", 3), ("revision", 0), ("lyrics", true));
    private static string InstallationId(string id)
    {
        LJ.Require(id.Length <= 128 && Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9._:-]*$", RegexOptions.CultureInvariant, NativeMusicSong.RegexTimeout));
        return id;
    }
    private static void ValidateSources(JsonObject state)
    {
        LJ.Fields(state, "schema revision installations", "schema revision installations");
        LJ.Require(LJ.I(state["schema"]) == 4, "CORRUPT_STORAGE"); _ = Revision(state);
        var installations = LJ.Array(state["installations"]);
        var ids = new HashSet<string>();
        foreach (var n in installations)
        {
            var item = LJ.Object(n); LJ.Fields(item, "id name enabled previous current", "id name enabled previous current");
            LJ.Require(ids.Add(InstallationId(LJ.Text(item["id"]))) && LJ.IsBool(item["enabled"])); _ = LJ.Text(item["name"], 255);
            void Check(JsonNode? n)
            {
                var r = LJ.Object(n); LJ.Fields(r, "sourceHash sourceBytes version permissions", "sourceHash sourceBytes version permissions");
                ContentIdentity(LJ.S(r["sourceHash"]),
                    (int)LJ.I(r["sourceBytes"], 1, SelectedScriptSnapshot.MaximumBytes));
                _ = LJ.Text(r["version"], 128);
                var permissions = LJ.Object(r["permissions"]);
                LJ.Fields(permissions, "storage", "storage");
                var storage = LJ.Array(permissions["storage"]);
                var storageValues = storage.Select(value => LJ.S(value)).ToArray();
                LJ.Require(storage.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out _)) &&
                    storageValues.All(value => value is "read" or "write") &&
                    storageValues.Distinct(StringComparer.Ordinal).Count() == storageValues.Length);
            }
            Check(item["current"]); if (item["previous"] is not null) Check(item["previous"]);
        }
        LJ.Require(LJ.Bytes(state).Length <= 3 * 1024 * 1024, "LIMIT");
    }
    private static void ValidateSettings(JsonObject settings)
    {
        LJ.Fields(settings, "schema revision lyrics", "schema revision lyrics");
        LJ.Require(LJ.I(settings["schema"]) == 3 && LJ.IsBool(settings["lyrics"]), "CORRUPT_STORAGE");
        _ = Revision(settings);
    }
    /// <summary>Trusted native scripting manager only. Does not execute/activate any source.</summary>
    public async Task<MusicSourceMetadataSnapshot> ReadSourceMetadataAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED"); await LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LJ.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
            var state = SourceState(document!); return new(LJ.Element(state), LJ.Digest(state));
        }
        finally { gate.Release(); }
    }
    /// <summary>Expected metadata SHA and next revision are checked inside the same owner gate and whole-document CAS.</summary>
    public Task<MusicSourceMetadataSnapshot> CommitSourceMetadataAsync(JsonElement state, string expectedSha256,
        CancellationToken cancellationToken = default) =>
        CommitSourceAsync(state, expectedSha256, null, cancellationToken);

    private sealed record ScriptCommit(string InstallationId, JsonObject Sites, string ExpectedSha256);

    /// <summary>Trusted native manager snapshot for a candidate's in-memory storage overlay.
    /// It intentionally does not require the current installation to be enabled or even present.</summary>
    public async Task<MusicSourceScriptSnapshot> ReadSourceScriptDataAsync(string installationId,
        long expectedSourceRevision, CancellationToken cancellationToken = default)
    {
        _ = InstallationId(installationId);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED"); await LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LJ.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
            LJ.Require(Revision(SourceState(document!)) == expectedSourceRevision, "STALE_SESSION");
            var sites = ScriptSites(document!, installationId);
            return new(LJ.Element(sites), LJ.Digest(sites));
        }
        finally { gate.Release(); }
    }
    /// <summary>Atomically installs one successful candidate and its isolated storage overlay.
    /// Never expose this trusted manager port to script RPC.</summary>
    public Task<MusicSourceMetadataSnapshot> CommitSourceWithScriptDataAsync(JsonElement state,
        string expectedMetadataSha, string installationId, JsonElement sites, string expectedScriptDataSha,
        CancellationToken cancellationToken = default)
    {
        _ = InstallationId(installationId);
        var nextSites = LJ.Object(LJ.Parse(JsonSerializer.SerializeToUtf8Bytes(sites, LJ.Json), 512 * 1024));
        ValidateSites(nextSites);
        return CommitSourceAsync(state, expectedMetadataSha, new(installationId, nextSites, expectedScriptDataSha), cancellationToken);
    }
    private async Task<MusicSourceMetadataSnapshot> CommitSourceAsync(JsonElement state, string expectedSha256,
        ScriptCommit? script, CancellationToken cancellationToken)
    {
        var next = LJ.Object(LJ.Parse(JsonSerializer.SerializeToUtf8Bytes(state, LJ.Json), 3 * 1024 * 1024));
        ValidateSources(next);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED"); await LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LJ.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
            var previous = SourceState(document!);
            LJ.Require(LJ.Digest(previous) == expectedSha256 && Revision(next) == Revision(previous) + 1, "REVISION_CONFLICT");
            if (script is not null)
            {
                LJ.Require(LJ.Array(next["installations"]).Any(i => LJ.S(i?["id"]) == script.InstallationId), "NOT_FOUND");
                var oldOthers = LJ.Arr(LJ.Array(previous["installations"]).Where(i => LJ.S(i?["id"]) != script.InstallationId));
                var newOthers = LJ.Arr(LJ.Array(next["installations"]).Where(i => LJ.S(i?["id"]) != script.InstallationId));
                LJ.Require(LJ.Digest(oldOthers) == LJ.Digest(newOthers), "REVISION_CONFLICT");
                LJ.Require(LJ.Digest(ScriptSites(document!, script.InstallationId)) == script.ExpectedSha256, "SCRIPT_DATA_CONFLICT");
            }
            var removed = LJ.Array(previous["installations"]).Select(i => LJ.S(i?["id"]))
                .Except(LJ.Array(next["installations"]).Select(i => LJ.S(i?["id"]))).ToHashSet();
            var candidate = (JsonObject)document!.DeepClone(); Set(candidate, "lx-installations", next);
            var entries = LJ.Array(candidate["entries"]);
            foreach (var entry in entries.OfType<JsonObject>().ToArray())
                if (TryScriptAreaKey(LJ.S(entry["key"]), out string installation) &&
                    (removed.Contains(installation) || script?.InstallationId == installation))
                    entries.Remove(entry);
            if (script is not null)
                foreach (var site in script.Sites) Set(candidate, ScriptArea(script.InstallationId, site.Key), site.Value!);
            await CommitAsync(candidate, PendingOwner.SourceMetadata, cancellationToken).ConfigureAwait(false);
            return new(LJ.Element(next), LJ.Digest(next));
        }
        finally { gate.Release(); }
    }
    private string ScriptArea(string installationId, string siteId)
    {
        _ = InstallationId(installationId); LJ.Require(IsScriptIdentifier(siteId));
        return LJ.Stringify(new JsonArray("lx-data", installationId, siteId));
    }
    private bool TryScriptAreaKey(string encoded, out string installation) => TryScriptAreaKey(encoded, out installation, out _);
    private bool TryScriptAreaKey(string encoded, out string installation, out string site)
    {
        installation = ""; site = "";
        try
        {
            if (JsonNode.Parse(encoded) is not JsonArray { Count: 5 } key ||
                LJ.S(key[0]) != "meowfilm" || LJ.S(key[1]) != "music" || LJ.N(key[2]) != 1 || LJ.S(key[3]) != scope ||
                JsonNode.Parse(LJ.S(key[4])) is not JsonArray { Count: 3 } area || LJ.S(area[0]) != "lx-data") return false;
            installation = InstallationId(LJ.Text(area[1]));
            site = LJ.Text(area[2]); _ = ScriptArea(installation, site);
            return encoded == Key(ScriptArea(installation, site));
        }
        catch (Exception error) when (error is JsonException or MusicLibraryException) { return false; }
    }
    private JsonObject ScriptSites(JsonObject doc, string installation)
    {
        var sites = new JsonObject();
        foreach (var entry in LJ.Array(doc["entries"]).OfType<JsonObject>())
            if (TryScriptAreaKey(LJ.S(entry["key"]), out string id, out string site) && id == installation)
            {
                LJ.Require(!sites.ContainsKey(site), "CORRUPT_STORAGE");
                sites[site] = entry["value"]?.DeepClone();
            }
        ValidateSites(sites); return sites;
    }
    private static void ValidateSites(JsonObject sites)
    {
        LJ.Require(sites.Count <= MaximumScriptSites && LJ.Bytes(sites).Length <= 512 * 1024, "LIMIT");
        foreach (var site in sites)
        {
            LJ.Require(IsScriptIdentifier(site.Key), "INVALID_REQUEST");
            ValidateScriptStorage(LJ.Object(site.Value));
        }
    }
    private static void ValidateScriptStorage(JsonObject state)
    {
        LJ.Fields(state, "schema values", "schema values");
        LJ.Require(LJ.I(state["schema"]) == 1, "CORRUPT_STORAGE");
        var values = LJ.Array(state["values"]); var keys = new HashSet<string>(StringComparer.Ordinal);
        LJ.Require(values.Count <= 64 && LJ.Bytes(state).Length <= 65536, "LIMIT");
        foreach (var n in values)
        {
            var entry = LJ.Object(n); LJ.Fields(entry, "key value", "key value");
            LJ.Require(LJ.Id(LJ.S(entry["key"]), 256) && keys.Add(LJ.S(entry["key"])), "CORRUPT_STORAGE");
            LJ.Require(JsonSerializer.SerializeToUtf8Bytes(entry["value"], LJ.Json).Length <= 16384, "LIMIT");
        }
    }
    private void ScriptAccess(string installation, long revision, string permission)
    {
        var state = SourceState(document!); LJ.Require(Revision(state) == revision, "STALE_SESSION");
        var item = LJ.Array(state["installations"]).OfType<JsonObject>().FirstOrDefault(s => LJ.S(s["id"]) == installation);
        LJ.Require(item is not null && LJ.B(item["enabled"]) &&
            (item["current"]?["permissions"]?["storage"] as JsonArray ?? []).Any(p => LJ.S(p) == permission), "PERMISSION_DENIED");
    }
    public async Task<JsonElement> ReadScriptValueAsync(string installationId, string siteId, string key, long expectedSourceRevision,
        CancellationToken cancellationToken = default) =>
        await ScriptValueAsync(installationId, siteId, key, expectedSourceRevision, null, false, false, cancellationToken).ConfigureAwait(false);
    public async Task<JsonElement> CommitScriptValueAsync(string installationId, string siteId, string key, long expectedSourceRevision,
        JsonElement? value, bool remove, CancellationToken cancellationToken = default) =>
        await ScriptValueAsync(installationId, siteId, key, expectedSourceRevision, value, true, remove, cancellationToken).ConfigureAwait(false);
    private async Task<JsonElement> ScriptValueAsync(string installation, string site, string key, long revision,
        JsonElement? value, bool write, bool remove, CancellationToken token)
    {
        string area = ScriptArea(installation, site); LJ.Require(LJ.Id(key, 256));
        byte[] supplied = JsonSerializer.SerializeToUtf8Bytes(value, LJ.Json);
        bool entered = false;
        try
        {
            LJ.Require(!write || remove || supplied.Length <= 16384, "LIMIT");
            JsonNode? suppliedValue = write && !remove && value is { ValueKind: not JsonValueKind.Null }
                ? LJ.Parse(supplied, 16384) : null;
            await EnterAsync(token).ConfigureAwait(false); entered = true;
            LJ.Require(!closed && !preparing, "CLOSED"); await LoadAsync(token).ConfigureAwait(false); LJ.Require(pending is null, "PENDING_WRITE");
            token.ThrowIfCancellationRequested();
            ScriptAccess(installation, revision, write ? "write" : "read");
            var state = Value(document!, area) as JsonObject ?? LJ.Obj(("schema", 1), ("values", new JsonArray()));
            ValidateScriptStorage(state);
            var values = LJ.Array(state["values"]);
            if (!write) return JsonSerializer.SerializeToElement(values.OfType<JsonObject>().FirstOrDefault(v => LJ.S(v["key"]) == key)?["value"], LJ.Json);
            var changed = (JsonObject)state.DeepClone();
            changed["values"] = LJ.Arr(values.Where(v => LJ.S(v?["key"]) != key));
            if (!remove) LJ.Array(changed["values"]).Add(LJ.Obj(("key", key), ("value", suppliedValue)));
            LJ.Require(LJ.Array(changed["values"]).Count <= 64 && LJ.Bytes(changed).Length <= 65536, "LIMIT");
            var doc = (JsonObject)document!.DeepClone(); Set(doc, area, changed);
            await CommitAsync(doc, PendingOwner.ScriptData, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement<object?>(null);
        }
        finally { if (entered) gate.Release(); }
    }
    private (JsonObject Result, bool Mutate) SettingsCommand(string method, JsonObject p, JsonObject doc)
    {
        if (method == "music.settings.get") { LJ.Fields(p, ""); return (LJ.Obj(("settings", Settings(doc))), false); }
        LJ.Fields(p, "ifRevision lyrics", "ifRevision lyrics");
        LJ.Require(pending is null, "PENDING_WRITE"); var settings = Settings(doc);
        LJ.Require(Revision(settings) == LJ.I(p["ifRevision"]), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 3), ("revision", Revision(settings) + 1), ("lyrics", p["lyrics"]));
        ValidateSettings(next);
        Set(doc, "online-settings", next); return (LJ.Obj(("settings", next)), true);
    }
}
