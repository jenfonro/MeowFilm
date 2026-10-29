// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner : IMusicAccountDetailsSource
{
    private readonly HashSet<DetailsFlight> detailsFlights = [];
    private readonly HashSet<string> detailsUncertain = new(StringComparer.Ordinal);
    private sealed class DetailsFlight(StoredAccount before, StoredAccountDetails? cache,
        MusicPlatformSession session, long epoch, TimeSpan remaining) : IDisposable
    {
        internal StoredAccount Before { get; } = before;
        internal StoredAccountDetails? Cache { get; } = cache;
        internal MusicPlatformSession Session { get; } = session;
        internal long Epoch { get; } = epoch;
        internal CancellationTokenSource Deadline { get; } = new(remaining);
        internal TaskCompletionSource<MusicAccountDetails> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
        internal bool Admitted;
        internal string? Revocation;
        internal void Abort(string reason)
        {
            Revocation ??= reason;
            try { Deadline.Cancel(); } catch (Exception e) when (e is AggregateException or ObjectDisposedException) { }
        }
        public void Dispose() => Deadline.Dispose();
        public override string ToString() => "AccountDetailsFlight (private)";
    }
    private sealed record DetailsBaseline(StoredAccount Account, StoredAccountDetails? Cache, long Epoch, bool Hidden);
    private void FenceDetailsLocked(string platform)
    {
        foreach (var flight in detailsFlights.Where(f => f.Session.Platform == platform).ToArray())
            flight.Abort(closed ? "CLOSED" : "SESSION_CHANGED");
    }
    private void ObserveDetailsAccountChange(string platform)
    {
        lock (gate)
            foreach (var flight in detailsFlights.Where(f => f.Session.Platform == platform).ToArray())
                if (!IsCurrent(flight.Session)) flight.Abort(closed ? "CLOSED" : "SESSION_CHANGED");
    }
    private void DetailsGuard(DetailsFlight flight)
    {
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            AccountRules.Require(flight.Revocation is null, flight.Revocation ?? "SESSION_CHANGED");
            AccountRules.Require(states[flight.Session.Platform].Epoch == flight.Epoch && IsCurrent(flight.Session), "SESSION_CHANGED");
            AccountRules.Require(!flight.Deadline.IsCancellationRequested, "TIMEOUT");
        }
    }
    private async Task<DetailsBaseline> LoadDetails(string platform, CancellationToken token, bool reconcileUnknown)
    {
        long epoch;
        lock (gate) { AccountRules.Require(!closed, "CLOSED"); epoch = states[platform].Epoch; }
        bool changed = false;
        var result = await DiskAsync(async () =>
        {
            var document = await ReadDocument(token).ConfigureAwait(false);
            var account = document.Get(platform);
            var cache = document.GetDetails(platform);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                AccountRules.Require(!closed, "CLOSED");
                var state = states[platform];
                AccountRules.Require(state.Epoch == epoch, "SESSION_CHANGED");
                AccountRules.Require(state.Active is null, "BUSY");
                if (state.Current is not null && !Same(state.Current, account) ||
                    state.Verified is not null && !Same(state.Verified, account))
                {
                    state.Epoch++; state.Verified = null; changed = true;
                    FenceDetailsLocked(platform);
                }
                state.Current = account;
                if (reconcileUnknown) detailsUncertain.Remove(platform);
                return new DetailsBaseline(account, cache, state.Epoch,
                    state.Revoked || state.Uncertain || detailsUncertain.Contains(platform));
            }
        }, token).ConfigureAwait(false);
        if (changed) Notify(platform);
        return result;
    }
    public async Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default)
    {
        AccountRules.Platform(platform); token.ThrowIfCancellationRequested();
        Enter();
        try
        {
            // Physical disk gate, no WaitAsync and no HTTP. An explicit read also
            // reconciles a prior cache-only unknown CAS after the writer settled.
            var baseline = await LoadDetails(platform, token, reconcileUnknown: true).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                AccountRules.Require(!closed && states[platform].Epoch == baseline.Epoch, "SESSION_CHANGED");
                return DetailsView(platform, baseline.Account, baseline.Cache, baseline.Hidden);
            }
        }
        catch (MusicAccountException e) when (e.Code == "CANCELLED" && token.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
        finally { Leave(); }
    }
    public async Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
        CancellationToken token = default)
    {
        AccountRules.Platform(platform); token.ThrowIfCancellationRequested();
        AccountRules.Require(expectedGeneration is >= 0 and <= AccountRules.MaxInteger);
        Enter();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var caller = admission.Token;
        DetailsFlight? flight = null;
        try
        {
            var baseline = await LoadDetails(platform, caller, reconcileUnknown: false).ConfigureAwait(false);
            bool start = false;
            lock (gate)
            {
                caller.ThrowIfCancellationRequested();
                AccountRules.Require(!closed && states[platform].Epoch == baseline.Epoch, "SESSION_CHANGED");
                AccountRules.Require(baseline.Account.Generation == expectedGeneration, "SESSION_CHANGED");
                AccountRules.Require(!detailsUncertain.Contains(platform), "COMMIT_UNKNOWN");
                AccountRules.Require(!baseline.Hidden && baseline.Account.Cookie is not null, "AUTH_REQUIRED");
                flight = detailsFlights.FirstOrDefault(f => f.Session.Platform == platform && f.Epoch == baseline.Epoch &&
                    f.Before.ValueFingerprint == baseline.Account.ValueFingerprint && !f.Completion.Task.IsCompleted && f.Revocation is null);
                if (flight is null)
                {
                    var account = baseline.Account;
                    _ = AccountRules.Cookies(account.Cookie!);
                    var session = new MusicPlatformSession(platform, account.Scope!, account.Generation, account.Profile!.UserId, account.Cookie!);
                    leases.Add(session, new(baseline.Epoch));
                    var left = TimeSpan.FromSeconds(25) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    AccountRules.Require(left > TimeSpan.Zero, "TIMEOUT");
                    flight = new(account, baseline.Cache, session, baseline.Epoch, left);
                    detailsFlights.Add(flight); Enter(isPhysical: true); start = true;
                }
                flight.Waiters++;
            }
            if (start) _ = RunDetailsFlight(flight);
            return await flight.Completion.Task.WaitAsync(caller).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException || e is MusicAccountException { Code: "CANCELLED" })
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (timeout.IsCancellationRequested) throw new MusicAccountException("TIMEOUT");
            throw;
        }
        finally
        {
            try
            {
                if (flight is not null)
                {
                    bool drain;
                    lock (gate)
                    {
                        flight.Waiters--;
                        drain = flight.Waiters == 0 && !flight.Completion.Task.IsCompleted;
                        if (drain) flight.Abort(timeout.IsCancellationRequested && !token.IsCancellationRequested ? "TIMEOUT" : "CANCELLED");
                    }
                    // One cancelled subscriber does not abort other subscribers.
                    // The last one must await actual HTTP/CAS/cleanup before leaving.
                    if (drain)
                    {
                        try { await flight.Completion.Task.ConfigureAwait(false); }
                        catch (MusicAccountException e) when (e.Code == "COMMIT_UNKNOWN") { throw; }
                        catch { }
                    }
                }
            }
            finally { Leave(); }
        }
    }
    private MusicAccountDetailsObservation ValidateDetailsObservation(
        MusicAccountDetailsObservation observed, string platform, string cookie)
    {
        AccountRules.Require(observed is not null, "INVALID_RESPONSE");
        var profile = AccountRules.Profile(observed!.Profile, platform, cookie);
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        AccountRules.Require(observed.ProfileFetchedAt > 0 && observed.ProfileFetchedAt <= now &&
            now - observed.ProfileFetchedAt <= 60000, "INVALID_RESPONSE");
        var rights = AccountDetailsSnapshot.Validate(observed.Entitlements, platform);
        AccountRules.Require(rights.CheckedAt <= now && now - rights.CheckedAt <= 60000, "INVALID_RESPONSE");
        return observed with { Profile = profile, Entitlements = rights };
    }
    private async Task RunDetailsFlight(DetailsFlight flight)
    {
        MusicAccountDetails? result = null; Exception? failure = null;
        string platform = flight.Session.Platform;
        try
        {
            DetailsGuard(flight);
            var observed = ValidateDetailsObservation(await protocol.ReadAccountDetailsAsync(flight.Session, flight.Deadline.Token)
                .ConfigureAwait(false), platform, flight.Session.Cookie);
            DetailsGuard(flight);
            var profile = observed.Profile;
            AccountRules.Require(profile.UserId == flight.Session.UserId, "IDENTITY_MISMATCH");
            var rights = observed.Entitlements;
            // WY/QQ keep a prior known cache on unknown/outage. The current
            // refresh still returns unknown, never the old active rights as new proof.
            var persistRights = rights.Membership.Vip is null && platform is "wy" or "tx" &&
                flight.Cache is { } old && old.BelongsTo(flight.Before) && old.Rights.Membership.Vip is not null
                    ? old.Rights : rights;
            var next = new StoredAccountDetails((flight.Cache?.Revision ?? 0) + 1, flight.Before.Generation,
                flight.Before.Scope!, flight.Before.ValueFingerprint!, profile.UserId, profile,
                observed.ProfileFetchedAt, observed.ProfileFetchedAt + AccountDetailsSnapshot.ProfileTtl, persistRights);
            await SaveDetails(flight, next).ConfigureAwait(false);
            lock (gate)
            {
                DetailsGuard(flight);
                states[platform].Verified = flight.Before; // Fresh credential + exact identity, not membership/device inference.
                result = DetailsView(platform, flight.Before, next, false, rights);
            }
        }
        catch (Exception error)
        {
            string code = error is MusicPlatformException pe ? pe.Code : AccountRules.Error(error);
            if (flight.Admitted && code != "STORAGE_CONFLICT") code = "COMMIT_UNKNOWN";
            else
            {
                lock (gate)
                {
                    if (closed) code = "CLOSED";
                    else if (flight.Revocation is not null) code = flight.Revocation;
                    else if (!IsCurrent(flight.Session)) code = "SESSION_CHANGED";
                    else if (flight.Deadline.IsCancellationRequested) code = "TIMEOUT";
                }
                if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "IDENTITY_MISMATCH" or "VERIFICATION_REQUIRED")
                    Revoke(flight.Session); // Exact issued snapshot only; a late failure cannot revoke its replacement.
            }
            failure = new MusicAccountException(code);
        }
        finally
        {
            lock (gate) { detailsFlights.Remove(flight); flight.Dispose(); }
            Leave(isPhysical: true);
        }
        if (failure is not null) flight.Completion.TrySetException(failure);
        else flight.Completion.TrySetResult(result!);
    }
    private Task SaveDetails(DetailsFlight flight, StoredAccountDetails next) => DiskAsync(async () =>
    {
        DetailsGuard(flight);
        var document = await ReadDocument(flight.Deadline.Token).ConfigureAwait(false);
        DetailsGuard(flight);
        var actualAccount = document.Get(flight.Session.Platform);
        if (actualAccount.ValueFingerprint != flight.Before.ValueFingerprint)
        {
            lock (gate)
            {
                var state = states[flight.Session.Platform];
                state.Epoch++; state.Current = actualAccount; state.Verified = null;
                FenceDetailsLocked(flight.Session.Platform);
            }
            Notify(flight.Session.Platform);
            throw new MusicAccountException("SESSION_CHANGED");
        }
        byte[] bytes = document.ReplaceDetails(flight.Session.Platform, flight.Before, flight.Cache, next);
        try
        {
            lock (gate)
            {
                DetailsGuard(flight);
                detailsUncertain.Add(flight.Session.Platform); flight.Admitted = true;
            }
            try
            {
                var receipt = await store.WriteAsync(DocumentNamespace, bytes, document.Fingerprint, flight.Deadline.Token).ConfigureAwait(false);
                AccountRules.Require(receipt == DocumentFingerprint.FromBytes(bytes), "COMMIT_UNKNOWN");
            }
            catch (ProtectedDocumentException e) when (e.Error == DocumentStoreError.Conflict)
            {
                lock (gate) { detailsUncertain.Remove(flight.Session.Platform); flight.Admitted = false; }
                throw new MusicAccountException("STORAGE_CONFLICT");
            }
            catch { throw new MusicAccountException("COMMIT_UNKNOWN"); }
            lock (gate)
            {
                DetailsGuard(flight);
                detailsUncertain.Remove(flight.Session.Platform); flight.Admitted = false;
            }
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }, flight.Deadline.Token);

    private MusicAccountDetails DetailsView(string platform, StoredAccount account, StoredAccountDetails? cache,
        bool hidden, MusicAccountEntitlements? observed = null)
    {
        var state = states[platform];
        hidden |= state.Revoked || state.Uncertain || detailsUncertain.Contains(platform);
        string status = hidden ? "revoked" : account.Cookie is null ? "none" :
            state.Verified is { } verified && Same(verified, account) ? "verified" : "saved";
        if (hidden || account.Cookie is null)
            return new(platform, account.Generation, status, null, 0, 0, new(null, "unknown", null), 0, 0, Array.Empty<string>(),
                ErrorCode: detailsUncertain.Contains(platform) ? "COMMIT_UNKNOWN" : null);
        if (cache?.BelongsTo(account) != true) cache = null;
        var profile = cache?.Profile ?? account.Profile;
        long fetched = cache?.ProfileFetchedAt ?? account.ValidatedAt ?? 0;
        long expires = fetched > 0 ? fetched + AccountDetailsSnapshot.ProfileTtl : 0;
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var rights = observed ?? cache?.Rights;
        bool fresh = rights is not null && rights.CheckedAt <= now && rights.RecheckAt > now;
        var membership = fresh || observed is not null ? rights!.Membership : new MusicAccountMembership(null, "unknown", rights?.Membership.ExpiresAt);
        var membershipFreshness = rights is null ? MusicAccountDetailsFreshness.Missing :
            rights.VerificationDeferred ? MusicAccountDetailsFreshness.Deferred :
            fresh ? MusicAccountDetailsFreshness.Fresh : MusicAccountDetailsFreshness.Stale;
        return new(platform, account.Generation, status, profile, fetched, expires, membership,
            rights?.CheckedAt ?? 0, rights?.RecheckAt ?? 0,
            fresh || observed is not null ? Array.AsReadOnly(rights!.AudioLevels.ToArray()) : Array.Empty<string>(),
            rights?.VerificationDeferred ?? false, rights?.ErrorCode, rights?.RetryAt ?? 0,
            profile is null ? MusicAccountDetailsFreshness.Missing : fetched <= now && expires > now
                ? MusicAccountDetailsFreshness.Fresh : MusicAccountDetailsFreshness.Stale, membershipFreshness);
    }
}
