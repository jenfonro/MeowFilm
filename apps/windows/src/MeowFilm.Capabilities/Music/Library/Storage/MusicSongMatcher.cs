using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Source discovery and its scoped retry state. It never owns a second song document.</summary>
internal sealed class MusicSongMatcher(MusicLibraryOwner library, MusicSongMatchStore store,
    Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> search) : IDisposable
{
    internal const long Freshness = 7L * 86400000, NotFoundRetry = 86400000;
    private readonly object gate = new();
    private readonly SemaphoreSlim storage = new(1, 1);
    private readonly Dictionary<(string SongId, string Platform), SongMatchPlatformState> states = new();
    private readonly Dictionary<(string SongId, string Platform), long> orders = new();
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SongId, string Platform, string Context, string Query, int Generation), Flight> flights = new();
    private readonly HashSet<string> dirtySongs = new(StringComparer.Ordinal);
    private readonly HashSet<string> removedSongs = new(StringComparer.Ordinal);
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

    internal sealed record MatchResult(MusicSongRecord Song, string? Error = null);
    internal static string Query(MusicSongRecord song) => (song.Title + " " + song.Artist).Trim();
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
            var current = await library.ReadMatchingSongIdsAsync(saved.Select(entry => entry.SongId), token)
                .ConfigureAwait(false);
            lock (gate)
            {
                foreach (var entry in saved)
                {
                    if (!current.Contains(entry.SongId))
                    {
                        removedSongs.Add(entry.SongId);
                        continue; // Evict recoverable state whose current repository record no longer exists.
                    }
                    foreach (var (platform, state) in entry.Platforms)
                        states[(entry.SongId, platform)] = state;
                }
                loaded = true;
            }
        }
        finally { storage.Release(); }
    }

    internal JsonElement ProjectKnownFacts(JsonElement value) =>
        library.ProjectRepository(value, MusicLibraryOwner.PlatformSongShape.Page);

    internal async Task<MusicSongRecord> KnownAsync(MusicSongRecord song, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        return await library.AdoptSongAsync(NativeMusicSong.Normalize(song), token).ConfigureAwait(false);
    }

    internal async Task<MatchResult> MatchAsync(MusicSongRecord song, string platform, string context,
        CancellationToken token, bool contentFailed = false)
    {
        song = await KnownAsync(song, token).ConfigureAwait(false);
        string songId = song.SongId;
        var key = (songId, platform);
        Flight flight;
        lock (gate)
        {
            bool hasSource = song.Sources.Any(source => source.Platform == platform);
            var status = states.GetValueOrDefault(key);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string query = Query(song);
            bool same = status is not null && status.Context == context && status.Query == query;
            bool recheck = contentFailed && (!same || status!.MatchedAt is null || now - status.MatchedAt.Value >= Freshness);
            if (hasSource && !recheck || same && now - status!.CheckedAt is >= 0 and < NotFoundRetry &&
                status.State == "notFound") return new MatchResult(song);
            if (!NativeMusicSong.IsOnline(song) || NativeMusicSong.Fingerprint(song).Length == 0 || (song.Duration ?? 0) <= 0)
                return new MatchResult(song);
            int generation = generations.GetValueOrDefault(platform);
            var flightKey = (songId, platform, context, query, generation);
            if (!flights.TryGetValue(flightKey, out flight!))
            {
                flight = new Flight(platform, generation);
                flights.Add(flightKey, flight);
                var owned = flight;
                var original = song;
                long order = NextObservation();
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
        lock (gate) MusicRules.Require(generations.GetValueOrDefault(platform) == flight.Generation, "AUTH_CHANGED");
        await PersistAsync(token).ConfigureAwait(false);
        return result;
    }

    private async Task<MatchResult> Run(MusicSongRecord song, string platform, string context, int generation, long order, CancellationToken token)
    {
        await Task.Yield();
        string? error = null;
        bool found = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
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
                var knownEl = library.ProjectRepository(candidate, MusicLibraryOwner.PlatformSongShape.Song);
                if (MusicWireCodec.Str(knownEl, "songId") != song.SongId) return false;
                if (!MusicWireCodec.TryGet(knownEl, "sources", out var sourcesEl) || sourcesEl.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (var src in sourcesEl.EnumerateArray())
                    if (MusicWireCodec.Str(src, "source") == platform)
                        return true;
                return false;
            });
        }
        catch (MusicPlatformException failure) { error = failure.Code; }
        catch (MusicLibraryException failure) { error = failure.Code; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { error = "TIMEOUT"; }
        token.ThrowIfCancellationRequested();
        var known = library.ProjectSong(song);
        lock (gate)
        {
            MusicRules.Require(generations.GetValueOrDefault(platform) == generation, "AUTH_CHANGED");
            Apply(known.SongId, platform, context, Query(song), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), order, error, found);
        }
        return new MatchResult(known, error);
    }

    private void Apply(string songId, string platform, string context, string query, long at, long order, string? error, bool found)
    {
        var key = (songId, platform);
        if (orders.GetValueOrDefault(key) >= order || states.TryGetValue(key, out var old) && old.CheckedAt > at) return;
        states[key] = new SongMatchPlatformState(
            error is not null ? "failed" : found ? "matched" : "notFound",
            at, context, query, error ?? "",
            found && error is null ? at : old?.MatchedAt);
        orders[key] = order;
        dirtySongs.Add(songId);
        removedSongs.Remove(songId);
    }

    internal void ObservePage(MusicSearchRequest request, JsonElement? response, string error, string context,
        (int Generation, long Order) observation)
    {
        if (request.Kind != MusicSearchKind.Songs) return;
        lock (gate) if (generations.GetValueOrDefault(request.Platform) != observation.Generation) return;
        var (found, missing) = PrepareObservation(request.Keyword, request.Page, response, error);
        long at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (gate)
        {
            if (generations.GetValueOrDefault(request.Platform) != observation.Generation) return;
            ApplyObservation(request.Platform, request.Keyword, found, missing, error, context, at, observation.Order);
        }
    }

    internal void ObserveSearch(JsonElement snapshot, Func<string, string> context)
    {
        if (MusicDto.Text(snapshot, "kind") != "songs" || snapshot.GetProperty("cancelled").GetBoolean()) return;
        string keyword = snapshot.GetProperty("keyword").GetString()!;
        foreach (var observation in snapshot.GetProperty("observations").EnumerateArray())
        {
            string platform = observation.GetProperty("platform").GetString()!;
            long generation = observation.GetProperty("generation").GetInt64();
            lock (gate) if (generation != generations.GetValueOrDefault(platform)) continue;
            string error = observation.GetProperty("error").GetString()!;
            int page = observation.GetProperty("page").GetInt32();
            long checkedAt = observation.GetProperty("checkedAt").GetInt64();
            long order = observation.GetProperty("order").GetInt64();
            var (found, missing) = PrepareObservation(keyword, page, observation, error);
            string matchContext = context(platform);
            lock (gate)
            {
                if (generation != generations.GetValueOrDefault(platform)) continue;
                ApplyObservation(platform, keyword, found, missing, error, matchContext, checkedAt, order);
            }
        }
    }

    private (HashSet<string> Found, List<string> Missing) PrepareObservation(string query, int page,
        JsonElement? response, string error)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (response is { } value && error.Length == 0)
            foreach (var item in ProjectKnownFacts(value).GetProperty("items").EnumerateArray())
            {
                var row = item.TryGetProperty("song", out var nested) ? nested : item;
                found.Add(row.GetProperty("songId").GetString()!);
            }
        var missing = new List<string>();
        if (page == 1)
            foreach (var song in library.SongSnapshots(query))
            {
                string id = song.SongId;
                if (!found.Contains(id) && Query(song) == query) missing.Add(id);
            }
        return (found, missing);
    }

    private void ApplyObservation(string platform, string query, HashSet<string> found, List<string> missing,
        string error, string context, long at, long order)
    {
        foreach (var id in found)
            Apply(id, platform, context, query, at, order, null, true);
        string? missError = error.Length == 0 ? null : error;
        foreach (var id in missing)
            Apply(id, platform, context, query, at, order, missError, false);
    }

    internal void Invalidate(string platform)
    {
        Flight[] work;
        lock (gate)
        {
            generations[platform] = generations.GetValueOrDefault(platform) + 1;
            foreach (var key in states.Keys.Where(k => k.Platform == platform).ToArray())
            {
                states.Remove(key);
                orders.Remove(key);
                dirtySongs.Add(key.SongId);
            }
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
            string[] dirty;
            string[] removed;
            lock (gate)
            {
                if (dirtySongs.Count == 0 && removedSongs.Count == 0) return;
                dirty = dirtySongs.ToArray();
                removed = removedSongs.ToArray();
                dirtySongs.Clear();
                removedSongs.Clear();
            }
            var validSongs = dirty.Length > 0
                ? await library.ReadMatchingSongIdsAsync(dirty, token).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal);
            var updated = new List<SongMatchRecord>();
            var toRemove = new HashSet<string>(removed, StringComparer.Ordinal);
            lock (gate)
            {
                foreach (string id in dirty)
                {
                    if (!validSongs.Contains(id))
                    {
                        toRemove.Add(id);
                        continue;
                    }
                    var platforms = new Dictionary<string, SongMatchPlatformState>(StringComparer.Ordinal);
                    foreach (string platform in NativeMusicSong.Platforms)
                        if (states.TryGetValue((id, platform), out var state)) platforms[platform] = state;
                    if (platforms.Count > 0)
                        updated.Add(new SongMatchRecord(id, platforms, []));
                    else
                        toRemove.Add(id);
                }
            }
            await store.ApplyChangesAsync(updated, toRemove.ToArray(), token).ConfigureAwait(false);
        }
        catch (MusicLibraryException) { /* Recoverable discovery state cannot change a library commit receipt. */ }
        catch (StorageException) { }
        finally { storage.Release(); }
    }

    internal async Task ClearAsync(CancellationToken token)
    {
        Flight[] work;
        lock (gate)
        {
            states.Clear();
            orders.Clear();
            dirtySongs.Clear();
            removedSongs.Clear();
            loaded = true;
            work = flights.Values.ToArray();
        }
        foreach (var flight in work) flight.Cancel();
        await storage.WaitAsync(token).ConfigureAwait(false);
        try { await store.ClearAsync(token).ConfigureAwait(false); }
        finally { storage.Release(); }
    }

    internal Task<(int SongCount, long StoredBytes, long MaximumBytes)> GetStatsAsync(CancellationToken token = default) =>
        store.GetStatsAsync(token);

    internal async Task DrainAsync()
    {
        Flight[] work; lock (gate) work = flights.Values.ToArray();
        foreach (var flight in work) flight.Cancel();
        try { await Task.WhenAll(work.Select(flight => flight.Work)).ConfigureAwait(false); } catch { }
    }

    public void Dispose()
    {
        storage.Dispose();
        store.Dispose();
    }
}
