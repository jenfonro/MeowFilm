using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class RuntimeRecoveryTests
{
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Runtime recovery assertion"); }
    private sealed class Session : IRuntimeSession
    {
        public bool IsConnected { get; set; } = true;
        public int Calls, Disposals;
        public bool FailDisposal;
        public Task<JsonElement>? Response;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; return Response ?? Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true })); }
        public ValueTask DisposeAsync()
        {
            Disposals++; IsConnected = false;
            return FailDisposal ? ValueTask.FromException(new Exception("synthetic-private")) : ValueTask.CompletedTask;
        }
    }
    private sealed class Scope : IRuntimeRecoveryScope
    {
        public int Prepares, Resumes;
        public Task? Hold;
        public bool Fail;
        public async Task PrepareRuntimeRecoveryAsync(CancellationToken cancellationToken)
        {
            Prepares++;
            if (Hold is not null) await Hold.WaitAsync(cancellationToken);
            if (Fail) throw new Exception("synthetic-private");
        }
        public void ResumeAfterRuntimeRecovery() => Resumes++;
    }
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-recovery: dormant healthy and late registration cannot trigger a replacement", async () =>
        {
            int factories = 0;
            await using var runtime = new OnDemandRuntimeClient(_ => { factories++; return Task.FromResult<IRuntimeSession>(new Session()); });
            Check(runtime.State == RuntimeConnectionState.Dormant && runtime.Generation == 0);
            await BridgeTests.Expect("INVALID_MESSAGE", () => runtime.RetryAsync());
            var scope = new Scope(); runtime.RegisterRecoveryScope(scope);
            BridgeTests.Throws("INVALID_MESSAGE", () => runtime.RegisterRecoveryScope(scope));
            await runtime.CallAsync("film.script.status");
            await BridgeTests.Expect("INVALID_MESSAGE", () => runtime.RetryAsync());
            BridgeTests.Throws("INVALID_MESSAGE", () => runtime.RegisterRecoveryScope(new Scope()));
            Check(factories == 1 && scope.Prepares == 0 && runtime.State == RuntimeConnectionState.Ready);
        });
        yield return new("runtime-recovery: failed startup explicit retry never replays the failed business call", async () =>
        {
            int factories = 0; var next = new Session(); var scope = new Scope();
            await using var runtime = new OnDemandRuntimeClient(_ => ++factories == 1
                ? Task.FromException<IRuntimeSession>(new Exception("synthetic-private")) : Task.FromResult<IRuntimeSession>(next));
            runtime.RegisterRecoveryScope(scope);
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.load"));
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.load"));
            Check(factories == 1 && runtime.CanRetry);
            await runtime.RetryAsync();
            Check(factories == 2 && next.Calls == 0 && scope.Prepares == 1 && scope.Resumes == 1 && runtime.Generation == 2);
            await runtime.CallAsync("music.script.status"); Check(next.Calls == 1);
        });
        yield return new("runtime-recovery: callback drain fences new calls and old successful replies", async () =>
        {
            var old = new Session(); var next = new Session(); int factories = 0;
            var hold = Pending<bool>(); var response = Pending<JsonElement>(); var scope = new Scope { Hold = hold.Task };
            await using var runtime = new OnDemandRuntimeClient(_ => Task.FromResult<IRuntimeSession>(++factories == 1 ? old : next));
            runtime.RegisterRecoveryScope(scope);
            await runtime.CallAsync("music.script.status"); old.Response = response.Task;
            var late = runtime.CallAsync("music.script.invoke"); old.IsConnected = false;
            var retry = runtime.RetryAsync();
            Check(!retry.IsCompleted && factories == 1 && old.Disposals == 1);
            await BridgeTests.Expect("CLOSED", () => runtime.CallAsync("music.script.status"));
            hold.SetResult(true); await retry;
            response.SetResult(JsonSerializer.SerializeToElement(new { ok = true }));
            await BridgeTests.Expect("CLOSED", () => late);
            Check(scope.Resumes == 1 && next.Calls == 0);
        });
        yield return new("runtime-recovery: concurrent explicit retries share work and cancelled UI wait cannot abandon it", async () =>
        {
            int factories = 0; var handshake = Pending<IRuntimeSession>(); CancellationToken owner = default;
            await using var runtime = new OnDemandRuntimeClient(token =>
            { owner = token; return ++factories == 1 ? Task.FromException<IRuntimeSession>(new Exception()) : handshake.Task; });
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("film.script.status"));
            using var caller = new CancellationTokenSource();
            var first = runtime.RetryAsync(caller.Token); var second = runtime.RetryAsync();
            caller.Cancel();
            try { await first; throw new Exception("Cancellation required"); } catch (OperationCanceledException) { }
            Check(!owner.IsCancellationRequested && factories == 2 && !second.IsCompleted);
            handshake.SetResult(new Session()); await second;
            Check(runtime.State == RuntimeConnectionState.Ready);
        });
        yield return new("runtime-recovery: failed native reconciliation blocks startup and permits explicit reconciliation retry", async () =>
        {
            int factories = 0; var scope = new Scope { Fail = true };
            await using var runtime = new OnDemandRuntimeClient(_ => ++factories == 1
                ? Task.FromException<IRuntimeSession>(new Exception()) : Task.FromResult<IRuntimeSession>(new Session()));
            runtime.RegisterRecoveryScope(scope);
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.status"));
            await BridgeTests.Expect("INTERNAL", () => runtime.RetryAsync());
            Check(factories == 1 && scope.Resumes == 0);
            scope.Fail = false; await runtime.RetryAsync();
            Check(factories == 2 && scope.Prepares == 2 && scope.Resumes == 1);
        });
        yield return new("runtime-recovery: failed actual retirement never masquerades as release on a second attempt", async () =>
        {
            int factories = 0; var session = new Session { FailDisposal = true };
            var runtime = new OnDemandRuntimeClient(_ => { factories++; return Task.FromResult<IRuntimeSession>(session); });
            await runtime.CallAsync("film.script.status"); session.IsConnected = false;
            await BridgeTests.Expect("INTERNAL", () => runtime.RetryAsync());
            await BridgeTests.Expect("INTERNAL", () => runtime.RetryAsync());
            Check(factories == 1 && session.Disposals == 1);
            await BridgeTests.Expect("INTERNAL", () => runtime.DisposeAsync().AsTask());
        });
        yield return new("runtime-recovery: shutdown retires a late new handshake without resuming scopes", async () =>
        {
            int factories = 0; var handshake = Pending<IRuntimeSession>(); CancellationToken owner = default; var scope = new Scope();
            var runtime = new OnDemandRuntimeClient(token =>
            { owner = token; return ++factories == 1 ? Task.FromException<IRuntimeSession>(new Exception()) : handshake.Task; });
            runtime.RegisterRecoveryScope(scope);
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("reader.rules.open"));
            var retry = runtime.RetryAsync(); var close = runtime.DisposeAsync().AsTask();
            Check(owner.IsCancellationRequested && !close.IsCompleted);
            var late = new Session(); handshake.SetResult(late);
            await BridgeTests.Expect("CLOSED", () => retry); await close;
            Check(late.Disposals == 1 && late.Calls == 0 && scope.Resumes == 0);
        });
    }
}
