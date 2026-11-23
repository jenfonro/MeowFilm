// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities.Sync;

internal sealed partial class MusicLibrarySyncEngine
{
    private async Task Push(string cid, MusicSyncAdapter adapter, SyncDirectory initial)
    {
        var c = Collection(cid); string platform = adapter.Platform;
        var bindings = c.Bindings.Where(b => b.Platform == platform && b.AccountId == adapter.AccountId).ToArray();
        SyncRemote? created = null;
        if (bindings.Length == 0 && !c.Deleted)
        {
            MusicRules.Require(c.Kind != "favorites", "FAVORITES_UNCONFIRMED");
            var fresh = await adapter.ReadAsync(Kinds(platform)); Validate(fresh, adapter);
            adapter.CheckWriteCapability("create");
            await Journal(cid, adapter, null, "create", [], [], fresh.Collections.Select(r => r.Id).ToArray());
            try { await adapter.CreateAsync(Collection(cid).Name); }
            catch (MusicSyncWriteException e) when (e.Status is "notSent" or "rejected")
            { await ClearUnappliedWrite(); throw; }
            catch (Exception e) when (!Fatal(e)) { /* Outcome is established only by a fresh full read, never by HTTP success. */ }
            created = await RecoverPending();
            bindings = Collection(cid).Bindings.Where(b => b.Platform == platform && b.AccountId == adapter.AccountId).ToArray();
        }
        foreach (string key in bindings.Select(BindingKey).ToArray())
        {
            c = Collection(cid); var b = Binding(cid, key);
            var current = created is not null && created.Id == b.RemoteId
                ? created
                : initial.Collections.FirstOrDefault(remote => remote.Id == b.RemoteId);
            if (ReferenceEquals(current, created)) created = null;
            if (current is null) { if (!c.Deleted) Issue(platform, cid, "REMOTE_MISSING"); continue; }
            MusicRules.Require(current.Writable && current.Owner == adapter.AccountId, "READ_ONLY");
            // Never overwrite changes observed since the input merge.
            MusicRules.Require(b.Baseline.SequenceEqual(current.Keys, StringComparer.Ordinal), "REMOTE_CHANGED");
            if (c.Deleted)
            {
                MusicRules.Require(current.Kind != "favorites", "FAVORITES_PROTECTED");
                string sig = current.Signature;
                await Confirm("DELETE_REMOTE_PLAYLIST", current.Members.Count, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("platform", platform);
                    w.WriteString("cid", cid);
                    w.WriteString("signature", sig);
                    w.WriteEndObject();
                });
                await Perform(cid, adapter, current, "delete", [], []);
                continue;
            }
            var map = b.Map;
            var intent = c.Intents;
            var desired = new List<string>();
            var songs = new Dictionary<string, MusicSongSourceRecord>(StringComparer.Ordinal);
            var existingBySong = current.Members
                .GroupBy(m => map.TryGetValue(m.Key, out var mapped) ? mapped : "", StringComparer.Ordinal)
                .Where(g => g.Key.Length > 0)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToArray(), StringComparer.Ordinal);
            foreach (string id in c.Order)
            {
                if (existingBySong.TryGetValue(id, out var existing)) { desired.AddRange(existing); continue; }
                var song = owner.ReadLibrarySyncSong(id);
                var source = song.Sources.FirstOrDefault(s => adapter.NativeId(s) is not null);
                if (source is null)
                {
                    string mapping = platform + ":" + id;
                    if (!reportedMappings.Contains(mapping))
                        Issue(platform, cid, "UNMAPPED_SONG");
                    continue;
                }
                string native = adapter.NativeId(source)!;
                desired.Add(native);
                songs[native] = source;
            }
            // Unresolved editions/occurrences are never a deletion instruction.
            var desiredSet = desired.ToHashSet(StringComparer.Ordinal);
            foreach (var member in current.Members)
            {
                string mapped = map.TryGetValue(member.Key, out var m) ? m : "";
                if (!desiredSet.Contains(member.Id) && (mapped.Length == 0 || !intent.TryGetValue(mapped, out var i) || i.Present))
                    desired.Add(member.Id);
            }
            var additions = desired.Except(current.Ids, StringComparer.Ordinal).ToArray();
            var deletions = current.Ids.Except(desired, StringComparer.Ordinal).ToArray();
            if (deletions.Length >= 20 || current.Members.Count > 0 && desired.Count == 0)
            {
                string sig = current.Signature;
                string[] desiredArr = desired.ToArray();
                await Confirm("BULK_LOCAL_DELETE", deletions.Length, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("platform", platform);
                    w.WriteString("cid", cid);
                    w.WriteString("signature", sig);
                    MusicWireCodec.WriteStringArray(w, "desired", desiredArr);
                    w.WriteEndObject();
                });
            }
            foreach (var operation in new[] { "remove", "add" })
            {
                string[] values = operation == "remove" ? deletions : additions;
                foreach (var chunk in values.Chunk(platform == "wy" && current.Kind == "favorites" ? 1 : 100))
                {
                    string[] next = operation == "remove"
                        ? current.Ids.Where(id => !chunk.Contains(id, StringComparer.Ordinal)).ToArray()
                        : current.Ids.Concat(chunk).ToArray();
                    current = (await Perform(cid, adapter, current, operation, chunk, next, songs))!;
                }
            }
            MusicRules.Require(current.Ids.Order(StringComparer.Ordinal).SequenceEqual(desired.Order(StringComparer.Ordinal), StringComparer.Ordinal), "MEMBERS_UNCONFIRMED");
            b = Binding(cid, key);
            var newMap = b.Map;
            // Do not regroup A,B,A unless the user actually changed its visible logical order.
            string[] logicalActual = current.Members.Select(m => newMap.TryGetValue(m.Key, out var v) ? v : "").Distinct(StringComparer.Ordinal).ToArray();
            var logicalActualSet = new HashSet<string>(logicalActual, StringComparer.Ordinal);
            string[] logicalDesired = Collection(cid).Order.Where(logicalActualSet.Contains).ToArray();
            if (!logicalActual.SequenceEqual(logicalDesired, StringComparer.Ordinal) && !current.Ids.SequenceEqual(desired, StringComparer.Ordinal))
                _ = await Perform(cid, adapter, current, "order", [], desired.ToArray());
        }
    }

    private async Task Journal(string cid, MusicSyncAdapter adapter, SyncRemote? remote, string operation,
        string[] ids, string[] desired, string[]? directory = null)
    {
        MusicRules.Require(state.Pending is null, "PENDING_WRITE");
        adapter.CheckWriteCapability(operation);
        state = state with
        {
            Pending = new MusicSyncPendingRecord(
                adapter.Platform, adapter.AccountId, cid, remote?.Id ?? "", operation,
                null, null, Collection(cid).Name, null, null, null,
                remote?.Ids ?? [], desired, ids, directory ?? [])
        };
        Status("pushing"); await Save();
    }

    private async Task<SyncRemote?> Perform(string cid, MusicSyncAdapter adapter, SyncRemote remote, string operation,
        string[] ids, string[] desired, IReadOnlyDictionary<string, MusicSongSourceRecord>? songs = null)
    {
        adapter.CheckWriteCapability(operation); await Guard();
        var fresh = await adapter.ReadCollectionAsync(remote.Id);
        MusicRules.Require(fresh is not null && fresh.Signature == remote.Signature && fresh.Writable && fresh.Owner == adapter.AccountId, "REMOTE_CHANGED");
        await Journal(cid, adapter, remote, operation, ids, desired);
        try
        {
            if (operation == "delete") await adapter.DeleteAsync(fresh);
            else if (operation == "order") await adapter.OrderAsync(fresh, desired);
            else await adapter.ChangeAsync(fresh, operation == "add", ids, songs ?? new Dictionary<string, MusicSongSourceRecord>(StringComparer.Ordinal));
        }
        catch (MusicSyncWriteException e) when (e.Status is "notSent" or "rejected")
        { await ClearUnappliedWrite(); throw; }
        catch (Exception e) when (!Fatal(e)) { /* Persistent write fence remains until a complete readback confirms the actual result. */ }
        return await RecoverPending();
    }

    private async Task ClearUnappliedWrite()
    {
        state = state with { Pending = null };
        // A definite unsent/rejected receipt settles this exact saved journal even
        // after cancellation. The owner's snapshot CAS, not a stale account lease,
        // guards the durable update.
        snapshot = await owner.CommitLibrarySyncAsync(snapshot, library, state, CancellationToken.None).ConfigureAwait(false);
        library = snapshot.Library;
        state = snapshot.State;
    }

    private async Task<SyncRemote?> RecoverPending()
    {
        var w = state.Pending!;
        string platform = w.Platform, cid = w.CollectionId, operation = w.Operation;
        MusicRules.Require(adapters.TryGetValue(platform, out var adapter) && adapter.AccountId == w.AccountId, "PENDING_ACCOUNT_CHANGED");
        await Guard();
        if (operation is "subscribe" or "unsubscribe")
        {
            await RecoverSubscriptionPending(adapter);
            return null;
        }
        if (operation == "create")
        {
            var data = await adapter.ReadAsync(Kinds(platform)); Validate(data, adapter);
            var candidates = data.Collections.Where(r => r.Writable && r.Kind == "playlist" && r.Owner == adapter.AccountId &&
                r.Name == w.Name && !w.Directory.Contains(r.Id, StringComparer.Ordinal)).ToArray();
            MusicRules.Require(candidates.Length == 1, "CREATE_UNCONFIRMED");
            var c = Collection(cid); var r = candidates.Single(); string key = BindingKey(platform, adapter.AccountId, r.Id);
            MusicRules.Require(!library.Collections.Any(other => other.CollectionId != cid && other.Bindings.Any(b => BindingKey(b) == key)), "IDENTITY_MISMATCH");
            if (!c.Bindings.Any(b => BindingKey(b) == key))
                ReplaceCollection(c with { Bindings = [.. c.Bindings, NewBinding(adapter, r)] });
            await UpdateBaseline(cid, key, r);
            state = state with { Pending = null }; await Save(); return r;
        }
        var actual = await adapter.ReadCollectionAsync(w.RemoteId);
        string bindingKey = BindingKey(platform, adapter.AccountId, w.RemoteId);
        if (operation == "delete")
        {
            MusicRules.Require(actual is null, "DELETE_UNCONFIRMED");
            ReplaceBinding(cid, bindingKey, Binding(cid, bindingKey) with { Baseline = [] });
            state = state with { Pending = null }; await Save(); return null;
        }
        MusicRules.Require(actual is not null && actual.Writable && actual.Owner == adapter.AccountId, "WRITE_UNCONFIRMED");
        string[] before = w.Before, desired = w.Desired, ids = w.Ids;
        // A partial requested delta may be checkpointed; unrelated additions/removals never advance the input baseline.
        bool unexpected = operation == "order"
            ? !actual.Ids.Order(StringComparer.Ordinal).SequenceEqual(before.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            : before.Except(actual.Ids, StringComparer.Ordinal).Any(id => operation != "remove" || !ids.Contains(id, StringComparer.Ordinal)) ||
              actual.Ids.Except(before, StringComparer.Ordinal).Any(id => operation != "add" || !ids.Contains(id, StringComparer.Ordinal));
        MusicRules.Require(!unexpected, "REMOTE_CHANGED_DURING_WRITE");
        await UpdateBaseline(cid, bindingKey, actual);
        var c2 = Collection(cid); var b2 = Binding(cid, bindingKey);
        var present = actual.Keys.Select(k => b2.Map.TryGetValue(k, out var v) ? v : "").ToHashSet(StringComparer.Ordinal);
        var updatedIntents = new Dictionary<string, MusicCollectionIntentRecord>(c2.Intents, StringComparer.Ordinal);
        foreach (var (id, intent) in c2.Intents)
        {
            if (present.Contains(id) == intent.Present)
                updatedIntents[id] = intent with
                {
                    Acknowledged = (intent.Acknowledged ?? []).Append(bindingKey).Distinct(StringComparer.Ordinal).ToArray()
                };
        }
        ReplaceCollection(c2 with { Intents = updatedIntents });
        bool complete = operation == "order"
            ? actual.Ids.SequenceEqual(desired, StringComparer.Ordinal)
            : actual.Ids.Order(StringComparer.Ordinal).SequenceEqual(desired.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (complete) state = state with { Pending = null };
        await Save();
        MusicRules.Require(complete, "WRITE_UNCONFIRMED");
        return actual;
    }

    private async Task UpdateBaseline(string cid, string key, SyncRemote remote)
    {
        await AdoptMembers(remote.Members);
        var b = Binding(cid, key);
        var map = new Dictionary<string, string>(b.Map, StringComparer.Ordinal);
        foreach (var m in remote.Members) map[m.Key] = MergeMember(m);
        ReplaceBinding(cid, key, b with { Map = map, Baseline = remote.Keys, Writable = remote.Writable, Name = remote.Name });
    }
}
