using System.Text.Json;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>Displays Node search snapshots and forwards actions; never schedules content requests.</summary>
public sealed class FilmSearchService : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<RuntimeConnection>> capture;
    private readonly IFilmSearchSources sources;
    private readonly Func<string, string> resolveImage;
    private readonly OwnerDispatcher dispatcher = OwnerDispatcher.Current;
    private readonly List<FilmSearchSession> sessions = [];
    private bool stopping, closed;
    private Task? preparation, disposal;
    public FilmSearchSession? Current { get; private set; }
    internal int TrackedSessions => sessions.Count;
    public event EventHandler? Changed;

    public FilmSearchService(Func<CancellationToken, Task<RuntimeConnection>> capture,
        IFilmSearchSources sources, Func<string, string>? resolveImage = null)
    {
        this.capture = capture;
        this.sources = sources;
        this.resolveImage = resolveImage ?? (path => path);
        sources.Changed += SourcesChanged;
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
            if (retained.Stopped && !retained.Resuming)
            {
                retained.Resuming = true;
                retained.Commanding = true;
                retained.Work = retained.Connection?.Revoked.IsCancellationRequested != false
                    ? RebindAsync(retained, retained.Work, ++retained.Publication)
                    : ResumeAsync(retained, retained.Work, ++retained.Publication);
            }
            return retained;
        }
        Invalidate();
        var session = new FilmSearchSession(this, query);
        sessions.Add(session);
        Current = session;
        session.Changed += SessionChanged;
        session.Work = OpenAsync(session);
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    internal Task SuspendRuntimeAsync()
    {
        dispatcher.VerifyAccess();
        if (Current is not { } session) return Task.CompletedTask;
        session.Publication++; session.Resuming = false;
        session.Fail(new FilmPlaybackException("CLOSED"));
        session.Execution.Cancel();
        session.Observation?.Cancel();
        return DrainAsync();

        async Task DrainAsync()
        {
            await session.Work;
            await Task.WhenAll(session.Handoffs.ToArray().Select(work => SettleHandoffAsync(session, work)));
            await session.Observing;
            session.ResetBinding();
        }
    }

    private async Task RebindAsync(FilmSearchSession session, Task prior, long publication)
    {
        await Task.Yield();
        await prior;
        if (closed || stopping || session.Retired || session.Publication != publication) return;
        session.Observation?.Cancel();
        await session.Observing;
        if (closed || stopping || session.Retired || session.Publication != publication) return;
        session.ResetBinding();
        session.Stopped = false;
        await OpenAsync(session);
        if (session.Publication == publication)
        {
            session.Resuming = false; session.Commanding = false; session.Notify();
        }
    }

    private async Task OpenAsync(FilmSearchSession session)
    {
        await Task.Yield();
        try
        {
            if (!CanPublish(session)) return;
            var available = await sources.ReadSearchSourcesAsync(session.Execution.Token);
            if (!CanPublish(session)) return;
            session.SourceSnapshot = available.Select(source => source.Wire).ToArray();
            var connection = session.Connection = await capture(session.Execution.Token);
            if (!CanPublish(session)) return;
            connection.Revoked.ThrowIfCancellationRequested();
            if (!SameSources(session)) { Invalidate(session); return; }
            session.Client = new(connection.Client);
            var snapshot = await session.Client.CallAsync("search.open",
                new { query = session.Query, sources = session.SourceSnapshot }, connection.Revoked);
            // An accepted open still owns an allocated ID when its presentation
            // was stopped or retired while awaiting the receipt.
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
        session.Publication++; session.Resuming = false;
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
                if (session.Error.Length > 0) throw new FilmPlaybackException(session.Error);
                session.Stopped = false;
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
        await Task.Yield();
        await session.Control.WaitAsync();
        try
        {
            if (action != "stop" && !CanPublish(session)) return;
            var snapshot = await session.Client!.CallAsync("search.command", new { sessionId = session.Id, action },
                session.Connection!.Revoked);
            if (action == "stop")
            {
                var prepared = await Task.Run(() => new FilmSearchPreparedSnapshot(snapshot, null, resolveImage, readDisplay: false));
                if (!session.Retired && !closed)
                {
                    session.ApplyStopped(prepared);
                    if (!stopping) session.PublishReference(prepared);
                }
            }
            else await ApplySnapshotAsync(session, snapshot);
        }
        catch (Exception error) { if (!session.Retired && !closed) session.Fail(error); }
        finally { session.Control.Release(); session.Commanding = false; session.Notify(); }
    }
    private void Observe(FilmSearchSession session)
    {
        session.Observation?.Dispose();
        session.Observation = CancellationTokenSource.CreateLinkedTokenSource(session.Connection!.Revoked);
        session.Observing = ObserveAsync(session, session.Observation.Token);
    }
    private async Task ObserveAsync(FilmSearchSession session, CancellationToken token)
    {
        try
        {
            await foreach (var snapshot in session.Connection!.Client.EventsAsync("film", Guid.Parse(session.Id),
                Math.Max(0, session.Revision), token))
            {
                token.ThrowIfCancellationRequested();
                // Await the one preparation/commit before reading again. The existing
                // HTTP mailbox coalesces frames received while this consumer is busy.
                await ApplySnapshotAsync(session, snapshot);
            }
            if (!token.IsCancellationRequested) ObservationFailed(session, new FilmPlaybackException("DISCONNECTED"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (session.Connection!.Revoked.IsCancellationRequested)
                ObservationFailed(session, new FilmPlaybackException("DISCONNECTED"));
        }
        catch (Exception error) { ObservationFailed(session, error); }
    }

    private void ObservationFailed(FilmSearchSession session, Exception error)
    {
        if (session.Retired || closed || stopping) return;
        bool requestStop = !ReferenceEquals(session.Work, session.Stopping);
        session.Publication++; session.Resuming = false;
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
            if (!session.Retired && !SameSources(session)) { Invalidate(session); return; }
            if (!CanPublish(session)) return;
        }
        long publication = session.Publication;
        bool readDisplay = CanPublish(session);
        var previous = session.Display;
        while (true)
        {
            var prepared = await Task.Run(() => new FilmSearchPreparedSnapshot(snapshot, previous, resolveImage, readDisplay));
            dispatcher.VerifyAccess();
            if (!session.Retired && !closed && prepared.Invalidated) { Invalidate(session); return; }
            if (session.Retired || closed || stopping) return;
            session.Connection!.Revoked.ThrowIfCancellationRequested();
            if (readDisplay && session.Publication == publication && CanPublish(session) &&
                (prepared.Revision > session.Revision || refresh && prepared.Revision == session.Revision))
            {
                // A command receipt can race an SSE frame. Its edits must be based
                // on the actual committed predecessor, never on an obsolete collection.
                if (!ReferenceEquals(previous, session.Display))
                {
                    previous = session.Display;
                    continue;
                }
                session.Apply(prepared, refresh);
            }
            // A retained playback selection may learn a newer Node reference while
            // the stopped results page keeps its exact last display snapshot.
            session.PublishReference(prepared);
            return;
        }
    }
    internal Task<FilmPlaybackRequest> HandoffAsync(FilmSearchContext context, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        var work = HandoffOrLocatorAsync(context, token);
        context.Session.Handoffs.Add(work);
        _ = SettleHandoffAsync(context.Session, work);
        return work;
    }
    private async Task<FilmPlaybackRequest> HandoffOrLocatorAsync(FilmSearchContext context, CancellationToken token)
    {
        try { return await HandoffCoreAsync(context, token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !closed && !stopping)
        { return context.PlaybackRequest; }
        catch (FilmPlaybackException error) when (!closed && !stopping &&
            error.Code is "STALE_SESSION" or "STALE_SOURCE" or "NOT_FOUND" or "CLOSED" or "DISCONNECTED")
        { return context.PlaybackRequest; }
        catch (BridgeException error) when (!closed && !stopping &&
            error.Code is "STALE_SESSION" or "CLOSED" or "DISCONNECTED")
        { return context.PlaybackRequest; }
    }
    private static async Task SettleHandoffAsync(FilmSearchSession session, Task work)
    {
        try { await work; }
        catch (Exception) { /* The caller owns this operation's error. */ }
        finally { session.Handoffs.Remove(work); }
    }
    private async Task<FilmPlaybackRequest> HandoffCoreAsync(FilmSearchContext context, CancellationToken token)
    {
        var session = context.Session;
        if (closed || stopping) throw new FilmPlaybackException("CLOSED");
        if (session.Retired || session.Connection?.Revoked.IsCancellationRequested != false)
            return context.PlaybackRequest;
        await session.Work.WaitAsync(token);
        var available = sources.CurrentSearchSources;
        if (closed || stopping) throw new FilmPlaybackException("CLOSED");
        if (session.Retired || session.Connection?.Revoked.IsCancellationRequested != false || !SameSources(session))
            return context.PlaybackRequest;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, session.Connection!.Revoked);
        var value = await session.Client!.CallAsync("search.handoff", new
        {
            sessionId = session.Id, selectionId = context.DisplayKey, sources = available.Select(source => source.Wire).ToArray()
        }, wait.Token);
        wait.Token.ThrowIfCancellationRequested();
        if (session.Retired || closed || stopping) throw new FilmPlaybackException("STALE_SESSION");
        if (FilmJson.Text(value, "selectionId") != context.PlaybackRequest.Target.Id ||
            FilmJson.Text(value.GetProperty("searchReference"), "sessionId") != session.Id ||
            FilmJson.Text(value.GetProperty("searchReference"), "selectionId") != context.DisplayKey)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return FilmSearchPlayback.Read(value, session.Connection);
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
        session.Publication++; session.Resuming = false;
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
        await Task.WhenAll(session.Handoffs.ToArray().Select(work => SettleHandoffAsync(session, work)));
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
            sessions.Remove(session);
        }
    }
    private bool SameSources(FilmSearchSession session)
    {
        var available = sources.CurrentSearchSources;
        return session.SourceSnapshot.Count == available.Count &&
            session.SourceSnapshot.Zip(available).All(pair => FilmJson.Equal(pair.First, pair.Second.Wire));
    }
    private void SourcesChanged(object? sender, EventArgs e)
    {
        if (dispatcher.IsClosing) return;
        if (dispatcher.HasThreadAccess) Apply(); else _ = dispatcher.EnqueueAsync(Apply);
        void Apply()
        {
            if (!closed && Current is { Id.Length: > 0 } session && !SameSources(session)) Invalidate(session);
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
            if (session.Releasing is not null || ReferenceEquals(session.Work, session.Stopping)) continue;
            session.Publication++; session.Resuming = false;
            session.Stopped = true;
            _ = QueueStop(session);
        }
        await Task.WhenAll(active.Select(session => session.Work));
        await Task.WhenAll(active.SelectMany(session => session.Handoffs.ToArray().Select(work => SettleHandoffAsync(session, work))));
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
        sources.Changed -= SourcesChanged;
        return new(disposal = CloseAsync(drained));
    }
    private async Task CloseAsync(Task drained)
    {
        await drained;
        foreach (var session in sessions.ToArray()) await ReleaseAsync(session);
    }
}
