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
/// CurrentUser DPAPI for opaque business documents, not account/credential discovery.
/// Root must be an existing dedicated local directory. All cooperating instances use
/// its exclusive lock file; no unprotected fallback or blind overwrite is supported.
/// </summary>
public sealed class DpapiDocumentStore : IProtectedDocumentStore
{
    public const int MaximumDocumentBytes = DocumentRules.MaximumDocumentBytes;
    private const string DocumentName = "document.mfdp";
    private const int EnvelopeHeaderBytes = 13, PlaintextHeaderBytes = 44;
    private readonly WindowsDocumentFiles.Root root;
    private readonly HashSet<string> namespaces;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly SemaphoreSlim admission = new(8, 8);
    private readonly CancellationTokenSource lifetime = new();
    private int closed;

    public DpapiDocumentStore(string productRoot, IEnumerable<string> allowedNamespaces)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows required.");
        try
        {
            namespaces = DocumentRules.Namespaces(allowedNamespaces);
            root = new WindowsDocumentFiles.Root(productRoot);
        }
        catch (ProtectedDocumentException) { throw; }
        catch { throw new ProtectedDocumentException(DocumentStoreError.InvalidRequest); }
    }

    public Task<ProtectedDocument> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default) =>
        RunAsync(documentNamespace, null, token => ReadLocked(documentNamespace, token), cancellationToken);

    public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
        DocumentFingerprint expected, CancellationToken cancellationToken = default)
    {
        if (data.Length > MaximumDocumentBytes) throw new ProtectedDocumentException(DocumentStoreError.Limit);
        if (expected is null || !expected.IsValid) throw new ProtectedDocumentException(DocumentStoreError.InvalidRequest);
        // RunAsync takes an immediate owned snapshot after bounded admission, before its first await.
        return RunAsync(documentNamespace, data, token => WriteLocked(documentNamespace, expected, token), cancellationToken);
    }

    private sealed record Operation(CancellationToken Cancellation, byte[]? Data);

    private async Task<T> RunAsync<T>(string ns, ReadOnlyMemory<byte>? data, Func<Operation, T> operation,
        CancellationToken cancellationToken)
    {
        ValidateNamespace(ns);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed) != 0) throw new ProtectedDocumentException(DocumentStoreError.Closed);
        if (!admission.Wait(0)) throw new ProtectedDocumentException(DocumentStoreError.Limit);
        byte[]? owned = null;
        bool entered = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            owned = data?.ToArray();
            await serial.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref closed) != 0) throw new ProtectedDocumentException(DocumentStoreError.Closed);
            return await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                root.Check();
                using var lease = AcquireRootLock(linked.Token);
                root.Check();
                return operation(new Operation(linked.Token, owned));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref closed) != 0)
        { throw new ProtectedDocumentException(DocumentStoreError.Closed); }
        catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
        catch (ProtectedDocumentException) { throw; }
        catch { throw new ProtectedDocumentException(DocumentStoreError.Storage); }
        finally
        {
            if (owned is not null) CryptographicOperations.ZeroMemory(owned);
            if (entered) serial.Release();
            admission.Release();
        }
    }

    private void ValidateNamespace(string ns)
    {
        if (!DocumentRules.ValidNamespace(ns)) throw new ProtectedDocumentException(DocumentStoreError.InvalidRequest);
        if (!namespaces.Contains(ns)) throw new ProtectedDocumentException(DocumentStoreError.Unavailable);
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
                    throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
                }
                return handle;
            }
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(5))
                throw new ProtectedDocumentException(DocumentStoreError.Storage);
            // Bounded waiting for another cooperating process; no service/port/global mutex.
            if (cancellation.WaitHandle.WaitOne(10)) cancellation.ThrowIfCancellationRequested();
        }
    }

    private string NamespacePath(string ns) => Path.Combine(root.Path,
        "n-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ns))));

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

    // Unknown files (including interrupted stages) require explicit recovery. Never
    // read unknown contents, delete them automatically, or infer a fresh empty store.
    private static void CheckInventory(string directory)
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            if (++count > 1 || !StringComparer.Ordinal.Equals(Path.GetFileName(path), DocumentName))
                throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
    }

    private ProtectedDocument ReadLocked(string ns, Operation operation)
    {
        operation.Cancellation.ThrowIfCancellationRequested();
        string directory = NamespacePath(ns);
        using var folder = WindowsDocumentFiles.OpenDirectory(directory, true);
        if (folder is null) return new ProtectedDocument(null, DocumentFingerprint.Missing);
        CheckInventory(directory);
        return ReadDocument(Path.Combine(directory, DocumentName), ns, operation.Cancellation);
    }

    private ProtectedDocument ReadDocument(string path, string ns, CancellationToken cancellation)
    {
        using var handle = WindowsDocumentFiles.OpenFile(path, WindowsDocumentFiles.Read,
            WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.OpenExisting, allowMissing: true);
        if (handle is null) return new ProtectedDocument(null, DocumentFingerprint.Missing);
        long length = WindowsDocumentFiles.FileLength(handle);
        if (length < EnvelopeHeaderBytes) throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
        if (length > CurrentUserDpapi.MaximumCiphertextBytes + EnvelopeHeaderBytes)
            throw new ProtectedDocumentException(DocumentStoreError.Limit);
        var encrypted = new byte[(int)length];
        using (var file = new FileStream(handle, FileAccess.Read))
        {
            file.ReadExactly(encrypted);
            if (file.ReadByte() != -1) throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
        }
        cancellation.ThrowIfCancellationRequested();
        if (!encrypted.AsSpan(0, 8).SequenceEqual("MEOWDPAP"u8))
            throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
        if (encrypted[8] != 1) throw new ProtectedDocumentException(DocumentStoreError.UnsupportedVersion);
        if (BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(9, 4)) != encrypted.Length - EnvelopeHeaderBytes)
            throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
        byte[] plain = CurrentUserDpapi.Transform(encrypted[EnvelopeHeaderBytes..], Entropy(ns), false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (plain.Length < PlaintextHeaderBytes || !plain.AsSpan(0, 8).SequenceEqual("MFDPDOC1"u8))
                throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
            int count = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(8, 4));
            if (count < 0 || count > MaximumDocumentBytes || count != plain.Length - PlaintextHeaderBytes ||
                !CryptographicOperations.FixedTimeEquals(plain.AsSpan(12, 32), SHA256.HashData(plain.AsSpan(PlaintextHeaderBytes))))
                throw new ProtectedDocumentException(DocumentStoreError.Corrupt);
            byte[] data = plain[PlaintextHeaderBytes..];
            return new ProtectedDocument(data, DocumentFingerprint.FromBytes(data));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private DocumentFingerprint WriteLocked(string ns, DocumentFingerprint expected, Operation operation)
    {
        byte[] data = operation.Data!;
        var cancellation = operation.Cancellation;
        var next = DocumentFingerprint.FromBytes(data);
        string directory = NamespacePath(ns);
        using var folder = WindowsDocumentFiles.OpenDirectory(directory, true) ?? WindowsDocumentFiles.CreateDirectory(directory);
        CheckInventory(directory);
        string destination = Path.Combine(directory, DocumentName);
        ProtectedDocument previous = ReadDocument(destination, ns, cancellation);
        try
        {
            if (previous.Fingerprint != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
        }
        finally { if (previous.Data is not null) CryptographicOperations.ZeroMemory(previous.Data); }
        cancellation.ThrowIfCancellationRequested();
        byte[] plain = new byte[PlaintextHeaderBytes + data.Length];
        byte[] encrypted;
        try
        {
            "MFDPDOC1"u8.CopyTo(plain);
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(8, 4), data.Length);
            SHA256.HashData(data).CopyTo(plain, 12);
            data.CopyTo(plain, PlaintextHeaderBytes);
            encrypted = CurrentUserDpapi.Transform(plain, Entropy(ns), true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        cancellation.ThrowIfCancellationRequested();
        string staging = Path.Combine(directory, "stage-" + Guid.NewGuid().ToString("N") + ".mfdp");
        using var stage = WindowsDocumentFiles.OpenFile(staging,
            WindowsDocumentFiles.Read | WindowsDocumentFiles.Write | WindowsDocumentFiles.Delete,
            WindowsDocumentFiles.ShareRead, WindowsDocumentFiles.CreateNew)!;
        using var file = new FileStream(stage, FileAccess.ReadWrite);
        bool committed = false;
        try
        {
            byte[] header = new byte[EnvelopeHeaderBytes];
            "MEOWDPAP"u8.CopyTo(header);
            header[8] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9, 4), encrypted.Length);
            file.Write(header);
            file.Write(encrypted);
            file.Flush(flushToDisk: true);
            root.Check();
            cancellation.ThrowIfCancellationRequested();
            // Commit point. No cancellation check or fallible verification after this
            // rename: a lost/cancelled ACK must not be presented as guaranteed rollback.
            WindowsDocumentFiles.Replace(stage, destination);
            committed = true;
            return next;
        }
        finally { if (!committed && !stage.IsClosed) WindowsDocumentFiles.DiscardStage(stage); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        try { root.Dispose(); }
        finally { serial.Release(); }
        // Semaphores/CTS remain valid for queued waiters and repeated disposal.
    }
}
