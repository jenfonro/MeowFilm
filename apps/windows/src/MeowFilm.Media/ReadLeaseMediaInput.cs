using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

// No path/URI/MIME is accepted here. All bytes and seeks stay on the granted stream.
// Cancel releases the lease BEFORE native Stop joins callback threads. The callback
// GCHandle and buffer remain alive until player/media retirement has completed.
internal sealed class ReadLeaseMediaInput(INativeMediaReadLease lease) : MediaInput, IAsyncDisposable
{
    internal const int BufferCapacity = 64 * 1024;
    internal const long MaximumLength = 1L << 40; // 1 TiB; never an unknown/unbounded native length.
    private const int ProbeSize = 512;
    private readonly object _io = new(), _ownership = new();
    private readonly byte[] _buffer = new byte[BufferCapacity];
    private readonly CancellationTokenSource _lifetime = new();
    private Stream? _stream;
    private CancellationToken _revoked;
    private CancellationTokenRegistration _revocation, _opening;
    private Task? _release, _dispose;
    private long _length, _position;
    private int _opened, _closed, _disposed;
    private string? _error;
    internal string DemuxName { get; private set; } = "";
    internal AdaptiveMediaSession? Adaptive { get; private set; }
    internal string? LastErrorCode => Volatile.Read(ref _error);
    internal event Action<string>? Failed;

