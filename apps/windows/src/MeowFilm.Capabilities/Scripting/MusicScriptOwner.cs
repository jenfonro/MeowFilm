using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Scripting;

/// <summary>
/// Native source metadata and online orchestration. Only original LX execution
/// crosses the borrowed script port. The library remains the only document writer.
/// </summary>
public sealed partial class MusicScriptOwner : IScriptPlatformCapabilities, IAsyncDisposable
{
    private readonly MusicLibraryOwner library;
    private readonly IRuntimeClient runtime;
    private readonly MusicNativeTransfers transfers;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1), storageGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object activityGate = new();
    private readonly Dictionary<string, Grant> grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly HashSet<string> touched = new(StringComparer.Ordinal);
    private readonly HashSet<CancellationTokenSource> onlineOperations = [];
    private MusicSourceMetadataSnapshot? metadata;
    private JsonObject? state;
    private CancellationTokenSource? resolution, lyrics;
    private bool closed, preparing;
    private volatile bool blocked;
    private int pending;
    private TaskCompletionSource drained = Complete();
    private Task? preparation, disposal;
    private sealed class Grant(string id, string installation, JsonObject permissions, long revision)
    {
        internal string Id = id, Installation = installation;
        internal JsonObject Permissions = permissions;
        internal long Revision = revision, Generation;
        internal string State = "candidate";
        internal JsonObject Sites = new();
        internal HashSet<string>? DeclaredSites;
        internal readonly HashSet<string> AccessedSites = new(StringComparer.Ordinal);
        internal string DataHash = "";
    }
    private sealed record Live(long Revision, long Generation, string Grant, JsonObject Capabilities);

    public MusicScriptOwner(MusicLibraryOwner library, IRuntimeClient runtime, TimeProvider? clock = null)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.clock = clock ?? TimeProvider.System;
        transfers = new(this.clock);
        ScriptCapabilities = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(StringComparer.Ordinal)
        {
            ["platform.musicScript.storage.get"] = (input, token) => StorageAsync("get", input, token),
            ["platform.musicScript.storage.set"] = (input, token) => StorageAsync("set", input, token),
            ["platform.musicScript.storage.delete"] = (input, token) => StorageAsync("delete", input, token)
        };
        if (runtime is IRuntimeRecovery recovery) recovery.RegisterRecoveryScope(this);
    }
    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> ScriptCapabilities { get; }
    private static TaskCompletionSource Complete() { var result = new TaskCompletionSource(); result.SetResult(); return result; }
    private static JsonObject Parse(object? value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value ?? new { }, LJ.Json);
        try { return LJ.Object(LJ.Parse(bytes, 512 * 1024)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Code(Exception error) => error switch
    {
        MusicLibraryException e => e.Code,
        BridgeException e => e.Code,
        ProtectedDocumentException e => e.Error == DocumentStoreError.Unavailable
            ? "STORAGE_UNAVAILABLE" : "STORAGE",
        OperationCanceledException => "CANCELLED",
        JsonException or FormatException => "INVALID_REQUEST",
        _ => "SCRIPT_EXCEPTION"
    };
    private static JsonElement Okay(JsonObject result)
    {
        result["ok"] = true;
        LJ.Require(LJ.Bytes(result).Length <= 512 * 1024, "LIMIT");
        return LJ.Element(result);
    }
    private static JsonElement Fail(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
    private void Admit()
    {
        lock (activityGate)
        {
            LJ.Require(!closed && !preparing && !recoveringRuntime, "CLOSED"); LJ.Require(pending < 24, "LIMIT");
            if (pending++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
    private void Finish() { lock (activityGate) if (--pending == 0) drained.TrySetResult(); }
    public void CancelPlayback()
    {
        lock (activityGate) { resolution?.Cancel(); lyrics?.Cancel(); }
    }
    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        bool admitted = false, entered = false;
        try
        {
            var input = Parse(parameters); Admit(); admitted = true;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var token = request.Token;
            if (method == "music.online.cancel") { LJ.Fields(input, ""); CancelPlayback(); return Okay(new()); }
            if (method.StartsWith("music.sources.", StringComparison.Ordinal) && method is not ("music.sources.list" or "music.sources.detail"))
                CancelPlayback();
            if (method is "music.online.resolve" or "music.online.lyrics")
                return Okay(await OnlineAsync(method, input, token).ConfigureAwait(false));
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            token.ThrowIfCancellationRequested();
            await ReconcileBlockedWriteAsync(token).ConfigureAwait(false);
            return Okay(await CommandAsync(method, input, token).ConfigureAwait(false));
        }
        catch (Exception error) { return Fail(Code(error)); }
        finally { if (entered) gate.Release(); if (admitted) Finish(); }
    }
    private async Task EnsureAsync(CancellationToken token)
    {
        await ReconcileBlockedWriteAsync(token).ConfigureAwait(false);
        if (metadata is not null) return;
        metadata = await library.ReadSourceMetadataAsync(token).ConfigureAwait(false);
        state = LJ.Object(JsonNode.Parse(metadata.State.GetRawText()));
    }
    private async Task ReconcileBlockedWriteAsync(CancellationToken token)
    {
        if (!blocked) return;
        await RetireAllAsync(token).ConfigureAwait(false);
        await library.ReconcileAsync(token).ConfigureAwait(false);
        metadata = null;
        state = null;
        blocked = false;
    }
    private JsonArray Installations => LJ.Array(state!["installations"]);
    private JsonObject Find(string id)
    {
        LJ.Require(LJ.Id(id, 128));
        return Installations.OfType<JsonObject>().SingleOrDefault(i => LJ.S(i["id"]) == id)
            ?? throw new MusicLibraryException("NOT_FOUND");
    }
    private JsonObject View(JsonObject item)
    {
        string id = LJ.S(item["id"]);
        live.TryGetValue(id, out var active);
        var capabilities = new JsonArray();
        if (active is not null)
            foreach (var pair in active.Capabilities)
            {
                var cap = LJ.Object(pair.Value).DeepClone().AsObject();
                cap["platform"] = pair.Key; capabilities.Add(cap);
            }
        var current = LJ.Object(item["current"]);
        return LJ.Obj(("installationId", id), ("name", item["name"]), ("version", current["version"]),
            ("contentHash", current["sourceHash"]),
            ("executionHash", LJ.Digest(current)), ("sourceRevision", state!["revision"]),
            ("generation", active?.Generation ?? 0), ("enabled", LJ.B(item["enabled"])),
            ("state", active is not null ? "ready" : LJ.B(item["enabled"]) ? "idle" : "disabled"),
            ("capabilities", capabilities), ("rollbackAvailable", item["previous"] is not null), ("lastFailure", null));
    }
    private static JsonObject Permissions(JsonNode? value)
    {
        var permissions = LJ.Object(value);
        LJ.Fields(permissions, "storage", "storage");
        var storage = LJ.Array(permissions["storage"]);
        LJ.Require(storage.Count <= 2 && storage.All(n => LJ.S(n) is "read" or "write") &&
            storage.Select(n => LJ.S(n)).Distinct(StringComparer.Ordinal).Count() == storage.Count);
        return LJ.Obj(("storage", storage));
    }
    private JsonObject Next(JsonObject? replacement = null, string? removed = null)
    {
        var next = state!.DeepClone().AsObject();
        next["revision"] = LJ.I(state["revision"]) + 1;
        next["installations"] = LJ.Arr(Installations.Where(n => LJ.S(n?["id"]) != removed)
            .Select(n => replacement is not null && LJ.S(n?["id"]) == LJ.S(replacement["id"]) ? replacement : n));
        return next;
    }
    private async Task PersistAsync(JsonObject next, Grant? candidate, CancellationToken token)
    {
        await storageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            metadata = candidate is null
                ? await library.CommitSourceMetadataAsync(LJ.Element(next), metadata!.Sha256, token).ConfigureAwait(false)
                : await library.CommitSourceWithScriptDataAsync(LJ.Element(next), metadata!.Sha256,
                    candidate.Installation, LJ.Element(candidate.Sites), candidate.DataHash, token).ConfigureAwait(false);
            state = JsonNode.Parse(metadata.State.GetRawText())!.AsObject();
            foreach (var grant in grants.Values) grant.Revision = LJ.I(state["revision"]);
            if (candidate is not null) { candidate.State = "active"; candidate.Sites = new(); }
        }
        catch (MusicLibraryException error) when (error.Code is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT" or "SCRIPT_DATA_CONFLICT")
        {
            blocked = true;
            foreach (var grant in grants.Values) grant.State = "revoked";
            CancelPlayback();
            throw;
        }
        finally { storageGate.Release(); }
    }
    private async Task<JsonObject> CommandAsync(string method, JsonObject p, CancellationToken token)
    {
        if (method == "music.online.describe")
        {
            LJ.Fields(p, "");
            return LJ.Obj(("version", 1), ("search", false), ("platforms", NativeMusicSong.Platforms.Append("local").ToArray()),
                ("qualities", MusicQualityPolicy.LxTypes),
                ("trustedScriptsOnly", false), ("transfer", new { chunkBytes = MusicNativeTransfers.ChunkBytes }));
        }
        if (method == "music.transfer.begin") return transfers.Begin(p);
        if (method == "music.transfer.chunk") return transfers.Chunk(p);
        if (method == "music.transfer.cancel") return transfers.Cancel(p);
        if (method == "music.online.song")
        {
            LJ.Fields(p, "platform musicInfo title artist album duration", "platform musicInfo title");
            string platform = LJ.Text(p["platform"]); LJ.Require(NativeMusicSong.Platforms.Contains(platform));
            var info = LJ.Object(p["musicInfo"]).DeepClone().AsObject();
            var song = NativeMusicSong.CreateOnline(platform, info, LJ.Text(p["title"], 512),
                LJ.S(p["artist"]), LJ.S(p["album"]), LJ.N(p["duration"]));
            return LJ.Obj(("song", song));
        }
        await EnsureAsync(token).ConfigureAwait(false);
        if (method == "music.sources.list")
        {
            LJ.Fields(p, ""); return LJ.Obj(("revision", state!["revision"]), ("items", LJ.Arr(Installations.OfType<JsonObject>().Select(View))));
        }
        if (method == "music.sources.install")
        {
            LJ.Fields(p, "token name version permissions", "token name version permissions");
            LJ.Require(Installations.Count < 32, "LIMIT");
            string name = LJ.Text(p["name"], 255), version = LJ.Text(p["version"], 128);
            var permissions = Permissions(p["permissions"]);
            using var snapshot = transfers.Take(LJ.Text(p["token"]), "script");
            var content = await library.StoreScriptContentAsync(snapshot.Bytes, token).ConfigureAwait(false);
            var item = LJ.Obj(("id", snapshot.InstallationId), ("name", name), ("enabled", false), ("previous", null),
                ("current", LJ.Obj(("sourceHash", content.Sha256), ("sourceBytes", content.Length),
                    ("version", version), ("permissions", permissions))));
            var next = Next(); LJ.Array(next["installations"]).Add(item.DeepClone());
            await PersistAsync(next, null, token).ConfigureAwait(false);
            return LJ.Obj(("item", View(item))); // Import and ordinary listing never start Node.
        }
        string id = LJ.Text(p["installationId"], 128);
        var selected = Find(id).DeepClone().AsObject();
        if (method == "music.sources.detail")
        {
            LJ.Fields(p, "installationId", "installationId");
            return LJ.Obj(("item", View(selected)), ("permissions", selected["current"]!["permissions"]));
        }
        if (method is "music.sources.enable" or "music.sources.disable" or "music.sources.remove" or "music.sources.rollback")
            LJ.Fields(p, "installationId", "installationId");
        if (method == "music.sources.enable")
        {
            if (live.ContainsKey(id) && LJ.B(selected["enabled"])) return LJ.Obj(("item", View(selected)));
            selected["enabled"] = true;
            await ActivateAsync(selected, token).ConfigureAwait(false);
        }
        else if (method is "music.sources.disable" or "music.sources.remove")
        {
            await UnloadAsync(id, token).ConfigureAwait(false); selected["enabled"] = false;
            await PersistAsync(method.EndsWith("remove", StringComparison.Ordinal) ? Next(removed: id) : Next(selected), null, token).ConfigureAwait(false);
            if (method.EndsWith("remove", StringComparison.Ordinal)) return LJ.Obj(("removed", true));
        }
        else if (method is "music.sources.update" or "music.sources.rollback")
        {
            var previous = selected["current"]!.DeepClone();
            if (method == "music.sources.rollback")
            {
                LJ.Require(selected["previous"] is not null, "ROLLBACK_UNAVAILABLE");
                selected["current"] = selected["previous"]!.DeepClone();
            }
            else
            {
                LJ.Fields(p, "installationId token version permissions", "installationId token version permissions");
                string version = LJ.Text(p["version"], 128);
                using var snapshot = transfers.Take(LJ.Text(p["token"]), "script");
                var content = await library.StoreScriptContentAsync(snapshot.Bytes, token).ConfigureAwait(false);
                selected["current"]!["sourceHash"] = content.Sha256;
                selected["current"]!["sourceBytes"] = content.Length; selected["current"]!["version"] = version;
                selected["current"]!["permissions"] = Permissions(p["permissions"]);
            }
            selected["previous"] = previous;
            if (LJ.B(selected["enabled"])) await ActivateAsync(selected, token).ConfigureAwait(false);
            else await PersistAsync(Next(selected), null, token).ConfigureAwait(false);
        }
        else throw new MusicLibraryException("METHOD_UNAVAILABLE");
        return LJ.Obj(("item", View(Find(id))));
    }
    private static JsonObject RequireSuccess(JsonElement reply)
    {
        var value = LJ.Object(JsonNode.Parse(reply.GetRawText()));
        LJ.Require(LJ.IsBool(value["ok"]), "INVALID_RESPONSE");
        if (!LJ.B(value["ok"])) throw new MusicLibraryException(LJ.Text(value["code"]));
        value.Remove("ok"); return value;
    }
    private async Task<JsonObject> ScriptAsync(string operation, object parameters, CancellationToken token)
    {
        var reply = await runtime.CallAsync("music.script." + operation, parameters, token).ConfigureAwait(false);
        var result = RequireSuccess(reply);
        LJ.Require(LJ.S(result["contract"]) == "music.script.v2", "INVALID_RESPONSE");
        result.Remove("contract"); return result;
    }
}
