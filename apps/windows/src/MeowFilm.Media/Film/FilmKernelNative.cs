using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MeowFilm.Media;

internal static class FilmKernelNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        internal uint Size, Abi;
        internal IntPtr Opaque, Open, Location, Read, Seek, Close, Interrupted, Failed;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        internal uint Size, Abi;
        internal int State, Error;
        internal double Position, Duration, BufferedUntil;
        internal int Width, Height, AudioTrack, SubtitleTrack;
        internal int NativeHdr, DolbyVision, HardwareDecoding, Seekable;
        internal ulong SubmittedPictures;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Track
    {
        internal uint Size;
        internal int Index, Kind, Selected;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Codec;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Language;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] internal byte[] Title;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OpenCallback(IntPtr context, IntPtr uri, long offset, long end,
        out ulong handle, out long size, out int seekable, out uint locationSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int LocationCallback(IntPtr context, ulong handle, IntPtr location, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReadCallback(IntPtr context, ulong handle, IntPtr output, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate long SeekCallback(IntPtr context, ulong handle, long offset, int whence);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CloseCallback(IntPtr context, ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InterruptedCallback(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FailedCallback(IntPtr context, int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateCallback(ref Callbacks callbacks, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri,
        int format, IntPtr keys, uint count, IntPtr defaultKey, out IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int TargetCallback(IntPtr owner, IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PrepareCallback(IntPtr owner, double position, IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FlagCallback(IntPtr owner, int flag);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ValueCallback(IntPtr owner, double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SnapshotCallback(IntPtr owner, ref Snapshot snapshot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TrackCountCallback(IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TrackReadCallback(IntPtr owner, int ordinal, ref Track track);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SelectTrackCallback(IntPtr owner, int kind, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OwnerCallback(IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr IdentityCallback();

    private static readonly object Gate = new();
    private static IntPtr library;
    private static CreateCallback create = null!;
    private static SnapshotCallback snapshot = null!;
    private static TrackCountCallback trackCount = null!;
    private static TrackReadCallback trackRead = null!;
    private static SelectTrackCallback selectTrack = null!;
    internal static PrepareCallback Prepare = null!;
    internal static TargetCallback Attach = null!;
    internal static FlagCallback Start = null!, Pause = null!;
    internal static ValueCallback Seek = null!, Rate = null!, Volume = null!, Aspect = null!;
    internal static OwnerCallback Cancel = null!, Release = null!;

    private static T Export<T>(IntPtr module, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    private static void Load()
    {
        lock (Gate)
        {
            if (library != IntPtr.Zero) return;
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            IntPtr module = IntPtr.Zero;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "film-kernel", "meowfilm_film_kernel.dll");
                using var file = File.OpenRead(path);
                if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(FilmKernelBuild.DllSha256,
                    StringComparison.OrdinalIgnoreCase)) throw new PlaybackException(PlaybackError.NativeUnavailable);
                module = NativeLibrary.Load(path);
                if (Marshal.PtrToStringUTF8(Export<IdentityCallback>(module, "mfk_build_identity")()) != FilmKernelBuild.Identity)
                    throw new PlaybackException(PlaybackError.NativeUnavailable);
                create = Export<CreateCallback>(module, "mfk_create");
                Prepare = Export<PrepareCallback>(module, "mfk_prepare");
                Attach = Export<TargetCallback>(module, "mfk_attach");
                Start = Export<FlagCallback>(module, "mfk_start");
                Pause = Export<FlagCallback>(module, "mfk_pause");
                Seek = Export<ValueCallback>(module, "mfk_seek");
                Rate = Export<ValueCallback>(module, "mfk_rate");
                Volume = Export<ValueCallback>(module, "mfk_volume");
                Aspect = Export<ValueCallback>(module, "mfk_aspect");
                snapshot = Export<SnapshotCallback>(module, "mfk_snapshot_read");
                trackCount = Export<TrackCountCallback>(module, "mfk_track_count");
                trackRead = Export<TrackReadCallback>(module, "mfk_track_read");
                selectTrack = Export<SelectTrackCallback>(module, "mfk_select_track");
                Cancel = Export<OwnerCallback>(module, "mfk_cancel");
                Release = Export<OwnerCallback>(module, "mfk_release");
                library = module; // Process-lifetime mapping; sessions own their workers, not this DLL.
            }
            catch
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
                throw new PlaybackException(PlaybackError.NativeUnavailable);
            }
        }
    }

    internal static IntPtr Create(ref Callbacks callbacks, Uri uri, NativeVideoFormat format,
        byte[] keys, byte[]? defaultKey)
    {
        GCHandle pairs = default, fallback = default;
        try
        {
            Load();
            if (keys.Length != 0) pairs = GCHandle.Alloc(keys, GCHandleType.Pinned);
            if (defaultKey is not null) fallback = GCHandle.Alloc(defaultKey, GCHandleType.Pinned);
            Check(create(ref callbacks, uri.AbsoluteUri, (int)format,
                pairs.IsAllocated ? pairs.AddrOfPinnedObject() : IntPtr.Zero, (uint)(keys.Length / 32),
                fallback.IsAllocated ? fallback.AddrOfPinnedObject() : IntPtr.Zero, out var owner));
            if (owner == IntPtr.Zero) throw new PlaybackException(PlaybackError.OpenFailed);
            return owner;
        }
        finally
        {
            if (pairs.IsAllocated) pairs.Free();
            if (fallback.IsAllocated) fallback.Free();
            CryptographicOperations.ZeroMemory(keys);
            if (defaultKey is not null) CryptographicOperations.ZeroMemory(defaultKey);
        }
    }

    internal static Snapshot Read(IntPtr owner)
    {
        var value = new Snapshot { Size = (uint)Marshal.SizeOf<Snapshot>(), Abi = 1 };
        Check(snapshot(owner, ref value));
        if (value.Abi != 1 || value.Size != Marshal.SizeOf<Snapshot>())
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        return value;
    }
    internal static int TrackCount(IntPtr owner) => trackCount(owner);
    internal static Track ReadTrack(IntPtr owner, int ordinal)
    {
        var value = new Track
        {
            Size = (uint)Marshal.SizeOf<Track>(),
            Codec = new byte[32], Language = new byte[32], Title = new byte[128]
        };
        Check(trackRead(owner, ordinal, ref value));
        if (value.Size != Marshal.SizeOf<Track>())
            throw new PlaybackException(PlaybackError.TrackMetadataInvalid);
        return value;
    }
    internal static int SelectTrack(IntPtr owner, FilmTrackKind kind, int index) =>
        selectTrack(owner, (int)kind, index);
    internal static void Check(int error)
    {
        if (error != 0) throw new PlaybackException(Code(error));
    }
    internal static string Code(int error) => error switch
    {
        1 => PlaybackError.OpenFailed, 2 => "FILM_FORMAT_UNSUPPORTED",
        3 => "FILM_ENCRYPTION_UNSUPPORTED", 4 => PlaybackError.ReadFailed,
        5 => PlaybackError.InvalidSeek, 6 => "FILM_DECODE_FAILED", 7 => "FILM_AUDIO_FAILED",
        8 => "FILM_RENDER_FAILED", 9 => "FILM_HDR_DISPLAY_UNSUPPORTED", 10 => "FILM_DOVI_UNSUPPORTED",
        11 => PlaybackError.Superseded, 12 => PlaybackError.InvalidRequest, 13 => "FILM_DEVICE_LOST",
        _ => PlaybackError.PlaybackFailed
    };
}
