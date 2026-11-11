// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>
/// The sole native writer of music.accounts.v1. The injected store is BORROWED:
/// this owner never disposes it, including unsuccessful preparation for shutdown.
/// Constructing an owner does not read disk, contact a platform or start Node.
/// </summary>
public sealed partial class MusicAccountOwner : IMusicPlatformSessionSource, IAsyncDisposable
{
    public const string DocumentNamespace = "music.accounts.v1";
    public const string Version = "1.3.0";
    private readonly IDocumentStore store;
    private readonly CacheDocumentStore detailsCacheStore;
    private readonly bool ownsDetailsCacheStore;
    public const string DetailsCacheNamespace = "music.account-details.v1";
    private readonly IMusicAccountProtocol protocol;
    private readonly MusicPlatformClientContextOwner clients;
    private readonly MusicPlatformClientProtocol clientProtocol;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly SerialOperationQueue disk = new();
    private readonly Dictionary<string, State> states = AccountRules.Platforms.ToDictionary(p => p, _ => new State());
    private readonly ConditionalWeakTable<MusicPlatformSession, Lease> leases = new();
    private int calls, physical;
    private bool closed;
    private TaskCompletionSource drained = Completed();
    private Task? disposal;

    /// <summary>Synchronous invalidation only; subscriber errors cannot change a committed result.</summary>
    public event Action<string>? SessionChanged;

