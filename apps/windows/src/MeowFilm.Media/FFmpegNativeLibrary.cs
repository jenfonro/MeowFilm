using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MeowFilm.Media;

/// <summary>One verified process-lifetime plugin mapping, not a second player or session owner.</summary>
internal static class FFmpegNativeLibrary
{
    private static readonly object Gate = new();
    private static IntPtr _library;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        internal uint Size, Abi;
        internal IntPtr Opaque, Open, Location, Read, Seek, Close, Interrupted, Failed;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OpenCallback(IntPtr owner, IntPtr uri, long offset, long end,
        out ulong handle, out long size, out int seekable, out uint locationSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int LocationCallback(IntPtr owner, ulong handle, IntPtr location, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReadCallback(IntPtr owner, ulong handle, IntPtr data, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate long SeekCallback(IntPtr owner, ulong handle, long offset, int whence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CloseCallback(IntPtr owner, ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InterruptedCallback(IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FailedCallback(IntPtr owner, int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateCallback(ref Callbacks callbacks, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri,
        int format, IntPtr keys, uint count, IntPtr defaultKey,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string token, out IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OwnerCallback(IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr IdentityCallback();
    private static CreateCallback _create = null!;
    private static OwnerCallback _cancel = null!, _release = null!;

    internal static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_library != IntPtr.Zero) return;
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            using var process = Process.GetCurrentProcess();
            var core = process.Modules.Cast<ProcessModule>().SingleOrDefault(module =>
                module.ModuleName.Equals("libvlccore.dll", StringComparison.OrdinalIgnoreCase));
            if (core is null ||
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(core.FileName))) != VideoKernelBuild.VlcCoreSha256)
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            // VLC 3 uses <actual loaded core>/plugins, not the obsolete --plugin-path.
            // MSBuild installs this plugin in that same fixed package closure.
            string path = Path.Combine(Path.GetDirectoryName(core.FileName)!, "plugins", "demux",
                "libmeowfilm_ffmpeg_plugin.dll");
            if (!File.Exists(path) ||
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != VideoKernelBuild.DllSha256)
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            var library = NativeLibrary.Load(path);
            try
            {
                var identity = Marshal.GetDelegateForFunctionPointer<IdentityCallback>(
                    NativeLibrary.GetExport(library, "mvk_build_identity"));
                if (Marshal.PtrToStringUTF8(identity()) != VideoKernelBuild.Identity)
                    throw new PlaybackException(PlaybackError.NativeUnavailable);
                _create = Marshal.GetDelegateForFunctionPointer<CreateCallback>(NativeLibrary.GetExport(library, "mvk_create"));
                _cancel = Marshal.GetDelegateForFunctionPointer<OwnerCallback>(NativeLibrary.GetExport(library, "mvk_cancel"));
                _release = Marshal.GetDelegateForFunctionPointer<OwnerCallback>(NativeLibrary.GetExport(library, "mvk_release"));
                _library = library;
            }
            catch { NativeLibrary.Free(library); throw; }
        }
    }

    internal static IntPtr Create(ref Callbacks callbacks, Uri address, NativeVideoFormat format,
        byte[] pairs, byte[]? defaultKey, string token)
    {
        EnsureLoaded();
        GCHandle pairHandle = default, defaultHandle = default;
        try
        {
            if (pairs.Length > 0) pairHandle = GCHandle.Alloc(pairs, GCHandleType.Pinned);
            if (defaultKey is not null) defaultHandle = GCHandle.Alloc(defaultKey, GCHandleType.Pinned);
            if (_create(ref callbacks, address.AbsoluteUri, (int)format,
                pairHandle.IsAllocated ? pairHandle.AddrOfPinnedObject() : IntPtr.Zero, (uint)(pairs.Length / 32),
                defaultHandle.IsAllocated ? defaultHandle.AddrOfPinnedObject() : IntPtr.Zero, token, out var owner) != 0)
                throw new PlaybackException(PlaybackError.OpenFailed);
            return owner;
        }
        finally
        {
            if (pairHandle.IsAllocated) pairHandle.Free();
            if (defaultHandle.IsAllocated) defaultHandle.Free();
            CryptographicOperations.ZeroMemory(pairs);
            if (defaultKey is not null) CryptographicOperations.ZeroMemory(defaultKey);
        }
    }
    internal static void Cancel(IntPtr owner) => _cancel(owner);
    internal static void Release(IntPtr owner) => _release(owner);
}
