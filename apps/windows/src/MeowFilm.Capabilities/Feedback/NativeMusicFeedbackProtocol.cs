// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>
/// Native dispatch: WY owns durable local admission and file receipts; QQ/KG
/// retain their independent protocols. All ports and the document store are borrowed.
/// </summary>
internal sealed class NativeMusicFeedbackProtocol(IMusicPlatformSessionSource accounts, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients, TimeProvider? clock = null,
    IDocumentStore? store = null, MusicFeedbackSettingsOwner? settings = null) : IMusicFeedbackProtocol, IMusicFeedbackPendingProtocol
{
    private readonly IMusicPlatformSessionSource accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly MusicPlatformHttp http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly MusicFeedbackSettingsOwner? settings = settings;
    private readonly NeteasePlaybackOutbox? wyOutbox = store is not null && settings is not null
        ? new(store, settings, accounts, new(accounts, http, clients), clock ?? TimeProvider.System) : null;
    private NeteasePlaybackFeedback? wy;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> pending = [];
    private readonly HashSet<Session> sessions = [];
    private Task? disposal;
    private bool closed;
    private sealed class Entry(string fingerprint)
    {
        internal readonly string Fingerprint = fingerprint;
        internal Task<IMusicFeedbackSession>? Start;
        internal Session? Active;
        internal string? Terminal;
    }
    private sealed class Session : IMusicFeedbackSession
    {
        internal readonly NativeMusicFeedbackProtocol Owner;
        internal readonly MusicPlatformSession Account;
        internal readonly MusicFeedbackStart Input;
        internal readonly Entry Entry;
        internal readonly long At;
        internal readonly CancellationTokenSource Stop;
        internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource<IMusicFeedbackSession> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Finished;
        internal MusicFeedbackEnd? End;
        internal NativeMusicFeedbackQq? Qq;
        internal Task? Disposal;
        internal Task? Release;
        internal bool Closed, Accepted;
        internal Session(NativeMusicFeedbackProtocol owner, MusicPlatformSession account, MusicFeedbackStart input,
            Entry entry, CancellationToken token)
        {
            Owner = owner; Account = account; Input = input; Entry = entry;
            At = owner.clock.GetTimestamp();
            Stop = CancellationTokenSource.CreateLinkedTokenSource(owner.lifetime.Token, token);
            Token = Stop.Token;
        }
        public MusicFeedbackRecording Recording => Input.Recording;
        public Task FinishAsync(MusicFeedbackEnd end, CancellationToken cancellationToken = default) =>
            Owner.FinishAsync(this, end, cancellationToken);
        public ValueTask DisposeAsync() => new(Owner.DisposeSession(this));
        public override string ToString() => "NativeMusicFeedbackSession (transient)";
    }
    public Task<IMusicFeedbackSession> StartAsync(MusicPlatformSession account, MusicFeedbackStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        if (account.Platform == "wy")
        {
            lock (gate)
            {
                GuardAccount(account);
                FeedbackProtocolRules.Require(wyOutbox is not null && settings is not null, "FEEDBACK_STORAGE_REQUIRED");
                wy ??= new(wyOutbox, settings, accounts);
                return wy.StartAsync(account, start, cancellationToken);
            }
        }
        start = FeedbackProtocolRules.Start(account, start);
        string fingerprint = FeedbackProtocolRules.Fingerprint(account, start);
        Session session;
        lock (gate)
        lock (start.Attempt.Gate)
        {
            GuardAccount(account);
            FeedbackProtocolRules.Require(start.Attempt.State is null or Entry, "INSTANCE_MISMATCH");
            if (start.Attempt.State is Entry old)
            {
                FeedbackProtocolRules.Require(old.Fingerprint == fingerprint, "INSTANCE_MISMATCH");
                FeedbackProtocolRules.Require(old.Terminal is null, old.Terminal ?? "START_UNCONFIRMED");
                FeedbackProtocolRules.Require(old.Active is { Closed: false }, "CLOSED");
                FeedbackProtocolRules.Require(ReferenceEquals(old.Active!.Owner, this), "INSTANCE_MISMATCH");
                old.Active!.Token.ThrowIfCancellationRequested();
                return ObserveDuplicate(old.Start!, old.Active, cancellationToken);
            }
            var entry = new Entry(fingerprint);
            session = new(this, account, start, entry, cancellationToken);
            entry.Active = session; entry.Start = session.Started.Task;
            start.Attempt.State = entry; sessions.Add(session); pending.Add(session.Started.Task);
        }
        _ = RunStart(session);
        return session.Started.Task;
    }
    private async Task<IMusicFeedbackSession> ObserveDuplicate(Task<IMusicFeedbackSession> task, Session session,
        CancellationToken token)
    {
        var result = await task.WaitAsync(token).ConfigureAwait(false);
        Guard(session, token);
        session.Qq?.Guard(token);
        return result;
    }
    private void GuardAccount(MusicPlatformSession account)
    {
        FeedbackProtocolRules.Require(!closed, "CLOSED");
        FeedbackProtocolRules.Require(accounts.IsCurrent(account), "SESSION_CHANGED");
    }
    private void Guard(Session session, CancellationToken token)
    {
        lock (gate)
        {
            GuardAccount(session.Account);
            FeedbackProtocolRules.Require(!session.Closed, "CLOSED");
            session.Token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
        }
    }
    private async Task RunStart(Session session)
    {
        Exception? failure = null;
        try
        {
            Guard(session, session.Token);
            var cookies = FeedbackProtocolRules.Cookies(session.Account);
            switch (session.Account.Platform)
            {
                case "tx":
                    session.Qq = await NativeMusicFeedbackQq.Start(accounts, session.Account, session.Input, http,
                        clock, token => Guard(session, token), session.Token).ConfigureAwait(false);
                    break;
                case "kg":
                    FeedbackProtocolRules.Require(cookies.GetValueOrDefault("KUGOU_API_MID", "").Length > 0 &&
                        cookies.GetValueOrDefault("KUGOU_API_GUID", "").Length > 0 &&
                        cookies.GetValueOrDefault("KUGOU_API_DEV", "").Length > 0, "DEVICE_CONTEXT_REQUIRED");
                    // Only http://d.kugou.com is evidenced by the fixed CSCC source.
                    throw new MusicFeedbackException("UNVERIFIED_TLS_PROTOCOL");
                default: throw new MusicFeedbackException("UNSUPPORTED");
            }
            Guard(session, session.Token);
            session.Qq?.Guard(session.Token);
        }
        catch (Exception error) { failure = Sanitize(error); }
        lock (gate)
        {
            if (failure is null)
                try { Guard(session, session.Token); session.Qq?.Guard(session.Token); }
                catch (Exception error) { failure = Sanitize(error); }
            if (failure is null)
            {
                pending.Remove(session.Started.Task);
                session.Accepted = true; session.Started.TrySetResult(session);
                return;
            }
        }
        // Failed registrations still own any returned device lease until it has settled.
        try { await ReleaseResources(session).ConfigureAwait(false); }
        catch (Exception error) { failure = Sanitize(error); }
        lock (gate)
        {
            Retire(session, "START_UNCONFIRMED");
            session.Closed = true;
            pending.Remove(session.Started.Task);
            if (failure is OperationCanceledException cancelled) session.Started.TrySetCanceled(cancelled.CancellationToken);
            else session.Started.TrySetException(failure!);
        }
    }
    private Task FinishAsync(Session session, MusicFeedbackEnd end, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(end);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (session.Finished is not null)
            {
                FeedbackProtocolRules.Require(session.End == end, "INSTANCE_MISMATCH");
                return session.Finished.Task.WaitAsync(token);
            }
            Guard(session, token);
            session.Qq?.Guard(token);
            FeedbackProtocolRules.Require(session.Accepted, "START_UNCONFIRMED");
            session.End = end;
            session.Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(session.Finished.Task);
            lock (session.Input.Attempt.Gate) session.Entry.Terminal = "INSTANCE_ENDED";
        }
        _ = RunFinish(session, end, token);
        return session.Finished.Task;
    }
    private async Task RunFinish(Session session, MusicFeedbackEnd end, CancellationToken external)
    {
        Exception? failure = null;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, external);
        try
        {
            Guard(session, linked.Token);
            FeedbackProtocolRules.End(session.Input, end, clock.GetElapsedTime(session.At));
            FeedbackProtocolRules.Require(session.Qq is not null, "UNSUPPORTED");
            await session.Qq.Finish(end, linked.Token).ConfigureAwait(false);
            Guard(session, linked.Token);
            session.Qq?.Guard(linked.Token);
        }
        catch (Exception error) { failure = Sanitize(error); }
        lock (gate)
        {
            if (failure is null)
                try { Guard(session, linked.Token); session.Qq?.Guard(linked.Token); }
                catch (Exception error) { failure = Sanitize(error); }
        }
        linked.Dispose();
        try { await ReleaseResources(session).ConfigureAwait(false); }
        catch (Exception error) { failure = Sanitize(error); }
        lock (gate)
        {
            session.Closed = true;
            Retire(session, "INSTANCE_ENDED");
            pending.Remove(session.Finished!.Task);
            if (failure is OperationCanceledException cancelled) session.Finished.TrySetCanceled(cancelled.CancellationToken);
            else if (failure is not null) session.Finished.TrySetException(failure);
            else session.Finished.TrySetResult();
        }
    }
    private static Exception Sanitize(Exception error) => error is MusicFeedbackException or OperationCanceledException
        ? error : new MusicFeedbackException(FeedbackProtocolRules.SafeError(error));
    private void Retire(Session session, string reason)
    {
        lock (session.Input.Attempt.Gate)
        {
            session.Entry.Terminal = reason;
            session.Entry.Active = null;
            session.Entry.Start = null;
        }
        sessions.Remove(session);
    }
    private Task ReleaseResources(Session session)
    {
        lock (gate) return session.Release ??= Release();
        async Task Release()
        {
            await Task.Yield();
            try { if (session.Qq is not null) await session.Qq.DisposeAsync().ConfigureAwait(false); }
            finally { session.Qq = null; session.Stop.Dispose(); }
        }
    }
    private Task DisposeSession(Session session)
    {
        TaskCompletionSource done;
        Task[] work;
        lock (gate)
        {
            if (session.Disposal is not null) return session.Disposal;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Disposal = done.Task; session.Closed = true;
            lock (session.Input.Attempt.Gate) session.Entry.Terminal ??= "INSTANCE_ENDED";
            work = session.Finished is null ? [session.Started.Task] : [session.Started.Task, session.Finished.Task];
        }
        _ = Drain();
        return done.Task;
        async Task Drain()
        {
            Cancel(session.Stop);
            try { await Task.WhenAll(work).ConfigureAwait(false); } catch { /* Results stay on their original tasks. */ }
            Exception? releaseFailure = null;
            try { await ReleaseResources(session).ConfigureAwait(false); }
            catch (Exception error) { releaseFailure = Sanitize(error); }
            lock (gate)
            {
                Retire(session, "INSTANCE_ENDED");
                if (releaseFailure is null) done.TrySetResult();
                else done.TrySetException(releaseFailure);
            }
        }
    }
    private static void Cancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (AggregateException) { /* A callback cannot skip physical drain. */ }
        catch (ObjectDisposedException) { /* A failed Start already released its source. */ }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource done;
        Task[] work;
        Session[] active;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true; done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = done.Task; work = pending.ToArray(); active = sessions.ToArray();
        }
        _ = Drain();
        return new(done.Task);
        async Task Drain()
        {
            Cancel(lifetime);
            var wyClose = wy is not null ? wy.DisposeAsync().AsTask() :
                wyOutbox is not null ? wyOutbox.DisposeAsync().AsTask() : Task.CompletedTask;
            try { await Task.WhenAll(work).ConfigureAwait(false); } catch { /* Drain faulted/cancelled operations too. */ }
            Exception? releaseFailure = null;
            try { await Task.WhenAll(active.Select(DisposeSession)).ConfigureAwait(false); }
            catch (Exception error) { releaseFailure = Sanitize(error); }
            try
            {
                await wyClose.ConfigureAwait(false);
            }
            catch (Exception error) { releaseFailure ??= Sanitize(error); }
            lock (gate)
            {
                lifetime.Dispose();
                if (releaseFailure is null) done.TrySetResult();
                else done.TrySetException(releaseFailure);
            }
        }
    }
    public bool RequiresDuration(string platform) => platform != "wy";
    public string? LastError => wyOutbox?.LastError;
    public Task InitializeAsync(CancellationToken token = default) =>
        wyOutbox?.InitializeAsync(token) ?? Task.CompletedTask;
    public Task QualifyAccountAsync(MusicPlatformSession account, CancellationToken token)
    {
        lock (gate) GuardAccount(account);
        return account.Platform == "wy" && wyOutbox is not null
            ? wyOutbox.QualifyAccountAsync(account, token) : Task.CompletedTask;
    }
    public void AuthorizationChanged(bool accountChanged) => wyOutbox?.AuthorizationChanged(accountChanged);
    public Task PrepareShutdownAsync() => wyOutbox?.PrepareShutdownAsync() ?? Task.CompletedTask;
    public Task FlushLocalAsync() => wyOutbox?.FlushLocalAsync() ?? Task.CompletedTask;
    public void Resume() => wyOutbox?.Resume();
    public Task DrainAsync(CancellationToken token = default) => wyOutbox?.DrainAsync(token) ?? Task.CompletedTask;
}
