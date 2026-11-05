using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

/// <summary>
/// Local documents and complete collection source files. Only explicitly configured
/// sensitive namespaces use CurrentUser DPAPI. All cooperating instances use the
/// existing dedicated root's exclusive lock file; no blind overwrite is supported.
/// </summary>
public sealed class LocalDocumentStore : IDocumentStore
{
    public const int MaximumDocumentBytes = DocumentRules.MaximumDocumentBytes;
    public const int MaximumSourceBytes = DocumentRules.MaximumSourceBytes;
    private const string DocumentName = "document.mfdp";
    private const int EnvelopeHeaderBytes = 13, PlaintextHeaderBytes = 44;
    private readonly WindowsDocumentFiles.Root root;
    private readonly HashSet<string> namespaces;
    private readonly HashSet<string> collections;
    private readonly HashSet<string> protectedNamespaces;
    private readonly SerialOperationQueue queue = new();
    private readonly object gate = new();
    private Task? disposal;
    private readonly CancellationTokenSource lifetime = new();
    private int closed;

    public LocalDocumentStore(string productRoot, IEnumerable<string> allowedNamespaces,
        IEnumerable<string>? allowedCollections = null, IEnumerable<string>? protectedNamespaces = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
        try
        {
            namespaces = DocumentRules.Namespaces(allowedNamespaces);
            collections = DocumentRules.Namespaces(allowedCollections ?? []);
            this.protectedNamespaces = DocumentRules.Namespaces(protectedNamespaces ?? []);
            if (!this.protectedNamespaces.IsSubsetOf(namespaces))
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            root = new WindowsDocumentFiles.Root(productRoot);
        }
        catch (DocumentStoreException) { throw; }
        catch { throw new DocumentStoreException(DocumentStoreError.InvalidRequest); }
    }

    public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
    {
        var location = Locate(documentNamespace);
        return RunAsync(location, null, token => ReadLocked(location, token), cancellationToken);
    }

