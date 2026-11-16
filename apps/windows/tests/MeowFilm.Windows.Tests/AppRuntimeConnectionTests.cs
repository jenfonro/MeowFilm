using System.Text.Json;
using System.Threading.Channels;
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
            IRuntimeLifetime lifetime = host;
            Check(lifetime.ShutdownCompletion is null, "reading a dormant host's lifetime does not start final shutdown");
            await Error("CLOSED", () => host.CaptureConnectionAsync());
            Check(launches == 0, "only App Start may create the runtime epoch");
            host.Start();
            Check(lifetime.ShutdownCompletion is null, "a pending start is not final shutdown");
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
            IRuntimeLifetime lifetime = host;
            host.Start();
            var old = await host.CaptureConnectionAsync();
            first!.Crash();
            Check(old.Revoked.IsCancellationRequested && host.CanRetry, "actual epoch failure revokes old capabilities");
            Check(lifetime.ShutdownCompletion is null, "epoch revocation does not publish a final shutdown receipt");
            await host.RetryAsync();
            var current = await host.CaptureConnectionAsync();
            Check(lifetime.ShutdownCompletion is null, "recovery retires an epoch without starting final App shutdown");
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
            IRuntimeLifetime lifetime = host;
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            Check(lifetime.ShutdownCompletion is null, "a live host has no final shutdown receipt");
            try
            {
                var close = host.DisposeAsync().AsTask();
                Check(captured.Revoked.IsCancellationRequested && !close.IsCompleted,
                    "revocation is immediate but does not pretend physical close is finished");
                Check(ReferenceEquals(close, lifetime.ShutdownCompletion) &&
                    ReferenceEquals(close, host.DisposeAsync().AsTask()),
                    "the host forwards the same pending App-owned physical drain task");
                await Error("CLOSED", () => host.CaptureConnectionAsync());
                drain.TrySetResult(); await close; await host.DisposeAsync();
                Check(close.IsCompletedSuccessfully && ReferenceEquals(close, lifetime.ShutdownCompletion),
                    "the successful final shutdown receipt remains stable");
                Check(session!.Disposals == 1, "one actual session retirement owns repeated host disposal");
            }
            finally { drain.TrySetResult(); await host.DisposeAsync(); }
        });
        yield return Test("host shutdown receipt retains physical drain failure across repeated close", async () =>
        {
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EpochSession? session = null;
            var host = new AppRuntimeConnectionHost(token => Task.FromResult<IRuntimeSession>(
                session = new(token, 41002) { Drain = drain.Task }));
            IRuntimeLifetime lifetime = host;
            host.Start();
            await host.CaptureConnectionAsync();
            try
            {
                var close = host.DisposeAsync().AsTask();
                Check(ReferenceEquals(close, lifetime.ShutdownCompletion) && !close.IsCompleted,
                    "the receipt waits for the actual session drain");
                drain.TrySetException(new InvalidOperationException("synthetic-private-drain"));
                await Error("INTERNAL", () => close);
                Check(close.IsFaulted && ReferenceEquals(close, lifetime.ShutdownCompletion) &&
                    ReferenceEquals(close, host.DisposeAsync().AsTask()),
                    "a failed physical drain never becomes a successful receipt on retry");
                await Error("INTERNAL", () => host.DisposeAsync().AsTask());
                Check(session!.Disposals == 1, "observing the failed receipt never repeats session disposal");
            }
            finally
            {
                drain.TrySetResult();
                await Error("INTERNAL", () => host.DisposeAsync().AsTask());
            }
        });
        foreach (bool failed in new[] { false, true })
            yield return Test($"host shutdown receipt retains {(failed ? "failed" : "successful")} cleanup of a late factory session", async () =>
            {
                var ready = new TaskCompletionSource<IRuntimeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken owner = default;
                var host = new AppRuntimeConnectionHost(token => { owner = token; return ready.Task; });
                IRuntimeLifetime lifetime = host;
                host.Start();
                var close = host.DisposeAsync().AsTask();
                var session = new EpochSession(owner, 41002) { Drain = drain.Task, DisposalStarted = disposing };
                try
                {
                    Check(owner.IsCancellationRequested && !close.IsCompleted &&
                        ReferenceEquals(close, lifetime.ShutdownCompletion),
                        "shutdown publishes its pending receipt before the factory returns");
                    ready.SetResult(session);
                    await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!close.IsCompleted && session.DisposeCalls == 1 && session.Disposals == 1,
                        "the rejected late session is physically drained once before shutdown can settle");
                    if (failed) drain.SetException(new InvalidOperationException("synthetic-private-drain"));
                    else drain.SetResult();
                    if (failed)
                    {
                        await Error("INTERNAL", () => close);
                        await Error("INTERNAL", () => host.DisposeAsync().AsTask());
                    }
                    else { await close; await host.DisposeAsync(); }
                    Check(close.IsFaulted == failed && close.IsCompletedSuccessfully == !failed &&
                        ReferenceEquals(close, lifetime.ShutdownCompletion) &&
                        ReferenceEquals(close, host.DisposeAsync().AsTask()) &&
                        session.DisposeCalls == 1 && session.Disposals == 1,
                        "the factory cleanup outcome is retained without disposing its session again");
                }
                finally
                {
                    ready.TrySetResult(session); drain.TrySetResult();
                    try { await host.DisposeAsync(); } catch (BridgeException) when (failed) { }
                }
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
            await host.DisposeAsync();
            Check(host.ShutdownCompletion?.IsCompletedSuccessfully == true && session.DisposeCalls == 1,
                "a startup failure with successful cleanup still permits a successful final shutdown");
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
        yield return Test("full-size source images use the quota-free reader and drain caller cancellation without revoking the epoch", async () =>
        {
            const string reference = "/v1/source/images/full-size";
            byte[] bytes = [1, 2, 3], late = [4, 5, 6];
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            int launches = 0;
            EpochSession? session = null;
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                launches++;
                session = new(owner, 41002)
                {
                    CompleteImageReader = (path, token) =>
                    {
                        Check(path == reference && token.CanBeCanceled,
                            "the complete reader receives the exact source reference and linked token without a thumbnail quota");
                        if (session!.ImageReads == 1) return Task.FromResult(bytes);
                        entered.TrySetResult(token); return response.Task;
                    }
                };
                return Task.FromResult<IRuntimeSession>(session);
            });
            IRuntimeImageClient images = host;
            await Error("CLOSED", () => images.ReadImageAsync(reference));
            await Error("INVALID_REQUEST", () => images.ReadImageAsync("https://example.invalid/page.png"));
            Check(launches == 0, "complete image reads never launch the dormant host");
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            await Error("INVALID_REQUEST", () => images.ReadImageAsync(reference + "?private=fixture"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await images.ReadImageAsync(reference, cancelled.Token); throw new InvalidOperationException("Expected caller cancellation"); }
            catch (OperationCanceledException) { }
            Check(session!.ImageReads == 0, "invalid and pre-cancelled reads never reach either reader");
            Check(ReferenceEquals(await images.ReadImageAsync(reference), bytes),
                "the App host forwards the complete image overload without substituting its bytes");
            using var caller = new CancellationTokenSource();
            var read = images.ReadImageAsync(reference, caller.Token);
            try
            {
                var readerToken = await entered.Task;
                caller.Cancel();
                Check(readerToken.IsCancellationRequested && !read.IsCompleted && !captured.Revoked.IsCancellationRequested,
                    "caller cancellation reaches the complete reader but still waits for physical completion");
                response.TrySetResult(late);
                try { await read; throw new InvalidOperationException("Expected caller cancellation"); }
                catch (OperationCanceledException error)
                { Check(error.CancellationToken == caller.Token, "the complete reader reports the original caller cancellation"); }
                Check(late.All(value => value == 0) && session.ImageReads == 2 && session.Disposals == 0 &&
                    host.State == RuntimeConnectionState.Ready && !captured.Revoked.IsCancellationRequested,
                    "late full-size bytes are cleared without disposing the shared epoch or calling the bounded reader");
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
        yield return Test("dormant settings saves never launch or queue notifications for an explicit start", async () =>
        {
            int launches = 0;
            EpochSession? session = null;
            await using var store = new FilmNativeSettingsFixture("film.preferences.v1");
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                launches++;
                return Task.FromResult<IRuntimeSession>(session = new(owner, 41002));
            });
            IRuntimeSettingsNotifications notifications = host;
            var settings = new FilmPlaybackSettings(host, store);
            await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["dormant-first"] });
            await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["dormant-final"] });
            notifications.SettingsChanged("pan.accounts");
            Check(launches == 0 && host.Generation == 0 && host.State == RuntimeConnectionState.Dormant && !host.CanRetry &&
                store.Writes == 2 && (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual(["dormant-final"]),
                "offline saves commit their latest native value without starting or recovering an epoch");
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            Check(launches == 1 && session!.Calls == 0 && !session.TimedCalls.Reader.TryPeek(out _) &&
                ReferenceEquals(captured.Client, session), "an explicit start does not replay any dormant notifications");
            notifications.SettingsChanged("tmdb");
            await (await ReadNotification(session!, "tmdb")).Response;
            Check(session!.Calls == 1 && !session.TimedCalls.Reader.TryPeek(out _) && store.Writes == 2 &&
                (await new FilmPlaybackSettings(host, store).ReadPreferencesAsync(default)).Keywords.SequenceEqual(["dormant-final"]),
                "only a new notification reaches the started epoch and native settings remain the sole saved value");
        });
        yield return Test("failed and recovering epochs drop settings notifications without retry or replay", async () =>
        {
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int launches = 0;
            EpochSession? first = null, next = null;
            await using var store = new FilmNativeSettingsFixture("film.preferences.v1");
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                var session = new EpochSession(owner, ++launches == 1 ? 41002 : 41003);
                if (launches == 1) { first = session; session.Drain = drain.Task; }
                else next = session;
                return Task.FromResult<IRuntimeSession>(session);
            });
            IRuntimeLifetime lifetime = host;
            host.Start();
            var old = await host.CaptureConnectionAsync();
            IRuntimeSettingsNotifications notifications = host;
            var settings = new FilmPlaybackSettings(host, store);
            try
            {
                first!.Crash();
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["failed-save"] });
                notifications.SettingsChanged("cat.sources");
                Check(host.State == RuntimeConnectionState.Failed && host.CanRetry && launches == 1 &&
                    host.Generation == 1 && first.Calls == 0 && first.Disposals == 0,
                    "saving in a failed epoch neither retries nor sends its obsolete connection a notification");
                var recovery = host.RetryAsync();
                Check(host.State == RuntimeConnectionState.Recovering && !recovery.IsCompleted && first.Disposals == 1,
                    "only explicit recovery retires the failed owner and waits for its physical drain");
                Check(lifetime.ShutdownCompletion is null, "an in-progress epoch recovery is not final App shutdown");
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["recovering-save"] });
                notifications.SettingsChanged("pan.accounts");
                Check(launches == 1 && first.Calls == 0 && store.Writes == 2 &&
                    (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual(["recovering-save"]),
                    "recovery does not block local commits or admit notifications to the retiring owner");
                drain.TrySetResult();
                await recovery.WaitAsync(TimeSpan.FromSeconds(5));
                var current = await host.CaptureConnectionAsync();
                Check(launches == 2 && next!.Calls == 0 && !next.TimedCalls.Reader.TryPeek(out _) &&
                    old.Revoked.IsCancellationRequested && !current.Revoked.IsCancellationRequested &&
                    current.BusinessOrigin.Port == 41003, "the replacement receives no failed or recovering notifications");
                notifications.SettingsChanged("douban");
                await (await ReadNotification(next!, "douban")).Response;
                Check(first.Calls == 0 && next!.Calls == 1 && !next.TimedCalls.Reader.TryPeek(out _),
                    "only a fresh notification can use the replacement epoch");
            }
            finally { drain.TrySetResult(); }
        });
        yield return Test("closing a starting host drops waiting notifications and later saves never reopen it", async () =>
        {
            var ready = new TaskCompletionSource<IRuntimeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            int launches = 0;
            EpochSession? session = null;
            await using var store = new FilmNativeSettingsFixture("film.preferences.v1");
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                launches++; session = new(owner, 41002); return ready.Task;
            });
            host.Start();
            IRuntimeSettingsNotifications notifications = host;
            var settings = new FilmPlaybackSettings(host, store);
            try
            {
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["starting-save"] });
                Check(host.State == RuntimeConnectionState.Starting && session!.Calls == 0 && store.Writes == 1,
                    "a save does not wait for its starting epoch to become callable");
                var close = host.DisposeAsync().AsTask();
                Check(host.State == RuntimeConnectionState.Closed && session!.Revoked.IsCancellationRequested && !close.IsCompleted,
                    "closing revokes the startup owner before its late factory physically settles");
                notifications.SettingsChanged("cat.sources");
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["closing-save"] });
                ready.TrySetResult(session!);
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["closed-save"] });
                notifications.SettingsChanged("pan.accounts");
                Check(launches == 1 && host.Generation == 1 && host.State == RuntimeConnectionState.Closed && !host.CanRetry &&
                    session!.Calls == 0 && session.Disposals == 1 && !session.TimedCalls.Reader.TryPeek(out _) &&
                    store.Writes == 3 && (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual(["closed-save"]),
                    "waiting and post-close notifications are dropped while local saves still commit their final value");
            }
            finally { ready.TrySetResult(session!); }
        });
        yield return Test("settings notifications waiting on a failed startup never follow its replacement", async () =>
        {
            var ready = new TaskCompletionSource<IRuntimeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            int launches = 0;
            EpochSession? next = null;
            await using var store = new FilmNativeSettingsFixture("film.preferences.v1");
            await using var host = new AppRuntimeConnectionHost(owner => ++launches == 1 ? ready.Task :
                Task.FromResult<IRuntimeSession>(next = new(owner, 41003)));
            host.Start();
            IRuntimeSettingsNotifications notifications = host;
            var settings = new FilmPlaybackSettings(host, store);
            try
            {
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["before-startup-failure"] });
                notifications.SettingsChanged("cat.sources");
                Check(host.State == RuntimeConnectionState.Starting && store.Writes == 1,
                    "ordinary saves commit while notifications wait on the same starting capture");
                var startup = host.CallAsync("film.session.snapshot");
                ready.TrySetException(new BridgeException("PERMISSION_DENIED"));
                await Error("PERMISSION_DENIED", () => startup);
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = ["after-startup-failure"] });
                Check(host.State == RuntimeConnectionState.Failed && host.CanRetry && launches == 1,
                    "a failed capture and later saves never recover the factory");
                await host.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(next!.Calls == 0 && launches == 2 && host.Generation == 2 &&
                    (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual(["after-startup-failure"]),
                    "explicit recovery does not replay startup notifications and the final native settings remain saved");
                notifications.SettingsChanged("film.rules");
                await (await ReadNotification(next!, "film.rules")).Response;
                Check(next!.Calls == 1 && !next.TimedCalls.Reader.TryPeek(out _) && store.Writes == 2,
                    "only the new epoch's explicit notification is sent after a failed startup");
            }
            finally { ready.TrySetException(new BridgeException("CLOSED")); }
        });
        foreach (bool starting in new[] { false, true })
            yield return Test($"ordinary settings saves in a {(starting ? "starting" : "ready")} epoch send only block names over HTTP", async () =>
            {
                var ready = new TaskCompletionSource<IRuntimeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var server = new RuntimeHttpClientTests.Server();
                await using var http = new RuntimeHttpClient(server.BaseUrl);
                await using var store = new FilmNativeSettingsFixture("film.preferences.v1");
                int launches = 0;
                EpochSession? session = null;
                await using var host = new AppRuntimeConnectionHost(owner =>
                {
                    launches++;
                    session = new(owner, new Uri(server.BaseUrl).Port) { TimedCaller = http.CallAsync };
                    return starting ? ready.Task : Task.FromResult<IRuntimeSession>(session);
                });
                host.Start();
                var settings = new FilmPlaybackSettings(host, store);
                try
                {
                    Check(host.State == (starting ? RuntimeConnectionState.Starting : RuntimeConnectionState.Ready),
                        "the save starts in the selected actual host lifecycle state");
                    var saved = await settings.SavePreferencesAsync(FilmPreferences.Defaults with
                        { Keywords = ["synthetic-local-setting", "本地配置🎬"] }).WaitAsync(TimeSpan.FromSeconds(5));
                    Check(store.Writes == 1 && saved.Keywords.SequenceEqual(["synthetic-local-setting", "本地配置🎬"]) &&
                        (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual(saved.Keywords),
                        "ordinary settings commit before either startup or a notification response completes");
                    if (starting)
                    {
                        Check(session!.Calls == 0 && !server.HasConnection && host.State == RuntimeConnectionState.Starting,
                            "a starting notification waits for its captured connection without sending settings or starting another factory");
                        ready.TrySetResult(session!);
                    }
                    var notification = await ReadNotification(session!, "film.preferences");
                    await using (var exchange = await server.AcceptAsync())
                    {
                        CheckNotificationHttp(exchange, "film.preferences");
                        Check(!notification.Response.IsCompleted, "the completed save does not depend on a notification acknowledgement");
                        await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(Notified()));
                    }
                    Check((await notification.Response).GetProperty("value").GetProperty("notified").GetBoolean(),
                        "the actual HTTP notification uses the runtime acknowledgement envelope");
                    IRuntimeSettingsNotifications notifications = host;
                    foreach (string block in new[] { "cat.sources", "pan.accounts", "tmdb", "douban", "film.rules" })
                    {
                        notifications.SettingsChanged(block);
                        var call = await ReadNotification(session!, block);
                        await using (var exchange = await server.AcceptAsync())
                        {
                            CheckNotificationHttp(exchange, block);
                            await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(Notified()));
                        }
                        Check((await call.Response).GetProperty("ok").GetBoolean(), "each block has an independent HTTP acknowledgement");
                    }
                    var captured = await host.CaptureConnectionAsync();
                    Check(launches == 1 && host.Generation == 1 && session!.Calls == 6 && !session.TimedCalls.Reader.TryPeek(out _) &&
                        !server.HasConnection && !captured.Revoked.IsCancellationRequested && ReferenceEquals(captured.Client, session) &&
                        captured.BusinessOrigin == new Uri(server.BaseUrl) && store.Writes == 1,
                        "all block notifications retain the same fixed epoch and never resend local document contents");
                }
                finally { ready.TrySetResult(session!); }
            });
        foreach (bool failed in new[] { false, true })
            yield return Test($"a late {(failed ? "failed" : "successful")} settings notification never leaks into a replacement epoch", async () =>
            {
                var late = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                int launches = 0;
                EpochSession? first = null, next = null;
                await using var host = new AppRuntimeConnectionHost(owner =>
                {
                    var session = new EpochSession(owner, ++launches == 1 ? 41002 : 41003);
                    if (launches == 1)
                    {
                        first = session;
                        // Deliberately ignore revocation to expose a late executor result after retirement.
                        session.TimedCaller = (_, _, _, _) => late.Task;
                    }
                    else next = session;
                    return Task.FromResult<IRuntimeSession>(session);
                });
                host.Start();
                var old = await host.CaptureConnectionAsync();
                IRuntimeSettingsNotifications notifications = host;
                try
                {
                    notifications.SettingsChanged("pan.accounts");
                    var pending = await ReadNotification(first!, "pan.accounts");
                    Check(!pending.Response.IsCompleted, "the first epoch owns an actually pending notification");
                    first!.Crash();
                    await host.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var current = await host.CaptureConnectionAsync();
                    Check(old.Revoked.IsCancellationRequested && pending.Token.IsCancellationRequested && first.Disposals == 1 &&
                        !pending.Response.IsCompleted && next!.Calls == 0 && current.BusinessOrigin.Port == 41003 &&
                        !current.Revoked.IsCancellationRequested, "retirement revokes the old notification without moving it to the successor");
                    notifications.SettingsChanged("cat.sources");
                    await (await ReadNotification(next!, "cat.sources")).Response;
                    if (failed)
                    {
                        late.TrySetException(new BridgeException("INVALID_RESPONSE"));
                        await Error("INVALID_RESPONSE", () => pending.Response);
                    }
                    else
                    {
                        late.TrySetResult(Notified());
                        await pending.Response;
                    }
                    notifications.SettingsChanged("film.rules");
                    await (await ReadNotification(next!, "film.rules")).Response;
                    var retained = await host.CaptureConnectionAsync();
                    Check(launches == 2 && host.Generation == 2 && first.Calls == 1 && next!.Calls == 2 &&
                        !first.TimedCalls.Reader.TryPeek(out _) && !next.TimedCalls.Reader.TryPeek(out _) &&
                        ReferenceEquals(retained.Client, current.Client) && retained.Revoked == current.Revoked &&
                        !retained.Revoked.IsCancellationRequested,
                        "a late old result cannot replay its block, retry the host, revoke the successor or suppress a later notification");
                }
                finally { late.TrySetResult(Notified()); }
            });
        yield return Test("HTTP notification failures preserve local commits and later notifications on the same epoch", async () =>
        {
            await using var server = new RuntimeHttpClientTests.Server();
            await using var http = new RuntimeHttpClient(server.BaseUrl);
            await using var store = new FilmNativeSettingsFixture("film.preferences.v1");
            int launches = 0;
            EpochSession? session = null;
            await using var host = new AppRuntimeConnectionHost(owner =>
            {
                launches++;
                return Task.FromResult<IRuntimeSession>(session = new(owner, new Uri(server.BaseUrl).Port)
                    { TimedCaller = http.CallAsync });
            });
            host.Start();
            var captured = await host.CaptureConnectionAsync();
            var settings = new FilmPlaybackSettings(host, store);
            int commits = 0;
            foreach (string failure in new[] { "public-error", "malformed-envelope", "truncated-response" })
            {
                string keyword = "saved-before-" + failure;
                var saved = await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = [keyword] })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                commits++;
                var notification = await ReadNotification(session!, "film.preferences");
                await using (var exchange = await server.AcceptAsync())
                {
                    CheckNotificationHttp(exchange, "film.preferences");
                    Check(saved.Keywords.SequenceEqual([keyword]) && store.Writes == commits && !notification.Response.IsCompleted &&
                        (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual([keyword]),
                        "the native save is already committed while its notification response is held");
                    if (failure == "public-error")
                        await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(new
                            { ok = false, contract = RuntimeHttpProtocol.Name, code = "PERMISSION_DENIED" }), status: 403);
                    else
                        await exchange.ReplyAsync("{"u8.ToArray(), declaredBytes: failure == "truncated-response" ? 20 : null);
                }
                if (failure == "public-error")
                {
                    var response = await notification.Response;
                    Check(!response.GetProperty("ok").GetBoolean() && response.GetProperty("code").GetString() == "PERMISSION_DENIED",
                        "the real HTTP transport retains a rejected notification's failure envelope");
                }
                else await Error(failure == "truncated-response" ? "CLOSED" : "INVALID_RESPONSE", () => notification.Response);
                Check((await new FilmPlaybackSettings(host, store).ReadPreferencesAsync(default)).Keywords.SequenceEqual([keyword]) &&
                    store.Writes == commits && launches == 1 && host.State == RuntimeConnectionState.Ready && !host.CanRetry,
                    "notification rejection or transport failure neither rolls back a commit nor recovers the shared host");
                string nextKeyword = "saved-after-" + failure;
                await settings.SavePreferencesAsync(FilmPreferences.Defaults with { Keywords = [nextKeyword] })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                commits++;
                var next = await ReadNotification(session!, "film.preferences");
                await using (var exchange = await server.AcceptAsync())
                {
                    CheckNotificationHttp(exchange, "film.preferences");
                    await exchange.ReplyAsync(JsonSerializer.SerializeToUtf8Bytes(Notified()));
                }
                Check((await next.Response).GetProperty("value").GetProperty("notified").GetBoolean() &&
                    (await settings.ReadPreferencesAsync(default)).Keywords.SequenceEqual([nextKeyword]) && store.Writes == commits,
                    "a later save and notification succeed independently after each failure");
            }
            var retained = await host.CaptureConnectionAsync();
            Check(commits == 6 && session!.Calls == 6 && !session.TimedCalls.Reader.TryPeek(out _) && !server.HasConnection &&
                launches == 1 && host.Generation == 1 && ReferenceEquals(retained.Client, captured.Client) &&
                retained.Revoked == captured.Revoked && !captured.Revoked.IsCancellationRequested,
                "failed notifications are never replayed and do not replace the epoch or prevent subsequent notifications");
        });
    }

    private static TestCase Test(string name, Func<Task> run) => new("app-runtime-capture: " + name, run);
    private static JsonElement Notified() => FilmJson.Encode(new
        { ok = true, contract = RuntimeHttpProtocol.Name, value = new { notified = true } });
    private sealed record TimedCall(string Method, JsonElement Parameters, TimeSpan Timeout,
        CancellationToken Token, Task<JsonElement> Response);
    private static async Task<TimedCall> ReadNotification(EpochSession session, string block)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var call = await session.TimedCalls.Reader.ReadAsync(deadline.Token);
        Check(call.Method == "runtime.settings.changed" && call.Timeout == TimeSpan.FromSeconds(5) &&
            call.Token == session.Revoked && call.Token.CanBeCanceled &&
            call.Parameters.ValueKind == JsonValueKind.Object && call.Parameters.EnumerateObject().Count() == 1 &&
            call.Parameters.GetProperty("block").GetString() == block,
            "a notification carries only its block name, bounded deadline and the exact captured epoch revocation token");
        return call;
    }
    private static void CheckNotificationHttp(RuntimeHttpClientTests.Exchange exchange, string block)
    {
        var body = BridgeCodec.DecodeJSONValue(exchange.Body);
        Check(exchange.Method == "POST" && exchange.Path == "/v1/settings/changed" &&
            exchange.Headers["Content-Type"] == "application/json" && body.ValueKind == JsonValueKind.Object &&
            body.EnumerateObject().Count() == 1 && body.GetProperty("block").GetString() == block &&
            !exchange.Headers.ContainsKey("Authorization") && !exchange.Headers.ContainsKey("X-Meow-Session") &&
            Guid.TryParseExact(exchange.Headers["X-Meow-Request-Id"], "D", out _),
            "the actual correlated HTTP request sends only the selected block without settings or credentials");
    }
    private sealed class EpochSession : IRuntimeSession, IRuntimeConnectionSource
    {
        private readonly CancellationTokenSource lifetime;
        internal readonly Uri Origin;
        internal CancellationToken Revoked { get; }
        internal int Calls, ImageReads, Disposals, DisposeCalls;
        internal bool Unrevocable;
        internal Task Drain = Task.CompletedTask;
        internal TaskCompletionSource? DisposalStarted;
        internal Func<string, int, CancellationToken, Task<byte[]>>? ImageReader;
        internal Func<string, CancellationToken, Task<byte[]>>? CompleteImageReader;
        internal readonly Channel<TimedCall> TimedCalls = Channel.CreateUnbounded<TimedCall>();
        internal Func<string, object?, TimeSpan, CancellationToken, Task<JsonElement>>? TimedCaller;
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
        public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new BridgeException("CLOSED");
            Calls++;
            var response = TimedCaller?.Invoke(method, parameters, timeout, cancellationToken) ?? Task.FromResult(Notified());
            TimedCalls.Writer.TryWrite(new(method, BridgeCodec.Value(parameters), timeout, cancellationToken, response));
            return response;
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
        public Task<byte[]> ReadImageAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new BridgeException("CLOSED");
            ImageReads++;
            return CompleteImageReader?.Invoke(reference, cancellationToken) ?? throw new BridgeException("METHOD_UNAVAILABLE");
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return new(disposal ??= CloseAsync()); }
        private async Task CloseAsync()
        {
            Disposals++;
            DisposalStarted?.TrySetResult();
            try { await lifetime.CancelAsync(); await Drain; }
            finally { lifetime.Dispose(); }
        }
    }
}
