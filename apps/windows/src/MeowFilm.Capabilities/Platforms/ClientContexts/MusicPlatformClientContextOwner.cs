// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Platforms;

// Sole client-state writer. Borrows storage; construction does not perform I/O.
internal sealed partial class MusicPlatformClientContextOwner : IAsyncDisposable
{
    internal const string DocumentNamespace = "music.platform-context.v1";
    private readonly IDocumentStore store;
    private readonly object gate = new();
    private readonly SerialOperationQueue updates = new();
    private CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> work = [];
    private MusicClientContextDocument? current;
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private Task? initialization, reload, preparation, disposal;
    private bool closed, stopping, uncertain, observed;
    internal event Action<string>? ContextChanged;
    internal bool NeedsReconciliation { get { lock (gate) return uncertain; } }

    internal MusicPlatformClientContextOwner(IDocumentStore store)
    { this.store = store ?? throw new ArgumentNullException(nameof(store)); }

    internal Task InitializeAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task task;
        lock (gate)
        {
            RequireOpen();
            MusicClientContextDocument.Require(!uncertain, "COMMIT_UNKNOWN");
            if (reload is { IsCompleted: false }) task = reload;
            else
            {
                if (current is not null) return Task.CompletedTask;
                if (initialization is null || initialization.IsCompleted)
                { initialization = LoadAsync(lifetime.Token, create: !observed); Track(initialization); }
                task = initialization;
            }
        }
        return task.WaitAsync(token);
    }
    internal async ValueTask<MusicPlatformClientContext> AcquireAsync(string platform, CancellationToken token = default)
    {
        MusicClientContextDocument.Require(platform is "wy" or "kg" or "tx", "INVALID_PLATFORM");
        await InitializeAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (gate) { RequireReady(); return Select(current!, platform); }
    }
    internal async ValueTask<MusicWyClientContext> AcquireWyAsync(CancellationToken token = default) =>
        (MusicWyClientContext)await AcquireAsync("wy", token).ConfigureAwait(false);
    internal async ValueTask<MusicKgClientContext> AcquireKgAsync(CancellationToken token = default) =>
        (MusicKgClientContext)await AcquireAsync("kg", token).ConfigureAwait(false);
    internal async ValueTask<MusicQqClientContext> AcquireQqAsync(CancellationToken token = default) =>
        (MusicQqClientContext)await AcquireAsync("tx", token).ConfigureAwait(false);
    internal bool IsCurrent(MusicPlatformClientContext snapshot)
    {
        lock (gate)
        {
            if (closed || stopping || uncertain || reload is { IsCompleted: false } || current is null) return false;
            var active = Select(current, snapshot.Platform);
            return ReferenceEquals(active, snapshot) || active is MusicWyClientContext wy && snapshot is MusicWyClientContext before &&
                ReferenceEquals(wy.Observation, before.Observation) && wy.DeviceId == before.DeviceId;
        }
    }
    private static MusicPlatformClientContext Select(MusicClientContextDocument document, string platform) =>
        platform switch { "wy" => document.Wy, "kg" => document.Kg, _ => document.Qq };

    // Explicit authoritative read, never a replay of an unconfirmed write.
    internal Task ReloadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task task;
        lock (gate)
        {
            RequireOpen();
            if (reload is null || reload.IsCompleted)
            { reload = LoadAsync(lifetime.Token, create: false); Track(reload); }
            task = reload;
        }
        return task.WaitAsync(token);
    }
    private void Track(Task task)
    {
        work.Add(task);
        _ = task.ContinueWith(done =>
        {
            _ = done.Exception;
            lock (gate) work.Remove(task);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private Task LoadAsync(CancellationToken token, bool create) => updates.Enqueue(async () =>
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        try
        {
            lock (gate)
            {
                RequireOpen();
                if (create && current is not null) return;
                current = null;
            }
            NotifyAll();
            var document = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data);
                token.ThrowIfCancellationRequested();
            }
            if (document.Data is null)
            {
                MusicClientContextDocument.Require(document.Fingerprint == DocumentFingerprint.Missing, "CORRUPT_STORAGE");
                lock (gate) { RequireOpen(); fingerprint = document.Fingerprint; uncertain = false; }
                MusicClientContextDocument.Require(create, "DEVICE_CONTEXT_REQUIRED");
                token.ThrowIfCancellationRequested();
                await WriteAsync(MusicClientContextDocument.Create(), document.Fingerprint, token).ConfigureAwait(false);
                return;
            }
            lock (gate) observed = true;
            var next = MusicClientContextDocument.Decode(document);
            lock (gate)
            { RequireOpen(); current = next; fingerprint = document.Fingerprint; uncertain = false; observed = true; }
        }
        catch (DocumentStoreException error) { throw new MusicPlatformException(StorageCode(error)); }
        catch (MusicPlatformException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new MusicPlatformException("STORAGE"); }
    });
    internal async Task<MusicKgClientContext> SaveKgAsync(MusicKgClientContext expected, string dfid,
        CancellationToken token = default) =>
        (await UpdateAsync(expected, document => document with
        { Kg = expected with { Generation = Next(expected.Generation), Dfid = dfid } }, token).ConfigureAwait(false)).Kg;
    internal async Task<MusicQqClientContext> SaveQqAsync(MusicQqClientContext expected,
        MusicClientQimei? qimei, MusicClientDeviceSession? session, CancellationToken token = default) =>
        (await UpdateAsync(expected, document => document with
        { Qq = expected with { Generation = Next(expected.Generation), Qimei = qimei, Session = session } }, token).ConfigureAwait(false)).Qq;
    private static long Next(long generation)
    {
        MusicClientContextDocument.Require(generation < 9007199254740991, "LIMIT");
        return generation + 1;
    }
    private Task<MusicClientContextDocument> UpdateAsync(MusicPlatformClientContext expected,
        Func<MusicClientContextDocument, MusicClientContextDocument> update, CancellationToken caller,
        Action? beforeCommit = null)
    {
        caller.ThrowIfCancellationRequested();
        Task<MusicClientContextDocument> task;
        lock (gate)
        {
            RequireReady();
            var generation = lifetime.Token;
            task = updates.Enqueue(() => ExecuteAsync(generation)); Track(task);
        }
        return task;
        async Task<MusicClientContextDocument> ExecuteAsync(CancellationToken generation)
        {
            await Task.Yield();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, generation);
            beforeCommit?.Invoke();
            linked.Token.ThrowIfCancellationRequested();
            MusicClientContextDocument next; DocumentFingerprint before;
            lock (gate)
            {
                RequireReady();
                MusicClientContextDocument.Require(IsCurrent(expected), "STORAGE_CONFLICT");
                next = update(current!); before = fingerprint;
                if (ReferenceEquals(next, current)) return current!;
            }
            await WriteAsync(next, before, linked.Token).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            lock (gate) { RequireReady(); return current!; }
        }
    }
    private async Task WriteAsync(MusicClientContextDocument next, DocumentFingerprint before, CancellationToken token)
    {
        byte[] bytes = next.Encode();
        try
        {
            MusicClientContextDocument.Require(bytes.Length <= MusicClientContextDocument.MaximumBytes, "LIMIT");
            var validated = MusicClientContextDocument.Decode(new(bytes.ToArray(), DocumentFingerprint.FromBytes(bytes)));
            token.ThrowIfCancellationRequested();
            lock (gate) { RequireOpen(); observed = true; }
            DocumentFingerprint actual;
            // Admission is final: a retiring read must not cancel its durable client
            // update and fence every later request behind an unknown receipt.
            try { actual = await store.WriteAsync(DocumentNamespace, bytes, before, CancellationToken.None).ConfigureAwait(false); }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict)
            {
                lock (gate) { current = null; initialization = null; }
                NotifyAll();
                throw new MusicPlatformException("STORAGE_CONFLICT");
            }
            catch
            {
                lock (gate) uncertain = true;
                NotifyAll();
                throw new MusicPlatformException("COMMIT_UNKNOWN");
            }
            if (actual != DocumentFingerprint.FromBytes(bytes))
            {
                lock (gate) uncertain = true;
                NotifyAll();
                throw new MusicPlatformException("COMMIT_UNKNOWN");
            }
            string[] changed;
            lock (gate)
            {
                // Retain unaffected platform snapshots across another platform's CAS.
                var previous = current;
                changed = new[] { "wy", "kg", "tx" }.Where(p =>
                    previous is null || !ReferenceEquals(Select(previous, p), Select(next, p))).ToArray();
                current = new(previous is not null && ReferenceEquals(previous.Wy, next.Wy) ? previous.Wy :
                    validated.Wy with { Observation = next.Wy.Observation },
                    previous is not null && ReferenceEquals(previous.Kg, next.Kg) ? previous.Kg : validated.Kg,
                    previous is not null && ReferenceEquals(previous.Qq, next.Qq) ? previous.Qq : validated.Qq);
                fingerprint = actual; uncertain = false;
            }
            foreach (var platform in changed) Notify(platform);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void NotifyAll() { foreach (var platform in new[] { "wy", "kg", "tx" }) Notify(platform); }
    private void Notify(string platform)
    {
        foreach (Action<string> listener in ContextChanged?.GetInvocationList() ?? [])
            try { listener(platform); } catch { /* Observers cannot alter a durable receipt. */ }
    }
    private void RequireOpen() => MusicClientContextDocument.Require(!closed && !stopping, "CLOSED");
    private void RequireReady()
    {
        RequireOpen();
        MusicClientContextDocument.Require(!uncertain, "COMMIT_UNKNOWN");
        MusicClientContextDocument.Require(reload is not { IsCompleted: false }, "STORAGE_CONFLICT");
        MusicClientContextDocument.Require(current is not null, "DEVICE_CONTEXT_REQUIRED");
    }
    private static string StorageCode(DocumentStoreException error) => error.Error switch
    {
        DocumentStoreError.Conflict => "STORAGE_CONFLICT",
        DocumentStoreError.Corrupt or DocumentStoreError.UnsupportedVersion or DocumentStoreError.Protection => "CORRUPT_STORAGE",
        DocumentStoreError.Closed => "CLOSED", DocumentStoreError.Limit => "LIMIT",
        DocumentStoreError.Unavailable => "STORAGE_UNAVAILABLE", _ => "STORAGE"
    };
    internal Task PrepareShutdownAsync()
    {
        lock (gate)
        {
            if (closed) return Task.FromException(new MusicPlatformException("CLOSED"));
            stopping = true; return preparation ??= PrepareCoreAsync(work.ToArray(), lifetime);
        }
    }
    private async Task PrepareCoreAsync(Task[] pending, CancellationTokenSource generation)
    {
        await Task.Yield();
        try { generation.Cancel(); } catch (AggregateException) { }
        NotifyAll();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* Operations retain their own failures. */ }
        lock (gate) MusicClientContextDocument.Require(!uncertain, "COMMIT_UNKNOWN");
    }
    internal async Task CancelShutdownPreparationAsync()
    {
        Task? pending;
        lock (gate) pending = preparation;
        if (pending is not null) { try { await pending.ConfigureAwait(false); } catch { } }
        lock (gate)
        {
            MusicClientContextDocument.Require(!closed, "CLOSED");
            if (!stopping) return;
            if (current is { } value)
                current = new(value.Wy with { Observation = new() }, value.Kg with { }, value.Qq with { });
            lifetime.Dispose(); lifetime = new();
            preparation = null; stopping = false;
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true; stopping = true;
            return new(disposal = ReleaseAsync(work.ToArray(), lifetime));
        }
    }
    private async Task ReleaseAsync(Task[] pending, CancellationTokenSource generation)
    {
        await Task.Yield();
        try { generation.Cancel(); } catch (AggregateException) { }
        NotifyAll();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        lock (gate) current = null;
        generation.Dispose();
    }
}
