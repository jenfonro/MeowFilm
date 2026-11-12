using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class CacheDocumentStoreTests
{
    private const string CacheNamespace = "test.cache";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Cache document factory remains lazy until first access", LazyFactory);
        yield return new("Cache document backing read failure becomes a missing snapshot", ReadFailure);
        yield return new("Cache document slow persistence does not delay memory receipts or independent business commits", SlowWrite);
        yield return new("Cache document persistence preserves write order and drain waits for the last write", OrderedDrain);
        yield return new("Cache document failed persistence retains memory and permits subsequent persistence", WriteFailure);
        yield return new("Cache document late initial read cannot replace a concurrent memory write", LateRead);
        yield return new("Cache document close rejects admission and drains accepted writes before owned backing disposal", Close);
        yield return new("Cache document Save publishes owned memory immediately while persistence is blocked", SaveMemory);
        yield return new("Cache document rebuilds corrupt protected local content without weakening business CAS", RebuildProtected);
        yield return new("Cache document rebuilds oversized local content", RebuildOversized);
        yield return new("Cache document local root lock and queue cannot block independent business commits", LocalIsolation);
        yield return new("Cache document hardlink target remains untouched while memory reads and saves succeed", LocalHardlink);
        yield return new("Cache document protected local content ignores unrelated stages without weakening business inventory", ProtectedStage);
    }

    private static async Task SaveMemory()
    {
        var entered = Signal();
        var release = Signal();
        var disk = new FakeStore
        {
            BeforeWrite = async (_, _) => { entered.TrySetResult(); await release.Task; }
        };
        var cache = new CacheDocumentStore(() => disk);
        byte[] first = Bytes("synthetic-first"), second = Bytes("synthetic-second");
        try
        {
            Check(cache.ReadMemory(CacheNamespace).Data is null, "unloaded memory must be a miss");
            byte[] callerBytes = first.ToArray();
            cache.Save(CacheNamespace, callerBytes);
            callerBytes.AsSpan().Clear();
            HasValue(cache.ReadMemory(CacheNamespace), first);
            var returned = cache.ReadMemory(CacheNamespace);
            returned.Data!.AsSpan().Clear();
            HasValue(cache.ReadMemory(CacheNamespace), first);
            await entered.Task.WaitAsync(Deadline);
            cache.Save(CacheNamespace, second);
            HasValue(cache.ReadMemory(CacheNamespace), second);
            var closing = cache.DisposeAsync().AsTask();
            Check(!closing.IsCompleted && !disk.Disposed, "close must await admitted Save persistence");
            await Closed(() => { cache.Save(CacheNamespace, first); return Task.CompletedTask; });
            await Closed(() => { cache.ReadMemory(CacheNamespace); return Task.CompletedTask; });
            release.TrySetResult();
            await closing.WaitAsync(Deadline);
            HasValue(disk.Snapshot(CacheNamespace), second);
            Check(disk.Disposed, "owned store must close after the last Save settles");
        }
        finally
        {
            release.TrySetResult();
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static async Task RebuildProtected()
    {
        using var fixture = new LocalFixture();
        byte[] seed = Bytes("synthetic-cache-seed"), replacement = Bytes("synthetic-cache-rebuilt");
        await using (var strict = fixture.CacheStore(protect: true))
        {
            var original = await strict.WriteAsync(CacheNamespace, seed, DocumentFingerprint.Missing).WaitAsync(Deadline);
            string path = Directory.GetFiles(fixture.CacheRoot, "document.mfdp", SearchOption.AllDirectories).Single();
            byte[] damaged = File.ReadAllBytes(path);
            damaged[^1] ^= 1;
            File.WriteAllBytes(path, damaged);
            await StoreError(() => strict.ReadAsync(CacheNamespace), DocumentStoreError.Protection);
            await StoreError(() => strict.WriteAsync(CacheNamespace, replacement, original), DocumentStoreError.Protection);
            Check(File.ReadAllBytes(path).SequenceEqual(damaged), "strict business CAS must not replace unreadable protected data");
        }
        await using (var cache = new CacheDocumentStore(() => fixture.CacheStore(protect: true)))
        {
            var miss = await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline);
            Check(miss.Data is null && miss.Fingerprint == DocumentFingerprint.Missing, "corrupt protected cache must miss");
            cache.Save(CacheNamespace, replacement);
            HasValue(cache.ReadMemory(CacheNamespace), replacement);
            await cache.DrainAsync().WaitAsync(Deadline);
        }
        await using var reopened = fixture.CacheStore(protect: true);
        HasValue(await reopened.ReadAsync(CacheNamespace).WaitAsync(Deadline), replacement);
        string rebuilt = Directory.GetFiles(fixture.CacheRoot, "document.mfdp", SearchOption.AllDirectories).Single();
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(rebuilt)).Contains(Encoding.UTF8.GetString(replacement),
            StringComparison.Ordinal), "rebuilt protected content must not be stored in plaintext");
    }

    private static async Task RebuildOversized()
    {
        using var fixture = new LocalFixture();
        byte[] replacement = Bytes("synthetic-small-cache");
        await using (var strict = fixture.CacheStore())
        {
            var original = await strict.WriteAsync(CacheNamespace, Bytes("seed"), DocumentFingerprint.Missing).WaitAsync(Deadline);
            string path = Directory.GetFiles(fixture.CacheRoot, "document.json", SearchOption.AllDirectories).Single();
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                file.SetLength(LocalDocumentStore.MaximumDocumentBytes + 1L);
            await StoreError(() => strict.ReadAsync(CacheNamespace), DocumentStoreError.Limit);
            await StoreError(() => strict.WriteAsync(CacheNamespace, replacement, original), DocumentStoreError.Limit);
        }
        await using (var cache = new CacheDocumentStore(() => fixture.CacheStore()))
        {
            Check((await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline)).Data is null, "oversized cache must miss");
            cache.Save(CacheNamespace, replacement);
            HasValue(cache.ReadMemory(CacheNamespace), replacement);
            await cache.DrainAsync().WaitAsync(Deadline);
        }
        await using var reopened = fixture.CacheStore();
        HasValue(await reopened.ReadAsync(CacheNamespace).WaitAsync(Deadline), replacement);
    }

    private static async Task LocalIsolation()
    {
        using var fixture = new LocalFixture();
        byte[] first = Bytes("synthetic-cache-one"), second = Bytes("synthetic-cache-two");
        await using (var seed = fixture.CacheStore())
            await seed.WriteAsync(CacheNamespace, Bytes("seed"), DocumentFingerprint.Missing).WaitAsync(Deadline);
        await using var business = new LocalDocumentStore(fixture.BusinessRoot, ["test.business"]);
        var cache = new CacheDocumentStore(() => fixture.CacheStore());
        FileStream? held = new(Path.Combine(fixture.CacheRoot, ".documents.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            cache.Save(CacheNamespace, first);
            cache.Save(CacheNamespace, second);
            HasValue(cache.ReadMemory(CacheNamespace), second);
            var drain = cache.DrainAsync();
            Check(!drain.IsCompleted, "held cache root lock must prevent disk persistence from settling");
            byte[] businessValue = Bytes("synthetic-business");
            var receipt = await business.WriteAsync("test.business", businessValue, DocumentFingerprint.Missing).WaitAsync(Deadline);
            Check(receipt == DocumentFingerprint.FromBytes(businessValue), "business receipt must not depend on cache root lock");
            HasValue(await business.ReadAsync("test.business").WaitAsync(Deadline), businessValue);
            Check(!drain.IsCompleted, "independent business operations must finish while cache writes remain blocked");
            var closing = cache.DisposeAsync().AsTask();
            Check(!closing.IsCompleted, "cache close must wait for physically blocked persistence");
            held.Dispose();
            held = null;
            await drain.WaitAsync(Deadline);
            await closing.WaitAsync(Deadline);
            await using var reopened = fixture.CacheStore();
            HasValue(await reopened.ReadAsync(CacheNamespace).WaitAsync(Deadline), second);
        }
        finally
        {
            held?.Dispose();
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static async Task LocalHardlink()
    {
        using var fixture = new LocalFixture();
        byte[] original = Bytes("synthetic-linked-content"), replacement = Bytes("synthetic-memory-only");
        await using (var seed = fixture.CacheStore())
            await seed.WriteAsync(CacheNamespace, original, DocumentFingerprint.Missing).WaitAsync(Deadline);
        string target = Directory.GetFiles(fixture.CacheRoot, "document.json", SearchOption.AllDirectories).Single();
        string external = Path.Combine(fixture.BusinessRoot, "synthetic-external.json");
        Check(CreateHardLinkW(external, target, IntPtr.Zero), "hardlink fixture creation failed");

        await using (var cache = new CacheDocumentStore(() => fixture.CacheStore()))
        {
            var miss = await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline);
            Check(miss.Data is null && miss.Fingerprint == DocumentFingerprint.Missing, "unsafe backing target must be a cache miss");
            cache.Save(CacheNamespace, replacement);
            HasValue(cache.ReadMemory(CacheNamespace), replacement);
            HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), replacement);
            await cache.DrainAsync().WaitAsync(Deadline);
            HasValue(cache.ReadMemory(CacheNamespace), replacement);
        }

        Check(File.ReadAllBytes(target).SequenceEqual(original), "cache persistence must not replace a hardlinked target");
        Check(File.ReadAllBytes(external).SequenceEqual(original), "cache persistence must not alter external linked bytes");
        await using var strict = fixture.CacheStore();
        await StoreError(() => strict.ReadAsync(CacheNamespace), DocumentStoreError.UnsafePath);
    }

    private static async Task ProtectedStage()
    {
        using var fixture = new LocalFixture();
        byte[] original = Bytes("synthetic-protected-cache"), replacement = Bytes("synthetic-replaced-cache");
        byte[] unrelated = Bytes("synthetic-unrelated-stage");
        await using (var seed = fixture.CacheStore(protect: true))
            await seed.WriteAsync(CacheNamespace, original, DocumentFingerprint.Missing).WaitAsync(Deadline);
        string target = Directory.GetFiles(fixture.CacheRoot, "document.mfdp", SearchOption.AllDirectories).Single();
        string stage = Path.Combine(Path.GetDirectoryName(target)!, "stage-synthetic-unrelated.mfdp");
        File.WriteAllBytes(stage, unrelated);
        await using (var strict = fixture.CacheStore(protect: true))
        {
            await StoreError(() => strict.ReadAsync(CacheNamespace), DocumentStoreError.Corrupt);
            await StoreError(() => strict.WriteAsync(CacheNamespace, replacement, DocumentFingerprint.FromBytes(original)),
                DocumentStoreError.Corrupt);
        }

        await using (var cache = new CacheDocumentStore(() => fixture.CacheStore(protect: true)))
        {
            HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), original);
            cache.Save(CacheNamespace, replacement);
            HasValue(cache.ReadMemory(CacheNamespace), replacement);
            await cache.DrainAsync().WaitAsync(Deadline);
        }
        await using (var strict = fixture.CacheStore(protect: true))
        {
            await StoreError(() => strict.ReadAsync(CacheNamespace), DocumentStoreError.Corrupt);
            await StoreError(() => strict.WriteAsync(CacheNamespace, original, DocumentFingerprint.FromBytes(replacement)),
                DocumentStoreError.Corrupt);
        }
        await using (var reopened = new CacheDocumentStore(() => fixture.CacheStore(protect: true)))
            HasValue(await reopened.ReadAsync(CacheNamespace).WaitAsync(Deadline), replacement);
        Check(File.ReadAllBytes(stage).SequenceEqual(unrelated), "cache reads, writes and close must preserve unrelated stages");
    }

    private static async Task StoreError(Func<Task> operation, DocumentStoreError expected)
    {
        try { await operation().WaitAsync(Deadline); }
        catch (DocumentStoreException error) when (error.Error == expected) { return; }
        throw new InvalidOperationException("unexpected local storage error");
    }

    private sealed class LocalFixture : IDisposable
    {
        private readonly string parent = TestWorkspace.DataPath("cache-document-tests");
        private readonly string root;
        internal string CacheRoot { get; }
        internal string BusinessRoot { get; }
        internal LocalFixture()
        {
            root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            CacheRoot = Path.Combine(root, "cache");
            BusinessRoot = Path.Combine(root, "business");
            Directory.CreateDirectory(CacheRoot);
            Directory.CreateDirectory(BusinessRoot);
        }
        internal LocalDocumentStore CacheStore(bool protect = false) =>
            new(CacheRoot, [CacheNamespace], protectedNamespaces: protect ? [CacheNamespace] : []);
        public void Dispose()
        {
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "unsafe test cleanup target");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task LazyFactory()
    {
        int opened = 0;
        var disk = new FakeStore();
        var cache = new CacheDocumentStore(() => { Interlocked.Increment(ref opened); return disk; });
        Check(opened == 0, "construction must not execute the factory");
        try
        {
            var missing = await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline);
            Check(missing.Data is null && missing.Fingerprint == DocumentFingerprint.Missing, "missing snapshot expected");
            Check(opened == 1, "first access must open backing once");
            await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline);
            Check(opened == 1, "memory reads must reuse the opened store");
        }
        finally { await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
        Check(disk.Disposed, "factory-created backing belongs to the cache owner");

        int unusedOpened = 0;
        var unused = new CacheDocumentStore(() => { Interlocked.Increment(ref unusedOpened); return new FakeStore(); });
        await unused.DisposeAsync().AsTask().WaitAsync(Deadline);
        Check(unusedOpened == 0, "closing an unused cache must not construct storage");
    }

    private static async Task ReadFailure()
    {
        var disk = new FakeStore
        {
            ReadOverride = (_, _) => Task.FromException<DocumentSnapshot>(new DocumentStoreException(DocumentStoreError.Storage))
        };
        await using var cache = new CacheDocumentStore(disk);
        var snapshot = await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline);
        Check(snapshot.Data is null && snapshot.Fingerprint == DocumentFingerprint.Missing, "storage failure must be a miss");
        await cache.DrainAsync().WaitAsync(Deadline);
    }

    private static async Task SlowWrite()
    {
        var entered = Signal();
        var release = Signal();
        var disk = new FakeStore
        {
            BeforeWrite = async (_, _) => { entered.TrySetResult(); await release.Task; }
        };
        var cache = new CacheDocumentStore(disk);
        await using var business = new FakeStore();
        byte[] value = Bytes("cached");
        try
        {
            var receipt = await cache.WriteAsync(CacheNamespace, value, DocumentFingerprint.Missing).WaitAsync(Deadline);
            await entered.Task.WaitAsync(Deadline);
            Check(!release.Task.IsCompleted, "backing write must remain under test control");
            Check(receipt == DocumentFingerprint.FromBytes(value), "memory commit must return its content receipt");
            HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), value);
            byte[] businessValue = Bytes("business");
            IDocumentStore businessStore = business;
            var committed = await businessStore.WriteAsync("test.business", businessValue, DocumentFingerprint.Missing)
                .WaitAsync(Deadline);
            Check(committed == DocumentFingerprint.FromBytes(businessValue), "independent business commit must not wait for cache");
            HasValue(await businessStore.ReadAsync("test.business").WaitAsync(Deadline), businessValue);
            Check(!release.Task.IsCompleted, "business commit must finish before slow cache persistence");
        }
        finally
        {
            release.TrySetResult();
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        HasValue(await disk.ReadAsync(CacheNamespace), value);
    }

    private static async Task OrderedDrain()
    {
        var firstEntered = Signal();
        var firstRelease = Signal();
        var secondEntered = Signal();
        var secondRelease = Signal();
        byte[] first = Bytes("first"), second = Bytes("second");
        var disk = new FakeStore
        {
            BeforeWrite = async (_, data) =>
            {
                if (data.SequenceEqual(first))
                {
                    firstEntered.TrySetResult();
                    await firstRelease.Task;
                }
                else
                {
                    Check(data.SequenceEqual(second), "unexpected persisted payload");
                    secondEntered.TrySetResult();
                    await secondRelease.Task;
                }
            }
        };
        var cache = new CacheDocumentStore(disk);
        try
        {
            var receipt = await cache.WriteAsync(CacheNamespace, first, DocumentFingerprint.Missing).WaitAsync(Deadline);
            await firstEntered.Task.WaitAsync(Deadline);
            await cache.WriteAsync(CacheNamespace, second, receipt).WaitAsync(Deadline);
            HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), second);
            var drain = cache.DrainAsync();
            Check(!drain.IsCompleted, "drain must wait for accepted persistence");
            firstRelease.TrySetResult();
            await secondEntered.Task.WaitAsync(Deadline);
            HasValue(await disk.ReadAsync(CacheNamespace), first);
            Check(!drain.IsCompleted, "drain must include the second admitted write");
            secondRelease.TrySetResult();
            await drain.WaitAsync(Deadline);
            HasValue(await disk.ReadAsync(CacheNamespace), second);
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static async Task WriteFailure()
    {
        int writes = 0;
        var disk = new FakeStore
        {
            BeforeWrite = (_, _) => Interlocked.Increment(ref writes) == 1
                ? Task.FromException(new DocumentStoreException(DocumentStoreError.Storage)) : Task.CompletedTask
        };
        await using var cache = new CacheDocumentStore(disk);
        byte[] first = Bytes("not persisted"), second = Bytes("persisted");
        var receipt = await cache.WriteAsync(CacheNamespace, first, DocumentFingerprint.Missing).WaitAsync(Deadline);
        await cache.DrainAsync().WaitAsync(Deadline);
        Check(writes == 1, "the failing persistence must actually run");
        Check((await disk.ReadAsync(CacheNamespace)).Data is null, "failed write must not silently commit");
        HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), first);
        await cache.WriteAsync(CacheNamespace, second, receipt).WaitAsync(Deadline);
        await cache.DrainAsync().WaitAsync(Deadline);
        Check(writes == 2, "failure must not prevent the next persistence attempt");
        HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), second);
        HasValue(await disk.ReadAsync(CacheNamespace), second);
    }

    private static async Task LateRead()
    {
        var entered = Signal();
        var release = Signal();
        int reads = 0;
        byte[] oldValue = Bytes("old"), newValue = Bytes("new");
        var disk = new FakeStore
        {
            ReadOverride = async (_, _) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return new(oldValue.ToArray(), DocumentFingerprint.FromBytes(oldValue));
            }
        };
        await disk.WriteAsync(CacheNamespace, oldValue, DocumentFingerprint.Missing);
        var cache = new CacheDocumentStore(disk);
        try
        {
            var reading = cache.ReadAsync(CacheNamespace);
            await entered.Task.WaitAsync(Deadline);
            await cache.WriteAsync(CacheNamespace, newValue, DocumentFingerprint.Missing).WaitAsync(Deadline);
            HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), newValue);
            release.TrySetResult();
            HasValue(await reading.WaitAsync(Deadline), newValue);
            await cache.DrainAsync().WaitAsync(Deadline);
            HasValue(await cache.ReadAsync(CacheNamespace).WaitAsync(Deadline), newValue);
            HasValue(disk.Snapshot(CacheNamespace), newValue);
        }
        finally
        {
            release.TrySetResult();
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static async Task Close()
    {
        var entered = Signal();
        var release = Signal();
        var disk = new FakeStore
        {
            BeforeWrite = async (_, _) => { entered.TrySetResult(); await release.Task; }
        };
        var cache = new CacheDocumentStore(() => disk);
        byte[] first = Bytes("accepted first"), second = Bytes("accepted second");
        Task? closing = null;
        try
        {
            var receipt = await cache.WriteAsync(CacheNamespace, first, DocumentFingerprint.Missing).WaitAsync(Deadline);
            await entered.Task.WaitAsync(Deadline);
            await cache.WriteAsync(CacheNamespace, second, receipt).WaitAsync(Deadline);
            closing = cache.DisposeAsync().AsTask();
            Check(!closing.IsCompleted, "dispose must wait for an admitted backing write");
            Check(!disk.Disposed, "backing cannot be disposed before accepted writes settle");
            await Closed(() => cache.ReadAsync(CacheNamespace));
            await Closed(() => cache.WriteAsync(CacheNamespace, Bytes("rejected"), DocumentFingerprint.FromBytes(second)));
            release.TrySetResult();
            await closing.WaitAsync(Deadline);
            Check(disk.Disposed, "owned backing must be disposed after drain");
            HasValue(disk.Snapshot(CacheNamespace), second);
            await Closed(() => cache.ReadAsync(CacheNamespace));
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        finally
        {
            release.TrySetResult();
            await (closing ?? cache.DisposeAsync().AsTask()).WaitAsync(Deadline);
        }
    }

    private static async Task Closed(Func<Task> operation)
    {
        try { await operation().WaitAsync(Deadline); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Closed) { return; }
        throw new InvalidOperationException("closed cache must reject new admission");
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void HasValue(DocumentSnapshot snapshot, byte[] expected)
    {
        Check(snapshot.Data is not null && snapshot.Data.SequenceEqual(expected), "unexpected document bytes");
        Check(snapshot.Fingerprint == DocumentFingerprint.FromBytes(expected), "unexpected document fingerprint");
    }

    private sealed class FakeStore : IDocumentStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, DocumentSnapshot> documents = new(StringComparer.Ordinal);
        private bool disposed;
        internal Func<string, CancellationToken, Task<DocumentSnapshot>>? ReadOverride { get; init; }
        internal Func<string, byte[], Task>? BeforeWrite { get; init; }
        internal bool Disposed { get { lock (gate) return disposed; } }

        internal DocumentSnapshot Snapshot(string ns)
        {
            lock (gate)
                return documents.TryGetValue(ns, out var value)
                    ? new(value.Data?.ToArray(), value.Fingerprint) : new(null, DocumentFingerprint.Missing);
        }

        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) if (disposed) throw new DocumentStoreException(DocumentStoreError.Closed);
            return ReadOverride?.Invoke(ns, cancellationToken) ?? Task.FromResult(Snapshot(ns));
        }

        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = data.ToArray();
            if (BeforeWrite is not null) await BeforeWrite(ns, bytes);
            lock (gate)
            {
                if (disposed) throw new DocumentStoreException(DocumentStoreError.Closed);
                var before = Snapshot(ns);
                if (before.Fingerprint != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
                var fingerprint = DocumentFingerprint.FromBytes(bytes);
                documents[ns] = new(bytes, fingerprint);
                return fingerprint;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate) disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}
