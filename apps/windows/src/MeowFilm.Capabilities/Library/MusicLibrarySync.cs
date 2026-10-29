using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

/// <summary>Immutable optimistic token issued by one library owner; contains no account credentials.</summary>
public sealed class MusicLibrarySyncSnapshot
{
    internal MusicLibrarySyncSnapshot(MusicLibraryOwner owner, JsonObject library, JsonObject state)
    { Owner = owner; Library = LJ.Element(library); State = LJ.Element(state); Stamp = LJ.Digest(new JsonArray(library.DeepClone(), state.DeepClone())); }
    internal MusicLibraryOwner Owner { get; }
    internal string Stamp { get; }
    public JsonElement Library { get; }
    public JsonElement State { get; }
}

public sealed partial class MusicLibraryOwner
{
    public const string LibrarySyncContractVersion = "1.0.0";
    private const string LibrarySyncArea = "library-sync";
    internal static readonly string[] SyncPlatforms = ["wy", "tx", "kg"];
    private static readonly HashSet<string> ConfirmationCodes =
        ["EMPTY_REMOTE", "BULK_REMOTE_DELETE", "DELETE_REMOTE_PLAYLIST", "BULK_LOCAL_DELETE"];
    internal static JsonObject EmptyLibrarySync()
    {
        var platforms = new JsonObject();
        foreach (string platform in SyncPlatforms) platforms[platform] = LJ.Obj(("favorites", false), ("playlists", false), ("accountId", ""));
        return LJ.Obj(("schema", 1), ("revision", 0), ("platforms", platforms), ("pending", null),
            ("confirmation", null), ("approved", null), ("status", LJ.Obj(("phase", "idle"), ("code", ""), ("at", 0), ("issues", new JsonArray()))));
    }
    private JsonObject LibrarySync(JsonObject doc) => HasValue(doc, LibrarySyncArea)
        ? LJ.Object(Value(doc, LibrarySyncArea), "CORRUPT_SYNC") : EmptyLibrarySync();
    private static void ValidateLibrarySync(JsonObject s)
    {
        try
        {
            LJ.Fields(s, "schema revision platforms pending confirmation approved status", "schema revision platforms pending confirmation approved status");
            LJ.Require(LJ.I(s["schema"]) == 1); _ = Revision(s);
            var platforms = LJ.Object(s["platforms"]); LJ.Fields(platforms, "wy tx kg", "wy tx kg");
            foreach (var platform in SyncPlatforms)
            {
                var p = LJ.Object(platforms[platform]); LJ.Fields(p, "favorites playlists accountId", "favorites playlists accountId");
                LJ.Require(LJ.IsBool(p["favorites"]) && LJ.IsBool(p["playlists"]) &&
                    p["accountId"] is JsonValue accountValue && accountValue.TryGetValue<string>(out _));
                string accountId = LJ.S(p["accountId"]);
                LJ.Require(LJ.B(p["favorites"]) || LJ.B(p["playlists"]) ?
                    LJ.Id(accountId, 64) : accountId.Length == 0 || LJ.Id(accountId, 64));
            }
            if (s["pending"] is JsonObject w)
            {
                LJ.Fields(w, "platform accountId collectionId remoteId operation before desired ids name directory", "platform accountId collectionId remoteId operation before desired ids name directory");
                LJ.Require(SyncPlatforms.Contains(LJ.S(w["platform"])) && LJ.Id(LJ.S(w["accountId"]), 64) && LJ.Id(LJ.S(w["collectionId"])));
                string operation = LJ.S(w["operation"]);
                LJ.Require(operation is "create" or "delete" or "add" or "remove" or "order");
                foreach (string field in new[] { "before", "desired", "ids", "directory" })
                    LJ.Require(LJ.Array(w[field]).Count <= 10000 && LJ.Array(w[field]).All(value =>
                        value is JsonValue scalar && scalar.TryGetValue<string>(out string? id) && LJ.Id(id)));
                string remoteId = LJ.S(w["remoteId"]);
                LJ.Require(LJ.Id(LJ.S(w["name"]), 200) &&
                    (operation == "create" ? remoteId.Length == 0 : LJ.Id(remoteId, 240)));
            }
            else LJ.Require(s["pending"] is null);
            foreach (string field in new[] { "confirmation", "approved" })
                if (s[field] is JsonObject v)
                {
                    LJ.Fields(v, "token code count", "token code count");
                    LJ.Require(LJ.S(v["token"]).Length == 64 && LJ.S(v["token"]).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                        ConfirmationCodes.Contains(LJ.S(v["code"])));
                    _ = LJ.I(v["count"], 0, 100000);
                }
                else LJ.Require(s[field] is null);
            LJ.Require(s["confirmation"] is null || s["approved"] is null);
            var status = LJ.Object(s["status"]); LJ.Fields(status, "phase code at issues", "phase code at issues");
            string phase = LJ.S(status["phase"]), code = LJ.S(status["code"]);
            LJ.Require(phase is "idle" or "pushing" or "synced" or "partial" or "blocked" or "superseded" &&
                code.Length <= 80);
            _ = LJ.I(status["at"]);
            var issues = LJ.Array(status["issues"]);
            LJ.Require(issues.Count <= 256);
            foreach (var issue in issues.OfType<JsonObject>())
            {
                LJ.Fields(issue, "platform collectionId code", "platform collectionId code");
                LJ.Require(SyncPlatforms.Contains(LJ.S(issue["platform"])) && LJ.Id(LJ.S(issue["collectionId"])) &&
                    LJ.Id(LJ.S(issue["code"]), 80));
            }
            LJ.Require(issues.All(issue => issue is JsonObject));
            LJ.Require(phase switch
            {
                "idle" or "synced" => code.Length == 0 && issues.Count == 0,
                "pushing" => code.Length == 0,
                "partial" => code.Length == 0 && issues.Count > 0,
                "blocked" or "superseded" => code.Length > 0,
                _ => false
            });
            bool awaitingConfirmation = phase == "blocked" && code == "CONFIRMATION_REQUIRED";
            LJ.Require((s["confirmation"] is JsonObject) == awaitingConfirmation);
            if (awaitingConfirmation) LJ.Require(s["pending"] is null);
        }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_SYNC"); }
    }
    private static void ValidateLibrarySyncRelations(JsonObject state, JsonObject library)
    {
        try
        {
            var collections = LJ.Array(library["collections"]).OfType<JsonObject>()
                .ToDictionary(collection => LJ.S(collection["id"]), StringComparer.Ordinal);
            if (state["pending"] is JsonObject pending)
            {
                string collectionId = LJ.S(pending["collectionId"]);
                LJ.Require(collections.TryGetValue(collectionId, out var collection));
                string platform = LJ.S(pending["platform"]), account = LJ.S(pending["accountId"]),
                    operation = LJ.S(pending["operation"]), remote = LJ.S(pending["remoteId"]);
                string[] before = LJ.Array(pending["before"]).Select(value => LJ.S(value)).ToArray();
                string[] desired = LJ.Array(pending["desired"]).Select(value => LJ.S(value)).ToArray();
                string[] ids = LJ.Array(pending["ids"]).Select(value => LJ.S(value)).ToArray();
                string[] directory = LJ.Array(pending["directory"]).Select(value => LJ.S(value)).ToArray();
                LJ.Require(LJ.S(state["platforms"]![platform]!["accountId"]) == account &&
                    LJ.S(library["accounts"]![platform]) == account &&
                    ids.Distinct(StringComparer.Ordinal).Count() == ids.Length &&
                    directory.Distinct(StringComparer.Ordinal).Count() == directory.Length);
                LJ.Require(operation switch
                {
                    "create" => LJ.S(collection!["kind"]) == "playlist" && remote.Length == 0 &&
                        before.Length == 0 && desired.Length == 0 && ids.Length == 0,
                    "delete" => remote.Length > 0 && desired.Length == 0 && ids.Length == 0 && directory.Length == 0,
                    "add" => remote.Length > 0 && ids.Length > 0 && directory.Length == 0 &&
                        ids.All(id => !before.Contains(id, StringComparer.Ordinal)) &&
                        desired.SequenceEqual(before.Concat(ids), StringComparer.Ordinal),
                    "remove" => remote.Length > 0 && ids.Length > 0 && directory.Length == 0 &&
                        ids.All(id => before.Contains(id, StringComparer.Ordinal)) &&
                        desired.SequenceEqual(before.Where(id => !ids.Contains(id, StringComparer.Ordinal)), StringComparer.Ordinal),
                    "order" => remote.Length > 0 && ids.Length == 0 && directory.Length == 0 &&
                        !desired.SequenceEqual(before, StringComparer.Ordinal) &&
                        desired.OrderBy(id => id, StringComparer.Ordinal)
                            .SequenceEqual(before.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal),
                    _ => false
                });
                var accountBindings = LJ.Array(collection!["bindings"]).OfType<JsonObject>().Where(binding =>
                    LJ.S(binding["platform"]) == platform && LJ.S(binding["accountId"]) == account).ToArray();
                LJ.Require(operation == "create"
                    ? accountBindings.Length == 0
                    : accountBindings.Count(binding => LJ.S(binding["id"]) == remote) == 1);
            }
            foreach (var issue in LJ.Array(state["status"]!["issues"]).OfType<JsonObject>())
                LJ.Require(collections.ContainsKey(LJ.S(issue["collectionId"])));
        }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_SYNC"); }
    }
    public async Task<MusicLibrarySyncSnapshot> ReadLibrarySyncAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LJ.Require(!closed && !preparing, "CLOSED"); LJ.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            var state = LibrarySync(document!); ValidateLibrarySync(state);
            return new(this, LJ.Object(LibraryState(document!)["library"]), state);
        }
        finally { gate.Release(); }
    }
    private MusicLibrarySyncSnapshot CheckLibrarySyncLocked(MusicLibrarySyncSnapshot expected)
    {
        LJ.Require(ReferenceEquals(expected.Owner, this), "INVALID_SYNC_TOKEN");
        LJ.Require(!closed && !preparing, "CLOSED"); LJ.Require(pending is null, pending?.Code ?? "PENDING_WRITE");
        var current = new MusicLibrarySyncSnapshot(this, LJ.Object(LibraryState(document!)["library"]), LibrarySync(document!));
        LJ.Require(expected.Stamp == current.Stamp, "SYNC_SUPERSEDED"); return current;
    }
    public async Task CheckLibrarySyncAsync(MusicLibrarySyncSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); await EnterAsync(cancellationToken).ConfigureAwait(false);
        try { _ = CheckLibrarySyncLocked(expected); } finally { gate.Release(); }
    }
    /// <summary>Only this owner writes. Unrelated queue/settings changes are retained in the same envelope CAS.</summary>
    public async Task<MusicLibrarySyncSnapshot> CommitLibrarySyncAsync(MusicLibrarySyncSnapshot expected,
        JsonElement library, JsonElement state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var nextLibrary = LJ.Object(LJ.Parse(System.Text.Encoding.UTF8.GetBytes(library.GetRawText())));
        var nextState = LJ.Object(LJ.Parse(System.Text.Encoding.UTF8.GetBytes(state.GetRawText())));
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = CheckLibrarySyncLocked(expected);
            nextLibrary = ValidateLibrary(nextLibrary, scope); ValidateLibrarySync(nextState);
            LJ.Require(Revision(nextLibrary) == expected.Library.GetProperty("revision").GetInt64() &&
                Revision(nextState) == expected.State.GetProperty("revision").GetInt64(), "SYNC_SUPERSEDED");
            var candidate = (JsonObject)document!.DeepClone(); var wrapper = (JsonObject)LibraryState(candidate).DeepClone();
            if (!JsonNode.DeepEquals(nextLibrary, JsonNode.Parse(expected.Library.GetRawText())))
            { Increment(nextLibrary); Increment(wrapper); }
            Increment(nextState); wrapper["library"] = nextLibrary.DeepClone();
            Set(candidate, "library", wrapper); Set(candidate, LibrarySyncArea, nextState);
            await CommitAsync(candidate, PendingOwner.Sync, cancellationToken).ConfigureAwait(false);
            return new(this, nextLibrary, nextState);
        }
        finally { gate.Release(); }
    }
    internal static string[] AddLibrarySyncSongs(JsonObject library, IEnumerable<JsonObject> songs) => AddSongs(library, songs);

}
