// SPDX-License-Identifier: GPL-3.0-only
// Native implementation derived from the fixed iWebPlus b900c259 collection behavior.
using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities.Sync;

internal sealed partial class MusicLibrarySyncEngine(MusicLibraryOwner owner, IMusicPlatformSessionSource sessions,
    MusicPlatformClient client, Action generationGuard, CancellationToken token)
{
    private MusicLibrarySyncSnapshot snapshot = null!;
    private MusicLibraryAreaRecord library = null!;
    private MusicLibrarySyncStateRecord state = null!;
    private readonly Dictionary<string, MusicSyncAdapter> adapters = [];
    private readonly Dictionary<string, MusicPlatformSession> leases = [];
    private readonly List<MusicSyncIssueRecord> issues = [];
    private readonly HashSet<string> issueKeys = [];
    private readonly HashSet<string> reportedMappings = [];
    private (HashSet<(string SongId, string Provider, string Platform)> ProviderPlatforms,
        HashSet<(string SongId, string SourceKey)> RecordingKeys) exclusionSnapshot = ([], []);

    private bool IsSourceExcluded(string songId, string platform, string? sourceKey) =>
        exclusionSnapshot.ProviderPlatforms.Contains((songId, MusicLibraryOwner.OfficialProvider, platform)) ||
        !string.IsNullOrEmpty(sourceKey) && exclusionSnapshot.RecordingKeys.Contains((songId, sourceKey));

    private static string BindingKey(MusicCollectionBindingRecord b) =>
        MusicLibraryOwner.BindingKey(b.Platform, b.AccountId, b.RemoteId);
    private static string BindingKey(string platform, string account, string id) =>
        MusicLibraryOwner.BindingKey(platform, account, id);

    private bool Enabled(string platform, string kind) =>
        state.Platforms.TryGetValue(platform, out var p) && (kind == "favorites" ? p.Favorites : p.Playlists);

    private string[] Kinds(string platform) =>
        new[] { "favorites", "playlist" }.Where(k => Enabled(platform, k)).ToArray();

    private void GuardLeases()
    {
        token.ThrowIfCancellationRequested(); generationGuard();
        foreach (var lease in leases.Values) MusicRules.Require(sessions.IsCurrent(lease), "STALE_SESSION");
    }

    private async Task Guard()
    {
        GuardLeases();
        await owner.CheckLibrarySyncAsync(snapshot, token).ConfigureAwait(false);
    }

    private async Task Save()
    {
        await Guard().ConfigureAwait(false);
        snapshot = await owner.CommitLibrarySyncAsync(snapshot, library, state, token).ConfigureAwait(false);
        library = snapshot.Library;
        state = snapshot.State;
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

    private MusicCollectionRecord Collection(string id) => library.Collections.Single(c => c.CollectionId == id);
    private MusicCollectionBindingRecord Binding(string cid, string key) => Collection(cid).Bindings.Single(b => BindingKey(b) == key);

    private void ReplaceCollection(MusicCollectionRecord updated)
    {
        var list = library.Collections.ToArray();
        int idx = Array.FindIndex(list, c => c.CollectionId == updated.CollectionId);
        list[idx] = updated;
        library = library with { Collections = list };
    }

    private void ReplaceBinding(string cid, string key, MusicCollectionBindingRecord updated)
    {
        var c = Collection(cid);
        var bindings = c.Bindings.ToArray();
        int idx = Array.FindIndex(bindings, b => BindingKey(b) == key);
        bindings[idx] = updated;
        ReplaceCollection(c with { Bindings = bindings });
    }

    private void SetAccount(string platform, string accountId)
    {
        var next = new Dictionary<string, string>(library.Accounts, StringComparer.Ordinal) { [platform] = accountId };
        library = library with { Accounts = next };
    }

    private void Status(string phase, string code = "") =>
        state = state with
        {
            StatusPhase = phase,
            StatusCode = code,
            StatusAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Issues = issues.ToArray()
        };

    private void Issue(string platform, string cid, string code)
    {
        if (issueKeys.Add(platform + "\n" + cid + "\n" + code))
            issues.Add(new MusicSyncIssueRecord(platform, cid, code));
    }

    internal static string Error(Exception error) => error switch
    {
        MusicLibraryException e => e.Code,
        MusicPlatformException e => e.Code,
        MusicSyncWriteException e => e.Code,
        OperationCanceledException => "CANCELLED",
        _ => "SYNC_FAILED"
    };

    private static bool Fatal(Exception e) => Error(e) is "CANCELLED" or "STALE_SESSION" or "CLOSED" or "SYNC_SUPERSEDED" or
        "STORAGE_CONFLICT" or "PENDING_WRITE" or "AUTH_REQUIRED" or "FORBIDDEN" or "CONFIRMATION_REQUIRED";

    internal async Task<MusicLibrarySyncResult> RunAsync(string? networkPlatform = null)
    {
        snapshot = await owner.ReadLibrarySyncAsync(token).ConfigureAwait(false);
        library = snapshot.Library;
        state = snapshot.State;
        try
        {
            if (networkPlatform is not null)
                MusicRules.Require(state.Confirmation is null, "CONFIRMATION_REQUIRED");
            foreach (string platform in MusicLibraryOwner.SyncPlatforms.Where(p => networkPlatform is null
                ? Kinds(p).Length > 0 : p == networkPlatform && Enabled(p, "playlist")))
            {
                generationGuard();
                var lease = await sessions.AcquireAsync(platform, token).ConfigureAwait(false);
                MusicRules.Require(lease is not null && sessions.IsCurrent(lease) && lease.Platform == platform, "AUTH_REQUIRED");
                string expectedAcc = state.Platforms.TryGetValue(platform, out var ps) ? ps.AccountId : "";
                MusicRules.Require(expectedAcc == lease.UserId, "ACCOUNT_REPLACED");
                string previous = library.Accounts.TryGetValue(platform, out var prev) ? prev : "";
                MusicRules.Require(previous.Length == 0 || previous == lease.UserId, "ACCOUNT_REBIND_REQUIRED");
                leases[platform] = lease;
                adapters[platform] = new(lease, client, Guard, GuardLeases, token);
            }
            if (adapters.Count == 0)
                return networkPlatform is null
                    ? MusicLibrarySyncResult.FromSnapshot(snapshot)
                    : MusicLibrarySyncResult.FromSnapshot(snapshot) with { LocalOnly = true };
            DebugLog.Info(DebugCategories.MusicLibrary, "sync", "sync_round_start",
                new { networkPlatform, platforms = adapters.Keys.ToArray() });
            await Guard();
            if (state.Pending is { } pen)
            {
                MusicRules.Require(adapters.ContainsKey(pen.Platform), "PENDING_TARGET_DISABLED");
                MusicRules.Require(networkPlatform is null || pen.Operation is "subscribe" or "unsubscribe", "PENDING_WRITE");
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
                exclusionSnapshot = await owner.ReadSyncExclusionSnapshotAsync(token).ConfigureAwait(false);
                await ProtectPull(pulls);
                await AdoptMembers(pulls.Values.SelectMany(pull => pull.Collections).SelectMany(remote => remote.Members));
                Merge(pulls);
                await ResolveMappings();
                DebugLog.Info(DebugCategories.MusicLibrary, "sync", "sync_merge_done",
                    new { collections = library.Collections.Length, issues = issues.Count });
            }
            MergeSubscriptions(pulls); Status("pushing"); await Save();
            foreach (string cid in networkPlatform is null ? library.Collections.Select(c => c.CollectionId).ToArray() : [])
                foreach (var (platform, adapter) in adapters)
                {
                    if (!Enabled(platform, Collection(cid).Kind)) continue;
                    try { await Push(cid, adapter, pulls[platform]); }
                    catch (Exception e) when (!Fatal(e))
                    {
                        Issue(platform, cid, Error(e));
                        if (state.Pending is not null || state.Confirmation is not null) throw;
                    }
                }
            foreach (var (platform, adapter) in adapters)
                if (Enabled(platform, "playlist")) await PushSubscriptions(adapter);
            if (networkPlatform is null)
            {
                Acknowledge();
                state = state with { Approved = null, Confirmation = null };
            }
            string finalPhase = issues.Count == 0 ? "synced" : "partial";
            Status(finalPhase); await Save();
            DebugLog.Info(DebugCategories.MusicLibrary, "sync", "sync_round_done",
                new { phase = finalPhase, issues = issues.Count });
            return MusicLibrarySyncResult.FromSnapshot(snapshot);
        }
        catch (Exception e)
        {
            string code = Error(e);
            string phase = code is "SYNC_SUPERSEDED" or "STALE_SESSION" or "CANCELLED" ? "superseded" : "blocked";
            if (code != "CONFIRMATION_REQUIRED")
                try { Status(phase, code); await Save(); }
                catch (Exception saveError)
                {
                    // A stale plan/unknown CAS must not overwrite the current owner's status.
                    DebugLog.Warn(DebugCategories.MusicLibrary, "sync", "sync_status_save_skipped",
                        new { code, phase, saveError = Error(saveError) });
                }
            DebugLog.Warn(DebugCategories.MusicLibrary, "sync", "sync_round_failed",
                new { phase, code });
            return MusicLibrarySyncResult.Failed(code, phase, state.Confirmation);
        }
    }

    internal static bool CanRunScheduled(MusicLibrarySyncSnapshot snapshot)
    {
        var s = snapshot.State;
        if (s.Pending is not null || s.Confirmation is not null) return false;
        if (s.StatusPhase is not ("idle" or "synced" or "partial")) return false;
        return MusicLibraryOwner.SyncPlatforms.Any(platform =>
            s.Platforms.TryGetValue(platform, out var p) && (p.Favorites || p.Playlists));
    }

    private void Validate(SyncDirectory directory, MusicSyncAdapter adapter)
    {
        MusicRules.Require(directory.AccountId == adapter.AccountId, "IDENTITY_MISMATCH");
        var ids = new HashSet<string>();
        foreach (var r in directory.Collections)
        {
            MusicRules.Require(ids.Add(r.Id) && Kinds(adapter.Platform).Contains(r.Kind) &&
                r.Writable && r.Owner == adapter.AccountId, "INCOMPLETE_PAGE");
            MusicRules.Require(r.Keys.Distinct().Count() == r.Keys.Length, "INVALID_RESPONSE");
        }
        MusicRules.Require(!Kinds(adapter.Platform).Contains("favorites") ||
            directory.Collections.Count(r => r.Kind == "favorites") == 1, "FAVORITES_UNCONFIRMED");
    }

    private async Task Confirm(string code, int count, Action<Utf8JsonWriter> writeEvidence)
    {
        string digest = MusicNodeSongCodec.HashJsonObject(w =>
        {
            w.WriteString("code", code);
            w.WritePropertyName("evidence");
            writeEvidence(w);
            w.WritePropertyName("platforms");
            w.WriteStartObject();
            foreach (string plat in MusicLibraryOwner.SyncPlatforms)
            {
                var p = state.Platforms.TryGetValue(plat, out var found) ? found : new MusicSyncPlatformRecord(false, false, "");
                w.WritePropertyName(plat);
                w.WriteStartObject();
                w.WriteBoolean("favorites", p.Favorites);
                w.WriteBoolean("playlists", p.Playlists);
                w.WriteString("accountId", p.AccountId);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WritePropertyName("local");
            w.WriteStartArray();
            foreach (var c in library.Collections)
            {
                w.WriteStartObject();
                w.WriteString("id", c.CollectionId);
                w.WriteNumber("revision", c.Revision);
                MusicNodeSongCodec.WriteStringArray(w, "order", c.Order);
                if (c.Deleted) w.WriteBoolean("deleted", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        if (state.Approved?.Token == digest) return;
        DebugLog.Warn(DebugCategories.MusicLibrary, "sync", "sync_confirmation_required",
            new { code, count });
        state = state with { Approved = null, Confirmation = new MusicSyncConfirmationRecord(digest, code, count) };
        Status("blocked", "CONFIRMATION_REQUIRED");
        await Save();
        throw new MusicLibraryException("CONFIRMATION_REQUIRED");
    }

    private async Task ProtectPull(Dictionary<string, SyncDirectory> pulls)
    {
        var changeWriters = new List<Action<Utf8JsonWriter>>();
        int count = 0; bool empty = false;
        foreach (var (platform, pull) in pulls)
        {
            var known = library.Collections.Where(c => !c.Deleted)
                .SelectMany(c => c.Bindings.Where(b => b.Platform == platform && b.AccountId == pull.AccountId && Enabled(platform, c.Kind)))
                .ToArray();
            var missing = known.Where(b => !pull.Collections.Any(r => r.Id == b.RemoteId)).ToArray();
            if (missing.Length > 0 && missing.Length == known.Length || missing.Length >= 20)
            {
                empty |= missing.Length == known.Length;
                count += missing.Length;
                string plat = platform;
                string[] missingIds = missing.Select(b => b.RemoteId).Order(StringComparer.Ordinal).ToArray();
                changeWriters.Add(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("platform", plat);
                    MusicNodeSongCodec.WriteStringArray(w, "missing", missingIds);
                    w.WriteEndObject();
                });
            }
            foreach (var b in known)
            {
                var remote = pull.Collections.FirstOrDefault(r => r.Id == b.RemoteId); if (remote is null) continue;
                var actualSongs = new HashSet<string>(StringComparer.Ordinal);
                var actualOccurrences = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in remote.Members)
                {
                    actualOccurrences.Add(member.Key);
                    string sourceKey = NativeMusicSong.SourceKey(member.Song);
                    if (sourceKey.Length > 0 && owner.TryFindLibrarySyncSource(sourceKey) is { Length: > 0 } songId)
                        actualSongs.Add(songId);
                    else if (b.Map.TryGetValue(member.Key, out var mapped) && mapped.Length > 0)
                        actualSongs.Add(mapped);
                }
                var removedSongs = new HashSet<string>(StringComparer.Ordinal);
                int unmappedRemoved = 0;
                foreach (string occurrence in b.Baseline)
                {
                    if (b.Map.TryGetValue(occurrence, out var songId) && songId.Length > 0)
                    {
                        if (!actualSongs.Contains(songId))
                            removedSongs.Add(songId);
                    }
                    else if (!actualOccurrences.Contains(occurrence))
                    {
                        unmappedRemoved++;
                    }
                }
                int removed = removedSongs.Count + unmappedRemoved;
                if (b.Baseline.Length > 0 && remote.Members.Count == 0 || removed >= 20)
                {
                    empty |= remote.Members.Count == 0;
                    count += removed;
                    string plat = platform;
                    string rid = remote.Id;
                    string[] keys = remote.Keys;
                    string sig = remote.Signature;
                    changeWriters.Add(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("platform", plat);
                        w.WriteString("id", rid);
                        MusicNodeSongCodec.WriteStringArray(w, "keys", keys);
                        w.WriteString("signature", sig);
                        w.WriteEndObject();
                    });
                }
            }
        }
        if (changeWriters.Count > 0)
        {
            await Confirm(empty ? "EMPTY_REMOTE" : "BULK_REMOTE_DELETE", count, w =>
            {
                w.WriteStartArray();
                foreach (var write in changeWriters) write(w);
                w.WriteEndArray();
            });
        }
    }

    private static MusicCollectionBindingRecord NewBinding(MusicSyncAdapter adapter, SyncRemote r) =>
        new(adapter.Platform, adapter.AccountId, r.Id, r.Writable, r.Name, [],
            new Dictionary<string, string>(StringComparer.Ordinal));

    private MusicCollectionRecord NewCollection(string name)
    {
        long next = library.NextId;
        var created = new MusicCollectionRecord(
            "playlist:" + next, name, "playlist", 0, false, false, false, [], [],
            new Dictionary<string, MusicCollectionIntentRecord>(StringComparer.Ordinal));
        library = library with
        {
            NextId = checked(next + 1),
            Collections = [.. library.Collections, created]
        };
        return created;
    }

    private string MergeMember(SyncMember member)
    {
        // Pull and readback register every real member source before mapping it.
        // File occurrence keys cannot substitute for this source ownership.
        return owner.FindLibrarySyncSource(NativeMusicSong.SourceKey(member.Song));
    }

    private void Merge(Dictionary<string, SyncDirectory> pulls)
    {
        var firstBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (platform, pull) in pulls)
        {
            SetAccount(platform, pull.AccountId);
            foreach (var c in library.Collections.ToArray())
            {
                if (c.Kind == "playlist" && Enabled(platform, "playlist") &&
                    c.Bindings.Any(b => b.Platform == platform && b.AccountId == pull.AccountId && !pull.Collections.Any(r => r.Id == b.RemoteId)))
                {
                    ReplaceCollection(c with { Deleted = true, Revision = c.Deleted ? c.Revision : checked(c.Revision + 1) });
                }
            }
            foreach (var r in pull.Collections)
            {
                string key = BindingKey(platform, pull.AccountId, r.Id);
                var c = library.Collections.FirstOrDefault(col => col.Bindings.Any(b => BindingKey(b) == key));
                if (c is null)
                {
                    c = r.Kind == "favorites"
                        ? Collection("favorites")
                        : library.Collections.FirstOrDefault(col => !col.Deleted && col.Kind == "playlist" &&
                            NativeMusicSong.NormalText(col.Name) == NativeMusicSong.NormalText(r.Name) &&
                            !col.Bindings.Any(b => b.Platform == platform));
                    c ??= NewCollection(r.Name);
                    ReplaceCollection(c with { Bindings = [.. c.Bindings, NewBinding(adapters[platform], r)] });
                    firstBindings.Add(key);
                }
            }
        }

        foreach (var c in library.Collections.ToArray())
        {
            var before = c.Order;
            string nextName = c.Name;
            var sequences = new List<string[]>();
            var removed = new HashSet<string>(StringComparer.Ordinal);
            var added = new HashSet<string>(StringComparer.Ordinal);
            var firstImported = new HashSet<string>(StringComparer.Ordinal);
            var intents = new Dictionary<string, MusicCollectionIntentRecord>(c.Intents, StringComparer.Ordinal);
            var updatedBindings = c.Bindings.ToArray();
            bool anyKnownLocalRename = false;
            var remoteRenames = new List<string>();

            for (int bi = 0; bi < updatedBindings.Length; bi++)
            {
                var b = updatedBindings[bi];
                string platform = b.Platform;
                string key = BindingKey(b);
                bool known = !firstBindings.Contains(key);
                if (known && c.Kind == "playlist" && b.Name != c.Name)
                    anyKnownLocalRename = true;
                if (!pulls.TryGetValue(platform, out var pull) || !Enabled(platform, c.Kind)) continue;
                var r = pull.Collections.FirstOrDefault(rem => rem.Id == b.RemoteId); if (r is null) continue;
                var map = new Dictionary<string, string>(b.Map, StringComparer.Ordinal);
                var oldKeys = b.Baseline;
                if (known && c.Kind == "playlist" && b.Name == c.Name && r.Name.Length > 0 && r.Name != b.Name)
                    remoteRenames.Add(r.Name);
                bool Observable(string id) => !intents.TryGetValue(id, out var intent) || !intent.Pending || intent.Origin != "local" ||
                    (intent.Acknowledged?.Contains(key, StringComparer.Ordinal) == true);
                var sequence = new List<string>();
                var sequenceSeen = new HashSet<string>(StringComparer.Ordinal);
                var fresh = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var member in r.Members)
                {
                    string id = MergeMember(member);
                    fresh[member.Key] = id;
                    if (sequenceSeen.Add(id)) sequence.Add(id);
                }
                var remoteKeys = new HashSet<string>(r.Keys, StringComparer.Ordinal);
                var freshIds = new HashSet<string>(fresh.Values, StringComparer.Ordinal);
                var oldKeySet = new HashSet<string>(oldKeys, StringComparer.Ordinal);
                foreach (string native in oldKeys)
                {
                    if (map.TryGetValue(native, out var id) && id.Length > 0 && !remoteKeys.Contains(native) &&
                        !freshIds.Contains(id) && Observable(id))
                        removed.Add(id);
                }
                foreach (var (native, id) in fresh)
                {
                    if (!oldKeySet.Contains(native) && known && Observable(id)) added.Add(id);
                    map[native] = id;
                }
                if (firstBindings.Contains(key)) firstImported.UnionWith(fresh.Values);
                updatedBindings[bi] = b with { Name = r.Name, Writable = r.Writable, Baseline = r.Keys, Map = map };
                sequences.Add(sequence.ToArray());
            }

            if (c.Kind == "playlist" && !anyKnownLocalRename && remoteRenames.Count > 0 &&
                remoteRenames.Distinct(StringComparer.Ordinal).Count() == 1)
                nextName = remoteRenames[0];

            foreach (string id in removed.Union(added))
                intents[id] = new MusicCollectionIntentRecord(!removed.Contains(id), true, "remote", null);
            // Only a newly bound collection can bring back an older cancellation.
            // Apply after every existing binding's delta so traversal order cannot change the union.
            foreach (string id in firstImported)
                if (intents.TryGetValue(id, out var intent) && !intent.Present)
                    intents[id] = new MusicCollectionIntentRecord(true, true, "remote", null);

            var order = before;
            if (!c.ManualOrder && sequences.Count > 0) order = MergeOrder(sequences.Aggregate(Array.Empty<string>(), MergeOrder), order);
            else foreach (var sequence in sequences) order = MergeOrder(order, sequence);
            order = order.Where(id => !intents.TryGetValue(id, out var intent) || intent.Present)
                .Concat(intents.Where(p => p.Value.Present).Select(p => p.Key))
                .Distinct(StringComparer.Ordinal).ToArray();

            long rev = before.SequenceEqual(order, StringComparer.Ordinal) && nextName == c.Name
                ? c.Revision : checked(c.Revision + 1);
            ReplaceCollection(c with { Name = nextName, Revision = rev, Order = order, Bindings = updatedBindings, Intents = intents });
        }
    }

    internal static string[] MergeOrder(string[] current, string[] incoming)
    {
        var result = current.Distinct(StringComparer.Ordinal).ToList();
        var seen = result.ToHashSet(StringComparer.Ordinal);
        var input = incoming.Distinct(StringComparer.Ordinal).ToArray();
        string? previous = null;
        for (int i = 0; i < input.Length;)
        {
            if (seen.Contains(input[i])) { previous = input[i++]; continue; }
            var run = new List<string>();
            while (i < input.Length && !seen.Contains(input[i])) run.Add(input[i++]);
            int after = previous is null ? -1 : result.IndexOf(previous);
            int before = i == input.Length ? -1 : result.IndexOf(input[i]);
            result.InsertRange(before > after ? before : after >= 0 ? after + 1 : result.Count, run);
            seen.UnionWith(run);
            previous = run[^1];
        }
        return result.ToArray();
    }

    private async Task ResolveMappings()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (platform, adapter) in adapters)
        {
            foreach (var c in library.Collections.Where(c => !c.Deleted))
            {
                if (!Enabled(platform, c.Kind)) continue;
                foreach (string id in c.Order)
                {
                    var song = owner.ReadLibrarySyncSong(id);
                    string key = platform + ":" + id;
                    bool hasAllowedSource = false;
                    foreach (var src in song.Sources)
                    {
                        if (adapter.NativeId(src) is null) continue;
                        if (!IsSourceExcluded(id, platform, src.SourceKey))
                        {
                            hasAllowedSource = true;
                            break;
                        }
                    }
                    if (hasAllowedSource || !seen.Add(key)) continue;
                    try
                    {
                        if (IsSourceExcluded(id, platform, null))
                        {
                            reportedMappings.Add(key);
                            Issue(platform, c.CollectionId, "UNMAPPED_SONG");
                            continue;
                        }
                        var match = await adapter.ResolveAsync(song,
                            sourceKey => IsSourceExcluded(id, platform, sourceKey)).ConfigureAwait(false);
                        if (match is null)
                        {
                            reportedMappings.Add(key);
                            Issue(platform, c.CollectionId, "UNMAPPED_SONG");
                            continue;
                        }
                        MusicRules.Require(new NativeMusicSong.Recordings(song, token).CanMerge(new(match, token), token), "RECORDING_CONFLICT");
                        match = match with { SongId = id };
                        await Guard().ConfigureAwait(false);
                        snapshot = await owner.AdoptLibrarySyncSongsAsync(snapshot, [(match, true)], token).ConfigureAwait(false);
                        GuardLeases();
                    }
                    catch (Exception e) when (!Fatal(e))
                    {
                        reportedMappings.Add(key);
                        Issue(platform, c.CollectionId, Error(e));
                    }
                }
            }
        }
    }

    private void Acknowledge()
    {
        foreach (var c in library.Collections.ToArray())
        {
            var targets = c.Bindings.Where(b => Enabled(b.Platform, c.Kind)).ToArray();
            var bindingPresent = targets.Select(b => b.Baseline
                .Select(k => b.Map.TryGetValue(k, out var mapped) ? mapped : "")
                .Where(songId => songId.Length > 0)
                .ToHashSet(StringComparer.Ordinal)).ToArray();
            var intents = new Dictionary<string, MusicCollectionIntentRecord>(c.Intents, StringComparer.Ordinal);
            foreach (var (id, intent) in c.Intents)
            {
                var ack = (intent.Acknowledged ?? []).ToHashSet(StringComparer.Ordinal);
                bool want = intent.Present;
                int confirmedCount = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    if (bindingPresent[i].Contains(id) == want)
                    {
                        ack.Add(BindingKey(targets[i]));
                        confirmedCount++;
                    }
                }
                // Missing target mappings cannot falsely clear pending.
                int required = adapters.Keys.Count(p => Enabled(p, c.Kind));
                bool stillPending = !(targets.Length > 0 && targets.Select(b => b.Platform).Distinct(StringComparer.Ordinal).Count() == required &&
                    confirmedCount == targets.Length) && intent.Pending;
                intents[id] = intent with
                {
                    Pending = stillPending,
                    Acknowledged = ack.Order(StringComparer.Ordinal).ToArray()
                };
            }
            ReplaceCollection(c with { Intents = intents });
        }
    }
}
