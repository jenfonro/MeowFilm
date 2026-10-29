using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowFilm.Platform.Files;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Windows.Tests;

internal sealed class SelectedSnapshotCases(bool script)
{
    private string Extension => script ? ".js" : ".json";
    private string Prefix => script ? "selected-script." : "selected-json.";
    private int MaximumBytes => script ? SelectedScriptSnapshot.MaximumBytes : SelectedJsonSnapshot.MaximumBytes;
    private static Type SharedType => typeof(SelectedJsonSnapshot).Assembly.GetType("MeowFilm.Platform.Files.SelectedFileSnapshot", throwOnError: true)!;
    private sealed record Data(byte[] Bytes, string Sha256, string Basename);
    private async Task<Data> ReadAsync(string path, CancellationToken token = default)
    {
        try
        {
            if (script)
            {
                var data = await SelectedScriptSnapshot.ReadAsync(path, token).ConfigureAwait(false);
                return new Data(data.Bytes, data.Sha256, data.Basename);
            }
            var json = await SelectedJsonSnapshot.ReadAsync(path, token).ConfigureAwait(false);
            return new Data(json.Bytes, json.Sha256, json.Basename);
        }
        catch (Exception error) when (error.GetType().FullName == "MeowFilm.Platform.Files.SelectedFileSnapshotException")
        {
            throw new InvalidOperationException("Shared error leaked through a public facade.");
        }
    }
    private static Data ToData(object value) => new(
        (byte[])value.GetType().GetProperty("Bytes")!.GetValue(value)!,
        (string)value.GetType().GetProperty("Sha256")!.GetValue(value)!,
        (string)value.GetType().GetProperty("Basename")!.GetValue(value)!);
    private SelectedJsonSnapshotError? Code(Exception error)
    {
        if (!script && error is SelectedJsonSnapshotException json) return json.Error;
        if (script && error is SelectedScriptSnapshotException js) return (SelectedJsonSnapshotError)js.Error;
        // White-box cases use only the shared lease/read algorithm, not a public error surface.
        if (error.GetType().FullName == "MeowFilm.Platform.Files.SelectedFileSnapshotException")
            return (SelectedJsonSnapshotError)(int)error.GetType().GetProperty("Error", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(error)!;
        return null;
    }
    internal IEnumerable<TestCase> Cases()
    {
        foreach (var test in FacadeCases()) yield return test;
        if (script) yield break;
        // Both facades delegate these path, handle and read semantics to SelectedFileSnapshot.
        yield return new(Prefix + "actual-ads-rejected-source-unchanged", AlternateStream);
        yield return new(Prefix + "readonly-source-not-modified", ReadOnlySource);
        yield return new(Prefix + "directory-rejected", DirectoryRejected);
        yield return new(Prefix + "hardlink-rejected-source-unchanged", Hardlink);
        yield return new(Prefix + "hardlink-rejection-not-cached-after-alias-removal", HardlinkRecovery);
        yield return new(Prefix + "fixture-cleanup-never-follows-junction", CleanupBoundary);
        yield return new(Prefix + "offline-file-rejected-without-mutation", OfflineFile);
        yield return new(Prefix + "reparse-file-rejected-source-unchanged", ReparseFile);
        yield return new(Prefix + "junction-ancestor-and-leaf-rejected", Junctions);
        yield return new(Prefix + "existing-deleter-rejected", ExistingDeleter);
        yield return new(Prefix + "metadata-change-with-restored-write-time-rejected", RestoredWriteTime);
        yield return new(Prefix + "changed-volume-identity-rejected", () => ChangedIdentity("VolumeSerialNumber"));
        yield return new(Prefix + "changed-file-id-low-rejected", () => ChangedIdentity("Low"));
        yield return new(Prefix + "changed-file-id-high-rejected", () => ChangedIdentity("High"));
        yield return new(Prefix + "changed-ancestor-identity-rejected", ChangedDirectoryIdentity);
        yield return new(Prefix + "read-lock-denies-growth", () => DenyMutation("grow"));
        yield return new(Prefix + "read-lock-denies-shrink", () => DenyMutation("shrink"));
        yield return new(Prefix + "read-lock-denies-same-size-write", () => DenyMutation("write"));
        yield return new(Prefix + "read-lock-denies-delete", () => DenyMutation("delete"));
        yield return new(Prefix + "read-lock-denies-rename", () => DenyMutation("rename"));
        yield return new(Prefix + "read-lock-denies-ancestor-replacement", () => DenyMutation("ancestor"));
        yield return new(Prefix + "read-lock-allows-other-readers", OtherReaders);
        yield return new(Prefix + "handles-noninheritable-and-released", HandleLifetime);
        yield return new(Prefix + "cancel-after-pin-releases-every-handle", CancelAfterPin);
        yield return new(Prefix + "concurrent-independent-owned-snapshots", ConcurrentReads);
        yield return new(Prefix + "defensive-growth-rejected-and-buffer-cleared", () => ChangingStream("grow"));
        yield return new(Prefix + "defensive-shrink-rejected-and-buffer-cleared", () => ChangingStream("shrink"));
        yield return new(Prefix + "mid-read-cancellation-and-buffer-cleared", () => ChangingStream("cancel"));
        yield return new(Prefix + "eof-cancellation-and-buffer-cleared", () => ChangingStream("cancel-eof"));
        yield return new(Prefix + "read-failure-clears-partial-buffer", () => ChangingStream("fail"));
        yield return new(Prefix + "short-reads-assembled-exactly", ShortReads);
        foreach (uint type in new uint[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        { uint captured = type; yield return new(Prefix + "drive-type-policy-" + type, () => DriveTypePolicy(captured)); }
        foreach (int length in new[] { 259, 260, 261, 1024, 4096 })
        { int captured = length; yield return new(Prefix + "long-path-" + length, () => LongPath(captured)); }
        string[] bad =
        [
            "null", "empty", "relative", "drive-relative", "unc", "extended", "device", "nt-device",
            "traversal", "dot", "duplicate-separator", "ads", "trailing-dot", "trailing-space",
            "control", "nul", "high-surrogate", "low-surrogate", "con", "con-space", "com", "lpt",
            "superscript-device", "wrong-extension", "component-too-long", "path-too-long", "too-many-components",
            "uri", "globalroot", "volume-guid", "root-only", "trailing-separator", "wildcard", "ads-type"
        ];
        foreach (string variant in bad)
        { string captured = variant; yield return new(Prefix + "invalid-" + variant, () => InvalidPath(captured)); }
    }

    private IEnumerable<TestCase> FacadeCases()
    {
        yield return new(Prefix + "contract.dto-and-fixed-errors", Contract);
        yield return new(Prefix + "exact-bytes-sha-basename-owned", Exact);
        yield return new(Prefix + "empty-and-invalid-content-remain-bytes", OpaqueBytes);
        yield return new(Prefix + (script ? "maximum-512-kib" : "maximum-eight-mib"), Maximum);
        yield return new(Prefix + "extension-policies-do-not-overlap", ExtensionPolicy);
        yield return new(Prefix + "no-ui-synchronization-context-capture", UiContext);
        yield return new(Prefix + "raw-script-is-not-executed", ScriptNotExecuted);
        yield return new(Prefix + "oversize-rejected-source-unchanged", Oversize);
        yield return new(Prefix + "missing-source-and-parent", Missing);
        yield return new(Prefix + "existing-writer-rejected", ExistingWriter);
        yield return new(Prefix + "metadata-change-rejected", MetadataChange);
        yield return new(Prefix + "byte-range-read-failure-sanitized-and-released", LockedReadFailure);
        yield return new(Prefix + "cancel-before-open", CancelBeforeOpen);
    }

    private Task Contract()
    {
        Equal(script ? 512 * 1024 : 8 * 1024 * 1024, MaximumBytes);
        string[] properties = (script ? typeof(SelectedScriptSnapshotData) : typeof(SelectedJsonSnapshotData)).GetProperties().Select(property => property.Name).Order().ToArray();
        Assert(properties.SequenceEqual(new[] { "Basename", "Bytes", "Sha256" }), "Snapshot exposes an unexpected property.");
        foreach (var code in Enum.GetValues<SelectedJsonSnapshotError>())
        {
            Exception error = script ? new SelectedScriptSnapshotException((SelectedScriptSnapshotError)code) :
                new SelectedJsonSnapshotException(code);
            Assert(Code(error) == code && error.Message == code.ToString() && error.InnerException is null);
        }
        string[] names = ["InvalidPath", "Unavailable", "UnsafeFile", "TooLarge", "InUse", "Changed", "ReadFailed"];
        var publicEnum = script ? typeof(SelectedScriptSnapshotError) : typeof(SelectedJsonSnapshotError);
        Assert(Enum.GetNames(publicEnum).SequenceEqual(names));
        Assert(Enum.GetValues(publicEnum).Cast<object>().Select(Convert.ToInt32).SequenceEqual(Enumerable.Range(0, names.Length)));
        Assert(!SharedType.IsPublic, "Shared arbitrary-extension reader must not be public.");
        return Task.CompletedTask;
    }

    private async Task ExtensionPolicy()
    {
        using var fixture = new Temp(Extension);
        foreach (string suffix in new[] { script ? ".json" : ".js", ".jsc", ".txt", ".js.map" })
        {
            string path = fixture.Write("wrong" + suffix, [1, 2, 3]);
            await Error(() => ReadAsync(path), SelectedJsonSnapshotError.InvalidPath);
            Bytes([1, 2, 3], File.ReadAllBytes(path));
            fixture.AssertReleased(path);
        }
    }

    private async Task AlternateStream()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, [1, 2, 3]);
        string alternate = path + ":metadata" + Extension;
        File.WriteAllBytes(alternate, [9, 8, 7]);
        await Error(() => ReadAsync(alternate), SelectedJsonSnapshotError.InvalidPath);
        Bytes([1, 2, 3], (await ReadAsync(path)).Bytes);
        Bytes([9, 8, 7], File.ReadAllBytes(alternate));
        fixture.AssertReleased(path);
    }

    private async Task ScriptNotExecuted()
    {
        using var fixture = new Temp(Extension);
        byte[] bytes = Encoding.UTF8.GetBytes("\uFEFFthrow new Error('synthetic-never-execute');\r\n");
        string path = fixture.Write("raw" + Extension, bytes);
        Bytes(bytes, (await ReadAsync(path)).Bytes);
        Equal(1, Directory.EnumerateFileSystemEntries(fixture.Root).Count());
        fixture.AssertReleased(path);
    }

    private async Task UiContext()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("context" + Extension, [4, 2]);
        var previous = SynchronizationContext.Current;
        var context = new RecordingContext();
        Task<Data> pending;
        try { SynchronizationContext.SetSynchronizationContext(context); pending = ReadAsync(path); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Bytes([4, 2], (await pending).Bytes);
        Equal(0, context.Posts);
        fixture.AssertReleased(path);
    }
    private sealed class RecordingContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private async Task DriveTypePolicy(uint type)
    {
        var method = SharedType.GetMethod("ValidateDriveType", BindingFlags.NonPublic | BindingFlags.Static)!;
        // Unit-level policy branch only. Never creates a network mapping or contacts an SMB server.
        if (type is 2 or 3) Invoke(method, null, type);
        else await Error(() => Task.FromResult(Invoke(method, null, type)), SelectedJsonSnapshotError.UnsafeFile);
    }

    private async Task Exact()
    {
        using var fixture = new Temp(Extension);
        byte[] data = Encoding.UTF8.GetBytes("\uFEFF{\"中文\":[1,\"e\u0301\",\"é\"]}\r\n");
        string path = fixture.Write("选择-资料-🎵" + Extension.ToUpperInvariant(), data);
        var before = File.GetLastWriteTimeUtc(path);
        var snapshot = await ReadAsync(path.Replace('\\', '/'));
        Bytes(data, snapshot.Bytes);
        Equal(Hash(data), snapshot.Sha256);
        Equal("选择-资料-🎵" + Extension.ToUpperInvariant(), snapshot.Basename);
        Assert(snapshot.Bytes.Length == data.Length && snapshot.Bytes != data);
        snapshot.Bytes[0] ^= 1;
        Bytes(data, File.ReadAllBytes(path));
        Equal(before, File.GetLastWriteTimeUtc(path));
        fixture.AssertReleased(path);
    }

    private async Task OpaqueBytes()
    {
        using var fixture = new Temp(Extension);
        foreach (byte[] data in new byte[][] { [], [0xff, 0x00, 0xfe], "not JSON"u8.ToArray() })
        {
            string path = fixture.Write("source" + Extension, data);
            var result = await ReadAsync(path);
            Bytes(data, result.Bytes);
            Equal(Hash(data), result.Sha256);
            fixture.AssertReleased(path);
        }
    }

    private async Task Maximum()
    {
        using var fixture = new Temp(Extension);
        byte[] data = new byte[MaximumBytes];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
        string path = fixture.Write("maximum" + Extension, data);
        var result = await ReadAsync(path);
        Equal(MaximumBytes, result.Bytes.Length);
        Bytes(data, result.Bytes);
        Equal(Hash(data), result.Sha256);
        fixture.AssertReleased(path);
    }

    private async Task Oversize()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("oversize" + Extension, [17, 42]);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
            file.SetLength(MaximumBytes + 1);
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        var writeTime = File.GetLastWriteTimeUtc(path);
        await Error(() => ReadAsync(path), SelectedJsonSnapshotError.TooLarge);
        Equal(MaximumBytes + 1L, new FileInfo(path).Length);
        Bytes(before, SHA256.HashData(File.ReadAllBytes(path)));
        Equal(writeTime, File.GetLastWriteTimeUtc(path));
        fixture.AssertReleased(path);
    }

