using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MeowFilm.Platform.Storage;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Windows.Tests;

public static class UserDataDirectoryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("open-is-passive-idempotent-and-never-clears-without-confirmation", Passive);
        yield return Case("shared-users-reset-refusal-exclusive-admission-and-two-generations", SharedUsers);
        yield return Case("missing-root-and-missing-parent-are-successful", Missing);
        yield return new("user-data-directory.unknown-corrupt-secret-and-cache-data-removed", AllData);
        yield return Case("delete-failure-keeps-intent-and-exclusive-admission-until-retry", DeleteFailure);
        yield return Case("interrupted-reset-is-completed-before-open", Recovery);
        yield return new("user-data-directory.legacy-store-pin-refuses-before-any-child-deletion", LegacyStore);
        yield return Case("root-and-nested-junctions-never-touch-targets", Junctions);
        yield return Case("hardlink-removal-preserves-external-bytes-and-readonly-attribute", Hardlink);
        yield return Case("parent-junction-and-linked-admission-metadata-are-rejected", UnsafeAdmission);
        yield return Case("pending-write-failure-does-not-delete-and-retains-exclusive-admission", MarkerFailure);
        yield return Case("completed-reset-lease-retry-preserves-another-generations-new-data", DowngradeFailure);
        yield return new("user-data-directory.failed-shared-restoration-rechecks-pending-and-legacy-pin", UpgradeRecovery);
    }

    private static TestCase Case(string name, Action action) =>
        new("user-data-directory." + name, () => { action(); return Task.CompletedTask; });

    private static void Passive()
    {
        using var f = new Fixture();
        string file = f.Write("owned/unknown.json", "not JSON"u8.ToArray());
        using var owner = new UserDataDirectory(f.Root);
        Check(!File.Exists(f.Usage) && !File.Exists(f.Pending), "Construction wrote admission metadata.");
        owner.Open();
        owner.Open();
        Failure(owner.Clear, DocumentStoreError.InvalidRequest);
        Bytes("not JSON"u8.ToArray(), File.ReadAllBytes(file));
        Check(!File.Exists(f.Pending), "Opening created reset intent.");
        owner.Dispose();
        Failure(owner.Open, DocumentStoreError.Closed);
        Failure(owner.RequestReset, DocumentStoreError.Closed);
        Failure(owner.Clear, DocumentStoreError.Closed);
    }

    private static void SharedUsers()
    {
        using var f = new Fixture();
        string file = f.Write("owned/keep", [1, 2, 3]);
        using var first = new UserDataDirectory(f.Root);
        using var second = new UserDataDirectory(f.Root);
        first.Open(); second.Open();
        Failure(first.RequestReset);
        Failure(second.RequestReset);
        Check(!File.Exists(f.Pending));
        Bytes([1, 2, 3], File.ReadAllBytes(file));
        using (var third = new UserDataDirectory(f.Root)) third.Open();
        second.Dispose();
        first.RequestReset();
        Check(File.Exists(f.Pending) && File.Exists(file), "RequestReset deleted data.");
        using var blocked = new UserDataDirectory(f.Root);
        Failure(blocked.Open);
        first.Clear();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
        first.Open(); blocked.Open();
        string next = f.Write("owned/new-generation", [4, 5]);
        first.Open();
        Bytes([4, 5], File.ReadAllBytes(next));
        Failure(first.RequestReset); // Clear restored shared rather than exclusive admission.
        blocked.Dispose();
        first.RequestReset(); first.Clear();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
    }

    private static void Missing()
    {
        using var f = new Fixture();
        string parent = Path.Combine(f.Base, "not-created");
        string root = Path.Combine(parent, "owned");
        using var owner = new UserDataDirectory(root);
        Check(!Directory.Exists(parent));
        owner.Open();
        Check(Directory.Exists(parent) && !Directory.Exists(root));
        owner.RequestReset(); owner.Clear(); owner.Open();
        Check(!Directory.Exists(root) && !File.Exists(Path.Combine(parent, ".owned.reset-pending")));
    }

    private static async Task AllData()
    {
        using var f = new Fixture();
        f.Write("owned/unknown/deeper/stage-interrupted.tmp", [0, 255, 1]);
        f.Write("owned/native/document.json", "{"u8.ToArray());
        f.Write("owned/music-playback-cache/unindexed.audio", [1, 3, 5]);
        string script = f.Write("owned/script-runtime/sources/source.js", "synthetic"u8.ToArray());
        File.SetAttributes(script, FileAttributes.ReadOnly);
        f.Write("owned/script-runtime/pan/accounts.json", "synthetic credential"u8.ToArray());
        f.Write("owned/script-runtime/volumes/installation/tmp/blob", [2, 4, 6]);
        f.Write("owned/unknown-profile-state", [9]);
        string original = f.Write("outside/import-original.js", [7, 8]);
        string sibling = f.Write("other-identity/keep.json", [6, 7]);
        await using (var store = new LocalDocumentStore(f.Root, ["test.secret"], protectedNamespaces: ["test.secret"]))
            await store.WriteAsync("test.secret", "synthetic secret"u8.ToArray(), DocumentFingerprint.Missing);
        foreach (string secret in Directory.EnumerateFiles(f.Root, "*.mfdp", SearchOption.AllDirectories))
            File.WriteAllBytes(secret, "broken DPAPI envelope"u8.ToArray());
        using var owner = new UserDataDirectory(f.Root);
        owner.Open(); owner.RequestReset(); owner.Clear();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
        Bytes([7, 8], File.ReadAllBytes(original));
        Bytes([6, 7], File.ReadAllBytes(sibling));
    }

    private static void DeleteFailure()
    {
        using var f = new Fixture();
        string file = f.Write("owned/locked", [3, 2, 1]);
        f.Write("owned/nested/unknown", [8]);
        using var owner = new UserDataDirectory(f.Root);
        owner.Open(); owner.RequestReset();
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Failure(owner.Clear);
            Check(File.Exists(f.Pending));
            using var other = new UserDataDirectory(f.Root);
            Failure(other.Open);
            Bytes([3, 2, 1], File.ReadAllBytes(file));
        }
        owner.Clear();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
        using var reopened = new UserDataDirectory(f.Root);
        reopened.Open();
    }

    private static void Recovery()
    {
        using var f = new Fixture();
        string file = f.Write("owned/interrupted", [5, 4, 3]);
        using (var interrupted = new UserDataDirectory(f.Root))
        {
            interrupted.Open(); interrupted.RequestReset();
        }
        Check(File.Exists(f.Pending) && File.Exists(file));
        using var next = new UserDataDirectory(f.Root);
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Failure(next.Open);
            Check(File.Exists(f.Pending));
            using var blocked = new UserDataDirectory(f.Root);
            Failure(blocked.Open);
        }
        next.Open();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
        string current = f.Write("owned/current", [6]);
        next.Open();
        Bytes([6], File.ReadAllBytes(current));
    }

    private static async Task LegacyStore()
    {
        using var f = new Fixture();
        string first = f.Write("owned/first", [1]);
        string last = f.Write("owned/nested/last", [2]);
        await using var store = new LocalDocumentStore(f.Root, ["test.document"]);
        using var owner = new UserDataDirectory(f.Root);
        owner.Open(); owner.RequestReset();
        Failure(owner.Clear);
        Bytes([1], File.ReadAllBytes(first));
        Bytes([2], File.ReadAllBytes(last));
        Check(Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories).Count() == 2,
            "Legacy root occupancy must be checked before touching any child.");
        Check(File.Exists(f.Pending));
        await store.DisposeAsync();
        owner.Clear();
        Check(!Directory.Exists(f.Root));
    }

    private static void Junctions()
    {
        using var f = new Fixture();
        string outside = f.Write("outside/original", [7, 7]);
        f.Write("owned/local", [1]);
        Junction(Path.Combine(f.Root, "nested-link"), Path.GetDirectoryName(outside)!);
        Junction(Path.Combine(f.Root, "dangling-link"), Path.Combine(f.Base, "missing-target"));
        using var owner = new UserDataDirectory(f.Root);
        owner.Open(); owner.RequestReset(); owner.Clear();
        Check(!Directory.Exists(f.Root));
        Bytes([7, 7], File.ReadAllBytes(outside));
        Junction(f.Root, Path.GetDirectoryName(outside)!);
        owner.RequestReset(); owner.Clear();
        Check(!Directory.Exists(f.Root));
        Bytes([7, 7], File.ReadAllBytes(outside));
    }

    private static void Hardlink()
    {
        using var f = new Fixture();
        string outside = f.Write("outside/original", [2, 7]);
        Directory.CreateDirectory(f.Root);
        Check(CreateHardLinkW(Path.Combine(f.Root, "alias"), outside, IntPtr.Zero));
        File.SetAttributes(outside, FileAttributes.ReadOnly);
        using var owner = new UserDataDirectory(f.Root);
        owner.Open(); owner.RequestReset(); owner.Clear();
        Check(!Directory.Exists(f.Root));
        Bytes([2, 7], File.ReadAllBytes(outside));
        Check((File.GetAttributes(outside) & FileAttributes.ReadOnly) != 0,
            "Removing an owned hardlink must not rewrite its external target's attributes.");
    }

    private static void UnsafeAdmission()
    {
        using var f = new Fixture();
        string outside = f.Write("outside/original", [8, 8]);
        string link = Path.Combine(f.Base, "parent-link");
        Junction(link, Path.GetDirectoryName(outside)!);
        using (var owner = new UserDataDirectory(Path.Combine(link, "owned")))
        {
            Failure(owner.Open, DocumentStoreError.UnsafePath);
            Failure(owner.RequestReset, DocumentStoreError.UnsafePath);
        }
        Check(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(outside)!).Count() == 1);
        foreach (string metadata in new[] { f.Usage, f.Pending, f.Admission })
        {
            File.Delete(metadata);
            Check(CreateHardLinkW(metadata, outside, IntPtr.Zero));
            using (var owner = new UserDataDirectory(f.Root))
                Failure(owner.Open, DocumentStoreError.UnsafePath);
            File.Delete(metadata);
            Bytes([8, 8], File.ReadAllBytes(outside));
        }
    }

    private static void MarkerFailure()
    {
        using var f = new Fixture();
        string file = f.Write("owned/keep", [9]);
        using var owner = new UserDataDirectory(f.Root);
        owner.Open();
        Directory.CreateDirectory(f.Pending);
        Failure(owner.RequestReset);
        Bytes([9], File.ReadAllBytes(file));
        using var blocked = new UserDataDirectory(f.Root);
        Failure(blocked.Open);
        Directory.Delete(f.Pending);
        owner.RequestReset();
        Check(File.Exists(f.Pending));
        owner.Clear();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
    }

    private static void DowngradeFailure()
    {
        using var f = new Fixture();
        f.Write("owned/old", [1]);
        using var owner = new UserDataDirectory(f.Root);
        owner.Open(); owner.RequestReset();
        using (var denied = new DenyRead(f.Usage))
            Failure(owner.Clear);
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending),
            "This failure must occur after physical cleanup and marker removal.");
        using var other = new UserDataDirectory(f.Root);
        other.Open();
        string current = f.Write("owned/other-generation", [8, 9]);
        owner.RequestReset(); // Retrying the original reset, not a new confirmation.
        Check(!File.Exists(f.Pending));
        owner.Clear(); owner.Open();
        Bytes([8, 9], File.ReadAllBytes(current));
        Failure(owner.RequestReset); // Both owners now hold normal shared leases.
    }

    private static async Task UpgradeRecovery()
    {
        using var f = new Fixture();
        string file = f.Write("owned/live", [4, 8]);
        using var first = new UserDataDirectory(f.Root);
        using var second = new UserDataDirectory(f.Root);
        first.Open(); second.Open();
        await using var legacy = new LocalDocumentStore(f.Root, ["test.document"]);
        using (var denied = new DenyRead(f.Usage))
            Failure(first.RequestReset); // Both exclusive acquisition and shared restoration fail.
        Check(!File.Exists(f.Pending));
        second.RequestReset();
        Failure(first.Open); // Must not treat the lost shared lease as still admitted.
        Failure(second.Clear);
        Bytes([4, 8], File.ReadAllBytes(file));
        second.Dispose();
        Failure(first.Open); // Pending recovery also respects the still-live legacy store.
        Bytes([4, 8], File.ReadAllBytes(file));
        Check(File.Exists(f.Pending));
        await legacy.DisposeAsync();
        first.Open();
        Check(!Directory.Exists(f.Root) && !File.Exists(f.Pending));
    }

    private static void Failure(Action action, DocumentStoreError expected = DocumentStoreError.Storage)
    {
        try { action(); }
        catch (DocumentStoreException error) { Check(error.Error == expected, "Unexpected storage failure code."); return; }
        throw new InvalidOperationException("Expected a storage failure.");
    }

    private static void Bytes(byte[] expected, byte[] actual) =>
        Check(expected.AsSpan().SequenceEqual(actual), "Fixture bytes changed.");
    private static void Check(bool condition, string message = "User data directory assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }

    // Change only the synthetic usage file's ACL. Existing handles stay valid,
    // so Clear can commit while subsequent shared/exclusive reopens really fail.
    private sealed class DenyRead : IDisposable
    {
        private readonly FileInfo file;
        private readonly byte[] original;
        internal DenyRead(string path)
        {
            file = new FileInfo(path);
            var denied = file.GetAccessControl(AccessControlSections.Access);
            original = denied.GetSecurityDescriptorBinaryForm();
            using var identity = WindowsIdentity.GetCurrent();
            denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny));
            file.SetAccessControl(denied);
        }
        public void Dispose()
        {
            var restored = new FileSecurity();
            // SetAccessControl persists only modified sections; replaying an
            // untouched GetAccessControl result would leave the deny rule active.
            restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            file.SetAccessControl(restored);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Base { get; } = TestWorkspace.DataPath("user-data-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Base, "owned");
        internal string Usage => Path.Combine(Base, ".owned.usage.lock");
        internal string Admission => Path.Combine(Base, ".owned.usage-gate.lock");
        internal string Pending => Path.Combine(Base, ".owned.reset-pending");
        internal Fixture() => Directory.CreateDirectory(Base);
        internal string Write(string relative, byte[] bytes)
        {
            string path = TestWorkspace.ValidatePath(Base, Path.Combine(Base, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public void Dispose() => Remove(TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Base));
        private static void Remove(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    foreach (string child in Directory.EnumerateFileSystemEntries(path)) Remove(child);
                Directory.Delete(path);
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
    }

    // Same real NTFS junction fixture as StorageTests; no symlink/admin privilege
    // or external command. Every path and target is synthetic worktree test data.
    private static void Junction(string link, string target)
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
        Check(!handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "Junction fixture creation failed.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
}
