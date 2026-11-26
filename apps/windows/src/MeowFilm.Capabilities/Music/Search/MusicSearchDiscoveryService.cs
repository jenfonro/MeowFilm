// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

public sealed record MusicSearchDiscoveryHot(string Keyword, double? Score);
public sealed record MusicSearchDiscoveryChart(string Id, string Name, string Cover, long? Count, string Creator,
    JsonElement[] Songs);
public sealed record MusicSearchDiscoveryFailure(string Stage, string? ChartId, string Code);
public sealed record MusicSearchDiscoverySnapshot(long Generation, MusicSearchDiscoveryHot[] Hot,
    string[] Suggestions, string[] Recommended, MusicSearchDiscoveryChart[] Charts,
    MusicSearchDiscoveryFailure[] Failures, long FetchedAt);

/// <summary>Read-only native discovery with one bounded in-memory snapshot, not a document/credential owner.</summary>
internal sealed class NativeMusicSearchDiscovery(
    MusicAccountOwner accounts, MusicPlatformClient client, MusicLibraryOwner library) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static TimeProvider clock => TimeProvider.System;
    private readonly SemaphoreSlim changes = new(1);
    private readonly object gate = new();
    private Flight? active;
    private MusicSearchDiscoverySnapshot? cache;
    private MusicPlatformSession? cachedSession;
    private int offset;
    private long generation;
    private bool closed;
    private Task? closeTask;

    private sealed class Flight(long generation, bool refresh)
    {
        internal readonly long Generation = generation;
        internal readonly bool Refresh = refresh;
        internal readonly CancellationTokenSource Cancelled = new();
        internal readonly CancellationTokenSource Deadline = new(TimeSpan.FromSeconds(25));
        internal CancellationTokenSource Linked = null!;
        internal Task<MusicSearchDiscoverySnapshot> Task = null!;
        internal MusicPlatformSession? Session;
        internal string Reason = "";
        internal void Cancel()
        {
            try { Cancelled.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
    }

    public bool IsCurrent(long expectedGeneration)
    {
        lock (gate)
            return !closed && expectedGeneration > 0 && cache is not null &&
                cache.Generation == expectedGeneration && Current(cachedSession);
    }

    public async Task<MusicSearchDiscoverySnapshot> ReadAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await accounts.RestoreForDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        Flight flight;
        try { await changes.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw Error("CANCELLED"); }
        try
        {
            while (true)
            {
                Flight? previous;
                lock (gate)
                {
                    if (closed) throw Error("CLOSED");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!refresh && cache is not null && Current(cachedSession)) return Copy(cache);
                    previous = active;
                    if (previous is null)
                    {
                        flight = new(++generation, refresh);
                        flight.Linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Cancelled.Token, flight.Deadline.Token);
                        active = flight;
                        flight.Task = Task.Run(() => Fetch(flight));
                        _ = flight.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    }
                    if (previous.Reason.Length == 0 && previous.Generation == generation && (!refresh || previous.Refresh))
                    {
                        flight = previous;
                        break;
                    }
                    if (previous.Reason.Length == 0) Volatile.Write(ref previous.Reason, "STALE_SESSION");
                }
                previous.Cancel(); await Settle(previous.Task).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw Error("CANCELLED"); }
        finally { changes.Release(); }
        try
        {
            var result = await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (closed) throw Error("CLOSED");
                if (flight.Generation != generation || !Current(flight.Session)) throw Error("STALE_SESSION");
                cancellationToken.ThrowIfCancellationRequested(); return Copy(result);
            }
        }
        catch (OperationCanceledException) { throw Error("CANCELLED"); }
    }

    private async Task<MusicSearchDiscoverySnapshot> Fetch(Flight flight)
    {
        var token = flight.Linked.Token;
        try
        {
            Guard(flight); flight.Session = await Acquire(token).ConfigureAwait(false); Guard(flight);
            long observation = library.BeginSongObservation();
            var value = await client.RequestAsync("wy", "searchDiscovery", new { refresh = flight.Refresh }, flight.Session, token, () => Guard(flight)).ConfigureAwait(false);
            value = await library.AdoptPlatformResultAsync(value, token, observation: observation,
                shape: MusicLibraryOwner.PlatformSongShape.Discovery).ConfigureAwait(false);
            Guard(flight);
            var suggestions = value.GetProperty("suggestions").Deserialize<string[]>(Json)!;
            var snapshot = new MusicSearchDiscoverySnapshot(flight.Generation,
                value.GetProperty("hot").Deserialize<MusicSearchDiscoveryHot[]>(Json)!,
                suggestions, suggestions.Take(6).ToArray(),
                value.GetProperty("charts").Deserialize<MusicSearchDiscoveryChart[]>(Json)!,
                value.GetProperty("failures").Deserialize<MusicSearchDiscoveryFailure[]>(Json)!,
                clock.GetUtcNow().ToUnixTimeMilliseconds());
            lock (gate)
            {
                Guard(flight); cache = snapshot; cachedSession = flight.Session; offset = 0;
                return snapshot;
            }
        }
        catch (OperationCanceledException) { Guard(flight); throw Error("CANCELLED"); }
        catch (MusicPlatformException error) { Guard(flight); throw Error(error.Code); }
        catch { Guard(flight); throw Error("INVALID_RESPONSE"); }
        finally
        {
            flight.Linked.Dispose(); flight.Deadline.Dispose(); flight.Cancelled.Dispose();
            lock (gate) if (ReferenceEquals(active, flight)) active = null;
        }
    }

    private async Task<MusicPlatformSession?> Acquire(CancellationToken token)
    {
        MusicPlatformSession? value;
        try { value = await accounts.AcquireSettledAsync("wy", token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw Error("CREDENTIALS_UNAVAILABLE"); }
        token.ThrowIfCancellationRequested();
        if (value is not null && (value.Platform != "wy" || !Current(value))) throw Error("STALE_SESSION");
        return value;
    }

    private bool Current(MusicPlatformSession? session)
    {
        if (session is null) return true;
        try { return accounts.IsCurrent(session); }
        catch { throw Error("CREDENTIALS_UNAVAILABLE"); }
    }

    private void Guard(Flight flight)
    {
        lock (gate)
        {
            if (closed) throw Error("CLOSED");
            var reason = Volatile.Read(ref flight.Reason);
            if (reason.Length > 0) throw Error(reason);
            if (flight.Generation != generation || !Current(flight.Session)) throw Error("STALE_SESSION");
            if (flight.Deadline.IsCancellationRequested) throw Error("TIMEOUT");
            if (flight.Cancelled.IsCancellationRequested) throw Error("CANCELLED");
        }
    }

    public MusicSearchDiscoverySnapshot Rotate(long expectedGeneration)
    {
        lock (gate)
        {
            if (closed) throw Error("CLOSED");
            if (cache is null || cache.Generation != expectedGeneration || !Current(cachedSession)) throw Error("STALE_SESSION");
            var words = cache.Suggestions;
            offset = words.Length == 0 ? 0 : (offset + 6) % words.Length;
            cache = cache with { Recommended = Enumerable.Range(0, Math.Min(6, words.Length)).Select(i => words[(offset + i) % words.Length]).ToArray() };
            return Copy(cache);
        }
    }

    public void Invalidate(string? platform = null)
    {
        if (platform is not null and not "wy") return;
        Flight? old;
        lock (gate)
        {
            if (closed) return;
            generation++; cache = null; cachedSession = null; offset = 0; old = active;
            if (old is not null) Volatile.Write(ref old.Reason, "STALE_SESSION");
        }
        old?.Cancel();
    }

    public async Task CancelAndDrainAsync()
    {
        await changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Flight? old;
            lock (gate)
            {
                old = active;
                if (old is not null)
                {
                    generation++;
                    if (old.Reason.Length == 0) Volatile.Write(ref old.Reason, "CANCELLED");
                }
            }
            if (old is not null) { old.Cancel(); await Settle(old.Task).ConfigureAwait(false); }
        }
        finally { changes.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closeTask is not null) return new(closeTask);
            closed = true; generation++; cache = null; cachedSession = null;
            var old = active; if (old is not null) Volatile.Write(ref old.Reason, "CLOSED");
            closeTask = Task.Run(async () => { old?.Cancel(); await CancelAndDrainAsync().ConfigureAwait(false); });
            return new(closeTask);
        }
    }

    private static async Task Settle(Task task) { try { await task.ConfigureAwait(false); } catch when (task.IsCompleted) { } }
    private static MusicSearchDiscoverySnapshot Copy(MusicSearchDiscoverySnapshot value) => value with
    {
        Hot = value.Hot.ToArray(), Suggestions = value.Suggestions.ToArray(), Recommended = value.Recommended.ToArray(),
        Charts = value.Charts.Select(chart => chart with { Songs = chart.Songs.Select(song => song.Clone()).ToArray() }).ToArray(),
        Failures = value.Failures.ToArray()
    };
    private static MusicPlatformException Error(string code) => new(code, "wy", "discovery");
}
