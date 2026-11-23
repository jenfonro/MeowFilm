// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>Borrows the App runtime. Native leases authorize publication; Node owns protocols and physical reads.</summary>
internal sealed class MusicPlatformClient(IRuntimeClient runtime) : IAsyncDisposable
{
    private sealed class Scope
    {
        internal long Epoch;
        internal CancellationTokenSource Reads = new();
        internal readonly SemaphoreSlim Publication = new(1, 1);
        internal Task Reset = Task.CompletedTask;
        internal MusicPlatformSession? Published;
        internal bool Initialized;
        internal Exception? PublicationFailure;
    }
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object gate = new();
    private readonly Dictionary<string, Scope> scopes = new(StringComparer.Ordinal);
    private readonly HashSet<Task> active = [];
    private IMusicPlatformSessionSource? sessions;
    private bool closed;
    private Task? disposal;
    internal void BindSessions(IMusicPlatformSessionSource source) => sessions = source;
    private Scope State(string platform)
    {
        lock (gate)
        {
            if (closed) throw new MusicPlatformException("CLOSED");
            if (!scopes.TryGetValue(platform, out var scope)) scopes[platform] = scope = new();
            return scope;
        }
    }
    private static bool Same(MusicPlatformSession? left, MusicPlatformSession? right) =>
        left?.Scope == right?.Scope && left?.Generation == right?.Generation &&
        left?.UserId == right?.UserId && left?.Cookie == right?.Cookie;
    private void Current(Scope scope, long epoch, MusicPlatformSession? session)
    {
        lock (gate)
            if (closed || scope.Epoch != epoch) throw new MusicPlatformException(closed ? "CLOSED" : "STALE_SESSION");
        if (session is not null && sessions is not null && !sessions.IsCurrent(session))
            throw new MusicPlatformException("STALE_SESSION");
    }
    private static JsonElement Value(JsonElement reply)
    {
        if (!reply.GetProperty("ok").GetBoolean())
        {
            string? code = reply.GetProperty("code").GetString();
            if (string.IsNullOrEmpty(code)) throw new JsonException();
            throw new MusicPlatformException(code);
        }
        return reply.GetProperty("value").Clone();
    }
    private async Task<JsonElement> Call(string method, object parameters, CancellationToken token, TimeSpan? timeout = null,
        bool smsSend = false)
    {
        try
        {
            return Value(await (timeout is { } deadline
                ? runtime.CallAsync(method, parameters, deadline, token)
                : runtime.CallAsync(method, parameters, token)).ConfigureAwait(false));
        }
        catch (MusicPlatformException) { throw; } // A received Node error is a definite protocol result.
        catch (Exception) when (smsSend) { throw new MusicPlatformException("SMS_SEND_UNCONFIRMED"); }
        catch (BridgeException error) { throw new MusicPlatformException(error.Code); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new MusicPlatformException("INVALID_RESPONSE"); }
    }
    private static JsonElement WriteFailure(string status, string code) =>
        JsonSerializer.SerializeToElement(new { status, errorCode = code });
    private static void Published(JsonElement receipt)
    {
        if (!receipt.TryGetProperty("published", out var published) || published.ValueKind != JsonValueKind.True)
            throw new MusicPlatformException("COMMIT_UNKNOWN");
    }
    private async Task<JsonElement> CallWrite(object parameters)
    {
        try
        {
            var reply = await runtime.CallAsync("music.platform.request", parameters, CancellationToken.None).ConfigureAwait(false);
            // Node's write operations return a receipt once dispatched. An envelope
            // rejection is therefore before that boundary, unlike a lost exchange.
            if (reply.GetProperty("ok").GetBoolean()) return reply.GetProperty("value").Clone();
            string? code = reply.GetProperty("code").GetString();
            if (string.IsNullOrEmpty(code)) throw new JsonException();
            return WriteFailure("notSent", code);
        }
        catch (BridgeException error) { return WriteFailure("unknown", error.Code); }
        catch (OperationCanceledException) { return WriteFailure("unknown", "CANCELLED"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { return WriteFailure("unknown", "INVALID_RESPONSE"); }
    }
    private async Task Publish(string platform, Scope scope, long epoch, MusicPlatformSession? session, CancellationToken token)
    {
        await scope.Publication.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Current(scope, epoch, session);
            if (scope.Initialized && Same(scope.Published, session)) return;
            var receipt = await Call("music.platform.session.update", new
            { platform, session = session is null ? null : new
                { scope = session.Scope, generation = session.Generation, userId = session.UserId, cookie = session.Cookie }
            }, token).ConfigureAwait(false);
            Published(receipt);
            Current(scope, epoch, session);
            lock (gate)
            {
                if (closed || scope.Epoch != epoch) throw new MusicPlatformException(closed ? "CLOSED" : "STALE_SESSION");
                scope.Published = session; scope.Initialized = true; scope.PublicationFailure = null;
            }
        }
        finally { scope.Publication.Release(); }
    }
    private Task<JsonElement> Start(Func<Task<JsonElement>> action)
    {
        Task<JsonElement> work;
        lock (gate)
        {
            if (closed) throw new MusicPlatformException("CLOSED");
            async Task<JsonElement> Run() { await Task.Yield(); return await action().ConfigureAwait(false); }
            work = Run(); active.Add(work);
        }
        _ = work.ContinueWith(done =>
        { _ = done.Exception; lock (gate) active.Remove(work); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }
    internal async Task<JsonElement> ReadAsync(string platform, string operation, object parameters, CancellationToken token)
    {
        var session = platform is "wy" or "tx" or "kg" && sessions is not null
            ? await sessions.AcquireAsync(platform, token).ConfigureAwait(false) : null;
        return await RequestAsync(platform, operation, parameters, session, token).ConfigureAwait(false);
    }
    internal Task<JsonElement> RequestAsync(string platform, string operation, object parameters,
        MusicPlatformSession? session, CancellationToken token, Action? authority = null)
    {
        if (token.IsCancellationRequested)
            return RequestAsync(platform, operation, static () => default, session, token, authority);
        var input = JsonSerializer.SerializeToElement(parameters, Json);
        return RequestAsync(platform, operation, () => input, session, token, authority);
    }
    // Official recordings are projected after account publication, at actual dispatch.
    internal Task<JsonElement> RequestAsync(string platform, string operation, Func<JsonElement> parameters,
        MusicPlatformSession? session, CancellationToken token, Action? authority = null)
    {
        bool write = operation is "sync.write" or "dailyStyle.save" or "feedback.send" or "feedback.upload" or "vip.checkIn";
        if (token.IsCancellationRequested)
            return write ? Task.FromResult(WriteFailure("notSent", "CANCELLED")) : Task.FromCanceled<JsonElement>(token);
        var scope = State(platform);
        long epoch; CancellationToken revoked; Task reset;
        lock (gate)
        {
            if (closed) throw new MusicPlatformException("CLOSED");
            epoch = scope.Epoch; revoked = scope.Reads.Token; reset = scope.Reset;
        }
        var account = session?.UserId == "0" ? null : session;
        return Start(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, revoked);
            try
            {
                linked.Token.ThrowIfCancellationRequested(); authority?.Invoke();
                await reset.WaitAsync(linked.Token).ConfigureAwait(false);
                await Publish(platform, scope, epoch, account, linked.Token).ConfigureAwait(false);
                Current(scope, epoch, account); authority?.Invoke(); linked.Token.ThrowIfCancellationRequested();
            }
            catch (Exception error) when (write)
            {
                return WriteFailure("notSent", error switch
                {
                    MusicPlatformException known => known.Code,
                    MeowFilm.Capabilities.Accounts.MusicAccountException known => known.Code,
                    MeowFilm.Capabilities.Library.MusicLibraryException known => known.Code,
                    OperationCanceledException => "CANCELLED", _ => "INVALID_RESPONSE"
                });
            }
            var input = parameters();
            var envelope = new
            { platform, operation, @params = input,
                expectedSession = account is null ? null : new { scope = account.Scope, generation = account.Generation }
            };
            if (write) return await CallWrite(envelope).ConfigureAwait(false);
            // The existing token worker owns a 45s budget. Only its preparation
            // request borrows the original 60s transport deadline.
            TimeSpan? timeout = platform == "wy" && operation == "sync.prepare" ? TimeSpan.FromSeconds(60) : null;
            var value = await Call("music.platform.request", envelope, linked.Token, timeout).ConfigureAwait(false);
            Current(scope, epoch, account); authority?.Invoke();
            return value;
        });
    }
    internal Task<JsonElement> CandidateAsync(string platform, string operation, object parameters,
        string? cookie, string userId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = State(platform);
        long epoch; CancellationToken revoked; Task reset;
        lock (gate)
        {
            if (closed) throw new MusicPlatformException("CLOSED");
            epoch = scope.Epoch; revoked = scope.Reads.Token; reset = scope.Reset;
        }
        var input = JsonSerializer.SerializeToElement(parameters, Json);
        return Start(async () =>
        {
            // QR cancellation is the control operation that drains a retired
            // challenge. The session invalidation it accompanies cannot cancel it.
            using var linked = operation == "qr.cancel"
                ? CancellationTokenSource.CreateLinkedTokenSource(token)
                : CancellationTokenSource.CreateLinkedTokenSource(token, revoked);
            linked.Token.ThrowIfCancellationRequested();
            if (operation == "qr.cancel")
            { lock (gate) if (closed) throw new MusicPlatformException("CLOSED"); }
            else
            {
                await reset.WaitAsync(linked.Token).ConfigureAwait(false);
                Current(scope, epoch, null);
                Exception? failure; lock (gate) failure = scope.PublicationFailure;
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return await Call("music.platform.request", new
            { platform, operation, @params = input, candidate = cookie is null ? null : new { cookie, userId }
            }, operation == "sms.send" ? CancellationToken.None : linked.Token,
                smsSend: operation == "sms.send").ConfigureAwait(false);
        });
    }
    internal void Invalidate(string? platform = null)
    {
        string[] targets;
        lock (gate) targets = platform is null ? scopes.Keys.ToArray() : [platform];
        foreach (var target in targets)
        {
            CancellationTokenSource? previousReads = null;
            lock (gate)
            {
                if (closed) return;
                if (!scopes.TryGetValue(target, out var scope)) continue;
                long epoch = ++scope.Epoch;
                previousReads = scope.Reads;
                scope.Reads = new();
                scope.Initialized = false; scope.Published = null;
                scope.Reset = Start(async () =>
                {
                    try { await Publish(target, scope, epoch, null, CancellationToken.None).ConfigureAwait(false); }
                    catch (MusicPlatformException error) when (error.Code is "STALE_SESSION" or "CLOSED")
                    { /* A newer publication or final close owns the physical reset. */ }
                    catch (Exception error)
                    {
                        lock (gate) if (scope.Epoch == epoch) scope.PublicationFailure = error;
                    }
                    return JsonSerializer.SerializeToElement(new { invalidated = true });
                });
            }
            if (previousReads is not null)
            {
                try { previousReads.Cancel(); }
                catch (AggregateException) { }
                finally { previousReads.Dispose(); }
            }
        }
    }
    internal Task<JsonElement> ClearAsync(string? platform, CancellationToken token) =>
        Start(() => Call("music.platform.cache.clear", new { platform }, token));
    internal async Task DrainAsync()
    {
        try { await DrainActiveAsync().ConfigureAwait(false); }
        finally
        {
            Task? closing; lock (gate) closing = disposal;
            if (closing is not null) await closing.ConfigureAwait(false);
        }
    }
    private async Task DrainActiveAsync()
    {
        while (true)
        {
            Task[] work; lock (gate) work = active.ToArray();
            if (work.Length == 0)
            {
                Exception? failure;
                lock (gate) failure = scopes.Values.Select(scope => scope.PublicationFailure).FirstOrDefault(error => error is not null);
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                return;
            }
            try { await Task.WhenAll(work).ConfigureAwait(false); } catch { }
        }
    }
    public ValueTask DisposeAsync()
    {
        CancellationTokenSource[] toCancel;
        TaskCompletionSource cancelDone;
        Task task;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            toCancel = new CancellationTokenSource[scopes.Count];
            int index = 0;
            foreach (var scope in scopes.Values)
            {
                scope.Epoch++;
                toCancel[index++] = scope.Reads;
            }
            cancelDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            task = disposal = Close(cancelDone.Task);
        }
        try
        {
            foreach (var reads in toCancel)
            {
                try { reads.Cancel(); }
                catch (AggregateException) { }
                catch (ObjectDisposedException) { }
            }
        }
        finally { cancelDone.TrySetResult(); }
        return new(task);
    }
    private async Task Close(Task cancellationSettled)
    {
        await Task.Yield();
        await cancellationSettled.ConfigureAwait(false);
        // Publication acknowledgements include Node's physical cancellation drain.
        var resets = scopes.Select(async pair =>
        {
            await pair.Value.Publication.WaitAsync().ConfigureAwait(false);
            try
            {
                if (runtime is IRuntimeLifetime { ShutdownCompletion: { } shutdown })
                    await shutdown.ConfigureAwait(false);
                else
                {
                    try
                    {
                        Published(await Call("music.platform.session.update",
                            new { platform = pair.Key, session = (object?)null }, CancellationToken.None).ConfigureAwait(false));
                    }
                    catch (MusicPlatformException error) when (error.Code == "CLOSED" &&
                        runtime is IRuntimeLifetime { ShutdownCompletion: not null })
                    {
                        await ((IRuntimeLifetime)runtime).ShutdownCompletion!.ConfigureAwait(false);
                    }
                }
                pair.Value.PublicationFailure = null;
            }
            finally { pair.Value.Publication.Release(); }
        }).ToArray();
        try { await Task.WhenAll(resets).ConfigureAwait(false); }
        finally
        {
            try { await DrainActiveAsync().ConfigureAwait(false); }
            finally { foreach (var scope in scopes.Values) { scope.Reads.Dispose(); scope.Publication.Dispose(); } }
        }
    }
}
