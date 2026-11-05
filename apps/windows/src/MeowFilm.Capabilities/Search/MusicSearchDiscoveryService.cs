// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

/// <summary>Read-only native discovery with one bounded in-memory snapshot, not a document/credential owner.</summary>
public sealed class NativeMusicSearchDiscovery : IMusicSearchDiscovery
{
    private readonly IMusicPlatformSessionSource sessions;
    private readonly MusicPlatformClientContextOwner clients;
    private readonly MusicPlatformSearchDiscovery protocol;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim changes = new(1);
    private readonly object gate = new();
    private Flight? active;
    private MusicSearchDiscoverySnapshot? cache;
    private MusicPlatformSession? cachedSession;
    private MusicWyClientContext? cachedClient;
    private int offset;
    private long generation;
    private bool closed;
    private Task? closeTask;
    private sealed class Flight(long generation, TimeProvider clock)
    {
        internal readonly long Generation = generation;
        internal readonly CancellationTokenSource Cancelled = new();
        internal readonly CancellationTokenSource Deadline = new(TimeSpan.FromSeconds(25), clock);
        internal CancellationTokenSource Linked = null!;
        internal Task<MusicSearchDiscoverySnapshot> Task = null!;
        internal MusicPlatformSession? Session;
        internal MusicWyClientContext? Client;
        internal string Reason = "";
        internal void Cancel()
        {
            try { Cancelled.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
    }
    internal NativeMusicSearchDiscovery(IMusicPlatformSessionSource sessions, MusicPlatformHttp http,
        MusicPlatformClientContextOwner clients, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clients);
        this.sessions = sessions; this.clients = clients; protocol = new(http, clients); this.clock = clock ?? TimeProvider.System;
    }
    public async Task<MusicSearchDiscoverySnapshot> ReadAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
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
                    if (!refresh && cache is not null && Current(cachedSession) &&
                        cachedClient is not null && clients.IsCurrent(cachedClient)) return Copy(cache);
                    previous = active;
                    if (previous is null)
                    {
                        flight = new(++generation, clock);
                        flight.Linked = CancellationTokenSource.CreateLinkedTokenSource(flight.Cancelled.Token, flight.Deadline.Token);
                        active = flight;
                        flight.Task = Task.Run(() => Fetch(flight));
                        _ = flight.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        break;
                    }
                    if (previous.Reason.Length == 0 && previous.Generation == generation)
                    {
                        flight = previous;
                        break;
                    }
                    if (previous.Reason.Length == 0) Volatile.Write(ref previous.Reason, "STALE_SESSION");
                }
                // Account invalidation can leave a retiring physical request. Do not
                // join it or start a replacement until all of its cleanup has settled.
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
                GuardClient(flight.Client);
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
            var data = await protocol.ReadAsync(flight.Session, () => Guard(flight), token).ConfigureAwait(false);
            flight.Client = data.Client;
            var finalSession = await Acquire(token).ConfigureAwait(false); Guard(flight);
            if (!Same(flight.Session, finalSession)) throw Error("STALE_SESSION");
            var snapshot = new MusicSearchDiscoverySnapshot(flight.Generation, data.Hot, data.Suggestions,
                data.Suggestions.Take(6).ToArray(), data.Charts, data.Failures, clock.GetUtcNow().ToUnixTimeMilliseconds());
            if (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > 512 * 1024) throw Error("LIMIT");
            lock (gate)
            {
                Guard(flight); cache = snapshot; cachedSession = flight.Session; cachedClient = flight.Client; offset = 0;
                return snapshot;
            }
        }
        catch (OperationCanceledException) { Guard(flight); throw Error("CANCELLED"); }
        catch (MusicPlatformException error) { Guard(flight); throw Error(MusicPlatformSearchDiscovery.SafeCode(error.Code)); }
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
        try { value = await sessions.AcquireAsync("wy", token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { throw Error("CREDENTIALS_UNAVAILABLE"); }
        token.ThrowIfCancellationRequested();
        if (value is not null && (value.Platform != "wy" || !Current(value))) throw Error("STALE_SESSION");
        return value;
    }
    private bool Current(MusicPlatformSession? session)
    {
        if (session is null) return true;
        try { return sessions.IsCurrent(session); }
        catch { throw Error("CREDENTIALS_UNAVAILABLE"); }
    }
    private static bool Same(MusicPlatformSession? first, MusicPlatformSession? second) =>
        first is null ? second is null : second is not null && first.Platform == second.Platform &&
            first.Scope == second.Scope && first.Generation == second.Generation && first.UserId == second.UserId && first.Cookie == second.Cookie;
    private void GuardClient(MusicWyClientContext? client)
    {
        if (client is not null && !clients.IsCurrent(client))
            throw Error(clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
    }
    private void Guard(Flight flight)
    {
        lock (gate)
        {
            if (closed) throw Error("CLOSED");
            var reason = Volatile.Read(ref flight.Reason);
            if (reason.Length > 0) throw Error(reason);
            if (flight.Generation != generation || !Current(flight.Session)) throw Error("STALE_SESSION");
            GuardClient(flight.Client);
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
            GuardClient(cachedClient);
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
            generation++; cache = null; cachedSession = null; cachedClient = null; offset = 0; old = active;
            if (old is not null) Volatile.Write(ref old.Reason, "STALE_SESSION");
        }
        old?.Cancel();
    }
    public async Task CancelAndDrainAsync()
    {
        // Do not hold gate while waiting for native cleanup or callback completion.
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
            closed = true; generation++; cache = null; cachedSession = null; cachedClient = null;
            var old = active; if (old is not null) Volatile.Write(ref old.Reason, "CLOSED");
            // Schedule outside caller lock/callback stacks; still return the actual drain task.
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
