using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MeowFilm.Media;

/// <summary>One finite adaptive graph and one loopback listener, owned by the existing native session.</summary>
internal sealed class AdaptiveMediaSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _abort;
    private readonly TcpListener _listener;
    private readonly AdaptiveHttpTransport _transport;
    private readonly AdaptiveManifest _manifest;
    private AdaptiveLiveSession? _live;
    private readonly NativeHttpMediaOptions _options;
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly HashSet<Task> _handlers = [];
    private Task _accept = Task.CompletedTask;
    private Task? _dispose;
    private string? _error;
    private int _nextClient;
    internal Uri Root { get; private set; } = null!;
    internal string Capability { get; private set; }
    internal int Port { get; }
    internal string? LastErrorCode => Volatile.Read(ref _error);
    internal event Action<string>? Failed;
    internal AdaptiveLiveSession? Live => _live;
    internal CancellationToken Lifetime => _lifetime.Token;
    internal void LiveRecoveryFailed() => Fail(new PlaybackException(PlaybackError.HttpExpired));

    private AdaptiveMediaSession(Uri origin, Dictionary<string, string> headers, NativeHttpMediaOptions options,
        CancellationToken token, OfflineAdaptiveArchive? offline = null)
    {
        _options = options;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Capability = "MeowAdaptive/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _transport = offline is null ? new(options, headers, origin, _lifetime.Token) : new(offline, _lifetime.Token);
        _manifest = new(_transport, new Uri($"http://127.0.0.1:{Port}/"), allowLive: offline is null);
        _abort = _lifetime.Token.Register(() =>
        {
            _listener.Stop();
            foreach (var client in _clients.Values) client.Dispose();
        });
    }
    internal static async Task<AdaptiveMediaSession> OpenOfflineAsync(Stream content, long length, CancellationToken token)
    {
        var archive = new OfflineAdaptiveArchive(content, length);
        await archive.LoadAsync(token).ConfigureAwait(false);
        var session = new AdaptiveMediaSession(NativeAdaptiveArchive.ArchiveOrigin, [], new(), token, archive);
        try
        {
            // Recompile even our own index. Arbitrary caller-provided archives cannot bypass the
            // manifest profile/URI checks; the transport has no upstream network implementation.
            session.Root = await session._manifest.BuildAsync(archive.RootBytes, archive.Root, false, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            session._accept = session.AcceptAsync(); return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal static async Task<AdaptiveMediaSession> OpenAsync(byte[] root, Uri resolved, bool credentials,
        Uri origin, Dictionary<string, string> headers, NativeHttpMediaOptions options, CancellationToken token)
    {
        var session = new AdaptiveMediaSession(origin, headers, options, token);
        try
        {
            using var graphDeadline = CancellationTokenSource.CreateLinkedTokenSource(token, session._lifetime.Token);
            graphDeadline.CancelAfter(options.RequestTimeout);
            session.Root = await session._manifest.BuildAsync(root, resolved, credentials, graphDeadline.Token).ConfigureAwait(false);
            session._live = AdaptiveLiveSession.Create(session._manifest, session._transport,
                new Uri($"http://127.0.0.1:{session.Port}/"), options.RequestTimeout, session._lifetime.Token);
            session._lifetime.Token.ThrowIfCancellationRequested();
            session._accept = session.AcceptAsync();
            return session;
        }
        catch (Exception error)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new PlaybackException(HttpMediaInput.Classify(error));
        }
        finally { Array.Clear(root); }
    }
    internal void Cancel()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void Fail(Exception error)
    {
        if (_lifetime.IsCancellationRequested) return;
        string code = HttpMediaInput.Classify(error);
        if (Interlocked.CompareExchange(ref _error, code, null) is not null) return;
        Cancel();
        Failed?.Invoke(code); // Posts to dispatcher; never runs native Stop on a service thread.
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                int id = Interlocked.Increment(ref _nextClient);
                _clients[id] = client;
                var handler = HandleAsync(id, client);
                lock (_handlers) _handlers.Add(handler);
                _ = handler.ContinueWith(task =>
                {
                    _ = task.Exception;
                    lock (_handlers) _handlers.Remove(task);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private async Task HandleAsync(int id, TcpClient client)
    {
        bool authenticated = false;
        AdaptiveResource? resource = null;
        byte[]? liveInitialization = null;
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task disconnect = Task.CompletedTask;
        try
        {
            // TcpClient owns this stream. Do not dispose it while unwinding into catch:
            // our own close would wake WatchDisconnectAsync and masquerade as a native seek.
            var stream = client.GetStream();
            var request = await RequestAsync(stream).ConfigureAwait(false);
            if (request is null)
            {
                await StatusAsync(stream, 403, 0).ConfigureAwait(false); return;
            }
            authenticated = true;
            disconnect = WatchDisconnectAsync(stream, requestLifetime);
            resource = _live is not null
                ? await _live.AcquireAsync(request.Value.Path, requestLifetime.Token).ConfigureAwait(false)
                : _manifest.Resources.TryGetValue(request.Value.Path, out var fixedResource) ? fixedResource.Retain() : null;
            if (resource is null)
            {
                if (_live is not null)
                    await _live.RecoverExpiredAsync(request.Value.Path, null, requestLifetime.Token).ConfigureAwait(false);
                await StatusAsync(stream, 404, 0).ConfigureAwait(false); return;
            }
            var (start, end) = ParseRange(request.Value.Range);
            if (resource.Manifest is { } manifest)
            {
                if (start >= manifest.Length || end >= manifest.Length) throw new PlaybackException(PlaybackError.HttpInvalidRange);
                int count = (int)((end ?? manifest.Length - 1) - start + 1);
                string mime = manifest[0] == '#' ? "application/vnd.apple.mpegurl" : "application/dash+xml";
                await StatusAsync(stream, request.Value.Range is null ? 200 : 206, count, mime,
                    request.Value.Range is null ? null : $"bytes {start}-{start + count - 1}/{manifest.Length}").ConfigureAwait(false);
                await WriteAsync(stream, manifest.AsMemory((int)start, count)).ConfigureAwait(false);
                return;
            }
            if (start != 0 && Volatile.Read(ref resource.Validated) == 0)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            if (resource.LiveInitialization is { } initialization)
            {
                // VLC 3.0.24 restarts the fMP4 demuxer at a discontinuity but does not
                // resend EXT-X-MAP. Supply the same validated init before that fragment.
                if (start != 0 || end.HasValue) throw new PlaybackException(PlaybackError.HttpInvalidRange);
                using var header = await _transport.OpenPayloadAsync(initialization, 0, null, requestLifetime.Token).ConfigureAwait(false);
                liveInitialization = new byte[checked((int)header.Length)];
                await ReadExactlyAsync(header, liveInitialization).ConfigureAwait(false);
                ValidateInit(liveInitialization);
                await header.VerifyEndAsync().ConfigureAwait(false);
            }
            AdaptiveHttpTransport.Response opened;
            try { opened = await _transport.OpenPayloadAsync(resource, start, end, requestLifetime.Token).ConfigureAwait(false); }
            catch (PlaybackException error) when (_live is not null && start == 0 && end is null &&
                error.Code is PlaybackError.HttpNotFound or PlaybackError.HttpExpired)
            {
                if (!await _live.RecoverExpiredAsync(request.Value.Path, resource, requestLifetime.Token).ConfigureAwait(false)) throw;
                await StatusAsync(stream, 404, 0).ConfigureAwait(false); return;
            }
            using var response = opened;
            byte[] buffer = new byte[65536];
            if (resource.Kind == AdaptiveResourceKind.Mp4Init)
            {
                if (start != 0 || end.HasValue) throw new PlaybackException(PlaybackError.HttpInvalidRange);
                var init = new byte[checked((int)response.Length)];
                try
                {
                    await ReadExactlyAsync(response, init).ConfigureAwait(false);
                    ValidateInit(init);
                    Volatile.Write(ref resource.Validated, 1);
                    await StatusAsync(stream, request.Value.Range is null ? 200 : 206, init.Length, "video/mp4",
                        request.Value.Range is null ? null : $"bytes 0-{init.Length - 1}/{init.Length}").ConfigureAwait(false);
                    await WriteAsync(stream, init).ConfigureAwait(false);
                }
                finally { Array.Clear(init); }
            }
            else
            {
                // Offset ranges may skip the signature only after this exact representation was opened.
                // Upstream strong validators are checked on every ranged/repeated request.
                int prefix = 0;
                if (resource.Kind == AdaptiveResourceKind.TransportStream && start == 0)
                {
                    prefix = (int)Math.Min(564, response.Length);
                    await ReadExactlyAsync(response, buffer.AsMemory(0, prefix)).ConfigureAwait(false);
                    if (prefix < 376 || buffer[0] != 0x47 || buffer[188] != 0x47)
                        throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
                    Volatile.Write(ref resource.Validated, 1);
                }
                string mime = resource.Kind == AdaptiveResourceKind.TransportStream ? "video/mp2t" : "video/mp4";
                long outputLength = response.Length + (liveInitialization?.Length ?? 0);
                long outputTotal = response.Total + (liveInitialization?.Length ?? 0);
                await StatusAsync(stream, request.Value.Range is null ? 200 : 206, outputLength, mime,
                    request.Value.Range is null ? null : $"bytes {start}-{start + outputLength - 1}/{outputTotal}").ConfigureAwait(false);
                if (prefix > 0) await WriteAsync(stream, buffer.AsMemory(0, prefix)).ConfigureAwait(false);
                if (liveInitialization is not null) await WriteAsync(stream, liveInitialization).ConfigureAwait(false);
                if (resource.Kind == AdaptiveResourceKind.Mp4Segment)
                {
                    if (start != 0) throw new PlaybackException(PlaybackError.HttpInvalidRange);
                    await CopyFragmentAsync(response, stream, buffer).ConfigureAwait(false);
                }
                else
                {
                    int read;
                    while ((read = await response.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                        await WriteAsync(stream, buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }
            await response.VerifyEndAsync().ConfigureAwait(false);
            _live?.PayloadCompleted(request.Value.Path);
        }
        catch (Exception error)
        {
            ReportRequestFailure(error, authenticated, requestLifetime.Token);
        }
        finally
        {
            requestLifetime.Cancel();
            client.Dispose();
            await disconnect.ConfigureAwait(false);
            if (liveInitialization is not null) Array.Clear(liveInitialization);
            resource?.Release();
            _clients.TryRemove(id, out _);
        }
    }
    private void ReportRequestFailure(Exception error, bool authenticated, CancellationToken request)
    {
        // A native seek/disconnect retires only that IO. But a confirmed payload rejection
        // cannot become successful EOF merely because the socket closes at the same time.
        // Session cancellation/old generations remain suppressed by Fail's lifetime check.
        if (!authenticated || error is LocalClientClosedException) return;
        if (request.IsCancellationRequested && error is not PlaybackException
            { Code: PlaybackError.HttpFormatUnsupported or PlaybackError.AdaptiveInvalid or
                PlaybackError.AdaptiveUnsupported or PlaybackError.AdaptiveDrmUnsupported or
                PlaybackError.AdaptiveEncryptionUnsupported or PlaybackError.AdaptiveBudget or
                PlaybackError.HttpAddressBlocked or PlaybackError.HttpRedirectBlocked or
                PlaybackError.HttpRepresentationChanged }) return;
        Fail(error);
    }
    private static async Task WatchDisconnectAsync(NetworkStream stream, CancellationTokenSource lifetime)
    {
        try
        {
            // No pipelining/body is allowed; EOF or extra input retires just this transfer (seek).
            await stream.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false);
            lifetime.Cancel();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { lifetime.Cancel(); }
    }
    private async Task<(string Path, string? Range)?> RequestAsync(NetworkStream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(_options.RequestTimeout);
        var bytes = new byte[8192];
        int count = 0;
        while (count < bytes.Length)
        {
            int n = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), deadline.Token).ConfigureAwait(false);
            if (n == 0) return null;
            count += n;
            if (bytes.AsSpan(0, count).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        int headerEnd = bytes.AsSpan(0, count).IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0 || headerEnd + 4 != count || bytes.AsSpan(0, count).ContainsAnyInRange((byte)128, byte.MaxValue))
            return null;
        var lines = Encoding.ASCII.GetString(bytes, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || first[2] is not ("HTTP/1.1" or "HTTP/1.0") ||
            !first[1].StartsWith("/r/", StringComparison.Ordinal) || first[1].Length != 35) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) return null;
        }
        if (headers.GetValueOrDefault("Host") != $"127.0.0.1:{Port}" ||
            headers.ContainsKey("Origin") || headers.ContainsKey("Transfer-Encoding") ||
            headers.ContainsKey("Content-Length") ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(headers.GetValueOrDefault("User-Agent", "")),
                Encoding.ASCII.GetBytes(Capability))) return null;
        return (first[1], headers.GetValueOrDefault("Range"));
    }
    private static (long Start, long? End) ParseRange(string? range)
    {
        if (range is null) return (0, null);
        if (!range.StartsWith("bytes=", StringComparison.Ordinal)) throw new PlaybackException(PlaybackError.HttpInvalidRange);
        var parts = range[6..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start) ||
            start < 0) throw new PlaybackException(PlaybackError.HttpInvalidRange);
        long? end = null;
        if (parts[1].Length != 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n < start)
                throw new PlaybackException(PlaybackError.HttpInvalidRange);
            end = n;
        }
        return (start, end);
    }
    private Task StatusAsync(NetworkStream stream, int status, long length, string mime = "application/octet-stream",
        string? range = null) => WriteAsync(stream, Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} Response\r\nContent-Length: {length}\r\nContent-Type: {mime}\r\n" +
            "Connection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
            (range is null ? "" : $"Content-Range: {range}\r\n") + "\r\n"));
    private async Task WriteAsync(NetworkStream stream, ReadOnlyMemory<byte> bytes)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(_options.ReadTimeout);
        try { await stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false); }
        catch (IOException) { throw new LocalClientClosedException(); }
        catch (ObjectDisposedException) { throw new LocalClientClosedException(); }
    }
    private sealed class LocalClientClosedException : Exception;
    private static async Task ReadExactlyAsync(AdaptiveHttpTransport.Response response, Memory<byte> bytes)
    {
        int position = 0;
        while (position < bytes.Length)
        {
            int n = await response.ReadAsync(bytes[position..]).ConfigureAwait(false);
            if (n == 0) throw new PlaybackException(PlaybackError.HttpTruncated);
            position += n;
        }
    }
    private async Task CopyFragmentAsync(AdaptiveHttpTransport.Response response, NetworkStream output, byte[] buffer)
    {
        long remaining = response.Length;
        while (remaining > 0)
        {
            if (remaining < 8) throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            await ReadExactlyAsync(response, buffer.AsMemory(0, 8)).ConfigureAwait(false);
            long size = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            string kind = Encoding.ASCII.GetString(buffer, 4, 4);
            if (kind is not ("styp" or "moof" or "mdat" or "sidx" or "free"))
                throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            int header = 8;
            if (size == 1)
            {
                await ReadExactlyAsync(response, buffer.AsMemory(8, 8)).ConfigureAwait(false);
                ulong large = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(8));
                if (large > long.MaxValue) throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
                size = (long)large; header = 16;
            }
            if (size == 0 && kind == "mdat") size = remaining;
            if (size < header || size > remaining) throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            await WriteAsync(output, buffer.AsMemory(0, header)).ConfigureAwait(false);
            long body = size - header;
            while (body > 0)
            {
                int n = (int)Math.Min(body, buffer.Length);
                await ReadExactlyAsync(response, buffer.AsMemory(0, n)).ConfigureAwait(false);
                await WriteAsync(output, buffer.AsMemory(0, n)).ConfigureAwait(false);
                body -= n;
            }
            remaining -= size;
        }
    }
    internal static void ValidateInit(ReadOnlySpan<byte> bytes, int depth = 0)
    {
        if (depth > 12) throw new PlaybackException(PlaybackError.AdaptiveBudget);
        while (!bytes.IsEmpty)
        {
            if (bytes.Length < 8) throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (size < 8 || size > bytes.Length) throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            string kind = Encoding.ASCII.GetString(bytes.Slice(4, 4));
            var body = bytes.Slice(8, (int)size - 8);
            if (depth == 0 && kind is not ("ftyp" or "moov" or "free") ||
                kind is "rmra" or "rmda" or "rdrf" or "uuid")
                throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            if (kind is "moov" or "trak" or "mdia" or "minf" or "dinf" or "stbl" or "mvex")
                ValidateInit(body, depth + 1);
            if (kind == "dref")
            {
                if (body.Length != 20 || BinaryPrimitives.ReadUInt32BigEndian(body[4..]) != 1 ||
                    BinaryPrimitives.ReadUInt32BigEndian(body[8..]) != 12 ||
                    !body.Slice(12, 4).SequenceEqual("url "u8) ||
                    BinaryPrimitives.ReadUInt32BigEndian(body[16..]) != 1)
                    throw new PlaybackException(PlaybackError.HttpFormatUnsupported);
            }
            bytes = bytes[(int)size..];
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_handlers) return new(_dispose ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Cancel();
        await _accept.ConfigureAwait(false);
        Task[] pending;
        lock (_handlers) pending = _handlers.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        _transport.Dispose();
        _live?.Clear();
        _manifest.Clear();
        lock (_handlers) _handlers.Clear();
        Capability = "";
        _abort.Dispose(); _lifetime.Dispose();
    }
}
