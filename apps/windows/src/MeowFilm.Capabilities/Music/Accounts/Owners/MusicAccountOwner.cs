// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Debug;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>
/// The sole native writer of music.accounts.v1. The injected store is BORROWED:
/// this owner never disposes it, including unsuccessful preparation for shutdown.
/// Constructing an owner does not read disk, contact a platform or start Node.
/// </summary>
public sealed partial class MusicAccountOwner : IMusicPlatformSessionSource, IAsyncDisposable
{
    public const string StorageOwner = "music.accounts.v1";
    private readonly MusicAccountRepository store;
    private readonly IStorageDatabase cacheDatabase;
    private readonly MusicAccountProtocol protocol;
    private static TimeProvider clock => TimeProvider.System;
    private readonly object gate = new();
    private readonly SemaphoreSlim disk = new(1, 1);
    private readonly Dictionary<string, State> states = AccountRules.Platforms.ToDictionary(p => p, _ => new State());
    private readonly ConditionalWeakTable<MusicPlatformSession, Lease> leases = new();
    private int calls, physical;
    private bool closed;
    private TaskCompletionSource drained = Completed();
    private Task? disposal;

    /// <summary>Synchronous invalidation only; subscriber errors cannot change a committed result.</summary>
    public event Action<string>? SessionChanged;

    internal MusicAccountOwner(IStorageDatabase dataDatabase, IStorageDatabase cacheDatabase, MusicAccountProtocol protocol)
    {
        store = new(dataDatabase ?? throw new ArgumentNullException(nameof(dataDatabase)));
        this.cacheDatabase = cacheDatabase ?? throw new ArgumentNullException(nameof(cacheDatabase));
        this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
    }

    private sealed class State
    {
        internal long Epoch;
        internal Operation? Active;
        internal bool Revoked;
        internal StoredAccount? Verified;
        internal StoredAccount? Current;
        internal ChallengeState? Challenge;
    }

    private sealed class Lease(long epoch) { internal long Epoch { get; } = epoch; }

    private sealed class Operation : IDisposable
    {
        internal Operation(string platform, long epoch, CancellationToken external)
        {
            Platform = platform; Epoch = epoch;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
            Token = Cancellation.Token;
        }
        internal string Platform { get; }
        internal long Epoch { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal CancellationToken Token { get; }
        public void Dispose() => Cancellation.Dispose();
        public override string ToString() => "MusicAccountOperation (redacted)";
    }

    private sealed class ChallengeState(MusicAccountChallenge value, StoredAccount before, long epoch)
    {
        internal MusicAccountChallenge Value { get; } = value;
        internal StoredAccount Before { get; } = before;
        internal long Epoch { get; } = epoch;
        internal bool Consumed;
        internal StoredAccount? Committed;
        public override string ToString() => "MusicAccountChallengeState (redacted)";
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(); return tcs;
    }

    private void Enter(bool isPhysical = false)
    {
        lock (gate)
        {
            if (!isPhysical) AccountRules.Require(!closed, "CLOSED");
            if (calls + physical == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (isPhysical) physical++; else calls++;
        }
    }

    private void Leave(bool isPhysical = false)
    {
        lock (gate)
        {
            if (isPhysical) physical--; else calls--;
            if (calls + physical == 0) drained.TrySetResult();
        }
    }

    private void Notify(string platform)
    {
        ObserveDetailsAccountChange(platform);
        var handlers = SessionChanged;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(platform); } catch { }
    }

    private Task FenceLocked(string platform)
    {
        var state = states[platform];
        state.Epoch++;
        state.Active?.Cancellation.Cancel();
        state.Active = null; state.Current = null; state.Verified = null; state.Challenge = null;
        FenceDetailsLocked(platform);
        return protocol.CancelAsync(platform);
    }

