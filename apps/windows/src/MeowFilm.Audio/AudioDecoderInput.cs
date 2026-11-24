using System.IO;
using System.Runtime.InteropServices;

namespace MeowFilm.Media;

// Owns either one HTTP representation or one transferred read lease. In particular,
// no filename derived from a lease is ever given to FFmpeg or opened a second time.
internal unsafe sealed class AudioDecoderInput : IDisposable
{
    private readonly INativeMediaReadLease? _lease;
    private readonly FFmpegHttpTransport? _http;
    private readonly CancellationTokenSource _lifetime;
    private readonly byte[]? _buffer;
    private CancellationTokenRegistration _abort, _revocation;
    private CancellationToken _cachedOpToken;
    private CancellationTokenSource? _cachedLinked;
    private Stream? _stream;
    private ulong _handle;
    private long _position;
    private int _cancelled, _disposed;
    private string? _error;
    internal long Length { get; private set; } = -1;
    internal bool CanSeek { get; private set; }
    internal string InputKind => _http is not null ? "http" : "read-lease";
    internal CancellationToken Revoked { get; private set; }
    internal CancellationToken Token => _lifetime.Token;
    internal CancellationToken OperationToken { get; set; }
    internal bool Interrupted => Token.IsCancellationRequested || OperationToken.IsCancellationRequested;
    internal string? LastErrorCode => _http?.LastErrorCode ?? Volatile.Read(ref _error);

    internal AudioDecoderInput(INativeMediaReadLease lease, CancellationToken token)
    {
        _lease = lease;
        _buffer = new byte[FFmpegHttpTransport.BufferCapacity];
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _abort = _lifetime.Token.Register(Abort);
    }

