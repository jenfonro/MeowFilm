using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class AppRuntimeClientTests
{
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Value(int number) => JsonSerializer.SerializeToElement(new { number });

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("app-runtime: only Start launches; calls before it never start a session", async () =>
        {
            int launches = 0;
            var port = new AppRuntimeClient(_ =>
            {
                launches++;
                throw new InvalidOperationException("must not run");
            });
            BridgeTests.Equal(false, port.HasStarted);
            BridgeTests.Equal(RuntimeConnectionState.Dormant, port.State);
            await BridgeTests.Expect("CLOSED", () => port.CallAsync("film.script.status"));
            BridgeTests.Equal(false, port.HasStarted);
            await port.DisposeAsync();
            await port.DisposeAsync();
            BridgeTests.Equal(0, launches);
            await BridgeTests.Expect("CLOSED", () => port.CallAsync("film.script.status"));
            try { port.Start(); throw new InvalidOperationException("expected closed"); }
            catch (BridgeException error) when (error.Code == "CLOSED") { }
        });
        yield return new("app-runtime: one Start owns the session every caller shares", async () =>
        {
            int launches = 0;
            var release = Pending<IRuntimeSession>();
            var session = new Session();
            var port = new AppRuntimeClient(_ => { launches++; return release.Task; });
            port.Start();
            BridgeTests.Equal(RuntimeConnectionState.Starting, port.State);
            try { port.Start(); throw new InvalidOperationException("expected a single start"); }
            catch (BridgeException error) when (error.Code == "INVALID_MESSAGE") { }
            var first = port.CallAsync("film.script.status");
            var second = port.CallAsync("music.script.sources.list");
            BridgeTests.Equal(1, launches);
            release.SetResult(session);
            await Task.WhenAll(first, second);
            BridgeTests.Equal(2, session.Calls);
            await port.DisposeAsync();
            await port.DisposeAsync();
            BridgeTests.Equal(1, session.Disposals);
        });
        yield return new("app-runtime: one cancelled caller does not cancel the App's startup", async () =>
        {
            CancellationToken owner = default;
            var release = Pending<IRuntimeSession>();
            await using var port = new AppRuntimeClient(token => { owner = token; return release.Task; });
            port.Start();
            using var caller = new CancellationTokenSource();
            var cancelled = port.CallAsync("film.script.status", cancellationToken: caller.Token);
            var retained = port.CallAsync("film.script.status");
            caller.Cancel();
            try { await cancelled; throw new InvalidOperationException("expected cancellation"); }
            catch (OperationCanceledException) { }
            BridgeTests.Equal(false, owner.IsCancellationRequested);
            var session = new Session();
            release.SetResult(session);
            await retained;
            BridgeTests.Equal(1, session.Calls);
        });
        yield return new("app-runtime: pre-cancel and malformed method never reach the session", async () =>
        {
            var session = new Session();
            await using var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await port.CallAsync("film.script.status", cancellationToken: cancellation.Token);
                throw new InvalidOperationException("expected cancellation");
            }
            catch (OperationCanceledException) { }
            await BridgeTests.Expect("INVALID_MESSAGE", () => port.CallAsync("bad method"));
            BridgeTests.Equal(0, session.Calls);
        });
        yield return new("app-runtime: shutdown owns a late factory result and rejects its pending calls", async () =>
        {
            CancellationToken owner = default;
            var release = Pending<IRuntimeSession>();
            var port = new AppRuntimeClient(token => { owner = token; return release.Task; });
            port.Start();
            var call = port.CallAsync("film.script.status");
            var close = port.DisposeAsync().AsTask();
            BridgeTests.Equal(true, owner.IsCancellationRequested);
            BridgeTests.Equal(false, close.IsCompleted);
            var session = new Session();
            release.SetResult(session);
            await close;
            await BridgeTests.Expect("CLOSED", () => call);
            BridgeTests.Equal(0, session.Calls);
            BridgeTests.Equal(1, session.Disposals);
        });
        yield return new("app-runtime: failed startup is sanitized and not retried by later calls", async () =>
        {
            int launches = 0;
            await using var port = new AppRuntimeClient(_ =>
            {
                launches++;
                throw new InvalidOperationException("synthetic-private-path-and-session");
            });
            port.Start();
            await BridgeTests.Expect("INTERNAL", () => port.CallAsync("film.script.status"));
            await BridgeTests.Expect("INTERNAL", () => port.CallAsync("film.script.status"));
            BridgeTests.Equal(1, launches);
        });
        yield return new("app-runtime: pending response cannot publish after product disposal", async () =>
        {
            var result = Pending<JsonElement>();
            var session = new Session { Response = result.Task };
            var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            var call = port.CallAsync("film.script.invoke");
            await port.DisposeAsync();
            result.SetResult(Value(3));
            await BridgeTests.Expect("CLOSED", () => call);
            BridgeTests.Equal(1, session.Disposals);
        });
        yield return new("app-runtime: disposal failure is observed and is never retried as a second release", async () =>
        {
            var session = new Session { FailDisposal = true };
            var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            await port.CallAsync("film.script.status");
            await BridgeTests.Expect("INTERNAL", () => port.DisposeAsync().AsTask());
            await BridgeTests.Expect("INTERNAL", () => port.DisposeAsync().AsTask());
            BridgeTests.Equal(1, session.Disposals);
        });
    }

    private sealed class Session : IRuntimeSession
    {
        public int Calls, Disposals;
        public Task<JsonElement>? Response;
        public bool FailDisposal;
        public Task<JsonElement> CallAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Response ?? Task.FromResult(Value(Calls));
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return FailDisposal ? ValueTask.FromException(new InvalidOperationException("synthetic-private"))
                : ValueTask.CompletedTask;
        }
    }
}
