// SPDX-License-Identifier: GPL-3.0-only
// Native implementation derived from the fixed iWebPlus b900c259 collection behavior.
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Capabilities.Sync;

internal sealed partial class MusicLibrarySyncEngine(MusicLibraryOwner owner, IMusicPlatformSessionSource sessions,
    MusicPlatformClient client, Action generationGuard, CancellationToken token)
{
    private MusicLibrarySyncSnapshot snapshot = null!;
    private JsonObject library = null!, state = null!;
    private readonly Dictionary<string, MusicSyncAdapter> adapters = [];
    private readonly Dictionary<string, MusicPlatformSession> leases = [];
    private readonly List<JsonObject> issues = [];
    private readonly HashSet<string> issueKeys = [];
    private readonly HashSet<string> reportedMappings = [];
    private static JsonObject O(params (string Key, object? Value)[] f) => LJ.Obj(f);
    private static string S(JsonNode? n) => LJ.S(n);
    private static string[] Strings(JsonNode? n) => LJ.Array(n).Select(n => LJ.S(n)).ToArray();
    private IEnumerable<JsonObject> Collections => LJ.Array(library["collections"]).OfType<JsonObject>();
    private static IEnumerable<JsonObject> Bindings(JsonObject c) => LJ.Array(c["bindings"]).OfType<JsonObject>();
    private static string BindingKey(JsonObject b) => LJ.Stringify(new JsonArray(S(b["platform"]), S(b["accountId"]), S(b["id"])));
    private static string BindingKey(string platform, string account, string id) => LJ.Stringify(new JsonArray(platform, account, id));
    private bool Enabled(string platform, string kind) => LJ.B(state["platforms"]![platform]![kind == "favorites" ? "favorites" : "playlists"]);
    private string[] Kinds(string platform) => new[] { "favorites", "playlist" }.Where(k => Enabled(platform, k)).ToArray();
    private void GuardLeases()
    {
        token.ThrowIfCancellationRequested(); generationGuard();
        foreach (var lease in leases.Values) LJ.Require(sessions.IsCurrent(lease), "STALE_SESSION");
    }
    private async Task Guard()
    {
        GuardLeases();
        await owner.CheckLibrarySyncAsync(snapshot, token).ConfigureAwait(false);
    }
    private async Task Save()
    {
        await Guard().ConfigureAwait(false);
        snapshot = await owner.CommitLibrarySyncAsync(snapshot, LJ.Element(library), LJ.Element(state), token).ConfigureAwait(false);
        library = JsonNode.Parse(snapshot.Library.GetRawText())!.AsObject(); state = JsonNode.Parse(snapshot.State.GetRawText())!.AsObject();
        // Callers never retain JsonNode references across a checkpoint; reacquire by stable ID.
    }
    private async Task AdoptMembers(IEnumerable<SyncMember> members)
    {
        var songs = members.Select(member => (member.Song, member.HasMetadata)).ToArray();
        if (songs.Length == 0) return;
        await Guard().ConfigureAwait(false);
        snapshot = await owner.AdoptLibrarySyncSongsAsync(snapshot, songs, token).ConfigureAwait(false);
        GuardLeases();
        // Preserve unsaved membership/journal edits. Songs remain in the repository,
        // not in checkpoint snapshots.
    }
    private JsonObject Collection(string id) => Collections.Single(c => S(c["id"]) == id);
    private JsonObject Binding(string cid, string key) => Bindings(Collection(cid)).Single(b => BindingKey(b) == key);
    private void Status(string phase, string code = "") => state["status"] = O(("phase", phase), ("code", code),
        ("at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("issues", LJ.Arr(issues)));
    private void Issue(string platform, string cid, string code)
    {
        if (issueKeys.Add(platform + "\n" + cid + "\n" + code))
            issues.Add(O(("platform", platform), ("collectionId", cid), ("code", code)));
    }
    internal static string Error(Exception error) => error switch
    { MusicLibraryException e => e.Code, MusicPlatformException e => e.Code, MusicSyncWriteException e => e.Code, OperationCanceledException => "CANCELLED", _ => "SYNC_FAILED" };
    private static bool Fatal(Exception e) => Error(e) is "CANCELLED" or "STALE_SESSION" or "CLOSED" or "SYNC_SUPERSEDED" or
        "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "AUTH_REQUIRED" or "FORBIDDEN" or "CONFIRMATION_REQUIRED";

    internal async Task<JsonElement> RunAsync(string? networkPlatform = null)
    {
        snapshot = await owner.ReadLibrarySyncAsync(token).ConfigureAwait(false);
        library = JsonNode.Parse(snapshot.Library.GetRawText())!.AsObject(); state = JsonNode.Parse(snapshot.State.GetRawText())!.AsObject();
        try
        {
            if (networkPlatform is not null)
                LJ.Require(state["confirmation"] is null, "CONFIRMATION_REQUIRED");
            foreach (string platform in MusicLibraryOwner.SyncPlatforms.Where(p => networkPlatform is null
                ? Kinds(p).Length > 0 : p == networkPlatform && Enabled(p, "playlist")))
            {
                generationGuard(); var lease = await sessions.AcquireAsync(platform, token).ConfigureAwait(false);
                LJ.Require(lease is not null && sessions.IsCurrent(lease) && lease.Platform == platform, "AUTH_REQUIRED");
                LJ.Require(S(state["platforms"]![platform]!["accountId"]) == lease.UserId, "ACCOUNT_REPLACED");
                string previous = S(library["accounts"]![platform]); LJ.Require(previous.Length == 0 || previous == lease.UserId, "ACCOUNT_REBIND_REQUIRED");
                leases[platform] = lease;
                adapters[platform] = new(lease, client, Guard, GuardLeases, token);
            }
            if (adapters.Count == 0) return networkPlatform is null ? View(snapshot) :
                LJ.Element(O(("ok", true), ("state", "local-only")));
            await Guard();
            if (state["pending"] is JsonObject pending)
            {
                LJ.Require(adapters.ContainsKey(S(pending["platform"])), "PENDING_TARGET_DISABLED");
                LJ.Require(networkPlatform is null || S(pending["operation"]) is "subscribe" or "unsubscribe", "PENDING_WRITE");
                await RecoverPending();
            }
            var pulls = new Dictionary<string, SyncDirectory>();
            // Explicit deterministic platform order; the round owns cancellation and complete snapshots.
            foreach (var (platform, adapter) in adapters)
            {
                var pull = networkPlatform is null ? await adapter.ReadAsync(Kinds(platform)) :
                    await ReadSubscriptions(adapter);
                if (networkPlatform is null) Validate(pull, adapter);
                pulls[platform] = pull;
            }
            await Guard();
            if (networkPlatform is null)
            {
                await ProtectPull(pulls);
                await AdoptMembers(pulls.Values.SelectMany(pull => pull.Collections).SelectMany(remote => remote.Members));
                Merge(pulls);
                await ResolveMappings();
            }
            MergeSubscriptions(pulls); Status("pushing"); await Save();
            // Stable IDs survive checkpoints; never reuse detached JsonObject references after CAS.
            foreach (string cid in networkPlatform is null ? Collections.Select(c => S(c["id"])).ToArray() : [])
                foreach (var (platform, adapter) in adapters)
                {
                    if (!Enabled(platform, S(Collection(cid)["kind"]))) continue;
                    try { await Push(cid, adapter, pulls[platform]); }
                    catch (Exception e) when (!Fatal(e))
                    {
                        Issue(platform, cid, Error(e));
                        if (state["pending"] is not null || state["confirmation"] is not null) throw;
                    }
                }
            foreach (var (platform, adapter) in adapters)
                if (Enabled(platform, "playlist")) await PushSubscriptions(adapter);
            if (networkPlatform is null)
            {
                Acknowledge();
                state["approved"] = null; state["confirmation"] = null;
            }
            Status(issues.Count == 0 ? "synced" : "partial"); await Save(); return View(snapshot);
        }
        catch (Exception e)
        {
            string code = Error(e);
            string phase = code is "SYNC_SUPERSEDED" or "STALE_SESSION" or "CANCELLED" ? "superseded" : "blocked";
            if (code != "CONFIRMATION_REQUIRED")
                try { Status(phase, code); await Save(); }
                catch { /* A stale plan/unknown CAS must not overwrite the current owner's status. */ }
            return LJ.Element(O(("ok", false), ("code", code), ("status", O(("phase", phase), ("code", code))), ("confirmation", state["confirmation"])));
        }
    }
    internal static JsonElement View(MusicLibrarySyncSnapshot snapshot)
    {
        var s = JsonNode.Parse(snapshot.State.GetRawText())!.AsObject();
        return LJ.Element(O(("ok", true), ("revision", s["revision"]), ("platforms", s["platforms"]), ("status", s["status"]),
            ("confirmation", s["confirmation"]), ("pending", s["pending"] is JsonObject p ? O(("platform", p["platform"]), ("collectionId", p["collectionId"]), ("operation", p["operation"])) : null)));
    }
    internal static bool CanRunScheduled(MusicLibrarySyncSnapshot snapshot)
    {
        var state = JsonNode.Parse(snapshot.State.GetRawText())!.AsObject();
        if (state["pending"] is not null || state["confirmation"] is not null) return false;
        string phase = S(state["status"]?["phase"]);
        if (phase is not ("idle" or "synced")) return false;
        return MusicLibraryOwner.SyncPlatforms.Any(platform =>
            LJ.B(state["platforms"]![platform]!["favorites"]) ||
            LJ.B(state["platforms"]![platform]!["playlists"]));
    }
    private void Validate(SyncDirectory directory, MusicSyncAdapter adapter)
    {
        LJ.Require(directory.AccountId == adapter.AccountId, "IDENTITY_MISMATCH");
        var ids = new HashSet<string>();
        foreach (var r in directory.Collections)
        {
            LJ.Require(ids.Add(r.Id) && Kinds(adapter.Platform).Contains(r.Kind) &&
                r.Writable && r.Owner == adapter.AccountId, "INCOMPLETE_PAGE");
            LJ.Require(r.Keys.Distinct().Count() == r.Keys.Length, "INVALID_RESPONSE");
        }
        LJ.Require(!Kinds(adapter.Platform).Contains("favorites") || directory.Collections.Count(r => r.Kind == "favorites") == 1, "FAVORITES_UNCONFIRMED");
    }
    private async Task Confirm(string code, int count, JsonNode evidence)
    {
        string digest = LJ.Digest(O(("code", code), ("evidence", evidence), ("platforms", state["platforms"]), ("local", LJ.Arr(Collections.Select(c => O(("id", c["id"]), ("revision", c["revision"]), ("order", c["order"]), ("deleted", c["deleted"])))))));
        if (S(state["approved"]?["token"]) == digest) return;
        state["approved"] = null; state["confirmation"] = O(("token", digest), ("code", code), ("count", count));
        Status("blocked", "CONFIRMATION_REQUIRED"); await Save(); throw new MusicLibraryException("CONFIRMATION_REQUIRED");
    }
    private async Task ProtectPull(Dictionary<string, SyncDirectory> pulls)
    {
        var changes = new JsonArray(); int count = 0; bool empty = false;
        foreach (var (platform, pull) in pulls)
        {
            var known = Collections.Where(c => !LJ.B(c["deleted"])).SelectMany(c => Bindings(c).Where(b => S(b["platform"]) == platform && S(b["accountId"]) == pull.AccountId && Enabled(platform, S(c["kind"]))) ).ToArray();
            var missing = known.Where(b => !pull.Collections.Any(r => r.Id == S(b["id"]))).ToArray();
            if (missing.Length > 0 && missing.Length == known.Length || missing.Length >= 20)
            { empty |= missing.Length == known.Length; count += missing.Length; changes.Add(O(("platform", platform), ("missing", missing.Select(b => S(b["id"])).Order().ToArray()))); }
            foreach (var b in known)
            {
                var remote = pull.Collections.FirstOrDefault(r => r.Id == S(b["id"])); if (remote is null) continue;
                var baseline = Strings(b["baseline"]); int removed = baseline.Except(remote.Keys).Count();
                if (baseline.Length > 0 && remote.Members.Count == 0 || removed >= 20)
                { empty |= remote.Members.Count == 0; count += removed; changes.Add(O(("platform", platform), ("id", remote.Id), ("keys", remote.Keys), ("signature", remote.Signature))); }
            }
        }
        if (changes.Count > 0) await Confirm(empty ? "EMPTY_REMOTE" : "BULK_REMOTE_DELETE", count, changes);
    }
    private static JsonObject NewBinding(MusicSyncAdapter adapter, SyncRemote r) => O(("platform", adapter.Platform), ("accountId", adapter.AccountId),
        ("id", r.Id), ("writable", r.Writable), ("baseline", new JsonArray()), ("map", new JsonObject()), ("name", r.Name));
    private JsonObject NewCollection(string name)
    {
        long next = LJ.I(library["nextId"], 1); library["nextId"] = checked(next + 1);
        var c = O(("id", "playlist:" + next), ("name", name), ("kind", "playlist"), ("order", new JsonArray()), ("revision", 0), ("manualOrder", false),
            ("bindings", new JsonArray()), ("intents", new JsonObject())); LJ.Array(library["collections"]).Add(c); return c;
    }
    private string MergeMember(SyncMember member)
    {
        // Pull and readback register every real member source before mapping it.
        // File occurrence keys cannot substitute for this source ownership.
        return owner.FindLibrarySyncSource(NativeMusicSong.SourceKey(member.Song));
    }
    private void Merge(Dictionary<string, SyncDirectory> pulls)
    {
        var firstBindings = new HashSet<string>();
        foreach (var (platform, pull) in pulls)
        {
            library["accounts"]![platform] = pull.AccountId;
            foreach (var c in Collections)
                if (S(c["kind"]) == "playlist" && Enabled(platform, "playlist") && Bindings(c).Any(b => S(b["platform"]) == platform && S(b["accountId"]) == pull.AccountId && !pull.Collections.Any(r => r.Id == S(b["id"]))))
                { if (!LJ.B(c["deleted"])) c["revision"] = LJ.I(c["revision"]) + 1; c["deleted"] = true; }
            foreach (var r in pull.Collections)
            {
                string key = BindingKey(platform, pull.AccountId, r.Id);
                var c = Collections.FirstOrDefault(c => Bindings(c).Any(b => BindingKey(b) == key));
                if (c is null)
                {
                    c = r.Kind == "favorites" ? Collection("favorites") : Collections.FirstOrDefault(c => !LJ.B(c["deleted"]) && S(c["kind"]) == "playlist" &&
                        NativeMusicSong.NormalText(S(c["name"])) == NativeMusicSong.NormalText(r.Name) && !Bindings(c).Any(b => S(b["platform"]) == platform));
                    c ??= NewCollection(r.Name); LJ.Array(c["bindings"]).Add(NewBinding(adapters[platform], r));
                    firstBindings.Add(key);
                }
            }
        }
        foreach (var c in Collections)
        {
            var before = Strings(c["order"]); var sequences = new List<string[]>(); var removed = new HashSet<string>(); var added = new HashSet<string>();
            var firstImported = new HashSet<string>();
            var intents = LJ.Object(c["intents"]);
            foreach (var b in Bindings(c))
            {
                string platform = S(b["platform"]); if (!pulls.TryGetValue(platform, out var pull) || !Enabled(platform, S(c["kind"]))) continue;
                var r = pull.Collections.FirstOrDefault(r => r.Id == S(b["id"])); if (r is null) continue;
                var map = LJ.Object(b["map"]);
                var oldKeys = Strings(b["baseline"]); bool known = map.Count > 0; string key = BindingKey(b);
                bool Observable(string id) => intents[id] is not JsonObject intent || !LJ.B(intent["pending"]) || S(intent["origin"]) != "local" ||
                    (intent["acknowledged"] as JsonArray ?? []).Any(k => S(k) == key);
                var sequence = new List<string>(); var sequenceSeen = new HashSet<string>(StringComparer.Ordinal);
                var fresh = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in r.Members)
                {
                    string id = MergeMember(member); fresh[member.Key] = id;
                    if (sequenceSeen.Add(id)) sequence.Add(id);
                }
                var remoteKeys = new HashSet<string>(r.Keys, StringComparer.Ordinal);
                var freshIds = new HashSet<string>(fresh.Values, StringComparer.Ordinal);
                var oldKeySet = new HashSet<string>(oldKeys, StringComparer.Ordinal);
                foreach (string native in oldKeys)
                { string id = S(map[native]); if (id.Length > 0 && !remoteKeys.Contains(native) && !freshIds.Contains(id) && Observable(id)) removed.Add(id); }
                foreach (var (native, id) in fresh)
                { if (!oldKeySet.Contains(native) && known && Observable(id)) added.Add(id); map[native] = id; }
                if (firstBindings.Contains(key)) firstImported.UnionWith(fresh.Values);
                b["name"] = r.Name; b["writable"] = r.Writable; b["baseline"] = LJ.Arr(r.Keys); sequences.Add(sequence.ToArray());
            }
            foreach (string id in removed.Union(added)) intents[id] = O(("present", !removed.Contains(id)), ("pending", true), ("origin", "remote"));
            // Only a newly bound collection can bring back an older cancellation.
            // Apply after every existing binding's delta so traversal order cannot change the union.
            foreach (string id in firstImported)
                if (intents[id] is JsonObject intent && !LJ.B(intent["present"]))
                    intents[id] = O(("present", true), ("pending", true), ("origin", "remote"));
            var order = before;
            if (!LJ.B(c["manualOrder"]) && sequences.Count > 0) order = MergeOrder(sequences.Aggregate(Array.Empty<string>(), MergeOrder), order);
            else foreach (var sequence in sequences) order = MergeOrder(order, sequence);
            order = order.Where(id => intents[id] is not JsonObject intent || LJ.B(intent["present"])).Concat(intents.Where(p => LJ.B(p.Value?["present"])).Select(p => p.Key)).Distinct().ToArray();
            if (!before.SequenceEqual(order)) c["revision"] = LJ.I(c["revision"]) + 1;
            c["order"] = LJ.Arr(order);
        }
    }
    internal static string[] MergeOrder(string[] current, string[] incoming)
    {
        var result = current.Distinct().ToList(); var seen = result.ToHashSet(); var input = incoming.Distinct().ToArray(); string? previous = null;
        for (int i = 0; i < input.Length;)
        {
            if (seen.Contains(input[i])) { previous = input[i++]; continue; }
            var run = new List<string>(); while (i < input.Length && !seen.Contains(input[i])) run.Add(input[i++]);
            int after = previous is null ? -1 : result.IndexOf(previous), before = i == input.Length ? -1 : result.IndexOf(input[i]);
            result.InsertRange(before > after ? before : after >= 0 ? after + 1 : result.Count, run); seen.UnionWith(run); previous = run[^1];
        }
        return result.ToArray();
    }
    private async Task ResolveMappings()
    {
        var seen = new HashSet<string>();
        foreach (var (platform, adapter) in adapters)
        {
            foreach (var c in Collections.Where(c => !LJ.B(c["deleted"])))
            {
                if (!Enabled(platform, S(c["kind"]))) continue;
                foreach (string id in Strings(c["order"]))
                {
                    var song = owner.ReadLibrarySyncSong(id);
                    string key = platform + ":" + id;
                    if (Sources(song).Any(s => adapter.NativeId(s) is not null) || !seen.Add(key)) continue;
                    try
                    {
                        var match = await adapter.ResolveAsync(song);
                        if (match is null)
                        {
                            reportedMappings.Add(key);
                            Issue(platform, S(c["id"]), "UNMAPPED_SONG");
                            continue;
                        }
                        LJ.Require(new NativeMusicSong.Recordings(song, token).CanMerge(new(match, token), token), "RECORDING_CONFLICT");
                        match["songId"] = id;
                        await Guard().ConfigureAwait(false);
                        snapshot = await owner.AdoptLibrarySyncSongsAsync(snapshot, [(match, true)], token).ConfigureAwait(false);
                        GuardLeases();
                    }
                    catch (Exception e) when (!Fatal(e))
                    {
                        reportedMappings.Add(key);
                        Issue(platform, S(c["id"]), Error(e));
                    }
                }
            }
        }
    }
    private static IEnumerable<JsonObject> Sources(JsonObject song) => LJ.Array(song["sources"]).Select(source => LJ.Object(source));
    private void Acknowledge()
    {
        foreach (var c in Collections)
        {
            var targets = Bindings(c).Where(b => Enabled(S(b["platform"]), S(c["kind"]))).ToArray();
            var bindingPresent = targets.Select(b => Strings(b["baseline"]).Select(k => S(b["map"]![k]))
                .Where(songId => songId.Length > 0).ToHashSet(StringComparer.Ordinal)).ToArray();
            foreach (var (id, raw) in LJ.Object(c["intents"]))
            {
                var intent = LJ.Object(raw); var ack = Strings(intent["acknowledged"] ?? new JsonArray()).ToHashSet();
                bool want = LJ.B(intent["present"]);
                int confirmedCount = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    if (bindingPresent[i].Contains(id) == want) { ack.Add(BindingKey(targets[i])); confirmedCount++; }
                }
                intent["acknowledged"] = LJ.Arr(ack.Order());
                // Missing target mappings cannot falsely clear pending.
                int required = adapters.Keys.Count(p => Enabled(p, S(c["kind"])));
                if (targets.Length > 0 && targets.Select(b => S(b["platform"])).Distinct().Count() == required && confirmedCount == targets.Length) intent["pending"] = false;
            }
        }
    }
}