    internal void Validate()
    {
        try
        {
            _revoked = lease.Revoked;
            _revocation = _revoked.Register(() =>
            {
                // Many owners cancel their token as part of DisposeAsync. Our own
                // cancellation/release is not a second, authorization-failure terminal.
                if (!_lifetime.IsCancellationRequested) Fail(PlaybackError.ReadRevoked);
            });
            ThrowIfFaulted();
            _stream = lease.Content;
            _length = lease.Length;
            if (_stream is null || !_stream.CanRead || !_stream.CanSeek || _stream.CanWrite ||
                _length < 0 || _length > MaximumLength || _stream.Length != _length)
                throw new PlaybackException(PlaybackError.InvalidReadLease);
            CanSeek = true;
            ThrowIfFaulted();
        }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException(_revoked.IsCancellationRequested
            ? PlaybackError.ReadRevoked : PlaybackError.InvalidReadLease); }
    }

    internal void AttachOpening(CancellationToken token) => _opening = token.Register(Cancel);

    internal async Task PrepareAsync()
    {
        try
        {
            CheckAvailable();
            SeekStream(0);
            int count = 0, target = (int)Math.Min(_length, ProbeSize);
            // Short reads (including byte-at-a-time authorized streams) are legal.
            while (count < target)
            {
                count += await ReadStreamAsync(_buffer.AsMemory(count, target - count)).ConfigureAwait(false);
            }
            if (count >= 8 && _buffer.AsSpan(0, 8).SequenceEqual(NativeAdaptiveArchive.Magic))
            {
                Adaptive = await AdaptiveMediaSession.OpenOfflineAsync(_stream!, _length, _lifetime.Token).ConfigureAwait(false);
                DemuxName = "adaptive";
            }
            else DemuxName = OfflineMediaFormat.Probe(_buffer.AsSpan(0, count));
            SeekStream(0); // Native starts at byte zero; no out-of-band reopen or cached representation.
            Array.Clear(_buffer);
        }
        catch (Exception error)
        {
            ThrowIfFaulted();
            _lifetime.Token.ThrowIfCancellationRequested();
            throw new PlaybackException(error is PlaybackException known ? known.Code : PlaybackError.ReadFailed);
        }
    }

    internal void ThrowIfFaulted()
    {
        if (LastErrorCode is { } code) throw new PlaybackException(code);
        if (_revoked.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            throw new PlaybackException(PlaybackError.ReadRevoked);
    }

    private void CheckAvailable()
    {
        ThrowIfFaulted();
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private void CheckRepresentation()
    {
        CheckAvailable();
        if (_stream!.Length != _length || _stream.Position != _position)
            throw new PlaybackException(PlaybackError.ReadChanged);
    }

    private async ValueTask<int> ReadStreamAsync(Memory<byte> destination)
    {
        CheckRepresentation();
        int count = (int)Math.Min(destination.Length, _length - _position);
        if (count == 0) return 0;
        int read = await _stream!.ReadAsync(destination[..count], _lifetime.Token).ConfigureAwait(false);
        CheckAvailable(); // Never deliver bytes returned concurrently with revocation/cancellation.
        if (read <= 0 || read > count) throw new PlaybackException(PlaybackError.ReadTruncated);
        _position += read;
        CheckRepresentation();
        return read;
    }

    private void SeekStream(long offset)
    {
        CheckAvailable();
        if (_stream!.Length != _length) throw new PlaybackException(PlaybackError.ReadChanged);
        try
        {
            if (_stream.Seek(offset, SeekOrigin.Begin) != offset || _stream.Position != offset)
                throw new PlaybackException(PlaybackError.ReadSeekFailed);
        }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException(_revoked.IsCancellationRequested
            ? PlaybackError.ReadRevoked : PlaybackError.ReadSeekFailed); }
        _position = offset;
        CheckRepresentation();
    }

    public override bool Open(out ulong size)
    {
        size = (ulong)_length;
        return Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _closed) == 0 && !_lifetime.IsCancellationRequested &&
            !_revoked.IsCancellationRequested && LastErrorCode is null &&
            DemuxName.Length != 0 && Interlocked.Exchange(ref _opened, 1) == 0;
    }

    public override int Read(IntPtr buf, uint len)
    {
        lock (_io)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _closed) != 0 || _lifetime.IsCancellationRequested) return -1;
            try
            {
                CheckAvailable();
                if (len == 0) return 0;
                if (buf == IntPtr.Zero) throw new PlaybackException(PlaybackError.InvalidRequest);
                int read = ReadStreamAsync(_buffer.AsMemory(0, (int)Math.Min(len, BufferCapacity)))
                    .AsTask().GetAwaiter().GetResult();
                CheckAvailable();
                if (read != 0) Marshal.Copy(_buffer, 0, buf, read);
                return read;
            }
            catch (Exception error) { CallbackFailure(error); return -1; }
        }
    }

    public override bool Seek(ulong offset)
    {
        lock (_io)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _closed) != 0 || _lifetime.IsCancellationRequested) return false;
            try
            {
                CheckAvailable();
                if (offset > (ulong)_length) throw new PlaybackException(PlaybackError.ReadSeekFailed);
                // Do not bypass the authorization check even for seek-to-current or EOF.
                SeekStream((long)offset);
                return true;
            }
            catch (Exception error) { CallbackFailure(error); return false; }
        }
    }

    private void CallbackFailure(Exception error)
    {
        if (!_lifetime.IsCancellationRequested)
            Fail(_revoked.IsCancellationRequested ? PlaybackError.ReadRevoked
                : error is PlaybackException known ? known.Code : PlaybackError.ReadFailed);
    }

    private void Fail(string code)
    {
        if (Interlocked.CompareExchange(ref _error, code, null) is not null) return;
        Cancel();
        Failed?.Invoke(code); // Only enqueues owner work; never invokes native Stop here.
    }

    internal void Cancel()
    {
        Adaptive?.Cancel();
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* A defective stream callback must not prevent handle release. */ }
        _ = ReleaseLease();
    }

    private Task ReleaseLease()
    {
        lock (_ownership)
            return _release ??= Task.Run(async () =>
            {
                // A lease is the sole resource owner. Never independently dispose its Content.
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch { throw new PlaybackException(PlaybackError.ReleaseFailed); }
            });
    }

    // Demux can finish before the final buffered audio/video is presented. Retain the
    // revocation subscription until the coordinator's terminal retirement, rather than
    // disposing the lease here and losing authorization over that buffered playback.
    public override void Close() => Interlocked.Exchange(ref _closed, 1);

    public ValueTask DisposeAsync()
    {
        lock (_ownership) return new(_dispose ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Cancel();
        try
        {
            try
            {
                if (Adaptive is not null) await Adaptive.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Cancel starts release; even a failed adaptive drain must join the
                // same ownership task before callback state is freed. Failure still propagates.
                await ReleaseLease().ConfigureAwait(false);
            }
        }
        finally
        {
            _opening.Dispose();
            _revocation.Dispose();
            lock (_io)
            {
                _stream = null;
                Array.Clear(_buffer);
                _lifetime.Dispose();
                base.Dispose(true);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