    private Operation Start(string platform, CancellationToken cancellation, bool logout = false, bool poll = false)
    {
        cancellation.ThrowIfCancellationRequested();
        Operation op;
        bool fenced = false;
        try
        {
            lock (gate)
            {
                AccountRules.Require(!closed, "CLOSED");
                var state = states[platform];
                if (!poll)
                {
                    fenced = true;
                    _ = FenceLocked(platform);
                    if (logout) state.Revoked = true;
                }
                else AccountRules.Require(state.Active is null, "BUSY");
                op = new(platform, state.Epoch, cancellation);
                state.Active = op;
            }
        }
        finally { if (fenced) Notify(platform); }
        return op;
    }

    private void Finish(Operation op)
    {
        lock (gate) if (ReferenceEquals(states[op.Platform].Active, op)) states[op.Platform].Active = null;
        op.Dispose();
    }

    private void Guard(Operation op)
    {
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            AccountRules.Require(!op.Token.IsCancellationRequested, "CANCELLED");
            AccountRules.Require(states[op.Platform].Epoch == op.Epoch, "STALE_SESSION");
        }
    }

    private async Task<T> DiskAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        Enter(isPhysical: true);
        bool entered = false;
        try
        {
            await disk.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        catch (MusicAccountException) { throw; }
        catch (StorageException error) { throw new MusicAccountException(AccountRules.Error(error)); }
        catch (OperationCanceledException) { throw new MusicAccountException("CANCELLED"); }
        catch { throw new MusicAccountException("STORAGE"); }
        finally
        {
            if (entered) disk.Release();
            Leave(isPhysical: true);
        }
    }

    private Task<AccountSnapshot> ReadSnapshot(CancellationToken token, string? platform = null) =>
        store.ReadAsync(platform, token);

    private Task<StoredAccount> Load(string platform, CancellationToken token) =>
        DiskAsync(async () => (await ReadSnapshot(token, platform).ConfigureAwait(false)).Get(platform), token);

    public Task<MusicAccountListResult> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(async token =>
        {
            var result = await List(token).ConfigureAwait(false);
            RememberInitialObservation();
            return result;
        }, cancellationToken);

