using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Scripting;

public sealed partial class MusicScriptOwner
{
    // Called only while the native source gate is held. Reverse storage uses its
    // separate gate and can never wait for the load operation that invoked it.
    private async Task ActivateAsync(JsonObject selected, CancellationToken token)
    {
        string id = LJ.S(selected["id"]);
        var next = Next(selected); long revision = LJ.I(next["revision"]);
        live.TryGetValue(id, out var previous);
        Grant? old = null;
        var candidate = new Grant(Guid.NewGuid().ToString("D"), id,
            LJ.Object(selected["current"]!["permissions"]).DeepClone().AsObject(), revision);
        await storageGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (previous is not null && grants.TryGetValue(previous.Grant, out old)) old.State = "suspended";
            var snapshot = await library.ReadSourceScriptDataAsync(id, LJ.I(state!["revision"]), token).ConfigureAwait(false);
            candidate.Sites = JsonNode.Parse(snapshot.Sites.GetRawText())!.AsObject();
            candidate.DataHash = snapshot.Sha256;
            grants.Add(candidate.Id, candidate);
        }
        catch { if (old is not null) old.State = "active"; throw; }
        finally { storageGate.Release(); }
        string? transfer = null;
        bool loadAttempted = false, committed = false;
        byte[] bytes = Encoding.UTF8.GetBytes(LJ.S(selected["current"]!["source"]));
        try
        {
            string hash = LJ.Hash(bytes);
            var stage = await ScriptAsync("stage.begin", new { basename = "source.js", length = bytes.Length, sha256 = hash }, token).ConfigureAwait(false);
            transfer = LJ.Text(stage["token"]); int chunkBytes = (int)LJ.I(stage["chunkBytes"], 1, 65536);
            for (int offset = 0; offset < bytes.Length;)
            {
                int count = Math.Min(chunkBytes, bytes.Length - offset);
                var result = await ScriptAsync("stage.chunk", new { token = transfer, offset,
                    data = Convert.ToBase64String(bytes, offset, count) }, token).ConfigureAwait(false);
                LJ.Require(LJ.I(result["next"]) == offset + count, "INVALID_RESPONSE"); offset += count;
            }
            var current = selected["current"]!;
            loadAttempted = true;
            touched.Add(id);
            var reply = await ScriptAsync("load", new { token = transfer, installationId = id, revision,
                expectedRevision = previous?.Revision ?? 0, version = LJ.S(current["version"]),
                config = new { }, permissions = new { networkOrigins = current["permissions"]!["networkOrigins"],
                    storage = current["permissions"]!["storage"], localServices = false }, grant = candidate.Id }, token).ConfigureAwait(false);
            var instance = LJ.Object(reply["instance"]); var generation = LJ.I(instance["generation"], 1);
            LJ.Require(LJ.S(instance["installationId"]) == id && LJ.I(instance["revision"]) == revision &&
                LJ.S(instance["state"]) == "ready", "INVALID_RESPONSE");
            var capabilities = LJ.Object(instance["capabilities"]).DeepClone().AsObject();
            ValidateCapabilities(capabilities);
            await storageGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                LJ.Require(candidate.Generation is 0 || candidate.Generation == generation, "STALE_SESSION");
                candidate.Generation = generation;
            }
            finally { storageGate.Release(); }
            token.ThrowIfCancellationRequested();
            await PersistAsync(next, candidate, token).ConfigureAwait(false);
            // CAS admission is final. Do not reinterpret later cancellation as a
            // rollback, or restore an old script against committed new metadata.
            committed = true;
            live[id] = new(revision, generation, candidate.Id, capabilities);
            await storageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { if (old is not null) grants.Remove(old.Id); }
            finally { storageGate.Release(); }
        }
        catch
        {
            if (!committed)
            {
                bool oldStillLive = !loadAttempted;
                if (loadAttempted)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var status = await ScriptAsync("status", new { }, cleanup.Token).ConfigureAwait(false);
                        var actual = LJ.Array(status["instances"]).OfType<JsonObject>().SingleOrDefault(i => LJ.S(i["installationId"]) == id);
                        oldStillLive = previous is not null && actual is not null &&
                            LJ.I(actual["revision"]) == previous.Revision && LJ.I(actual["generation"]) == previous.Generation;
                        if (actual is not null && !oldStillLive)
                        {
                            await ScriptAsync("unload", new { installationId = id, revision = LJ.I(actual["revision"]),
                                generation = LJ.I(actual["generation"]) }, cleanup.Token).ConfigureAwait(false);
                            touched.Remove(id);
                        }
                        else if (actual is null) touched.Remove(id);
                    }
                    catch { blocked = true; oldStillLive = false; }
                }
                await storageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    grants.Remove(candidate.Id);
                    if (old is not null)
                    {
                        if (oldStillLive && !blocked) old.State = "active";
                        else { old.State = "revoked"; grants.Remove(old.Id); }
                    }
                }
                finally { storageGate.Release(); }
                if (!oldStillLive || blocked) live.Remove(id);
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (transfer is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ScriptAsync("stage.abort", new { token = transfer }, cleanup.Token).ConfigureAwait(false);
                }
                catch { /* A staged buffer expires; it grants no persistence or execution authority. */ }
            }
        }
    }
    private static void ValidateCapabilities(JsonObject capabilities)
    {
        LJ.Require(capabilities.Count is > 0 and <= 6, "SCRIPT_PROTOCOL_ERROR");
        foreach (var pair in capabilities)
        {
            LJ.Require(NativeMusicSong.Platforms.Append("local").Contains(pair.Key), "SCRIPT_PROTOCOL_ERROR");
            var cap = LJ.Object(pair.Value); var actions = LJ.Array(cap["actions"]);
            LJ.Require(actions.Count > 0 && actions.All(n => LJ.S(n) is "musicUrl" or "lyric" or "pic"), "SCRIPT_PROTOCOL_ERROR");
            var qualities = LJ.Array(cap["qualitys"]);
            LJ.Require(qualities.All(n => LJ.S(n) is "128k" or "192k" or "320k" or "flac" or "flac24bit" or "master"), "SCRIPT_PROTOCOL_ERROR");
        }
    }
    private async Task UnloadAsync(string id, CancellationToken token)
    {
        if (!live.TryGetValue(id, out var active)) return;
        await storageGate.WaitAsync(token).ConfigureAwait(false);
        try { if (grants.TryGetValue(active.Grant, out var grant)) grant.State = "revoked"; }
        finally { storageGate.Release(); }
        try
        {
            await ScriptAsync("unload", new { installationId = id, revision = active.Revision,
                generation = active.Generation }, token).ConfigureAwait(false);
            live.Remove(id);
            touched.Remove(id);
            await storageGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { grants.Remove(active.Grant); }
            finally { storageGate.Release(); }
        }
        catch { blocked = true; throw; }
    }
    private async Task RetireAllAsync(CancellationToken token)
    {
        await storageGate.WaitAsync(token).ConfigureAwait(false);
        try { foreach (var grant in grants.Values) grant.State = "revoked"; }
        finally { storageGate.Release(); }
        // A cancelled/unknown load may have published a session without giving
        // the caller its receipt. Retirement reconciles every touched ID.
        // No active/attempted script means that even shutdown is Node-free.
        if (touched.Count > 0 && runtime is MeowFilm.Core.Runtime.IRuntimeRecovery
            { State: MeowFilm.Core.Runtime.RuntimeConnectionState.Closed })
        {
            touched.Clear(); live.Clear();
            await storageGate.WaitAsync(token).ConfigureAwait(false);
            try { grants.Clear(); }
            finally { storageGate.Release(); }
        }
        else if (touched.Count > 0)
        {
            var status = await ScriptAsync("status", new { }, token).ConfigureAwait(false);
            foreach (var instance in LJ.Array(status["instances"]).OfType<JsonObject>())
            {
                string id = LJ.Text(instance["installationId"]);
                LJ.Require(touched.Contains(id), "SCRIPT_CLEANUP");
                await ScriptAsync("unload", new { installationId = id, revision = LJ.I(instance["revision"]),
                    generation = LJ.I(instance["generation"]) }, token).ConfigureAwait(false);
            }
            touched.Clear(); live.Clear();
            await storageGate.WaitAsync(token).ConfigureAwait(false);
            try { grants.Clear(); }
            finally { storageGate.Release(); }
        }
    }
    private async Task<object?> StorageAsync(string operation, JsonElement input, CancellationToken cancellationToken)
    {
        bool admitted = false, entered = false;
        try
        {
            var p = Parse(input);
            LJ.Fields(p, operation == "set" ? "grant installationId siteId generation key value" : "grant installationId siteId generation key",
                operation == "set" ? "grant installationId siteId generation key value" : "grant installationId siteId generation key");
            Admit(); admitted = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var token = linked.Token;
            await storageGate.WaitAsync(token).ConfigureAwait(false); entered = true;
            string grantId = LJ.Text(p["grant"], 80), id = LJ.Text(p["installationId"], 128),
                site = LJ.Text(p["siteId"]), key = LJ.Text(p["key"], 256);
            long generation = LJ.I(p["generation"], 1);
            LJ.Require(!blocked && grants.TryGetValue(grantId, out _), "STALE_SESSION");
            var grant = grants[grantId];
            LJ.Require(grant.Installation == id && (grant.State is "candidate" or "active") &&
                NativeMusicSong.Platforms.Append("local").Contains(site), "STALE_SESSION");
            LJ.Require(LJ.Array(grant.Permissions["storage"]).Any(n => LJ.S(n) == (operation == "get" ? "read" : "write")), "PERMISSION_DENIED");
            LJ.Require(grant.Generation is 0 || grant.Generation == generation, "STALE_SESSION");
            if (grant.Generation == 0) grant.Generation = generation;
            JsonElement value;
            if (grant.State == "candidate")
            {
                // The shared LX host already rejects startup storage without an
                // activated site scope. This overlay does not weaken that rule.
                var data = grant.Sites[site] as JsonObject ?? LJ.Obj(("schema", 1), ("values", new JsonArray()));
                var values = LJ.Array(data["values"]);
                if (operation == "get")
                    value = JsonSerializer.SerializeToElement(values.OfType<JsonObject>().FirstOrDefault(v => LJ.S(v["key"]) == key)?["value"], LJ.Json);
                else
                {
                    LJ.Require(operation == "delete" || JsonSerializer.SerializeToUtf8Bytes(p["value"], LJ.Json).Length <= 16384, "LIMIT");
                    var next = LJ.Arr(values.Where(v => LJ.S(v?["key"]) != key));
                    if (operation == "set") next.Add(LJ.Obj(("key", key), ("value", p["value"])));
                    var result = LJ.Obj(("schema", 1), ("values", next));
                    LJ.Require(next.Count <= 64 && LJ.Bytes(result).Length <= 65536, "LIMIT");
                    grant.Sites[site] = result; value = JsonSerializer.SerializeToElement<object?>(null);
                }
            }
            else
            {
                value = operation == "get"
                    ? await library.ReadScriptValueAsync(id, site, key, grant.Revision, token).ConfigureAwait(false)
                    : await library.CommitScriptValueAsync(id, site, key, grant.Revision,
                        operation == "set" ? JsonSerializer.SerializeToElement(p["value"], LJ.Json) : null,
                        operation == "delete", token).ConfigureAwait(false);
            }
            return new { ok = true, value };
        }
        catch (Exception error)
        {
            if (Code(error) is "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE")
            { blocked = true; CancelPlayback(); }
            return new { ok = false, code = Code(error) };
        }
        finally { if (entered) storageGate.Release(); if (admitted) Finish(); }
    }
    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (activityGate)
        {
            if (preparation is null)
            {
                preparing = true;
                foreach (var operation in onlineOperations.ToArray()) operation.Cancel();
                preparation = PrepareCoreAsync(drained.Task);
            }
            return preparation.WaitAsync(cancellationToken);
        }
    }
    private async Task PrepareCoreAsync(Task drain)
    {
        await Task.Yield();
        await drain.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RetireAllAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch { blocked = true; throw; }
        finally { gate.Release(); }
    }
    public async Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (activityGate) { LJ.Require(!closed, "CLOSED"); task = preparation; }
        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch when (!cancellationToken.IsCancellationRequested) { /* The next admitted command reconciles the current source state. */ }
        }
        lock (activityGate) { LJ.Require(!closed, "CLOSED"); preparation = null; preparing = false; }
    }
    public ValueTask DisposeAsync()
    {
        lock (activityGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        lock (activityGate)
        {
            closed = true; lifetime.Cancel();
            foreach (var operation in onlineOperations.ToArray()) operation.Cancel();
        }
        try { await PrepareShutdownAsync().ConfigureAwait(false); }
        finally { transfers.Dispose(); }
        // Store, runtime and library lifetimes are owned by the App.
    }
}