    internal MusicAccountOwner(IDocumentStore store, IMusicAccountProtocol protocol,
        MusicPlatformClientContextOwner clients, MusicPlatformClientProtocol clientProtocol, TimeProvider? clock = null,
        IDocumentStore? detailsCacheStore = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        this.clients = clients ?? throw new ArgumentNullException(nameof(clients));
        this.clientProtocol = clientProtocol ?? throw new ArgumentNullException(nameof(clientProtocol));
        this.clock = clock ?? TimeProvider.System;
        this.detailsCacheStore = detailsCacheStore as CacheDocumentStore ??
            (detailsCacheStore is null ? new CacheDocumentStore() : new CacheDocumentStore(detailsCacheStore));
        ownsDetailsCacheStore = detailsCacheStore is not CacheDocumentStore;
        clients.ContextChanged += ClientContextChanged;
    }
    private sealed class State
    {
        internal long Epoch;
        internal Operation? Active;
        internal bool Revoked, Uncertain;
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
        internal volatile bool Admitted;
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
        internal MusicKgClientContext? KgDevice;
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
        if (platform == "tx") ObserveQqAccountChange();
        var handlers = SessionChanged;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(platform); } catch { /* Never leak observer failures or change CAS truth. */ }
    }
    private void FenceLocked(string platform)
    {
        var state = states[platform];
        state.Epoch++;
        state.Active?.Cancellation.Cancel();
        state.Active = null; state.Current = null; state.Verified = null; state.Challenge = null;
        FenceDetailsLocked(platform);
        if (platform == "tx") FenceQqFeedbackLocked();
        protocol.Cancel(platform);
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
                    FenceLocked(platform);
                    if (logout) state.Revoked = true; // Before any disk I/O, even an unknown commit.
                    AccountRules.Require(!state.Uncertain, "COMMIT_UNKNOWN");
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
    private Task<T> DiskAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        Enter(isPhysical: true);
        async Task<T> Physical()
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return await action().ConfigureAwait(false);
            }
            catch (MusicAccountException) { throw; }
            catch (DocumentStoreException error) { throw new MusicAccountException(AccountRules.Error(error)); }
            catch (OperationCanceledException) { throw new MusicAccountException("CANCELLED"); }
            catch { throw new MusicAccountException("STORAGE"); }
        }
        // Cancellation can skip a queued operation, never release a running writer.
        var task = disk.Enqueue(Physical, token);
        var settled = SettleAsync();
        _ = settled.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return settled;
        async Task<T> SettleAsync()
        {
            try { return await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new MusicAccountException("CANCELLED"); }
            finally { Leave(isPhysical: true); }
        }
    }
    private async Task<AccountDocument> ReadDocument(CancellationToken token) =>
        AccountDocument.Decode(await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false));
    private Task<StoredAccount> Load(string platform, CancellationToken token) =>
        DiskAsync(async () => (await ReadDocument(token).ConfigureAwait(false)).Get(platform), token);

    /// <summary>Same safe JSON command shapes as the UI, executed entirely in native code.</summary>
    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var input = parameters is JsonElement element ? element.Clone() :
                JsonSerializer.SerializeToElement(parameters ?? new { });
            return ExecuteAsync(method, input, cancellationToken);
        }
        catch (Exception error) { return Task.FromResult(Failure(AccountRules.Error(error))); }
    }
    public async Task<JsonElement> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        bool entered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = method switch
            {
                "music.accounts.list" => "list", "music.accounts.import" => "import",
                "music.accounts.validate" => "validate", "music.accounts.logout" => "logout",
                "music.accounts.cancel" => "cancel", "music.accounts.qr.begin" => "qr.begin",
                "music.accounts.qr.poll" => "qr.poll", "music.accounts.sms.send" => "sms.send",
                "music.accounts.sms.login" => "sms.login", _ => throw new MusicAccountException("INVALID_REQUEST")
            };
            string[] fields = name switch { "list" => [], "import" => ["platform", "cookie"],
                "qr.poll" => ["platform", "challengeId"],
                "sms.send" => ["platform", "phone", "countryCode"],
                "sms.login" => ["platform", "phone", "countryCode", "captcha"], _ => ["platform"] };
            AccountRules.Fields(parameters, fields, name.StartsWith("sms.", StringComparison.Ordinal)
                ? fields.Where(f => f != "countryCode").ToArray() : fields);
            string platform = name == "list" ? "" : AccountRules.Platform(AccountRules.String(parameters, "platform", 8));
            if (name == "import") _ = AccountRules.Cookies(AccountRules.String(parameters, "cookie", 16384, "INVALID_CREDENTIALS"));
            if (name == "qr.poll") AccountRules.Text(AccountRules.String(parameters, "challengeId", 80), 80);
            var smsInput = name.StartsWith("sms.", StringComparison.Ordinal) ? SmsInput(parameters, name == "sms.login") : null;
            // Cancellation must remain available even when ignored I/O holds every admission slot.
            if (name == "cancel")
            {
                lock (gate) { AccountRules.Require(!closed, "CLOSED"); FenceLocked(platform); }
                Notify(platform);
                return JsonSerializer.SerializeToElement(new { ok = true, cancelled = true });
            }
            Enter(); entered = true;
            var output = name switch
            {
                "list" => await List(cancellationToken).ConfigureAwait(false),
                "import" => await Validate(platform, parameters.GetProperty("cookie").GetString(), cancellationToken).ConfigureAwait(false),
                "validate" => await Validate(platform, null, cancellationToken).ConfigureAwait(false),
                "logout" => await Logout(platform, cancellationToken).ConfigureAwait(false),
                "qr.begin" => await Begin(platform, cancellationToken).ConfigureAwait(false),
                "qr.poll" => await Poll(platform, parameters.GetProperty("challengeId").GetString()!, cancellationToken).ConfigureAwait(false),
                "sms.send" => await Sms(smsInput!, false, cancellationToken).ConfigureAwait(false),
                "sms.login" => await Sms(smsInput!, true, cancellationToken).ConfigureAwait(false),
                _ => throw new MusicAccountException("INVALID_REQUEST")
            };
            output["ok"] = true;
            var result = JsonSerializer.SerializeToElement(output);
            if (name == "list") RememberInitialObservation(result);
            return result;
        }
        catch (Exception error)
        {
            return error is MusicAccountException { RetryAfterSeconds: > 0 } known
                ? JsonSerializer.SerializeToElement(new { ok = false, code = known.Code, retryAfter = known.RetryAfterSeconds })
                : Failure(AccountRules.Error(error));
        }
        finally { if (entered) Leave(); }
    }
    private static JsonElement Failure(string code) => JsonSerializer.SerializeToElement(new { ok = false, code });
    private JsonObject Status(string platform, StoredAccount value)
    {
        lock (gate)
        {
            var state = states[platform];
            bool revoked = state.Revoked || state.Uncertain;
            return new JsonObject
            {
                ["platform"] = platform, ["generation"] = value.Generation,
                ["state"] = revoked ? "revoked" : value.Cookie is null ? "none" :
                    state.Verified is { } verified && Same(verified, value) ? "verified" : "saved",
                ["profile"] = revoked ? null : AccountDocument.ProfileNode(value.Profile),
                ["validatedAt"] = revoked ? null : value.ValidatedAt
            };
        }
    }
    private async Task<JsonObject> List(CancellationToken token, bool awaitPhysical = false)
    {
        Dictionary<string, long> epochs;
        lock (gate) epochs = states.ToDictionary(p => p.Key, p => p.Value.Epoch);
        // Read AND reconcile while holding the same physical storage gate. Otherwise
        // an account CAS could be admitted after the read, then an older list snapshot
        // could clear its Uncertain flag or roll back the observed account generation.
        var physicalRead = DiskAsync(async () =>
        {
            var snapshot = await ReadDocument(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var items = new JsonArray();
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
                        state.Uncertain = false; // Explicit observation, never automatic replay.
                        if (value.Cookie is null) state.Revoked = false; // Confirm durable deletion.
                        if (state.Current is not null && !Same(state.Current, value) ||
                            state.Verified is not null && !Same(state.Verified, value))
                        { state.Epoch++; state.Verified = null; state.Current = null; changed.Add(platform); }
                    }
                }
                items.Add(Status(platform, value));
            }
            return (Output: new JsonObject { ["version"] = 1, ["items"] = items }, Changed: changed);
        }, token);
        var result = await (awaitPhysical ? physicalRead : physicalRead.WaitAsync(token)).ConfigureAwait(false);
        foreach (var platform in result.Changed) Notify(platform);
        // Reconciliation may already have fenced a lease. Finish its synchronous
        // notifications before observing late cancellation; never lose that fence.
        if (awaitPhysical) token.ThrowIfCancellationRequested();
        return result.Output;
    }
    private async Task<StoredAccount> Commit(Operation op, StoredAccount before, string? cookie,
        MusicAccountDetailsObservation? observation, bool newScope, CancellationToken authority = default, MusicKgClientContext? kgDevice = null)
    {
        Guard(op);
        authority.ThrowIfCancellationRequested();
        AccountRules.Require((cookie is null) == (observation is null), "INVALID_RESPONSE");
        if (observation is not null)
        {
            observation = ValidateDetailsObservation(observation, op.Platform, cookie!);
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
            var document = await ReadDocument(writeToken).ConfigureAwait(false);
            Guard(op);
            if (kgDevice is not null)
                AccountRules.Require(clients.IsCurrent(kgDevice), "STORAGE_CONFLICT");
            byte[] bytes = document.Replace(op.Platform, before, next);
            try
            {
                lock (gate)
                {
                    Guard(op);
                    writeToken.ThrowIfCancellationRequested();
                    states[op.Platform].Uncertain = true;
                    op.Admitted = true;
                }
                DocumentFingerprint actual;
                try
                {
                    actual = await store.WriteAsync(DocumentNamespace, bytes, document.Fingerprint, writeToken).ConfigureAwait(false);
                    AccountRules.Require(actual == DocumentFingerprint.FromBytes(bytes), "COMMIT_UNKNOWN");
                }
                catch (DocumentStoreException e) when (e.Error == DocumentStoreError.Conflict)
                {
                    lock (gate) if (states[op.Platform].Epoch == op.Epoch) states[op.Platform].Uncertain = false;
                    throw new MusicAccountException("STORAGE_CONFLICT");
                }
                catch { throw new MusicAccountException("COMMIT_UNKNOWN"); }
                return document.Get(op.Platform);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, writeToken);
        try
        {
            var committed = await task.WaitAsync(writeToken).ConfigureAwait(false);
            lock (gate)
            {
                // A successful disk write is not permission for a stale callback to reauthorize.
                Guard(op);
                writeToken.ThrowIfCancellationRequested();
                if (kgDevice is not null) AccountRules.Require(clients.IsCurrent(kgDevice), "STORAGE_CONFLICT");
                var state = states[op.Platform];
                state.Uncertain = false; state.Revoked = false; state.Current = null;
                state.Verified = cookie is null ? null : committed;
            }
            if (observation is not null)
                SaveDetailsCache(op.Platform, new(committed.Generation, committed.Scope!, committed.ValueFingerprint!,
                    observation.Profile.UserId, observation.Profile, observation.ProfileFetchedAt,
                    observation.ProfileFetchedAt + AccountDetailsSnapshot.ProfileTtl, observation.Entitlements), () => Guard(op));
            Guard(op);
            Notify(op.Platform);
            return committed;
        }
        catch (Exception error)
        {
            if (AccountRules.Error(error) == "STORAGE_CONFLICT") throw;
            if (op.Admitted) throw new MusicAccountException("COMMIT_UNKNOWN");
            throw;
        }
    }
    private async Task<JsonObject> Validate(string platform, string? supplied, CancellationToken token)
    {
        using var op = Start(platform, token);
        try
        {
            var before = await Load(platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            if (supplied is null)
                lock (gate) AccountRules.Require(!states[platform].Revoked && before.Cookie is not null, "AUTH_REQUIRED");
            string cookie = supplied ?? before.Cookie!;
            var observation = await protocol.ValidateAsync(platform, cookie, op.Token).ConfigureAwait(false);
            Guard(op);
            var value = await Commit(op, before, cookie, observation, supplied is not null).ConfigureAwait(false);
            return new JsonObject { ["account"] = Status(platform, value) };
        }
        catch (Exception error)
        {
            if (supplied is null && AccountRules.Error(error) is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN")
            {
                bool changed = false;
                lock (gate)
                    if (states[platform].Epoch == op.Epoch && !op.Token.IsCancellationRequested && !closed)
                    { states[platform].Revoked = true; states[platform].Current = null; changed = true; }
                if (changed) Notify(platform);
            }
            throw;
        }
        finally { Finish(op); }
    }
    private async Task<JsonObject> Logout(string platform, CancellationToken token)
    {
        using var op = Start(platform, token, logout: true);
        try
        {
            var before = await Load(platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            Guard(op);
            return new JsonObject { ["account"] = Status(platform,
                await Commit(op, before, null, null, false).ConfigureAwait(false)) };
        }
        finally { Finish(op); }
    }
    private async Task<JsonObject> Begin(string platform, CancellationToken token)
    {
        AccountRules.Require(platform is "wy" or "tx" or "kg", "UNSUPPORTED");
        using var op = Start(platform, token);
        try
        {
            var before = await Load(platform, op.Token).WaitAsync(op.Token).ConfigureAwait(false);
            var value = await protocol.BeginAsync(platform, op.Token).ConfigureAwait(false);
            var device = value.KgClient;
            if (platform == "kg") AccountRules.Require(device is not null && clients.IsCurrent(device), "DEVICE_CONTEXT_REQUIRED");
            Guard(op); AccountRules.Challenge(value, platform);
            lock (gate) { Guard(op); states[platform].Challenge = new(value, before, op.Epoch) { KgDevice = device }; }
            var display = new JsonObject { ["platform"] = value.Platform,
                ["challengeId"] = value.ChallengeId, ["expiresAt"] = value.ExpiresAt, ["displayKind"] = value.DisplayKind };
            if (value.ImageBase64 is { } image) { display["imageBase64"] = image; display["imageMimeType"] = "image/png"; }
            else display["url"] = value.Url;
            return new JsonObject { ["challenge"] = display };
        }
        finally { Finish(op); }
    }
    private async Task<JsonObject> Poll(string platform, string challengeId, CancellationToken token)
    {
        AccountRules.Require(platform is "wy" or "tx" or "kg", "UNSUPPORTED");
        ChallengeState? challenge;
        lock (gate) challenge = states[platform].Challenge;
        JsonObject Result(string status) => new() { ["platform"] = platform, ["challengeId"] = challengeId, ["status"] = status };
        if (challenge is null || challenge.Value.ChallengeId != challengeId || challenge.Consumed) return Result("expired");
        using var op = Start(platform, token, poll: true);
        try
        {
            AccountRules.Require(challenge.Epoch == op.Epoch, "STALE_SESSION");
            if (challenge.KgDevice is { } device)
                AccountRules.Require(clients.IsCurrent(device), "STORAGE_CONFLICT");
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
                // Same admission/CAS as explicit import; the callback cannot invent a baseline.
                challenge.Committed = await Commit(op, challenge.Before, handoff.Cookie, handoff.Details, true, handoffToken,
                    challenge.KgDevice).ConfigureAwait(false);
                return true;
            }, op.Token).ConfigureAwait(false);
            Guard(op);
            AccountRules.Require(result.Platform == platform && result.ChallengeId == challengeId &&
                result.Status is "waiting" or "confirming" or "expired" or "refused" or "authenticated", "INVALID_RESPONSE");
            var output = Result(result.Status);
            if (result.Status == "authenticated")
            {
                AccountRules.Require(challenge.Consumed && challenge.Committed is not null, "HANDOFF_REJECTED");
                output["account"] = Status(platform, challenge.Committed!);
            }
            if (result.Status is "authenticated" or "expired" or "refused")
                lock (gate) if (ReferenceEquals(states[platform].Challenge, challenge)) states[platform].Challenge = null;
            return output;
        }
        catch
        {
            lock (gate) if (ReferenceEquals(states[platform].Challenge, challenge)) states[platform].Challenge = null;
            if (op.Admitted) throw new MusicAccountException("HANDOFF_UNCONFIRMED");
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
            if (closed || states[platform].Revoked || states[platform].Uncertain || states[platform].Active is not null) return null;
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
                if (state.Revoked || state.Uncertain || state.Active is not null) return null;
                if (state.Current is not null && !Same(state.Current, value) ||
                    state.Verified is not null && !Same(state.Verified, value))
                { state.Epoch++; state.Verified = null; changed = true; }
                // Observe a durable tombstone before returning anonymous. Otherwise
                // IsCurrent could still authorize a lease after another process logged out.
                state.Current = value;
                if (value.Cookie is not null)
                {
                    _ = AccountRules.Cookies(value.Cookie);
                    try { session = new(platform, value.Scope!, value.Generation, value.Profile!.UserId, value.Cookie); }
                    catch { throw new MusicAccountException("INVALID_CREDENTIALS"); }
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
                !state.Revoked && !state.Uncertain && state.Active is null && state.Current is { } value &&
                value.Generation == session.Generation && value.Scope == session.Scope &&
                value.Profile?.UserId == session.UserId && value.Cookie == session.Cookie;
    }

    /// <summary>Trusted content failure can revoke only the exact currently issued snapshot, never its replacement.</summary>
    public bool Revoke(MusicPlatformSession session)
    {
        lock (gate)
        {
            if (!IsCurrent(session)) return false;
            FenceLocked(session.Platform);
            states[session.Platform].Revoked = true;
        }
        Notify(session.Platform);
        return true;
    }

    /// <summary>Fences immediately, then drains physical storage work. Timeout/cancellation is not a successful close.</summary>
    public async Task PrepareCloseAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        InitialObservationFlight[] observations;
        lock (gate)
        {
            if (!closed)
            {
                closed = true;
                try { transferLifetime.Cancel(); } catch { /* Still fence every platform and drain physical work. */ }
                foreach (var platform in AccountRules.Platforms)
                {
                    // Even a broken protocol must not prevent fencing other platforms.
                    try { FenceLocked(platform); } catch { states[platform].Revoked = true; }
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
        if (ownsDetailsCacheStore) await detailsCacheStore.DisposeAsync().ConfigureAwait(false);
        await protocol.DisposeAsync().ConfigureAwait(false);
        transferLifetime.Dispose();
        clients.ContextChanged -= ClientContextChanged;
        qqContextSerial.Dispose();
        // The App owns the shared DPAPI store and disposes it only after every owner drained.
    }
}
