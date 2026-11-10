using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class AppRuntimeConnectionTests
{
    private const string ImageReference = "/v1/tmdb/images/w500/poster.jpg";

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("capture never starts a dormant host and a cancelled waiter cannot cancel its shared factory", async () =>
        {
            var ready = new TaskCompletionSource<IRuntimeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken owner = default;
            int launches = 0;
            await using var host = new AppRuntimeConnectionHost(token => { launches++; owner = token; return ready.Task; });
            await Error("CLOSED", () => host.CaptureConnectionAsync());
            Check(launches == 0, "only App Start may create the runtime epoch");
            host.Start();
            using var caller = new CancellationTokenSource();
            var cancelled = host.CaptureConnectionAsync(caller.Token);
            var retained = host.CaptureConnectionAsync();
            caller.Cancel();
            try { await cancelled; throw new InvalidOperationException("Expected caller cancellation"); }
            catch (OperationCanceledException) { }
            Check(launches == 1 && !owner.IsCancellationRequested && !retained.IsCompleted,
                "capture cancellation affects only one waiter, not the trusted factory");
            var session = new EpochSession(owner, 41002);
            ready.TrySetResult(session);
            var connection = await retained;
            Check(ReferenceEquals(connection.Client, session) && connection.BusinessOrigin == session.Origin &&
                connection.Revoked == session.Revoked && connection.Revoked.CanBeCanceled,
                "capture returns one actual fixed client origin and owner capability");
        });
        yield return Test("a captured client cannot silently follow reconnection or inherit a new epoch origin", async () =>
        {
            int launches = 0;
            EpochSession? first = null, next = null;
            await using var host = new AppRuntimeConnectionHost(token =>
            {
                var session = new EpochSession(token, ++launches == 1 ? 41002 : 41003);
                if (launches == 1) first = session; else next = session;
                return Task.FromResult<IRuntimeSession>(session);
            });
            host.Start();
            var old = await host.CaptureConnectionAsync();
            first!.Crash();
            Check(old.Revoked.IsCancellationRequested && host.CanRetry, "actual epoch failure revokes old capabilities");
            await host.RetryAsync();
            var current = await host.CaptureConnectionAsync();
            Check(first.Disposals == 1 && launches == 2 && !current.Revoked.IsCancellationRequested &&
                !ReferenceEquals(current.Client, old.Client) && current.BusinessOrigin.Port == 41003 && old.BusinessOrigin.Port == 41002,
                "recovery publishes a new triple only after retiring the old owner");
            await Error("CLOSED", () => old.Client.CallAsync("film.session.snapshot"));
            Check(next!.Calls == 0, "an old borrowed client never forwards to the new connection");
            await current.Client.CallAsync("film.session.snapshot");
            Check(next.Calls == 1, "the new epoch is independently callable");
        });
        yield return Test("host disposal revokes borrowed connections before awaiting the session physical drain", async () =>
        {
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EpochSession? session = null;
            var host = new AppRuntimeConnectionHost(token => Task.FromResult<IRuntimeSession>(
                session = new(token, 41002) { Drain = drain.Task }));
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            try
            {
                var close = host.DisposeAsync().AsTask();
                Check(captured.Revoked.IsCancellationRequested && !close.IsCompleted,
                    "revocation is immediate but does not pretend physical close is finished");
                await Error("CLOSED", () => host.CaptureConnectionAsync());
                drain.TrySetResult(); await close; await host.DisposeAsync();
                Check(session!.Disposals == 1, "one actual session retirement owns repeated host disposal");
            }
            finally { drain.TrySetResult(); await host.DisposeAsync(); }
        });
        yield return Test("an unrevocable capture is rejected and its actual session is disposed", async () =>
        {
            var session = new EpochSession(default, 41002) { Unrevocable = true };
            var ready = new TaskCompletionSource<IRuntimeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new AppRuntimeConnectionHost(_ => ready.Task);
            host.Start();
            var capture = host.CaptureConnectionAsync();
            ready.SetResult(session);
            await Error("INVALID_RESPONSE", () => capture);
            Check(session.Disposals == 1, "failed trusted capture does not strand its factory result");
        });
        yield return Test("image forwarding preserves the reference budget and bytes without starting a dormant host", async () =>
        {
            int launches = 0;
            byte[] bytes = [1, 2, 3];
            EpochSession? session = null;
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                launches++;
                session = new(owner, 41002)
                {
                    ImageReader = (reference, budget, token) =>
                    {
                        Check(reference == ImageReference && budget == bytes.Length && token.CanBeCanceled,
                            "the image session receives the exact validated reference budget and linked token");
                        return Task.FromResult(bytes);
                    }
                };
                return Task.FromResult<IRuntimeSession>(session);
            });
            IRuntimeImageClient images = host;
            await Error("CLOSED", () => images.ReadImageAsync(ImageReference, bytes.Length));
            await Error("INVALID_REQUEST", () => images.ReadImageAsync("https://example.invalid/poster.jpg", bytes.Length));
            await Error("INVALID_REQUEST", () => images.ReadImageAsync(ImageReference, 0));
            Check(launches == 0, "image reads and input validation never implicitly start an App runtime");
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            await Error("INVALID_REQUEST", () => images.ReadImageAsync("/v1/tmdb/images/w500/../poster.jpg", bytes.Length));
            await Error("INVALID_REQUEST", () => images.ReadImageAsync(ImageReference, -1));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await images.ReadImageAsync(ImageReference, bytes.Length, cancelled.Token); throw new InvalidOperationException("Expected caller cancellation"); }
            catch (OperationCanceledException) { }
            Check(session!.ImageReads == 0, "invalid and pre-cancelled image reads never reach the session");
            Check(ReferenceEquals(await images.ReadImageAsync(ImageReference, bytes.Length), bytes),
                "successful image bytes are returned from the actual App runtime without substitution");
            var retained = await host.CaptureConnectionAsync();
            Check(launches == 1 && session.ImageReads == 1 && ReferenceEquals(retained.Client, captured.Client) &&
                retained.BusinessOrigin == captured.BusinessOrigin && retained.Revoked == captured.Revoked &&
                !captured.Revoked.IsCancellationRequested, "ordinary images do not replace or revoke the captured media capability");
        });
        yield return Test("image caller cancellation waits for the physical reader and cannot revoke its shared epoch", async () =>
        {
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] late = [4, 5, 6];
            EpochSession? session = null;
            await using var host = new AppRuntimeConnectionHost(owner => Task.FromResult<IRuntimeSession>(
                session = new(owner, 41002)
                {
                    Drain = response.Task,
                    ImageReader = (_, _, token) => { entered.TrySetResult(token); return response.Task; }
                }));
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            using var caller = new CancellationTokenSource();
            IRuntimeImageClient images = host;
            var read = images.ReadImageAsync(ImageReference, late.Length, caller.Token);
            try
            {
                var readerToken = await entered.Task;
                caller.Cancel();
                Check(readerToken.IsCancellationRequested && !read.IsCompleted && !captured.Revoked.IsCancellationRequested &&
                    session!.Disposals == 0, "cancellation reaches the reader but neither abandons its drain nor closes the epoch");
                response.TrySetResult(late);
                try { await read; throw new InvalidOperationException("Expected caller cancellation"); }
                catch (OperationCanceledException error)
                { Check(error.CancellationToken == caller.Token, "the completed image read reports the original borrower cancellation"); }
                Check(late.All(value => value == 0), "cancelled image results are cleared after the actual reader completes");
                var retained = await host.CaptureConnectionAsync();
                Check(ReferenceEquals(retained.Client, captured.Client) && retained.Revoked == captured.Revoked &&
                    !captured.Revoked.IsCancellationRequested && host.State == RuntimeConnectionState.Ready && session!.ImageReads == 1,
                    "cancelling one image borrower leaves the same fixed epoch available");
            }
            finally { response.TrySetResult(late); }
        });
        yield return Test("image forwarding drains a retiring epoch and never redirects its pending result to the replacement", async () =>
        {
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] late = [7, 8, 9], fresh = [10, 11, 12];
            int launches = 0;
            EpochSession? first = null, next = null;
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                var session = new EpochSession(owner, ++launches == 1 ? 41002 : 41003);
                if (launches == 1)
                {
                    first = session;
                    session.Drain = response.Task;
                    session.ImageReader = (_, _, token) => { entered.TrySetResult(token); return response.Task; };
                }
                else
                {
                    next = session;
                    session.ImageReader = (_, _, _) => Task.FromResult(fresh);
                }
                return Task.FromResult<IRuntimeSession>(session);
            });
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            IRuntimeImageClient images = host;
            var read = images.ReadImageAsync(ImageReference, late.Length);
            try
            {
                var readerToken = await entered.Task;
                var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = readerToken.Register(() => cancellation.TrySetResult());
                first!.Crash();
                var recovery = host.RetryAsync();
                await cancellation.Task;
                Check(captured.Revoked.IsCancellationRequested && !read.IsCompleted && !recovery.IsCompleted && launches == 1,
                    "recovery revokes the old capability but cannot publish a new epoch before image drain");
                await Error("CLOSED", () => images.ReadImageAsync(ImageReference, fresh.Length));
                response.TrySetResult(late);
                await Error("CLOSED", () => read);
                await recovery;
                var current = await host.CaptureConnectionAsync();
                Check(late.All(value => value == 0) && first.Disposals == 1 && launches == 2 &&
                    !ReferenceEquals(current.Client, captured.Client) && current.BusinessOrigin.Port == 41003 &&
                    captured.BusinessOrigin.Port == 41002 && !current.Revoked.IsCancellationRequested,
                    "the retired image result is cleared and cannot inherit the replacement origin or capability");
                Check(ReferenceEquals(await images.ReadImageAsync(ImageReference, fresh.Length), fresh) &&
                    first.ImageReads == 1 && next!.ImageReads == 1,
                    "only a new explicit image read may use the replacement epoch");
            }
            finally { response.TrySetResult(late); }
        });
    }

    private static TestCase Test(string name, Func<Task> run) => new("app-runtime-capture: " + name, run);
    private sealed class EpochSession : IRuntimeSession, IRuntimeConnectionSource
    {
        private readonly CancellationTokenSource lifetime;
        internal readonly Uri Origin;
        internal CancellationToken Revoked { get; }
        internal int Calls, ImageReads, Disposals;
        internal bool Unrevocable;
        internal Task Drain = Task.CompletedTask;
        internal Func<string, int, CancellationToken, Task<byte[]>>? ImageReader;
        private Task? disposal;
        internal EpochSession(CancellationToken owner, int port)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
            Revoked = lifetime.Token; Origin = new($"http://127.0.0.1:{port}/");
        }
        public bool IsConnected => !Revoked.IsCancellationRequested;
        internal void Crash() => lifetime.Cancel();
        public Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeConnection(this, Origin, Unrevocable ? default : Revoked));
        }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new BridgeException("CLOSED");
            Calls++; return Task.FromResult(FilmRuntimeProbe.Ok(FilmJson.Encode(new { })));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            CallAsync("fixture.upload", cancellationToken: cancellationToken);
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new BridgeException("CLOSED");
            ImageReads++;
            return ImageReader?.Invoke(reference, maximumBytes, cancellationToken) ?? throw new BridgeException("METHOD_UNAVAILABLE");
        }
        public ValueTask DisposeAsync() => new(disposal ??= CloseAsync());
        private async Task CloseAsync()
        {
            Disposals++; await lifetime.CancelAsync(); await Drain;
            lifetime.Dispose();
        }
    }
}
