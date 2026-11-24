using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

/// <summary>Source discovery and its scoped retry state. It never owns a second song document.</summary>
internal sealed class MusicSongMatcher(MusicLibraryOwner library, MusicSongMatchStore store,
    Func<MusicSearchRequest, CancellationToken, Task<JsonElement>> search) : IDisposable
{
    internal const long Freshness = 7L * 86400000, NotFoundRetry = 86400000;
    private readonly object gate = new();
    private readonly SemaphoreSlim storage = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> backgroundPersists = [];
    private int disposed;
    private int activeOps;
    private TaskCompletionSource opsDrained = CompletedOps();
    private bool draining;
    private readonly Dictionary<(string SongId, string Platform), SongMatchPlatformState> states = new();
    private readonly Dictionary<(string SongId, string Platform), long> orders = new();
    private readonly Dictionary<string, int> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SongId, string Platform, string Context, string Query, int Generation), Flight> flights = new();
    private readonly HashSet<string> dirtySongs = new(StringComparer.Ordinal);
    private readonly HashSet<string> removedSongs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> songChangeVersions = new(StringComparer.Ordinal);
    private long observationOrder, changeVersion;
    private bool loaded;

    private static TaskCompletionSource CompletedOps()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private bool TryEnterOperation()
    {
        lock (gate)
        {
            if (draining || Volatile.Read(ref disposed) != 0) return false;
            if (activeOps++ == 0) opsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    private void LeaveOperation()
    {
        lock (gate) if (--activeOps == 0) opsDrained.TrySetResult();
    }

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
    internal static string Query(MusicSongRecord song) =>
        NativeMusicSong.NormalText((song.Title + " " + song.Artist).Trim());
    internal long NextObservation() => Interlocked.Increment(ref observationOrder);

    internal async Task InitializeAsync(CancellationToken token)
    {
        lock (gate)
        {
            if (draining || Volatile.Read(ref disposed) != 0) throw new MusicLibraryException("CLOSED");
            if (loaded) return;
        }
        if (!TryEnterOperation()) throw new MusicLibraryException("CLOSED");
        bool entered = false;
        try
        {
            await storage.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            lock (gate)
            {
                if (draining || Volatile.Read(ref disposed) != 0) throw new MusicLibraryException("CLOSED");
                if (loaded) return;
            }
            var saved = await store.ReadAsync(token).ConfigureAwait(false);
            var current = await library.ReadMatchingSongIdsAsync(saved.Select(entry => entry.SongId), token)
                .ConfigureAwait(false);
            lock (gate)
            {
                foreach (var entry in saved)
                {
                    if (!current.Contains(entry.SongId))
                    {
                        dirtySongs.Remove(entry.SongId);
                        removedSongs.Add(entry.SongId);
                        songChangeVersions[entry.SongId] = ++changeVersion;
                        continue; // Evict recoverable state whose current repository record no longer exists.
                    }
                    foreach (var (platform, state) in entry.Platforms)
                    {
                        var key = (entry.SongId, platform);
                        if (!states.TryGetValue(key, out var existing) || state.CheckedAt > existing.CheckedAt)
                            states[key] = state;
                    }
                }
                loaded = true;
            }
        }
        finally
        {
            if (entered) storage.Release();
            LeaveOperation();
        }
    }

    internal MusicSongRecord[] ProjectKnownFacts(IReadOnlyList<MusicSongRecord> songs) =>
        library.ProjectSongs(songs);

    internal async Task<MusicSongRecord> KnownAsync(MusicSongRecord song, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        return await library.AdoptSongAsync(NativeMusicSong.Normalize(song), token).ConfigureAwait(false);
    }

    internal async Task<MatchResult> MatchAsync(MusicSongRecord song, string platform, string context,
        CancellationToken token, bool contentFailed = false)
    {
        if (!TryEnterOperation()) throw new MusicLibraryException("CLOSED");
        try
        {
            song = await KnownAsync(song, token).ConfigureAwait(false);
            string songId = song.SongId;
            var key = (songId, platform);
            Flight flight;
            lock (gate)
            {
                if (draining || Volatile.Read(ref disposed) != 0) throw new MusicLibraryException("CLOSED");
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
        finally { LeaveOperation(); }
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
                if (candidate.ValueKind != JsonValueKind.Object) return false;
                var knownCandidate = library.ProjectSong(NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(candidate)));
                return knownCandidate.SongId == song.SongId &&
                    knownCandidate.Sources.Any(src => src.Platform == platform);
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
        songChangeVersions[songId] = ++changeVersion;
    }

    // Broad keyword search pages may only confirm positive matches, never suppress
    // exact song matching with 24h notFound (per docs/common/music/smart-playback.md).
    internal void ObserveSearch(MusicSearchSnapshot snapshot, Func<string, string> context)
    {
        if (snapshot.Kind != "songs" || snapshot.Cancelled) return;
        string normalizedQuery = NativeMusicSong.NormalText(snapshot.Keyword);
        foreach (var observation in snapshot.Observations)
        {
            string platform = observation.Platform;
            long generation = observation.Generation;
            lock (gate) if (generation != generations.GetValueOrDefault(platform)) continue;
            if (observation.Error.Length > 0 || observation.Songs.Length == 0) continue;
            var projected = library.ProjectSongs(observation.Songs);
            var found = new HashSet<string>(projected.Select(s => s.SongId), StringComparer.Ordinal);
            if (found.Count == 0) continue;
            string matchContext = context(platform);
            lock (gate)
            {
                if (generation != generations.GetValueOrDefault(platform)) continue;
                ApplyObservation(platform, normalizedQuery, found, matchContext, observation.CheckedAt, observation.Order);
            }
        }
    }

    private void ApplyObservation(string platform, string query, HashSet<string> found,
        string context, long at, long order)
    {
        foreach (var id in found)
            Apply(id, platform, context, query, at, order, null, true);
    }

    internal void Invalidate(string platform)
    {
        Flight[] work;
        bool shouldPersist = false;
        lock (gate)
        {
            generations[platform] = generations.GetValueOrDefault(platform) + 1;
            foreach (var key in states.Keys.Where(k => k.Platform == platform).ToArray())
            {
                states.Remove(key);
                orders.Remove(key);
                dirtySongs.Add(key.SongId);
                songChangeVersions[key.SongId] = ++changeVersion;
                shouldPersist = true;
            }
            work = flights.Values.Where(flight => flight.Platform == platform).ToArray();
            if (shouldPersist && !draining && Volatile.Read(ref disposed) == 0)
            {
                var token = lifetime.Token;
                var task = PersistInvalidationAsync(token);
                backgroundPersists.Add(task);
                _ = task.ContinueWith(done =>
                {
                    _ = done.Exception;
                    lock (gate) backgroundPersists.Remove(done);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        foreach (var flight in work) flight.Cancel();
    }

    private async Task PersistInvalidationAsync(CancellationToken token)
    {
        await Task.Yield();
        try { await PersistAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "matcher", "invalidate_persist_failed",
                new Dictionary<string, object?> { ["error"] = error.Message });
        }
    }

    internal async Task PersistAsync(CancellationToken token)
    {
        if (!TryEnterOperation()) return;
        bool entered = false;
        try
        {
            await InitializeAsync(token).ConfigureAwait(false);
            await storage.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            string[] dirty;
            (string Id, long Version)[] removed;
            lock (gate)
            {
                if (dirtySongs.Count == 0 && removedSongs.Count == 0) return;
                dirty = dirtySongs.ToArray();
                removed = removedSongs.Select(id => (id, songChangeVersions.GetValueOrDefault(id))).ToArray();
            }
            var validSongs = dirty.Length > 0
                ? await library.ReadMatchingSongIdsAsync(dirty, token).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal);
            var updated = new List<SongMatchRecord>();
            var toRemove = new HashSet<string>(removed.Select(item => item.Id), StringComparer.Ordinal);
            var capturedDirty = new List<(string Id, long Version)>(dirty.Length);
            lock (gate)
            {
                foreach (string id in dirty)
                {
                    capturedDirty.Add((id, songChangeVersions.GetValueOrDefault(id)));
                    if (!validSongs.Contains(id))
                    {
                        toRemove.Add(id);
                        continue;
                    }
                    var platforms = new Dictionary<string, SongMatchPlatformState>(StringComparer.Ordinal);
                    foreach (string platform in NativeMusicSong.Platforms)
                        if (states.TryGetValue((id, platform), out var state)) platforms[platform] = state;
                    if (platforms.Count > 0)
                        updated.Add(new SongMatchRecord(id, platforms));
                    else
                        toRemove.Add(id);
                }
            }
            await store.ApplyChangesAsync(updated, toRemove.ToArray(), token).ConfigureAwait(false);
            lock (gate)
            {
                foreach (var (id, version) in capturedDirty)
                    if (songChangeVersions.GetValueOrDefault(id) == version) dirtySongs.Remove(id);
                foreach (var (id, version) in removed)
                    if (songChangeVersions.GetValueOrDefault(id) == version) removedSongs.Remove(id);
            }
        }
        catch (MusicLibraryException error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "matcher", "persist_skipped",
                new Dictionary<string, object?> { ["code"] = error.Code });
        }
        catch (StorageException error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "matcher", "persist_failed",
                new Dictionary<string, object?> { ["error"] = error.Error.ToString() });
        }
        finally
        {
            if (entered) storage.Release();
            LeaveOperation();
        }
    }

    internal async Task ClearAsync(CancellationToken token)
    {
        if (!TryEnterOperation()) throw new MusicLibraryException("CLOSED");
        bool entered = false;
        try
        {
            Flight[] work;
            lock (gate)
            {
                states.Clear();
                orders.Clear();
                dirtySongs.Clear();
                removedSongs.Clear();
                songChangeVersions.Clear();
                loaded = true;
                work = flights.Values.ToArray();
            }
            foreach (var flight in work) flight.Cancel();
            await storage.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            await store.ClearAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (entered) storage.Release();
            LeaveOperation();
        }
    }

    internal async Task<(int SongCount, long StoredBytes, long MaximumBytes)> GetStatsAsync(CancellationToken token = default)
    {
        if (!TryEnterOperation()) return (0, 0, 0);
        try { return await store.GetStatsAsync(token).ConfigureAwait(false); }
        finally { LeaveOperation(); }
    }

    internal void CancelFlights()
    {
        Flight[] work;
        lock (gate) work = flights.Values.ToArray();
        foreach (var flight in work) flight.Cancel();
    }

    internal async Task DrainAsync()
    {
        Task waitOps;
        lock (gate)
        {
            draining = true;
            waitOps = opsDrained.Task;
        }
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        while (true)
        {
            Flight[] work;
            Task[] persists;
            lock (gate)
            {
                work = flights.Values.ToArray();
                persists = backgroundPersists.ToArray();
            }
            if (work.Length == 0 && persists.Length == 0) break;
            foreach (var flight in work) flight.Cancel();
            if (work.Length > 0)
                try { await Task.WhenAll(work.Select(flight => flight.Work)).ConfigureAwait(false); } catch { }
            if (persists.Length > 0)
                try { await Task.WhenAll(persists).ConfigureAwait(false); } catch { }
        }
        await waitOps.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (gate) draining = true;
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        lifetime.Dispose();
        storage.Dispose();
        store.Dispose();
    }
}
