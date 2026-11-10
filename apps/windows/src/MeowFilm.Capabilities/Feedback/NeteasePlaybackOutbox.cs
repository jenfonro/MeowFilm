using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>
/// One borrowed native document, one serial uploader. Local admission is not a
/// server receipt. Nothing marked inflight is ever recovered as retryable work.
/// </summary>
internal sealed class NeteasePlaybackOutbox(IDocumentStore store, MusicFeedbackSettingsOwner settings,
    IMusicPlatformSessionSource accounts, NeteasePlaybackTransport transport, TimeProvider clock) : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim disk = new(1, 1);
    private readonly Dictionary<Guid, NeteasePlaybackPending> entries = [];
    private readonly Dictionary<Guid, CancellationToken> live = [];
    private readonly HashSet<Task> work = [];
    private readonly HashSet<Task> localWork = [];
    private CancellationTokenSource network = new();
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private bool loaded, suspended, closed, draining, requested, loadingPreferences, pendingAccountChange;
    private string? storageError, uploadError;
    private Task? disposal;
    internal string? LastError { get { lock (sync) return storageError ?? uploadError; } }
    internal Task QualifyAccountAsync(MusicPlatformSession account, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (sync)
        {
            FeedbackProtocolRules.Require(!closed && !suspended, "CLOSED");
            var request = CancellationTokenSource.CreateLinkedTokenSource(token, network.Token);
            var task = QualifyAsync();
            Track(task, remote: true);
            return task.WaitAsync(token);
            async Task QualifyAsync()
            {
                try { await transport.QualifyAccountAsync(account, request.Token).ConfigureAwait(false); }
                finally { request.Dispose(); }
            }
        }
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        await settings.ReadAsync(token: token).ConfigureAwait(false);
        await ReconcileAsync(dropAnonymous: false, token).ConfigureAwait(false);
        RequestDrain();
    }

    internal async Task AdmitAsync(MusicPlatformSession account, NeteasePlaybackPending value, CancellationToken token)
    {
        await disk.WaitAsync(token).ConfigureAwait(false);
        try
        {
            GuardLocal(account, token, preserveConsent: true);
            await LoadAsync().ConfigureAwait(false);
            GuardStorage();
            long revision = settings.WyConsentRevision;
            FeedbackProtocolRules.Require(value.ConsentRevision == revision || settings.PreservesWyConsent, "SESSION_CHANGED");
            value = value with { ConsentRevision = revision };
            FeedbackProtocolRules.Require(!entries.ContainsKey(value.PlaybackId), "INSTANCE_MISMATCH");
            FeedbackProtocolRules.Require(NeteasePlaybackOutboxDocument.Encode([value]).Length <=
                NeteasePlaybackOutboxDocument.MaximumEventBytes, "LIMIT");
            entries.Add(value.PlaybackId, value); live[value.PlaybackId] = token;
            Trim();
            FeedbackProtocolRules.Require(entries.ContainsKey(value.PlaybackId), "LIMIT");
            await SaveAsync().ConfigureAwait(false);
        }
        finally { disk.Release(); }
        RequestDrain();
    }

    internal async Task EndAsync(MusicPlatformSession account, Guid playbackId, NeteasePlaybackEvent end, CancellationToken token)
    {
        await disk.WaitAsync(token).ConfigureAwait(false);
        try
        {
            GuardLocal(account, token, preserveConsent: true); GuardStorage();
            FeedbackProtocolRules.Require(entries.TryGetValue(playbackId, out var before) &&
                before.Account.Matches(account) && (before.Start is not null || before.StartConfirmed), "START_UNCONFIRMED");
            FeedbackProtocolRules.Require(before.End is null, "INSTANCE_ENDED");
            var after = before with { End = end };
            FeedbackProtocolRules.Require(NeteasePlaybackOutboxDocument.Encode([after]).Length <=
                NeteasePlaybackOutboxDocument.MaximumEventBytes, "LIMIT");
            entries[playbackId] = after;
            Trim();
            await SaveAsync().ConfigureAwait(false);
        }
        finally { disk.Release(); }
        RequestDrain();
    }

    // The original playback token is an immediate network fence; this task
    // removes its persisted unsent facts after any already admitted CAS settles.
    internal void Revoke(Guid playbackId) => Schedule(async () =>
    {
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            await LoadAsync().ConfigureAwait(false);
            if (entries.Remove(playbackId)) await SaveAsync().ConfigureAwait(false);
            live.Remove(playbackId);
        }
        finally { disk.Release(); }
    });

    internal void Release(Guid playbackId) => Schedule(async () =>
    {
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            live.Remove(playbackId);
            if (entries.TryGetValue(playbackId, out var value) && value.Count == 0) entries.Remove(playbackId);
        }
        finally { disk.Release(); }
    });

    internal void AuthorizationChanged(bool accountChanged)
    {
        lock (sync)
        {
            if (closed) return;
            pendingAccountChange |= accountChanged;
            if (accountChanged || !settings.IsEnabled("wy")) FenceNetwork();
            if (!settings.IsConfirmed && !settings.RequiresReconciliation)
            {
                // An account-ready notification can precede the first settings
                // read. Unknown startup preferences are not disabled consent.
                if (loadingPreferences) return;
                loadingPreferences = true;
                Schedule(async () =>
                {
                    try { await settings.ReadAsync().ConfigureAwait(false); }
                    finally { lock (sync) loadingPreferences = false; }
                    AuthorizationChanged(accountChanged: false);
                });
                return;
            }
            accountChanged = pendingAccountChange; pendingAccountChange = false;
            bool carryConsent = !accountChanged && settings.PreservesWyConsent;
            bool forceClear = !accountChanged && !settings.IsEnabled("wy") && !carryConsent;
            // Register for the local lane now. Re-enabling or a new account/event
            // cannot overtake the revocation just because a Task.Run starts later.
            var admission = disk.WaitAsync();
            Schedule(async () =>
            {
                await admission.ConfigureAwait(false);
                try { await ReconcileLockedAsync(accountChanged, CancellationToken.None, carryConsent, forceClear).ConfigureAwait(false); }
                finally { disk.Release(); }
                RequestDrain();
            });
        }
    }

    private async Task ReconcileAsync(bool dropAnonymous, CancellationToken token)
    {
        await disk.WaitAsync(token).ConfigureAwait(false);
        try { await ReconcileLockedAsync(dropAnonymous, token).ConfigureAwait(false); }
        finally { disk.Release(); }
    }
    private async Task ReconcileLockedAsync(bool dropAnonymous, CancellationToken token,
        bool carryConsent = false, bool forceClear = false)
    {
        if (forceClear)
        {
            await LoadAsync().ConfigureAwait(false);
            if (entries.Count > 0) { entries.Clear(); await SaveAsync().ConfigureAwait(false); }
            return;
        }
        if (carryConsent && !settings.IsConfirmed) return; // CAS still owns its unknown receipt.
        // Startup may run before an account is ready. Absence then defers work;
        // an explicit logout/change notification authorizes its removal.
        var account = settings.IsEnabled("wy") ? await accounts.AcquireAsync("wy", token).ConfigureAwait(false) : null;
        await LoadAsync().ConfigureAwait(false);
        bool changed = Trim();
        foreach (var item in entries.Values.ToArray())
        {
            if (!settings.IsEnabled("wy") || !carryConsent && item.ConsentRevision != settings.ConfirmedRevision ||
                (account is null ? dropAnonymous : !item.Account.Matches(account) || !accounts.IsCurrent(account)))
            { entries.Remove(item.PlaybackId); changed = true; }
            else if (carryConsent && item.ConsentRevision != settings.ConfirmedRevision)
            { entries[item.PlaybackId] = item with { ConsentRevision = settings.ConfirmedRevision }; changed = true; }
        }
        if (changed) await SaveAsync().ConfigureAwait(false);
    }

    private void RequestDrain()
    {
        lock (sync)
        {
            if (closed || suspended || storageError is not null || !settings.IsEnabled("wy")) return;
            requested = true;
            if (draining) return;
            draining = true;
            Schedule(async () =>
            {
                try
                {
                    for (;;)
                    {
                        CancellationToken token;
                        lock (sync)
                        {
                            if (!requested || suspended || closed || storageError is not null) return;
                            requested = false; token = network.Token;
                        }
                        await DrainOnceAsync(token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    lock (sync)
                    {
                        draining = false;
                        // Only a real admission/authorization event sets requested.
                        if (requested) RequestDrain();
                    }
                }
            }, remote: true);
        }
    }

    private async Task DrainOnceAsync(CancellationToken token)
    {
        Guid[] batch;
        await disk.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync().ConfigureAwait(false); GuardStorage();
            if (Trim()) await SaveAsync().ConfigureAwait(false);
            batch = entries.Values.Where(e => e.Count > 0).OrderBy(e => e.Oldest).Select(e => e.PlaybackId).ToArray();
        }
        finally { disk.Release(); }
        if (batch.Length == 0 || !settings.IsEnabled("wy")) return;
        var account = await accounts.AcquireAsync("wy", token).ConfigureAwait(false);
        if (account is null || !accounts.IsCurrent(account)) return;
        foreach (var id in batch)
        {
            // Each snapshot contains at most one start and one end. New events
            // request another bounded pass; they do not create a retry timer.
            for (int phase = 0; phase < 2; phase++)
                if (!await SendNextAsync(account, id, token).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> SendNextAsync(MusicPlatformSession account, Guid id, CancellationToken networkToken)
    {
        NeteasePlaybackPending value;
        NeteasePlaybackEvent next;
        CancellationToken playbackToken;
        await disk.WaitAsync(networkToken).ConfigureAwait(false);
        try
        {
            if (!entries.TryGetValue(id, out value!) || value.Count == 0) return true;
            if (!value.Account.Matches(account))
            {
                entries.Remove(id); await SaveAsync().ConfigureAwait(false); return true;
            }
            if (!settings.IsEnabled("wy")) return false;
            if (value.ConsentRevision != settings.ConfirmedRevision)
            {
                if (settings.PreservesWyConsent) return false; // The queued local CAS reconciliation owns the revision.
                entries.Remove(id); await SaveAsync().ConfigureAwait(false); return true;
            }
            next = value.Start ?? value.End!;
            playbackToken = live.GetValueOrDefault(id);
        }
        finally { disk.Release(); }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(networkToken, playbackToken);
        bool marked = false;
        try
        {
            request.Token.ThrowIfCancellationRequested();
            if (!settings.IsEnabled("wy")) return false;
            GuardLocal(account, request.Token);
            FeedbackProtocolRules.Require(value.ConsentRevision == settings.ConfirmedRevision, "SESSION_CHANGED");
            // The account read and local encoding precede the durable
            // send boundary. A failure here proves the playback file was unsent.
            using var upload = await transport.PrepareAsync(account, value, next, request.Token).ConfigureAwait(false);
            await disk.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                request.Token.ThrowIfCancellationRequested();
                if (!settings.IsEnabled("wy")) return false;
                GuardLocal(account, request.Token); GuardStorage();
                if (!entries.TryGetValue(id, out var current) || (current.Start ?? current.End)?.Id != next.Id) return true;
                if (current.ConsentRevision != value.ConsentRevision ||
                    current.ConsentRevision != settings.ConfirmedRevision) return false;
                var inflight = next with { Inflight = true };
                entries[id] = current.Start is not null ? current with { Start = inflight } : current with { End = inflight };
                await SaveAsync().ConfigureAwait(false); // No upload if the durable fence is uncertain.
                marked = true;
            }
            finally { disk.Release(); }
            await upload.SendAsync(request.Token).ConfigureAwait(false);
            GuardLocal(account, request.Token);
            await disk.WaitAsync().ConfigureAwait(false);
            try
            {
                if (entries.TryGetValue(id, out var current))
                {
                    if (current.Start?.Id == next.Id)
                    {
                        if (current.End is null && !live.ContainsKey(id)) entries.Remove(id);
                        else entries[id] = current with { Start = null, StartConfirmed = true };
                    }
                    else if (current.End?.Id == next.Id) entries.Remove(id);
                    await SaveAsync().ConfigureAwait(false);
                }
            }
            finally { disk.Release(); }
            lock (sync) uploadError = null;
            return true;
        }
        catch (Exception error)
        {
            lock (sync) uploadError = error is OperationCanceledException ? null :
                error is MusicFeedbackException feedback ? feedback.Code : FeedbackProtocolRules.SafeError(error);
            if (marked || error is MusicFeedbackException { Code: "AUTH_EXPIRED" or "IDENTITY_MISMATCH" or "LIMIT" } ||
                error is MusicFeedbackException { Code: "SESSION_CHANGED" } && !accounts.IsCurrent(account))
            {
                // Includes missing/wrong filename, lost receipt, cancellation
                // after send admission, malformed response and HTTP failure.
                // Never return any of these to pending, nor send an orphan end.
                await disk.WaitAsync().ConfigureAwait(false);
                try { if (entries.Remove(id)) await SaveAsync().ConfigureAwait(false); }
                finally { disk.Release(); }
            }
            return false;
        }
    }

    private async Task LoadAsync()
    {
        GuardStorage();
        if (loaded) return;
        try
        {
            var snapshot = await store.ReadAsync(NeteasePlaybackOutboxDocument.Namespace).ConfigureAwait(false);
            var saved = NeteasePlaybackOutboxDocument.Decode(snapshot);
            fingerprint = snapshot.Fingerprint;
            entries.Clear();
            foreach (var value in saved) entries.Add(value.PlaybackId, value);
            loaded = true;
            // A crash after the durable send fence is indistinguishable from a
            // lost ACK. Drop the complete pair; no presumed upstream idempotence.
            bool discarded = false;
            foreach (var value in entries.Values.Where(v => v.Start?.Inflight == true || v.End?.Inflight == true).ToArray())
            { entries.Remove(value.PlaybackId); discarded = true; }
            if (Trim() || discarded) await SaveAsync().ConfigureAwait(false);
        }
        catch
        {
            lock (sync) storageError = "FEEDBACK_STORAGE_FAILED";
            throw new MusicFeedbackException("FEEDBACK_STORAGE_FAILED");
        }
    }

    private bool Trim()
    {
        bool changed = false;
        long oldest = (clock.GetUtcNow() - NeteasePlaybackOutboxDocument.Retention).ToUnixTimeMilliseconds();
        foreach (var value in entries.Values.ToArray())
            if (value.Count > 0 && value.Oldest < oldest && value.Start?.Inflight != true && value.End?.Inflight != true)
            { entries.Remove(value.PlaybackId); live.Remove(value.PlaybackId); changed = true; }
        while (entries.Values.Sum(e => e.Count) > NeteasePlaybackOutboxDocument.MaximumEvents ||
            NeteasePlaybackOutboxDocument.Encode(entries.Values).Length > NeteasePlaybackOutboxDocument.MaximumBytes)
        {
            var first = entries.Values.Where(e => e.Count > 0 && e.Start?.Inflight != true && e.End?.Inflight != true)
                .MinBy(e => e.Oldest);
            FeedbackProtocolRules.Require(first is not null, "LIMIT");
            entries.Remove(first.PlaybackId); live.Remove(first.PlaybackId); changed = true;
        }
        return changed;
    }

    private async Task SaveAsync()
    {
        GuardStorage();
        try
        {
            var data = NeteasePlaybackOutboxDocument.Encode(entries.Values);
            var expected = DocumentFingerprint.FromBytes(data);
            var receipt = await store.WriteAsync(NeteasePlaybackOutboxDocument.Namespace, data, fingerprint,
                CancellationToken.None).ConfigureAwait(false);
            FeedbackProtocolRules.Require(receipt == expected, "COMMIT_UNKNOWN");
            fingerprint = receipt;
        }
        catch
        {
            // No blind overwrite or automatic reconciliation after an uncertain
            // CAS. A new process can safely reload pending/inflight truth.
            lock (sync) storageError = "FEEDBACK_STORAGE_FAILED";
            throw new MusicFeedbackException("FEEDBACK_STORAGE_FAILED");
        }
    }

    private void GuardStorage()
    { lock (sync) FeedbackProtocolRules.Require(storageError is null, "FEEDBACK_STORAGE_FAILED"); }
    private void GuardLocal(MusicPlatformSession account, CancellationToken token, bool preserveConsent = false)
    {
        token.ThrowIfCancellationRequested();
        lock (sync) FeedbackProtocolRules.Require(!closed, "CLOSED");
        FeedbackProtocolRules.Require((settings.IsEnabled("wy") || preserveConsent && settings.PreservesWyConsent) &&
            accounts.IsCurrent(account), "SESSION_CHANGED");
    }
    private void FenceNetwork()
    {
        CancellationTokenSource previous;
        lock (sync)
        {
            if (closed) return;
            previous = network; network = new();
        }
        var cancelled = previous.CancelAsync(); // Revoke immediately, not when the cleanup task gets CPU.
        Schedule(async () => { await cancelled.ConfigureAwait(false); previous.Dispose(); }, remote: true);
    }
    private void Schedule(Func<Task> action, bool remote = false)
    {
        lock (sync)
        {
            if (closed) return;
            Track(Task.Run(action), remote);
        }
    }
    private void Track(Task task, bool remote)
    {
        lock (sync)
        {
            work.Add(task);
            if (!remote) localWork.Add(task);
            _ = task.ContinueWith(done =>
            {
                if (done.Exception is not null)
                    lock (sync) uploadError = "FEEDBACK_FAILED";
                lock (sync) { work.Remove(done); localWork.Remove(done); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal async Task DrainAsync(CancellationToken token = default)
    {
        for (;;)
        {
            Task[] pending; lock (sync) pending = work.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(false); }
            catch when (!token.IsCancellationRequested) { /* Fixed errors remain observable; no retry. */ }
        }
    }

    internal async Task PrepareShutdownAsync()
    {
        lock (sync) { suspended = true; requested = false; }
        FenceNetwork();
        await FlushLocalAsync().ConfigureAwait(false);
        await disk.WaitAsync().ConfigureAwait(false);
        try
        {
            // Network cleanup remains tracked by the transport. The local close
            // receipt waits only for durable facts and the no-replay fence, never
            // for a handler to return an ACK after cancellation.
            bool changed = false;
            foreach (var entry in entries.Values.Where(e => e.Start?.Inflight == true || e.End?.Inflight == true).ToArray())
            { entries.Remove(entry.PlaybackId); changed = true; }
            if (changed) await SaveAsync().ConfigureAwait(false);
        }
        finally { disk.Release(); }
        GuardStorage();
    }
    internal async Task FlushLocalAsync()
    {
        for (;;)
        {
            Task[] pending; lock (sync) pending = localWork.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Storage failure stays observable and blocks a successful close. */ }
        }
    }
    internal void Resume()
    {
        lock (sync) if (!closed) suspended = false;
        RequestDrain();
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            suspended = closed = true; requested = false;
            return new(disposal = DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await network.CancelAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        await disk.WaitAsync().ConfigureAwait(false);
        try { live.Clear(); entries.Clear(); }
        finally { disk.Release(); network.Dispose(); }
        GuardStorage();
    }
}
