// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner : IMusicQqFeedbackContextSource
{
    private readonly SemaphoreSlim qqContextSerial = new(1, 1);
    private readonly HashSet<QqContextAcquisition> qqAcquisitions = [];
    private readonly HashSet<QqContextLease> qqContexts = [];

    private sealed class QqContextAcquisition : IDisposable
    {
        internal QqContextAcquisition(MusicPlatformSession account, CancellationToken caller, TimeProvider clock)
        {
            Account = account;
            Deadline = new(TimeSpan.FromSeconds(25), clock);
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, Deadline.Token);
            Token = Cancellation.Token;
        }
        internal MusicPlatformSession Account { get; }
        internal CancellationTokenSource Deadline { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal CancellationToken Token { get; }
        internal void Abort() { try { Cancellation.Cancel(); } catch (AggregateException) { } }
        public void Dispose() { Cancellation.Dispose(); Deadline.Dispose(); }
        public override string ToString() => "QqContextAcquisition (redacted)";
    }

    private bool QqAccountCurrentLocked(MusicPlatformSession account)
    {
        var state = states["tx"];
        return !closed && account.Platform == "tx" && leases.TryGetValue(account, out var issued) &&
            issued.Epoch == state.Epoch && !state.Revoked && !state.Uncertain &&
            state.Active is null && state.Current is { } current && current.Generation == account.Generation &&
            current.Scope == account.Scope && current.Profile?.UserId == account.UserId && current.Cookie == account.Cookie;
    }
    private void QqGuard(QqContextAcquisition op)
    {
        op.Token.ThrowIfCancellationRequested();
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            AccountRules.Require(!states["tx"].Uncertain, "COMMIT_UNKNOWN");
            AccountRules.Require(QqAccountCurrentLocked(op.Account), "SESSION_CHANGED");
        }
    }
    private static void CheckQqAccount(AccountDocument document, MusicPlatformSession account)
    {
        var value = document.Get("tx");
        AccountRules.Require(value.Generation == account.Generation && value.Scope == account.Scope &&
            value.Profile?.UserId == account.UserId && value.Cookie == account.Cookie, "SESSION_CHANGED");
    }
    // Called under the existing owner gate; no new writer or account/session event bus.
    private void FenceQqFeedbackLocked()
    {
        foreach (var op in qqAcquisitions.ToArray()) op.Abort();
        foreach (var lease in qqContexts.ToArray()) lease.Abort();
    }
    private void ObserveQqAccountChange()
    {
        lock (gate)
        {
            foreach (var op in qqAcquisitions.ToArray())
                if (!QqAccountCurrentLocked(op.Account)) op.Abort();
            foreach (var lease in qqContexts.ToArray())
                if (!QqAccountCurrentLocked(lease.Account)) lease.Abort();
        }
    }
    private void ClientContextChanged(string platform)
    {
        if (platform != "tx") return;
        lock (gate) foreach (var lease in qqContexts.ToArray()) lease.Abort();
    }
    private async Task ReadQqAccount(QqContextAcquisition op)
    {
        try
        {
            await DiskAsync(async () =>
            {
                QqGuard(op);
                var document = await ReadDocument(op.Token).ConfigureAwait(false);
                QqGuard(op); CheckQqAccount(document, op.Account);
                return true;
            }, op.Token).ConfigureAwait(false);
        }
        catch (MusicAccountException error) when (error.Code == "CORRUPT_STORAGE")
        {
            lock (gate) foreach (var lease in qqContexts.ToArray()) lease.Abort();
            throw;
        }
    }

    async ValueTask<IMusicQqFeedbackContextLease> IMusicQqFeedbackContextSource.AcquireForAccountAsync(
        MusicPlatformSession exactAccount, CancellationToken token)
    {
        bool entered = false, serial = false;
        QqContextAcquisition? op = null;
        try
        {
            token.ThrowIfCancellationRequested();
            AccountRules.Require(exactAccount is not null && exactAccount.Platform == "tx", "INVALID_REQUEST");
            var deviceProtocol = protocol;
            Enter(); entered = true;
            lock (gate)
            {
                AccountRules.Require(!states["tx"].Uncertain, "COMMIT_UNKNOWN");
                AccountRules.Require(QqAccountCurrentLocked(exactAccount!), "SESSION_CHANGED");
                op = new(exactAccount!, token, clientProtocol.Clock);
                qqAcquisitions.Add(op);
            }
            await qqContextSerial.WaitAsync(op.Token).ConfigureAwait(false); serial = true;
            QqGuard(op);
            await ReadQqAccount(op).ConfigureAwait(false);
            QqGuard(op);
            await clients.InitializeAsync(op.Token).ConfigureAwait(false);
            QqGuard(op);
            // Always live, including a warm cache and Finish reacquisition. Saved profile,
            // anonymous GetSession and successful QIMEI are NEVER authentication proof.
            var profile = AccountRules.Profile(await deviceProtocol.VerifyQqFeedbackAccountAsync(
                exactAccount!, op.Token).ConfigureAwait(false), "tx", exactAccount!.Cookie);
            QqGuard(op);
            AccountRules.Require(profile.UserId == exactAccount.UserId, "IDENTITY_MISMATCH");
            // Anonymous client initialization/renewal is independently owned;
            // no account document write or login proof is involved in it.
            var device = await clients.EnsureQqAsync(clientProtocol, op.Token).ConfigureAwait(false);
            QqGuard(op);
            await ReadQqAccount(op).ConfigureAwait(false);
            QqGuard(op);
            AccountRules.Require(clients.IsCurrent(device), "STORAGE_CONFLICT");
            lock (gate)
            {
                QqGuard(op);
                if (!device.SessionCurrent(clientProtocol.Clock.GetUtcNow()))
                    throw new MusicPlatformException("DEVICE_CONTEXT_EXPIRED");
                var lease = new QqContextLease(this, exactAccount, device, clientProtocol.Clock, token);
                qqContexts.Add(lease);
                return lease;
            }
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (error is MusicPlatformException) throw;
            string code = AccountRules.Error(error);
            if (op is not null)
            {
                bool timedOut = op.Deadline.IsCancellationRequested, cancelled = op.Token.IsCancellationRequested;
                if (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "FORBIDDEN" or "IDENTITY_MISMATCH" or "SESSION_CHANGED")
                    Revoke(op.Account); // This cannot revoke a replacement or an unissued object.
                if (timedOut) code = "TIMEOUT";
                else if (cancelled) code = closed ? "CLOSED" : "SESSION_CHANGED";
            }
            throw new MusicPlatformException(code switch {
                "AUTH_EXPIRED" => "AUTH_REQUIRED", "STALE_SESSION" or "SUPERSEDED" => "SESSION_CHANGED",
                _ => code });
        }
        finally
        {
            if (serial) qqContextSerial.Release();
            if (op is not null) { lock (gate) qqAcquisitions.Remove(op); op.Dispose(); }
            if (entered) Leave();
        }
    }

    [DebuggerDisplay("MusicQqFeedbackContextLease (redacted)")]
    private sealed class QqContextLease : IMusicQqFeedbackContextLease
    {
        private readonly MusicAccountOwner owner;
        private readonly MusicQqClientContext stored;
        private readonly TimeProvider clock;
        private readonly CancellationTokenSource cancellation;
        private readonly ITimer? timer;
        private bool disposed;
        internal QqContextLease(MusicAccountOwner owner, MusicPlatformSession account, MusicQqClientContext stored,
            TimeProvider clock, CancellationToken caller)
        {
            this.owner = owner; this.stored = stored; this.clock = clock; Account = account;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller);
            Revoked = cancellation.Token;
            Device = new(stored.InstallationId, stored.Generation, stored.Session!.Generation, stored.OpenUdid, stored.OpenUdid2,
                stored.AndroidId, "15", "V2408A", stored.Qimei!.Q36, stored.Qimei.SavedAt,
                stored.Session.Uid, stored.Session.Sid, stored.Session.SavedAt);
            var now = clock.GetUtcNow();
            var midnight = now.LocalDateTime.Date.AddDays(1);
            // Windows time zone rules are also used by SessionCurrent. Do not perpetuate
            // a session through a skipped local-midnight DST transition.
            while (TimeZoneInfo.Local.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
            var nextDay = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight)).ToUniversalTime();
            var qimeiExpiry = DateTimeOffset.FromUnixTimeMilliseconds(stored.Qimei.SavedAt).AddDays(1);
            var expiry = nextDay < qimeiExpiry ? nextDay : qimeiExpiry;
            timer = clock.CreateTimer(_ => Abort(), null, expiry > now ? expiry - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            if (Revoked.IsCancellationRequested) timer.Dispose();
        }
        [JsonIgnore] public MusicPlatformSession Account { get; }
        [JsonIgnore] public MusicQqFeedbackDeviceSnapshot Device { get; }
        [JsonIgnore] public CancellationToken Revoked { get; }
        [JsonIgnore] public bool IsCurrent
        {
            get
            {
                lock (owner.gate)
                {
                    if (!disposed && !Revoked.IsCancellationRequested && owner.QqAccountCurrentLocked(Account) &&
                        owner.clients.IsCurrent(stored) &&
                        stored.SessionCurrent(clock.GetUtcNow())) return true;
                    Abort(); return false;
                }
            }
        }
        internal void Abort()
        {
            try { cancellation.Cancel(); } catch (AggregateException) { } catch (ObjectDisposedException) { }
            timer?.Dispose();
        }
        public ValueTask DisposeAsync()
        {
            lock (owner.gate)
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true; owner.qqContexts.Remove(this);
            }
            Abort(); timer?.Dispose(); cancellation.Dispose();
            return ValueTask.CompletedTask;
        }
        public override string ToString() => "MusicQqFeedbackContextLease (redacted)";
    }
}
