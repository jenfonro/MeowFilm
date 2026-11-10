using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace MeowFilm.Core.Runtime;

public sealed partial class RuntimeHttpClient
{
    /// <summary>A snapshot subscription with one coalescing mailbox and no history.
    /// EOF is a disconnect, not a successful empty result. Reconnect is the caller's decision.</summary>
    public async IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(module) || module[0] is < 'a' or > 'z'
            || module.Any(value => value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || after is < 0 or > BridgeCodec.MaximumSafeInteger)
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
        try
        {
            var path = $"/v1/{module}/events/{eventSessionId:D}?after={after.ToString(CultureInfo.InvariantCulture)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            var requestId = Guid.NewGuid().ToString("D");
            request.Headers.Add("X-Meow-Request-Id", requestId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            // Only header admission has a deadline. A live observation has no absolute RPC deadline.
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(subscription);
            admission.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await EventIO(() => client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, admission.Token), caller).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK
                || !response.Headers.TryGetValues("X-Meow-Request-Id", out var ids)
                || !ids.SequenceEqual([requestId], StringComparer.Ordinal)
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentType?.CharSet is { } charset
                    && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
                throw new BridgeException("INVALID_RESPONSE");
            admission.CancelAfter(Timeout.InfiniteTimeSpan);
            using var body = await EventIO(() => response.Content.ReadAsStreamAsync(subscription),
                caller).ConfigureAwait(false);
            using var parser = new RuntimeHttpEventParser(eventSessionId);
            var bytes = new byte[32 * 1024];
            var received = false;
            while (true)
            {
                var count = await EventIO(async () => await body.ReadAsync(bytes, subscription).ConfigureAwait(false),
                    caller).ConfigureAwait(false);
                if (count == 0) throw new BridgeException(!received || parser.Incomplete ? "INVALID_RESPONSE" : "CLOSED");
                for (var index = 0; index < count; index++)
                {
                    subscription.ThrowIfCancellationRequested();
                    var snapshot = parser.Feed(bytes[index]);
                    if (snapshot is not { } value) continue;
                    received = true;
                    snapshots.TryWrite(value);
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
            // The bounded reader runs independently of a parked UI iterator, so App close
            // can drain it even if that iterator never calls MoveNext again.
            snapshots.TryComplete(failure);
            lock (gate) pending.Remove(owner);
            owner.TrySetResult();
        }
    }

    private async Task<T> EventIO<T>(Func<Task<T>> action, CancellationToken caller)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            caller.ThrowIfCancellationRequested();
            throw new BridgeException(lifetime.IsCancellationRequested ? "CLOSED" : "TIMEOUT");
        }
        catch (HttpRequestException) { throw new BridgeException("CLOSED"); }
        catch (IOException) { throw new BridgeException("CLOSED"); }
        catch (ObjectDisposedException) { throw new BridgeException("CLOSED"); }
    }
}

/// <summary>Byte framing avoids an unbounded ReadLine/string accumulator. Only the
/// current JSON value is buffered; its size is governed by the existing JSON contract,
/// not a new transport quota. Unknown SSE fields/comments are discarded as they arrive.</summary>
internal sealed class RuntimeHttpEventParser(Guid sessionId) : IDisposable
{
    private static readonly byte[][] Fields = ["id"u8.ToArray(), "event"u8.ToArray(), "data"u8.ToArray()];
    private MemoryStream data = new();
    private int candidates = 7, fieldLength, field = -1, eventOffset, bom;
    private int utf8Remaining, utf8Minimum = 0x80, utf8Maximum = 0xbf;
    private long id;
    private bool lineStarted, valueStarted, skipSpace, skipLf, hasId, hasEvent, hasData, idDigit;
    public bool Incomplete => lineStarted || hasId || hasEvent || hasData || bom is 1 or 2 || utf8Remaining != 0;

