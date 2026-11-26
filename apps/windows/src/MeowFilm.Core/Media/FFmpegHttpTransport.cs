using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using MeowFilm.Core.Debug;

namespace MeowFilm.Media;

/// <summary>Custom AVIO transport. Uses the input HTTP headers and revocable lifetime; never starts a server.</summary>
internal sealed class FFmpegHttpTransport : IDisposable
{
    internal const int BufferCapacity = 64 * 1024;
    private const long MaxForwardSkipBytes = 256 * 1024;

    private static int activeTransports;
    private static int activeReaders;
    internal static int ActiveTransportCount => Volatile.Read(ref activeTransports);
    internal static int ActiveReaderCount => Volatile.Read(ref activeReaders);

    private readonly object _gate = new();
    private readonly object _lifetimeGate = new();
    private readonly NativeHttpMediaOptions _options;
    private readonly Dictionary<string, string> _headers;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _lifetimeToken;
    private readonly Dictionary<ulong, Reader> _readers = [];
    private ulong _nextHandle;
    private bool _cancelled;
    private bool _disposed;
    private string? _error;

    internal string? LastErrorCode => Volatile.Read(ref _error);
    internal bool Cancelled => _lifetimeToken.IsCancellationRequested || Volatile.Read(ref _cancelled);

    internal FFmpegHttpTransport(Uri root, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, CancellationToken token)
    {
        _options = options;
        _headers = NativeHttpMediaPolicy.ValidateRequest(root, headers, allowAdaptive: true);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _lifetimeToken = _lifetime.Token;
        (_client, _ownsClient) = options.AcquireClient();
        Interlocked.Increment(ref activeTransports);
    }

    internal async Task<long> CopyCompleteAsync(Uri address, Stream destination, long maximumBytes,
        CancellationToken token, Action<long>? progress, Action<long?>? opened)
    {
        using var reader = new Reader(this, address, 0, -1, progressive: true);
        using var abort = _lifetimeToken.Register(reader.Abort);
        return await reader.CopyCompleteAsync(destination, maximumBytes, token, progress, opened).ConfigureAwait(false);
    }

