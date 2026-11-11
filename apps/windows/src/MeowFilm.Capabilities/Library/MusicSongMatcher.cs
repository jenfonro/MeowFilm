using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>One mutable recording fact and its search evidence, without URLs or account authority.</summary>
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
    private readonly Dictionary<string, HashSet<Entry>> identities = new(StringComparer.Ordinal), sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private bool loaded;
    private long revision, memoryBytes, observationOrder;

    private sealed class Entry(string key, JsonObject song)
    {
        internal readonly string Key = key;
        internal JsonObject Song = song;
        internal readonly JsonObject Platforms = new();
        // Rejected target recordings are scoped to the original entry, not a global recording ban.
        internal readonly Dictionary<string, HashSet<string>> Excluded = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, long> Orders = new(StringComparer.Ordinal);
        internal HashSet<string> Identities = [], Sources = [];
        internal long Used;
        internal int Bytes;
        internal JsonObject Snapshot() => LJ.Obj(("key", Key), ("song", Song), ("platforms", Platforms),
            ("excluded", LJ.Arr(Excluded.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                LJ.Obj(("key", pair.Key), ("sources", LJ.Arr(pair.Value.Order(StringComparer.Ordinal))))))));
    }
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal required string Platform;
        internal required int Generation;
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
    internal long NextObservation() => Interlocked.Increment(ref observationOrder);
    internal (int Generation, long Order) BeginSearch(string platform)
    { lock (gate) return (generations.GetValueOrDefault(platform), NextObservation()); }
    internal Task InitializeAsync(CancellationToken token) => LoadAsync(token);

    private Entry? Related(JsonObject song)
    {
        var incoming = new NativeMusicSong.Recordings(song);
        bool Compatible(Entry entry) => NativeMusicSong.IsOnline(entry.Song) == !incoming.IsLocal &&
            new NativeMusicSong.Recordings(entry.Song).CanMerge(incoming);
        if (entries.TryGetValue(Key(song), out var exact) && Compatible(exact)) return exact;
        var visited = new HashSet<Entry>();
        foreach (var source in incoming.Sources)
            foreach (var entry in sources.GetValueOrDefault(LJ.S(source["sourceKey"])) ?? [])
                if (visited.Add(entry) && Compatible(entry)) return entry;
        if (incoming.IsLocal) return null;
        foreach (string fingerprint in incoming.Evidence.Select(row => row.Fingerprint).Where(value => value.Length > 0))
            foreach (var entry in identities.GetValueOrDefault(fingerprint) ?? [])
                if (visited.Add(entry) && Compatible(entry)) return entry;
        return null;
    }
    private Entry Accept(JsonObject song)
    {
        var entry = Related(song);
        if (entry is null)
        {
            string key = Key(song);
            // Two valid sets may share their primary descriptor while disagreeing
            // on another recording's duration. They must remain separate owners.
            if (entries.ContainsKey(key)) key = NativeMusicSong.Hash(new NativeMusicSong.Recordings(song).QualifiedId());
            entry = new(key, song.DeepClone().AsObject());
            entries.Add(entry.Key, entry);
        }
        else
        {
            var recordings = new NativeMusicSong.Recordings(entry.Song);
            var incoming = new NativeMusicSong.Recordings(song);
            if (recordings.TryMerge(incoming)) entry.Song = recordings.ToSong();
        }
        Touch(entry);
        return entry;
    }
    private static void Index(Dictionary<string, HashSet<Entry>> index, HashSet<string> before,
        HashSet<string> after, Entry entry)
    {
        foreach (string key in before.Except(after))
            if (index.TryGetValue(key, out var values))
            { values.Remove(entry); if (values.Count == 0) index.Remove(key); }
        foreach (string key in after.Except(before))
        {
            if (!index.TryGetValue(key, out var values)) index.Add(key, values = []);
            values.Add(entry);
        }
    }
    private void Touch(Entry entry)
    {
        var recordings = new NativeMusicSong.Recordings(entry.Song);
        var fingerprints = recordings.Evidence.Select(row => row.Fingerprint).Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var native = recordings.Sources.Select(source => LJ.S(source["sourceKey"])).ToHashSet(StringComparer.Ordinal);
        Index(identities, entry.Identities, fingerprints, entry); entry.Identities = fingerprints;
        Index(sources, entry.Sources, native, entry); entry.Sources = native;
        int bytes = LJ.Bytes(entry.Snapshot()).Length;
        memoryBytes += bytes - entry.Bytes;
        entry.Bytes = bytes;
        entry.Used = ++revision;
    }
    private static bool Excluded(Entry entry, JsonObject original, string sourceKey) =>
        entry.Excluded.TryGetValue(Key(original), out var excluded) && excluded.Contains(sourceKey);
    private static JsonObject Project(JsonObject song, Entry entry)
    {
        // Display metadata, logical song id and the primary recording remain the caller's snapshot.
        var output = song.DeepClone().AsObject();
        output["sources"] = LJ.Arr(LJ.Array(output["sources"]).OfType<JsonObject>()
            .Where(row => !Excluded(entry, song, LJ.S(row["sourceKey"]))));
        var recordings = new NativeMusicSong.Recordings(output);
        foreach (var source in LJ.Array(entry.Song["sources"]).OfType<JsonObject>())
            if (!Excluded(entry, song, LJ.S(source["sourceKey"]))) recordings.TryAddSource(source, replace: true);
        return recordings.ToSong();
    }
    private void Trim()
    {
        while (memoryBytes > MemoryBytes && entries.Count > 0)
        {
            var oldest = entries.Values.MinBy(entry => entry.Used)!;
            memoryBytes -= oldest.Bytes; entries.Remove(oldest.Key);
            Index(identities, oldest.Identities, [], oldest);
            Index(sources, oldest.Sources, [], oldest);
        }
    }
    private async Task LoadAsync(CancellationToken token)
    {
        lock (gate) if (loaded) return;
        await storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (loaded) return;
            var saved = await store.ReadAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                foreach (var row in saved)
                {
                    var song = NativeMusicSong.Normalize(row["song"]);
                    var entry = Accept(song);
                    foreach (var state in row["platforms"]!.AsObject())
                    {
                        if (entry.Platforms[state.Key] is JsonObject current &&
                            LJ.I(current["checkedAt"]) >= LJ.I(state.Value?["checkedAt"])) continue;
                        entry.Platforms[state.Key] = state.Value?.DeepClone();
                    }
                    foreach (var scope in row["excluded"]!.AsArray().OfType<JsonObject>())
                    {
                        string key = LJ.S(scope["key"]);
                        if (!entry.Excluded.TryGetValue(key, out var excluded)) entry.Excluded.Add(key, excluded = []);
                        excluded.UnionWith(scope["sources"]!.AsArray().Select(value => LJ.S(value)));
                    }
                    Touch(entry);
                }
                loaded = true; Trim();
            }
        }
        finally { storage.Release(); }
    }

    // Snapshots add missing sources only. Real search observations own replacement and query ordering.
    internal void ObserveSongs(IEnumerable<JsonObject> songs)
    {
        lock (gate)
        {
            foreach (var song in songs) Accept(NativeMusicSong.Normalize(song));
            Trim();
        }
    }
    internal JsonObject ProjectFacts(JsonObject value) => ProjectFacts(value, observe: true);
    internal JsonObject ProjectKnownFacts(JsonObject value) => ProjectFacts(value, observe: false);
    private JsonObject ProjectFacts(JsonObject value, bool observe)
    {
        lock (gate)
        {
            static bool IsSong(JsonObject song) => song.ContainsKey("songId") && song.ContainsKey("sources") &&
                song.ContainsKey("title") && song.ContainsKey("artist") && song.ContainsKey("duration") &&
                song.ContainsKey("album") && song.ContainsKey("cover_url");
            void Observe(JsonNode? node)
            {
                if (node is JsonObject song && IsSong(song)) { Accept(NativeMusicSong.Normalize(song)); return; }
                if (node is JsonObject obj) foreach (var pair in obj) Observe(pair.Value);
                if (node is JsonArray array) foreach (var item in array) Observe(item);
            }
            JsonNode? Visit(JsonNode? node)
            {
                if (node is JsonObject song && IsSong(song))
                {
                    var normalized = NativeMusicSong.Normalize(song);
                    return Related(normalized) is { } entry ? Project(normalized, entry) : normalized;
                }
                if (node is JsonObject obj) return new JsonObject(obj.Select(pair =>
                    KeyValuePair.Create(pair.Key, Visit(pair.Value))));
                if (node is JsonArray array) return new JsonArray(array.Select(Visit).ToArray());
                return node?.DeepClone();
            }
            if (observe) Observe(value);
            var output = Visit(value)!.AsObject();
            Trim(); return output;
        }
    }
    internal JsonElement ProjectFacts(JsonElement value) => LJ.Element(ProjectFacts(JsonNode.Parse(value.GetRawText())!.AsObject()));
    internal JsonElement ProjectKnownFacts(JsonElement value) => LJ.Element(ProjectKnownFacts(JsonNode.Parse(value.GetRawText())!.AsObject()));

    private JsonObject Known(JsonObject song) => Project(song, Accept(song));

    internal async Task<JsonObject> KnownAsync(JsonObject song, CancellationToken token)
    {
        song = NativeMusicSong.Normalize(song);
        await LoadAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            var result = Known(song);
            Trim(); return result;
        }
    }

    internal async Task<MatchResult> MatchAsync(JsonObject song, string platform, string context,
        CancellationToken token, bool contentFailed = false)
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
            bool hasSource = LJ.Array(known["sources"]).OfType<JsonObject>().Any(row => LJ.S(row["source"]) == platform);
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var status = entry.Platforms[platform] as JsonObject;
            bool sameContext = status is not null && LJ.S(status["context"]) == context;
            bool sameQuery = status is not null && LJ.S(status["query"]) == Query(song);
            bool hasMatchTime = sameContext && status!["matchedAt"] is not null;
            long matchAge = hasMatchTime ? now - LJ.I(status!["matchedAt"]) : 0;
            bool recheck = contentFailed && (!hasMatchTime || matchAge >= Freshness);
            long age = status is null ? 0 : now - LJ.I(status["checkedAt"]);
            if (hasSource && !recheck || sameContext && sameQuery && age >= 0 && age < NotFoundRetry &&
                (LJ.S(status!["state"]) == "notFound" || !hasSource && LJ.S(status!["state"]) == "matched"))
                return new(known);
            if (!NativeMusicSong.IsOnline(song) || NativeMusicSong.Fingerprint(song).Length == 0 ||
                LJ.N(song["duration"]) <= 0) return new(known);
            int generation = generations.GetValueOrDefault(platform);
            flightKey = LJ.Stringify(new JsonArray(entry.Key, platform, context, Query(song), generation));
            if (!flights.TryGetValue(flightKey, out flight!))
            {
                flight = new() { Platform = platform, Generation = generation };
                flights.Add(flightKey, flight);
                var current = flight;
                long order = NextObservation();
                flight.Work = Task.Run(() => SearchAsync(song, platform, context, generation, order, current.Cancellation.Token));
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
        lock (gate)
        {
            LJ.Require(generations.GetValueOrDefault(platform) == flight.Generation, "AUTH_CHANGED");
            var output = Project(song, Accept(song));
            Trim(); return result with { Song = output };
        }
    }
    private async Task<MatchResult> SearchAsync(JsonObject song, string platform, string context,
        int generation, long order, CancellationToken token)
    {
        MusicSearchPage? result = null;
        string? error = null;
        var query = new MusicSearchRequest(platform, Query(song), MusicSearchKind.Songs, 1, 30, false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12), clock);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var reply = await search(query, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            result = MusicSearchPage.Read(reply, platform, MusicSearchKind.Songs, 1);
        }
        catch (MusicPlatformException e) { error = e.Code; }
        catch (MusicLibraryException e) { error = e.Code; }
        catch (HttpRequestException) { error = "NETWORK_ERROR"; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { error = "TIMEOUT"; }
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            LJ.Require(generations.GetValueOrDefault(platform) == generation, "AUTH_CHANGED");
            var entry = Accept(song);
            ObservePage(result?.Items.Select(candidate => candidate.Item).ToArray() ?? [], [song], platform, context, query.Keyword,
                query.Page, query.Pages, query.Limit, clock.GetUtcNow().ToUnixTimeMilliseconds(), order, error ?? "");
            var output = Project(song, entry);
            Trim(); return new(output, error);
        }
    }
    private void Apply(Entry entry, IEnumerable<JsonObject> candidates, string platform, string context, string query,
        int page, int pages, int limit, long at, long order, string error, bool focused)
    {
        var old = entry.Platforms[platform] as JsonObject;
        if (entry.Orders.TryGetValue(platform, out long previous) && previous >= order ||
            old is not null && LJ.I(old["checkedAt"]) > at) return;
        var recordings = new NativeMusicSong.Recordings(entry.Song);
        bool found = false;
        if (error.Length == 0)
            foreach (var candidate in candidates)
            {
                var incoming = new NativeMusicSong.Recordings(candidate);
                if (!recordings.CanMerge(incoming)) continue;
                foreach (var source in incoming.Sources.Where(source => LJ.S(source["source"]) == platform))
                    if (recordings.TryAddSource(source, replace: true)) found = true;
            }
        // Absence only describes a focused first-page query for this recording.
        // A broad page (including a later page of the same keyword) cannot prove nonexistence.
        if (!found && error.Length == 0 && (page != 1 || !focused)) return;
        entry.Song = recordings.ToSong();
        JsonNode? matchedAt = found ? JsonValue.Create(at) : old is not null && LJ.S(old["context"]) == context
            ? old["matchedAt"]?.DeepClone() : null;
        entry.Platforms[platform] = LJ.Obj(("state", error.Length > 0 ? "failed" : found ? "matched" : "notFound"),
            ("checkedAt", at), ("context", context), ("query", query), ("code", error),
            ("page", page), ("pages", pages), ("limit", limit), ("matchedAt", matchedAt));
        entry.Orders[platform] = order;
        Touch(entry);
    }

    private void ObservePage(JsonObject[] candidates, JsonObject[] displayed, string platform, string context, string query,
        int page, int pages, int limit, long at, long order, string error)
    {
        var affected = new HashSet<Entry>();
        var focused = new HashSet<Entry>();
        foreach (var song in candidates)
            affected.Add(Related(song) ?? Accept(song));
        foreach (var song in displayed)
            if (Related(song) is { } entry)
            { affected.Add(entry); if (Query(song) == query) focused.Add(entry); }
        foreach (var entry in entries.Values)
            if (NativeMusicSong.IsOnline(entry.Song) &&
                NativeMusicSong.NormalText(Query(entry.Song)) == NativeMusicSong.NormalText(query))
            { affected.Add(entry); focused.Add(entry); }
        foreach (var entry in affected)
            Apply(entry, candidates, platform, context, query, page, pages, limit, at, order, error, focused.Contains(entry));
    }

    internal void ObservePage(MusicSearchRequest request, JsonElement? response, string error, string context,
        (int Generation, long Order) observation)
    {
        if (request.Kind != MusicSearchKind.Songs || error == "CANCELLED") return;
        var candidates = response is { } value
            ? MusicSearchPage.Read(value, request.Platform, request.Kind, request.Page).Items.Select(item => item.Item).ToArray() : [];
        lock (gate)
        {
            if (generations.GetValueOrDefault(request.Platform) != observation.Generation) return;
            ObservePage(candidates, [], request.Platform, context, request.Keyword, request.Page, request.Pages, request.Limit,
                clock.GetUtcNow().ToUnixTimeMilliseconds(), observation.Order, error);
            Trim();
        }
    }

    // The coordinator emits actual page evidence independently of its displayed/fused page cache.
    // Replaying a cached page keeps both the original check time and observation order.
    internal void ObserveSearch(JsonElement snapshot, Func<string, string> context)
    {
        if (MusicDto.Text(snapshot, "kind") != "songs" || snapshot.GetProperty("cancelled").GetBoolean()) return;
        var displayed = snapshot.GetProperty("items").EnumerateArray()
            .Select(item => NativeMusicSong.Normalize(JsonNode.Parse(item.GetRawText()))).ToArray();
        string query = snapshot.GetProperty("keyword").GetString()!;
        lock (gate)
        {
            var observations = snapshot.GetProperty("observations").EnumerateArray().Where(observation =>
                observation.GetProperty("checkedAt").GetInt64() > 0 && observation.GetProperty("order").GetInt64() > 0 &&
                observation.GetProperty("error").GetString() != "CANCELLED" &&
                observation.GetProperty("generation").GetInt64() ==
                    generations.GetValueOrDefault(observation.GetProperty("platform").GetString()!)).ToArray();
            // An empty platform can settle before the platform which discovers the song.
            // Seed only current candidates, then apply every check against that shared owner.
            foreach (var observation in observations)
                foreach (var item in observation.GetProperty("items").EnumerateArray())
                {
                    var song = NativeMusicSong.Normalize(JsonNode.Parse(item.GetRawText()));
                    if (Related(song) is null) Accept(song);
                }
            foreach (var observation in observations)
            {
                string platform = observation.GetProperty("platform").GetString()!, error = observation.GetProperty("error").GetString()!;
                long at = observation.GetProperty("checkedAt").GetInt64(), order = observation.GetProperty("order").GetInt64();
                int page = observation.GetProperty("page").GetInt32(), pages = observation.GetProperty("pages").GetInt32(),
                    limit = observation.GetProperty("limit").GetInt32();
                var candidates = observation.GetProperty("items").EnumerateArray()
                    .Select(item => NativeMusicSong.Normalize(JsonNode.Parse(item.GetRawText()))).ToArray();
                ObservePage(candidates, displayed, platform, context(platform), query, page, pages, limit, at, order, error);
            }
            Trim();
        }
    }

    internal bool IsExcluded(JsonObject song, string sourceKey)
    { lock (gate) return Related(song) is { } entry && Excluded(entry, song, sourceKey); }

    internal async Task ExcludeAsync(JsonObject song, string sourceKey, CancellationToken token)
    {
        await LoadAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            song = NativeMusicSong.Normalize(song);
            var entry = Accept(song);
            string key = Key(song);
            if (!entry.Excluded.TryGetValue(key, out var excluded)) entry.Excluded.Add(key, excluded = []);
            excluded.Add(sourceKey);
            foreach (var source in LJ.Array(entry.Song["sources"]).OfType<JsonObject>())
                if (LJ.S(source["sourceKey"]) == sourceKey)
                {
                    string platform = LJ.S(source["source"]);
                    entry.Platforms.Remove(platform);
                    entry.Orders[platform] = NextObservation();
                }
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
            { entry.Orders.Remove(platform); if (entry.Platforms.Remove(platform)) Touch(entry); }
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
                // Eligibility does not create per-library-key copies or renew a recording's check time.
                var eligible = local.Where(NativeMusicSong.IsOnline).Select(Related).OfType<Entry>().Distinct().ToArray();
                snapshot = eligible.OrderBy(entry => entry.Used).Select(entry => entry.Snapshot()).ToArray();
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
        catch (DocumentStoreException) { }
    }
    internal async Task DrainAsync()
    {
        Flight[] pending;
        lock (gate) pending = flights.Values.ToArray();
        foreach (var flight in pending) flight.Cancel();
        foreach (var flight in pending)
            try { await flight.Work.ConfigureAwait(false); } catch { }
    }
}