    public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken cancellationToken = default)
    {
        var location = Locate(documentNamespace);
        if (data.Length > location.MaximumBytes) throw new DocumentStoreException(DocumentStoreError.Limit);
        if (expected is null || !expected.IsValidFor(location.MaximumBytes))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        // Take an immediate owned snapshot before waiting for the serial writer.
        return RunAsync(location, data, operation => WriteLocked(location, expected, operation.Data!), cancellationToken);
    }

    private sealed record DocumentLocation(string Namespace, string Directory, string FileName,
        bool Protected, int MaximumBytes);
    private sealed record Operation(CancellationToken Cancellation, byte[]? Data);

    private Task<T> RunAsync<T>(DocumentLocation location, ReadOnlyMemory<byte>? data, Func<Operation, T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed != 0) throw new DocumentStoreException(DocumentStoreError.Closed);
            // Accepted writes own their bytes and receipt, including while queued.
            // Reads remain cancellable; shutdown drains writes before releasing root.
            var owned = data?.ToArray();
            return queue.Enqueue(async () =>
            {
                using var linked = owned is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token) : null;
                var token = linked?.Token ?? CancellationToken.None;
                try
                {
                    return await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        root.Check();
                        using var lease = AcquireRootLock(token);
                        root.Check();
                        return operation(new Operation(token, owned));
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (Volatile.Read(ref closed) != 0)
                { throw new DocumentStoreException(DocumentStoreError.Closed); }
                catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
                catch (DocumentStoreException) { throw; }
                catch { throw new DocumentStoreException(DocumentStoreError.Storage); }
                finally
                {
                    if (location.Protected && owned is not null) CryptographicOperations.ZeroMemory(owned);
                }
            }, owned is null ? cancellationToken : CancellationToken.None);
        }
    }

    private DocumentLocation Locate(string ns)
    {
        if (!DocumentRules.ValidNamespace(ns)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        if (namespaces.Contains(ns))
        {
            bool protect = protectedNamespaces.Contains(ns);
            string directory = Path.Combine(root.Path, protect ? "n-" + NamespaceHash(ns) : "n-" + ns + "-" + NamespaceHash(ns));
            return new(ns, directory, protect ? DocumentName : "document.json", protect, MaximumDocumentBytes);
        }
        // Collection access is trusted-native only. The bridge retains its exact
        // namespace grants and 8 MiB document/transfer contract.
        foreach (string collection in collections)
        {
            if (!ns.StartsWith(collection + ".", StringComparison.Ordinal)) continue;
            string hash = ns[(collection.Length + 1)..];
            if (DocumentRules.ValidHash(hash))
                return new(ns, Path.Combine(root.Path, "c-" + collection + "-" + NamespaceHash(collection)),
                    hash + ".js", false, MaximumSourceBytes);
        }
        throw new DocumentStoreException(DocumentStoreError.Unavailable);
    }

    private SafeFileHandle AcquireRootLock(CancellationToken cancellation)
    {
        long start = Stopwatch.GetTimestamp();
        string path = Path.Combine(root.Path, ".documents.lock");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var handle = WindowsDocumentFiles.OpenFile(path, WindowsDocumentFiles.Read | WindowsDocumentFiles.Write,
                0, WindowsDocumentFiles.OpenAlways, sharingIsBusy: true);
            if (handle is not null)
            {
                if (WindowsDocumentFiles.FileLength(handle) != 0)
                {
                    handle.Dispose();
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                }
                return handle;
            }
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(5))
                throw new DocumentStoreException(DocumentStoreError.Storage);
            // Bounded waiting for another cooperating process; no service/port/global mutex.
            if (cancellation.WaitHandle.WaitOne(10)) cancellation.ThrowIfCancellationRequested();
        }
    }

    private static string NamespaceHash(string ns) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ns)));

    private byte[] Entropy(string ns)
    {
        byte[] prefix = "meowfilm.windows.documents.dpapi.v1\0"u8.ToArray();
        byte[] name = Encoding.UTF8.GetBytes(ns);
        byte[] material = new byte[prefix.Length + root.Identity.Length + name.Length];
        prefix.CopyTo(material, 0);
        root.Identity.CopyTo(material, prefix.Length);
        name.CopyTo(material, prefix.Length + root.Identity.Length);
        return SHA256.HashData(material);
    }

    // Sensitive namespaces retain their strict inventory check. Unknown files
    // (including interrupted stages) require explicit recovery, never automatic deletion.
    private static void CheckInventory(string directory)
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            if (++count > 1 || !StringComparer.Ordinal.Equals(Path.GetFileName(path), DocumentName))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }

    private DocumentSnapshot ReadLocked(DocumentLocation location, Operation operation)
    {
        operation.Cancellation.ThrowIfCancellationRequested();
        string directory = location.Directory;
        using var folder = WindowsDocumentFiles.OpenDirectory(directory, true);
        if (folder is null) return new DocumentSnapshot(null, DocumentFingerprint.Missing);
        if (location.Protected) CheckInventory(directory);
        return ReadDocument(Path.Combine(directory, location.FileName), location, operation.Cancellation);
    }

    private DocumentSnapshot ReadDocument(string path, DocumentLocation location, CancellationToken cancellation)
    {
        using var handle = WindowsDocumentFiles.OpenFile(path, WindowsDocumentFiles.Read,
            WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.OpenExisting, allowMissing: true);
        if (handle is null) return new DocumentSnapshot(null, DocumentFingerprint.Missing);
        long length = WindowsDocumentFiles.FileLength(handle);
        if (!location.Protected)
        {
            if (length > location.MaximumBytes) throw new DocumentStoreException(DocumentStoreError.Limit);
            var data = new byte[(int)length];
            using (var file = new FileStream(handle, FileAccess.Read))
            {
                file.ReadExactly(data);
                if (file.ReadByte() != -1) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            }
            cancellation.ThrowIfCancellationRequested();
            return new DocumentSnapshot(data, DocumentFingerprint.FromBytes(data));
        }
        if (length < EnvelopeHeaderBytes) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        if (length > CurrentUserDpapi.MaximumCiphertextBytes + EnvelopeHeaderBytes)
            throw new DocumentStoreException(DocumentStoreError.Limit);
        var encrypted = new byte[(int)length];
        using (var file = new FileStream(handle, FileAccess.Read))
        {
            file.ReadExactly(encrypted);
            if (file.ReadByte() != -1) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        }
        cancellation.ThrowIfCancellationRequested();
        if (!encrypted.AsSpan(0, 8).SequenceEqual("MEOWDPAP"u8))
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        if (encrypted[8] != 1) throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        if (BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(9, 4)) != encrypted.Length - EnvelopeHeaderBytes)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        byte[] plain = CurrentUserDpapi.Transform(encrypted[EnvelopeHeaderBytes..], Entropy(location.Namespace), false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (plain.Length < PlaintextHeaderBytes || !plain.AsSpan(0, 8).SequenceEqual("MFDPDOC1"u8))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            int count = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(8, 4));
            if (count < 0 || count > MaximumDocumentBytes || count != plain.Length - PlaintextHeaderBytes ||
                !CryptographicOperations.FixedTimeEquals(plain.AsSpan(12, 32), SHA256.HashData(plain.AsSpan(PlaintextHeaderBytes))))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            byte[] data = plain[PlaintextHeaderBytes..];
            return new DocumentSnapshot(data, DocumentFingerprint.FromBytes(data));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private DocumentFingerprint WriteLocked(DocumentLocation location, DocumentFingerprint expected, byte[] data)
    {
        var next = DocumentFingerprint.FromBytes(data);
        string directory = location.Directory;
        using var folder = WindowsDocumentFiles.OpenDirectory(directory, true) ?? WindowsDocumentFiles.CreateDirectory(directory);
        if (location.Protected) CheckInventory(directory);
        string destination = Path.Combine(directory, location.FileName);
        DocumentSnapshot previous = ReadDocument(destination, location, CancellationToken.None);
        try
        {
            if (previous.Fingerprint != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
        }
        finally { if (location.Protected && previous.Data is not null) CryptographicOperations.ZeroMemory(previous.Data); }
        byte[] contents = location.Protected ? Protect(data, location.Namespace) : data;
        string staging = Path.Combine(directory, "stage-" + Guid.NewGuid().ToString("N") + Path.GetExtension(location.FileName));
        using var stage = WindowsDocumentFiles.OpenFile(staging,
            WindowsDocumentFiles.Read | WindowsDocumentFiles.Write | WindowsDocumentFiles.Delete,
            WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.CreateNew)!;
        using var file = new FileStream(stage, FileAccess.ReadWrite);
        bool committed = false;
        try
        {
            if (location.Protected)
            {
                byte[] header = new byte[EnvelopeHeaderBytes];
                "MEOWDPAP"u8.CopyTo(header);
                header[8] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9, 4), contents.Length);
                file.Write(header);
            }
            file.Write(contents);
            file.Flush(flushToDisk: true);
            root.Check();
            // Commit point. No cancellation check or fallible verification after this
            // rename: a lost/cancelled ACK must not be presented as guaranteed rollback.
            WindowsDocumentFiles.Replace(stage, destination);
            committed = true;
            return next;
        }
        finally { if (!committed && !stage.IsClosed) WindowsDocumentFiles.DiscardStage(stage); }
    }

    private byte[] Protect(byte[] data, string ns)
    {
        byte[] plain = new byte[PlaintextHeaderBytes + data.Length];
        try
        {
            "MFDPDOC1"u8.CopyTo(plain);
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(8, 4), data.Length);
            SHA256.HashData(data).CopyTo(plain, 12);
            data.CopyTo(plain, PlaintextHeaderBytes);
            return CurrentUserDpapi.Transform(plain, Entropy(ns), true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            Volatile.Write(ref closed, 1);
            return new(disposal = CloseAsync(queue.DrainAsync()));
        }
    }
    private async Task CloseAsync(Task work)
    {
        await Task.Yield();
        try { lifetime.Cancel(); } catch (AggregateException) { /* Cancellation callbacks cannot skip physical drain. */ }
        await work.ConfigureAwait(false);
        root.Dispose();
        lifetime.Dispose();
    }
}
