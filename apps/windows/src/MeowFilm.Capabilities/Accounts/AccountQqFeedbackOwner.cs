// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner : IMusicQqFeedbackContextSource
{
    private readonly SemaphoreSlim qqContextSerial = new(1, 1);
    private readonly HashSet<QqContextAcquisition> qqAcquisitions = [];
    private readonly HashSet<QqContextLease> qqContexts = [];
    private StoredQqFeedbackDevice? qqObservedDevice;

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
        internal bool Admitted;
        internal void Abort() { try { Cancellation.Cancel(); } catch (AggregateException) { } }
        public void Dispose() { Cancellation.Dispose(); Deadline.Dispose(); }
        public override string ToString() => "QqContextAcquisition (redacted)";
    }

    private bool QqAccountCurrentLocked(MusicPlatformSession account, bool ownWrite = false)
    {
        var state = states["tx"];
        return !closed && account.Platform == "tx" && leases.TryGetValue(account, out var issued) &&
            issued.Epoch == state.Epoch && !state.Revoked && (!state.Uncertain || ownWrite) &&
            state.Active is null && state.Current is { } current && current.Generation == account.Generation &&
            current.Scope == account.Scope && current.Profile?.UserId == account.UserId && current.Cookie == account.Cookie;
    }
    private void QqGuard(QqContextAcquisition op)
    {
        op.Token.ThrowIfCancellationRequested();
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            AccountRules.Require(!states["tx"].Uncertain || op.Admitted, "COMMIT_UNKNOWN");
            AccountRules.Require(QqAccountCurrentLocked(op.Account, op.Admitted), "SESSION_CHANGED");
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
                if (!QqAccountCurrentLocked(op.Account, op.Admitted)) op.Abort();
            foreach (var lease in qqContexts.ToArray())
                if (!QqAccountCurrentLocked(lease.Account)) lease.Abort();
        }
    }
    private StoredQqFeedbackDevice ObserveQqDocument(AccountDocument document)
    {
        try
        {
            var device = document.GetQqFeedbackDevice();
            lock (gate)
            {
                if (qqObservedDevice is not null && !StoredQqFeedbackDevice.Same(qqObservedDevice, device))
                    foreach (var lease in qqContexts.ToArray()) lease.Abort();
                qqObservedDevice = device;
            }
            return device;
        }
        catch
        {
            InvalidateQqDocument();
            throw;
        }
    }
    private void InvalidateQqDocument()
    {
        lock (gate)
        {
            qqObservedDevice = null;
            foreach (var lease in qqContexts.ToArray()) lease.Abort();
        }
    }
    private async Task<StoredQqFeedbackDevice> ReadQqDevice(QqContextAcquisition op)
    {
        try
        {
            return await DiskAsync(async () =>
            {
                QqGuard(op);
                var document = await ReadDocument(op.Token).ConfigureAwait(false);
                QqGuard(op);
                CheckQqAccount(document, op.Account);
                return ObserveQqDocument(document);
            }, op.Token).ConfigureAwait(false);
        }
        catch (MusicAccountException error) when (error.Code == "CORRUPT_STORAGE")
        {
            InvalidateQqDocument();
            throw;
        }
    }

    private async Task<StoredQqFeedbackDevice> SaveQqDevice(QqContextAcquisition op,
        StoredQqFeedbackDevice expected, StoredQqFeedbackDevice next)
    {
        // Unlike UI command receipts, this capability MUST await the physical CAS even
        // after cancellation. No following network step can outrun an unknown save.
        return await DiskAsync(async () =>
        {
            QqGuard(op);
            var document = await ReadDocument(op.Token).ConfigureAwait(false);
            QqGuard(op); CheckQqAccount(document, op.Account);
            var actualDevice = ObserveQqDocument(document);
            AccountRules.Require(StoredQqFeedbackDevice.Same(actualDevice, expected), "STORAGE_CONFLICT");
            byte[] bytes = document.ReplaceQqFeedbackDevice(expected, next with { Generation = expected.Generation + 1 });
            try
            {
                lock (gate)
                {
                    QqGuard(op);
                    foreach (var lease in qqContexts.ToArray()) lease.Abort();
                    states["tx"].Uncertain = true; op.Admitted = true;
                }
                try
                {
                    var receipt = await store.WriteAsync(DocumentNamespace, bytes, document.Fingerprint, op.Token).ConfigureAwait(false);
                    AccountRules.Require(receipt == DocumentFingerprint.FromBytes(bytes), "COMMIT_UNKNOWN");
                }
                catch (ProtectedDocumentException error) when (error.Error == DocumentStoreError.Conflict)
                {
                    lock (gate)
                    {
                        op.Admitted = false;
                        if (QqAccountCurrentLocked(op.Account, ownWrite: true)) states["tx"].Uncertain = false;
                    }
                    throw new MusicAccountException("STORAGE_CONFLICT");
                }
                catch { throw new MusicAccountException("COMMIT_UNKNOWN"); }
                lock (gate)
                {
                    // An ACK arriving after cancellation/change does not restore authority.
                    QqGuard(op);
                    states["tx"].Uncertain = false; op.Admitted = false;
                    return ObserveQqDocument(document);
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, op.Token).ConfigureAwait(false);
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
                AccountRules.Require(qqContexts.Count < 32, "RATE_LIMITED");
                op = new(exactAccount!, token, deviceProtocol.QqFeedbackClock);
                qqAcquisitions.Add(op);
            }
            await qqContextSerial.WaitAsync(op.Token).ConfigureAwait(false); serial = true;
            QqGuard(op);
            var device = await ReadQqDevice(op).ConfigureAwait(false);
            QqGuard(op);
            // Always live, including a warm cache and Finish reacquisition. Saved profile,
            // anonymous GetSession and successful QIMEI are NEVER authentication proof.
            var profile = AccountRules.Profile(await deviceProtocol.VerifyQqFeedbackAccountAsync(
                exactAccount!, op.Token).ConfigureAwait(false), "tx", exactAccount!.Cookie);
            QqGuard(op);
            AccountRules.Require(profile.UserId == exactAccount.UserId, "IDENTITY_MISMATCH");
            if (device.Generation == 0)
                device = await SaveQqDevice(op, device, StoredQqFeedbackDevice.Create()).ConfigureAwait(false);
            QqGuard(op);
            if (!device.QimeiCurrent(deviceProtocol.QqFeedbackClock.GetUtcNow()))
            {
                var qimei = await deviceProtocol.RequestQqQimeiAsync(device, op.Token).ConfigureAwait(false);
                QqGuard(op);
                AccountRules.Require(qimei.Generation == (device.Qimei?.Generation ?? 0) + 1, "INVALID_RESPONSE");
                device = await SaveQqDevice(op, device, device with { Qimei = qimei }).ConfigureAwait(false);
            }
            QqGuard(op);
            if (!device.SessionCurrent(deviceProtocol.QqFeedbackClock.GetUtcNow()))
            {
                var session = await deviceProtocol.RequestQqDeviceSessionAsync(device, op.Token).ConfigureAwait(false);
                QqGuard(op);
                AccountRules.Require(session.Generation == (device.Session?.Generation ?? 0) + 1 &&
                    session.QimeiGeneration == device.Qimei!.Generation, "INVALID_RESPONSE");
                device = await SaveQqDevice(op, device, device with { Session = session }).ConfigureAwait(false);
            }
            // Reobserve account/device after the network and CAS, including other owners.
            var observed = await ReadQqDevice(op).ConfigureAwait(false);
            QqGuard(op);
            AccountRules.Require(StoredQqFeedbackDevice.Same(device, observed), "STORAGE_CONFLICT");
            lock (gate)
            {
                QqGuard(op);
                if (!device.SessionCurrent(deviceProtocol.QqFeedbackClock.GetUtcNow()))
                    throw new MusicPlatformException("DEVICE_CONTEXT_EXPIRED");
                AccountRules.Require(qqContexts.Count < 32, "RATE_LIMITED");
                var lease = new QqContextLease(this, exactAccount, device, deviceProtocol.QqFeedbackClock, token);
                qqContexts.Add(lease);
                return lease;
            }
        }
        catch (Exception error)
        {
            // Unknown admission outranks cancellation: recovery is explicit list/read only.
            if (op?.Admitted == true) throw new MusicPlatformException("COMMIT_UNKNOWN");
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
        private readonly StoredQqFeedbackDevice stored;
        private readonly TimeProvider clock;
        private readonly CancellationTokenSource cancellation;
        private readonly ITimer? timer;
        private bool disposed;
        internal QqContextLease(MusicAccountOwner owner, MusicPlatformSession account, StoredQqFeedbackDevice stored,
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
                        owner.qqObservedDevice is { } observed && StoredQqFeedbackDevice.Same(observed, stored) &&
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
