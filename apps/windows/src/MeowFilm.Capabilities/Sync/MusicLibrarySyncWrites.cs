// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
namespace MeowFilm.Capabilities.Sync;

internal sealed partial class MusicLibrarySyncEngine
{
    private async Task Push(string cid, MusicSyncAdapter adapter, SyncDirectory initial)
    {
        var c = Collection(cid); string platform = adapter.Platform;
        var bindings = Bindings(c).Where(b => S(b["platform"]) == platform && S(b["accountId"]) == adapter.AccountId).ToArray();
        SyncRemote? created = null;
        if (bindings.Length == 0 && !LJ.B(c["deleted"]))
        {
            if (SubscribedOnly(c)) return;
            LJ.Require(S(c["kind"]) != "favorites", "FAVORITES_UNCONFIRMED");
            var fresh = await adapter.ReadAsync(Kinds(platform)); Validate(fresh, adapter);
            adapter.CheckWriteCapability("create");
            await Journal(cid, adapter, null, "create", [], [], fresh.Collections.Select(r => r.Id).ToArray());
            try { await adapter.CreateAsync(S(Collection(cid)["name"])); }
            catch (Exception e) when (!Fatal(e)) { /* Outcome is established only by a fresh full read, never by HTTP success. */ }
            created = await RecoverPending();
            bindings = Bindings(Collection(cid)).Where(b => S(b["platform"]) == platform && S(b["accountId"]) == adapter.AccountId).ToArray();
        }
        foreach (string key in bindings.Select(BindingKey).ToArray())
        {
            c = Collection(cid); var b = Binding(cid, key);
            if (!LJ.B(b["writable"])) continue;
            var current = created is not null && created.Id == S(b["id"])
                ? created
                : initial.Collections.FirstOrDefault(remote => remote.Id == S(b["id"]));
            if (ReferenceEquals(current, created)) created = null;
            if (current is null) { if (!LJ.B(c["deleted"])) Issue(platform, cid, "REMOTE_MISSING"); continue; }
            LJ.Require(current.Writable && current.Owner == adapter.AccountId, "READ_ONLY");
            // Never overwrite changes observed since the input merge.
            LJ.Require(Strings(b["baseline"]).SequenceEqual(current.Keys), "REMOTE_CHANGED");
            if (LJ.B(c["deleted"]))
            {
                LJ.Require(current.Kind != "favorites", "FAVORITES_PROTECTED");
                await Confirm("DELETE_REMOTE_PLAYLIST", current.Members.Count, O(("platform", platform), ("cid", cid), ("signature", current.Signature)));
                await Perform(cid, adapter, current, "delete", [], []); continue;
            }
            var map = LJ.Object(b["map"]); var intent = LJ.Object(c["intents"]); var desired = new List<string>(); var songs = new Dictionary<string, JsonObject>();
            foreach (string id in Strings(c["order"]))
            {
                var existing = current.Members.Where(m => S(map[m.Key]) == id).Select(m => m.Id).ToArray();
                if (existing.Length > 0) { desired.AddRange(existing); continue; }
                var song = LJ.Object(library["songs"]![id]);
                var source = Sources(song).FirstOrDefault(s => adapter.NativeId(s) is not null);
                if (source is null)
                {
                    string mapping = platform + ":" + id;
                    if (!reportedMappings.Contains(mapping))
                        Issue(platform, cid, deferredMappings.Contains(mapping) ? "SYNC_DEFERRED" : "UNMAPPED_SONG");
                    continue;
                }
                string native = adapter.NativeId(source)!; desired.Add(native); songs[native] = source;
            }
            // Unresolved editions/occurrences are never a deletion instruction.
            var desiredSet = desired.ToHashSet();
            foreach (var member in current.Members)
                if (!desiredSet.Contains(member.Id) && (S(map[member.Key]).Length == 0 || intent[S(map[member.Key])] is not JsonObject i || LJ.B(i["present"]))) desired.Add(member.Id);
            var additions = desired.Except(current.Ids).ToArray(); var deletions = current.Ids.Except(desired).ToArray();
            if (deletions.Length >= 20 || current.Members.Count > 0 && desired.Count == 0)
                await Confirm("BULK_LOCAL_DELETE", deletions.Length, O(("platform", platform), ("cid", cid), ("signature", current.Signature), ("desired", desired)));
            foreach (var operation in new[] { "remove", "add" })
            {
                string[] values = operation == "remove" ? deletions : additions;
                foreach (var chunk in values.Chunk(platform == "wy" && current.Kind == "favorites" ? 1 : 100))
                {
                    string[] next = operation == "remove" ? current.Ids.Where(id => !chunk.Contains(id)).ToArray() : current.Ids.Concat(chunk).ToArray();
                    current = (await Perform(cid, adapter, current, operation, chunk, next, songs))!;
                }
            }
            LJ.Require(current.Ids.Order().SequenceEqual(desired.Order()), "MEMBERS_UNCONFIRMED");
            b = Binding(cid, key); var newMap = LJ.Object(b["map"]);
            // Do not regroup A,B,A unless the user actually changed its visible logical order.
            string[] logicalActual = current.Members.Select(m => S(newMap[m.Key])).Distinct().ToArray();
            string[] logicalDesired = Strings(Collection(cid)["order"]).Where(id => logicalActual.Contains(id)).ToArray();
            if (!logicalActual.SequenceEqual(logicalDesired) && !current.Ids.SequenceEqual(desired))
                _ = await Perform(cid, adapter, current, "order", [], desired.ToArray());
        }
    }
    private async Task Journal(string cid, MusicSyncAdapter adapter, SyncRemote? remote, string operation, string[] ids, string[] desired, string[]? directory = null)
    {
        LJ.Require(state["pending"] is null, "PENDING_WRITE");
        adapter.CheckWriteCapability(operation);
        state["pending"] = O(("platform", adapter.Platform), ("accountId", adapter.AccountId), ("collectionId", cid), ("remoteId", remote?.Id ?? ""),
            ("operation", operation), ("before", remote?.Ids ?? []), ("desired", desired), ("ids", ids), ("name", S(Collection(cid)["name"])), ("directory", directory ?? []));
        Status("pushing"); await Save();
    }
    private async Task<SyncRemote?> Perform(string cid, MusicSyncAdapter adapter, SyncRemote remote, string operation,
        string[] ids, string[] desired, IReadOnlyDictionary<string, JsonObject>? songs = null)
    {
        adapter.CheckWriteCapability(operation); await Guard();
        var fresh = await adapter.ReadCollectionAsync(remote.Id);
        LJ.Require(fresh is not null && fresh.Signature == remote.Signature && fresh.Writable && fresh.Owner == adapter.AccountId, "REMOTE_CHANGED");
        await Journal(cid, adapter, remote, operation, ids, desired);
        try
        {
            await Guard();
            if (operation == "delete") await adapter.DeleteAsync(fresh);
            else if (operation == "order") await adapter.OrderAsync(fresh, desired);
            else await adapter.ChangeAsync(fresh, operation == "add", ids, songs ?? new Dictionary<string, JsonObject>());
        }
        catch (Exception e) when (!Fatal(e)) { /* Persistent write fence remains until a complete readback confirms the actual result. */ }
        return await RecoverPending();
    }
    private async Task<SyncRemote?> RecoverPending()
    {
        var w = LJ.Object(state["pending"]); string platform = S(w["platform"]), cid = S(w["collectionId"]), operation = S(w["operation"]);
        LJ.Require(adapters.TryGetValue(platform, out var adapter) && adapter.AccountId == S(w["accountId"]), "PENDING_ACCOUNT_CHANGED");
        await Guard();
        if (operation == "create")
        {
            var data = await adapter.ReadAsync(Kinds(platform)); Validate(data, adapter);
            var candidates = data.Collections.Where(r => r.Writable && r.Kind == "playlist" && r.Owner == adapter.AccountId &&
                r.Name == S(w["name"]) && !Strings(w["directory"]).Contains(r.Id)).ToArray();
            LJ.Require(candidates.Length == 1, "CREATE_UNCONFIRMED");
            var c = Collection(cid); var r = candidates.Single(); string key = BindingKey(platform, adapter.AccountId, r.Id);
            LJ.Require(!Collections.Any(other => S(other["id"]) != cid && Bindings(other).Any(b => BindingKey(b) == key)), "IDENTITY_MISMATCH");
            if (!Bindings(c).Any(b => BindingKey(b) == key)) LJ.Array(c["bindings"]).Add(NewBinding(adapter, r));
            UpdateBaseline(cid, key, r);
            state["pending"] = null; await Save(); return r;
        }
        var actual = await adapter.ReadCollectionAsync(S(w["remoteId"])); string bindingKey = BindingKey(platform, adapter.AccountId, S(w["remoteId"]));
        if (operation == "delete")
        {
            LJ.Require(actual is null, "DELETE_UNCONFIRMED"); Binding(cid, bindingKey)["baseline"] = new JsonArray();
            state["pending"] = null; await Save(); return null;
        }
        LJ.Require(actual is not null && actual.Writable && actual.Owner == adapter.AccountId, "WRITE_UNCONFIRMED");
        string[] before = Strings(w["before"]), desired = Strings(w["desired"]), ids = Strings(w["ids"]);
        // A partial requested delta may be checkpointed; unrelated additions/removals never advance the input baseline.
        bool unexpected = operation == "order" ? !actual.Ids.Order().SequenceEqual(before.Order()) :
            before.Except(actual.Ids).Any(id => operation != "remove" || !ids.Contains(id)) || actual.Ids.Except(before).Any(id => operation != "add" || !ids.Contains(id));
        LJ.Require(!unexpected, "REMOTE_CHANGED_DURING_WRITE");
        UpdateBaseline(cid, bindingKey, actual);
        var c2 = Collection(cid); var b2 = Binding(cid, bindingKey); var present = actual.Keys.Select(k => S(b2["map"]![k])).ToHashSet();
        foreach (var (id, value) in LJ.Object(c2["intents"]))
        {
            var intent = LJ.Object(value);
            if (present.Contains(id) == LJ.B(intent["present"]))
                intent["acknowledged"] = LJ.Arr(Strings(intent["acknowledged"] ?? new JsonArray()).Append(bindingKey).Distinct());
        }
        bool complete = operation == "order" ? actual.Ids.SequenceEqual(desired) : actual.Ids.Order().SequenceEqual(desired.Order());
        if (complete) state["pending"] = null;
        await Save(); LJ.Require(complete, "WRITE_UNCONFIRMED"); return actual;
    }
    private void UpdateBaseline(string cid, string key, SyncRemote remote)
    {
        var b = Binding(cid, key); var map = LJ.Object(b["map"]);
        foreach (var m in remote.Members) map[m.Key] = MergeMember(m);
        b["baseline"] = LJ.Arr(remote.Keys); b["writable"] = remote.Writable; b["name"] = remote.Name;
    }
}
