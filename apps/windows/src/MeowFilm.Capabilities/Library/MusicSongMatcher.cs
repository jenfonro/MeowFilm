using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Accepted recordings and bounded search evidence, not media URLs or account authority.</summary>
internal sealed class MusicSongMatcher
{
    internal const long Freshness = 7L * 86400000, NotFoundRetry = 86400000;
    private const int MemoryBytes = 4 * 1024 * 1024;
    private readonly MusicLibraryOwner library;
    private readonly MusicSongMatchStore store;
    private readonly Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> search;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim storage = new(1, 1);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private bool loaded;
    private long revision, memoryBytes;

    private sealed class Entry(string key, JsonObject song)
    {
        internal readonly string Key = key;
        internal JsonObject Song = song;
        internal readonly JsonObject Platforms = new();
        internal readonly HashSet<string> Excluded = new(StringComparer.Ordinal);
        internal long Used;
        internal int Bytes;
        internal readonly string Identity = NativeMusicSong.Fingerprint(song);
        internal JsonObject Snapshot() => LJ.Obj(("key", Key), ("song", Song), ("platforms", Platforms),
            ("excluded", LJ.Arr(Excluded.Order(StringComparer.Ordinal))));
    }
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal required string Platform;
        internal Task<MatchResult> Work = null!;
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (Exception error) when (error is ObjectDisposedException or AggregateException) { }
        }
    }
    internal sealed record MatchResult(JsonObject Song, string? Error = null);

    internal MusicSongMatcher(MusicLibraryOwner library, MusicSongMatchStore store,
        Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> search, TimeProvider? time = null)
    {
        this.library = library; this.store = store; this.search = search;
        clock = time ?? TimeProvider.System;
    }

    private static string Key(JsonObject song) => NativeMusicSong.Hash(LJ.Stringify(new JsonArray(
        NativeMusicSong.SourceKey(song), NativeMusicSong.Descriptor(song),
        NativeMusicSong.IsOnline(song) ? "" : LJ.S(song["songId"]))));
    internal static string Query(JsonObject song)
    {
        string query = (LJ.S(song["title"]) + " " + LJ.S(song["artist"])).Trim();
        return query.Length > 256 ? query[..256] : query;
    }
    private Entry? Related(JsonObject song)
    {
        string key = Key(song);
        var incoming = new NativeMusicSong.Recordings(song);
        if (entries.TryGetValue(key, out var exact) && new NativeMusicSong.Recordings(exact.Song).CanMerge(incoming))
            return exact;
        var sources = incoming.Sources.Select(row => LJ.S(row["sourceKey"])).ToHashSet(StringComparer.Ordinal);
        string identity = NativeMusicSong.Fingerprint(song);
        IEnumerable<Entry> candidates = identities.TryGetValue(identity, out var keys) ? keys.Select(id => entries[id]) :
            identity.Length == 0 ? entries.Values : [];
        return candidates.FirstOrDefault(entry =>
            (LJ.S(entry.Song["songId"]) == LJ.S(song["songId"]) ||
                LJ.Array(entry.Song["sources"]).OfType<JsonObject>().Any(row => sources.Contains(LJ.S(row["sourceKey"]))) ||
                NativeMusicSong.Fingerprint(song).Length > 0 &&
                NativeMusicSong.Fingerprint(entry.Song) == NativeMusicSong.Fingerprint(song)) &&
            new NativeMusicSong.Recordings(entry.Song).CanMerge(incoming));
    }
    private Entry Accept(JsonObject song)
    {
        string key = Key(song);
        if (!entries.TryGetValue(key, out var entry))
        {
            var related = Related(song);
            entry = new(key, song.DeepClone().AsObject());
            if (related is not null)
            {
                var merged = new NativeMusicSong.Recordings(entry.Song);
                if (merged.TryMerge(new(related.Song))) entry.Song = merged.ToSong();
                foreach (var state in related.Platforms) entry.Platforms[state.Key] = state.Value?.DeepClone();
                // Wrong-recording exclusions belong to this original recording, not every
                // compatible song which happened to share search results.
            }
            Add(entry);
        }
        var recordings = new NativeMusicSong.Recordings(entry.Song);
        if (recordings.TryMerge(new(song))) entry.Song = recordings.ToSong();
        Touch(entry);
        return entry;
    }
    private void Add(Entry entry)
    {
        entries.Add(entry.Key, entry);
        if (!identities.TryGetValue(entry.Identity, out var keys)) identities.Add(entry.Identity, keys = []);
        keys.Add(entry.Key);
    }
    private void Touch(Entry entry)
    {
        int bytes = LJ.Bytes(entry.Snapshot()).Length;
        memoryBytes += bytes - entry.Bytes;
        entry.Bytes = bytes;
        entry.Used = ++revision;
    }
    private static JsonObject Project(JsonObject song, Entry entry)
    {
        // Preserve the caller's logical id, display representative and primary recording.
        var output = song.DeepClone().AsObject();
        output["sources"] = LJ.Arr(LJ.Array(output["sources"]).OfType<JsonObject>()
            .Where(row => !entry.Excluded.Contains(LJ.S(row["sourceKey"]))));
        var recordings = new NativeMusicSong.Recordings(output);
        foreach (var source in LJ.Array(entry.Song["sources"]).OfType<JsonObject>())
            if (!entry.Excluded.Contains(LJ.S(source["sourceKey"]))) recordings.TryAddSource(source);
        return recordings.ToSong();
    }
    private void Trim()
    {
        while (memoryBytes > MemoryBytes && entries.Count > 0)
        {
            var oldest = entries.Values.MinBy(entry => entry.Used)!;
            memoryBytes -= oldest.Bytes;
            entries.Remove(oldest.Key);
            identities[oldest.Identity].Remove(oldest.Key);
            if (identities[oldest.Identity].Count == 0) identities.Remove(oldest.Identity);
        }
    }
    private static JsonObject Reuse(JsonObject original, JsonObject expanded, string platform)
    {
        var result = original.DeepClone().AsObject();
        result["sources"] = LJ.Arr(LJ.Array(result["sources"]).OfType<JsonObject>().Where(source =>
            LJ.S(source["source"]) != platform || LJ.Array(expanded["sources"]).OfType<JsonObject>().Any(row =>
                LJ.S(row["sourceKey"]) == LJ.S(source["sourceKey"]))));
        var recordings = new NativeMusicSong.Recordings(result);
        foreach (var source in LJ.Array(expanded["sources"]).OfType<JsonObject>()) recordings.TryAddSource(source);
        return recordings.ToSong();
    }
    private async Task LoadAsync(CancellationToken token)
    {
        await storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (loaded) return;
            var saved = await store.ReadAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                foreach (var row in saved)
                {
                    string key = LJ.S(row["key"]);
                    if (!entries.TryGetValue(key, out var entry))
                    {
                        entry = new(key, NativeMusicSong.Normalize(row["song"]));
                        Add(entry);
                    }
                    var recordings = new NativeMusicSong.Recordings(entry.Song);
                    if (recordings.TryMerge(new(NativeMusicSong.Normalize(row["song"])))) entry.Song = recordings.ToSong();
                    foreach (var state in row["platforms"]!.AsObject())
                    {
                        if (entry.Platforms[state.Key] is JsonObject current &&
                            LJ.I(current["checkedAt"]) >= LJ.I(state.Value?["checkedAt"])) continue;
                        entry.Platforms[state.Key] = state.Value?.DeepClone();
                    }
                    entry.Excluded.UnionWith(row["excluded"]!.AsArray().Select(value => LJ.S(value)));
                    Touch(entry);
                }
                loaded = true;
                Trim();
            }
        }
        finally { storage.Release(); }
    }

    internal async Task<JsonObject> KnownAsync(JsonObject song, CancellationToken token)
    {
        song = NativeMusicSong.Normalize(song);
        await LoadAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            var entry = Accept(song);
            var result = Project(song, entry);
            Trim();
            return result;
        }
    }

    internal async Task<MatchResult> MatchAsync(JsonObject song, string platform, string context,
        CancellationToken token)
    {
        LJ.Require(NativeMusicSong.Platforms.Contains(platform), "INVALID_REQUEST");
        LJ.Require(context.Length == 0 || context.Length == 16 &&
            context.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'), "INVALID_REQUEST");
        song = NativeMusicSong.Normalize(song);
        await LoadAsync(token).ConfigureAwait(false);
        Flight flight;
        string flightKey;
        lock (gate)
        {
            var entry = Accept(song);
            var known = Project(song, entry);
            bool hasSource = LJ.Array(known["sources"]).OfType<JsonObject>().Any(row =>
                LJ.S(row["source"]) == platform);
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var status = entry.Platforms[platform] as JsonObject;
            bool sameContext = status is not null && LJ.S(status["context"]) == context;
            bool sameQuery = status is not null && LJ.S(status["query"]) == Query(song);
            long age = status is null ? long.MaxValue : now - LJ.I(status["checkedAt"]);
            bool recent = age >= 0;
            if (hasSource ||
                sameContext && recent && (LJ.S(status!["state"]) == "notFound" && sameQuery && age < NotFoundRetry ||
                    LJ.S(status!["state"]) == "matched" && age < Freshness))
                return new(known);
            if (!NativeMusicSong.IsOnline(song) || NativeMusicSong.Fingerprint(song).Length == 0 ||
                LJ.N(song["duration"]) <= 0) return new(known);
            flightKey = LJ.Stringify(new JsonArray(entry.Key, platform, context,
                generations.GetValueOrDefault(platform)));
            if (!flights.TryGetValue(flightKey, out flight!))
            {
                flight = new() { Platform = platform };
                flights.Add(flightKey, flight);
                int generation = generations.GetValueOrDefault(platform);
                var current = flight;
                flight.Work = Task.Run(() => SearchAsync(song, platform, context,
                    generation, current.Cancellation.Token));
                _ = flight.Work.ContinueWith(done =>
                {
                    _ = done.Exception;
                    lock (gate) if (flights.GetValueOrDefault(flightKey) == current) flights.Remove(flightKey);
                    current.Cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        var result = await flight.Work.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await SaveAsync(token).ConfigureAwait(false);
        return result with { Song = Reuse(song, result.Song, platform) };
    }
    private async Task<MatchResult> SearchAsync(JsonObject song, string platform, string context,
        int generation, CancellationToken token)
    {
        MusicSearchPage? result = null;
        string? error = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12), clock);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var reply = await search(new(platform, Query(song), MusicSearchKind.Songs, 1, 30, false), request.Token)
                .ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            result = MusicSearchPage.Read(reply, platform, MusicSearchKind.Songs, 1);
        }
        catch (MusicPlatformException e) { error = e.Code; }
        catch (MusicLibraryException e) { error = e.Code; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { error = "TIMEOUT"; }
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            LJ.Require(generations.GetValueOrDefault(platform) == generation, "AUTH_CHANGED");
            var entry = Accept(song);
            var recordings = new NativeMusicSong.Recordings(entry.Song);
            if (result is not null)
                foreach (var candidate in result.Items)
                {
                    var incoming = new NativeMusicSong.Recordings(candidate.Item);
                    if (!recordings.CanMerge(incoming)) continue;
                    foreach (var source in incoming.Sources)
                        if (LJ.S(source["source"]) == platform && !entry.Excluded.Contains(LJ.S(source["sourceKey"])))
                            recordings.TryAddSource(source, replace: true);
                }
            entry.Song = recordings.ToSong();
            bool found = recordings.Sources.Any(source =>
                LJ.S(source["source"]) == platform && !entry.Excluded.Contains(LJ.S(source["sourceKey"])));
            entry.Platforms[platform] = Status(error is not null ? "failed" : found ? "matched" : "notFound",
                clock.GetUtcNow().ToUnixTimeMilliseconds(), context, Query(song), error ?? "");
            Touch(entry);
            var output = Project(song, entry);
            Trim();
            return new(output, error);
        }
    }
    private static JsonObject Status(string state, long at, string context, string query, string code) =>
        LJ.Obj(("state", state), ("checkedAt", at), ("context", context), ("query", query), ("code", code));

    // A synchronous observer performs no I/O and starts no detached work. Saved at the next
    // playback/library operation or the application's shutdown barrier.
    internal void ObserveSearch(JsonElement snapshot, Func<string, string> context)
    {
        if (MusicDto.Text(snapshot, "kind") != "songs" ||
            snapshot.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True) return;
        var songs = snapshot.GetProperty("items").EnumerateArray()
            .Select(item => NativeMusicSong.Normalize(JsonNode.Parse(item.GetRawText()))).ToArray();
        // Optional snapshot strings may be empty (a successful platform has no error code).
        static string Optional(JsonElement value, string key) =>
            value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
        string keyword = Optional(snapshot, "keyword").Trim();
        if (keyword.Length > 256) keyword = keyword[..256];
        lock (gate)
        {
            foreach (var song in songs)
            {
                var observed = LJ.Array(song["sources"]).OfType<JsonObject>()
                    .Select(row => LJ.S(row["source"])).ToHashSet(StringComparer.Ordinal);
                var entry = Accept(song);
                var retained = LJ.Array(entry.Song["sources"]).OfType<JsonObject>()
                    .Where(row => !entry.Excluded.Contains(LJ.S(row["sourceKey"])))
                    .Select(row => LJ.S(row["source"])).ToHashSet(StringComparer.Ordinal);
                foreach (var platform in snapshot.GetProperty("platforms").EnumerateArray())
                {
                    if (platform.GetProperty("loading").GetBoolean() ||
                        !platform.TryGetProperty("checkedAt", out var checkedAt) || !checkedAt.TryGetInt64(out long at) || at <= 0) continue;
                    string name = MusicDto.Text(platform, "platform"), error = Optional(platform, "error");
                    if (!NativeMusicSong.Platforms.Contains(name) || error.Length == 0 &&
                        platform.GetProperty("page").GetInt32() == 0) continue;
                    if (entry.Platforms[name] is JsonObject old && LJ.I(old["checkedAt"]) >= at) continue;
                    bool found = observed.Contains(name) && retained.Contains(name);
                    // A broad page that omits an accepted recording is no evidence against it.
                    if (!found && retained.Contains(name)) continue;
                    entry.Platforms[name] = Status(error.Length > 0 ? "failed" : found ? "matched" : "notFound",
                        at, context(name), keyword, error);
                }
                Touch(entry);
            }
            Trim();
        }
    }

    internal bool IsExcluded(JsonObject song, string sourceKey)
    { lock (gate) return entries.TryGetValue(Key(song), out var entry) && entry.Excluded.Contains(sourceKey); }

    internal async Task ExcludeAsync(JsonObject song, string sourceKey, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            var entry = Accept(NativeMusicSong.Normalize(song));
            entry.Excluded.Add(sourceKey);
            foreach (var source in LJ.Array(entry.Song["sources"]).OfType<JsonObject>())
                if (LJ.S(source["sourceKey"]) == sourceKey) entry.Platforms.Remove(LJ.S(source["source"]));
            Touch(entry);
        }
        await SaveAsync(token).ConfigureAwait(false);
    }
    internal void Invalidate(string platform)
    {
        Flight[] retired;
        lock (gate)
        {
            generations[platform] = generations.GetValueOrDefault(platform) + 1;
            foreach (var entry in entries.Values)
                if (entry.Platforms.Remove(platform)) Touch(entry);
            revision++;
            retired = flights.Values.Where(flight => flight.Platform == platform).ToArray();
        }
        foreach (var flight in retired) flight.Cancel();
    }
    internal async Task PersistAsync(CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        await storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var local = await library.ReadMatchingSongsAsync(token).ConfigureAwait(false);
            JsonObject[] snapshot;
            lock (gate)
            {
                // Membership is checked even with an unchanged cache revision (collection removal).
                // Membership never renews recency, so capacity still evicts the least recently used.
                var eligible = new HashSet<Entry>();
                // Local files never acquire matching state; skipping them also avoids unindexed scans.
                foreach (var song in local.Where(NativeMusicSong.IsOnline))
                {
                    if (Related(song) is not { } related) continue;
                    if (related.Key == Key(song)) { eligible.Add(related); continue; }
                    long used = related.Used;
                    var accepted = Accept(song);
                    accepted.Used = used;
                    eligible.Add(accepted);
                }
                snapshot = eligible.OrderBy(entry => entry.Used).Select(entry => entry.Snapshot())
                    .Where(MusicSongMatchStore.Accepts).ToArray();
                Trim();
            }
            await store.WriteAsync(snapshot, token).ConfigureAwait(false);
        }
        finally { storage.Release(); }
    }
    private async Task SaveAsync(CancellationToken token)
    {
        try { await PersistAsync(token).ConfigureAwait(false); }
        catch (MusicLibraryException) { /* Pending user state does not invalidate accepted match evidence. */ }
        catch (MusicPlaybackCacheCommitException) { }
        catch (DocumentStoreException error) when (MusicPlaybackCacheDataException.IsOptionalStoreFailure(error)) { }
    }
    internal async Task DrainAsync()
    {
        Flight[] pending;
        lock (gate)
        {
            pending = flights.Values.ToArray();
        }
        foreach (var flight in pending) flight.Cancel();
        foreach (var flight in pending)
            try { await flight.Work.ConfigureAwait(false); } catch { }
    }
}
