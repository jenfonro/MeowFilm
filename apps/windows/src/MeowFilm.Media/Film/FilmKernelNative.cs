using System.IO;
using System.Runtime.InteropServices;
using MeowFilm.Core.Debug;

namespace MeowFilm.Media;

internal static class FilmKernelNative
{
    internal const uint Abi = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Surface : IEquatable<Surface>
    {
        internal uint Size, Abi;
        internal nint DisplayWindow;
        internal int Width, Height;
        internal float ScaleX, ScaleY;
        internal int Visible;
        internal int Reserved;

        public bool Equals(Surface other) =>
            Size == other.Size && Abi == other.Abi && DisplayWindow == other.DisplayWindow &&
            Width == other.Width && Height == other.Height &&
            ScaleX.Equals(other.ScaleX) && ScaleY.Equals(other.ScaleY) && Visible == other.Visible;

        public override bool Equals(object? obj) => obj is Surface other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Size, Abi, DisplayWindow, Width, Height, ScaleX, ScaleY, Visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        internal uint Size, Abi;
        internal IntPtr Opaque, Open, Location, Read, Seek, Close, Interrupted, Failed, Log;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        internal uint Size, Abi;
        internal int State, Error;
        internal double Position, Duration, BufferedUntil;
        internal int Width, Height;
        internal int VideoTrack, AudioTrack, SubtitleTrack;
        internal int NativeHdr, ToneMapped, DolbyVision, HardwareDecoding;
        internal int Seekable, Muted, AudioDegraded;
        internal ulong SubmittedPictures;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Diagnostics
    {
        internal uint Size, Abi;
        internal ulong DroppedFrames;
        internal ulong AudioUnderruns;
        internal ulong AudioReconnects;
        internal ulong SubtitleErrors;
        internal double AvSyncDiff;
        internal double RenderFps;
        internal int VideoPacketQueue;
        internal int AudioPacketQueue;
        internal int SubtitlePacketQueue;
        internal int PictureQueue;
        internal int HardwareFallback;
        internal int AudioDegraded;
        internal int ToneMapped;
        internal int DisplayHdrEnabled;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] internal byte[] DecodeFallbackReason;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] internal byte[] LastNativeError;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct Chapter
    {
        internal uint Size;
        internal int Index;
        internal double Start, End;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] internal byte[] Title;
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
    internal delegate void LogCallback(IntPtr context, int level, IntPtr component, IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateCallback(ref Callbacks callbacks, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri,
        int format, IntPtr keys, uint count, IntPtr defaultKey, out IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TargetCallback(IntPtr owner, IntPtr target, out IntPtr swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SurfaceTargetCallback(IntPtr owner, ref Surface target, out IntPtr swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int SurfaceUpdateCallback(IntPtr owner, ref Surface target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PrepareCallback(IntPtr owner, double position, IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FlagCallback(IntPtr owner, int flag);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ValueCallback(IntPtr owner, double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SnapshotCallback(IntPtr owner, ref Snapshot snapshot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DiagnosticsCallback(IntPtr owner, ref Diagnostics diagnostics);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountCallback(IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TrackReadCallback(IntPtr owner, int ordinal, ref Track track);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ChapterReadCallback(IntPtr owner, int ordinal, ref Chapter chapter);
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
    private static DiagnosticsCallback diagnostics = null!;
    private static CountCallback trackCount = null!;
    private static TrackReadCallback trackRead = null!;
    private static CountCallback chapterCount = null!;
    private static ChapterReadCallback chapterRead = null!;
    private static SelectTrackCallback selectTrack = null!;
    internal static PrepareCallback Prepare = null!;
    private static TargetCallback detach = null!;
    private static SurfaceTargetCallback attach = null!;
    internal static SurfaceUpdateCallback UpdateSurface = null!;
    internal static FlagCallback Start = null!, Pause = null!, Mute = null!;
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
                module = NativeLibrary.Load(path);
                if (Marshal.PtrToStringUTF8(Export<IdentityCallback>(module, "mfk_build_identity")()) != FilmKernelBuild.Identity)
                    throw new PlaybackException(PlaybackError.NativeUnavailable);

                // Resolve all exports into locals first so a partial failure never publishes dangling delegates.
                var fnCreate = Export<CreateCallback>(module, "mfk_create");
                var fnPrepare = Export<PrepareCallback>(module, "mfk_prepare");
                var fnDetach = Export<TargetCallback>(module, "mfk_attach");
                var fnAttach = Export<SurfaceTargetCallback>(module, "mfk_attach");
                var fnUpdateSurface = Export<SurfaceUpdateCallback>(module, "mfk_surface_update");
                var fnStart = Export<FlagCallback>(module, "mfk_start");
                var fnPause = Export<FlagCallback>(module, "mfk_pause");
                var fnSeek = Export<ValueCallback>(module, "mfk_seek");
                var fnRate = Export<ValueCallback>(module, "mfk_rate");
                var fnVolume = Export<ValueCallback>(module, "mfk_volume");
                var fnMute = Export<FlagCallback>(module, "mfk_mute");
                var fnAspect = Export<ValueCallback>(module, "mfk_aspect");
                var fnSnapshot = Export<SnapshotCallback>(module, "mfk_snapshot_read");
                var fnDiagnostics = Export<DiagnosticsCallback>(module, "mfk_diagnostics_read");
                var fnTrackCount = Export<CountCallback>(module, "mfk_track_count");
                var fnTrackRead = Export<TrackReadCallback>(module, "mfk_track_read");
                var fnChapterCount = Export<CountCallback>(module, "mfk_chapter_count");
                var fnChapterRead = Export<ChapterReadCallback>(module, "mfk_chapter_read");
                var fnSelectTrack = Export<SelectTrackCallback>(module, "mfk_select_track");
                var fnCancel = Export<OwnerCallback>(module, "mfk_cancel");
                var fnRelease = Export<OwnerCallback>(module, "mfk_release");

                create = fnCreate;
                Prepare = fnPrepare;
                detach = fnDetach;
                attach = fnAttach;
                UpdateSurface = fnUpdateSurface;
                Start = fnStart;
                Pause = fnPause;
                Seek = fnSeek;
                Rate = fnRate;
                Volume = fnVolume;
                Mute = fnMute;
                Aspect = fnAspect;
                snapshot = fnSnapshot;
                diagnostics = fnDiagnostics;
                trackCount = fnTrackCount;
                trackRead = fnTrackRead;
                chapterCount = fnChapterCount;
                chapterRead = fnChapterRead;
                selectTrack = fnSelectTrack;
                Cancel = fnCancel;
                Release = fnRelease;
                library = module;
            }
            catch (Exception error)
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
                if (DebugLog.IsEnabled)
                    DebugLog.Error(DebugCategories.FilmPlayback, "filmkernel", "native.load_failed", error: error);
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
        }
    }

    internal static Snapshot Read(IntPtr owner)
    {
        var value = new Snapshot { Size = (uint)Marshal.SizeOf<Snapshot>(), Abi = Abi };
        Check(snapshot(owner, ref value));
        if (value.Abi != Abi || value.Size != Marshal.SizeOf<Snapshot>())
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        return value;
    }

    internal static Diagnostics ReadDiagnostics(IntPtr owner)
    {
        var value = new Diagnostics
        {
            Size = (uint)Marshal.SizeOf<Diagnostics>(),
            Abi = Abi,
            DecodeFallbackReason = new byte[64],
            LastNativeError = new byte[128]
        };
        Check(diagnostics(owner, ref value));
        if (value.Abi != Abi || value.Size != Marshal.SizeOf<Diagnostics>())
            throw new PlaybackException(PlaybackError.NativeUnavailable);
        return value;
    }

    internal static int Attach(IntPtr owner, Surface? target, out IntPtr swapchain)
    {
        if (target is not { } value) return detach(owner, IntPtr.Zero, out swapchain);
        return attach(owner, ref value, out swapchain);
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

    internal static int ChapterCount(IntPtr owner) => chapterCount(owner);

    internal static Chapter ReadChapter(IntPtr owner, int ordinal)
    {
        var value = new Chapter
        {
            Size = (uint)Marshal.SizeOf<Chapter>(),
            Title = new byte[256]
        };
        Check(chapterRead(owner, ordinal, ref value));
        if (value.Size != Marshal.SizeOf<Chapter>())
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
        1 => PlaybackError.OpenFailed,
        2 => PlaybackError.FilmFormatUnsupported,
        3 => PlaybackError.FilmEncryptionUnsupported,
        4 => PlaybackError.ReadFailed,
        5 => PlaybackError.InvalidSeek,
        6 => PlaybackError.FilmDecodeFailed,
        7 => PlaybackError.FilmAudioFailed,
        8 => PlaybackError.FilmRenderFailed,
        9 => PlaybackError.FilmHdrDisplayUnsupported,
        10 => PlaybackError.FilmDoviUnsupported,
        11 => PlaybackError.Superseded,
        12 => PlaybackError.InvalidRequest,
        13 => PlaybackError.FilmDeviceLost,
        _ => PlaybackError.PlaybackFailed
    };
}
