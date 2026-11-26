using System.Text.Json;
using MeowFilm.Core.Debug;
using MeowFilm.Threading;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>Displays Node search snapshots and forwards actions; Node resolves live video sources directly.</summary>
public sealed class FilmSearchService : IAsyncDisposable
{
    private const int MaxObserveReconnectAttempts = 3;
    private readonly Func<CancellationToken, Task<RuntimeConnection>> capture;
    private readonly FilmSourceService sources;
    private readonly OwnerDispatcher dispatcher = OwnerDispatcher.Current;
    private readonly List<FilmSearchSession> sessions = [];
    private bool stopping, closed;
    private Task? preparation, disposal;
    public FilmSearchSession? Current { get; private set; }
    public event EventHandler? Changed;

    public FilmSearchService(Func<CancellationToken, Task<RuntimeConnection>> capture,
        FilmSourceService sources)
    {
        this.capture = capture;
        this.sources = sources;
    }

    public FilmSearchSession Start(string query, CancellationToken token = default)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(closed, this);
        if (stopping) throw new InvalidOperationException("SHUTTING_DOWN");
        token.ThrowIfCancellationRequested();
        query = query.Trim();
        ArgumentException.ThrowIfNullOrEmpty(query);
        if (Current is { } retained && retained.Query == query)
        {
            if (retained.Id.Length == 0 && retained.Error.Length > 0)
            {
                Invalidate(retained);
            }
            else
            {
                if (retained.Stopped && !retained.Resuming)
                {
                    retained.Resuming = true;
                    retained.Commanding = true;
                    retained.Work = retained.Connection is null || retained.Connection.Revoked.IsCancellationRequested
                        ? RebindAsync(retained, retained.Work, ++retained.Publication)
                        : ResumeAsync(retained, retained.Work, ++retained.Publication);
                }
                return retained;
            }
        }
        Invalidate();
        var session = new FilmSearchSession(this, query);
        sessions.Add(session);
        Current = session;
        session.Changed += SessionChanged;
        DebugLog.Info(DebugCategories.Search, "film.search", "search.start", new { query });
        session.Work = OpenAsync(session);
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    internal Task SuspendRuntimeAsync()
    {
        dispatcher.VerifyAccess();
        if (Current is not { } session) return Task.CompletedTask;
        session.Publication++; session.Resuming = false; session.Commanding = false;
        session.Fail(new FilmPlaybackException("CLOSED"));
        session.Execution.Cancel();
        session.Observation?.Cancel();
        return DrainAsync();

        async Task DrainAsync()
        {
            await session.Work;
            await session.Observing;
            session.ResetBinding();
        }
    }

    private async Task RebindAsync(FilmSearchSession session, Task prior, long publication)
    {
        await Task.Yield();
        await prior;
        if (closed || stopping || session.Retired || session.Publication != publication) return;
        try
        {
            session.Observation?.Cancel();
            await session.Observing;
            if (closed || stopping || session.Retired || session.Publication != publication) return;
            session.ResetBinding(reopen: true);
            await OpenAsync(session);
        }
        finally
        {
            if (session.Publication == publication)
            {
                session.Resuming = false; session.Commanding = false; session.Notify();
            }
        }
    }

    private async Task OpenAsync(FilmSearchSession session)
    {
        await Task.Yield();
        try
        {
            if (!CanPublish(session)) return;
            await sources.ReadSearchSourcesAsync(session.Execution.Token);
            if (!CanPublish(session)) return;
            var connection = session.Connection = await capture(session.Execution.Token);
            if (!CanPublish(session)) return;
            connection.Revoked.ThrowIfCancellationRequested();
            session.Client = new(connection.Client);
            var snapshot = await session.Client.CallAsync("search.open",
                new { query = session.Query }, connection.Revoked);
            await ApplySnapshotAsync(session, snapshot, opening: true);
            if (CanPublish(session)) Observe(session);
        }
        catch (Exception error) { if (!session.Retired && !closed) session.Fail(error); }
    }

