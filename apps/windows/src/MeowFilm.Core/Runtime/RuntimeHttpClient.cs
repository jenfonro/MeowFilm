using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MeowFilm.Core.Runtime;

/// <summary>Authenticated loopback HTTP exchanges. The process owner holds session lifetime.</summary>
public sealed class RuntimeHttpClient : IRuntimeClient, IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string sessionId, requestKey;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly HashSet<TaskCompletionSource> pending = [];
    private Task? disposal;

    public RuntimeHttpClient(string baseUrl, string sessionId, string requestKey)
    {
        endpoint = RuntimeHttpProtocol.ValidateEndpoint(baseUrl);
        RuntimeHttpProtocol.ValidateSession(sessionId, requestKey);
        this.sessionId = sessionId; this.requestKey = requestKey;
        client = new(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            // SocketsHttpHandler may replay a POST when a reused HTTP/1.1
            // connection dies before response headers. Fresh connections avoid
            // that implicit replay; concurrent requests have no admission quota.
            PooledConnectionLifetime = TimeSpan.Zero
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (id, contract) in RuntimeHttpProtocol.Modules)
        {
            if (method is null || !method.StartsWith(contract.Prefix, StringComparison.Ordinal)) continue;
            var operation = method[contract.Prefix.Length..];
            if (!contract.Operations.Contains(operation, StringComparer.Ordinal)) break;
            return RequestAsync(HttpMethod.Post, $"/v1/{id}/{operation}",
                BridgeCodec.EncodeJSONValue(parameters ?? new { }), false, contract.Name, contract.Codes, cancellationToken);
        }
        throw new BridgeException("METHOD_UNAVAILABLE");
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

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync(HttpMethod.Post, "/v1/close", "{}"u8.ToArray(), false,
            RuntimeHttpProtocol.Name, RuntimeHttpProtocol.PublicCodes, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!result.GetProperty("ok").GetBoolean()) throw new BridgeException(result.GetProperty("code").GetString()!);
            var value = result.GetProperty("value");
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2
                || !value.GetProperty("closed").GetBoolean() || !value.GetProperty("cleanupComplete").GetBoolean())
                throw new BridgeException("INVALID_RESPONSE");
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, ReadOnlyMemory<byte> body,
        bool raw, string contract, IReadOnlySet<string> codes, CancellationToken cancellationToken)
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
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(method, new Uri(endpoint, path))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var requestId = Guid.NewGuid().ToString("D");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestKey);
            request.Headers.Add("X-Meow-Session", sessionId);
            request.Headers.Add("X-Meow-Request-Id", requestId);
            if (method != HttpMethod.Get)
            {
                request.Content = new ReadOnlyMemoryContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(raw ? "application/octet-stream" : "application/json");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.Headers.TryGetValues("X-Meow-Request-Id", out var ids)
                || !ids.SequenceEqual([requestId], StringComparer.Ordinal)
                || response.Content.Headers.ContentType?.ToString() != "application/json"
                || (int)response.StatusCode is not (200 or 400 or 401 or 403 or 404 or 413 or 415))
                throw new BridgeException("INVALID_RESPONSE");
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            var result = RuntimeHttpProtocol.ValidateEnvelope(bytes, contract, codes);
            if (response.StatusCode != HttpStatusCode.OK && result.GetProperty("ok").GetBoolean())
                throw new BridgeException("INVALID_RESPONSE");
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) throw new BridgeException("CLOSED");
            return result;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new BridgeException(lifetime.IsCancellationRequested ? "CLOSED" : "TIMEOUT");
        }
        catch (BridgeException) { throw; }
        catch (HttpRequestException) { throw new BridgeException("CLOSED"); }
        catch (IOException) { throw new BridgeException("CLOSED"); }
        catch (ObjectDisposedException) { throw new BridgeException("CLOSED"); }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
        finally
        {
            lock (gate) pending.Remove(owner);
            owner.TrySetResult();
        }
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
