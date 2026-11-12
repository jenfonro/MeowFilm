using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

/// <summary>
/// One progressive representation, read directly by libVLC. Own until AFTER Media/MediaPlayer
/// release. Cancel BEFORE native Stop: Stop may join a thread blocked in Read/Seek.
/// No producer queue, file cache, cookies, JavaScript or local forwarding service.
/// </summary>
public sealed class HttpMediaInput : MediaInput
{
    public const int BufferCapacity = 64 * 1024;
    private const int ProbeSize = 512;
    private readonly object _io = new();
    private readonly NativeHttpMediaOptions _options;
    private readonly Dictionary<string, string> _headers;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _abort;
    private readonly byte[] _buffer = new byte[BufferCapacity];
    private HttpResponseMessage? _response;
    private Stream? _stream;
    private Uri _uri;
    private string? _etag, _error;
    private long? _length, _responseEnd;
    private long _position, _wirePosition;
    private int _bufferOffset, _bufferCount, _opened, _disposed;
    private bool _allowAdaptive;
    internal string DemuxName { get; private set; } = "";
    internal Uri EffectiveUri => _uri;
    public string? LastErrorCode => Volatile.Read(ref _error);
    public long Position => Interlocked.Read(ref _position);
    public long? Length => _length;
    /// <summary>Bounded complete progressive transfer for a native cache consumer, not a download job.
    /// Call on a fresh input only; the same range/redirect/header/EOF policies as playback apply.</summary>
    public async Task<long> CopyCompleteAsync(Stream destination, long maximumBytes, CancellationToken token = default,
        Action<long>? progress = null)
    {
        if (maximumBytes <= 0 || Interlocked.Exchange(ref _opened, 1) != 0)
            throw new PlaybackException(PlaybackError.InvalidRequest);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        if (_length > maximumBytes) throw new PlaybackException(PlaybackError.InvalidRequest);
        long written = 0;
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_bufferCount == 0)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                deadline.CancelAfter(_options.ReadTimeout);
                _bufferCount = await ReadWireAsync(_buffer, deadline.Token).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferCount == 0) break;
            }
            if (written + _bufferCount > maximumBytes) throw new PlaybackException(PlaybackError.InvalidRequest);
            await destination.WriteAsync(_buffer.AsMemory(_bufferOffset, _bufferCount), linked.Token).ConfigureAwait(false);
            written += _bufferCount; _bufferCount = 0;
            progress?.Invoke(written);
        }
        if (written == 0 || _length is { } expected && written != expected)
            throw new PlaybackException(PlaybackError.HttpTruncated);
        return written;
    }
    internal event Action<string>? Failed;

    private HttpMediaInput(Uri uri, Dictionary<string, string> headers,
        NativeHttpMediaOptions options, HttpClient client, CancellationToken token)
    {
        _uri = uri;
        _headers = headers;
        _options = options;
        _client = client;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _abort = _lifetime.Token.Register(() => Volatile.Read(ref _response)?.Dispose());
        CanSeek = false;
    }

    public static async Task<HttpMediaInput> OpenAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions? options = null, CancellationToken token = default)
        => await OpenInternalAsync(uri, headers, options, token, false).ConfigureAwait(false);

    internal static Task<HttpMediaInput> OpenPlaybackAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
        NativeHttpMediaOptions options, CancellationToken token) =>
        OpenInternalAsync(uri, headers, options, token, true);

    private static async Task<HttpMediaInput> OpenInternalAsync(Uri uri,
        IReadOnlyDictionary<string, string> headers, NativeHttpMediaOptions? options,
        CancellationToken token, bool allowAdaptive)
    {
        options ??= new NativeHttpMediaOptions();
        var copy = ValidateRequest(uri, headers, allowAdaptive);
        token.ThrowIfCancellationRequested();
        var input = new HttpMediaInput(uri, copy, options, options.CreateClient(), token);
        input._allowAdaptive = allowAdaptive;
        try
        {
            using var deadline = input.Deadline(options.RequestTimeout);
            await input.RequestAsync(0, initial: true, deadline.Token).ConfigureAwait(false);
            while (input._bufferCount < ProbeSize)
            {
                int read = await input.ReadWireAsync(
                    input._buffer.AsMemory(input._bufferCount, ProbeSize - input._bufferCount),
                    deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                input._bufferCount += read;
            }
            var prefix = input._buffer.AsSpan(0, input._bufferCount);
            bool manifest = AdaptiveManifest.IsManifest(prefix);
            if (allowAdaptive && manifest) input.DemuxName = "adaptive";
            else
            {
                RejectAdaptivePath(input._uri);
                CheckContentType(input._response!, false);
                input.DemuxName = Probe(prefix);
            }
            return input;
        }
        catch (Exception error)
        {
            bool cancelled = input._lifetime.IsCancellationRequested;
            var code = Classify(error);
            input.Dispose();
            if (cancelled) throw new OperationCanceledException(token);
            throw new PlaybackException(code);
        }
    }

    internal static Dictionary<string, string> ValidateRequest(Uri uri,
        IReadOnlyDictionary<string, string> headers, bool allowAdaptive = false)
    {
        NativeHttpMediaOptions.ValidateSyntax(uri);
        if (!allowAdaptive) RejectAdaptivePath(uri);
        if (headers is null || headers.Count > 32) throw new PlaybackException(PlaybackError.InvalidHeaders);
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int size = 0;
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || value is null || value.Length > 8192 ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
                value.Any(c => c < 0x20 || c > 0x7e) ||
                !copy.TryAdd(name, value) || (size += name.Length + value.Length) > 16384)
                throw new PlaybackException(PlaybackError.InvalidHeaders);
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("If-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) ||
                new[] { "Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new PlaybackException(PlaybackError.UnsupportedHeaders);
        }
        return copy;
    }

    private static void RejectAdaptivePath(Uri uri)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (new[] { ".m3u8", ".m3u", ".mpd", ".ism", ".isml" }
            .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException(PlaybackError.UnsupportedAdaptive);
    }

    private static void CheckContentType(HttpResponseMessage response, bool allowAdaptive = false)
    {
        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (!allowAdaptive && (type.Contains("mpegurl", StringComparison.Ordinal) || type.Contains("dash", StringComparison.Ordinal) ||
            type.Contains("vnd.ms-sstr", StringComparison.Ordinal)))
            throw new PlaybackException(PlaybackError.UnsupportedAdaptive);
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException(PlaybackError.HttpEncodingUnsupported);
        if (type.StartsWith("multipart/", StringComparison.Ordinal))
            throw new PlaybackException(PlaybackError.HttpInvalidRange);
    }

    private async Task RequestAsync(long offset, bool initial, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int redirects = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            NativeHttpMediaOptions.ValidateSyntax(_uri);
            if (!_allowAdaptive) RejectAdaptivePath(_uri);
            if (!visited.Add(_uri.AbsoluteUri)) throw new PlaybackException(PlaybackError.HttpRedirectFailed);
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            foreach (var (name, value) in _headers)
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    throw new PlaybackException(PlaybackError.UnsupportedHeaders);
            NativeHttpMediaOptions.ApplyUserInfo(request);
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (!initial) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(_etag!));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // Publish before checking cancellation so Cancel can abort headers/body without the IO lock.
            Interlocked.Exchange(ref _response, response)?.Dispose();
            token.ThrowIfCancellationRequested();
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location is null || redirects++ >= _options.MaximumRedirects)
                    throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                var next = location.IsAbsoluteUri ? location : new Uri(_uri, location);
                NativeHttpMediaOptions.ValidateSyntax(next);
                // Range identity is pinned to the first final representation. A fresh signed
                // resource/redirect must be re-resolved by the capability owner, never silently spliced.
                if (!initial) throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                _uri = next;
                CloseResponse();
                continue;
            }
            if (!initial && status is 200 or 412)
                throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
            if (status is not (200 or 206))
                throw new PlaybackException(status switch
                {
                    401 or 403 => PlaybackError.HttpUnauthorized,
                    404 => PlaybackError.HttpNotFound,
                    410 => PlaybackError.HttpExpired,
                    416 => PlaybackError.HttpRangeRejected,
                    429 => PlaybackError.HttpThrottled,
                    >= 500 => PlaybackError.HttpServerFailed,
                    _ => PlaybackError.HttpStatusFailed
                });
            CheckContentType(response, _allowAdaptive);
            var tag = response.Headers.ETag;
            string? strongTag = tag is { IsWeak: false } && tag.Tag != "*" ? tag.ToString() : null;
            if (!initial && !string.Equals(_etag, strongTag, StringComparison.Ordinal))
                throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
            if (status == 206)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is null || range.Unit != "bytes" || !range.HasRange || !range.HasLength ||
                    range.From != offset || range.To < offset || range.To >= range.Length ||
                    range.Length <= 0 || response.Content.Headers.ContentLength is long count &&
                    count != range.To - range.From + 1)
                    throw new PlaybackException(PlaybackError.HttpInvalidRange);
                if (!initial && _length != range.Length)
                    throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                _length = range.Length;
                _responseEnd = range.To!.Value + 1;
                if (initial) CanSeek = strongTag is not null;
                if (_responseEnd != _length && !CanSeek)
                    throw new PlaybackException(PlaybackError.HttpInvalidRange);
            }
            else
            {
                if (response.Content.Headers.ContentRange is not null)
                    throw new PlaybackException(PlaybackError.HttpInvalidRange);
                _length = response.Content.Headers.ContentLength;
                _responseEnd = _length;
                CanSeek = false; // Accept-Ranges alone is not evidence that this resource supports ranges.
            }
            if (initial) _etag = strongTag;
            _wirePosition = offset;
            _stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return;
        }
    }

    private async Task<int> ReadWireAsync(Memory<byte> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_responseEnd is long end && _wirePosition == end)
        {
            if (_length == end) return 0;
            // Some servers cap each range. Continue only with the same strong representation.
            CloseResponse();
            await RequestAsync(_wirePosition, initial: false, token).ConfigureAwait(false);
        }
        int count = destination.Length;
        if (_responseEnd is long limit) count = (int)Math.Min(count, limit - _wirePosition);
        int read = await _stream!.ReadAsync(destination[..count], token).ConfigureAwait(false);
        if (read == 0 && _responseEnd is long expected && _wirePosition != expected)
            throw new PlaybackException(PlaybackError.HttpTruncated);
        _wirePosition += read;
        return read;
    }

    public override bool Open(out ulong size)
    {
        size = _length is long length ? (ulong)length : ulong.MaxValue;
        return Volatile.Read(ref _disposed) == 0 && !_lifetime.IsCancellationRequested &&
            LastErrorCode is null && Interlocked.Exchange(ref _opened, 1) == 0;
    }

    public override int Read(IntPtr buf, uint len)
    {
        lock (_io)
        {
            if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested || LastErrorCode is not null) return -1;
            if (len == 0) return 0;
            try
            {
                if (buf == IntPtr.Zero) throw new PlaybackException(PlaybackError.InvalidRequest);
                int count = (int)Math.Min(len, BufferCapacity);
                if (_bufferCount == 0)
                {
                    if (_length == _position) return 0; // Includes seek-to-EOF without an invalid Range request.
                    using var deadline = Deadline(_options.ReadTimeout);
                    _bufferCount = ReadWireAsync(_buffer.AsMemory(0, count), deadline.Token).GetAwaiter().GetResult();
                    _bufferOffset = 0;
                }
                int take = Math.Min(count, _bufferCount);
                Marshal.Copy(_buffer, _bufferOffset, buf, take);
                _bufferOffset += take;
                _bufferCount -= take;
                _position += take;
                return take;
            }
            catch (Exception error)
            {
                if (!_lifetime.IsCancellationRequested) Fail(Classify(error));
                return -1; // No exception or credential-bearing native diagnostic crosses the callback ABI.
            }
        }
    }

    public override bool Seek(ulong offset)
    {
        lock (_io)
        {
            if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested || LastErrorCode is not null) return false;
            if (offset == (ulong)_position) return true;
            if (!CanSeek || _length is not long length || offset > (ulong)length) return false;
            try
            {
                CloseResponse();
                _bufferCount = _bufferOffset = 0;
                if (offset < (ulong)length)
                {
                    using var deadline = Deadline(_options.RequestTimeout);
                    RequestAsync((long)offset, initial: false, deadline.Token).GetAwaiter().GetResult();
                }
                _position = (long)offset;
                return true;
            }
            catch (Exception error)
            {
                if (!_lifetime.IsCancellationRequested) Fail(Classify(error));
                return false;
            }
        }
    }

    /// <summary>Thread-safe, never takes the IO lock; interrupts active DNS/connect/headers/body/seek.</summary>
    public void Cancel()
    {
        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public override void Close() => Cancel();

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        lock (_io)
        {
            CloseResponse();
            _client.Dispose();
            _abort.Dispose();
            _lifetime.Dispose();
            _headers.Clear();
            Array.Clear(_buffer);
            base.Dispose(disposing);
        }
    }

    private CancellationTokenSource Deadline(TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        source.CancelAfter(timeout);
        return source;
    }

    private void CloseResponse()
    {
        _stream = null;
        Interlocked.Exchange(ref _response, null)?.Dispose();
    }

    private void Fail(string code)
    {
        if (Interlocked.CompareExchange(ref _error, code, null) is not null) return;
        Cancel();
        Failed?.Invoke(code); // Internal subscriber only posts to the owner, never calls libVLC here.
    }

    internal static string Classify(Exception error)
    {
        // Transport exceptions can be wrapped by HttpClient. Only extract our fixed code;
        // no URI/response/header/exception body or inner exception is published.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PlaybackException known) return known.Code;
        return error switch
        {
            OperationCanceledException => PlaybackError.HttpDeadline,
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => PlaybackError.HttpTlsFailed,
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => PlaybackError.HttpTruncated,
            HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded } => PlaybackError.HttpTruncated,
            _ => PlaybackError.HttpNetworkFailed
        };
    }

    // Only a bounded manifest is materialized. Progressive bytes keep the existing callbacks.
    internal async Task<byte[]> ReadManifestAsync(CancellationToken token)
    {
        if (DemuxName != "adaptive") throw new PlaybackException(PlaybackError.AdaptiveInvalid);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(_options.RequestTimeout);
        using var data = new MemoryStream();
        data.Write(_buffer, 0, _bufferCount);
        while (true)
        {
            if (data.Length > AdaptiveManifest.MaximumBytes)
                throw new PlaybackException(PlaybackError.AdaptiveBudget);
            int read = await ReadWireAsync(_buffer.AsMemory(0,
                (int)Math.Min(_buffer.Length, AdaptiveManifest.MaximumBytes + 1 - data.Length)),
                deadline.Token).ConfigureAwait(false);
            if (read == 0) return data.ToArray();
            data.Write(_buffer, 0, read);
        }
    }

    private static string Probe(ReadOnlySpan<byte> bytes)
    {
        // Permit only a binary progressive demuxer. Never hand an unclassified text/manifest
        // stream to VLC's adaptive/playlist auto detection (which would bypass this transport).
        string text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<MPD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<SmoothStreamingMedia", StringComparison.OrdinalIgnoreCase))
            throw new PlaybackException(PlaybackError.UnsupportedAdaptive);
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8))
        {
            if (bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "wav";
            if (bytes.Slice(8, 4).SequenceEqual("AVI "u8)) return "avi";
        }
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return "mp4";
        if (bytes.StartsWith("fLaC"u8)) return "flac";
        if (bytes.StartsWith("OggS"u8)) return "ogg";
        if (bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return "mkv";
        if (bytes.StartsWith("ID3"u8)) return "mpga";
        if (bytes.Length >= 4 && bytes[0] == 0xff)
        {
            if ((bytes[1] & 0xf6) == 0xf0) return "mp4a";
            if ((bytes[1] & 0xe0) == 0xe0 && (bytes[1] & 6) != 0) return "mpga";
        }
        throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
    }
}