    internal AudioDecoderInput(Uri uri, IReadOnlyDictionary<string, string> validatedHeaders, CancellationToken token)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            // Caller validated URI/headers (including rejecting adaptive playlists);
            // the decoder also restricts demuxers and rejects every nested io_open.
            _http = new(uri, validatedHeaders, new NativeHttpMediaOptions(), _lifetime.Token);
            _abort = _lifetime.Token.Register(Abort);
        }
        catch { _lifetime.Dispose(); throw; }
    }

    internal void Open(Uri? address = null)
    {
        if (_http is not null)
        {
            IntPtr uri = Marshal.StringToCoTaskMemUTF8(address!.AbsoluteUri);
            try
            {
                int result = _http.Open(IntPtr.Zero, uri, 0, -1, out _handle, out long length,
                    out int seekable, out _);
                CheckAvailable();
                if (result < 0) throw new PlaybackException(PlaybackError.OpenFailed);
                Length = length;
                CanSeek = seekable != 0;
            }
            finally { Marshal.FreeCoTaskMem(uri); }
            return;
        }
        try
        {
            Revoked = _lease!.Revoked;
            _revocation = Revoked.Register(() =>
            {
                if (!_lifetime.IsCancellationRequested) Fail(PlaybackError.ReadRevoked);
            });
            CheckAvailable();
            _stream = _lease.Content;
            Length = _lease.Length;
            if (_stream is null || !_stream.CanRead || !_stream.CanSeek || _stream.CanWrite ||
                Length < 0 || _stream.Length != Length)
                throw new PlaybackException(PlaybackError.InvalidReadLease);
            CanSeek = true;
            Seek(0, 0);
        }
        catch (PlaybackException) { throw; }
        catch (OperationCanceledException) { CheckAvailable(); throw; }
        catch { throw new PlaybackException(PlaybackError.InvalidReadLease); }
    }

    internal void CheckAuthorization()
    {
        if (LastErrorCode is { } code) throw new PlaybackException(code);
        if (Revoked.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            throw new PlaybackException(PlaybackError.ReadRevoked);
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    internal void CheckAvailable()
    {
        CheckAuthorization();
        OperationToken.ThrowIfCancellationRequested();
    }

    private CancellationToken GetReadToken()
    {
        var op = OperationToken;
        if (!op.CanBeCanceled) return _lifetime.Token;
        if (_cachedLinked is null || _cachedOpToken != op)
        {
            _cachedLinked?.Dispose();
            _cachedOpToken = op;
            _cachedLinked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, op);
        }
        return _cachedLinked.Token;
    }

    internal int Read(IntPtr destination, int count)
    {
        try
        {
            CheckAvailable();
            if (_http is not null)
            {
                int read = _http.Read(IntPtr.Zero, _handle, destination, count, OperationToken);
                CheckAvailable();
                if (read < 0) throw new PlaybackException(PlaybackError.ReadFailed);
                return read;
            }
            CheckRepresentation();
            count = (int)Math.Min(count, Length - _position);
            if (count == 0) return 0;
            int copied;
            if (_stream is FileStream fileStream)
            {
                copied = fileStream.Read(new Span<byte>((void*)destination, count));
            }
            else
            {
                var buffer = _buffer!;
                count = Math.Min(count, buffer.Length);
                var readTask = _stream!.ReadAsync(buffer.AsMemory(0, count), GetReadToken());
                copied = readTask.IsCompletedSuccessfully ? readTask.Result : readTask.AsTask().GetAwaiter().GetResult();
                if (copied > 0) Marshal.Copy(buffer, 0, destination, copied);
            }
            CheckAvailable();
            if (copied <= 0 || copied > count) throw new PlaybackException(PlaybackError.ReadTruncated);
            _position += copied;
            CheckRepresentation();
            return copied;
        }
        catch (Exception error)
        {
            CallbackFailure(error, PlaybackError.ReadFailed);
            throw;
        }
    }

    internal long Seek(long offset, int whence)
    {
        try
        {
            CheckAvailable();
            if (_http is not null)
            {
                long result = _http.Seek(IntPtr.Zero, _handle, offset, whence, OperationToken);
                CheckAvailable();
                if (result < 0) throw new PlaybackException(PlaybackError.ReadSeekFailed);
                return result;
            }
            if (_stream!.Length != Length) throw new PlaybackException(PlaybackError.ReadChanged);
            long target = whence switch
            {
                0 => offset, 1 => checked(_position + offset), 2 => checked(Length + offset),
                _ => throw new PlaybackException(PlaybackError.ReadSeekFailed)
            };
            if (target < 0 || target > Length || _stream.Seek(target, SeekOrigin.Begin) != target)
                throw new PlaybackException(PlaybackError.ReadSeekFailed);
            _position = target;
            CheckRepresentation();
            return target;
        }
        catch (Exception error)
        {
            CallbackFailure(error, PlaybackError.ReadSeekFailed);
            throw;
        }
    }

    private void CheckRepresentation()
    {
        CheckAvailable();
        if (_stream!.Length != Length || _stream.Position != _position)
            throw new PlaybackException(PlaybackError.ReadChanged);
    }

    private void CallbackFailure(Exception error, string fallback)
    {
        if (!Interrupted)
            Fail(Revoked.IsCancellationRequested ? PlaybackError.ReadRevoked :
                error is PlaybackException known ? known.Code : fallback);
    }

    internal void Fail(string code)
    {
        Interlocked.CompareExchange(ref _error, code, null);
        Cancel();
    }

    // Lock-free cancellation interrupts blocked HTTP or stream reads.
    // The read lease itself is disposed strictly in Dispose() after the decoder worker has exited.
    internal void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
        Abort();
    }

    private void Abort() => _http?.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        try
        {
            if (_lease is not null) _lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _http?.Dispose();
        }
        catch { throw new PlaybackException(PlaybackError.ReleaseFailed); }
        finally
        {
            _revocation.Dispose();
            _abort.Dispose();
            _cachedLinked?.Dispose();
            _cachedLinked = null;
            _stream = null;
            _lifetime.Dispose();
        }
    }
}

internal sealed class AudioDecoderFileLease : INativeMediaReadLease
{
    private readonly FileStream _stream;
    public Stream Content => _stream;
    public long Length { get; }
    public CancellationToken Revoked => CancellationToken.None;

    internal AudioDecoderFileLease(string file)
    {
        _stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            FFmpegHttpTransport.BufferCapacity, FileOptions.RandomAccess);
        try { Length = _stream.Length; }
        catch { _stream.Dispose(); throw; }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
