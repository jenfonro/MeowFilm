using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class RuntimeRecoveryTests
{
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Runtime recovery assertion"); }
    private sealed class Session : IRuntimeSession
    {
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Image requests are not part of this fixture.");

        public bool IsConnected { get; set; } = true;
        public int Calls, Uploads, Disposals;
        public bool FailDisposal;
        public Task<JsonElement>? Response;
        public CancellationToken UploadCancellation;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; return Response ?? Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true,
            contract = "music.script.v3", value = new { } })); }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            Uploads++; UploadCancellation = cancellationToken;
            return Response ?? Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true,
                contract = "cat.script.v2", value = new { installationId, sourceRevision } }));
        }
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
        public TaskCompletionSource<bool>? PrepareEntered;
        public Action<CancellationToken>? OnPrepare;
        public bool Fail;
        public async Task PrepareRuntimeRecoveryAsync(CancellationToken cancellationToken)
        {
            Prepares++;
            OnPrepare?.Invoke(cancellationToken);
            PrepareEntered?.TrySetResult(true);
            if (Hold is not null) await Hold.WaitAsync(cancellationToken);
            if (Fail) throw new Exception("synthetic-private");
        }
        public void ResumeAfterRuntimeRecovery() => Resumes++;
    }
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("runtime-recovery: unstarted, healthy and late registration cannot trigger a replacement", async () =>
        {
            int factories = 0;
            await using var runtime = new AppRuntimeClient(_ => { factories++; return Task.FromResult<IRuntimeSession>(new Session()); });
            Check(runtime.State == RuntimeConnectionState.Dormant && runtime.Generation == 0);
            await BridgeTests.Expect("INVALID_MESSAGE", () => runtime.RetryAsync());
            var scope = new Scope(); runtime.RegisterRecoveryScope(scope);
            BridgeTests.Throws("INVALID_MESSAGE", () => runtime.RegisterRecoveryScope(scope));
            runtime.Start();
            await runtime.CallAsync("music.script.sources.list");
            await BridgeTests.Expect("INVALID_MESSAGE", () => runtime.RetryAsync());
            BridgeTests.Throws("INVALID_MESSAGE", () => runtime.RegisterRecoveryScope(new Scope()));
            Check(factories == 1 && scope.Prepares == 0 && runtime.State == RuntimeConnectionState.Ready);
        });
        yield return new("runtime-recovery: failed startup explicit retry never replays the failed business call", async () =>
        {
            int factories = 0; var next = new Session(); var scope = new Scope();
            await using var runtime = new AppRuntimeClient(_ => ++factories == 1
                ? Task.FromException<IRuntimeSession>(new Exception("synthetic-private")) : Task.FromResult<IRuntimeSession>(next));
            runtime.RegisterRecoveryScope(scope);
            runtime.Start();
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.sources.enable"));
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.sources.enable"));
            Check(factories == 1 && runtime.CanRetry);
            await runtime.RetryAsync();
            Check(factories == 2 && next.Calls == 0 && scope.Prepares == 1 && scope.Resumes == 1 && runtime.Generation == 2);
            await runtime.CallAsync("music.script.sources.list"); Check(next.Calls == 1);
        });
        yield return new("runtime-recovery: callback drain fences new calls and old successful replies", async () =>
        {
            var old = new Session(); var next = new Session(); int factories = 0;
            var hold = Pending<bool>(); var response = Pending<JsonElement>(); var prepared = Pending<bool>();
            var scope = new Scope { Hold = hold.Task, PrepareEntered = prepared };
            await using var runtime = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(++factories == 1 ? old : next));
            runtime.RegisterRecoveryScope(scope);
            runtime.Start();
            await runtime.CallAsync("music.script.sources.list"); old.Response = response.Task;
            var late = runtime.CallAsync("music.script.invoke"); old.IsConnected = false;
            var retry = runtime.RetryAsync();
            Check(!retry.IsCompleted && factories == 1 && scope.Resumes == 0);
            await BridgeTests.Expect("CLOSED", () => runtime.CallAsync("music.script.sources.list"));
            // Recovery enters prepare only after the old session's actual disposal completes.
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!retry.IsCompleted && factories == 1 && old.Disposals == 1);
            Check(scope.Prepares == 1 && scope.Resumes == 0);
            await BridgeTests.Expect("CLOSED", () => runtime.CallAsync("music.script.sources.list"));
            hold.SetResult(true); await retry;
            response.SetResult(JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3", value = new { } }));
            await BridgeTests.Expect("CLOSED", () => late);
            Check(scope.Resumes == 1 && next.Calls == 0);
        });
        yield return new("runtime-recovery: concurrent explicit retries share work and cancelled UI wait cannot abandon it", async () =>
        {
            int factories = 0; var handshake = Pending<IRuntimeSession>(); CancellationToken owner = default;
            await using var runtime = new AppRuntimeClient(token =>
            { owner = token; return ++factories == 1 ? Task.FromException<IRuntimeSession>(new Exception()) : handshake.Task; });
            runtime.Start();
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.sources.list"));
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
            await using var runtime = new AppRuntimeClient(_ => ++factories == 1
                ? Task.FromException<IRuntimeSession>(new Exception()) : Task.FromResult<IRuntimeSession>(new Session()));
            runtime.RegisterRecoveryScope(scope);
            runtime.Start();
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.sources.list"));
            await BridgeTests.Expect("INTERNAL", () => runtime.RetryAsync());
            Check(factories == 1 && scope.Resumes == 0);
            scope.Fail = false; await runtime.RetryAsync();
            Check(factories == 2 && scope.Prepares == 2 && scope.Resumes == 1);
        });
        yield return new("runtime-recovery: failed actual retirement never masquerades as release on a second attempt", async () =>
        {
            int factories = 0; var session = new Session { FailDisposal = true };
            var runtime = new AppRuntimeClient(_ => { factories++; return Task.FromResult<IRuntimeSession>(session); });
            runtime.Start();
            await runtime.CallAsync("music.script.sources.list"); session.IsConnected = false;
            await BridgeTests.Expect("INTERNAL", () => runtime.RetryAsync());
            await BridgeTests.Expect("INTERNAL", () => runtime.RetryAsync());
            Check(factories == 1 && session.Disposals == 1);
            await BridgeTests.Expect("INTERNAL", () => runtime.DisposeAsync().AsTask());
        });
        yield return new("runtime-recovery: shutdown retires a late new handshake without resuming scopes", async () =>
        {
            int factories = 0; var handshake = Pending<IRuntimeSession>(); CancellationToken owner = default; var scope = new Scope();
            var runtime = new AppRuntimeClient(token =>
            { owner = token; return ++factories == 1 ? Task.FromException<IRuntimeSession>(new Exception()) : handshake.Task; });
            runtime.RegisterRecoveryScope(scope);
            runtime.Start();
            await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("source.runtime.load"));
            var retry = runtime.RetryAsync(); var close = runtime.DisposeAsync().AsTask();
            Check(owner.IsCancellationRequested && !close.IsCompleted);
            var late = new Session(); handshake.SetResult(late);
            await BridgeTests.Expect("CLOSED", () => retry); await close;
            Check(late.Disposals == 1 && late.Calls == 0 && scope.Resumes == 0);
        });
        yield return new("runtime-recovery: pending lifetime callbacks cannot delay borrowed epoch revocation or finish shutdown early", async () =>
        {
            int factories = 0;
            var session = new Session();
            CancellationTokenSource? borrowed = null;
            var callbackEntered = Pending<bool>();
            using var releaseCallback = new ManualResetEventSlim();
            CancellationTokenRegistration registration = default;
            var scope = new Scope
            {
                OnPrepare = token => registration = token.Register(() =>
                {
                    callbackEntered.TrySetResult(true);
                    releaseCallback.Wait();
                })
            };
            var runtime = new AppRuntimeClient(token =>
            {
                if (++factories == 1) return Task.FromException<IRuntimeSession>(new Exception("synthetic-private"));
                borrowed = CancellationTokenSource.CreateLinkedTokenSource(token);
                return Task.FromResult<IRuntimeSession>(session);
            });
            try
            {
                runtime.RegisterRecoveryScope(scope);
                runtime.Start();
                await BridgeTests.Expect("INTERNAL", () => runtime.CallAsync("music.script.sources.list"));
                await runtime.RetryAsync();
                var close = runtime.DisposeAsync().AsTask();
                Check(borrowed!.IsCancellationRequested);
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(!close.IsCompleted);
                releaseCallback.Set();
                await close;
                Check(factories == 2 && session.Disposals == 1 && scope.Resumes == 1);
            }
            finally
            {
                releaseCallback.Set();
                await runtime.DisposeAsync();
                registration.Dispose();
                borrowed?.Dispose();
            }
        });
        yield return new("runtime-recovery: explicit retry cancels and fences old uploads without replay", async () =>
        {
            var response = Pending<JsonElement>();
            var old = new Session { Response = response.Task }; var next = new Session(); int factories = 0;
            await using var runtime = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(++factories == 1 ? old : next));
            runtime.Start();
            var late = runtime.UploadSourceAsync("cat", "candidate", 1, "x"u8.ToArray());
            old.IsConnected = false;
            await runtime.RetryAsync();
            Check(old.UploadCancellation.IsCancellationRequested && old.Disposals == 1 && next.Uploads == 0);
            Check(!late.IsCompleted);
            response.SetResult(JsonSerializer.SerializeToElement(new { ok = true, contract = "cat.script.v2", value = new { } }));
            await BridgeTests.Expect("CLOSED", () => late);
            var result = await runtime.UploadSourceAsync("cat", "candidate", 2, "y"u8.ToArray());
            Check(result.GetProperty("value").GetProperty("sourceRevision").GetInt64() == 2);
            Check(old.Uploads == 1 && next.Uploads == 1 && factories == 2);
        });
    }
}