    public JsonElement? Feed(byte value)
    {
        ValidateUtf8(value);
        if (bom < 3)
        {
            byte[] prefix = [0xef, 0xbb, 0xbf];
            if (bom != 0 || value == prefix[0])
            {
                if (value != prefix[bom]) throw new BridgeException("INVALID_RESPONSE");
                bom++;
                return null;
            }
            bom = 3;
        }
        if (skipLf) { skipLf = false; if (value == 10) return null; }
        if (value is 10 or 13)
        {
            skipLf = value == 13;
            return EndLine();
        }
        lineStarted = true;
        if (!valueStarted)
        {
            if (value == 58)
            {
                field = -1;
                for (var index = 0; index < Fields.Length; index++)
                    if ((candidates & (1 << index)) != 0 && fieldLength == Fields[index].Length) field = index;
                valueStarted = true; skipSpace = true;
                if (field == 0)
                {
                    if (hasId) throw new BridgeException("INVALID_RESPONSE");
                    id = 0; idDigit = false;
                }
                else if (field == 1)
                {
                    if (hasEvent) throw new BridgeException("INVALID_RESPONSE");
                    eventOffset = 0;
                }
                else if (field == 2)
                {
                    if (hasData) data.WriteByte(10);
                    hasData = true;
                }
                return null;
            }
            for (var index = 0; index < Fields.Length; index++)
                if (fieldLength >= Fields[index].Length || Fields[index][fieldLength] != value)
                    candidates &= ~(1 << index);
            // Once no field can match there is no need to count an arbitrary field name.
            if (candidates != 0) fieldLength++;
            return null;
        }
        if (skipSpace) { skipSpace = false; if (value == 32) return null; }
        if (field == 0)
        {
            if (value is < 48 or > 57 || id > (BridgeCodec.MaximumSafeInteger - (value - 48)) / 10)
                throw new BridgeException("INVALID_RESPONSE");
            id = id * 10 + value - 48; idDigit = true;
        }
        else if (field == 1)
        {
            if (eventOffset >= 8 || "snapshot"u8[eventOffset++] != value)
                throw new BridgeException("INVALID_RESPONSE");
        }
        else if (field == 2) data.WriteByte(value);
        return null;
    }

    private void ValidateUtf8(byte value)
    {
        if (utf8Remaining != 0)
        {
            if (value < utf8Minimum || value > utf8Maximum) throw new BridgeException("INVALID_RESPONSE");
            utf8Remaining--; utf8Minimum = 0x80; utf8Maximum = 0xbf;
        }
        else if (value < 0x80) { }
        else if (value is >= 0xc2 and <= 0xdf) utf8Remaining = 1;
        else if (value is >= 0xe0 and <= 0xef)
        {
            utf8Remaining = 2;
            if (value == 0xe0) utf8Minimum = 0xa0;
            if (value == 0xed) utf8Maximum = 0x9f;
        }
        else if (value is >= 0xf0 and <= 0xf4)
        {
            utf8Remaining = 3;
            if (value == 0xf0) utf8Minimum = 0x90;
            if (value == 0xf4) utf8Maximum = 0x8f;
        }
        else throw new BridgeException("INVALID_RESPONSE");
    }

    private JsonElement? EndLine()
    {
        if (lineStarted)
        {
            if (field == 0) { if (!idDigit) throw new BridgeException("INVALID_RESPONSE"); hasId = true; }
            if (field == 1) { if (eventOffset != 8) throw new BridgeException("INVALID_RESPONSE"); hasEvent = true; }
            candidates = 7; fieldLength = 0; field = -1;
            lineStarted = valueStarted = skipSpace = false;
            return null;
        }
        if (!hasId && !hasEvent && !hasData) return null;
        if (!hasId || !hasEvent || !hasData) throw new BridgeException("INVALID_RESPONSE");
        JsonElement snapshot;
        try
        {
            snapshot = BridgeCodec.DecodeJSONValue(data.GetBuffer().AsMemory(0, checked((int)data.Length)));
            if (snapshot.ValueKind != JsonValueKind.Object
                || !snapshot.TryGetProperty("sessionId", out var session)
                || !Guid.TryParseExact(session.GetString(), "D", out var actualSession) || actualSession != sessionId
                || !snapshot.TryGetProperty("sequence", out var sequence) || sequence.GetDouble() != id)
                throw new BridgeException("INVALID_RESPONSE");
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
        // Do not retain the largest historical frame's backing array indefinitely.
        data.Dispose();
        data = new MemoryStream();
        hasId = hasEvent = hasData = false;
        return snapshot;
    }

    public void Dispose() => data.Dispose();
}
