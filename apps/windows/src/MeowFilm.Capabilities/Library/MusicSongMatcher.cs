using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Source discovery and its scoped retry state. It never owns a second song document.</summary>
internal sealed class MusicSongMatcher(MusicLibraryOwner library, MusicSongMatchStore store,
    Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> search, TimeProvider? time = null)
{
    internal const long Freshness = 7L * 86400000, NotFoundRetry = 86400000;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly SemaphoreSlim storage = new(1, 1);
    private readonly Dictionary<string, JsonObject> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> excluded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private long observationOrder;
    private bool loaded;
    private sealed class Flight(string platform, int generation)
    {
        internal readonly string Platform = platform;
        internal readonly int Generation = generation;
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<MatchResult> Work = null!;
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { } // Completion may already have released this flight.
        }
    }
    internal sealed record MatchResult(JsonObject Song, string? Error = null);
    internal static string Query(JsonObject song) => (LJ.S(song["title"]) + " " + LJ.S(song["artist"])).Trim();
    private static string Key(string songId, string platform) => LJ.Stringify(new JsonArray(songId, platform));
    internal long NextObservation() => Interlocked.Increment(ref observationOrder);
    internal (int Generation, long Order) BeginSearch(string platform)
    { lock (gate) return (generations.GetValueOrDefault(platform), NextObservation()); }
    internal async Task InitializeAsync(CancellationToken token)
    {
        lock (gate) if (loaded) return;
        await storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (loaded) return;
            var saved = await store.ReadAsync(token).ConfigureAwait(false);
            var current = await library.ReadMatchingSongIdsAsync(saved.Select(entry => LJ.S(entry["songId"])), token)
                .ConfigureAwait(false);
            lock (gate)
            {
                foreach (var entry in saved)
                {
                    var songId = LJ.S(entry["songId"]);
                    if (!current.Contains(songId)) continue; // Evict recoverable state whose current repository record no longer exists.
                    foreach (var pair in entry["platforms"]!.AsObject())
                        states[Key(songId, pair.Key)] = pair.Value!.DeepClone().AsObject();
                    excluded[songId] = entry["excluded"]!.AsArray().Select(value => LJ.S(value)).ToHashSet(StringComparer.Ordinal);
                }
                loaded = true;
            }
        }
        finally { storage.Release(); }
    }
    internal JsonObject ProjectKnownFacts(JsonObject value) => library.ProjectRepository(value, MusicLibraryOwner.PlatformSongShape.Page);
    internal JsonElement ProjectKnownFacts(JsonElement value) => LJ.Element(ProjectKnownFacts(JsonNode.Parse(value.GetRawText())!.AsObject()));
    internal async Task<JsonObject> KnownAsync(JsonObject song, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        return await library.AdoptSongAsync(NativeMusicSong.Normalize(song), token).ConfigureAwait(false);
    }
    internal async Task<MatchResult> MatchAsync(JsonObject song, string platform, string context,
        CancellationToken token, bool contentFailed = false)
    {
        song = await KnownAsync(song, token).ConfigureAwait(false);
        string songId = LJ.S(song["songId"]), key = Key(songId, platform);
        Flight flight;
        lock (gate)
        {
            var sources = LJ.Array(song["sources"]).OfType<JsonObject>();
            bool hasSource = sources.Any(source => LJ.S(source["source"]) == platform && !IsExcluded(song, LJ.S(source["sourceKey"])));
            var status = states.GetValueOrDefault(key);
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            bool same = status is not null && LJ.S(status["context"]) == context && LJ.S(status["query"]) == Query(song);
            bool recheck = contentFailed && (!same || status!["matchedAt"] is null || now - LJ.I(status["matchedAt"]) >= Freshness);
            if (hasSource && !recheck || same && now - LJ.I(status!["checkedAt"]) is >= 0 and < NotFoundRetry &&
                LJ.S(status["state"]) == "notFound") return new(song);
            if (!NativeMusicSong.IsOnline(song) || NativeMusicSong.Fingerprint(song).Length == 0 || LJ.N(song["duration"]) <= 0)
                return new(song);
            int generation = generations.GetValueOrDefault(platform);
            string flightKey = LJ.Stringify(new JsonArray(songId, platform, context, Query(song), generation));
            if (!flights.TryGetValue(flightKey, out flight!))
            {
                flight = new(platform, generation); flights.Add(flightKey, flight);
                var owned = flight; var original = song.DeepClone().AsObject(); long order = NextObservation();
                flight.Work = Run(original, platform, context, generation, order, owned.Cancellation.Token);
                _ = flight.Work.ContinueWith(done =>
                {
                    _ = done.Exception;
                    lock (gate) if (flights.GetValueOrDefault(flightKey) == owned) flights.Remove(flightKey);
                    owned.Cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        var result = await flight.Work.WaitAsync(token).ConfigureAwait(false);
        lock (gate) LJ.Require(generations.GetValueOrDefault(platform) == flight.Generation, "AUTH_CHANGED");
        await PersistAsync(token).ConfigureAwait(false);
        return result;
    }
    private async Task<MatchResult> Run(JsonObject song, string platform, string context, int generation, long order, CancellationToken token)
    {
        await Task.Yield();
        string? error = null;
        bool found = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12), clock);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            // SearchCatalog adopts actual results into the unique repository. It alone may register sources;
            // this discovery owner must not attach an ambiguous candidate or rebind an existing source.
            var page = await search(new(platform, Query(song)), request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            found = page.GetProperty("items").EnumerateArray().Any(item =>
            {
                var candidate = item.TryGetProperty("song", out var nested) ? nested : item;
                var known = library.ProjectRepository(JsonNode.Parse(candidate.GetRawText())!.AsObject(), MusicLibraryOwner.PlatformSongShape.Song);
                return LJ.S(known["songId"]) == LJ.S(song["songId"]) &&
                    LJ.Array(known["sources"]).OfType<JsonObject>().Any(source =>
                        LJ.S(source["source"]) == platform && !IsExcluded(known, LJ.S(source["sourceKey"])));
            });
        }
        catch (MusicPlatformException failure) { error = failure.Code; }
        catch (MusicLibraryException failure) { error = failure.Code; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { error = "TIMEOUT"; }
        token.ThrowIfCancellationRequested();
        var known = library.ProjectRepository(song, MusicLibraryOwner.PlatformSongShape.Song);
        lock (gate)
        {
            LJ.Require(generations.GetValueOrDefault(platform) == generation, "AUTH_CHANGED");
            Apply(LJ.S(known["songId"]), platform, context, Query(song), clock.GetUtcNow().ToUnixTimeMilliseconds(), order, error, found);
        }
        return new(known, error);
    }
    private void Apply(string songId, string platform, string context, string query, long at, long order, string? error, bool found)
    {
        string key = Key(songId, platform);
        if (orders.GetValueOrDefault(key) >= order || states.TryGetValue(key, out var old) && LJ.I(old["checkedAt"]) > at) return;
        states[key] = LJ.Obj(("state", error is not null ? "failed" : found ? "matched" : "notFound"),
            ("checkedAt", at), ("context", context), ("query", query), ("code", error ?? ""),
            ("matchedAt", found && error is null ? JsonValue.Create(at) : old?["matchedAt"]));
        orders[key] = order;
    }
    internal void ObservePage(MusicSearchRequest request, JsonElement? response, string error, string context,
        (int Generation, long Order) observation)
    {
        if (request.Kind != MusicSearchKind.Songs) return;
        lock (gate)
        {
            if (generations.GetValueOrDefault(request.Platform) != observation.Generation) return;
            Observe(request.Platform, request.Keyword, request.Page, response, error, context,
                clock.GetUtcNow().ToUnixTimeMilliseconds(), observation.Order);
        }
    }
    internal void ObserveSearch(JsonElement snapshot, Func<string, string> context)
    {
        if (MusicDto.Text(snapshot, "kind") != "songs" || snapshot.GetProperty("cancelled").GetBoolean()) return;
        lock (gate)
            foreach (var observation in snapshot.GetProperty("observations").EnumerateArray())
            {
                string platform = observation.GetProperty("platform").GetString()!;
                if (observation.GetProperty("generation").GetInt64() != generations.GetValueOrDefault(platform)) continue;
                Observe(platform, snapshot.GetProperty("keyword").GetString()!, observation.GetProperty("page").GetInt32(),
                    observation, observation.GetProperty("error").GetString()!, context(platform),
                    observation.GetProperty("checkedAt").GetInt64(), observation.GetProperty("order").GetInt64());
            }
    }
    private void Observe(string platform, string query, int page, JsonElement? response, string error,
        string context, long at, long order)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (response is { } value && error.Length == 0)
            foreach (var item in ProjectKnownFacts(value).GetProperty("items").EnumerateArray())
            {
                var row = item.TryGetProperty("song", out var nested) ? nested : item;
                string id = row.GetProperty("songId").GetString()!;
                found.Add(id);
                Apply(id, platform, context, query, at, order, null, true);
            }
        if (page != 1) return;
        foreach (var song in library.SongSnapshots(query))
        {
            string id = LJ.S(song["songId"]);
            if (!found.Contains(id) && Query(song) == query)
                Apply(id, platform, context, query, at, order, error.Length == 0 ? null : error, false);
        }
    }
    internal bool IsExcluded(JsonObject song, string sourceKey)
    { lock (gate) return excluded.GetValueOrDefault(LJ.S(song["songId"]))?.Contains(sourceKey) == true; }
    internal async Task ExcludeAsync(JsonObject song, string sourceKey, CancellationToken token)
    {
        song = await KnownAsync(song, token).ConfigureAwait(false);
        lock (gate)
        {
            string id = LJ.S(song["songId"]);
            if (!excluded.TryGetValue(id, out var values)) excluded[id] = values = [];
            values.Add(sourceKey);
        }
        await PersistAsync(token).ConfigureAwait(false);
    }
    internal void Invalidate(string platform)
    {
        Flight[] work;
        lock (gate)
        {
            generations[platform] = generations.GetValueOrDefault(platform) + 1;
            foreach (var key in states.Keys.Where(key => JsonNode.Parse(key)![1]!.GetValue<string>() == platform).ToArray())
            { states.Remove(key); orders.Remove(key); }
            work = flights.Values.Where(flight => flight.Platform == platform).ToArray();
        }
        foreach (var flight in work) flight.Cancel();
    }
    internal async Task PersistAsync(CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await storage.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string[] ids;
            lock (gate)
                ids = states.Keys.Select(key => LJ.S(JsonNode.Parse(key)![0])).Concat(excluded.Keys)
                    .Distinct(StringComparer.Ordinal).ToArray();
            var songs = await library.ReadMatchingSongIdsAsync(ids, token).ConfigureAwait(false);
            JsonObject[] snapshot;
            lock (gate)
                snapshot = songs.Select(id =>
                {
                    var platforms = new JsonObject();
                    foreach (string platform in NativeMusicSong.Platforms)
                        if (states.TryGetValue(Key(id, platform), out var state)) platforms[platform] = state.DeepClone();
                    return LJ.Obj(("songId", id), ("platforms", platforms), ("excluded", LJ.Arr(excluded.GetValueOrDefault(id) ?? [])));
                }).Where(entry => entry["platforms"]!.AsObject().Count > 0 || entry["excluded"]!.AsArray().Count > 0).ToArray();
            await store.WriteAsync(snapshot, token).ConfigureAwait(false);
        }
        catch (MusicLibraryException) { /* Recoverable discovery state cannot change a library commit receipt. */ }
        catch (DocumentStoreException) { }
        finally { storage.Release(); }
    }
    internal async Task DrainAsync()
    {
        Flight[] work; lock (gate) work = flights.Values.ToArray();
        foreach (var flight in work) flight.Cancel();
        try { await Task.WhenAll(work.Select(flight => flight.Work)).ConfigureAwait(false); } catch { }
    }
}
