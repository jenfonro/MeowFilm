// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>
/// Borrowed native accounts/HTTP; no settings, media clock, automatic retry or durable work.
/// Each platform/instance is reserved before I/O. Unsupported exact protocols fail closed.
/// </summary>
internal sealed class NativeMusicFeedbackProtocol(IMusicPlatformSessionSource accounts, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients, TimeProvider? clock = null) : IMusicFeedbackProtocol
{
    private readonly IMusicPlatformSessionSource accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    private readonly MusicPlatformHttp http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly MusicPlatformClientContextOwner clients = clients ?? throw new ArgumentNullException(nameof(clients));
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<(string Platform, string Id), Entry> entries = [];
    private readonly Dictionary<string, Queue<long>> attempts = [];
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
        internal MusicWyClientContext? Client;
        internal MusicWyClientContext WyClient => Client!;
        internal Task? Disposal;
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
        start = FeedbackProtocolRules.Start(account, start);
        string fingerprint = FeedbackProtocolRules.Fingerprint(account, start);
        Session session;
        lock (gate)
        {
            GuardAccount(account);
            var key = (account.Platform, start.InstanceId);
            if (entries.TryGetValue(key, out var old))
            {
                FeedbackProtocolRules.Require(old.Fingerprint == fingerprint, "INSTANCE_MISMATCH");
                FeedbackProtocolRules.Require(old.Terminal is null, old.Terminal ?? "START_UNCONFIRMED");
                FeedbackProtocolRules.Require(old.Active is { Closed: false }, "CLOSED");
                old.Active!.Token.ThrowIfCancellationRequested();
                return ObserveDuplicate(old.Start!, old.Active, cancellationToken);
            }
            // Compact terminal entries are not expired/evicted: eviction could replay an ambiguous write.
            FeedbackProtocolRules.Require(entries.Count < 8192, "INSTANCE_LIMIT");
            FeedbackProtocolRules.Require(sessions.Count < 128 && pending.Count < 8, "RATE_LIMITED");
            Reserve(account.Platform);
            var entry = new Entry(fingerprint);
            session = new(this, account, start, entry, cancellationToken);
            entry.Active = session; entry.Start = session.Started.Task;
            entries.Add(key, entry); sessions.Add(session); pending.Add(session.Started.Task);
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
    private void Reserve(string platform)
    {
        if (!attempts.TryGetValue(platform, out var times)) attempts[platform] = times = new();
        long now = clock.GetTimestamp();
        while (times.TryPeek(out var at) && clock.GetElapsedTime(at, now) >= TimeSpan.FromMinutes(1)) times.Dequeue();
        FeedbackProtocolRules.Require(times.Count < 40, "RATE_LIMITED");
        times.Enqueue(now);
    }
    private async Task Send(Session session, HttpRequestMessage request, CancellationToken token,
        Action<JsonObject> validate)
    {
        void GuardClient()
        {
            Guard(session, token);
            FeedbackProtocolRules.Require(session.Client is not null && clients.IsCurrent(session.Client),
                clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
        }
        try { GuardClient(); }
        catch { request.Dispose(); throw; }
        try
        {
            // The transport owner must drain the physical request even after cancellation.
            var result = await http.SendFeedbackJsonAsync(session.Account.Platform, request, token).ConfigureAwait(false);
            GuardClient();
            validate(result.Body);
            session.Client = await clients.AcceptWyAsync(session.WyClient, session.Account.Cookie,
                result.SetCookieHeaders, GuardClient, token).ConfigureAwait(false);
            GuardClient();
        }
        catch (Exception error)
        {
            Guard(session, token);
            if (error is MusicFeedbackException) throw;
            if (error is OperationCanceledException) throw;
            throw new MusicFeedbackException(FeedbackProtocolRules.SafeError(error));
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
                case "wy":
                    session.Client = await clients.AcquireWyAsync(session.Token).ConfigureAwait(false);
                    await Send(session, FeedbackProtocolRequests.WyIdentity(session.Account, session.WyClient), session.Token,
                        body => FeedbackProtocolRequests.VerifyWyIdentity(body, session.Account)).ConfigureAwait(false);
                    await Send(session, FeedbackProtocolRequests.WyRecording(session.Account, session.Input.Recording, session.WyClient), session.Token,
                        body => FeedbackProtocolRequests.VerifyWyRecording(body, session.Input)).ConfigureAwait(false);
                    await Send(session, FeedbackProtocolRequests.WyReport(session.Account, session.Input, null, session.WyClient), session.Token,
                        FeedbackProtocolRules.WySuccess).ConfigureAwait(false);
                    break;
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
        if (session.Qq is not null)
        {
            try { await session.Qq.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = Sanitize(error); }
            session.Qq = null;
        }
        lock (gate)
        {
            Retire(session, "START_UNCONFIRMED");
            session.Closed = true;
            session.Stop.Dispose();
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
            Guard(session, token);
            session.Qq?.Guard(token);
            FeedbackProtocolRules.Require(session.Accepted, "START_UNCONFIRMED");
            if (session.Finished is not null)
            {
                FeedbackProtocolRules.Require(session.End == end, "INSTANCE_MISMATCH");
                return ObserveFinish(session.Finished.Task, session, token);
            }
            FeedbackProtocolRules.Require(pending.Count < 8, "RATE_LIMITED");
            Reserve(session.Account.Platform);
            session.End = end;
            session.Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(session.Finished.Task);
            session.Entry.Terminal = "INSTANCE_ENDED";
        }
        _ = RunFinish(session, end, token);
        return session.Finished.Task;
    }
    private async Task ObserveFinish(Task task, Session session, CancellationToken token)
    {
        await task.WaitAsync(token).ConfigureAwait(false);
        Guard(session, token);
        session.Qq?.Guard(token);
    }
    private async Task RunFinish(Session session, MusicFeedbackEnd end, CancellationToken external)
    {
        Exception? failure = null;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, external);
        try
        {
            Guard(session, linked.Token);
            FeedbackProtocolRules.End(session.Input, end, clock.GetElapsedTime(session.At));
            if (session.Qq is { } qq) await qq.Finish(end, linked.Token).ConfigureAwait(false);
            else
            {
                // Revalidate live login, not merely the saved account generation, before the final write.
                session.Client = await clients.AcquireWyAsync(linked.Token).ConfigureAwait(false);
                await Send(session, FeedbackProtocolRequests.WyIdentity(session.Account, session.WyClient), linked.Token,
                    body => FeedbackProtocolRequests.VerifyWyIdentity(body, session.Account)).ConfigureAwait(false);
                await Send(session, FeedbackProtocolRequests.WyReport(session.Account, session.Input, end, session.WyClient), linked.Token,
                    FeedbackProtocolRules.WySuccess).ConfigureAwait(false);
            }
            Guard(session, linked.Token);
            session.Qq?.Guard(linked.Token);
        }
        catch (Exception error) { failure = Sanitize(error); }
        lock (gate)
        {
            if (failure is null)
                try { Guard(session, linked.Token); session.Qq?.Guard(linked.Token); }
                catch (Exception error) { failure = Sanitize(error); }
            linked.Dispose();
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
        session.Entry.Terminal = reason;
        session.Entry.Active = null;
        session.Entry.Start = null;
        // The caller owns a successful session until Dispose; retaining it here allows owner-wide drain.
        if (!session.Accepted) sessions.Remove(session);
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
            session.Entry.Terminal ??= "INSTANCE_ENDED";
            work = session.Finished is null ? [session.Started.Task] : [session.Started.Task, session.Finished.Task];
        }
        _ = Drain();
        return done.Task;
        async Task Drain()
        {
            Cancel(session.Stop);
            try { await Task.WhenAll(work).ConfigureAwait(false); } catch { /* Results stay on their original tasks. */ }
            Exception? releaseFailure = null;
            if (session.Qq is not null)
                try { await session.Qq.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { releaseFailure = Sanitize(error); }
            lock (gate)
            {
                Retire(session, "INSTANCE_ENDED");
                sessions.Remove(session);
                session.Stop.Dispose();
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
            try { await Task.WhenAll(work).ConfigureAwait(false); } catch { /* Drain faulted/cancelled operations too. */ }
            Exception? releaseFailure = null;
            try { await Task.WhenAll(active.Select(DisposeSession)).ConfigureAwait(false); }
            catch (Exception error) { releaseFailure = Sanitize(error); }
            lock (gate)
            {
                entries.Clear(); attempts.Clear(); lifetime.Dispose();
                if (releaseFailure is null) done.TrySetResult();
                else done.TrySetException(releaseFailure);
            }
        }
    }
}
