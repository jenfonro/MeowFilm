using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MeowFilm.Core.Debug;

namespace MeowFilm.Core.Runtime;

public sealed partial class RuntimeHttpClient
{
    /// <summary>A snapshot subscription with one coalescing mailbox and no history.
    /// EOF is a disconnect, not a successful empty result. Reconnect is the caller's decision.</summary>
    public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(module) || after is < 0 or > BridgeCodec.MaximumSafeInteger)
            throw new BridgeException("INVALID_REQUEST");
        cancellationToken.ThrowIfCancellationRequested();
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource subscription;
        lock (gate)
        {
            if (disposal is not null) throw new BridgeException("CLOSED");
            subscription = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            pending.Add(owner);
        }
        using (subscription)
        {
            var snapshots = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
            });
            var reading = ReadEventsAsync(module, sessionId, after, snapshots.Writer, subscription.Token, cancellationToken, owner);
            try
            {
                await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (lifetime.IsCancellationRequested) throw new BridgeException("CLOSED");
                    yield return snapshot;
                }
            }
            finally
            {
                await subscription.CancelAsync().ConfigureAwait(false);
                await reading.ConfigureAwait(false);
            }
        }
    }

    private async Task ReadEventsAsync(string module, Guid eventSessionId, long after,
        ChannelWriter<JsonElement> snapshots, CancellationToken subscription, CancellationToken caller,
        TaskCompletionSource owner)
    {
        Exception? failure = null;
        string? debugOpId = null;
        string? debugTraceId = null;
        try
        {
            var path = $"/v1/{module}/events/{eventSessionId:D}?after={after.ToString(CultureInfo.InvariantCulture)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Add("X-Meow-Request-Id", Guid.NewGuid().ToString("D"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            debugOpId = DebugLog.IsEnabled ? DebugLog.NextOpId() : null;
            debugTraceId = DebugLog.IsEnabled ? DebugLog.Correlation?.TraceId : null;
            if (debugOpId is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Meow-Debug-Op", debugOpId);
                if (!string.IsNullOrEmpty(debugTraceId))
                    request.Headers.TryAddWithoutValidation("X-Meow-Debug-Trace", debugTraceId);
                DebugLog.Debug(DebugCategories.RuntimeBridge, $"sse.{module}", "sse.connect",
                    new { module, sessionId = eventSessionId.ToString("D"), after }, opId: debugOpId, traceId: debugTraceId);
            }
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(subscription);
            admission.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await EventIO(() => client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, admission.Token), caller, subscription, hasDeadline: true).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("INVALID_RESPONSE");
            admission.CancelAfter(Timeout.InfiniteTimeSpan);
            using var body = await EventIO(() => response.Content.ReadAsStreamAsync(subscription),
                caller, subscription, hasDeadline: false).ConfigureAwait(false);
            using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? eventId = null;
            string? eventType = null;
            StringBuilder? data = null;
            while (true)
            {
                var line = await EventIO(async () => await reader.ReadLineAsync(subscription).ConfigureAwait(false),
                    caller, subscription, hasDeadline: false).ConfigureAwait(false);
                if (line is null)
                    throw new BridgeException(eventId is not null || eventType is not null || data is not null ? "INVALID_RESPONSE" : "CLOSED");
                if (line.Length == 0)
                {
                    if (eventId is null && eventType is null && data is null) continue;
                    if (eventType != "snapshot" || data is null ||
                        !long.TryParse(eventId, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedSeq))
                        throw new BridgeException("INVALID_RESPONSE");
                    var value = BridgeCodec.DecodeJSONValue(Encoding.UTF8.GetBytes(data.ToString()));
                    if (!Guid.TryParseExact(BridgeCodec.RequiredProperty(value, "sessionId").GetString(), "D", out var actualSession)
                        || actualSession != eventSessionId
                        || !BridgeCodec.RequiredProperty(value, "sequence").TryGetInt64(out var sequence)
                        || sequence != expectedSeq)
                        throw new BridgeException("INVALID_RESPONSE");
                    eventId = null;
                    eventType = null;
                    data = null;
                    if (DebugLog.IsEnabled)
                    {
                        DebugLog.Trace(DebugCategories.RuntimeBridge, $"sse.{module}", "sse.snapshot",
                            new { module, sessionId = eventSessionId.ToString("D"), sequence = expectedSeq, snapshot = value },
                            opId: debugOpId, traceId: debugTraceId);
                    }
                    snapshots.TryWrite(value);
                    continue;
                }
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var field = line.AsSpan(0, colon);
                var payload = colon + 1 < line.Length && line[colon + 1] == ' '
                    ? line[(colon + 2)..] : line[(colon + 1)..];
                if (field.SequenceEqual("id")) eventId = payload;
                else if (field.SequenceEqual("event")) eventType = payload;
                else if (field.SequenceEqual("data"))
                {
                    if (data is null) data = new StringBuilder(payload);
                    else data.Append('\n').Append(payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure = caller.IsCancellationRequested ? new OperationCanceledException(caller)
                : new BridgeException(lifetime.IsCancellationRequested ? "CLOSED" : "CANCELLED");
        }
        catch (BridgeException error) { failure = error; }
        catch { failure = new BridgeException("INVALID_RESPONSE"); }
        finally
        {
            if (DebugLog.IsEnabled)
            {
                DebugLog.Debug(DebugCategories.RuntimeBridge, $"sse.{module}", "sse.closed",
                    new { module, sessionId = eventSessionId.ToString("D"), error = failure?.Message },
                    opId: debugOpId, traceId: debugTraceId);
            }
            snapshots.TryComplete(failure);
            lock (gate) pending.Remove(owner);
            owner.TrySetResult();
        }
    }

    private async Task<T> EventIO<T>(Func<Task<T>> action, CancellationToken caller, CancellationToken subscription, bool hasDeadline)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            caller.ThrowIfCancellationRequested();
            throw new BridgeException(lifetime.IsCancellationRequested ? "CLOSED"
                : hasDeadline && !subscription.IsCancellationRequested ? "TIMEOUT" : "CANCELLED");
        }
        catch (HttpRequestException) { throw new BridgeException("CLOSED"); }
        catch (IOException) { throw new BridgeException("CLOSED"); }
        catch (ObjectDisposedException) { throw new BridgeException("CLOSED"); }
    }
}
