using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace MeowFilm.Media;

internal enum AdaptiveResourceKind { Manifest, TransportStream, Mp4Init, Mp4Segment, IdentityKey }

internal sealed class AdaptiveResource(Uri uri, AdaptiveResourceKind kind,
    long? offset = null, long? count = null)
{
    internal readonly Uri OriginalUri = uri;
    internal Uri Uri = uri;
    internal readonly AdaptiveResourceKind Kind = kind;
    internal readonly long? Offset = offset, Count = count;
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal string? ETag;
    internal long? Total;
    internal bool Opened;
    internal int Validated;
    internal byte[]? Manifest;
    internal AdaptiveEncryption? Encryption;
    internal HlsWindow? Window;
    // Live catalogs own these states, not the unbounded history of a transport.
    internal bool Independent;
    internal AdaptiveResource? Representation;
    internal AdaptiveResource? LiveInitialization;
    private int references = 1;
    internal AdaptiveResource Retain() { Interlocked.Increment(ref references); return this; }
    internal void Release()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        if (Manifest is { } bytes) Array.Clear(bytes);
        if (Encryption is { } encryption)
        {
            Array.Clear(encryption.IV); encryption.Key.Clear();
        }
        Representation?.Release();
        LiveInitialization?.Release();
        if (Independent) Gate.Dispose();
    }
}

/// <summary>Adaptive HTTP representations and bounded reads; no URL access policy.</summary>
internal sealed partial class AdaptiveHttpTransport : IDisposable
{
    private readonly NativeHttpMediaOptions _options;
    private readonly Dictionary<string, string> _headers;
    private readonly HttpClient _client;
    private readonly Dictionary<string, AdaptiveResource> _representations = new(StringComparer.Ordinal);
    private readonly CancellationToken _lifetime;
    private readonly OfflineAdaptiveArchive? _offline;
    internal bool IsOffline => _offline is not null;

    internal AdaptiveHttpTransport(NativeHttpMediaOptions options, Dictionary<string, string> headers,
        CancellationToken lifetime)
    {
        _options = options;
        _headers = new(headers, StringComparer.OrdinalIgnoreCase);
        _client = options.CreateClient();
        _lifetime = lifetime;
    }
    internal AdaptiveHttpTransport(OfflineAdaptiveArchive offline, CancellationToken lifetime)
    {
        _offline = offline; _lifetime = lifetime; _options = new(); _headers = [];
        _client = null!;
    }

