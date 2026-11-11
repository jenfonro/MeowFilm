// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner : IMusicAccountDetailsSource
{
    private readonly HashSet<DetailsFlight> detailsFlights = [];
    private bool readsStopping;

    internal async Task PrepareReadShutdownAsync()
    {
        DetailsFlight[] details;
        InitialObservationFlight[] observations;
        lock (gate)
        {
            readsStopping = true;
            details = detailsFlights.ToArray();
            observations = initialObservationFlights.ToArray();
            foreach (var flight in details) flight.Abort("CANCELLED");
        }
        foreach (var flight in observations) flight.Cancel();
        foreach (var task in details.Select(f => (Task)f.Completion.Task)
            .Concat(observations.Select(f => f.Work ?? Task.CompletedTask)))
            try { await task.ConfigureAwait(false); } catch { /* Outcome remains with the owner. */ }
    }
    internal void ResumeReads() { lock (gate) { AccountRules.Require(!closed, "CLOSED"); readsStopping = false; } }
    private sealed class DetailsFlight(StoredAccount before, StoredAccountDetails? cache,
        MusicPlatformSession session, long epoch, TimeSpan remaining) : IDisposable
    {
        internal StoredAccount Before { get; } = before;
        internal StoredAccountDetails? Cache { get; } = cache;
        internal MusicPlatformSession Session { get; } = session;
        internal long Epoch { get; } = epoch;
        internal CancellationTokenSource Deadline { get; } = new(remaining);
        internal TaskCompletionSource<MusicAccountDetails> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    private async Task<DetailsBaseline> LoadDetails(string platform, CancellationToken token)
    {
        long epoch;
        lock (gate) { AccountRules.Require(!closed, "CLOSED"); epoch = states[platform].Epoch; }
        bool changed = false;
        var result = await DiskAsync(async () =>
        {
            var document = await ReadDocument(token).ConfigureAwait(false);
            var account = document.Get(platform);
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
                return new DetailsBaseline(account, null, state.Epoch, state.Revoked || state.Uncertain);
            }
        }, token).ConfigureAwait(false);
        if (changed) Notify(platform);
        var cached = await ReadDetailsCache(token).ConfigureAwait(false);
        StoredAccountDetails? cache = null;
        try
        {
            if (cached[platform] is { } value)
                cache = AccountDetailsDocument.ReadDetails(JsonSerializer.SerializeToElement(value), platform);
        }
        catch { /* A display snapshot that cannot be decoded is a cache miss. */ }
        return result with { Cache = cache };
    }
    public async Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default)
    {
        AccountRules.Platform(platform); token.ThrowIfCancellationRequested();
        Enter();
        try
        {
            // Credentials retain their physical disk gate; display cache has no business reconciliation.
            var baseline = await LoadDetails(platform, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                AccountRules.Require(!closed && !readsStopping && states[platform].Epoch == baseline.Epoch, "SESSION_CHANGED");
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
            var baseline = await LoadDetails(platform, caller).ConfigureAwait(false);
            bool start = false;
            lock (gate)
            {
                caller.ThrowIfCancellationRequested();
                AccountRules.Require(!closed && !readsStopping && states[platform].Epoch == baseline.Epoch, "SESSION_CHANGED");
                AccountRules.Require(baseline.Account.Generation == expectedGeneration, "SESSION_CHANGED");
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
                    _ = flight.Completion.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
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
        finally { Leave(); }
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
            var next = new StoredAccountDetails(flight.Before.Generation,
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
            lock (gate)
            {
                if (closed) code = "CLOSED";
                else if (flight.Revocation is not null) code = flight.Revocation;
                else if (!IsCurrent(flight.Session)) code = "SESSION_CHANGED";
                else if (flight.Deadline.IsCancellationRequested) code = "TIMEOUT";
            }
            if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "IDENTITY_MISMATCH" or "VERIFICATION_REQUIRED")
                Revoke(flight.Session); // Exact issued snapshot only; a late failure cannot revoke its replacement.
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
    private async Task SaveDetails(DetailsFlight flight, StoredAccountDetails next)
    {
        // Re-observe the authoritative credential before publishing an observation.
        // Display writes never enter the credential CAS/pending state.
        await DiskAsync(async () =>
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
            return true;
        }, flight.Deadline.Token).ConfigureAwait(false);
        DetailsGuard(flight);
        SaveDetailsCache(flight.Session.Platform, next, () => DetailsGuard(flight));
        DetailsGuard(flight);
    }
    private async Task<JsonObject> ReadDetailsCache(CancellationToken token)
    {
        DocumentSnapshot? snapshot = null;
        try
        {
            snapshot = await detailsCacheStore.ReadAsync(DetailsCacheNamespace, token).ConfigureAwait(false);
            return snapshot.Data is null ? new() : JsonNode.Parse(snapshot.Data)!.AsObject();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new(); }
        finally { if (snapshot?.Data is { } data) CryptographicOperations.ZeroMemory(data); }
    }
    private void SaveDetailsCache(string platform, StoredAccountDetails next, Action guard)
    {
        try
        {
            lock (gate)
            {
                guard();
                var snapshot = detailsCacheStore.ReadMemory(DetailsCacheNamespace);
                JsonObject root;
                try
                {
                    try { root = snapshot.Data is null ? new() : JsonNode.Parse(snapshot.Data)!.AsObject(); }
                    catch { root = new(); }
                }
                finally { if (snapshot.Data is { } data) CryptographicOperations.ZeroMemory(data); }
                root[platform] = AccountDetailsDocument.Encode(next);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root);
                try { detailsCacheStore.Save(DetailsCacheNamespace, bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch { System.Diagnostics.Trace.TraceWarning("Music account details cache write failed."); }
    }

    private MusicAccountDetails DetailsView(string platform, StoredAccount account, StoredAccountDetails? cache,
        bool hidden, MusicAccountEntitlements? observed = null)
    {
        var state = states[platform];
        hidden |= state.Revoked || state.Uncertain;
        string status = hidden ? "revoked" : account.Cookie is null ? "none" :
            state.Verified is { } verified && Same(verified, account) ? "verified" : "saved";
        if (hidden || account.Cookie is null)
            return new(platform, account.Generation, status, null, 0, 0, new(null, "unknown", null), 0, 0, Array.Empty<string>());
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
