using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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
    internal event Action<string>? Failed;

    internal FFmpegHttpTransport(Uri root, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, CancellationToken token)
    {
        _options = options;
        _headers = HttpMediaInput.ValidateRequest(root, headers, allowAdaptive: true);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _client = options.CreateClient();
    }
    internal bool Cancelled => _lifetime.IsCancellationRequested;

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
    internal int Read(IntPtr ignored, ulong handle, IntPtr buffer, int length)
    {
        try { return ReaderFor(handle).Read(buffer, length); }
        catch (Exception error) { Fail(error); return -1; }
    }
    internal long Seek(IntPtr ignored, ulong handle, long offset, int whence)
    {
        try { return ReaderFor(handle).Seek(offset, whence); }
        catch (Exception error) { Fail(error); return -1; }
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
    private CancellationTokenSource Deadline(TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(timeout); return deadline;
    }
    private void Fail(Exception error)
    {
        if (Cancelled) return;
        string code = HttpMediaInput.Classify(error);
        if (Interlocked.CompareExchange(ref _error, code, null) is not null) return;
        Cancel();
        try { Failed?.Invoke(code); }
        catch { Interlocked.Exchange(ref _error, PlaybackError.CallbackFailed); }
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
        private readonly byte[] _buffer = new byte[BufferCapacity];
        private HttpResponseMessage? _response;
        private Stream? _body;
        private string? _etag;
        private DateTimeOffset? _modified;
        private long _position;
        private long? _responseEnd;
        private int _disposed;
        internal Uri Address { get; private set; }
        internal long? Length { get; private set; }
        internal bool CanSeek { get; private set; }

        internal Reader(FFmpegHttpTransport owner, Uri address, long start, long end)
        {
            if (start < 0 || end < -1 || end >= 0 && end <= start)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            _owner = owner; Address = address; _position = start; _end = end;
        }
        internal async Task OpenAsync(bool initial)
        {
            using var deadline = _owner.Deadline(_owner._options.RequestTimeout);
            var token = deadline.Token;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int hop = 0; ; hop++)
            {
                token.ThrowIfCancellationRequested();
                NativeHttpMediaOptions.ValidateSyntax(Address);
                if (!visited.Add(Address.AbsoluteUri)) throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                using var request = new HttpRequestMessage(HttpMethod.Get, Address)
                {
                    Version = new Version(1, 1), VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                foreach (var (name, value) in _owner._headers) request.Headers.TryAddWithoutValidation(name, value);
                NativeHttpMediaOptions.ApplyUserInfo(request);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                request.Headers.Range = new RangeHeaderValue(_position, _end >= 0 ? _end - 1 : null);
                if (!initial && _etag is not null)
                    request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(_etag));
                else if (!initial && _modified is not null)
                    request.Headers.IfRange = new RangeConditionHeaderValue(_modified.Value);
                var response = await _owner._client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _response, response)?.Dispose();
                token.ThrowIfCancellationRequested();
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (!initial || hop >= _owner._options.MaximumRedirects ||
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
                var tag = response.Headers.ETag is { IsWeak: false } etag && etag.Tag != "*" ? etag.ToString() : null;
                var modified = response.Content.Headers.LastModified;
                if (!initial && (Length != total || _etag is not null && _etag != tag ||
                    _modified is not null && _modified != modified))
                    throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                if (initial)
                {
                    Length = total; _etag = tag; _modified = modified;
                    // A real 206 proves range support; a bare Accept-Ranges header does not.
                    // Validators, if provided, must remain identical across seeks.
                    CanSeek = status == 206 && total is not null;
                }
                _body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return;
            }
        }
        internal int Read(IntPtr output, int length)
        {
            lock (_io)
            {
                _owner._lifetime.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0 || output == IntPtr.Zero || length <= 0)
                    throw new PlaybackException(PlaybackError.ReadFailed);
                if (_end >= 0 && _position == _end || Length == _position) return 0;
                if (_responseEnd is { } end && _position == end)
                {
                    if (!CanSeek) throw new PlaybackException(PlaybackError.HttpTruncated);
                    Abort(); OpenAsync(initial: false).GetAwaiter().GetResult();
                }
                int count = Math.Min(length, BufferCapacity);
                if (_responseEnd is { } limit) count = (int)Math.Min(count, limit - _position);
                using var deadline = _owner.Deadline(_owner._options.ReadTimeout);
                int read = _body!.ReadAsync(_buffer.AsMemory(0, count), deadline.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0 && _responseEnd is { } expected && _position != expected)
                    throw new PlaybackException(PlaybackError.HttpTruncated);
                Marshal.Copy(_buffer, 0, output, read);
                CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, read));
                _position += read;
                return read;
            }
        }
        internal long Seek(long offset, int whence)
        {
            lock (_io)
            {
                _owner._lifetime.Token.ThrowIfCancellationRequested();
                if (!CanSeek || Length is not { } length) throw new PlaybackException(PlaybackError.NotSeekable);
                long target = whence switch
                {
                    0 => offset, 1 => checked(_position + offset), 2 => checked(length + offset),
                    _ => throw new PlaybackException(PlaybackError.InvalidSeek)
                };
                if (target < 0 || target > length || _end >= 0 && target > _end)
                    throw new PlaybackException(PlaybackError.InvalidSeek);
                if (target == _position) return target;
                Abort(); _position = target;
                if (target != length && target != _end) OpenAsync(initial: false).GetAwaiter().GetResult();
                return target;
            }
        }
        internal void Abort() => Interlocked.Exchange(ref _response, null)?.Dispose();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Abort();
            lock (_io) { _body = null; CryptographicOperations.ZeroMemory(_buffer); }
        }
    }
}