    internal Uri Resolve(Uri parent, string reference)
    {
        if (_offline is not null) return _offline.Resolve(parent, reference);
        if (string.IsNullOrEmpty(reference) ||
            reference.Any(c => char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(parent, reference, out var uri))
            throw new PlaybackException(PlaybackError.AdaptiveInvalid);
        NativeHttpMediaOptions.ValidateSyntax(uri);
        return uri;
    }

    internal async Task<Response> OpenAsync(AdaptiveResource resource, long start, long? end,
        CancellationToken token)
    {
        if (_offline is not null) return await _offline.OpenAsync(resource, start, end, token, _lifetime).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime);
        linked.CancelAfter(_options.RequestTimeout); // Includes representation ownership and redirect hops.
        var ct = linked.Token;
        bool held = false;
        HttpResponseMessage? response = null;
        AdaptiveResource representation;
        if (resource.Independent) representation = resource.Representation ?? resource;
        else lock (_representations)
        {
            string key = resource.OriginalUri.AbsoluteUri;
            if (!_representations.TryGetValue(key, out representation!))
            {
                if (_representations.Count >= 4096) throw new PlaybackException(PlaybackError.AdaptiveBudget);
                _representations.Add(key, representation = resource);
            }
        }
        try
        {
            await representation.Gate.WaitAsync(ct).ConfigureAwait(false); held = true;
            if (start < 0 || end < start || resource.Count is long count && start >= count)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            long upstreamStart = checked((resource.Offset ?? 0) + start);
            long? upstreamEnd = end.HasValue ? checked((resource.Offset ?? 0) + end.Value) :
                resource.Count.HasValue ? checked(resource.Offset!.Value + resource.Count.Value - 1) : null;
            if (resource.Count is long extent && end.HasValue && end >= extent)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            bool range = resource.Offset.HasValue || start != 0 || end.HasValue;
            var uri = representation.Uri;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int hop = 0; ; hop++)
            {
                NativeHttpMediaOptions.ValidateSyntax(uri);
                if (!visited.Add(uri.AbsoluteUri)) throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri)
                {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                foreach (var (name, value) in _headers) request.Headers.TryAddWithoutValidation(name, value);
                NativeHttpMediaOptions.ApplyUserInfo(request);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (range) request.Headers.Range = new RangeHeaderValue(upstreamStart, upstreamEnd);
                if (representation.Opened)
                {
                    if (representation.ETag is null) throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                    if (range) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(representation.ETag));
                    else request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(representation.ETag));
                }
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (representation.Opened) throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                    if (hop >= _options.MaximumRedirects || response.Headers.Location is not { } location)
                        throw new PlaybackException(PlaybackError.HttpRedirectFailed);
                    uri = Resolve(uri, location.OriginalString);
                    response.Dispose(); response = null;
                    continue;
                }
                if (representation.Opened && status == 412 || range && status == 200)
                    throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                if (status is not (200 or 206))
                    throw new PlaybackException(status switch
                    {
                        401 or 403 => PlaybackError.HttpUnauthorized,
                        404 => PlaybackError.HttpNotFound, 410 => PlaybackError.HttpExpired,
                        416 => PlaybackError.HttpRangeRejected, 429 => PlaybackError.HttpThrottled,
                        >= 500 => PlaybackError.HttpServerFailed, _ => PlaybackError.HttpStatusFailed
                    });
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new PlaybackException(PlaybackError.HttpEncodingUnsupported);
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
                    throw new PlaybackException(PlaybackError.HttpInvalidRange);
                long total, length;
                if (status == 206)
                {
                    var r = response.Content.Headers.ContentRange;
                    if (r is null || r.Unit != "bytes" || !r.HasRange || !r.HasLength ||
                        r.From != upstreamStart || r.To >= r.Length || r.To < r.From ||
                        upstreamEnd.HasValue && r.To != upstreamEnd ||
                        !upstreamEnd.HasValue && r.To != r.Length - 1)
                        throw new PlaybackException(PlaybackError.HttpInvalidRange);
                    total = r.Length!.Value; length = r.To!.Value - r.From!.Value + 1;
                    if (response.Content.Headers.ContentLength is long cl && cl != length)
                        throw new PlaybackException(PlaybackError.HttpInvalidRange);
                }
                else
                {
                    if (response.Content.Headers.ContentRange is not null ||
                        response.Content.Headers.ContentLength is not long known)
                        throw new PlaybackException(PlaybackError.HttpInvalidRange);
                    total = length = known;
                }
                long maximum = resource.Kind switch
                {
                    AdaptiveResourceKind.Manifest or AdaptiveResourceKind.Mp4Init => AdaptiveManifest.MaximumBytes,
                    AdaptiveResourceKind.IdentityKey => 16,
                    _ => 64L * 1024 * 1024
                };
                // A complete PKCS#7 block can extend an otherwise maximum-sized plaintext.
                if (resource.Encryption is not null) maximum += 16;
                if (length <= 0 || length > maximum || total > 1L << 40 ||
                    resource.Count is null && total > maximum)
                    throw new PlaybackException(PlaybackError.AdaptiveBudget);
                string? tag = response.Headers.ETag is { IsWeak: false } etag && etag.Tag != "*" ? etag.ToString() : null;
                if (range && tag is null || representation.Opened && (tag != representation.ETag || total != representation.Total))
                    throw new PlaybackException(PlaybackError.HttpRepresentationChanged);
                representation.ETag = tag; representation.Total = total; representation.Opened = true;
                representation.Uri = resource.Uri = uri;
                var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var owned = new Response(response, stream, length, resource.Count ?? total, start,
                    _options.ReadTimeout, _lifetime, token, representation.Gate);
                response = null; held = false;
                return owned;
            }
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested || _lifetime.IsCancellationRequested)
                throw new OperationCanceledException(token);
            throw new PlaybackException(HttpMediaInput.Classify(error));
        }
        finally
        {
            response?.Dispose();
            if (held) representation.Gate.Release();
        }
    }

    internal async Task<byte[]> ManifestAsync(AdaptiveResource resource, CancellationToken token)
    {
        using var response = await OpenAsync(resource, 0, null, token).ConfigureAwait(false);
        var bytes = new byte[checked((int)response.Length)];
        int position = 0;
        while (position < bytes.Length)
        {
            int read = await response.ReadAsync(bytes.AsMemory(position, Math.Min(65536, bytes.Length - position))).ConfigureAwait(false);
            if (read == 0) throw new PlaybackException(PlaybackError.HttpTruncated);
            position += read;
        }
        await response.VerifyEndAsync().ConfigureAwait(false);
        return bytes;
    }

    public void Dispose()
    {
        _client?.Dispose(); _headers.Clear();
        foreach (var resource in _representations.Values) resource.Gate.Dispose();
        _representations.Clear();
    }

    internal sealed class Response : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _stream;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationTokenRegistration _abort;
        private readonly TimeSpan _timeout;
        private readonly SemaphoreSlim? _gate;
        private long _read;
        private int _disposed;
        internal long Length { get; }
        internal long Total { get; }
        internal long Start { get; }
        internal string? ETag => _response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        internal Response(HttpResponseMessage response, Stream stream, long length, long total, long start,
            TimeSpan timeout, CancellationToken session, CancellationToken caller, SemaphoreSlim? gate = null)
        {
            _response = response; _stream = stream; Length = length; Total = total; Start = start;
            _timeout = timeout; _gate = gate;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(session, caller);
            _abort = _lifetime.Token.Register(response.Dispose);
        }
        internal async Task<int> ReadAsync(Memory<byte> buffer)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(_timeout);
            int size = (int)Math.Min(Math.Min(buffer.Length, 65536), Length - _read);
            if (size == 0) return 0;
            int count = await _stream.ReadAsync(buffer[..size], timeout.Token).ConfigureAwait(false);
            if (count == 0) throw new PlaybackException(PlaybackError.HttpTruncated);
            _read += count;
            return count;
        }
        internal Task VerifyEndAsync()
        {
            if (_read != Length) throw new PlaybackException(PlaybackError.HttpTruncated);
            return Task.CompletedTask; // HttpClient enforces the declared representation extent.
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _response.Dispose(); _abort.Dispose(); _lifetime.Dispose();
            _gate?.Release();
        }
    }
}
