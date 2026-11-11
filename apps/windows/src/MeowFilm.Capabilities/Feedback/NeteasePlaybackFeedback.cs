using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

internal interface IMusicFeedbackPendingProtocol
{
    string? LastError { get; }
    Task InitializeAsync(CancellationToken token = default);
    Task QualifyAccountAsync(MusicPlatformSession account, CancellationToken token);
    void AuthorizationChanged(bool accountChanged);
    Task PrepareShutdownAsync();
    Task FlushLocalAsync();
    void Resume();
    Task DrainAsync(CancellationToken token = default);
}

/// <summary>Attempt-local duplicate protection around durable local admission, not remote ACK.</summary>
internal sealed class NeteasePlaybackFeedback(NeteasePlaybackOutbox outbox, MusicFeedbackSettingsOwner settings,
    IMusicPlatformSessionSource accounts) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HashSet<Session> sessions = [];
    private Task? disposal;
    private bool closed;
    private sealed class Attempt(string fingerprint, NeteasePlaybackFeedback owner)
    {
        internal readonly string Fingerprint = fingerprint;
        internal NeteasePlaybackFeedback? Owner = owner;
        internal string? Terminal;
        internal Task<IMusicFeedbackSession>? Start;
    }
    private sealed class Session(NeteasePlaybackFeedback owner, MusicPlatformSession account,
        MusicFeedbackStart input, Attempt attempt) : IMusicFeedbackSession
    {
        internal readonly MusicPlatformSession Account = account;
        internal readonly MusicFeedbackStart Input = input;
        internal readonly Attempt Attempt = attempt;
        // One logical listen can be re-admitted after an account/consent change.
        // Its retired platform attempt must not revoke the new durable pair.
        internal readonly Guid OutboxId = Guid.NewGuid();
        internal CancellationToken Token;
        internal CancellationTokenRegistration Revocation;
        internal MusicFeedbackEnd? End;
        internal Task? Finish, Disposal;
        internal bool Closed;
        public MusicFeedbackRecording Recording => Input.Recording;
        public Task FinishAsync(MusicFeedbackEnd end, CancellationToken token = default) => owner.FinishAsync(this, end, token);
        public ValueTask DisposeAsync() => new(owner.ReleaseAsync(this));
    }

    internal Task<IMusicFeedbackSession> StartAsync(MusicPlatformSession account, MusicFeedbackStart input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FeedbackProtocolRules.Start(account, input);
        _ = FeedbackProtocolRules.Cookies(account);
        string fingerprint = FeedbackProtocolRules.Fingerprint(account, input);
        lock (gate)
        lock (input.Attempt.Gate)
        {
            FeedbackProtocolRules.Require(!closed, "CLOSED");
            FeedbackProtocolRules.Require(accounts.IsCurrent(account), "SESSION_CHANGED");
            if (input.Attempt.State is not null)
            {
                FeedbackProtocolRules.Require(input.Attempt.State is Attempt, "INSTANCE_MISMATCH");
                var previous = (Attempt)input.Attempt.State;
                FeedbackProtocolRules.Require(previous.Fingerprint == fingerprint, "INSTANCE_MISMATCH");
                FeedbackProtocolRules.Require(previous.Terminal is null, previous.Terminal ?? "START_UNCONFIRMED");
                FeedbackProtocolRules.Require(previous.Owner == this, "INSTANCE_MISMATCH");
                return DuplicateAsync(previous, token);
            }
            var attempt = new Attempt(fingerprint, this);
            var session = new Session(this, account, input, attempt) { Token = token };
            input.Attempt.State = attempt; sessions.Add(session);
            session.Revocation = token.Register(() => outbox.Revoke(session.OutboxId));
            attempt.Start = AdmitAsync(session);
            return attempt.Start;
        }
    }

    private async Task<IMusicFeedbackSession> DuplicateAsync(Attempt attempt, CancellationToken token)
    {
        var session = (Session)await attempt.Start!.WaitAsync(token).ConfigureAwait(false);
        Guard(session, token);
        return session;
    }

    private async Task<IMusicFeedbackSession> AdmitAsync(Session session)
    {
        await Task.Yield(); // Publish the admission task before synchronous failure/re-entry.
        try
        {
            Guard(session, session.Token);
            var value = NeteasePlaybackTransport.Start(session.Account, session.Input, settings.WyConsentRevision)
                with { PlaybackId = session.OutboxId };
            await outbox.AdmitAsync(session.Account, value, session.Token).ConfigureAwait(false);
            Guard(session, session.Token);
            return session;
        }
        catch
        {
            lock (gate)
            {
                session.Attempt.Terminal = "START_UNCONFIRMED";
                session.Attempt.Start = null; session.Attempt.Owner = null;
            }
            await session.Revocation.DisposeAsync().ConfigureAwait(false);
            lock (gate) sessions.Remove(session);
            throw;
        }
    }

    private Task FinishAsync(Session session, MusicFeedbackEnd end, CancellationToken token)
    {
        lock (gate)
        {
            if (session.Finish is not null)
            {
                FeedbackProtocolRules.Require(session.End == end, "INSTANCE_MISMATCH");
                return session.Finish.WaitAsync(token);
            }
            Guard(session, token);
            session.End = end; session.Attempt.Terminal = "INSTANCE_ENDED";
            return session.Finish = FinishCoreAsync(session, end, token);
        }
    }
    private async Task FinishCoreAsync(Session session, MusicFeedbackEnd end, CancellationToken token)
    {
        FeedbackProtocolRules.End(session.Input, end, TimeSpan.Zero);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token, token);
        await outbox.EndAsync(session.Account, session.OutboxId,
            new(Guid.NewGuid(), end.EndedAt.ToUnixTimeMilliseconds(), false, end.MediaListened, end.Listened,
                end.Reason, end.Rate), linked.Token).ConfigureAwait(false);
    }
    private void Guard(Session session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); session.Token.ThrowIfCancellationRequested();
        lock (gate) FeedbackProtocolRules.Require(!closed && !session.Closed, "CLOSED");
        FeedbackProtocolRules.Require((settings.IsEnabled("wy") || settings.PreservesWyConsent) &&
            accounts.IsCurrent(session.Account), "SESSION_CHANGED");
    }
    private Task ReleaseAsync(Session session)
    {
        lock (gate)
        {
            if (session.Disposal is not null) return session.Disposal;
            session.Closed = true; session.Attempt.Terminal ??= "INSTANCE_ENDED";
            return session.Disposal = ReleaseCoreAsync(session);
        }
    }
    private async Task ReleaseCoreAsync(Session session)
    {
        await Task.Yield();
        try
        {
            if (session.Attempt.Start is { } start) await start.ConfigureAwait(false);
        }
        catch { /* Original admission owns the error. */ }
        if (session.Finish is { } finish)
            try { await finish.ConfigureAwait(false); } catch { /* Original finish owns the error. */ }
        await session.Revocation.DisposeAsync().ConfigureAwait(false);
        // Finishing/disposal is not revocation. Pending facts survive normal exit.
        outbox.Release(session.OutboxId);
        lock (gate)
        {
            session.Attempt.Start = null; session.Attempt.Owner = null;
            sessions.Remove(session);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            return new(disposal = DisposeCoreAsync(sessions.ToArray()));
        }
    }
    private async Task DisposeCoreAsync(Session[] active)
    {
        await Task.Yield();
        var suspension = outbox.PrepareShutdownAsync();
        foreach (var session in active) await ReleaseAsync(session).ConfigureAwait(false);
        try { await suspension.ConfigureAwait(false); }
        finally { await outbox.DisposeAsync().ConfigureAwait(false); }
    }
}
