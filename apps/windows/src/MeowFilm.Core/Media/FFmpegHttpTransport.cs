using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: InternalsVisibleTo("MeowFilm.Media")]
[assembly: InternalsVisibleTo("MeowFilm.Audio")]

namespace MeowFilm.Media;

/// <summary>Custom AVIO transport. Uses the input HTTP headers and revocable lifetime; never starts a server.</summary>
internal sealed class FFmpegHttpTransport : IDisposable
{
    internal const int BufferCapacity = 64 * 1024;
    private readonly object _gate = new();
    private readonly NativeHttpMediaOptions _options;
    private readonly Dictionary<string, string> _headers;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<ulong, Reader> _readers = [];
    private ulong _nextHandle;
    private int _cancelled;
    private int _releaseFailed;
    private string? _error;
    internal string? LastErrorCode => Volatile.Read(ref _error);

    internal FFmpegHttpTransport(Uri root, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, CancellationToken token)
    {
        _options = options;
        _headers = NativeHttpMediaPolicy.ValidateRequest(root, headers, allowAdaptive: true);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _client = options.CreateClient();
    }
    internal bool Cancelled => _lifetime.IsCancellationRequested;

    internal async Task<long> CopyCompleteAsync(Uri address, Stream destination, long maximumBytes,
        CancellationToken token, Action<long>? progress, Action<long?>? opened)
    {
        using var reader = new Reader(this, address, 0, -1, progressive: true);
        using var abort = _lifetime.Token.Register(reader.Abort);
        return await reader.CopyCompleteAsync(destination, maximumBytes, token, progress, opened).ConfigureAwait(false);
    }