    public Task<MusicAccountStatusRecord> LogoutAccountAsync(string platform, CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(token => Logout(AccountRules.Platform(platform), token), cancellationToken);

    public async Task CancelAccountAsync(string platform, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string p = AccountRules.Platform(platform);
        Task cancellation;
        lock (gate) { AccountRules.Require(!closed, "CLOSED"); cancellation = FenceLocked(p); }
        Notify(p);
        await cancellation.ConfigureAwait(false);
    }

    public Task<MusicAccountQrChallenge> BeginQrAsync(string platform, CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(token => Begin(AccountRules.Platform(platform), token), cancellationToken);

    public Task<MusicAccountQrPollResult> PollQrAsync(string platform, string challengeId, CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(token => Poll(AccountRules.Platform(platform), challengeId, token), cancellationToken);

    public Task<MusicAccountSmsSendResult> SendSmsAsync(string platform, string phone, string countryCode = "86", CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(token => SendSmsCoreAsync(new MusicAccountSmsInput(AccountRules.Platform(platform), phone, countryCode, null), token), cancellationToken);

    public Task<MusicAccountStatusRecord> LoginSmsAsync(string platform, string phone, string countryCode, string captcha, CancellationToken cancellationToken = default) =>
        RunAccountOperationAsync(token => LoginSmsCoreAsync(new MusicAccountSmsInput(AccountRules.Platform(platform), phone, countryCode, captcha), token), cancellationToken);

    private async Task<T> RunAccountOperationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enter(); entered = true;
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally { if (entered) Leave(); }
    }

    private MusicAccountStatusRecord Status(string platform, StoredAccount value)
    {
        lock (gate)
        {
            var state = states[platform];
            bool revoked = state.Revoked;
            return new MusicAccountStatusRecord(
                platform,
                value.Generation,
                revoked ? "revoked" : value.Cookie is null ? "none" :
                    state.Verified is { } verified && Same(verified, value) ? "verified" : "saved",
                revoked ? null : value.Profile,
                revoked ? null : value.ValidatedAt);
        }
    }

    private async Task<MusicAccountListResult> List(CancellationToken token, bool awaitPhysical = false)
    {
        Dictionary<string, long> epochs;
        lock (gate) epochs = states.ToDictionary(p => p.Key, p => p.Value.Epoch);
        var physicalRead = DiskAsync(async () =>
        {
            var snapshot = await ReadSnapshot(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var items = new List<MusicAccountStatusRecord>(AccountRules.Platforms.Length);
            var changed = new List<string>();
            foreach (var platform in AccountRules.Platforms)
            {
                var value = snapshot.Get(platform);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    var state = states[platform];
                    if (state.Epoch == epochs[platform] && state.Active is null)
                    {
                        if (value.Cookie is null) state.Revoked = false;
                        if (state.Current is not null && !Same(state.Current, value) ||
                            state.Verified is not null && !Same(state.Verified, value))
                        { state.Epoch++; state.Verified = null; state.Current = null; changed.Add(platform); }
                    }
                }
                items.Add(Status(platform, value));
            }
            return (Output: new MusicAccountListResult(items), Changed: changed);
        }, token);
        var result = await (awaitPhysical ? physicalRead : physicalRead.WaitAsync(token)).ConfigureAwait(false);
        foreach (var platform in result.Changed) Notify(platform);
        if (awaitPhysical) token.ThrowIfCancellationRequested();
        return result.Output;
    }

    private async Task<StoredAccount> Commit(Operation op, StoredAccount before, string? cookie,
        MusicAccountDetailsObservation? observation, bool newScope, CancellationToken authority = default)
    {
        Guard(op);
        authority.ThrowIfCancellationRequested();
        AccountRules.Require((cookie is null) == (observation is null), "INVALID_RESPONSE");
        AccountRules.Require(cookie is null || cookie.Length > 0, "INVALID_RESPONSE");
        if (observation is not null)
        {
            AccountRules.Require(observation.Profile.Platform == op.Platform &&
                !string.IsNullOrEmpty(observation.Profile.UserId), "IDENTITY_MISMATCH");
            if (!newScope) AccountRules.Require(observation.Profile.UserId == before.Profile?.UserId, "IDENTITY_MISMATCH");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(op.Token, authority);
        var writeToken = linked.Token;
        AccountRules.Require(before.Generation < AccountRules.MaxInteger, "LIMIT");
        var next = new StoredAccount
        {
            Generation = before.Generation + 1, Cookie = cookie, Profile = observation?.Profile,
            Scope = cookie is null ? null : newScope ? Guid.NewGuid().ToString("D") : before.Scope,
            ValidatedAt = cookie is null ? null : clock.GetUtcNow().ToUnixTimeMilliseconds()
        };
        var task = DiskAsync(async () =>
        {
            Guard(op);
            writeToken.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshot(writeToken, op.Platform).ConfigureAwait(false);
            Guard(op);
            var current = snapshot.Get(op.Platform);
            AccountRules.Require(current.Generation == before.Generation &&
                current.Scope == before.Scope &&
                current.Profile?.UserId == before.Profile?.UserId, "STORAGE_CONFLICT");
            AccountRules.Require(next.Generation == current.Generation + 1 && next.Generation <= AccountRules.MaxInteger, "LIMIT");
            var (committed, _) = await store.ReplaceAsync(op.Platform, before, next, snapshot.Revision, writeToken).ConfigureAwait(false);
            return committed;
        }, writeToken);
        var committed = await task.WaitAsync(writeToken).ConfigureAwait(false);
        lock (gate)
        {
            Guard(op);
            writeToken.ThrowIfCancellationRequested();
            var state = states[op.Platform];
            state.Revoked = false; state.Current = null;
            state.Verified = cookie is null ? null : committed;
        }
        if (observation is not null)
            await SaveDetailsCacheAsync(op.Platform, new(committed.Generation, committed.Scope!,
                observation.Profile.UserId, observation.Profile, observation.ProfileFetchedAt,
                observation.ProfileFetchedAt + StoredAccountDetails.ProfileTtl, observation.Entitlements), () => Guard(op)).ConfigureAwait(false);
        else
            await DeleteDetailsCacheAsync(op.Platform).ConfigureAwait(false);
        Guard(op);
        Notify(op.Platform);
        return committed;
    }

    private async Task DeleteDetailsCacheAsync(string platform)
    {
        try
        {
            await cacheDatabase.WriteAsync(db =>
            {
                AccountDetailsStore.Delete(db, platform);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.Storage, "music.accountDetails", "delete.failed",
                new { platform }, error: error);
        }
    }

    private async Task<MusicAccountStatusRecord> Logout(string platform, CancellationToken token)
    {
        using var op = Start(platform, token, logout: true);
        try
        {
            var before = await Load(platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            return Status(platform, await Commit(op, before, null, null, false).ConfigureAwait(false));
        }
        finally { Finish(op); }
    }

    private async Task<MusicAccountQrChallenge> Begin(string platform, CancellationToken token)
    {
        AccountRules.Require(platform is "wy" or "tx" or "kg", "UNSUPPORTED");
        using var op = Start(platform, token);
        try
        {
            var before = await Load(platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            var value = await protocol.BeginAsync(platform, op.Token).ConfigureAwait(false);
            Guard(op);
            lock (gate) { Guard(op); states[platform].Challenge = new(value, before, op.Epoch); }
            return new MusicAccountQrChallenge(
                value.Platform,
                value.ChallengeId,
                value.ExpiresAt,
                value.DisplayKind,
                value.ImageBase64 is null ? value.Url : null,
                value.ImageBase64);
        }
        finally { Finish(op); }
    }

    private async Task<MusicAccountQrPollResult> Poll(string platform, string challengeId, CancellationToken token)
    {
        AccountRules.Require(platform is "wy" or "tx" or "kg", "UNSUPPORTED");
        ChallengeState? challenge;
        lock (gate) challenge = states[platform].Challenge;
        if (challenge is null || challenge.Value.ChallengeId != challengeId || challenge.Consumed)
            return new MusicAccountQrPollResult(platform, challengeId, "expired");
        using var op = Start(platform, token, poll: true);
        try
        {
            AccountRules.Require(challenge.Epoch == op.Epoch, "STALE_SESSION");
            var result = await protocol.PollAsync(platform, challengeId, async (handoff, handoffToken) =>
            {
                handoffToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    Guard(op);
                    AccountRules.Require(ReferenceEquals(states[platform].Challenge, challenge) && !challenge.Consumed &&
                        handoff.Platform == platform && handoff.ChallengeId == challengeId, "HANDOFF_REJECTED");
                    challenge.Consumed = true;
                }
                challenge.Committed = await Commit(op, challenge.Before, handoff.Cookie, handoff.Details, true, handoffToken).ConfigureAwait(false);
                return true;
            }, op.Token).ConfigureAwait(false);
            Guard(op);
            AccountRules.Require(result.Platform == platform && result.ChallengeId == challengeId &&
                result.Status is "waiting" or "confirming" or "expired" or "refused" or "authenticated", "INVALID_RESPONSE");
            MusicAccountStatusRecord? account = null;
            if (result.Status == "authenticated")
            {
                AccountRules.Require(challenge.Consumed && challenge.Committed is not null, "HANDOFF_REJECTED");
                account = Status(platform, challenge.Committed!);
            }
            if (result.Status is "authenticated" or "expired" or "refused")
                lock (gate) if (ReferenceEquals(states[platform].Challenge, challenge)) states[platform].Challenge = null;
            return new MusicAccountQrPollResult(platform, challengeId, result.Status, account);
        }
        catch (Exception error)
        {
            if (!challenge.Consumed && AccountRules.Error(error) is "RATE_LIMITED" or "BUSY") throw;
            lock (gate) if (ReferenceEquals(states[platform].Challenge, challenge)) states[platform].Challenge = null;
            throw;
        }
        finally { Finish(op); }
    }

    private static bool Same(StoredAccount a, StoredAccount b) => a.Generation == b.Generation && a.Scope == b.Scope &&
        a.Profile?.UserId == b.Profile?.UserId && a.Cookie == b.Cookie;

    public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken = default) =>
        AcquireCoreAsync(platform, cancellationToken, awaitPhysical: false);

    private async ValueTask<MusicPlatformSession?> AcquireCoreAsync(string platform, CancellationToken cancellationToken,
        bool awaitPhysical)
    {
        AccountRules.Platform(platform); cancellationToken.ThrowIfCancellationRequested();
        long epoch;
        lock (gate)
        {
            if (closed || states[platform].Revoked || states[platform].Active is not null) return null;
            epoch = states[platform].Epoch;
        }
        Enter();
        try
        {
            var physicalRead = Load(platform, cancellationToken);
            var value = await (awaitPhysical ? physicalRead : physicalRead.WaitAsync(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            MusicPlatformSession? session = null;
            bool changed = false;
            lock (gate)
            {
                var state = states[platform];
                AccountRules.Require(!closed && state.Epoch == epoch, "STALE_SESSION");
                if (state.Revoked || state.Active is not null) return null;
                if (state.Current is not null && !Same(state.Current, value) ||
                    state.Verified is not null && !Same(state.Verified, value))
                { state.Epoch++; state.Verified = null; changed = true; }
                state.Current = value;
                if (value.Cookie is not null)
                {
                    session = new(platform, value.Scope!, value.Generation, value.Profile!.UserId, value.Cookie);
                    leases.Add(session, new(state.Epoch));
                }
            }
            if (changed) Notify(platform);
            if (awaitPhysical) cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        finally { Leave(); }
    }

    public bool IsCurrent(MusicPlatformSession session)
    {
        if (session is null) return false;
        lock (gate)
            return !closed && states.TryGetValue(session.Platform, out var state) &&
                leases.TryGetValue(session, out var lease) && lease.Epoch == state.Epoch &&
                !state.Revoked && state.Active is null && state.Current is { } value &&
                value.Generation == session.Generation && value.Scope == session.Scope &&
                value.Profile?.UserId == session.UserId && value.Cookie == session.Cookie;
    }

    public bool Revoke(MusicPlatformSession session)
    {
        lock (gate)
        {
            if (!IsCurrent(session)) return false;
            _ = FenceLocked(session.Platform);
            states[session.Platform].Revoked = true;
        }
        Notify(session.Platform);
        return true;
    }

    public async Task PrepareCloseAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        InitialObservationFlight[] observations;
        lock (gate)
        {
            if (!closed)
            {
                closed = true;
                try { transferLifetime.Cancel(); } catch { }
                foreach (var platform in AccountRules.Platforms)
                {
                    try { _ = FenceLocked(platform); } catch { states[platform].Revoked = true; }
                }
            }
            observations = initialObservationFlights.ToArray();
            pending = drained.Task;
        }
        foreach (var observation in observations) observation.Cancel();
        foreach (var platform in AccountRules.Platforms) Notify(platform);
        await Task.WhenAll(observations.Select(value => value.Work ?? Task.CompletedTask).Append(pending))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCore());
    }

    private async Task DisposeCore()
    {
        await PrepareCloseAsync().ConfigureAwait(false);
        await protocol.DisposeAsync().ConfigureAwait(false);
        transferLifetime.Dispose();
        detailsCacheGate.Dispose();
        disk.Dispose();
    }
}
