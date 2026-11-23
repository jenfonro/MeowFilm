using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MeowFilm.Core.Debug;

[assembly: InternalsVisibleTo("MeowFilm.Platform")]

namespace MeowFilm.Core.Runtime;

/// <summary>HTTP exchanges with request correlation. The process owner holds session lifetime.</summary>
public sealed partial class RuntimeHttpClient : IRuntimeClient, IRuntimeImageClient, IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient imageClient;
    private readonly Uri endpoint;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly HashSet<TaskCompletionSource> pending = [];
    private Task? disposal;
    internal string EndpointUrl => endpoint.AbsoluteUri;
    internal int ActiveRequestCount { get { lock (gate) return pending.Count; } }

    public RuntimeHttpClient(string baseUrl) : this(RuntimeHttpProtocol.ValidateEndpoint(baseUrl)) { }

    internal RuntimeHttpClient(Uri endpoint)
    {
        this.endpoint = endpoint;
        client = new(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            // SocketsHttpHandler may replay a POST when a reused HTTP/1.1
            // connection dies before response headers. Fresh connections avoid
            // that implicit replay; concurrent requests have no admission quota.
            PooledConnectionLifetime = TimeSpan.Zero
        }) { Timeout = Timeout.InfiniteTimeSpan };
        imageClient = new(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            // Reuse complete image GETs; disposing an unfinished body must
            // close its connection rather than start a background drain.
            MaxResponseDrainSize = 0
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        CallAsyncCore(method, parameters, null, cancellationToken);

    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        CallAsyncCore(method, parameters, timeout, cancellationToken);

    private Task<JsonElement> CallAsyncCore(string method, object? parameters, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (method == "runtime.settings.changed")
            return RequestAsync(HttpMethod.Post, "/v1/settings/changed", BridgeCodec.EncodeJSONValue(parameters ?? new { }),
                false, RuntimeHttpProtocol.Name, RuntimeHttpProtocol.PublicCodes, cancellationToken, timeout: timeout);
        foreach (var (id, contract) in RuntimeHttpProtocol.Modules)
        {
            if (method is null || !method.StartsWith(contract.Prefix, StringComparison.Ordinal)) continue;
            var operation = method[contract.Prefix.Length..];
            if (!RuntimeHttpProtocol.ValidOperation(operation)) break;
            return SendJsonAsync($"/v1/{id}/{operation}", parameters ?? new { }, contract, timeout, cancellationToken);
        }
        throw new BridgeException("METHOD_UNAVAILABLE");
    }
    private async Task<JsonElement> SendJsonAsync(string path, object parameters,
        RuntimeHttpProtocol.ModuleContract contract, TimeSpan? timeout, CancellationToken token)
    {
        var bytes = BridgeCodec.EncodeJSONValue(parameters);
        try { return await RequestAsync(HttpMethod.Post, path, bytes, false, contract.Name, contract.Codes, token,
            timeout: timeout ?? (contract.Name == "netease.token.v1" ? TimeSpan.FromSeconds(60) : null)).ConfigureAwait(false); }
        finally { Clear(bytes); }
    }

    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RuntimeHttpProtocol.Modules.TryGetValue(module, out var contract))
            throw new BridgeException("METHOD_UNAVAILABLE");
        if (installationId is null || sourceRevision is <= 0 or > BridgeCodec.MaximumSafeInteger)
            throw new BridgeException("INVALID_REQUEST");
        // Module-specific source size, UTF-8, basename and identity contracts
        // are checked by the runtime and returned in its complete envelope.
        var path = $"/v1/{module}/sources/{Uri.EscapeDataString(installationId)}/" +
            sourceRevision.ToString(CultureInfo.InvariantCulture) +
            (basename is null ? "" : "?basename=" + Uri.EscapeDataString(basename));
        return RequestAsync(HttpMethod.Put, path, source, true, contract.Name, contract.Codes, cancellationToken);
    }

    public Task<JsonElement> DescribeAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(HttpMethod.Get, "/v1/describe", default, false,
            RuntimeHttpProtocol.Name, RuntimeHttpProtocol.PublicCodes, cancellationToken);

    public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var provider = RuntimeHttpProtocol.ImageProvider(reference);
        if (provider is null) throw new BridgeException("INVALID_REQUEST");
        return ExchangeAsync(imageClient, HttpMethod.Get, reference, default, false, async (response, token) =>
        {
            var bytes = await ReadImageBodyAsync(response, token).ConfigureAwait(false);
            try
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if ((int)response.StatusCode is not (400 or 401 or 403 or 404 or 413 or 415 or 502) ||
                        response.Content.Headers.ContentType?.ToString() != "application/json")
                        throw new BridgeException("INVALID_RESPONSE");
                    var spec = RuntimeHttpProtocol.Modules[provider];
                    var failure = RuntimeHttpProtocol.ValidateEnvelope(bytes, spec.Name, spec.Codes);
                    if (failure.GetProperty("ok").GetBoolean()) throw new BridgeException("INVALID_RESPONSE");
                    throw new BridgeException(failure.GetProperty("code").GetString()!);
                }
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    throw new BridgeException("INVALID_RESPONSE");
                return bytes;
            }
            catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
        }, cancellationToken, Clear);
    }

    private static async Task<byte[]> ReadImageBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new BridgeException("INVALID_RESPONSE");
        long? expected = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[32768];
        try
        {
            for (;;)
            {
                int count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            if (bytes.Length == 0 || expected is { } length && bytes.Length != length)
                throw new BridgeException("INVALID_RESPONSE");
            return bytes.ToArray();
        }
        finally
        {
            Clear(buffer);
            if (bytes.TryGetBuffer(out var view)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(view);
        }
    }

    private static void Clear(byte[] bytes) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);

    private Task<JsonElement> RequestAsync(HttpMethod method, string path, ReadOnlyMemory<byte> body,
        bool raw, string contract, IReadOnlySet<string> codes, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        bool debug = DebugLog.IsEnabled;
        string? opId = debug ? DebugLog.NextOpId() : null;
        string? traceId = debug ? DebugLog.Correlation?.TraceId : null;
        string? requestText = debug && !raw && !body.IsEmpty ? Encoding.UTF8.GetString(body.Span) : null;
        var started = debug ? Stopwatch.GetTimestamp() : 0L;
        if (debug)
        {
            var sub = SubFromPath(path);
            var reqPayload = new { method = method.Method, path, contract, raw, bodyBytes = body.Length, body = requestText };
            DebugLog.Debug(DebugCategories.RuntimeBridge, sub, "rpc.request", reqPayload, opId: opId, traceId: traceId);
            foreach (var domainCat in DomainCategoriesFromPath(path))
                DebugLog.Debug(domainCat, sub, "rpc.request", reqPayload, opId: opId, traceId: traceId);
        }
        return ExchangeAsync(client, method, path, body, raw, async (response, token) =>
        {
            if (response.Content.Headers.ContentType?.ToString() != "application/json"
                || (int)response.StatusCode is not (200 or 400 or 401 or 403 or 404 or 413 or 415))
            {
                if (debug)
                {
                    var sub = SubFromPath(path);
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var errPayload = new
                    {
                        method = method.Method,
                        path,
                        status = (int)response.StatusCode,
                        contentType = response.Content.Headers.ContentType?.ToString(),
                        code = "INVALID_RESPONSE"
                    };
                    DebugLog.Error(DebugCategories.RuntimeBridge, sub, "rpc.invalid_http", errPayload, opId: opId, traceId: traceId, durationMs: elapsedMs);
                    foreach (var domainCat in DomainCategoriesFromPath(path))
                        DebugLog.Error(domainCat, sub, "rpc.invalid_http", errPayload, opId: opId, traceId: traceId, durationMs: elapsedMs);
                }
                throw new BridgeException("INVALID_RESPONSE");
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            try
            {
                string? responseText = debug ? Encoding.UTF8.GetString(bytes) : null;
                JsonElement result;
                try
                {
                    result = RuntimeHttpProtocol.ValidateEnvelope(bytes, contract, codes);
                }
                catch (BridgeException ex)
                {
                    if (debug)
                    {
                        var sub = SubFromPath(path);
                        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        var invalidPayload = new
                        {
                            method = method.Method,
                            path,
                            status = (int)response.StatusCode,
                            code = ex.Code,
                            responseBytes = bytes.Length,
                            response = responseText
                        };
                        DebugLog.Error(DebugCategories.RuntimeBridge, sub, "rpc.invalid_envelope", invalidPayload, error: ex, opId: opId, traceId: traceId, durationMs: elapsedMs);
                        foreach (var domainCat in DomainCategoriesFromPath(path))
                            DebugLog.Error(domainCat, sub, "rpc.invalid_envelope", invalidPayload, error: ex, opId: opId, traceId: traceId, durationMs: elapsedMs);
                    }
                    throw;
                }
                if (response.StatusCode != HttpStatusCode.OK && result.GetProperty("ok").GetBoolean())
                    throw new BridgeException("INVALID_RESPONSE");
                if (debug)
                {
                    bool ok = result.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                    string? code = !ok && result.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String
                        ? codeProp.GetString() : null;
                    var sub = SubFromPath(path);
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var respPayload = new { method = method.Method, path, status = (int)response.StatusCode, ok, code, responseBytes = bytes.Length, response = responseText };
                    DebugLog.Write(ok ? "info" : "warn", DebugCategories.RuntimeBridge, sub, "rpc.response",
                        respPayload, opId: opId, traceId: traceId, durationMs: elapsedMs);
                    foreach (var domainCat in DomainCategoriesFromPath(path))
                        DebugLog.Write(ok ? "info" : "warn", domainCat, sub, "rpc.response",
                            respPayload, opId: opId, traceId: traceId, durationMs: elapsedMs);
                }
                return result;
            }
            finally { Clear(bytes); }
        }, cancellationToken, timeout: timeout, debugOpId: opId, debugTraceId: traceId, debugStarted: started);
    }

    internal async Task<DebugNodeForwardResult> SendNodeDebugRequestAsync(HttpMethod method, string path,
        object? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = body is null ? ReadOnlyMemory<byte>.Empty : BridgeCodec.EncodeJSONValue(body);
        var opId = DebugLog.IsEnabled ? (DebugLog.Correlation?.OpId ?? DebugLog.NextOpId()) : null;
        var traceId = DebugLog.IsEnabled ? DebugLog.Correlation?.TraceId : null;
        var started = Stopwatch.GetTimestamp();
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource deadline;
        lock (gate)
        {
            if (disposal is not null)
                return new DebugNodeForwardResult(false, 503, null, "RUNTIME_CLOSED");
            deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            pending.Add(owner);
        }
        using (deadline)
        try
        {
            deadline.CancelAfter(timeout);
            using var request = new HttpRequestMessage(method, new Uri(endpoint, path))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var requestId = Guid.NewGuid().ToString("D");
            request.Headers.Add("X-Meow-Request-Id", requestId);
            if (opId is not null) request.Headers.Add("X-Meow-Debug-Op", opId);
            if (traceId is not null) request.Headers.Add("X-Meow-Debug-Trace", traceId);
            if (method != HttpMethod.Get)
            {
                request.Content = new ReadOnlyMemoryContent(bytes.IsEmpty ? "{}"u8.ToArray() : bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var payloadBytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            JsonElement? parsed = null;
            if (payloadBytes.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadBytes);
                    parsed = doc.RootElement.Clone();
                }
                catch { }
            }
            int status = (int)response.StatusCode;
            bool methodNotFound = status == 404 ||
                (parsed is { ValueKind: JsonValueKind.Object } obj &&
                 obj.TryGetProperty("code", out var codeProp) &&
                 codeProp.ValueKind == JsonValueKind.String &&
                 string.Equals(codeProp.GetString(), "METHOD_NOT_FOUND", StringComparison.Ordinal));
            if (DebugLog.IsEnabled)
            {
                DebugLog.Debug(DebugCategories.RuntimeBridge, "node-debug", "debug.forward",
                    new { method = method.Method, path, status, available = !methodNotFound },
                    opId: opId, traceId: traceId, durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            return methodNotFound
                ? new DebugNodeForwardResult(false, status, parsed, "NODE_DEBUG_ENDPOINT_UNAVAILABLE")
                : new DebugNodeForwardResult(true, status, parsed, null);
        }
        catch (OperationCanceledException)
        {
            if (lifetime.IsCancellationRequested)
                return new DebugNodeForwardResult(false, 503, null, "RUNTIME_CLOSED");
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new DebugNodeForwardResult(false, 504, null, "TIMEOUT");
        }
        catch (Exception ex)
        {
            return new DebugNodeForwardResult(false, 503, null, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            lock (gate) pending.Remove(owner);
            owner.TrySetResult();
        }
    }

    internal async Task<DebugNodeInvokeResult> InvokeDebugRawAsync(string path, JsonElement? input,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object payloadObject = input is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null }
            ? input.Value : new { };
        var bytes = BridgeCodec.EncodeJSONValue(payloadObject);
        bool debug = DebugLog.IsEnabled;
        var opId = debug ? (DebugLog.Correlation?.OpId ?? DebugLog.NextOpId()) : null;
        var traceId = debug ? DebugLog.Correlation?.TraceId : null;
        string? requestText = debug && bytes.Length > 0 ? Encoding.UTF8.GetString(bytes) : null;
        var started = Stopwatch.GetTimestamp();
        var sub = SubFromPath(path);
        if (debug)
        {
            var reqPayload = new { method = "POST", path, bodyBytes = bytes.Length, body = requestText };
            DebugLog.Debug(DebugCategories.RuntimeBridge, sub, "rpc.request", reqPayload, opId: opId, traceId: traceId);
            foreach (var domainCat in DomainCategoriesFromPath(path))
                DebugLog.Debug(domainCat, sub, "rpc.request", reqPayload, opId: opId, traceId: traceId);
        }

        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource deadline;
        lock (gate)
        {
            if (disposal is not null)
            {
                Clear(bytes);
                throw DebugApiException.RuntimeUnavailable("Node runtime connection closed.");
            }
            deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            pending.Add(owner);
        }
        using (deadline)
        try
        {
            deadline.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, path))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Add("X-Meow-Request-Id", Guid.NewGuid().ToString("D"));
            if (opId is not null) request.Headers.Add("X-Meow-Debug-Op", opId);
            if (traceId is not null) request.Headers.Add("X-Meow-Debug-Trace", traceId);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var payloadBytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int status = (int)response.StatusCode;
            string responseText = payloadBytes.Length > 0 ? Encoding.UTF8.GetString(payloadBytes) : "";
            JsonElement? parsed = null;
            if (payloadBytes.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(payloadBytes);
                    parsed = doc.RootElement.Clone();
                }
                catch { }
            }
            if (debug)
            {
                bool ok = status == 200 && parsed is { ValueKind: JsonValueKind.Object } obj &&
                    obj.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                string? code = !ok && parsed is { ValueKind: JsonValueKind.Object } errObj &&
                    errObj.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String
                    ? codeProp.GetString() : null;
                var respPayload = new { method = "POST", path, status, ok, code, responseBytes = payloadBytes.Length, response = responseText };
                DebugLog.Write(ok ? "info" : "warn", DebugCategories.RuntimeBridge, sub, "rpc.response",
                    respPayload, opId: opId, traceId: traceId, durationMs: elapsedMs);
                foreach (var domainCat in DomainCategoriesFromPath(path))
                    DebugLog.Write(ok ? "info" : "warn", domainCat, sub, "rpc.response",
                        respPayload, opId: opId, traceId: traceId, durationMs: elapsedMs);
            }
            return new DebugNodeInvokeResult(status, parsed, parsed is null ? responseText : null, Math.Round(elapsedMs, 3));
        }
        catch (OperationCanceledException)
        {
            if (lifetime.IsCancellationRequested)
                throw DebugApiException.RuntimeUnavailable("Node runtime connection closed.");
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw DebugApiException.Timeout($"Node runtime request timed out: {path}");
        }
        catch (DebugApiException) { throw; }
        catch (Exception ex)
        {
            if (debug)
            {
                DebugLog.Error(DebugCategories.RuntimeBridge, sub, "rpc.error", new { method = "POST", path },
                    error: ex, opId: opId, traceId: traceId, durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            throw DebugApiException.RuntimeUnavailable($"Node runtime request failed: {ex.Message}");
        }
        finally
        {
            Clear(bytes);
            lock (gate) pending.Remove(owner);
            owner.TrySetResult();
        }
    }

    private static string SubFromPath(string path)
    {
        var trimmed = path.TrimStart('/');
        if (trimmed.StartsWith("v1/", StringComparison.Ordinal)) trimmed = trimmed[3..];
        int query = trimmed.IndexOf('?');
        if (query >= 0) trimmed = trimmed[..query];
        return trimmed.Replace('/', '.');
    }

    private static IReadOnlyList<string> DomainCategoriesFromPath(string path)
    {
        var sub = SubFromPath(path);
        if (sub.StartsWith("settings.", StringComparison.Ordinal)) return [DebugCategories.Storage];
        if (sub.StartsWith("netease.", StringComparison.Ordinal)) return [DebugCategories.MusicPlatform];
        if (sub.StartsWith("music-platform.", StringComparison.Ordinal))
        {
            var op = sub["music-platform.".Length..].ToLowerInvariant();
            if (op.Contains("account") || op.Contains("login") || op.Contains("qr") || op.Contains("sms") || op.Contains("auth") || op.Contains("vip") || op.Contains("cookie"))
                return [DebugCategories.MusicPlatform, DebugCategories.MusicAccount];
            if (op.Contains("search"))
                return [DebugCategories.MusicPlatform, DebugCategories.Search];
            if (op.Contains("playlist") || op.Contains("favorite") || op.Contains("library") || op.Contains("lyric") || op.Contains("history") || op.Contains("sync"))
                return [DebugCategories.MusicPlatform, DebugCategories.MusicLibrary];
            if (op.Contains("play") || op.Contains("track") || op.Contains("media") || op.Contains("url") || op.Contains("quality"))
                return [DebugCategories.MusicPlatform, DebugCategories.MusicPlayback];
            if (op.Contains("home") || op.Contains("channel") || op.Contains("discover") || op.Contains("daily") || op.Contains("fm") || op.Contains("recommend") || op.Contains("rank") || op.Contains("catalog"))
                return [DebugCategories.MusicPlatform, DebugCategories.MusicCatalog];
            return [DebugCategories.MusicPlatform];
        }
        if (sub.StartsWith("music.", StringComparison.Ordinal)) return [DebugCategories.MusicPlayback];
        if (sub.StartsWith("tmdb.", StringComparison.Ordinal) || sub.StartsWith("douban.", StringComparison.Ordinal) || sub.StartsWith("cat.", StringComparison.Ordinal))
            return sub.Contains("search", StringComparison.OrdinalIgnoreCase)
                ? [DebugCategories.FilmCatalog, DebugCategories.Search]
                : [DebugCategories.FilmCatalog];
        if (sub.StartsWith("pan.", StringComparison.Ordinal) || sub.StartsWith("film.", StringComparison.Ordinal))
            return [DebugCategories.FilmPlayback];
        if (sub.StartsWith("source.", StringComparison.Ordinal))
            return [DebugCategories.Novel];
        return [];
    }

    private async Task<T> ExchangeAsync<T>(HttpClient transport, HttpMethod method, string path, ReadOnlyMemory<byte> body,
        bool raw, Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken cancellationToken,
        Action<T>? discard = null, TimeSpan? timeout = null,
        string? debugOpId = null, string? debugTraceId = null, long debugStarted = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource deadline;
        lock (gate)
        {
            if (disposal is not null) throw new BridgeException("CLOSED");
            deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            pending.Add(owner);
        }
        using (deadline)
        try
        {
            // One rules batch may contain many independently budgeted calls.
            // Its owner and connection tokens retire the exchange; Node applies
            // each rule's execution budget without a shorter HTTP batch timer.
            if (path != "/v1/source/rules.process")
                deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(method, new Uri(endpoint, path))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var requestId = Guid.NewGuid().ToString("D");
            request.Headers.Add("X-Meow-Request-Id", requestId);
            if (DebugLog.IsEnabled)
            {
                var op = debugOpId ?? DebugLog.Correlation?.OpId;
                var trace = debugTraceId ?? DebugLog.Correlation?.TraceId;
                if (!string.IsNullOrEmpty(op)) request.Headers.Add("X-Meow-Debug-Op", op);
                if (!string.IsNullOrEmpty(trace)) request.Headers.Add("X-Meow-Debug-Trace", trace);
            }
            if (method != HttpMethod.Get)
            {
                request.Content = new ReadOnlyMemoryContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(raw ? "application/octet-stream" : "application/json");
            }
            using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.Headers.TryGetValues("X-Meow-Request-Id", out var ids)
                || !ids.SequenceEqual([requestId], StringComparer.Ordinal)
                || (int)response.StatusCode is >= 300 and < 400)
                throw new BridgeException("INVALID_RESPONSE");
            var result = await read(response, deadline.Token).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lifetime.IsCancellationRequested) throw new BridgeException("CLOSED");
                return result;
            }
            catch { discard?.Invoke(result); throw; }
        }
        catch (OperationCanceledException)
        {
            if (DebugLog.IsEnabled && debugStarted != 0)
            {
                var cancelCode = lifetime.IsCancellationRequested ? "CLOSED"
                    : cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT";
                LogRpcError(path, method, cancelCode, null, debugOpId, debugTraceId, debugStarted, warnOnly: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new BridgeException(lifetime.IsCancellationRequested ? "CLOSED" : "TIMEOUT");
        }
        catch (BridgeException ex)
        {
            if (DebugLog.IsEnabled && debugStarted != 0)
                LogRpcError(path, method, ex.Code, ex, debugOpId, debugTraceId, debugStarted);
            throw;
        }
        catch (HttpRequestException ex)
        {
            if (DebugLog.IsEnabled && debugStarted != 0)
                LogRpcError(path, method, "CLOSED", ex, debugOpId, debugTraceId, debugStarted);
            throw new BridgeException("CLOSED");
        }
        catch (IOException ex)
        {
            if (DebugLog.IsEnabled && debugStarted != 0)
                LogRpcError(path, method, "CLOSED", ex, debugOpId, debugTraceId, debugStarted);
            throw new BridgeException("CLOSED");
        }
        catch (ObjectDisposedException ex)
        {
            if (DebugLog.IsEnabled && debugStarted != 0)
                LogRpcError(path, method, "CLOSED", ex, debugOpId, debugTraceId, debugStarted);
            throw new BridgeException("CLOSED");
        }
        catch (Exception ex)
        {
            if (DebugLog.IsEnabled && debugStarted != 0)
                LogRpcError(path, method, "INVALID_RESPONSE", ex, debugOpId, debugTraceId, debugStarted);
            throw new BridgeException("INVALID_RESPONSE");
        }
        finally
        {
            lock (gate) pending.Remove(owner);
            owner.TrySetResult();
        }
    }

    private static void LogRpcError(string path, HttpMethod method, string code, Exception? ex,
        string? opId, string? traceId, long started, bool warnOnly = false)
    {
        var sub = SubFromPath(path);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var payload = new { method = method.Method, path, code };
        var level = warnOnly ? "warn" : "error";
        DebugLog.Write(level, DebugCategories.RuntimeBridge, sub, "rpc.error", payload, opId, traceId, elapsedMs, ex);
        foreach (var domainCat in DomainCategoriesFromPath(path))
            DebugLog.Write(level, domainCat, sub, "rpc.error", payload, opId, traceId, elapsedMs, ex);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Task[] requests;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            requests = pending.Select(owner => owner.Task).ToArray();
        }
        _ = DisposeCoreAsync(requests, completion);
        return new(disposal);
    }

    private async Task DisposeCoreAsync(Task[] requests, TaskCompletionSource completion)
    {
        var failed = false;
        try
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); } catch { failed = true; }
            try { client.Dispose(); } catch { failed = true; }
            try { imageClient.Dispose(); } catch { failed = true; }
            await Task.WhenAll(requests).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
            if (failed) completion.TrySetException(new BridgeException("INTERNAL"));
            else completion.TrySetResult();
        }
    }
}