    internal int Open(IntPtr ignored, IntPtr uri, long offset, long end,
        out ulong handle, out long size, out int seekable, out uint locationSize)
    {
        handle = 0; size = -1; seekable = 0; locationSize = 0;
        Reader? reader = null;
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var address = new Uri(Marshal.PtrToStringUTF8(uri)!, UriKind.Absolute);
            NativeHttpMediaOptions.ValidateSyntax(address);
            reader = new(this, address, offset, end);
            reader.OpenAsync(initial: true).GetAwaiter().GetResult();
            locationSize = checked((uint)Encoding.UTF8.GetByteCount(reader.Address.AbsoluteUri) + 1);
            lock (_gate)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                handle = ++_nextHandle;
                _readers.Add(handle, reader);
                size = reader.Length ?? -1;
                seekable = reader.CanSeek ? 1 : 0;
                reader = null;
            }
            return 0;
        }
        catch (Exception error) { Fail(error); return -1; }
        finally { CloseReader(reader); }
    }
    internal int Location(IntPtr ignored, ulong handle, IntPtr location, uint capacity)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(ReaderFor(handle).Address.AbsoluteUri + "\0");
            // This is the caller's exact allocation, not a URL access budget.
            if (location == IntPtr.Zero || bytes.Length > capacity)
                throw new PlaybackException(PlaybackError.ReadFailed);
            Marshal.Copy(bytes, 0, location, bytes.Length);
            return 0;
        }
        catch (Exception error) { Fail(error); return -1; }
    }
    internal int Read(IntPtr ignored, ulong handle, IntPtr buffer, int length, CancellationToken token = default)
    {
        try { return ReaderFor(handle).Read(buffer, length, token); }
        catch (Exception error) { if (!token.IsCancellationRequested) Fail(error); return -1; }
    }
    internal long Seek(IntPtr ignored, ulong handle, long offset, int whence, CancellationToken token = default)
    {
        try { return ReaderFor(handle).Seek(offset, whence, token); }
        catch (Exception error) { if (!token.IsCancellationRequested) Fail(error); return -1; }
    }
    internal void Close(IntPtr ignored, ulong handle)
    {
        try
        {
            Reader? reader;
            lock (_gate) _readers.Remove(handle, out reader);
            CloseReader(reader);
        }
        catch (Exception error) { Fail(error); }
    }
    private void CloseReader(Reader? reader)
    {
        try { reader?.Dispose(); }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _releaseFailed, 1);
            Fail(error);
        }
    }
    private Reader ReaderFor(ulong handle)
    {
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            return _readers.TryGetValue(handle, out var reader) ? reader :
                throw new PlaybackException(PlaybackError.ReadFailed);
        }
    }
    private CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        deadline.CancelAfter(timeout); return deadline;
    }
    private void Fail(Exception error)
    {
        if (Cancelled) return;
        string code = NativeHttpMediaPolicy.Classify(error);
        if (Interlocked.CompareExchange(ref _error, code, null) is not null) return;
        Cancel();
    }
    internal void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelled, 1) != 0) return;
        try { _lifetime.Cancel(); }
        catch { Interlocked.Exchange(ref _releaseFailed, 1); }
        Reader[] readers;
        lock (_gate) readers = _readers.Values.ToArray();
        foreach (var reader in readers)
        {
            // Never take the per-read lock: native Stop may be joining that read.
            try { reader.Abort(); }
            catch { Interlocked.Exchange(ref _releaseFailed, 1); }
        }
    }
    public void Dispose()
    {
        Cancel();
        Reader[] readers;
        lock (_gate) { readers = _readers.Values.ToArray(); _readers.Clear(); }
        foreach (var reader in readers)
        {
            try { reader.Dispose(); }
            catch { Interlocked.Exchange(ref _releaseFailed, 1); }
        }
        try { _client.Dispose(); }
        catch { Interlocked.Exchange(ref _releaseFailed, 1); }
        _headers.Clear(); _lifetime.Dispose();
        if (Volatile.Read(ref _releaseFailed) != 0)
            throw new PlaybackException(PlaybackError.ReleaseFailed);
    }

    private sealed class Reader : IDisposable
    {
        private readonly object _io = new();
        private readonly FFmpegHttpTransport _owner;
        private readonly long _end;
        private readonly bool _progressive;
        private readonly byte[] _buffer = new byte[BufferCapacity];
        private HttpResponseMessage? _response;
        private Stream? _body;
        private long _position;
        private long? _responseEnd;
        private int _disposed;
        internal Uri Address { get; private set; }
        internal long? Length { get; private set; }
        internal bool CanSeek { get; private set; }

        internal Reader(FFmpegHttpTransport owner, Uri address, long start, long end, bool progressive = false)
        {
            if (start < 0 || end < -1 || end >= 0 && end <= start)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            _owner = owner; Address = address; _position = start; _end = end; _progressive = progressive;
        }
        internal async Task OpenAsync(bool initial, CancellationToken operation = default)
        {
            using var deadline = _progressive ? null : _owner.Deadline(_owner._options.RequestTimeout, operation);
            var token = deadline?.Token ?? operation;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int hop = 0; ; hop++)
            {
                token.ThrowIfCancellationRequested();
                NativeHttpMediaOptions.ValidateSyntax(Address);
                if (_progressive) NativeHttpMediaPolicy.RejectAdaptivePath(Address);
                if (!visited.Add(Address.AbsoluteUri)) throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                using var request = new HttpRequestMessage(HttpMethod.Get, Address);
                foreach (var (name, value) in _owner._headers)
                    request.Headers.TryAddWithoutValidation(name, value);
                NativeHttpMediaOptions.ApplyUserInfo(request);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                request.Headers.Range = new RangeHeaderValue(_position, _end >= 0 ? _end - 1 : null);
                var response = await _owner._client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    response.Dispose();
                    token.ThrowIfCancellationRequested();
                }
                Interlocked.Exchange(ref _response, response)?.Dispose();
                try
                {
                    int status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        if (hop >= _owner._options.MaximumRedirects ||
                            response.Headers.Location is not { } location)
                            throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                        var next = location.IsAbsoluteUri ? location : new Uri(Address, location);
                        NativeHttpMediaOptions.ValidateSyntax(next);
                        Address = next; Abort(); continue;
                    }
                    if (!initial && status is 200 or 412)
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
                        if (range is null || range.Unit != "bytes" || !range.HasRange || !range.HasLength ||
                            range.From != _position || range.To < _position || range.To >= range.Length ||
                            _end >= 0 && range.To != _end - 1 || range.Length <= 0 ||
                            response.Content.Headers.ContentLength is { } count && count != range.To - range.From + 1)
                            throw new PlaybackException(PlaybackError.HttpInvalidRange);
                        total = range.Length;
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
                        CanSeek = status == 206 && total is not null;
                    }
                    var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref _body, stream);
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
                Marshal.Copy(_buffer, 0, output, read);
                return read;
            }
        }
        private async Task<int> ReadAsync(Memory<byte> destination, CancellationToken token, bool probe = false)
        {
            _owner._lifetime.Token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) throw new PlaybackException(PlaybackError.ReadFailed);
            if (_end >= 0 && _position == _end || Length == _position) return 0;
            using var transferDeadline = _progressive && !probe ? _owner.Deadline(_owner._options.ReadTimeout, token) : null;
            var readToken = transferDeadline?.Token ?? token;
            var body = Volatile.Read(ref _body);
            if (body is null || _responseEnd is { } end && _position == end)
            {
                if (!CanSeek) throw new PlaybackException(PlaybackError.HttpTruncated);
                // AVIO can seek within its own buffer without a seek callback.
                // Resume at the last byte actually delivered to that buffer.
                Abort(); await OpenAsync(initial: false, readToken).ConfigureAwait(false);
                body = Volatile.Read(ref _body);
                if (body is null)
                {
                    _owner._lifetime.Token.ThrowIfCancellationRequested();
                    token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException();
                }
            }
            int count = destination.Length;
            if (_responseEnd is { } limit) count = (int)Math.Min(count, limit - _position);
            using var deadline = _progressive ? null : _owner.Deadline(_owner._options.ReadTimeout, token);
            readToken = deadline?.Token ?? readToken;
            int read;
            try { read = await body.ReadAsync(destination[..count], readToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Abort();
                throw;
            }
            if (read == 0 && _responseEnd is { } expected && _position != expected)
                throw new PlaybackException(PlaybackError.HttpTruncated);
            _position += read;
            return read;
        }
        internal async Task<long> CopyCompleteAsync(Stream destination, long maximumBytes, CancellationToken token,
            Action<long>? progress, Action<long?>? opened)
        {
            int prefix = 0;
            using (var deadline = _owner.Deadline(_owner._options.RequestTimeout, token))
            {
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
            if (bytes.Length >= 12 && (bytes[..4].SequenceEqual("RIFF"u8) &&
                (bytes.Slice(8, 4).SequenceEqual("WAVE"u8) || bytes.Slice(8, 4).SequenceEqual("AVI "u8)) ||
                bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) ||
                bytes.StartsWith("fLaC"u8) || bytes.StartsWith("OggS"u8) ||
                bytes.StartsWith((ReadOnlySpan<byte>)[0x1a, 0x45, 0xdf, 0xa3]) || bytes.StartsWith("ID3"u8) ||
                bytes.Length >= 4 && bytes[0] == 0xff &&
                ((bytes[1] & 0xf6) == 0xf0 || (bytes[1] & 0xe0) == 0xe0 && (bytes[1] & 6) != 0))
                return;
            throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
        }
        internal long Seek(long offset, int whence, CancellationToken token)
        {
            lock (_io)
            {
                _owner._lifetime.Token.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                if (!CanSeek || Length is not { } length) throw new PlaybackException(PlaybackError.NotSeekable);
                long target = whence switch
                {
                    0 => offset, 1 => checked(_position + offset), 2 => checked(length + offset),
                    _ => throw new PlaybackException(PlaybackError.InvalidSeek)
                };
                if (target < 0 || target > length || _end >= 0 && target > _end)
                    throw new PlaybackException(PlaybackError.InvalidSeek);
                if (target == _position && Volatile.Read(ref _body) is not null) return target;
                Abort(); _position = target;
                if (target != length && target != _end) OpenAsync(initial: false, token).GetAwaiter().GetResult();
                return target;
            }
        }
        internal void Abort()
        {
            Volatile.Write(ref _body, null); // Read or seek must reopen at the retained byte position.
            Interlocked.Exchange(ref _response, null)?.Dispose();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Abort();
            lock (_io) { Volatile.Write(ref _body, null); }
        }
    }
}
