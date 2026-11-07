using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowFilm.AppHost;
using MeowFilm.Platform.Storage;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Windows.Tests;

public static class StorageTests
{
    private const string Ns = "test.documents.v1", Other = "test.other.v1";
    private const int Maximum = LocalDocumentStore.MaximumDocumentBytes;
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("storage.local.raw-json-edit-length-and-reopen", LocalRoundTrip);
        yield return new("storage.local.exact-sensitive-list-mixed-with-plain", LocalMixedProtection);
        yield return new("storage.local.protected-list-must-be-exact-and-registered", LocalProtectionConfiguration);
        yield return new("storage.local.case-and-device-name-namespaces", LocalNames);
        yield return new("storage.local.cas-atomic-reader-caller-snapshot", LocalAtomic);
        yield return new("storage.local.failed-rename-preserves-old-cleans-own-stage", LocalRenameFailure);
        yield return new("storage.local.concurrent-store-instances-cas", LocalConcurrent);
        yield return new("storage.local.cancel-close-lock-contention", LocalCancellation);
        yield return new("storage.local.accepted-writes-FIFO-survive-cancel-and-close", () => QueuedWrites(false));
        yield return new("storage.dpapi.accepted-writes-FIFO-survive-cancel-and-close", () => QueuedWrites(true));
        yield return new("storage.local.document-eight-mib-limit-on-read-and-write", LocalMaximum);
        yield return new("storage.local.unknown-files-and-stages-preserved", LocalUnknownFiles);
        yield return new("storage.local.complete-source-sixteen-mib-edit-and-cas", LocalSource);
        yield return new("storage.local.plain-junction-and-hardlink-rejected", LocalPathIsolation);
        yield return new("storage.dpapi.absent-empty-binary-reopen", DpapiRoundTrip);
        yield return new("storage.dpapi.product-and-namespace-binding", DpapiBinding);
        yield return new("storage.dpapi.case-and-device-name-namespaces", DpapiNames);
        yield return new("storage.dpapi.randomized-ciphertext-no-plaintext", DpapiCiphertext);
        yield return new("storage.dpapi.cas-atomic-reader-caller-snapshot", DpapiAtomic);
        yield return new("storage.dpapi.atomic-replace-retains-multiple-reader-generations", DpapiReaderGenerations);
        yield return new("storage.dpapi.failed-rename-preserves-old-cleans-stage", DpapiRenameFailure);
        yield return new("storage.dpapi.readonly-destination-preserved-and-retry", DpapiReadOnlyDestination);
        yield return new("storage.dpapi.rename-exact-path-repeated-unicode-lengths", DpapiRenameNames);
        foreach (int length in new[] { 148, 149, 150, 280, 1024 })
        { int captured = length; yield return new("storage.dpapi.long-path-root-" + captured, () => DpapiLongPath(captured)); }
        yield return new("storage.dpapi.long-path-unsafe-inputs-rejected", DpapiLongPathUnsafe);
        yield return new("storage.dpapi.long-path-junction-root-ancestor-namespace-rejected", DpapiLongPathJunction);
        yield return new("storage.dpapi.long-path-hardlink-document-rejected", DpapiLongPathHardlink);
        yield return new("storage.dpapi.concurrent-store-instances-cas", DpapiConcurrent);
        yield return new("storage.dpapi.cancel-close-lock-contention", DpapiCancellation);
        yield return new("storage.dpapi.maximum-and-oversize", DpapiMaximum);
        yield return new("storage.dpapi.unknown-file-is-not-empty", DpapiUnknownFile);
        yield return new("storage.dpapi.root-passive-pinned-and-path-validation", DpapiRoot);
        yield return new("storage.dpapi.directory-pins-share-write-not-delete", DpapiDirectorySharing);
        yield return new("storage.dpapi.junction-root-and-ancestor-rejected", DpapiJunctionRoot);
        yield return new("storage.dpapi.junction-namespace-rejected", DpapiJunctionNamespace);
        yield return new("storage.dpapi.hardlink-document-rejected", DpapiHardlink);
        yield return new("storage.dpapi.document-directory-rejected", DpapiDocumentDirectory);
        foreach (string variant in new[] { "magic", "version", "length", "truncated", "ciphertext", "plaintext" })
        { string captured = variant; yield return new("storage.dpapi.corruption-" + captured, () => DpapiCorruption(captured)); }
        string[] invalid = ["", "../escape", "a..b", "a/b", "a\\b", ".hidden", "_hidden", "a:b", "a\0b", "a\n", "中文", new string('a', 129)];
        for (int i = 0; i < invalid.Length; i++)
        { string captured = invalid[i]; yield return new("storage.namespace.invalid-" + i, () => InvalidNamespace(captured)); }
    }

    private static async Task LocalRoundTrip()
    {
        using var fixture = new Temp();
        byte[] edited = "{\"edited\":true,\"differentLength\":12345}"u8.ToArray();
        DocumentFingerprint original;
        await using (var store = new LocalDocumentStore(fixture.Root, [Ns]))
        {
            var missing = await store.ReadAsync(Ns);
            Assert(missing.Data is null && missing.Fingerprint == DocumentFingerprint.Missing);
            var empty = await store.WriteAsync(Ns, Array.Empty<byte>(), missing.Fingerprint);
            var readEmpty = await store.ReadAsync(Ns);
            Assert(empty.Exists && empty.Length == 0 && readEmpty.Data is { Length: 0 } && readEmpty.Fingerprint == empty);
            Assert(new FileInfo(fixture.PlainDocument(Ns)).Length == 0);
            byte[] json = "{\"value\":1}"u8.ToArray();
            original = await store.WriteAsync(Ns, json, empty);
            Equal(json, File.ReadAllBytes(fixture.PlainDocument(Ns)));
            File.WriteAllBytes(fixture.PlainDocument(Ns), edited);
            var read = await store.ReadAsync(Ns);
            Equal(edited, read.Data!);
            Assert(read.Fingerprint == DocumentFingerprint.FromBytes(edited) && read.Fingerprint.Length != original.Length);
        }
        await using var reopened = new LocalDocumentStore(fixture.Root, [Ns]);
        var restored = await reopened.ReadAsync(Ns);
        Equal(edited, restored.Data!);
        await StoreError(() => reopened.WriteAsync(Ns, "stale"u8.ToArray(), original), DocumentStoreError.Conflict);
        // Raw documents do not interpret a magic-looking prefix as an envelope.
        byte[] raw = "MEOWDPAP plain user-edited bytes"u8.ToArray();
        var next = await reopened.WriteAsync(Ns, raw, restored.Fingerprint);
        Equal(raw, File.ReadAllBytes(fixture.PlainDocument(Ns)));
        Assert((await reopened.ReadAsync(Ns)).Fingerprint == next);
    }
    private static async Task LocalMixedProtection()
    {
        using var fixture = new Temp();
        const string account = "music.accounts.v1", device = "music.platform-context.v1", ordinary = account + ".notes";
        Assert(AppIdentity.ProtectedNamespaces.SequenceEqual(new[] { account, device }));
        string[] names = [Ns, account, device, ordinary];
        byte[] plain = "{\"generated\":\"fixture-value\"}"u8.ToArray();
        await using (var store = new LocalDocumentStore(fixture.Root, names, protectedNamespaces: AppIdentity.ProtectedNamespaces))
        {
            foreach (string ns in names) await store.WriteAsync(ns, plain, DocumentFingerprint.Missing);
            foreach (string ns in new[] { account, device })
            {
                byte[] disk = File.ReadAllBytes(fixture.Document(ns));
                Assert(disk.AsSpan(0, 8).SequenceEqual("MEOWDPAP"u8) && disk[8] == 1 && disk.AsSpan().IndexOf(plain) < 0);
                Equal(plain, (await store.ReadAsync(ns)).Data!);
            }
            foreach (string ns in new[] { Ns, ordinary })
                Equal(plain, File.ReadAllBytes(fixture.PlainDocument(ns)));
            string unknown = Path.Combine(fixture.Namespace(account), "unknown.json");
            File.WriteAllBytes(unknown, [42]);
            await StoreError(() => store.ReadAsync(account), DocumentStoreError.Corrupt);
            Equal(plain, (await store.ReadAsync(Ns)).Data!);
            Equal(plain, (await store.ReadAsync(device)).Data!);
            Equal([42], File.ReadAllBytes(unknown));
        }
        await using var reopened = new LocalDocumentStore(fixture.Root, names, protectedNamespaces: AppIdentity.ProtectedNamespaces);
        Equal(plain, (await reopened.ReadAsync(device)).Data!);
        Equal(plain, (await reopened.ReadAsync(ordinary)).Data!);
    }
    private static async Task LocalProtectionConfiguration()
    {
        using var fixture = new Temp();
        await StoreError(() => Task.FromResult(new LocalDocumentStore(fixture.Root, [Ns],
            protectedNamespaces: [Other])), DocumentStoreError.InvalidRequest);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(fixture.Root, [Ns],
            protectedNamespaces: [Ns, Ns])), DocumentStoreError.InvalidRequest);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(fixture.Root, [Ns],
            protectedNamespaces: ["../escape"])), DocumentStoreError.InvalidRequest);
        Assert(!Directory.EnumerateFileSystemEntries(fixture.Root).Any());
    }
    private static async Task LocalNames()
    {
        using var fixture = new Temp();
        string[] names = ["Alpha", "alpha", "CON", "a.", new string('a', 128)];
        await using var store = new LocalDocumentStore(fixture.Root, names);
        for (int i = 0; i < names.Length; i++) await store.WriteAsync(names[i], new byte[] { (byte)i }, DocumentFingerprint.Missing);
        for (int i = 0; i < names.Length; i++)
        {
            Equal([(byte)i], (await store.ReadAsync(names[i])).Data!);
            Equal([(byte)i], File.ReadAllBytes(fixture.PlainDocument(names[i])));
        }
        Assert(Directory.GetDirectories(fixture.Root).Length == names.Length);
        await StoreError(() => store.ReadAsync(Other), DocumentStoreError.Unavailable);
    }
    private static async Task LocalAtomic()
    {
        using var fixture = new Temp(); await using var store = new LocalDocumentStore(fixture.Root, [Ns]);
        var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
        using var reader = new FileStream(fixture.PlainDocument(Ns), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        byte[] next = "new"u8.ToArray();
        var write = store.WriteAsync(Ns, next, old); next.AsSpan().Fill(99);
        var fingerprint = await write;
        Equal("new"u8.ToArray(), (await store.ReadAsync(Ns)).Data!);
        byte[] held = new byte[reader.Length]; reader.ReadExactly(held); Equal("old"u8.ToArray(), held);
        await StoreError(() => store.WriteAsync(Ns, "stale"u8.ToArray(), old), DocumentStoreError.Conflict);
        Assert((await store.ReadAsync(Ns)).Fingerprint == fingerprint);
        Equal("new"u8.ToArray(), File.ReadAllBytes(fixture.PlainDocument(Ns)));
    }
    private static async Task LocalRenameFailure()
    {
        using var fixture = new Temp(); await using var store = new LocalDocumentStore(fixture.Root, [Ns]);
        var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
        string directory = Path.GetDirectoryName(fixture.PlainDocument(Ns))!;
        using (var block = new FileStream(fixture.PlainDocument(Ns), FileMode.Open, FileAccess.Read, FileShare.Read))
            await StoreError(() => store.WriteAsync(Ns, "next"u8.ToArray(), old), DocumentStoreError.Storage);
        Equal("old"u8.ToArray(), File.ReadAllBytes(fixture.PlainDocument(Ns)));
        Assert((await store.ReadAsync(Ns)).Fingerprint == old && Directory.GetFiles(directory).Length == 1);
        var next = await store.WriteAsync(Ns, "next"u8.ToArray(), old);
        Assert((await store.ReadAsync(Ns)).Fingerprint == next && Directory.GetFiles(directory).Length == 1);
        Equal("next"u8.ToArray(), File.ReadAllBytes(fixture.PlainDocument(Ns)));
    }
    private static async Task LocalConcurrent()
    {
        using var fixture = new Temp();
        await using var one = new LocalDocumentStore(fixture.Root, [Ns]);
        await using var two = new LocalDocumentStore(fixture.Root, [Ns]);
        async Task<bool> Write(IDocumentStore store, byte value)
        {
            try { await store.WriteAsync(Ns, new byte[] { value }, DocumentFingerprint.Missing); return true; }
            catch (DocumentStoreException e) when (e.Error == DocumentStoreError.Conflict) { return false; }
        }
        bool[] results = await Task.WhenAll(Write(one, 1), Write(two, 2));
        Assert(results.Count(result => result) == 1);
        Equal([results[0] ? (byte)1 : (byte)2], (await one.ReadAsync(Ns)).Data!);
    }
    private static async Task LocalCancellation()
    {
        using var fixture = new Temp(); var store = new LocalDocumentStore(fixture.Root, [Ns]);
        try
        {
            var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => store.WriteAsync(Ns, "next"u8.ToArray(), old, cancelled.Token));
            using (var held = new FileStream(Path.Combine(fixture.Root, ".documents.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Cancelled(() => store.ReadAsync(Ns, timeout.Token));
            }
            Assert((await store.ReadAsync(Ns)).Fingerprint == old);
            await store.DisposeAsync();
            await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.Closed);
        }
        finally { await store.DisposeAsync(); }
    }
    private static async Task QueuedWrites(bool protect)
    {
        using var fixture = new Temp();
        await using var store = new LocalDocumentStore(fixture.Root, [Ns], protectedNamespaces: protect ? [Ns] : []);
        var initial = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
        using var cancellation = new CancellationTokenSource();
        Task<DocumentFingerprint> first, second, conflict, final;
        Task close;
        using (var held = new FileStream(Path.Combine(fixture.Root, ".documents.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var bytes = "first"u8.ToArray();
            first = store.WriteAsync(Ns, bytes, initial, cancellation.Token);
            bytes.AsSpan().Fill(0); // Queued operation must own the input at admission.
            second = store.WriteAsync(Ns, "second"u8.ToArray(), DocumentFingerprint.FromBytes("first"u8), cancellation.Token);
            conflict = store.WriteAsync(Ns, "wrong"u8.ToArray(), initial, cancellation.Token);
            final = store.WriteAsync(Ns, "final"u8.ToArray(), DocumentFingerprint.FromBytes("second"u8), cancellation.Token);
            cancellation.Cancel();
            close = store.DisposeAsync().AsTask();
            Assert(!close.IsCompleted && !first.IsCompleted && !final.IsCompleted);
            await StoreError(() => store.WriteAsync(Ns, "denied"u8.ToArray(), initial), DocumentStoreError.Closed);
        }
        Assert(await first == DocumentFingerprint.FromBytes("first"u8));
        Assert(await second == DocumentFingerprint.FromBytes("second"u8));
        await StoreError(() => conflict, DocumentStoreError.Conflict);
        Assert(await final == DocumentFingerprint.FromBytes("final"u8));
        await close;
        await using var reopened = new LocalDocumentStore(fixture.Root, [Ns], protectedNamespaces: protect ? [Ns] : []);
        Equal("final"u8.ToArray(), (await reopened.ReadAsync(Ns)).Data!);
    }
    private static async Task LocalMaximum()
    {
        using var fixture = new Temp(); await using var store = new LocalDocumentStore(fixture.Root, [Ns]);
        byte[] data = new byte[Maximum]; data.AsSpan().Fill((byte)' ');
        var next = await store.WriteAsync(Ns, data, DocumentFingerprint.Missing);
        Equal(data, File.ReadAllBytes(fixture.PlainDocument(Ns)));
        Equal(data, (await store.ReadAsync(Ns)).Data!);
        await StoreError(() => store.WriteAsync(Ns, new byte[Maximum + 1], next), DocumentStoreError.Limit);
        Assert((await store.ReadAsync(Ns)).Fingerprint == next);
        using (var file = new FileStream(fixture.PlainDocument(Ns), FileMode.Open, FileAccess.Write))
            file.SetLength(Maximum + 1);
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.Limit);
        await StoreError(() => store.WriteAsync(Ns, "x"u8.ToArray(), next), DocumentStoreError.Limit);
        Assert(new FileInfo(fixture.PlainDocument(Ns)).Length == Maximum + 1);
    }
    private static async Task LocalUnknownFiles()
    {
        using var fixture = new Temp();
        await using (var store = new LocalDocumentStore(fixture.Root, [Ns, Other]))
        {
            var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
            string directory = Path.GetDirectoryName(fixture.PlainDocument(Ns))!;
            string stage = Path.Combine(directory, "stage-generated.json"), unknown = Path.Combine(directory, "notes.txt");
            File.WriteAllBytes(stage, [10, 20]); File.WriteAllBytes(unknown, [30]);
            Directory.CreateDirectory(Path.Combine(directory, "unknown-directory"));
            Assert((await store.ReadAsync(Ns)).Fingerprint == old);
            await store.WriteAsync(Ns, "new"u8.ToArray(), old);
            Equal([10, 20], File.ReadAllBytes(stage)); Equal([30], File.ReadAllBytes(unknown));
            string otherDirectory = fixture.PlainNamespace(Other); Directory.CreateDirectory(otherDirectory);
            File.WriteAllBytes(Path.Combine(otherDirectory, "stage-existing.json"), [40]);
            Assert((await store.ReadAsync(Other)).Fingerprint == DocumentFingerprint.Missing);
            await store.WriteAsync(Other, "other"u8.ToArray(), DocumentFingerprint.Missing);
            Equal([40], File.ReadAllBytes(Path.Combine(otherDirectory, "stage-existing.json")));
        }
        await using var reopened = new LocalDocumentStore(fixture.Root, [Ns, Other]);
        Equal("new"u8.ToArray(), (await reopened.ReadAsync(Ns)).Data!);
        Equal("other"u8.ToArray(), (await reopened.ReadAsync(Other)).Data!);
    }
    private static async Task LocalSource()
    {
        using var fixture = new Temp();
        const string collection = "music.script-sources.v2";
        byte[] original = "globalThis.value = 1;"u8.ToArray(), edited = "globalThis.value = 23456789;"u8.ToArray();
        string importHash = Hash(original), ns = collection + "." + importHash;
        string path;
        await using (var store = new LocalDocumentStore(fixture.Root, [Ns], [collection]))
        {
            var first = await store.WriteAsync(ns, original, DocumentFingerprint.Missing);
            path = Directory.GetFiles(fixture.Root, "*.js", SearchOption.AllDirectories).Single();
            Assert(Path.GetFileName(path) == importHash + ".js" && Path.GetFileName(Path.GetDirectoryName(path)!).Contains(collection, StringComparison.Ordinal));
            Equal(original, File.ReadAllBytes(path));
            byte[] maximum = new byte[LocalDocumentStore.MaximumSourceBytes]; maximum.AsSpan().Fill((byte)' ');
            original.CopyTo(maximum, 0);
            var maximumWrite = await store.WriteAsync(ns, maximum, first);
            Assert(maximumWrite.Length == 16 * 1024 * 1024 && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.js").Length == 1);
            Equal(maximum, File.ReadAllBytes(path));
            maximum[^1] = (byte)'\n';
            File.WriteAllBytes(path, maximum);
            var read = await store.ReadAsync(ns);
            Assert(read.Fingerprint.Length == 16 * 1024 * 1024 && read.Fingerprint != maximumWrite && read.Fingerprint.Sha256 != importHash);
            Equal(maximum, read.Data!);
            // A CAS base over 8 MiB is valid for a source, but never for an ordinary document.
            await StoreError(() => store.WriteAsync(Ns, edited, read.Fingerprint), DocumentStoreError.InvalidRequest);
            using (var block = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await StoreError(() => store.WriteAsync(ns, edited, read.Fingerprint), DocumentStoreError.Storage);
            Assert((await store.ReadAsync(ns)).Fingerprint == read.Fingerprint &&
                Directory.GetFiles(Path.GetDirectoryName(path)!, "*.js").Length == 1);
            Equal(maximum, File.ReadAllBytes(path));
            var next = await store.WriteAsync(ns, edited, read.Fingerprint);
            Equal(edited, File.ReadAllBytes(path));
            await StoreError(() => store.WriteAsync(ns, original, first), DocumentStoreError.Conflict);
            await StoreError(() => store.WriteAsync(ns, new byte[LocalDocumentStore.MaximumSourceBytes + 1], next), DocumentStoreError.Limit);
            File.WriteAllBytes(path, new byte[LocalDocumentStore.MaximumSourceBytes + 1]);
            await StoreError(() => store.ReadAsync(ns), DocumentStoreError.Limit);
            File.WriteAllBytes(path, edited);
            string sibling = collection + "." + Hash("another source"u8.ToArray());
            await store.WriteAsync(sibling, "another source"u8.ToArray(), DocumentFingerprint.Missing);
            string stage = Path.Combine(Path.GetDirectoryName(path)!, "stage-interrupted.js"); File.WriteAllBytes(stage, [99]);
            Equal(edited, (await store.ReadAsync(ns)).Data!);
            Equal("another source"u8.ToArray(), (await store.ReadAsync(sibling)).Data!);
            Equal([99], File.ReadAllBytes(stage));
            await StoreError(() => store.ReadAsync(ns + ".0"), DocumentStoreError.Unavailable);
            await StoreError(() => store.ReadAsync(collection + "." + new string('g', 64)), DocumentStoreError.Unavailable);
            await StoreError(() => store.ReadAsync("unregistered.sources." + importHash), DocumentStoreError.Unavailable);
            Assert(!Directory.GetFiles(fixture.Root, "*.mfdp", SearchOption.AllDirectories).Any());
        }
        byte[] reopenedEdit = "globalThis.value = 9876543210; // edited before reopen"u8.ToArray();
        File.WriteAllBytes(path, reopenedEdit);
        await using var reopened = new LocalDocumentStore(fixture.Root, [Ns], [collection]);
        var restored = await reopened.ReadAsync(ns);
        Equal(reopenedEdit, restored.Data!);
        Assert(restored.Fingerprint.Length == reopenedEdit.Length && restored.Fingerprint.Sha256 != importHash);
    }
    private static async Task LocalPathIsolation()
    {
        using var fixture = new Temp(); await using var store = new LocalDocumentStore(fixture.Root, [Ns, Other]);
        var old = await store.WriteAsync(Ns, "x"u8.ToArray(), DocumentFingerprint.Missing);
        string alias = Path.Combine(fixture.Base, "alias.json");
        Assert(CreateHardLinkW(alias, fixture.PlainDocument(Ns), IntPtr.Zero), "Hardlink fixture creation failed.");
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.UnsafePath);
        await StoreError(() => store.WriteAsync(Ns, "y"u8.ToArray(), old), DocumentStoreError.UnsafePath);
        Equal("x"u8.ToArray(), File.ReadAllBytes(alias));
        string target = Path.Combine(fixture.Base, "target"); Directory.CreateDirectory(target);
        CreateJunction(fixture.PlainNamespace(Other), target);
        await StoreError(() => store.ReadAsync(Other), DocumentStoreError.UnsafePath);
        await StoreError(() => store.WriteAsync(Other, "z"u8.ToArray(), DocumentFingerprint.Missing), DocumentStoreError.UnsafePath);
        Assert(!Directory.EnumerateFileSystemEntries(target).Any());
    }

    private static async Task DpapiRoundTrip()
    {
        using var fixture = new Temp();
        await using (var store = fixture.Store())
        {
            var missing = await store.ReadAsync(Ns);
            Assert(missing.Data is null && missing.Fingerprint == DocumentFingerprint.Missing);
            var empty = await store.WriteAsync(Ns, Array.Empty<byte>(), missing.Fingerprint);
            Assert(empty.Exists && empty.Length == 0 && empty.Sha256 == Hash([]));
            var read = await store.ReadAsync(Ns);
            Assert(read.Data is { Length: 0 } && read.Fingerprint == empty);
            await store.WriteAsync(Ns, new byte[] { 0, 255, 128, 0, 42, 10 }, empty);
        }
        await using var reopened = fixture.Store();
        Equal([0, 255, 128, 0, 42, 10], (await reopened.ReadAsync(Ns)).Data!);
    }
    private static async Task DpapiBinding()
    {
        using var first = new Temp(); using var second = new Temp();
        await using var one = first.Store(); await using var two = second.Store();
        await one.WriteAsync(Ns, "first"u8.ToArray(), DocumentFingerprint.Missing);
        await one.WriteAsync(Other, "second"u8.ToArray(), DocumentFingerprint.Missing);
        await two.WriteAsync(Ns, "third"u8.ToArray(), DocumentFingerprint.Missing);
        byte[] bytes = File.ReadAllBytes(first.Document(Ns));
        File.WriteAllBytes(first.Document(Other), bytes); File.WriteAllBytes(second.Document(Ns), bytes);
        await StoreError(() => one.ReadAsync(Other), DocumentStoreError.Protection);
        await StoreError(() => two.ReadAsync(Ns), DocumentStoreError.Protection);
        await StoreError(() => one.WriteAsync(Other, "new"u8.ToArray(), DocumentFingerprint.Missing), DocumentStoreError.Protection);
        Equal(bytes, File.ReadAllBytes(first.Document(Other)));
    }
    private static async Task DpapiNames()
    {
        using var fixture = new Temp();
        string[] names = ["Alpha", "alpha", "CON", "a.", new string('a', 128)];
        await using var store = new LocalDocumentStore(fixture.Root, names, protectedNamespaces: names);
        for (int i = 0; i < names.Length; i++) await store.WriteAsync(names[i], new byte[] { (byte)i }, DocumentFingerprint.Missing);
        for (int i = 0; i < names.Length; i++) Equal([(byte)i], (await store.ReadAsync(names[i])).Data!);
        Assert(Directory.GetDirectories(fixture.Root).Length == names.Length);
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.Unavailable);
    }
    private static async Task DpapiCiphertext()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        byte[] plain = Encoding.UTF8.GetBytes("{\"测试\":\"generated-" + Guid.NewGuid().ToString("N") + "\"}");
        var fingerprint = await store.WriteAsync(Ns, plain, DocumentFingerprint.Missing);
        byte[] first = File.ReadAllBytes(fixture.Document(Ns));
        await store.WriteAsync(Ns, plain, fingerprint);
        byte[] second = File.ReadAllBytes(fixture.Document(Ns));
        Assert(!first.SequenceEqual(second) && first.AsSpan().IndexOf(plain) < 0 && second.AsSpan().IndexOf(plain) < 0);
        Assert(first.AsSpan(0, 8).SequenceEqual("MEOWDPAP"u8) && first[8] == 1);
        Assert(Directory.GetFiles(fixture.Namespace(Ns)).Length == 1);
    }
    private static async Task DpapiAtomic()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
        byte[] ciphertext = File.ReadAllBytes(fixture.Document(Ns));
        using var reader = new FileStream(fixture.Document(Ns), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] next = "new"u8.ToArray();
        var write = store.WriteAsync(Ns, next, old); next.AsSpan().Fill(99);
        var fingerprint = await write;
        Equal("new"u8.ToArray(), (await store.ReadAsync(Ns)).Data!);
        byte[] held = new byte[reader.Length]; reader.ReadExactly(held); Equal(ciphertext, held);
        await StoreError(() => store.WriteAsync(Ns, "stale"u8.ToArray(), old), DocumentStoreError.Conflict);
        Assert((await store.ReadAsync(Ns)).Fingerprint == fingerprint);
    }
    private static async Task DpapiRenameFailure()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
        byte[] bytes = File.ReadAllBytes(fixture.Document(Ns));
        using (var block = new FileStream(fixture.Document(Ns), FileMode.Open, FileAccess.Read, FileShare.Read))
            await StoreError(() => store.WriteAsync(Ns, "next"u8.ToArray(), old), DocumentStoreError.Storage);
        Equal(bytes, File.ReadAllBytes(fixture.Document(Ns)));
        Assert(Directory.GetFiles(fixture.Namespace(Ns)).Length == 1);
        Assert((await store.ReadAsync(Ns)).Fingerprint == old);
        var next = await store.WriteAsync(Ns, "next"u8.ToArray(), old);
        var restored = await store.ReadAsync(Ns);
        Assert(restored.Fingerprint == next && next != old);
        Equal("next"u8.ToArray(), restored.Data!);
        Assert(Directory.GetFiles(fixture.Namespace(Ns)).Length == 1, "Retry left an encrypted stage behind.");
    }
    private static async Task DpapiReaderGenerations()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        var fingerprint = await store.WriteAsync(Ns, new byte[] { 0 }, DocumentFingerprint.Missing);
        var readers = new List<(FileStream File, byte[] Ciphertext)>();
        try
        {
            for (byte revision = 1; revision <= 8; revision++)
            {
                byte[] old = File.ReadAllBytes(fixture.Document(Ns));
                // DELETE sharing is sufficient; concurrent writes to the file are not
                // granted. Every retained generation must survive subsequent commits.
                var reader = new FileStream(fixture.Document(Ns), FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
                readers.Add((reader, old));
                byte[] data = [revision, 0, 255, revision];
                fingerprint = await store.WriteAsync(Ns, data, fingerprint);
                var restored = await store.ReadAsync(Ns);
                Assert(restored.Fingerprint == fingerprint);
                Equal(data, restored.Data!);
                Assert(Directory.GetFiles(fixture.Namespace(Ns)).Length == 1);
                foreach (var held in readers)
                {
                    Assert(held.File.Length == held.Ciphertext.Length);
                    held.File.Position = 0;
                    var bytes = new byte[held.Ciphertext.Length];
                    held.File.ReadExactly(bytes);
                    Equal(held.Ciphertext, bytes);
                }
            }
        }
        finally { foreach (var held in readers) held.File.Dispose(); }
    }
    private static async Task DpapiReadOnlyDestination()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
        string path = fixture.Document(Ns);
        byte[] ciphertext = File.ReadAllBytes(path);
        var originalAttributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);
            await StoreError(() => store.WriteAsync(Ns, "new"u8.ToArray(), old), DocumentStoreError.Storage);
            Equal(ciphertext, File.ReadAllBytes(path));
            Assert((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "Replacement cleared read-only protection.");
            Assert(Directory.GetFiles(fixture.Namespace(Ns)).Length == 1);
            Assert((await store.ReadAsync(Ns)).Fingerprint == old);
        }
        finally { File.SetAttributes(path, originalAttributes); }
        var next = await store.WriteAsync(Ns, "new"u8.ToArray(), old);
        var restored = await store.ReadAsync(Ns);
        Assert(restored.Fingerprint == next && next != old);
        Equal("new"u8.ToArray(), restored.Data!);
    }
    private static async Task DpapiRenameNames()
    {
        using var fixture = new Temp();
        // Exercise multiple native WCHAR path lengths and allocator reuse. A successful
        // syscall must never commit under a suffix read beyond the FileName allocation.
        for (int round = 0; round < 16; round++)
        {
            string root = Path.Combine(fixture.Root, "文档-" + new string('p', round + 1));
            Directory.CreateDirectory(root);
            await using var store = new LocalDocumentStore(root, [Ns], protectedNamespaces: [Ns]);
            string folder = Path.Combine(root, "n-" + Hash(Encoding.UTF8.GetBytes(Ns)));
            string destination = Path.Combine(folder, "document.mfdp");
            var fingerprint = DocumentFingerprint.Missing;
            for (int revision = 0; revision < 3; revision++)
            {
                byte[] data = new byte[round * 17 + revision + 1];
                data.AsSpan().Fill((byte)(round + revision));
                fingerprint = await store.WriteAsync(Ns, data, fingerprint);
                string[] names = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToArray()!;
                Assert(names.Length == 1 && names[0] == "document.mfdp", "Atomic rename produced an unexpected filename.");
                Assert(File.Exists(destination), "Atomic rename omitted the exact destination.");
                var restored = await store.ReadAsync(Ns);
                Assert(restored.Fingerprint == fingerprint, "Committed fingerprint changed on read.");
                Equal(data, restored.Data!);
            }
        }
    }
    private static async Task DpapiConcurrent()
    {
        using var fixture = new Temp(); await using var one = fixture.Store(); await using var two = fixture.Store();
        async Task<bool> Write(IDocumentStore store, byte value)
        {
            try { await store.WriteAsync(Ns, new byte[] { value }, DocumentFingerprint.Missing); return true; }
            catch (DocumentStoreException e) when (e.Error == DocumentStoreError.Conflict) { return false; }
        }
        bool[] results = await Task.WhenAll(Write(one, 1), Write(two, 2));
        Assert(results.Count(result => result) == 1);
        var actual = await one.ReadAsync(Ns);
        Assert(actual.Data is { Length: 1 } && actual.Data[0] == (results[0] ? 1 : 2));
    }
    private static async Task DpapiLongPath(int rootLength)
    {
        using var fixture = new Temp(compact: true);
        string root = fixture.RootAtLength(rootLength);
        string folder = Path.Combine(root, "n-" + Hash(Encoding.UTF8.GetBytes(Ns)));
        string destination = Path.Combine(folder, "document.mfdp");
        // Fixed boundaries reproduce the integrated Music fixture: root 150,
        // document 231, stage 261. Longer roots also exercise directory and rename APIs.
        Assert(root.Length == rootLength && destination.Length == rootLength + 81);
        Assert(Path.Combine(folder, "stage-" + new string('a', 32) + ".mfdp").Length == rootLength + 111);
        byte[] nextData = [0, 255, 128, 42];
        DocumentFingerprint next;
        await using (var store = new LocalDocumentStore(root, [Ns], protectedNamespaces: [Ns]))
        {
            var missing = await store.ReadAsync(Ns);
            Assert(missing.Data is null && missing.Fingerprint == DocumentFingerprint.Missing);
            var first = await store.WriteAsync(Ns, new byte[] { 1 }, missing.Fingerprint);
            Equal([1], (await store.ReadAsync(Ns)).Data!);
            next = await store.WriteAsync(Ns, nextData, first);
            var restored = await store.ReadAsync(Ns);
            Assert(restored.Fingerprint == next);
            Equal(nextData, restored.Data!);
            byte[] ciphertext = File.ReadAllBytes(destination);
            await StoreError(() => store.WriteAsync(Ns, new byte[] { 2 }, first), DocumentStoreError.Conflict);
            Equal(ciphertext, File.ReadAllBytes(destination));
            Assert(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).SequenceEqual(["document.mfdp"]));
            using var deletion = CreateFileW(@"\\?\" + root, 0x00010000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            int error = Marshal.GetLastPInvokeError();
            Assert(deletion.IsInvalid && error == 32, "Long-path root lost its no-delete pin.");
        }
        await using var reopened = new LocalDocumentStore(root, [Ns], protectedNamespaces: [Ns]);
        var afterReopen = await reopened.ReadAsync(Ns);
        Assert(afterReopen.Fingerprint == next);
        Equal(nextData, afterReopen.Data!);
        if (rootLength == 1024)
            await StoreError(() => Task.FromResult(new LocalDocumentStore(root + "x", [Ns])), DocumentStoreError.UnsafePath);
    }
    private static async Task DpapiLongPathUnsafe()
    {
        using var fixture = new Temp();
        string root = fixture.RootAtLength(280);
        string[] invalid =
        [
            @"\\?\" + root, @"\\.\" + root, @"\??\" + root, @"\\localhost\C$\not-authorized",
            root + @"\..\escape", root + @"\.\child", root + @"\\child", root + @"\file:stream",
            root + @"\trailing.", root + @"\trailing ", root + @"\CON", root + @"\COM1",
            root + "\\COM\u00b9", root + "\\LPT\u00b2", root + "\\control\u0001"
        ];
        foreach (string path in invalid)
            await StoreError(() => Task.FromResult(new LocalDocumentStore(path, [Ns])), DocumentStoreError.UnsafePath);
        Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "Unsafe root request created entries.");
    }
    private static async Task DpapiLongPathJunction()
    {
        using var fixture = new Temp();
        string root = fixture.RootAtLength(280);
        string target = Path.Combine(root, "target"), link = Path.Combine(root, "junction");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        CreateJunction(link, target);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(link, [Ns])), DocumentStoreError.UnsafePath);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(Path.Combine(link, "child"), [Ns])), DocumentStoreError.UnsafePath);
        await using var store = new LocalDocumentStore(root, [Ns], protectedNamespaces: [Ns]);
        CreateJunction(Path.Combine(root, "n-" + Hash(Encoding.UTF8.GetBytes(Ns))), target);
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.UnsafePath);
        await StoreError(() => store.WriteAsync(Ns, new byte[] { 1 }, DocumentFingerprint.Missing), DocumentStoreError.UnsafePath);
        Assert(!Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any(), "Long-path junction target was written.");
    }
    private static async Task DpapiLongPathHardlink()
    {
        using var fixture = new Temp();
        string root = fixture.RootAtLength(280);
        await using var store = new LocalDocumentStore(root, [Ns], protectedNamespaces: [Ns]);
        var old = await store.WriteAsync(Ns, new byte[] { 1 }, DocumentFingerprint.Missing);
        string document = Path.Combine(root, "n-" + Hash(Encoding.UTF8.GetBytes(Ns)), "document.mfdp");
        string alias = Path.Combine(root, "alias.mfdp");
        Assert(CreateHardLinkW(@"\\?\" + alias, @"\\?\" + document, IntPtr.Zero), "Long-path hardlink fixture creation failed.");
        byte[] ciphertext = File.ReadAllBytes(alias);
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.UnsafePath);
        await StoreError(() => store.WriteAsync(Ns, new byte[] { 2 }, old), DocumentStoreError.UnsafePath);
        Equal(ciphertext, File.ReadAllBytes(alias));
    }
    private static async Task DpapiCancellation()
    {
        using var fixture = new Temp(); var store = fixture.Store();
        try
        {
            var old = await store.WriteAsync(Ns, "old"u8.ToArray(), DocumentFingerprint.Missing);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(() => store.WriteAsync(Ns, "next"u8.ToArray(), old, cancelled.Token));
            using (var held = new FileStream(Path.Combine(fixture.Root, ".documents.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await Cancelled(() => store.ReadAsync(Ns, timeout.Token));
            }
            Assert((await store.ReadAsync(Ns)).Fingerprint == old);
            await store.DisposeAsync();
            await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.Closed);
        }
        finally { await store.DisposeAsync(); }
    }
    private static async Task DpapiMaximum()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        byte[] data = new byte[Maximum]; RandomNumberGenerator.Fill(data);
        var next = await store.WriteAsync(Ns, data, DocumentFingerprint.Missing);
        Equal(data, (await store.ReadAsync(Ns)).Data!);
        await StoreError(() => store.WriteAsync(Ns, new byte[Maximum + 1], next), DocumentStoreError.Limit);
        Assert((await store.ReadAsync(Ns)).Fingerprint == next);
    }
    private static async Task DpapiCorruption(string variant)
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        var old = await store.WriteAsync(Ns, "generated"u8.ToArray(), DocumentFingerprint.Missing);
        byte[] bytes = File.ReadAllBytes(fixture.Document(Ns));
        DocumentStoreError error = DocumentStoreError.Corrupt;
        switch (variant)
        {
            case "magic": bytes[0] ^= 1; break;
            case "version": bytes[8] = 255; error = DocumentStoreError.UnsupportedVersion; break;
            case "length": bytes[9] ^= 1; break;
            case "truncated": bytes = bytes[..7]; break;
            case "ciphertext": bytes[^1] ^= 1; error = DocumentStoreError.Protection; break;
            case "plaintext": bytes = "{\"unprotected\":true}"u8.ToArray(); break;
        }
        File.WriteAllBytes(fixture.Document(Ns), bytes);
        await StoreError(() => store.ReadAsync(Ns), error);
        await StoreError(() => store.WriteAsync(Ns, "new"u8.ToArray(), old), error);
        Equal(bytes, File.ReadAllBytes(fixture.Document(Ns)));
    }
    private static async Task DpapiUnknownFile()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        Directory.CreateDirectory(fixture.Namespace(Ns));
        string unknown = Path.Combine(fixture.Namespace(Ns), "stage-generated.mfdp"); File.WriteAllBytes(unknown, [10, 20]);
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.Corrupt);
        await StoreError(() => store.WriteAsync(Ns, "x"u8.ToArray(), DocumentFingerprint.Missing), DocumentStoreError.Corrupt);
        Equal([10, 20], File.ReadAllBytes(unknown)); Assert(!File.Exists(fixture.Document(Ns)));
    }
    private static async Task DpapiRoot()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        Assert(!Directory.EnumerateFileSystemEntries(fixture.Root).Any(), "Store construction created files.");
        bool blocked = false;
        try { Directory.Move(fixture.Root, fixture.Root + "-moved"); } catch (IOException) { blocked = true; }
        Assert(blocked, "Product root could be renamed while its store was open.");
        await StoreError(() => Task.FromResult(new LocalDocumentStore(Path.Combine(fixture.Root, "missing"), [Ns])), DocumentStoreError.UnsafePath);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(fixture.Root + "\\..\\escape", [Ns])), DocumentStoreError.UnsafePath);
        await StoreError(() => Task.FromResult(new LocalDocumentStore("relative", [Ns])), DocumentStoreError.UnsafePath);
    }
    private static async Task DpapiDirectorySharing()
    {
        using var fixture = new Temp();
        var store = fixture.Store();
        const uint addFileAndAttributes = 0x82, deleteAccess = 0x00010000;
        const uint shareAll = 7, openExisting = 3, noFollowDirectory = 0x02200000;
        string[] pinned = [fixture.Root, fixture.Base];
        try
        {
            foreach (string path in pinned)
            {
                // Atomic rename needs directory write access. Pinning must not reject
                // that open, but must still reject delete/rename on root and ancestors.
                using var writer = CreateFileW(path, addFileAndAttributes, shareAll, IntPtr.Zero,
                    openExisting, noFollowDirectory, IntPtr.Zero);
                Assert(!writer.IsInvalid, "Directory pin rejected FILE_ADD_FILE.");
                using var deletion = CreateFileW(path, deleteAccess, shareAll, IntPtr.Zero,
                    openExisting, noFollowDirectory, IntPtr.Zero);
                int error = Marshal.GetLastPInvokeError();
                Assert(deletion.IsInvalid && error == 32, "Directory pin did not deny delete with a sharing violation.");
            }
            var first = await store.WriteAsync(Ns, "first"u8.ToArray(), DocumentFingerprint.Missing);
            var second = await store.WriteAsync(Ns, "second"u8.ToArray(), first);
            var restored = await store.ReadAsync(Ns);
            Assert(restored.Fingerprint == second);
            Equal("second"u8.ToArray(), restored.Data!);
        }
        finally { await store.DisposeAsync(); }
        foreach (string path in pinned)
        {
            // This only opens DELETE access, never deletes. It proves the preceding
            // rejection came from live pins, not fixture ACLs or a permanent handle leak.
            using var released = CreateFileW(path, deleteAccess, shareAll, IntPtr.Zero,
                openExisting, noFollowDirectory, IntPtr.Zero);
            Assert(!released.IsInvalid, "Disposed store retained a directory pin.");
        }
    }
    private static async Task DpapiJunctionRoot()
    {
        using var fixture = new Temp();
        string target = Path.Combine(fixture.Base, "target"); Directory.CreateDirectory(Path.Combine(target, "child"));
        string link = Path.Combine(fixture.Base, "junction"); CreateJunction(link, target);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(link, [Ns])), DocumentStoreError.UnsafePath);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(Path.Combine(link, "child"), [Ns])), DocumentStoreError.UnsafePath);
        Assert(!Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any());
    }
    private static async Task DpapiJunctionNamespace()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        string target = Path.Combine(fixture.Base, "target"); Directory.CreateDirectory(target);
        CreateJunction(fixture.Namespace(Ns), target);
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.UnsafePath);
        await StoreError(() => store.WriteAsync(Ns, "x"u8.ToArray(), DocumentFingerprint.Missing), DocumentStoreError.UnsafePath);
        Assert(!Directory.EnumerateFileSystemEntries(target).Any());
    }
    private static async Task DpapiHardlink()
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        var old = await store.WriteAsync(Ns, "x"u8.ToArray(), DocumentFingerprint.Missing);
        Assert(CreateHardLinkW(Path.Combine(fixture.Base, "alias.mfdp"), fixture.Document(Ns), IntPtr.Zero), "Hardlink fixture creation failed.");
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.UnsafePath);
        await StoreError(() => store.WriteAsync(Ns, "y"u8.ToArray(), old), DocumentStoreError.UnsafePath);
    }
    private static async Task DpapiDocumentDirectory()
    {
        using var fixture = new Temp(); await using var store = fixture.Store(); Directory.CreateDirectory(fixture.Document(Ns));
        await StoreError(() => store.ReadAsync(Ns), DocumentStoreError.Storage);
        await StoreError(() => store.WriteAsync(Ns, "x"u8.ToArray(), DocumentFingerprint.Missing), DocumentStoreError.Storage);
    }
    private static async Task InvalidNamespace(string ns)
    {
        using var fixture = new Temp(); await using var store = fixture.Store();
        await StoreError(() => store.ReadAsync(ns), DocumentStoreError.InvalidRequest);
        await StoreError(() => store.WriteAsync(ns, new byte[] { 1 }, DocumentFingerprint.Missing), DocumentStoreError.InvalidRequest);
        await StoreError(() => Task.FromResult(new LocalDocumentStore(fixture.Root, [ns])), DocumentStoreError.InvalidRequest);
        Assert(!Directory.EnumerateFileSystemEntries(fixture.Root).Any());
    }
    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static async Task StoreError(Func<Task> operation, DocumentStoreError error)
    {
        try { await operation(); }
        catch (DocumentStoreException e) { Assert(e.Error == error && e.InnerException is null, "Unexpected store error."); return; }
        throw new InvalidOperationException("Expected store rejection.");
    }
    private static async Task Cancelled(Func<Task> operation)
    {
        try { await operation(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
    private static void Equal(byte[] expected, byte[] actual) => Assert(expected.AsSpan().SequenceEqual(actual), "Byte comparison failed.");
    private static void Assert(bool condition, string message = "Storage assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Temp : IDisposable
    {
        internal string Base { get; }
        internal string Root { get; }
        private readonly string parent;
        internal Temp(bool compact = false)
        {
            // Keep the original 148/149/150 boundaries constructible under the
            // explicit run data root, without weakening them or shortening the GUID.
            parent = compact ? TestWorkspace.DataRoot : TestWorkspace.DataPath("storage-tests");
            Base = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Base, compact ? "p" : "product"); Directory.CreateDirectory(Root);
        }
        internal LocalDocumentStore Store() => new(Root, [Ns, Other], protectedNamespaces: [Ns, Other]);
        internal string Namespace(string ns) => Path.Combine(Root, "n-" + Hash(Encoding.UTF8.GetBytes(ns)));
        internal string Document(string ns) => Path.Combine(Namespace(ns), "document.mfdp");
        internal string PlainNamespace(string ns) => Path.Combine(Root, "n-" + ns + "-" + Hash(Encoding.UTF8.GetBytes(ns)));
        internal string PlainDocument(string ns) => Path.Combine(PlainNamespace(ns), "document.json");
        internal string RootAtLength(int length)
        {
            string path = Root;
            Assert(length >= path.Length + 2, "Fixture base is too long for the requested boundary.");
            while (path.Length < length)
            {
                int remaining = length - path.Length, segmentLength = Math.Min(60, remaining - 1);
                if (remaining - segmentLength - 1 == 1) segmentLength--;
                Assert(segmentLength > 0, "Invalid long-path fixture segment.");
                path = Path.Combine(path, new string('p', segmentLength));
            }
            Directory.CreateDirectory(path); // Managed fixture setup; production receives a normal drive path.
            Assert(path.Length == length && !path.StartsWith(@"\\", StringComparison.Ordinal));
            return path;
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Base);
            Assert(resolved.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe test cleanup target.");
            Remove(resolved);
        }
        private static void Remove(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0) { File.Delete(path); return; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Remove(child);
            Directory.Delete(path); // Never follow a junction during cleanup.
        }
    }
    // Real NTFS junction fixture, no external commands/admin symlink privilege.
    // Both ends are generated temporary test data in this Windows worktree.
    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 16 + substitute.Length + 2);
        using var handle = CreateFileW(@"\\?\" + Path.GetFullPath(link), 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert(!handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero), "Junction fixture creation failed.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
}