    public Task LoadMoreAsync()
    {
        dispatcher.VerifyAccess();
        if (closed || stopping || Current is not { CanLoadMore: true } session) return Task.CompletedTask;
        session.Commanding = true; session.Notify();
        return session.Work = CommandAsync(session, "more");
    }

    public Task StopAsync()
    {
        dispatcher.VerifyAccess();
        if (Current is not { } session) return Task.CompletedTask;
        if (session.Stopped && !session.Resuming && ReferenceEquals(session.Work, session.Stopping)) return session.Work;
        session.Publication++; session.Resuming = false; session.Commanding = false;
        session.Stopped = true; session.Notify();
        return QueueStop(session);
    }

    private Task QueueStop(FilmSearchSession session)
    {
        if (ReferenceEquals(session.Work, session.Stopping)) return session.Work;
        var work = StopAsync(session, session.Work);
        session.Stopping = work;
        return session.Work = work;
    }

    private async Task StopAsync(FilmSearchSession session, Task prior)
    {
        await prior;
        if (session.Id.Length == 0)
        {
            if (!session.Retired && !closed) session.CancelUnopened();
            return;
        }
        if (session.Connection?.Revoked.IsCancellationRequested != false) return;
        await CommandAsync(session, "stop");
    }

    private async Task ResumeAsync(FilmSearchSession session, Task prior, long publication)
    {
        await Task.Yield();
        await prior;
        if (!CanResume()) return;
        try
        {
            if (session.Id.Length == 0)
            {
                session.ResetForRetry();
                await OpenAsync(session);
                return;
            }
            session.Connection!.Revoked.ThrowIfCancellationRequested();
            var snapshot = await session.Client!.CallAsync("search.snapshot", new { sessionId = session.Id }, session.Connection.Revoked);
            if (!CanResume()) return;
            if (FilmJson.Bool(snapshot, "invalidated")) { Invalidate(session); return; }
            await session.Control.WaitAsync();
            try
            {
                if (!CanResume()) return;
                snapshot = await session.Client.CallAsync("search.command", new { sessionId = session.Id, action = "resume" }, session.Connection.Revoked);
                if (!CanResume()) return;
                session.Stopped = false;
                await ApplySnapshotAsync(session, snapshot, refresh: true);
                if (CanPublish(session) && session.Observing.IsCompleted) Observe(session);
            }
            finally { session.Control.Release(); }
        }
        catch (Exception error) { if (CanResume()) session.Fail(error); }
        finally
        {
            if (session.Publication == publication) { session.Commanding = false; session.Resuming = false; session.Notify(); }
        }
        bool CanResume() => !closed && !stopping && !session.Retired && session.Publication == publication;
    }

    private async Task CommandAsync(FilmSearchSession session, string action)
    {
        long publication = session.Publication;
        await Task.Yield();
        await session.Control.WaitAsync();
        try
        {
            if (action != "stop" && !CanPublish(session)) return;
            var snapshot = await session.Client!.CallAsync("search.command", new { sessionId = session.Id, action },
                session.Connection!.Revoked);
            if (action == "stop")
            {
                var prepared = await Task.Run(() => new FilmSearchPreparedSnapshot(snapshot, null, readDisplay: false));
                if (!session.Retired && !closed) session.ApplyStopped(prepared);
            }
            else await ApplySnapshotAsync(session, snapshot);
        }
        catch (Exception error) { if (!session.Retired && !closed) session.Fail(error); }
        finally
        {
            session.Control.Release();
            if (session.Publication == publication) { session.Commanding = false; session.Notify(); }
        }
    }

    private void Observe(FilmSearchSession session)
    {
        session.Observation?.Dispose();
        session.Observation = CancellationTokenSource.CreateLinkedTokenSource(session.Connection!.Revoked);
        session.Observing = ObserveAsync(session, session.Observation.Token);
    }

