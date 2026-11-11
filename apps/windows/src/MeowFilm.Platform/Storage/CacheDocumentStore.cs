using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MeowFilm.Platform.Storage;

/// <summary>
/// Disposable documents: CAS receipts describe the current memory snapshot, not a
/// durable business commit. Disk persistence has its own ordered, drainable owner.
/// No backing store is constructed until the first access.
/// </summary>
public sealed class CacheDocumentStore : IDocumentStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, DocumentSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> reads = new(StringComparer.Ordinal);
    private readonly SerialOperationQueue writes = new();
    private readonly Func<IDocumentStore>? factory;
    private readonly bool ownsBacking;
    private readonly CancellationTokenSource lifetime = new();
    private IDocumentStore? backing;
    private Task<IDocumentStore>? opening;
    private Task? disposal;
    private bool closed;

    public CacheDocumentStore() { }
    public CacheDocumentStore(IDocumentStore backing) =>
        this.backing = backing ?? throw new ArgumentNullException(nameof(backing));
    public CacheDocumentStore(Func<IDocumentStore> factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ownsBacking = true;
    }

    public DocumentSnapshot ReadMemory(string documentNamespace)
    {
        lock (gate)
        {
            Admit(documentNamespace);
            return snapshots.TryGetValue(documentNamespace, out var snapshot)
                ? Copy(snapshot) : new(null, DocumentFingerprint.Missing);
        }
    }

    /// <summary>A complete cache snapshot never needs to read old disk content before publication.</summary>
    public void Save(string documentNamespace, ReadOnlyMemory<byte> data)
    {
        if (data.Length > LocalDocumentStore.MaximumDocumentBytes)
            throw new DocumentStoreException(DocumentStoreError.Limit);
        byte[] bytes = data.ToArray();
        lock (gate)
        {
            Admit(documentNamespace);
            Publish(documentNamespace, bytes, DocumentFingerprint.FromBytes(bytes));
        }
    }

    public async Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task read;
        lock (gate)
        {
            Admit(documentNamespace);
            if (snapshots.TryGetValue(documentNamespace, out var snapshot)) return Copy(snapshot);
            if (!reads.TryGetValue(documentNamespace, out read!))
                reads[documentNamespace] = read = Task.Run(() => LoadAsync(documentNamespace));
        }
        await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate) return Copy(snapshots[documentNamespace]);
    }

    public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > LocalDocumentStore.MaximumDocumentBytes)
            throw new DocumentStoreException(DocumentStoreError.Limit);
        byte[] bytes = data.ToArray();
        var fingerprint = DocumentFingerprint.FromBytes(bytes);
        lock (gate)
        {
            Admit(documentNamespace);
            var current = snapshots.TryGetValue(documentNamespace, out var snapshot)
                ? snapshot.Fingerprint : DocumentFingerprint.Missing;
            if (expected != current) throw new DocumentStoreException(DocumentStoreError.Conflict);
            Publish(documentNamespace, bytes, fingerprint);
        }
        return Task.FromResult(fingerprint);
    }

    private void Publish(string ns, byte[] bytes, DocumentFingerprint fingerprint)
    {
        snapshots[ns] = new(bytes, fingerprint);
        if (backing is not null || factory is not null)
            // The queue owns both execution and errors; caller cancellation after
            // this memory commit cannot detach an admitted disk write.
            _ = writes.Enqueue(() => Task.Run(() => PersistAsync(ns, bytes)));
    }

    private void Admit(string documentNamespace)
    {
        if (closed) throw new DocumentStoreException(DocumentStoreError.Closed);
        if (!DocumentRules.ValidNamespace(documentNamespace))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
    }

    private static DocumentSnapshot Copy(DocumentSnapshot value) => new(value.Data?.ToArray(), value.Fingerprint);

    private async Task<IDocumentStore?> OpenAsync()
    {
        Task<IDocumentStore> pending;
        lock (gate)
        {
            if (backing is not null || factory is null) return backing;
            pending = opening ??= Task.Run(factory);
        }
        try
        {
            var value = await pending.ConfigureAwait(false);
            lock (gate) backing = value;
            return value;
        }
        finally
        {
            lock (gate) if (ReferenceEquals(opening, pending)) opening = null;
        }
    }

    private async Task LoadAsync(string ns)
    {
        var snapshot = new DocumentSnapshot(null, DocumentFingerprint.Missing);
        try
        {
            if (await OpenAsync().ConfigureAwait(false) is { } disk)
                snapshot = disk is LocalDocumentStore local
                    ? await local.ReadCacheAsync(ns, lifetime.Token).ConfigureAwait(false)
                    : await disk.ReadAsync(ns, lifetime.Token).ConfigureAwait(false);
        }
        catch { Trace.TraceWarning("CACHE_READ_UNAVAILABLE"); }
        lock (gate)
        {
            // A write admitted while the initial read was running wins.
            if (!snapshots.ContainsKey(ns)) snapshots[ns] = Copy(snapshot);
        }
    }

    private async Task PersistAsync(string ns, byte[] bytes)
    {
        try
        {
            if (await OpenAsync().ConfigureAwait(false) is not { } disk) return;
            DocumentFingerprint receipt;
            if (disk is LocalDocumentStore local)
                receipt = await local.ReplaceCacheAsync(ns, bytes).ConfigureAwait(false);
            else
            {
                var before = await disk.ReadAsync(ns).ConfigureAwait(false);
                receipt = await disk.WriteAsync(ns, bytes, before.Fingerprint).ConfigureAwait(false);
            }
            if (receipt != DocumentFingerprint.FromBytes(bytes))
                Trace.TraceWarning("CACHE_WRITE_UNCONFIRMED");
        }
        catch { Trace.TraceWarning("CACHE_WRITE_UNAVAILABLE"); }
    }

    public Task DrainAsync()
    {
        lock (gate) return Task.WhenAll(Task.WhenAll(reads.Values), writes.DrainAsync());
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            return new(disposal = CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        await Task.Yield();
        lifetime.Cancel();
        await DrainAsync().ConfigureAwait(false);
        if (ownsBacking && backing is not null)
        {
            try { await backing.DisposeAsync().ConfigureAwait(false); }
            catch { Trace.TraceWarning("CACHE_CLOSE_UNAVAILABLE"); }
        }
        lifetime.Dispose();
    }
}
