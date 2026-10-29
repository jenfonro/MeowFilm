using System.Text.Json;
using System.Threading.Channels;

namespace MeowFilm.Core.Runtime;

/// <summary>Protocol v1 peer. Carries bounded control messages, never media streams.</summary>
public sealed class BridgePeer : IAsyncDisposable
{
    // All mutable request/queue state is protected by gate. No synchronous user
    // handler, cancellation callback, or transport operation runs under it.
    private sealed class Pending
    {
        public readonly TaskCompletionSource<JsonElement> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Id;
        public bool Sent;
        public string? Cancelled;
        public Timer? Deadline;
    }

    private sealed class Active
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Timer? Deadline;
        public Task? CancellationWork;
        public bool Responded, HandlerFinished;
    }

    private sealed record Outbound(byte[] Frame, Pending? Request = null);

    private readonly Stream stream;
    private readonly string session;
    private readonly IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> methods;
    private readonly Dictionary<long, Pending> pending = new();
    private readonly Dictionary<long, Active> active = new();
    private readonly object gate = new();
    // Admission is bounded by BOTH bytes and frame count under gate, including
    // the in-flight write. Channel continuations must never run under gate.
    private readonly Channel<Outbound> outgoing = Channel.CreateUnbounded<Outbound>(
        new UnboundedChannelOptions { AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<Task> transportStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan timeout;
    private readonly Task completion;
    private long nextId, lastIncomingId, queuedBytes;
    private int queuedFrames, closed;
    public const int MaximumPending = 128;
    private const long MaximumQueuedBytes = 8L * BridgeCodec.MaximumFrameBytes;
    private const int MaximumQueuedFrames = MaximumPending * 4;
    private const int ControlReserveBytes = MaximumPending * 256;

    public BridgePeer(Stream stream, string session,
        IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>? methods = null,
        TimeSpan? timeout = null)
    {
        if (stream is null || !stream.CanRead || !stream.CanWrite || !BridgeCodec.ValidSession(session))
            throw new BridgeException("INVALID_MESSAGE");
        this.stream = stream;
        this.session = session;
        this.methods = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(
            methods ?? new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(), StringComparer.Ordinal);
        if (this.methods.Any(method => !BridgeCodec.ValidMethod(method.Key) || method.Value is null))
            throw new BridgeException("INVALID_MESSAGE");
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (this.timeout.TotalMilliseconds is < 1 or > 300_000) throw new BridgeException("INVALID_MESSAGE");
        completion = CompleteAsync(Task.Run(ReadLoopAsync), Task.Run(WriteLoopAsync));
    }

    public bool IsClosed => Volatile.Read(ref closed) != 0;
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public int ActiveCount { get { lock (gate) return active.Count; } }
    public long QueuedBytes { get { lock (gate) return queuedBytes; } }
    /// <summary>Transport workers have stopped, not proof that non-cooperative handlers have stopped.</summary>
    public Task Completion => completion;

    private Dictionary<string, object?> Envelope(string kind, long id) => new()
    {
        ["version"] = 1, ["session"] = session, ["kind"] = kind, ["id"] = id
    };

    private byte[] ErrorFrame(long id, string code)
    {
        var message = Envelope("error", id);
        message["code"] = code;
        return BridgeCodec.Encode(message);
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (IsClosed) throw new BridgeException("CLOSED");
        if (cancellationToken.IsCancellationRequested) throw new BridgeException("CANCELLED");
        var deadline = timeout ?? this.timeout;
        if (!BridgeCodec.ValidMethod(method) || deadline.TotalMilliseconds is < 1 or > 300_000)
            throw new BridgeException("INVALID_MESSAGE");
        // Snapshot arbitrary caller DTOs outside gate: getters are application
        // code. Only our envelope and validated JSON are encoded inside gate.
        var snapshot = BridgeCodec.Value(parameters);
        var entry = new Pending();
        using var timer = new Timer(_ => CancelPending(entry, "TIMEOUT"), null, Timeout.Infinite, Timeout.Infinite);
        entry.Deadline = timer;
        using var registration = cancellationToken.UnsafeRegister(_ => CancelPending(entry, "CANCELLED"), null);
        lock (gate)
        {
            if (IsClosed) throw new BridgeException("CLOSED");
            if (entry.Cancelled is { } code) throw new BridgeException(code);
            if (pending.Count >= MaximumPending || nextId >= BridgeCodec.MaximumSafeInteger) throw new BridgeException("LIMIT");
            var id = ++nextId;
            var message = Envelope("request", id);
            message["method"] = method;
            message["params"] = snapshot;
            var bytes = BridgeCodec.Encode(message);
            entry.Id = id;
            // ID allocation and FIFO admission are one critical section. A
            // concurrent call must never overtake an earlier request ID.
            if (!EnqueueLocked(bytes, false, entry)) throw new BridgeException("LIMIT");
            pending.Add(id, entry);
            timer.Change(deadline, Timeout.InfiniteTimeSpan);
        }
        return await entry.Completion.Task.ConfigureAwait(false);
    }

    private void CancelPending(Pending entry, string code)
    {
        var close = false;
        lock (gate)
        {
            entry.Cancelled ??= code;
            if (!pending.Remove(entry.Id, out var removed)) return;
            removed.Deadline?.Dispose();
            removed.Completion.TrySetException(new BridgeException(code));
            // Unsent requests stay in the bounded FIFO but are discarded by
            // the writer, without ever invoking a remote mutation.
            if (removed.Sent && !IsClosed)
                close = !EnqueueLocked(BridgeCodec.Encode(Envelope("cancel", entry.Id)), true);
        }
        if (close) Close();
    }

    // Caller holds gate. False for data means explicit LIMIT; false for
    // control means the caller must close, never silently drop a terminal frame.
    private bool EnqueueLocked(byte[] frame, bool control, Pending? request = null)
    {
        var bytesLimit = MaximumQueuedBytes - (control ? 0 : ControlReserveBytes);
        var framesLimit = MaximumQueuedFrames - (control ? 0 : MaximumPending);
        if (IsClosed || queuedBytes + frame.Length > bytesLimit || queuedFrames >= framesLimit) return false;
        if (!outgoing.Writer.TryWrite(new Outbound(frame, request))) return false;
        queuedBytes += frame.Length;
        queuedFrames++;
        return true;
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    lock (gate)
                    {
                        if (IsClosed) continue;
                        if (item.Request is { } request)
                        {
                            if (!pending.ContainsKey(request.Id)) continue;
                            request.Sent = true;
                        }
                    }
                    await stream.WriteAsync(item.Frame, lifetime.Token).ConfigureAwait(false);
                    await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        queuedBytes -= item.Frame.Length;
                        queuedFrames--;
                    }
                }
            }
        }
        catch { /* Finite public failures only; no transport/payload logging. */ }
        finally { Close(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await BridgeCodec.ReadAsync(stream, lifetime.Token).ConfigureAwait(false);
                if (!string.Equals(message.GetProperty("session").GetString(), session, StringComparison.Ordinal))
                    throw new BridgeException("INVALID_MESSAGE");
                // JSON.parse accepts 1.0 and 1e0 as integer IDs too. Decode has
                // already checked positivity, finiteness and the safe range.
                var id = (long)message.GetProperty("id").GetDouble();
                var kind = message.GetProperty("kind").GetString();
                var close = false;
                lock (gate)
                {
                    if (IsClosed) return;
                    switch (kind)
                    {
                        case "result":
                        case "error":
                            if (pending.Remove(id, out var request))
                            {
                                request.Deadline?.Dispose();
                                if (kind == "result") request.Completion.TrySetResult(message.GetProperty("value").Clone());
                                else request.Completion.TrySetException(new BridgeException(message.GetProperty("code").GetString()!));
                            }
                            break;
                        case "cancel":
                            if (active.TryGetValue(id, out var running))
                                close = CancelActiveLocked(id, running, "CANCELLED");
                            break;
                        case "request":
                            if (id <= lastIncomingId) throw new BridgeException("INVALID_MESSAGE");
                            lastIncomingId = id;
                            var name = message.GetProperty("method").GetString()!;
                            if (!methods.TryGetValue(name, out var handler))
                                close = !EnqueueLocked(ErrorFrame(id, "METHOD_UNAVAILABLE"), true);
                            else if (active.Count >= MaximumPending)
                                close = !EnqueueLocked(ErrorFrame(id, "LIMIT"), true);
                            else
                            {
                                var operation = new Active();
                                active.Add(id, operation);
                                operation.Deadline = new Timer(_ => TimeoutActive(id, operation), null, timeout, Timeout.InfiniteTimeSpan);
                                var parameters = message.GetProperty("params").Clone();
                                // A synchronous/non-cooperative handler must not
                                // occupy the reader or block incoming cancels.
                                _ = Task.Run(() => HandleAsync(id, operation, handler, parameters));
                            }
                            break;
                    }
                }
                if (close) { Close(); return; }
            }
        }
        catch { /* Protocol/transport failure terminates this generation. */ }
        finally { Close(); }
    }

    private static void StopDeadline(Active operation)
    {
        operation.Deadline?.Dispose();
        operation.Deadline = null;
    }

    // CancelAsync sets the token state synchronously but runs callbacks
    // asynchronously. Unlike Cancel(), it cannot execute user code under gate.
    // Keep ownership until those callbacks finish, including faulted callbacks.
    private static void RequestCancellationLocked(Active operation)
    {
        if (!operation.HandlerFinished && operation.CancellationWork is null)
            operation.CancellationWork = ObserveCancellationAsync(operation.Cancellation.CancelAsync());
    }

    private static async Task ObserveCancellationAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* A user cancellation callback must not abort bridge cleanup. */ }
    }

    private bool CancelActiveLocked(long id, Active operation, string code)
    {
        if (operation.Responded || IsClosed) return false;
        operation.Responded = true;
        StopDeadline(operation);
        RequestCancellationLocked(operation);
        return !EnqueueLocked(ErrorFrame(id, code), true);
    }

    private void TimeoutActive(long id, Active operation)
    {
        bool close;
        lock (gate) close = CancelActiveLocked(id, operation, "TIMEOUT");
        if (close) Close();
    }

    private async Task HandleAsync(long id, Active operation,
        Func<JsonElement, CancellationToken, Task<object?>> handler, JsonElement parameters)
    {
        try
        {
            // Linearize dispatch with Close/timeout before entering user code.
            // Already-running work still owns its slot until it really settles.
            lock (gate) { if (operation.Responded || IsClosed) return; }
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            var result = await handler(parameters, operation.Cancellation.Token).ConfigureAwait(false);
            lock (gate) { if (operation.Responded || IsClosed) return; }
            var envelope = Envelope("result", id);
            envelope["value"] = result;
            // Validate before claiming the terminal response; failed results
            // return INVALID_MESSAGE/LIMIT instead of timing out remotely.
            Respond(id, operation, BridgeCodec.Encode(envelope), false);
        }
        catch (Exception error)
        {
            Respond(id, operation, ErrorFrame(id, error switch
            {
                BridgeException bridge => bridge.Code,
                OperationCanceledException => "CANCELLED",
                _ => "INTERNAL"
            }), true);
        }
        finally
        {
            Task cancellationWork;
            lock (gate)
            {
                operation.HandlerFinished = true;
                StopDeadline(operation);
                cancellationWork = operation.CancellationWork ?? Task.CompletedTask;
            }
            await cancellationWork.ConfigureAwait(false);
            lock (gate)
            {
                operation.Cancellation.Dispose();
                active.Remove(id);
            }
        }
    }

    private void Respond(long id, Active operation, byte[] frame, bool control)
    {
        var close = false;
        lock (gate)
        {
            if (operation.Responded || IsClosed) return;
            operation.Responded = true;
            StopDeadline(operation);
            if (!EnqueueLocked(frame, control))
                close = control || !EnqueueLocked(ErrorFrame(id, "LIMIT"), true);
        }
        if (close) Close();
    }

    public void Close()
    {
        lock (gate)
        {
            if (IsClosed) return;
            Volatile.Write(ref closed, 1);
            foreach (var request in pending.Values)
            {
                request.Deadline?.Dispose();
                request.Completion.TrySetException(new BridgeException("CLOSED"));
            }
            pending.Clear();
            foreach (var operation in active.Values)
            {
                operation.Responded = true;
                StopDeadline(operation);
                RequestCancellationLocked(operation);
            }
            outgoing.Writer.TryComplete();
            while (outgoing.Reader.TryRead(out var item))
            {
                queuedBytes -= item.Frame.Length;
                queuedFrames--;
            }
        }
        // Do not let a throwing transport/cancellation callback leave pending
        // callers unresolved or prevent the other cleanup steps.
        var cancellationWork = ObserveCancellationAsync(lifetime.CancelAsync());
        try { stream.Dispose(); }
        catch { }
        finally { transportStopped.TrySetResult(cancellationWork); }
    }

    private async Task CompleteAsync(Task reader, Task writer)
    {
        await Task.WhenAll(reader, writer).ConfigureAwait(false);
        await (await transportStopped.Task.ConfigureAwait(false)).ConfigureAwait(false);
        lifetime.Dispose();
        // Active handlers are deliberately NOT cleared or awaited here.
        // Non-cooperative code cannot be forcibly stopped inside this process.
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await completion.ConfigureAwait(false);
    }
}