    private async Task ReadOnlySource()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("readonly" + Extension, "{}"u8.ToArray());
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            Bytes("{}"u8.ToArray(), (await ReadAsync(path)).Bytes);
            Assert((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
        }
        finally { File.SetAttributes(path, attributes); }
        fixture.AssertReleased(path);
    }

    private async Task Missing()
    {
        using var fixture = new Temp(Extension);
        await Error(() => ReadAsync(Path.Combine(fixture.Root, "missing" + Extension)), SelectedJsonSnapshotError.Unavailable);
        await Error(() => ReadAsync(Path.Combine(fixture.Root, "missing", "source" + Extension)), SelectedJsonSnapshotError.Unavailable);
        Assert(!Directory.EnumerateFileSystemEntries(fixture.Root).Any(), "Read created a file or directory.");
        fixture.AssertDirectoryReleased();
    }

    private async Task DirectoryRejected()
    {
        using var fixture = new Temp(Extension);
        string path = Path.Combine(fixture.Root, "folder" + Extension);
        Directory.CreateDirectory(path);
        await Error(() => ReadAsync(path), SelectedJsonSnapshotError.UnsafeFile);
        Assert(Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any());
        fixture.AssertDirectoryReleased();
    }

    private async Task Hardlink()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        string alias = Path.Combine(fixture.Root, "alias" + Extension);
        fixture.Hardlink(alias, path);
        await Error(() => ReadAsync(path), SelectedJsonSnapshotError.UnsafeFile);
        await Error(() => ReadAsync(alias), SelectedJsonSnapshotError.UnsafeFile);
        Bytes("{}"u8.ToArray(), File.ReadAllBytes(path));
        Bytes("{}"u8.ToArray(), File.ReadAllBytes(alias));
        fixture.AssertReleased(path);
    }

    private async Task HardlinkRecovery()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, [1, 4, 9]);
        string alias = Path.Combine(fixture.Root, "alias" + Extension);
        fixture.Hardlink(alias, path);
        await Error(() => ReadAsync(path), SelectedJsonSnapshotError.UnsafeFile);
        fixture.RemoveOwned(alias);
        Bytes([1, 4, 9], (await ReadAsync(path)).Bytes);
        fixture.AssertReleased(path);
    }

    private Task CleanupBoundary()
    {
        using var fixture = new Temp(Extension);
        string target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "sentinel" + Extension);
        File.WriteAllBytes(sentinel, [3, 2, 1]);
        string link = Path.Combine(fixture.Root, "link");
        fixture.Junction(link, target);
        fixture.RemoveOwned(link);
        Assert(!Directory.Exists(link), "Fixture link was not removed.");
        Bytes([3, 2, 1], File.ReadAllBytes(sentinel));
        bool rejected = false;
        try { fixture.RemoveOwned(Path.GetDirectoryName(fixture.Root)!); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Fixture cleanup did not reject an escaping path.");
        return Task.CompletedTask;
    }

    private async Task OfflineFile()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("offline" + Extension, [2, 5]);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.Offline);
        try
        {
            Assert((File.GetAttributes(path) & FileAttributes.Offline) != 0, "Offline fixture was not marked.");
            await Error(() => ReadAsync(path), SelectedJsonSnapshotError.UnsafeFile);
            Assert((File.GetAttributes(path) & FileAttributes.Offline) != 0, "Reader modified offline state.");
        }
        finally { File.SetAttributes(path, attributes); }
        Bytes([2, 5], File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task ReparseFile()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("tagged" + Extension, "{}"u8.ToArray());
        // A third-party GUID reparse tag needs no symlink/developer-mode privilege.
        // No resolver is installed and no target is referenced. Only the generated file is tagged.
        byte[] buffer = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x00000042);
        new Guid("12b99af1-d6d3-473f-910e-f7ccf6ee6b2b").TryWriteBytes(buffer.AsSpan(8));
        using (var file = CreateFileW(Native(path), 0x40000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            Assert(!file.IsInvalid && DeviceIoControl(file, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "File reparse fixture creation failed.");
        Assert((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        await Error(() => ReadAsync(path), SelectedJsonSnapshotError.UnsafeFile);
        Assert((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "Reader cleared the reparse tag.");
        // Read the underlying generated file through a no-follow handle only, not via a resolver.
        using var held = CreateFileW(Native(path), 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        Assert(!held.IsInvalid);
        using var stream = new FileStream(held, FileAccess.Read);
        byte[] bytes = new byte[2]; stream.ReadExactly(bytes);
        Bytes("{}"u8.ToArray(), bytes);
    }

    private async Task Junctions()
    {
        using var fixture = new Temp(Extension);
        string target = Path.Combine(fixture.Root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "source" + Extension), "{}"u8.ToArray());
        string link = Path.Combine(fixture.Root, "link");
        fixture.Junction(link, target);
        string leaf = Path.Combine(fixture.Root, "leaf" + Extension);
        fixture.Junction(leaf, target);
        await Error(() => ReadAsync(Path.Combine(link, "source" + Extension)), SelectedJsonSnapshotError.UnsafeFile);
        await Error(() => ReadAsync(leaf), SelectedJsonSnapshotError.UnsafeFile);
        Bytes("{}"u8.ToArray(), File.ReadAllBytes(Path.Combine(target, "source" + Extension)));
        Assert(Directory.EnumerateFileSystemEntries(target).Count() == 1);
        fixture.AssertDirectoryReleased();
    }

    private async Task ExistingWriter()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            await Error(() => ReadAsync(path), SelectedJsonSnapshotError.InUse);
        Bytes("{}"u8.ToArray(), (await ReadAsync(path)).Bytes);
        fixture.AssertReleased(path);
    }

    private async Task ExistingDeleter()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        using (var held = CreateFileW(Native(path), 0x00010000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
        {
            Assert(!held.IsInvalid, "DELETE-access fixture open failed.");
            await Error(() => ReadAsync(path), SelectedJsonSnapshotError.InUse);
        }
        Bytes("{}"u8.ToArray(), (await ReadAsync(path)).Bytes);
        fixture.AssertReleased(path);
    }

    private async Task MetadataChange()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        var originalTime = File.GetLastWriteTimeUtc(path);
        using (var lease = OpenLease(path))
        {
            // Attribute-only opens can alter timestamps even though data writes are
            // denied. The handle metadata comparison must catch this independently.
            File.SetLastWriteTimeUtc(path, originalTime.AddMinutes(1));
            await Error(() => Task.FromResult(LeaseRead(lease)), SelectedJsonSnapshotError.Changed);
        }
        Bytes("{}"u8.ToArray(), File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task RestoredWriteTime()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, [7, 9]);
        var originalTime = File.GetLastWriteTimeUtc(path);
        using (var lease = OpenLease(path))
        {
            File.SetLastWriteTimeUtc(path, originalTime.AddMinutes(1));
            File.SetLastWriteTimeUtc(path, originalTime);
            // Set a definite change time rather than sleep for the filesystem clock to tick.
            // FILE_WRITE_ATTRIBUTES alone does not require data-write sharing.
            using (var handle = CreateFileW(Native(path), 0x100, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                Assert(!handle.IsInvalid);
                var change = new BasicInformation { ChangeTime = originalTime.AddMinutes(2).ToFileTimeUtc() };
                Assert(SetFileInformationByHandle(handle, 0, ref change, (uint)Marshal.SizeOf<BasicInformation>()),
                    "Synthetic change-time setup failed.");
            }
            Equal(originalTime, File.GetLastWriteTimeUtc(path));
            await Error(() => Task.FromResult(LeaseRead(lease)), SelectedJsonSnapshotError.Changed);
        }
        Bytes([7, 9], File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task ChangedIdentity(string component)
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, [6, 8]);
        using (var lease = OpenLease(path))
        {
            // Deterministic comparison seam: alter the saved identity, never OS file IDs.
            // Native identity queries/read and cleanup still execute against the real file.
            var field = lease.GetType().GetField("Initial", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object initial = field.GetValue(lease)!;
            object identity = initial.GetType().GetProperty("Identity")!.GetValue(initial)!;
            ChangeIdentityComponent(identity, component);
            var constructor = initial.GetType().GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(info => info.GetParameters().Length == 3);
            field.SetValue(lease, constructor.Invoke([
                initial.GetType().GetProperty("Info")!.GetValue(initial),
                identity, initial.GetType().GetProperty("Basic")!.GetValue(initial)
            ]));
            await Error(() => Task.FromResult(LeaseRead(lease)), SelectedJsonSnapshotError.Changed);
        }
        Bytes([6, 8], File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task ChangedDirectoryIdentity()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, [6, 8]);
        using (var lease = OpenLease(path))
        {
            var identities = (System.Collections.IList)lease.GetType()
                .GetField("DirectoryIdentities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
            object identity = identities[identities.Count - 1]!;
            ChangeIdentityComponent(identity, "High");
            identities[identities.Count - 1] = identity;
            await Error(() => Task.FromResult(LeaseRead(lease)), SelectedJsonSnapshotError.Changed);
        }
        fixture.AssertReleased(path);
    }

    private static void ChangeIdentityComponent(object identity, string component)
    {
        var field = identity.GetType().GetField(component, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(identity, (ulong)field.GetValue(identity)! ^ 1UL);
    }

    private async Task LockedReadFailure()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{\"generated\":true}"u8.ToArray());
        byte[] original = File.ReadAllBytes(path);
        using (var handle = CreateFileW(Native(path), 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
        {
            Assert(!handle.IsInvalid, "Lock fixture open failed.");
            var overlapped = new Overlapped();
            Assert(LockFileEx(handle, 3, 0, 1, 0, ref overlapped), "Byte-range fixture lock failed.");
            try
            {
                // The file open succeeds; the actual ReadFile is denied. Native path
                // and IOException details must not escape the public failure surface.
                await Error(() => ReadAsync(path), SelectedJsonSnapshotError.ReadFailed);
            }
            finally { Assert(UnlockFileEx(handle, 0, 1, 0, ref overlapped), "Byte-range fixture unlock failed."); }
        }
        Bytes(original, File.ReadAllBytes(path));
        fixture.AssertReleased(path);
        Bytes(original, (await ReadAsync(path)).Bytes);
    }

    private Task DenyMutation(string operation)
    {
        using var fixture = new Temp(Extension);
        string directory = Path.Combine(fixture.Root, "selected");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source" + Extension);
        byte[] original = Encoding.UTF8.GetBytes("{\"generated\":true}");
        File.WriteAllBytes(path, original);
        // White-box lifetime boundary: keep the exact production open/pin lease alive
        // before invoking its reader. No timing race, test-only hook or mock native I/O.
        using (var lease = OpenLease(path))
        {
            Blocked(() =>
            {
                if (operation == "delete") File.Delete(path);
                else if (operation == "rename") File.Move(path, path + ".moved");
                else if (operation == "ancestor") Directory.Move(directory, directory + "-moved");
                else
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    if (operation == "grow") writer.SetLength(original.Length + 1);
                    else if (operation == "shrink") writer.SetLength(original.Length - 1);
                    else writer.Write(new byte[original.Length]);
                }
            });
            Bytes(original, LeaseRead(lease).Bytes);
        }
        Bytes(original, File.ReadAllBytes(path));
        fixture.AssertReleased(path);
        return Task.CompletedTask;
    }

    private async Task OtherReaders()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        using (var lease = OpenLease(path))
        {
            Bytes("{}"u8.ToArray(), await File.ReadAllBytesAsync(path));
            Bytes("{}"u8.ToArray(), LeaseRead(lease).Bytes);
        }
        fixture.AssertReleased(path);
    }

    private Task HandleLifetime()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        SafeFileHandle[] owned;
        using (var lease = OpenLease(path))
        {
            var file = (SafeFileHandle)lease.GetType().GetField("File", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
            var directories = (IEnumerable<SafeFileHandle>)lease.GetType().GetField("Directories", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lease)!;
            owned = directories.Append(file).ToArray();
            foreach (var handle in owned)
                Assert(GetHandleInformation(handle, out uint flags) && (flags & 1) == 0, "Snapshot handle is inheritable.");
        }
        Assert(owned.All(handle => handle.IsClosed), "Not every owned handle was closed.");
        fixture.AssertReleased(path);
        return Task.CompletedTask;
    }

    private async Task CancelBeforeOpen()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Cancelled(() => ReadAsync(path, cancellation.Token), cancellation.Token);
        fixture.AssertReleased(path);
    }

    private async Task CancelAfterPin()
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        using (var lease = OpenLease(path))
        {
            cancellation.Cancel();
            await Cancelled(() => Task.FromResult(LeaseRead(lease, cancellation.Token)), cancellation.Token);
        }
        Bytes("{}"u8.ToArray(), File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task ConcurrentReads()
    {
        using var fixture = new Temp(Extension);
        byte[] bytes = new byte[128 * 1024]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);
        string path = fixture.Write("source" + Extension, bytes);
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ReadAsync(path)));
        foreach (var snapshot in snapshots) { Bytes(bytes, snapshot.Bytes); Equal(Hash(bytes), snapshot.Sha256); }
        snapshots[0].Bytes[0] ^= 255;
        for (int i = 1; i < snapshots.Length; i++) Bytes(bytes, snapshots[i].Bytes);
        Bytes(bytes, File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task ChangingStream(string change)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ControlledStream(128 * 1024, change, cancellation);
        // Defensive algorithm cases for providers that violate the frozen-length
        // assumption. Real local sharing denial is independently tested above.
        if (change is "cancel" or "cancel-eof")
            await Cancelled(() => Task.FromResult(ReadFixed(stream, 128 * 1024, cancellation.Token)), cancellation.Token);
        else if (change == "fail")
        {
            bool failed = false;
            try { _ = ReadFixed(stream, 128 * 1024); } catch (IOException) { failed = true; }
            Assert(failed, "Injected stream failure was swallowed.");
        }
        else await Error(() => Task.FromResult(ReadFixed(stream, 128 * 1024)), SelectedJsonSnapshotError.Changed);
        Assert(stream.CapturedBuffer is { Length: 128 * 1024 } && stream.CapturedBuffer.All(value => value == 0),
            "Failed snapshot retained partial bytes.");
        Assert(stream.MaximumReadCount <= 65536, "Read exceeded the bounded chunk.");
    }

    private Task ShortReads()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ControlledStream(131073, "short", cancellation);
        byte[] result = ReadFixed(stream, 131073);
        Equal(131073, result.Length);
        Assert(result.All(value => value == 90) && stream.MaximumReadCount <= 65536);
        return Task.CompletedTask;
    }

    private async Task LongPath(int length)
    {
        using var fixture = new Temp(Extension);
        string path = fixture.LongFile(length);
        byte[] original = Encoding.UTF8.GetBytes("{\"long\":\"测试\"}");
        File.WriteAllBytes(path, original);
        var result = await ReadAsync(path);
        Equal(length, path.Length);
        Equal("snapshot" + Extension, result.Basename);
        Equal(Hash(original), result.Sha256);
        Bytes(original, result.Bytes);
        Bytes(original, File.ReadAllBytes(path));
        fixture.AssertReleased(path);
    }

    private async Task InvalidPath(string variant)
    {
        using var fixture = new Temp(Extension);
        string path = fixture.Write("source" + Extension, "{}"u8.ToArray());
        string supplied = variant switch
        {
            "null" => null!, "empty" => "", "relative" => "source" + Extension, "drive-relative" => "C:source" + Extension,
            "unc" => @"\\fixture.invalid\share\source" + Extension, "extended" => @"\\?\" + path,
            "device" => @"\\.\" + path, "nt-device" => @"\??\" + path,
            "traversal" => Path.Combine(fixture.Root, "..", "source" + Extension),
            "dot" => fixture.Root + @"\.\source" + Extension, "duplicate-separator" => fixture.Root + @"\\source" + Extension,
            "ads" => path + ":stream" + Extension, "trailing-dot" => fixture.Root + @"\folder.\source" + Extension,
            "trailing-space" => path + " ", "control" => fixture.Root + "\\a\u0001" + Extension,
            "nul" => path + "\0", "high-surrogate" => fixture.Root + "\\\ud800" + Extension,
            "low-surrogate" => fixture.Root + "\\\udc00" + Extension, "con" => fixture.Root + @"\CON" + Extension,
            "con-space" => fixture.Root + @"\CON " + Extension, "com" => fixture.Root + @"\COM1" + Extension,
            "lpt" => fixture.Root + @"\LPT0" + Extension, "superscript-device" => fixture.Root + "\\COM\u00b9" + Extension,
            "wrong-extension" => path + ".txt", "component-too-long" => Path.Combine(fixture.Root, new string('a', 256) + Extension),
            "path-too-long" => path + new string('a', 4097) + Extension,
            "too-many-components" => fixture.Root + string.Concat(Enumerable.Repeat("\\a", 129)) + "\\source" + Extension,
            "uri" => new Uri(path).AbsoluteUri,
            "globalroot" => @"\\?\GLOBALROOT\Device\HarddiskVolume1\source" + Extension,
            "volume-guid" => @"\\?\Volume{00000000-0000-0000-0000-000000000000}\source" + Extension,
            "root-only" => Path.GetPathRoot(path)!,
            "trailing-separator" => path + "\\",
            "wildcard" => fixture.Root + "\\*" + Extension,
            "ads-type" => path + "::$DATA",
            _ => throw new InvalidOperationException("Unknown test variant.")
        };
        await Error(() => ReadAsync(supplied), SelectedJsonSnapshotError.InvalidPath);
        Bytes("{}"u8.ToArray(), File.ReadAllBytes(path));
        Equal(1, Directory.EnumerateFileSystemEntries(fixture.Root).Count());
        fixture.AssertReleased(path);
    }

    private sealed class ControlledStream : MemoryStream
    {
        private readonly string change;
        private readonly CancellationTokenSource cancellation;
        private int reads;
        internal byte[]? CapturedBuffer;
        internal int MaximumReadCount;
        internal ControlledStream(int length, string change, CancellationTokenSource cancellation)
        {
            this.change = change; this.cancellation = cancellation;
            Write(Enumerable.Repeat((byte)90, length).ToArray());
            Position = 0;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            CapturedBuffer = buffer; MaximumReadCount = Math.Max(MaximumReadCount, count);
            if (reads++ > 0 && change == "fail") throw new IOException("Synthetic read failure.");
            int read = base.Read(buffer, offset, change == "short" ? Math.Min(97, count) : count);
            if (reads == 1)
            {
                if (change == "grow") SetLength(Length + 1);
                if (change == "shrink") SetLength(Position);
                if (change == "cancel") cancellation.Cancel();
            }
            return read;
        }
        public override int ReadByte()
        {
            int result = base.ReadByte();
            if (change == "cancel-eof") cancellation.Cancel();
            return result;
        }
    }

    private IDisposable OpenLease(string path) => (IDisposable)Invoke(
        SharedType.GetMethod("OpenSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!,
        null, path, MaximumBytes, CancellationToken.None, Array.Empty<string>())!;
    private Data LeaseRead(IDisposable lease, CancellationToken token = default) =>
        ToData(Invoke(lease.GetType().GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!,
            lease, token)!);
    private byte[] ReadFixed(Stream stream, int length, CancellationToken token = default) => (byte[])Invoke(
        SharedType.GetMethod("ReadFixed", BindingFlags.Static | BindingFlags.NonPublic)!,
        null, stream, length, MaximumBytes, token)!;
    private static object? Invoke(MethodInfo method, object? target, params object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private async Task Error(Func<Task> operation, SelectedJsonSnapshotError code)
    {
        try { await operation(); }
        catch (Exception error) when (Code(error) is not null)
        {
            Assert(Code(error) == code && error.Message == code.ToString() && error.InnerException is null,
                "Unexpected fixed snapshot error.");
            return;
        }
        throw new InvalidOperationException("Expected snapshot rejection.");
    }
    private static async Task Cancelled(Func<Task> operation, CancellationToken token)
    {
        try { await operation(); }
        catch (OperationCanceledException error) { Assert(error.CancellationToken == token); return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
    private static void Blocked(Action operation)
    {
        try { operation(); }
        catch (IOException error) { Equal(32, error.HResult & 0xffff); return; }
        throw new InvalidOperationException("Source mutation was not denied.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Bytes(byte[] expected, byte[] actual) => Assert(expected.AsSpan().SequenceEqual(actual), "Snapshot bytes changed.");
    private static void Equal<T>(T expected, T actual) where T : IEquatable<T> => Assert(expected.Equals(actual), "Snapshot assertion mismatch.");
    private static void Assert(bool condition, string message = "Snapshot assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Native(string path) => @"\\?\" + Path.GetFullPath(path);

    private sealed class Temp : IDisposable
    {
        internal string Root { get; }
        private readonly string parent, extension;
        internal Temp(string extension)
        {
            this.extension = extension;
            string allowed = TestWorkspace.DataRoot;
            string? selected = Environment.GetEnvironmentVariable("MEOWFILM_SELECTED_FILES_TEST_ROOT");
            parent = selected is null ? allowed : Path.GetFullPath(selected);
            Assert(parent == allowed || parent.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Fixture root must stay inside Windows working test results.");
            NoReparseAncestors(parent);
            Root = Path.Combine(parent, "selected-files-fixture-" + Guid.NewGuid().ToString("N"));
            Assert(!Path.Exists(Root), "Fixture directory must be new.");
            Directory.CreateDirectory(Root);
            NoReparseAncestors(Root);
        }
        internal string Write(string basename, byte[] bytes)
        {
            Assert(basename == Path.GetFileName(basename), "Fixture write must be a basename.");
            string path = Path.Combine(Root, basename);
            Contained(path); NoReparseAncestors(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes); return path;
        }
        internal void Hardlink(string link, string target)
        {
            Contained(link); Contained(target);
            NoReparseAncestors(Path.GetDirectoryName(link)!); NoReparseAncestors(target);
            Assert(CreateHardLinkW(Native(link), Native(target), IntPtr.Zero), "Hardlink fixture creation failed.");
        }
        internal void Junction(string link, string target)
        {
            Contained(link); Contained(target);
            NoReparseAncestors(Path.GetDirectoryName(link)!); NoReparseAncestors(target);
            Assert(!Path.Exists(link), "Junction path must be new.");
            CreateJunction(link, target);
        }
        internal void RemoveOwned(string path)
        {
            Contained(path);
            NoReparseAncestors(Path.GetDirectoryName(Path.GetFullPath(path))!);
            Remove(Path.GetFullPath(path));
        }
        internal string LongFile(int length)
        {
            string directory = Root;
            int target = length - ("\\snapshot" + extension).Length;
            Assert(target > directory.Length + 1);
            while (directory.Length < target)
            {
                int remaining = target - directory.Length, segment = Math.Min(60, remaining - 1);
                if (remaining - segment - 1 == 1) segment--;
                Assert(segment > 0);
                directory = Path.Combine(directory, new string('x', segment));
            }
            Contained(directory); NoReparseAncestors(directory);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "snapshot" + extension);
        }
        internal void AssertReleased(string path)
        {
            using var handle = CreateFileW(Native(path), 0xC0010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            Assert(!handle.IsInvalid, "Reader retained a file handle.");
            AssertDirectoryReleased();
        }
        internal void AssertDirectoryReleased()
        {
            using var directory = CreateFileW(Native(Root), 0x00010000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert(!directory.IsInvalid, "Reader retained ancestor pins.");
        }
        public void Dispose()
        {
            string path = Path.GetFullPath(Root);
            Assert(path.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Unsafe fixture cleanup target.");
            NoReparseAncestors(parent);
            Remove(path);
        }
        private void Contained(string path)
        {
            string absolute = Path.GetFullPath(path);
            Assert(absolute == Root || absolute.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Fixture operation escaped its owned root.");
        }
        private static void NoReparseAncestors(string path)
        {
            for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if (Path.Exists(current))
                    Assert((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Unsafe fixture ancestor.");
        }
        private void Remove(string path)
        {
            Contained(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0) { File.Delete(path); return; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Remove(child);
            Directory.Delete(path); // Never traverse reparse points.
        }
    }

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
        using var handle = CreateFileW(Native(link), 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert(!handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "Junction fixture creation failed.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafeFileHandle handle, out uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal uint FileAttributes;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref BasicInformation information, uint length);
    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        internal IntPtr Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal IntPtr Event;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved,
        uint countLow, uint countHigh, ref Overlapped overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnlockFileEx(SafeFileHandle handle, uint reserved,
        uint countLow, uint countHigh, ref Overlapped overlapped);
}