    private async Task ObserveAsync(FilmSearchSession session, CancellationToken token)
    {
        int reconnectAttempt = 0;
        while (!token.IsCancellationRequested && !session.Retired && !session.Stopped && !closed && !stopping)
        {
            var activeConnection = session.Connection;
            var client = session.Client;
            if (activeConnection is null || client is null || session.Id.Length == 0) return;
            long revisionBefore = session.Revision;
            try
            {
                await foreach (var snapshot in activeConnection.Client.EventsAsync("film", Guid.Parse(session.Id),
                    Math.Max(0, session.Revision), token))
                {
                    token.ThrowIfCancellationRequested();
                    await ApplySnapshotAsync(session, snapshot);
                }
                if (token.IsCancellationRequested) return;
                throw new FilmPlaybackException("DISCONNECTED");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (activeConnection.Revoked.IsCancellationRequested)
                    ObservationFailed(session, new FilmPlaybackException("DISCONNECTED"));
                return;
            }
            catch (Exception error)
            {
                if (token.IsCancellationRequested || session.Retired || session.Stopped || closed || stopping) return;
                if (activeConnection.Revoked.IsCancellationRequested)
                {
                    ObservationFailed(session, new FilmPlaybackException("DISCONNECTED"));
                    return;
                }
                if (session.Revision > revisionBefore) reconnectAttempt = 0;
                if (reconnectAttempt >= MaxObserveReconnectAttempts)
                {
                    ObservationFailed(session, error);
                    return;
                }
                bool caughtUp = false;
                while (!caughtUp)
                {
                    int delayMs = 500 << reconnectAttempt;
                    reconnectAttempt++;
                    DebugLog.Info(DebugCategories.Search, "film.search", "observe.reconnect",
                        new { sessionId = session.Id, attempt = reconnectAttempt, delayMs });
                    try
                    {
                        await Task.Delay(delayMs, token);
                        if (token.IsCancellationRequested || session.Retired || session.Stopped || closed || stopping) return;
                        var latest = await client.CallAsync("search.snapshot", new { sessionId = session.Id }, token);
                        if (token.IsCancellationRequested || session.Retired || session.Stopped || closed || stopping) return;
                        await ApplySnapshotAsync(session, latest, refresh: true);
                        caughtUp = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (activeConnection.Revoked.IsCancellationRequested)
                            ObservationFailed(session, new FilmPlaybackException("DISCONNECTED"));
                        return;
                    }
                    catch (Exception reconnectError)
                    {
                        if (token.IsCancellationRequested || session.Retired || session.Stopped || closed || stopping) return;
                        if (activeConnection.Revoked.IsCancellationRequested)
                        {
                            ObservationFailed(session, new FilmPlaybackException("DISCONNECTED"));
                            return;
                        }
                        if (reconnectAttempt >= MaxObserveReconnectAttempts)
                        {
                            DebugLog.Warn(DebugCategories.Search, "film.search", "observe.reconnect.failed",
                                new { sessionId = session.Id, attempt = reconnectAttempt }, error: reconnectError);
                            ObservationFailed(session, reconnectError);
                            return;
                        }
                    }
                }
            }
        }
    }

    private void ObservationFailed(FilmSearchSession session, Exception error)
    {
        if (session.Retired || closed || stopping) return;
        bool requestStop = !ReferenceEquals(session.Work, session.Stopping);
        session.Publication++; session.Resuming = false; session.Commanding = false;
        session.Fail(error);
        if (requestStop) QueueStop(session);
    }

    private bool CanPublish(FilmSearchSession session) => !session.Retired && !session.Stopped && !closed && !stopping;

