using System.Runtime.InteropServices;

namespace MeowFilm.Media;

/// <summary>Private packet-input lifetime; libVLC remains the decode/render owner.</summary>
internal sealed class FFmpegMediaInput : IAsyncDisposable
{
    private readonly FFmpegHttpTransport _http;
    // Keep every delegate alive until mvk_release confirms the plugin has no users.
    private readonly FFmpegNativeLibrary.OpenCallback _open;
    private readonly FFmpegNativeLibrary.LocationCallback _location;
    private readonly FFmpegNativeLibrary.ReadCallback _read;
    private readonly FFmpegNativeLibrary.SeekCallback _seek;
    private readonly FFmpegNativeLibrary.CloseCallback _close;
    private readonly FFmpegNativeLibrary.InterruptedCallback _interrupted;
    private readonly FFmpegNativeLibrary.FailedCallback _failed;
    private readonly object _releaseGate = new();
    private IntPtr _owner;
    private int _cancelled;
    private string? _error;
    private Task? _release;
    internal string Mrl { get; }
    internal string? LastErrorCode => _http.LastErrorCode ?? Volatile.Read(ref _error);
    internal event Action<string>? Failed;

    internal FFmpegMediaInput(Uri address, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, NativeVideoFormat format, byte[] pairs, byte[]? defaultKey,
        CancellationToken token)
    {
        _http = new(address, headers, options, token);
        _open = _http.Open; _location = _http.Location; _read = _http.Read; _seek = _http.Seek; _close = _http.Close;
        _interrupted = _ => _http.Cancelled ? 1 : 0;
        _failed = (_, code) =>
        {
            if (Volatile.Read(ref _cancelled) != 0 || _http.Cancelled) return;
            string error = code switch
            {
                2 => PlaybackError.HttpFormatUnsupported,
                3 => PlaybackError.AdaptiveEncryptionUnsupported,
                4 => PlaybackError.ReadFailed,
                5 => PlaybackError.ReadSeekFailed,
                _ => PlaybackError.OpenFailed
            };
            if (Interlocked.CompareExchange(ref _error, error, null) is null) Notify(error);
        };
        _http.Failed += OnHttpFailed;
        var callbacks = new FFmpegNativeLibrary.Callbacks
        {
            Size = (uint)Marshal.SizeOf<FFmpegNativeLibrary.Callbacks>(), Abi = 2,
            Open = Marshal.GetFunctionPointerForDelegate(_open),
            Location = Marshal.GetFunctionPointerForDelegate(_location), Read = Marshal.GetFunctionPointerForDelegate(_read),
            Seek = Marshal.GetFunctionPointerForDelegate(_seek), Close = Marshal.GetFunctionPointerForDelegate(_close),
            Interrupted = Marshal.GetFunctionPointerForDelegate(_interrupted),
            Failed = Marshal.GetFunctionPointerForDelegate(_failed)
        };
        string identity = Guid.NewGuid().ToString("N");
        Mrl = "meowfilm-ffmpeg://" + identity;
        try { _owner = FFmpegNativeLibrary.Create(ref callbacks, address, format, pairs, defaultKey, identity); }
        catch { _http.Dispose(); throw; }
    }
    private void OnHttpFailed(string code) => Notify(code);
    private void Notify(string code)
    {
        try { Failed?.Invoke(code); }
        catch { Interlocked.Exchange(ref _error, PlaybackError.CallbackFailed); }
    }

    internal void Cancel()
    {
        lock (_releaseGate)
        {
            if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
            // Unblock IO before native Stop. Release cannot overtake this native pointer use.
            _http.Cancel();
            if (_owner != IntPtr.Zero) FFmpegNativeLibrary.Cancel(_owner);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_releaseGate) return new(_release ??= ReleaseAsync());
    }
    private async Task ReleaseAsync()
    {
        await Task.Yield();
        Cancel();
        await Task.Run(() => FFmpegNativeLibrary.Release(_owner)).ConfigureAwait(false);
        lock (_releaseGate) _owner = IntPtr.Zero;
        _http.Failed -= OnHttpFailed;
        _http.Dispose();
        GC.KeepAlive(_open); GC.KeepAlive(_location); GC.KeepAlive(_read); GC.KeepAlive(_seek); GC.KeepAlive(_close);
        GC.KeepAlive(_interrupted); GC.KeepAlive(_failed);
    }
}
