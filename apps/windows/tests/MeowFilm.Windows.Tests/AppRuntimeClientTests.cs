using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class AppRuntimeClientTests
{
    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonElement Value(int number) =>
        JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3", value = new { number } });

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
            await BridgeTests.Expect("CLOSED", () => port.CallAsync("music.script.sources.list"));
            BridgeTests.Equal(false, port.HasStarted);
            await port.DisposeAsync();
            await port.DisposeAsync();
            BridgeTests.Equal(0, launches);
            await BridgeTests.Expect("CLOSED", () => port.CallAsync("music.script.sources.list"));
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
            var first = port.CallAsync("music.script.sources.list");
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
            var cancelled = port.CallAsync("music.script.sources.list", cancellationToken: caller.Token);
            var retained = port.CallAsync("music.script.sources.list");
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
                await port.CallAsync("music.script.sources.list", cancellationToken: cancellation.Token);
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
            var call = port.CallAsync("music.script.sources.list");
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
            await BridgeTests.Expect("INTERNAL", () => port.CallAsync("music.script.sources.list"));
            await BridgeTests.Expect("INTERNAL", () => port.CallAsync("music.script.sources.list"));
            BridgeTests.Equal(1, launches);
        });
        yield return new("app-runtime: pending response cannot publish after product disposal", async () =>
        {
            var result = Pending<JsonElement>();
            var session = new Session { Response = result.Task };
            var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            var call = port.CallAsync("music.script.invoke");
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
            await port.CallAsync("music.script.sources.list");
            await BridgeTests.Expect("INTERNAL", () => port.DisposeAsync().AsTask());
            await BridgeTests.Expect("INTERNAL", () => port.DisposeAsync().AsTask());
            BridgeTests.Equal(1, session.Disposals);
        });
        foreach (bool retry in new[] { false, true })
            yield return new($"app-runtime: {(retry ? "retry" : "shutdown")} revokes linked borrowers outside the owner lock and drains after callback failure", async () =>
            {
                var drain = Pending<bool>();
                var session = new Session { DisposalDrain = drain.Task };
                CancellationToken owner = default;
                int launches = 0;
                var port = new AppRuntimeClient(token =>
                {
                    launches++; owner = token;
                    return Task.FromResult<IRuntimeSession>(session);
                });
                port.Start();
                using var borrowed = CancellationTokenSource.CreateLinkedTokenSource(owner);
                Task? reentered = null;
                bool callbackFinished = false, remainingCallback = false;
                using var remaining = borrowed.Token.Register(() => remainingCallback = true);
                using var failing = borrowed.Token.Register(() =>
                {
                    // A different thread must acquire the owner gate while cancellation is in progress.
                    Task.Run(() =>
                    {
                        BridgeTests.Equal(retry ? RuntimeConnectionState.Recovering : RuntimeConnectionState.Closed, port.State);
                        reentered = retry ? port.RetryAsync() : port.DisposeAsync().AsTask();
                    }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    callbackFinished = true;
                    throw new InvalidOperationException("synthetic-private-cancellation");
                });
                if (retry) session.IsConnected = false;
                try
                {
                    var retirement = retry ? port.RetryAsync() : port.DisposeAsync().AsTask();
                    BridgeTests.Equal(true, borrowed.IsCancellationRequested);
                    BridgeTests.Equal(true, callbackFinished);
                    BridgeTests.Equal(true, remainingCallback);
                    BridgeTests.Equal(true, ReferenceEquals(retirement, reentered));
                    BridgeTests.Equal(false, retirement.IsCompleted);
                    var repeated = retry ? port.RetryAsync() : port.DisposeAsync().AsTask();
                    BridgeTests.Equal(true, ReferenceEquals(retirement, repeated));
                    await BridgeTests.Expect("CLOSED", () => port.CallAsync("music.script.sources.list"));
                    drain.SetResult(true);
                    await BridgeTests.Expect("INTERNAL", () => retirement);
                    await BridgeTests.Expect("INTERNAL", () => retry ? port.RetryAsync() : port.DisposeAsync().AsTask());
                    BridgeTests.Equal(1, launches);
                    BridgeTests.Equal(1, session.Disposals);
                }
                finally
                {
                    drain.TrySetResult(true);
                    await BridgeTests.Expect("INTERNAL", () => port.DisposeAsync().AsTask());
                }
            });
        yield return new("app-runtime: upload and calls preserve complete envelopes and exact source identity", async () =>
        {
            var session = new Session();
            await using var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            var bytes = "const 音乐 = '🎵';"u8.ToArray();
            var reply = await port.UploadSourceAsync("music", "candidate", 9123, bytes, "音乐.js");
            BridgeTests.Equal("music", session.Module);
            BridgeTests.Equal("candidate", session.InstallationId);
            BridgeTests.Equal(9123L, session.Revision);
            BridgeTests.Equal("音乐.js", session.Basename);
            BridgeTests.Equal(true, bytes.SequenceEqual(session.Source.ToArray()));
            BridgeTests.Equal(true, reply.GetProperty("ok").GetBoolean());
            BridgeTests.Equal("music.script.v3", reply.GetProperty("contract").GetString());
            BridgeTests.Equal(1, reply.GetProperty("value").GetProperty("number").GetInt32());
            var failure = JsonSerializer.SerializeToElement(new { ok = false, contract = "music.script.v3", code = "STALE_SOURCE" });
            session.Response = Task.FromResult(failure);
            BridgeTests.Equal(failure.GetRawText(), (await port.CallAsync("music.script.sources.add")).GetRawText());
        });
        yield return new("app-runtime: upload cancellation is independent during startup and active exchanges", async () =>
        {
            var release = Pending<IRuntimeSession>();
            var response = Pending<JsonElement>();
            var session = new Session { UploadResponse = response.Task };
            await using var port = new AppRuntimeClient(_ => release.Task);
            port.Start();
            using var startup = new CancellationTokenSource();
            var beforeStart = port.UploadSourceAsync("cat", "first", 1, "x"u8.ToArray(), cancellationToken: startup.Token);
            startup.Cancel();
            try { await beforeStart; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            release.SetResult(session);
            using var caller = new CancellationTokenSource();
            var upload = port.UploadSourceAsync("cat", "second", 2, "y"u8.ToArray(), cancellationToken: caller.Token);
            await port.CallAsync("music.script.sources.list");
            caller.Cancel();
            BridgeTests.Equal(true, session.UploadCancellation.IsCancellationRequested);
            BridgeTests.Equal(false, upload.IsCompleted);
            response.SetResult(Value(2));
            try { await upload; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            BridgeTests.Equal(1, session.Uploads);
            BridgeTests.Equal(1, session.Calls);
        });
        yield return new("app-runtime: cancelled upload retains borrowed bytes until the actual sender finishes", async () =>
        {
            var response = Pending<JsonElement>();
            var session = new Session { UploadResponse = response.Task };
            await using var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            using var caller = new CancellationTokenSource();
            var bytes = "const 音乐 = '🎵';"u8.ToArray();
            var upload = port.UploadSourceAsync("music", "candidate", 1, bytes, "source.js", caller.Token);
            var returned = ReturnBorrowedBytesAsync();
            caller.Cancel();
            try
            {
                BridgeTests.Equal(true, session.UploadCancellation.IsCancellationRequested);
                BridgeTests.Equal(false, upload.IsCompleted);
                BridgeTests.Equal(false, returned.IsCompleted);
                BridgeTests.Equal(true, session.Source.Span.SequenceEqual("const 音乐 = '🎵';"u8));
                BridgeTests.Equal(true, (await port.CallAsync("music.script.sources.list")).GetProperty("ok").GetBoolean());
            }
            finally { response.TrySetResult(Value(2)); }
            await returned;
            BridgeTests.Equal(true, bytes.All(value => value == 0));

            async Task ReturnBorrowedBytesAsync()
            {
                try { await upload; throw new InvalidOperationException("Expected cancellation"); }
                catch (OperationCanceledException) { }
                finally { Array.Clear(bytes); }
            }
        });
        yield return new("app-runtime: cancelled image waits for physical bytes and clears rejected pixels", async () =>
        {
            var response = Pending<byte[]>();
            var session = new Session { ImageResponse = response.Task };
            await using var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            using var caller = new CancellationTokenSource();
            var image = port.ReadImageAsync("/v1/tmdb/images/w500/a.jpg", 4 * 1024 * 1024, caller.Token);
            caller.Cancel();
            BridgeTests.Equal(true, session.ImageCancellation.IsCancellationRequested);
            BridgeTests.Equal(false, image.IsCompleted);
            byte[] bytes = [1, 2, 3];
            response.SetResult(bytes);
            try { await image; throw new InvalidOperationException("Expected image cancellation"); }
            catch (OperationCanceledException) { }
            BridgeTests.Equal(true, bytes.All(value => value == 0));
        });
        yield return new("app-runtime: shutdown epoch fences a late image and clears its returned bytes", async () =>
        {
            var response = Pending<byte[]>();
            var session = new Session { ImageResponse = response.Task };
            var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            var image = port.ReadImageAsync("/v1/tmdb/images/w500/a.jpg", 4 * 1024 * 1024);
            await port.DisposeAsync();
            BridgeTests.Equal(true, session.ImageCancellation.IsCancellationRequested);
            BridgeTests.Equal(false, image.IsCompleted);
            byte[] bytes = [1, 2, 3]; response.SetResult(bytes);
            await BridgeTests.Expect("CLOSED", () => image);
            BridgeTests.Equal(true, bytes.All(value => value == 0));
        });
        yield return new("app-runtime: shutdown cancels and fences a late successful upload", async () =>
        {
            var response = Pending<JsonElement>();
            var session = new Session { UploadResponse = response.Task };
            var port = new AppRuntimeClient(_ => Task.FromResult<IRuntimeSession>(session));
            port.Start();
            var upload = port.UploadSourceAsync("source", "candidate", 1, "{}"u8.ToArray(), "source.json");
            await port.DisposeAsync();
            BridgeTests.Equal(true, session.UploadCancellation.IsCancellationRequested);
            BridgeTests.Equal(false, upload.IsCompleted);
            response.SetResult(Value(7));
            await BridgeTests.Expect("CLOSED", () => upload);
            BridgeTests.Equal(1, session.Disposals);
        });
    }

    private sealed class Session : IRuntimeSession
    {
        public Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default)
        { ImageCancellation = cancellationToken; return ImageResponse ?? Task.FromResult(new byte[] { 1 }); }

        public int Calls, Uploads, Disposals;
        public Task<JsonElement>? Response;
        public Task<JsonElement>? UploadResponse;
        public string? Module, InstallationId, Basename;
        public long Revision;
        public ReadOnlyMemory<byte> Source;
        public CancellationToken UploadCancellation;
        public bool FailDisposal;
        public bool IsConnected { get; set; } = true;
        public Task? DisposalDrain;
        public Task<byte[]>? ImageResponse;
        public CancellationToken ImageCancellation;
        public Task<JsonElement> CallAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Response ?? Task.FromResult(Value(Calls));
        }
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        {
            Uploads++; Module = module; InstallationId = installationId; Revision = sourceRevision;
            Source = source; Basename = basename; UploadCancellation = cancellationToken;
            return UploadResponse ?? Task.FromResult(Value(Uploads));
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (DisposalDrain is not null) await DisposalDrain.ConfigureAwait(false);
            if (FailDisposal) throw new InvalidOperationException("synthetic-private");
        }
    }
}