    private async Task ApplySnapshotAsync(FilmSearchSession session, JsonElement snapshot,
        bool refresh = false, bool opening = false)
    {
        dispatcher.VerifyAccess();
        if (opening)
        {
            var identity = await Task.Run(() => FilmSearchPreparedSnapshot.ReadIdentity(snapshot));
            session.Bind(identity.Id, identity.Query);
            if (!CanPublish(session)) return;
        }
        long publication = session.Publication;
        bool readDisplay = CanPublish(session);
        var previous = session.Display;
        while (true)
        {
            var prepared = await Task.Run(() => new FilmSearchPreparedSnapshot(snapshot, previous, readDisplay));
            dispatcher.VerifyAccess();
            if (!session.Retired && !closed && prepared.Invalidated) { Invalidate(session); return; }
            if (session.Retired || closed || stopping) return;
            session.Connection!.Revoked.ThrowIfCancellationRequested();
            if (readDisplay && session.Publication == publication && CanPublish(session) &&
                (prepared.Revision > session.Revision || refresh && prepared.Revision == session.Revision))
            {
                if (!ReferenceEquals(previous, session.Display))
                {
                    previous = session.Display;
                    continue;
                }
                session.Apply(prepared, refresh);
            }
            return;
        }
    }

    public void InvalidateQuery(string query)
    {
        dispatcher.VerifyAccess();
        if (Current is { } session && session.Query != query) Invalidate();
    }

    public void InvalidateMetadata() { dispatcher.VerifyAccess(); Invalidate(); }

    private void Invalidate()
    {
        if (Current is { } session) Invalidate(session);
    }

    private void Invalidate(FilmSearchSession session)
    {
        if (session.Retired) return;
        session.Publication++; session.Resuming = false; session.Commanding = false;
        session.Retired = true; session.Stopped = true;
        session.Execution.Cancel();
        session.Changed -= SessionChanged;
        session.Observation?.Cancel();
        QueueStop(session);
        if (ReferenceEquals(Current, session)) Current = null;
        _ = ReleaseAsync(session);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task ReleaseAsync(FilmSearchSession session) => session.Releasing ??= ReleaseCoreAsync(session);

    private async Task ReleaseCoreAsync(FilmSearchSession session)
    {
        await session.Work;
        session.Observation?.Cancel();
        await session.Observing;
        try
        {
            if (session.Id.Length > 0 && session.Connection?.Revoked.IsCancellationRequested == false)
                await session.Client!.CallAsync("search.release", new { sessionId = session.Id }, session.Connection.Revoked);
        }
        catch (Exception error) { session.Fail(error); }
        finally
        {
            session.Changed -= SessionChanged;
            session.Observation?.Dispose();
            session.Observation = null;
            session.Execution.Dispose();
            session.Control.Dispose();
            sessions.Remove(session);
        }
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        if (!closed && ReferenceEquals(Current, sender)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task PrepareShutdownAsync()
    {
        dispatcher.VerifyAccess();
        if (preparation is not null) return preparation;
        stopping = true;
        return preparation = DrainAsync();
    }

    private async Task DrainAsync()
    {
        var active = sessions.ToArray();
        foreach (var session in active)
        {
            session.Execution.Cancel();
            if (session.Releasing is not null || ReferenceEquals(session.Work, session.Stopping)) continue;
            session.Publication++; session.Resuming = false; session.Commanding = false;
            session.Stopped = true;
            _ = QueueStop(session);
        }
        await Task.WhenAll(active.Select(session => session.Work));
        foreach (var session in active) session.Observation?.Cancel();
        await Task.WhenAll(active.Select(session => session.Observing));
        await Task.WhenAll(active.Select(session => session.Releasing ?? Task.CompletedTask));
    }

    public ValueTask DisposeAsync()
    {
        dispatcher.VerifyAccess();
        if (disposal is not null) return new(disposal);
        var drained = PrepareShutdownAsync();
        closed = true;
        return new(disposal = CloseAsync(drained));
    }

    private async Task CloseAsync(Task drained)
    {
        await drained;
        foreach (var session in sessions.ToArray()) await ReleaseAsync(session);
    }
}
