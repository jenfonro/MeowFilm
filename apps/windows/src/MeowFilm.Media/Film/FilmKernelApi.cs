using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeowFilm.Windows.Tests")]
[assembly: InternalsVisibleTo("MeowFilm.FilmKernel.Tests")]

namespace MeowFilm.Media;

// Per-owner seam: lifecycle tests can block native work without replacing process-wide exports.
internal class FilmKernelApi
{
    internal virtual IntPtr Create(ref FilmKernelNative.Callbacks callbacks, Uri address,
        NativeVideoFormat format, byte[] keys, byte[]? defaultKey) =>
        FilmKernelNative.Create(ref callbacks, address, format, keys, defaultKey);
    internal virtual int Prepare(IntPtr owner, double position, IntPtr target) => FilmKernelNative.Prepare(owner, position, target);
    internal virtual int Attach(IntPtr owner, IntPtr target) => FilmKernelNative.Attach(owner, target);
    internal virtual int Start(IntPtr owner, int paused) => FilmKernelNative.Start(owner, paused);
    internal virtual int Pause(IntPtr owner, int paused) => FilmKernelNative.Pause(owner, paused);
    internal virtual int Seek(IntPtr owner, double position) => FilmKernelNative.Seek(owner, position);
    internal virtual int Rate(IntPtr owner, double rate) => FilmKernelNative.Rate(owner, rate);
    internal virtual int Volume(IntPtr owner, double volume) => FilmKernelNative.Volume(owner, volume);
    internal virtual int Aspect(IntPtr owner, double ratio) => FilmKernelNative.Aspect(owner, ratio);
    internal virtual FilmKernelNative.Snapshot Read(IntPtr owner) => FilmKernelNative.Read(owner);
    internal virtual int TrackCount(IntPtr owner) => FilmKernelNative.TrackCount(owner);
    internal virtual FilmKernelNative.Track ReadTrack(IntPtr owner, int ordinal) => FilmKernelNative.ReadTrack(owner, ordinal);
    internal virtual int SelectTrack(IntPtr owner, FilmTrackKind kind, int index) => FilmKernelNative.SelectTrack(owner, kind, index);
    internal virtual void Cancel(IntPtr owner) => FilmKernelNative.Cancel(owner);
    internal virtual void Release(IntPtr owner) => FilmKernelNative.Release(owner);
}