    internal int Open(IntPtr ignored, IntPtr uri, long offset, long end,
        out ulong handle, out long size, out int seekable, out uint locationSize)
    {
        handle = 0; size = -1; seekable = 0; locationSize = 0;
        Reader? reader = null;
        try
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            string? raw = Marshal.PtrToStringUTF8(uri);
            if (string.IsNullOrEmpty(raw)) throw new PlaybackException(PlaybackError.UnsupportedUri);
            var address = new Uri(raw, UriKind.Absolute);
            NativeHttpMediaOptions.ValidateSyntax(address);
            reader = new(this, address, offset, end);
            reader.OpenAsync(initial: true).GetAwaiter().GetResult();
            locationSize = checked((uint)Encoding.UTF8.GetByteCount(reader.Address.AbsoluteUri) + 1);
            lock (_gate)
            {
                _lifetimeToken.ThrowIfCancellationRequested();
                handle = ++_nextHandle;
                _readers.Add(handle, reader);
                size = reader.Length ?? -1;
                seekable = reader.CanSeek ? 1 : 0;
                reader = null;
            }
            return 0;
        }
        catch (Exception error)
        {
            RecordReaderError("open", error);
            return -1;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    internal int Location(IntPtr ignored, ulong handle, IntPtr location, uint capacity)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(ReaderFor(handle).Address.AbsoluteUri + "\0");
            if (location == IntPtr.Zero || bytes.Length > capacity)
                throw new PlaybackException(PlaybackError.ReadFailed);
            Marshal.Copy(bytes, 0, location, bytes.Length);
            return 0;
        }
        catch (Exception error)
        {
            RecordReaderError("location", error);
            return -1;
        }
    }

    internal int Read(IntPtr ignored, ulong handle, IntPtr buffer, int length, CancellationToken token = default)
    {
        try { return ReaderFor(handle).Read(buffer, length, token); }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) RecordReaderError("read", error);
            return -1;
        }
    }

    internal long Seek(IntPtr ignored, ulong handle, long offset, int whence, CancellationToken token = default)
    {
        try { return ReaderFor(handle).Seek(offset, whence, token); }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) RecordReaderError("seek", error);
            return -1;
        }
    }

    internal void Close(IntPtr ignored, ulong handle)
    {
        Reader? reader;
        lock (_gate) _readers.Remove(handle, out reader);
        try { reader?.Dispose(); }
        catch (Exception error) { RecordReaderError("close", error); }
    }

    private Reader ReaderFor(ulong handle)
    {
        lock (_gate)
        {
            _lifetimeToken.ThrowIfCancellationRequested();
            return _readers.TryGetValue(handle, out var reader) ? reader :
                throw new PlaybackException(PlaybackError.ReadFailed);
        }
    }

    // Isolate per-reader errors: never call global Cancel() on a single sub-stream
    // error or speculative seek, so sibling HLS/DASH readers keep running cleanly.
    private void RecordReaderError(string op, Exception error)
    {
        if (Cancelled) return;
        string code = NativeHttpMediaPolicy.Classify(error);
        Interlocked.CompareExchange(ref _error, code, null);
        if (DebugLog.IsEnabled)
        {
            DebugLog.Warn(DebugCategories.FilmPlayback, "http-transport", "reader.error",
                new { op, code });
        }
    }

    internal void Cancel()
    {
        lock (_lifetimeGate)
        {
            if (_cancelled) return;
            _cancelled = true;
            if (!_disposed)
            {
                try { _lifetime.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
        Reader[] readers;
        lock (_gate) readers = _readers.Values.ToArray();
        foreach (var reader in readers)
        {
            try { reader.Abort(); }
            catch { }
        }
    }

    public void Dispose()
    {
        Cancel();
        Reader[] readers;
        lock (_gate)
        {
            readers = _readers.Values.ToArray();
            _readers.Clear();
        }
        foreach (var reader in readers)
        {
            try { reader.Dispose(); }
            catch { }
        }
        bool decrement;
        lock (_lifetimeGate)
        {
            decrement = !_disposed;
            if (!_disposed)
            {
                _disposed = true;
                _lifetime.Dispose();
            }
        }
        if (decrement)
        {
            Interlocked.Decrement(ref activeTransports);
            if (_ownsClient) { try { _client.Dispose(); } catch { } }
            _headers.Clear();
        }
    }

    private sealed class Reader : IDisposable
    {
        private readonly object _io = new();
        private readonly object _streamGate = new();
        private readonly FFmpegHttpTransport _owner;
        private readonly long _end;
        private readonly bool _progressive;
        private readonly byte[] _buffer = new byte[BufferCapacity];
        private CancellationTokenSource? _readCts;
        private HttpResponseMessage? _response;
        private Stream? _body;
        private long _position;
        private long _responseStart;
        private long? _responseEnd;
        private bool _rangeSupported;
        private int _abortVersion;
        private int _disposed;

        internal Uri Address { get; private set; }
        internal long? Length { get; private set; }
        internal bool CanSeek { get; private set; }

        internal Reader(FFmpegHttpTransport owner, Uri address, long start, long end, bool progressive = false)
        {
            if (start < 0 || end < -1 || end >= 0 && end <= start)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            _owner = owner; Address = address; _position = start; _end = end; _progressive = progressive;
            Interlocked.Increment(ref activeReaders);
        }

        private CancellationToken AcquireDeadline(TimeSpan timeout, CancellationToken external, int expectedAbortVersion,
            out CancellationTokenSource? linked)
        {
            if (external.CanBeCanceled)
            {
                if (_owner.Cancelled || Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _abortVersion) != expectedAbortVersion)
                    throw new OperationCanceledException();
                linked = CancellationTokenSource.CreateLinkedTokenSource(_owner._lifetimeToken, external);
                linked.CancelAfter(timeout);
                return linked.Token;
            }
            lock (_streamGate)
            {
                if (_owner.Cancelled || _disposed != 0 || _abortVersion != expectedAbortVersion)
                    throw new OperationCanceledException();
                if (_readCts is null || !_readCts.TryReset())
                {
                    _readCts?.Dispose();
                    _readCts = CancellationTokenSource.CreateLinkedTokenSource(_owner._lifetimeToken);
                }
                _readCts.CancelAfter(timeout);
                linked = null;
                return _readCts.Token;
            }
        }

        private void DisarmDeadline()
        {
            lock (_streamGate)
            {
                if (_disposed == 0 && _readCts is not null)
                {
                    try { _readCts.CancelAfter(Timeout.InfiniteTimeSpan); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        internal async Task OpenAsync(bool initial, CancellationToken operation = default)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_owner._lifetimeToken, operation);
            if (!_progressive) deadline.CancelAfter(_owner._options.RequestTimeout);
            var token = deadline.Token;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var currentUri = Address;

            for (int hop = 0; ; hop++)
            {
                token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0) throw new PlaybackException(PlaybackError.ReadFailed);
                int openVersion = Volatile.Read(ref _abortVersion);
                NativeHttpMediaOptions.ValidateSyntax(currentUri);
                if (_progressive) NativeHttpMediaPolicy.RejectAdaptivePath(currentUri);
                if (!visited.Add(currentUri.AbsoluteUri)) throw new PlaybackException(PlaybackError.HttpRedirectFailed);

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                foreach (var (name, value) in _owner._headers)
                    request.Headers.TryAddWithoutValidation(name, value);
                NativeHttpMediaOptions.ApplyUserInfo(request);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                request.Headers.Range = new RangeHeaderValue(_position, _end >= 0 ? _end - 1 : null);
                var response = await _owner._client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (token.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 ||
                    Volatile.Read(ref _abortVersion) != openVersion)
                {
                    response.Dispose();
                    token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException();
                }

                HttpResponseMessage? oldResponse;
                lock (_streamGate)
                {
                    if (_disposed != 0 || _abortVersion != openVersion)
                    {
                        response.Dispose();
                        throw new OperationCanceledException();
                    }
                    oldResponse = _response;
                    _response = response;
                    _body = null;
                }
                oldResponse?.Dispose();

                try
                {
                    int status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        if (hop >= _owner._options.MaximumRedirects || response.Headers.Location is not { } location)
                            throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                        var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        NativeHttpMediaOptions.ValidateSyntax(next);
                        if (initial || status is 301 or 308) Address = next;
                        currentUri = next;
                        Abort();
                        continue;
                    }
                    if (!initial && (status == 412 || (status == 200 && (_position != 0 || _end >= 0))))
                        throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                    if (status is not (200 or 206))
                        throw new PlaybackException(status switch
                        {
                            401 or 403 => PlaybackError.HttpUnauthorized, 404 => PlaybackError.HttpNotFound,
                            410 => PlaybackError.HttpExpired, 416 => PlaybackError.HttpRangeRejected,
                            429 => PlaybackError.HttpThrottled, >= 500 => PlaybackError.HttpServerFailed,
                            _ => PlaybackError.HttpStatusFailed
                        });
                    if (response.Content.Headers.ContentEncoding.Any(value =>
                        !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                        throw new PlaybackException(PlaybackError.HttpEncodingUnsupported);
                    if (response.Content.Headers.ContentType?.MediaType?.StartsWith("multipart/",
                        StringComparison.OrdinalIgnoreCase) == true)
                        throw new PlaybackException(PlaybackError.HttpInvalidRange);
                    if (_progressive)
                    {
                        string type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                        if (type.Contains("mpegurl", StringComparison.Ordinal) || type.Contains("dash", StringComparison.Ordinal) ||
                            type.Contains("vnd.ms-sstr", StringComparison.Ordinal))
                            throw new PlaybackException(PlaybackError.UnsupportedAdaptive);
                    }

                    long? total;
                    if (status == 206)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                            !range.HasRange || range.From != _position || range.To < _position ||
                            (range.HasLength && (range.Length <= 0 || range.To >= range.Length)) ||
                            (_end >= 0 && range.To >= _end) ||
                            (response.Content.Headers.ContentLength is { } count && count != range.To - range.From + 1))
                            throw new PlaybackException(PlaybackError.HttpInvalidRange);
                        total = range.HasLength ? range.Length : null;
                        _responseEnd = range.To + 1;
                    }
                    else
                    {
                        if (_position != 0 || _end >= 0 || response.Content.Headers.ContentRange is not null)
                            throw new PlaybackException(PlaybackError.HttpInvalidRange);
                        total = response.Content.Headers.ContentLength;
                        _responseEnd = total;
                    }
                    if (!initial && Length is not null && total is not null && Length != total)
                        throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                    if (initial)
                    {
                        Length = total;
                        bool acceptsBytes = response.Headers.AcceptRanges.Any(v =>
                            v.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                        _rangeSupported = status == 206 || acceptsBytes;
                        CanSeek = _rangeSupported && total is > 0;
                    }

                    var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    lock (_streamGate)
                    {
                        if (_disposed != 0 || _abortVersion != openVersion || token.IsCancellationRequested ||
                            !ReferenceEquals(_response, response))
                        {
                            stream.Dispose();
                            token.ThrowIfCancellationRequested();
                            throw new OperationCanceledException();
                        }
                        _responseStart = _position;
                        _body = stream;
                    }
                    return;
                }
                catch
                {
                    Abort();
                    throw;
                }
            }
        }

        internal int Read(IntPtr output, int length, CancellationToken token)
        {
            lock (_io)
            {
                if (output == IntPtr.Zero || length <= 0)
                    throw new PlaybackException(PlaybackError.ReadFailed);
                int read = ReadAsync(_buffer.AsMemory(0, Math.Min(length, BufferCapacity)), token).GetAwaiter().GetResult();
                if (read > 0) Marshal.Copy(_buffer, 0, output, read);
                return read;
            }
        }

        private Stream? CurrentBody(out int abortVersion)
        {
            lock (_streamGate)
            {
                abortVersion = _abortVersion;
                return _body;
            }
        }

        private async Task<int> ReadAsync(Memory<byte> destination, CancellationToken token, bool probe = false)
        {
            while (true)
            {
                _owner._lifetimeToken.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0) throw new PlaybackException(PlaybackError.ReadFailed);
                if (_end >= 0 && _position == _end || Length == _position) return 0;

                var body = CurrentBody(out int readVersion);
                if (body is null || _responseEnd is { } end && _position == end)
                {
                    if (!CanSeek && !_rangeSupported) throw new PlaybackException(PlaybackError.HttpTruncated);
                    Abort();
                    try
                    {
                        await OpenAsync(initial: false, token).ConfigureAwait(false);
                    }
                    catch (PlaybackException ex) when (Length is null && ex.Code == PlaybackError.HttpRangeRejected)
                    {
                        // Unknown-total 206 stream (e.g. bytes 0-1023/*) reached EOF cleanly.
                        return 0;
                    }
                    body = CurrentBody(out readVersion);
                    if (body is null)
                    {
                        _owner._lifetimeToken.ThrowIfCancellationRequested();
                        token.ThrowIfCancellationRequested();
                        throw new OperationCanceledException();
                    }
                }
                int count = destination.Length;
                if (_responseEnd is { } limit) count = (int)Math.Min(count, limit - _position);

                CancellationToken readToken;
                CancellationTokenSource? linked = null;
                if (_progressive && probe)
                {
                    readToken = token;
                }
                else
                {
                    readToken = AcquireDeadline(_owner._options.ReadTimeout, token, readVersion, out linked);
                }

                int read;
                try
                {
                    read = await body.ReadAsync(destination[..count], readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && !_owner.Cancelled)
                {
                    if (CanSeek) Abort();
                    throw;
                }
                catch (OperationCanceledException) when (_owner.Cancelled)
                {
                    Abort();
                    throw;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException &&
                    CanSeek && !_progressive && _position > _responseStart &&
                    !_owner.Cancelled && !token.IsCancellationRequested)
                {
                    // Upstream Range response closed mid-slice after delivering bytes; continue from new offset.
                    Abort();
                    continue;
                }
                finally
                {
                    linked?.Dispose();
                    if (linked is null && !(_progressive && probe)) DisarmDeadline();
                }

                if (read == 0 && _responseEnd is { } expected && _position != expected)
                {
                    if (CanSeek && !_progressive && _position > _responseStart &&
                        !_owner.Cancelled && !token.IsCancellationRequested)
                    {
                        Abort();
                        continue;
                    }
                    throw new PlaybackException(PlaybackError.HttpTruncated);
                }
                _position += read;
                return read;
            }
        }

        internal async Task<long> CopyCompleteAsync(Stream destination, long maximumBytes, CancellationToken token,
            Action<long>? progress, Action<long?>? opened)
        {
            int prefix = 0;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_owner._lifetimeToken, token))
            {
                deadline.CancelAfter(_owner._options.RequestTimeout);
                await OpenAsync(initial: true, deadline.Token).ConfigureAwait(false);
                if (Length > maximumBytes) throw new PlaybackException(PlaybackError.InvalidRequest);
                while (prefix < 512)
                {
                    int read = await ReadAsync(_buffer.AsMemory(prefix, 512 - prefix), deadline.Token, probe: true)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    prefix += read;
                }
            }
            RequireProgressiveFormat(_buffer.AsSpan(0, prefix));
            opened?.Invoke(Length);
            long written = 0;
            int count = prefix;
            while (count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (written + count > maximumBytes) throw new PlaybackException(PlaybackError.InvalidRequest);
                await destination.WriteAsync(_buffer.AsMemory(0, count), token).ConfigureAwait(false);
                written += count;
                progress?.Invoke(written);
                count = await ReadAsync(_buffer, token).ConfigureAwait(false);
            }
            if (written == 0 || Length is { } expected && written != expected)
                throw new PlaybackException(PlaybackError.HttpTruncated);
            return written;
        }

        private static void RequireProgressiveFormat(ReadOnlySpan<byte> bytes)
        {
            string text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("<MPD", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("<SmoothStreamingMedia", StringComparison.OrdinalIgnoreCase))
                throw new PlaybackException(PlaybackError.UnsupportedAdaptive);

            if (bytes.Length >= 12 && (
                    (bytes[..4].SequenceEqual("RIFF"u8) && (bytes.Slice(8, 4).SequenceEqual("WAVE"u8) || bytes.Slice(8, 4).SequenceEqual("AVI "u8))) ||
                    (bytes[..4].SequenceEqual("FORM"u8) && (bytes.Slice(8, 4).SequenceEqual("AIFF"u8) || bytes.Slice(8, 4).SequenceEqual("AIFC"u8))) ||
                    bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) ||
                bytes.StartsWith("fLaC"u8) || bytes.StartsWith("OggS"u8) ||
                bytes.StartsWith("MAC "u8) || bytes.StartsWith("DSD "u8) || bytes.StartsWith("wvpk"u8) ||
                bytes.StartsWith("#!AMR"u8) || bytes.StartsWith("ID3"u8) ||
                bytes.StartsWith((ReadOnlySpan<byte>)[0x1a, 0x45, 0xdf, 0xa3]) ||
                bytes.StartsWith((ReadOnlySpan<byte>)[0x30, 0x26, 0xb2, 0x75]) ||
                bytes.StartsWith((ReadOnlySpan<byte>)[0x0b, 0x77]) ||
                bytes.Length >= 4 && bytes[0] == 0xff &&
                ((bytes[1] & 0xf6) == 0xf0 || (bytes[1] & 0xe0) == 0xe0 && (bytes[1] & 6) != 0))
                return;
            throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
        }

        internal long Seek(long offset, int whence, CancellationToken token)
        {
            lock (_io)
            {
                _owner._lifetimeToken.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                if (!CanSeek || Length is not { } length) throw new PlaybackException(PlaybackError.NotSeekable);
                long target = whence switch
                {
                    0 => offset, 1 => checked(_position + offset), 2 => checked(length + offset),
                    _ => throw new PlaybackException(PlaybackError.InvalidSeek)
                };
                if (target < 0 || target > length || _end >= 0 && target > _end)
                    throw new PlaybackException(PlaybackError.InvalidSeek);
                var body = CurrentBody(out _);
                if (target == _position && body is not null) return target;

                // Small forward seek within existing response slice: drain in-place without TCP/TLS reset.
                if (body is not null && target > _position && target - _position <= MaxForwardSkipBytes &&
                    (_responseEnd is null || target <= _responseEnd.Value))
                {
                    try
                    {
                        while (_position < target)
                        {
                            int toRead = (int)Math.Min(BufferCapacity, target - _position);
                            int got = ReadAsync(_buffer.AsMemory(0, toRead), token).GetAwaiter().GetResult();
                            if (got == 0) break;
                        }
                        if (_position == target) return target;
                    }
                    catch when (!_owner.Cancelled && !token.IsCancellationRequested)
                    {
                        // Fall through to clean Range reopen on skip failure.
                    }
                }

                Abort();
                _position = target;
                if (target != length && target != _end) OpenAsync(initial: false, token).GetAwaiter().GetResult();
                return target;
            }
        }

        internal void Abort()
        {
            HttpResponseMessage? response;
            lock (_streamGate)
            {
                Interlocked.Increment(ref _abortVersion);
                _body = null;
                response = _response;
                _response = null;
                if (_disposed == 0 && _readCts is not null)
                {
                    try { _readCts.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }
            response?.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Abort();
            lock (_io)
            {
                CancellationTokenSource? cts;
                lock (_streamGate)
                {
                    _body = null;
                    cts = _readCts;
                    _readCts = null;
                }
                cts?.Dispose();
            }
            Interlocked.Decrement(ref activeReaders);
        }
    }
}
