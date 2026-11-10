using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.AdaptiveMediaTests;
using static MeowFilm.Windows.Tests.FFmpegVideoFixtures;

namespace MeowFilm.Windows.Tests;

public static class FFmpegVideoKernelTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (bool redirect in new[] { false, true })
            yield return Test("long typed HTTP location produces real frames; redirect=" + redirect, async () =>
            {
                await using var origin = Fixture("progressive");
                var route = origin.Route;
                string address = RootPath + "?fixture=" + new string('x', 17000);
                origin.Route = path => path == address ? route(RootPath) :
                    redirect && path == "/entry" ? Reply.Redirect(address) : route(path);
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                var intent = Intent();
                await player.PrepareRuntimeMediaAsync(intent, origin.Uri(redirect ? "/entry" : address), Input("progressive"));
                Check(DisplayedPictures(Field(player, "_handoffSession")!) > 0 &&
                    origin.Requests.Any(request => request.Path == address),
                    "the exact final URL passes through native AVIO and produces decoded video");
                if (redirect)
                    Check(origin.Requests.Count(request => request.Path == "/entry") == 1,
                        "reading the final location must not reopen the initial HTTP request");
                await player.CommitRuntimeMediaAsync(intent, () => { });
                await Until(() => player.ObservedVideoOutput is { DisplayedPictures: > 0 }, "committed long URL video");
                await player.StopAsync();
                Check(!player.HasMedia && Session(player) is null && Field(player, "_handoffSession") is null,
                    "native input and exact HTTP reads have drained after stop");
            });
        foreach (string format in new[] { "progressive", "hls-mp4", "dash-list" })
            foreach (bool explicitHeader in new[] { false, true })
                yield return Test($"userinfo typed HTTP {format} reads real frames; explicit Authorization={explicitHeader}", async () =>
                {
                    await using var origin = Fixture(format);
                    origin.Credential = explicitHeader ? "Fixture explicit-only-value" :
                        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture user:p@ss:word"));
                    origin.RequireAuthorization = true;
                    var route = origin.Route;
                    if (format == "hls-mp4")
                        origin.Route = path => path == RootPath
                            ? new Reply(Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nvariant\n"))
                            : route(path);
                    var address = new Uri($"http://fixture%20user:p%40ss%3Aword@127.0.0.1:{origin.Base.Port}{RootPath}");
                    using var revoked = new CancellationTokenSource();
                    var receipt = new HeaderReceipt(address, origin.Headers, revoked);
                    await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                    var intent = Intent();
                    if (explicitHeader) await player.PrepareRuntimeMediaAsync(intent, receipt, Input(format));
                    else await player.PrepareRuntimeMediaAsync(intent, address, Input(format));
                    Check(DisplayedPictures(Field(player, "_handoffSession")!) > 0 && origin.Requests.Count > 0 &&
                        origin.Requests.All(request => request.Authorized && !request.LocalCapability) &&
                        receipt.Disposals == 0, "public typed input reached authenticated HTTP and produced a real native frame");
                    if (format != "progressive")
                        Check(origin.Requests.Any(request => request.Path.EndsWith("/video-init", StringComparison.Ordinal)) &&
                            origin.Requests.Any(request => request.Path.Contains("/video-", StringComparison.Ordinal) &&
                                !request.Path.EndsWith("/video-init", StringComparison.Ordinal)),
                            "actual adaptive initialization and video segments retain their resolved URL userinfo or explicit headers");
                    await player.CommitRuntimeMediaAsync(intent, () => { });
                    await Until(() => player.ObservedVideoOutput is { DisplayedPictures: > 0 }, "actual committed video output");
                    await player.StopAsync();
                    Check(receipt.Disposals == (explicitHeader ? 1 : 0) && !player.HasMedia,
                        "exact public receipt ownership is unchanged by URL userinfo support");
                });
        foreach (string entry in new[] { "typed-validation", "runtime-validation", "resource-handoff",
            "resource-open", "material-validation", "output-rejection", "read-open", "read-handoff", "read-reopen" })
            foreach (bool failRelease in new[] { false, true })
                yield return Test("WKF01 outer rejection drain " + entry + " failure=" + failRelease, async () =>
                {
                    await using var origin = Fixture("progressive");
                    var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                    var receipt = new PreflightReceipt(origin.Uri(RootPath), failRelease);
                    Task request = Task.CompletedTask;
                    try
                    {
                        var first = Intent();
                        await player.PrepareRuntimeMediaAsync(first, origin.Uri(RootPath), Input("progressive"));
                        await player.CommitRuntimeMediaAsync(first, () => { });
                        await player.PauseAsync();
                        var donor = Session(player)!;
                        var native = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
                        var engine = (LibVLCSharp.Shared.LibVLC)Field(player, "_engine")!;
                        int started = 0; player.Started += (_, _) => started++;
                        var invalid = Intent(Scope(player)) with { Position = double.NaN };
                        if (entry == "typed-validation") request = player.PrepareRuntimeMediaAsync(invalid, receipt, Input("progressive"));
                        else if (entry == "runtime-validation") request = player.PrepareRuntimeMediaAsync(invalid, receipt);
                        else if (entry == "material-validation")
                        {
                            var input = Input("progressive"); input.Dispose();
                            request = player.PrepareRuntimeMediaAsync(Intent(), receipt, input);
                        }
                        else if (entry == "resource-open") request = player.OpenResourceAsync("", "invalid", receipt, "fixture");
                        else if (entry == "read-open") request = player.OpenReadAsync("", "invalid", new PreflightReadReceipt(receipt), "fixture");
                        else if (entry == "read-handoff") request = player.PrepareReadHandoffAsync(new PreflightReadReceipt(receipt), new CancellationToken(true));
                        else if (entry == "read-reopen") request = player.ReopenReadAsync(null!, new PreflightReadReceipt(receipt));
                        else if (entry == "resource-handoff")
                        {
                            receipt.InvalidHeaders = true;
                            request = player.PrepareResourceHandoffAsync(receipt);
                        }
                        else request = player.OwnRuntimeOutputAsync(Scope(player) with { Generation = -1 }, receipt);
                        await receipt.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                        Check(!request.IsCompleted && Field(player, "_handoffPreparation") is null &&
                            ReferenceEquals(Session(player), donor) && native.NativeReference != IntPtr.Zero,
                            "outer rejection owns its exact receipt without admitting or replacing a candidate");
                        var close = player.DisposeAsync().AsTask(); var duplicate = player.DisposeAsync().AsTask();
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        Check(ReferenceEquals(close, duplicate) && !close.IsCompleted && !request.IsCompleted &&
                            receipt.Disposals == 1 && engine.NativeReference != IntPtr.Zero && native.NativeReference != IntPtr.Zero,
                            "close joins the outer rejection before disposing donor or engine");
                        receipt.Finish.TrySetResult();
                        if (failRelease)
                        {
                            await ReleaseFailure(() => request); await ReleaseFailure(() => close);
                            await ReleaseFailure(() => duplicate); await ReleaseFailure(() => player.DisposeAsync().AsTask());
                        }
                        else
                        {
                            await Reject(() => request);
                            await Task.WhenAll(close, duplicate).WaitAsync(TimeSpan.FromSeconds(8));
                            await player.DisposeAsync();
                        }
                        Check(receipt.Disposals == 1 && engine.NativeReference == IntPtr.Zero && native.NativeReference == IntPtr.Zero &&
                            Field(player, "_handoffSession") is null && Session(player) is null && !player.HasMedia && started == 0 &&
                            player.LastErrorCode == (failRelease ? PlaybackError.ReleaseFailed : null),
                            "unique physical receipt, zero late output, stable duplicate close after complete drain");
                    }
                    finally
                    {
                        receipt.Finish.TrySetResult();
                        await CleanupReleaseFailure(player);
                        if (!request.IsCompletedSuccessfully) await Reject(() => request);
                    }
                });
        yield return Test("WKF01 close cancels an accepted read continuation before a late paused callback", async () =>
        {
            var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = new RuntimeMediaTests.WaveLease(); var next = new RuntimeMediaTests.WaveLease();
            Task request = Task.CompletedTask;
            object? pending = null;
            try
            {
                await player.OpenReadAsync("music", "continuation", first, "fixture");
                await Decoded(player); await player.PauseAsync();
                var point = player.CaptureContinuation()!;
                var previous = Session(player);
                var engine = (LibVLCSharp.Shared.LibVLC)Field(player, "_engine")!;
                int started = 0; player.Started += (_, _) => started++;
                player.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName != nameof(NativePlayback.HasMedia) || !player.HasMedia ||
                        Session(player) is not { } current || ReferenceEquals(current, previous)) return;
                    pending = current;
                    // Hold only this real session's Paused delivery, not Play/decoder completion.
                    var native = (LibVLCSharp.Shared.MediaPlayer)Field(current, "Player")!;
                    native.Paused -= (EventHandler<EventArgs>)Field(current, "_paused")!;
                };
                request = player.ReopenReadAsync(point, next);
                var commands = (SemaphoreSlim)Field(player, "_commands")!;
                await Until(() => pending is not null && commands.CurrentCount == 1 && !request.IsCompleted &&
                    Field(pending, "ContinuationReady") is TaskCompletionSource { Task.IsCompleted: false } &&
                    ((LibVLCSharp.Shared.MediaPlayer)Field(pending, "Player")!).State == LibVLCSharp.Shared.VLCState.Paused,
                    "accepted paused continuation waits for its held callback outside the command gate");
                var close = player.DisposeAsync().AsTask(); var duplicate = player.DisposeAsync().AsTask();
                await Task.WhenAll(close, duplicate).WaitAsync(TimeSpan.FromSeconds(8));
                await Reject(() => request);
                Check(ReferenceEquals(close, duplicate) && first.Disposals == 1 && next.Disposals == 1 &&
                    engine.NativeReference == IntPtr.Zero &&
                    ((LibVLCSharp.Shared.MediaPlayer)Field(pending!, "Player")!).NativeReference == IntPtr.Zero &&
                    Session(player) is null && !player.HasMedia && started == 0 && player.LastErrorCode is null,
                    "close wakes owned continuation work before retirement, without a timeout, late start or retained engine");
            }
            finally
            {
                await CleanupReleaseFailure(player);
                if (!request.IsCompletedSuccessfully) await Reject(() => request);
            }
        });
        foreach (string checkpoint in new[] { "engine", "session" })
            foreach (bool failRelease in new[] { false, true })
                yield return Test("WKF01 close joins unregistered " + checkpoint + " worker and receipt failure=" + failRelease, async () =>
                {
                    await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
                    using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
                    int releasedA = 0, releasedB = 0, publications = 0, started = 0;
                    await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
                    var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var workerFinish = new ManualResetEventSlim();
                    int engineWorkers = 0, workerExited = 0;
                    LibVLCSharp.Shared.MediaPlayer? latePlayer = null;
                    async ValueTask Release(string _)
                    {
                        Interlocked.Increment(ref releasedB); releaseEntered.TrySetResult();
                        await releaseFinish.Task.ConfigureAwait(false);
                        if (failRelease) throw new IOException("synthetic-private-unregistered-release-canary");
                    }
                    var authorityB = Authority(runtime.Token, Release);
                    var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                    Task preparation = Task.CompletedTask;
                    try
                    {
                        var first = Intent();
                        await player.PrepareRuntimeMediaAsync(first,
                            authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                        await player.CommitRuntimeMediaAsync(first, () => { });
                        await player.PauseAsync();
                        var donor = Session(player)!; var donorScope = Scope(player);
                        var donorPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
                        var engine = (LibVLCSharp.Shared.LibVLC)Field(player, "_engine")!;
                        player.Started += (_, _) => started++;
                        SetWorkerObserver(player, native =>
                        {
                            if (native is null && Interlocked.Increment(ref engineWorkers) != 2) return;
                            if ((checkpoint == "engine") != (native is null)) return;
                            latePlayer = native; workerEntered.TrySetResult();
                            if (!workerFinish.Wait(TimeSpan.FromSeconds(20)))
                                throw new TimeoutException("unregistered prepare worker gate");
                            Interlocked.Exchange(ref workerExited, 1);
                        });
                        var intent = Intent(donorScope);
                        preparation = player.PrepareRuntimeMediaAsync(intent,
                            authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                        await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                        Check(Field(player, "_handoffSession") is null && !preparation.IsCompleted &&
                            ReferenceEquals(Session(player), donor) && engine.NativeReference != IntPtr.Zero,
                            "the actual worker has not returned and its local session is not registered");
                        if (checkpoint == "session")
                            Check(latePlayer is { NativeReference: var pointer } && pointer != IntPtr.Zero,
                                "CreateSession produced a real local native player before the gate");
                        var close = player.DisposeAsync().AsTask();
                        var duplicate = player.DisposeAsync().AsTask();
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        var discard = player.DiscardRuntimeMediaAsync(intent);
                        Check(ReferenceEquals(close, duplicate) && !close.IsCompleted && !discard.IsCompleted &&
                            Volatile.Read(ref workerExited) == 0 && releasedB == 0 &&
                            engine.NativeReference != IntPtr.Zero && donorPlayer.NativeReference != IntPtr.Zero,
                            "close and exact discard wait for the real worker before engine or donor destruction");
                        await Reject(() => player.CommitRuntimeMediaAsync(intent, () => publications++));
                        workerFinish.Set();
                        await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                        Check(Volatile.Read(ref workerExited) == 1 && !close.IsCompleted && !duplicate.IsCompleted &&
                            !discard.IsCompleted && !preparation.IsCompleted && releasedB == 1 &&
                            engine.NativeReference != IntPtr.Zero &&
                            (latePlayer is null || latePlayer.NativeReference == IntPtr.Zero),
                            "worker exit and late native release still cannot bypass the exact outer receipt gate");
                        releaseFinish.TrySetResult();
                        if (failRelease)
                        {
                            await ReleaseFailure(() => preparation);
                            await ReleaseFailure(() => close);
                            await ReleaseFailure(() => duplicate);
                            await ReleaseFailure(() => discard);
                            await ReleaseFailure(() => player.DisposeAsync().AsTask());
                            await ReleaseFailure(() => player.DiscardRuntimeMediaAsync(intent));
                        }
                        else
                        {
                            await Reject(() => preparation);
                            await Task.WhenAll(close, duplicate, discard).WaitAsync(TimeSpan.FromSeconds(8));
                            await player.DisposeAsync();
                            await player.DiscardRuntimeMediaAsync(intent);
                        }
                        Check(releasedA == 1 && releasedB == 1 && publications == 0 && started == 0 &&
                            engine.NativeReference == IntPtr.Zero && donorPlayer.NativeReference == IntPtr.Zero &&
                            Session(player) is null && Field(player, "_handoffSession") is null &&
                            Field(player, "_runtimeHandoffWindow") is null && Field(player, "_handoffPreparation") is null &&
                            !player.HasMedia && player.ObservedVideoOutput is null &&
                            player.LastErrorCode == (failRelease ? PlaybackError.ReleaseFailed : null),
                            "one final drain, zero late publication, consistent successful or fixed-failed close");
                    }
                    finally
                    {
                        workerFinish.Set(); releaseFinish.TrySetResult();
                        await CleanupReleaseFailure(player);
                        if (!preparation.IsCompletedSuccessfully) await Reject(() => preparation);
                        await CleanupReleaseFailure(authorityB);
                    }
                });
        foreach (string checkpoint in new[] { "engine", "session" })
            yield return Test("WKF01 stale unregistered " + checkpoint + " prepare drains before newest candidate", async () =>
            {
                await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
                await using var c = Fixture("progressive");
                using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
                int releasedA = 0, releasedB = 0, releasedC = 0, publications = 0, engineWorkers = 0;
                await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
                var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var workerFinish = new ManualResetEventSlim();
                async ValueTask Release(string _)
                {
                    releasedB++; releaseEntered.TrySetResult(); await releaseFinish.Task.ConfigureAwait(false);
                }
                await using var authorityB = Authority(runtime.Token, Release);
                await using var authorityC = Authority(runtime.Token, _ => { releasedC++; return ValueTask.CompletedTask; });
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                Task old = Task.CompletedTask, next = Task.CompletedTask, discard = Task.CompletedTask;
                try
                {
                    var first = Intent();
                    await player.PrepareRuntimeMediaAsync(first,
                        authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                    await player.CommitRuntimeMediaAsync(first, () => { });
                    await player.PauseAsync(); await player.SeekAsync(7);
                    var donor = Session(player)!; var scope = Scope(player);
                    SetWorkerObserver(player, native =>
                    {
                        if (native is null && Interlocked.Increment(ref engineWorkers) != 2) return;
                        if ((checkpoint == "engine") != (native is null)) return;
                        workerEntered.TrySetResult();
                        if (!workerFinish.Wait(TimeSpan.FromSeconds(20)))
                            throw new TimeoutException("superseded prepare worker gate");
                    });
                    var rejected = Intent(scope); var latest = Intent(scope);
                    old = player.PrepareRuntimeMediaAsync(rejected,
                        authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                    await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    Check(Field(player, "_handoffSession") is null, "old worker is blocked before session registration");
                    discard = player.DiscardRuntimeMediaAsync(rejected);
                    next = player.PrepareRuntimeMediaAsync(latest,
                        authorityC.CreateLease(ResourceId, c.Uri(RootPath), lifetime.Token), Input("progressive"));
                    var duplicate = player.DiscardRuntimeMediaAsync(rejected);
                    Check(!discard.IsCompleted && !duplicate.IsCompleted && !next.IsCompleted &&
                        ReferenceEquals(Field(player, "_runtimeHandoffIntent"), latest),
                        "new intent owns admission while old receipt and unregistered worker still drain");
                    SetWorkerObserver(player, null);
                    workerFinish.Set();
                    await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    Check(!old.IsCompleted && !discard.IsCompleted && !duplicate.IsCompleted && !next.IsCompleted &&
                        ReferenceEquals(Session(player), donor) && releasedA == 0 && releasedC == 0,
                        "the old outer receipt gate holds retirement without touching donor or newest candidate");
                    releaseFinish.TrySetResult();
                    await Reject(() => old);
                    await Task.WhenAll(discard, duplicate, next).WaitAsync(TimeSpan.FromSeconds(12));
                    var candidate = Field(player, "_handoffSession")!;
                    Check(DisplayedPictures(candidate) > 0 && releasedB == 1 && releasedA == 0 &&
                        ReferenceEquals(Session(player), donor) && player.PauseRequested && Math.Abs(player.PositionSeconds - 7) < .5,
                        "the latest actual picture survives the stale prepare catch, preserving exact donor controls");
                    await player.DiscardRuntimeMediaAsync(rejected);
                    await Reject(() => player.CommitRuntimeMediaAsync(rejected, () => publications++));
                    Check(ReferenceEquals(Field(player, "_handoffSession"), candidate), "old callbacks cannot retire the newest candidate");
                    Check(!await player.TryCommitRuntimeMediaAsync(latest, static () => false, () => publications++) &&
                        ReferenceEquals(Field(player, "_handoffSession"), candidate) && publications == 0,
                        "false still defers the very same ready receipt");
                    await player.CommitRuntimeMediaAsync(latest, () => publications++);
                    Check(publications == 1 && ReferenceEquals(Session(player), candidate) && releasedA == 1 &&
                        releasedC == 0 && player.PauseRequested && Math.Abs(player.PositionSeconds - 7) < .5,
                        "only the newest exact candidate publishes once at preserved donor time");
                }
                finally
                {
                    SetWorkerObserver(player, null); workerFinish.Set(); releaseFinish.TrySetResult();
                    await player.DisposeAsync();
                    await Reject(() => old); await discard; await next;
                }
            });
        yield return Test("WKF01 prepare failure drains its local session and receipt without joining itself", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            int releasedA = 0, releasedB = 0;
            await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
            var releaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async ValueTask Release(string _)
            {
                releasedB++; releaseEntered.TrySetResult(); await releaseFinish.Task.ConfigureAwait(false);
            }
            await using var authorityB = Authority(runtime.Token, Release);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            LibVLCSharp.Shared.MediaPlayer? localPlayer = null;
            Task? preparation = null;
            try
            {
                var first = Intent();
                await player.PrepareRuntimeMediaAsync(first,
                    authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                await player.CommitRuntimeMediaAsync(first, () => { }); await player.PauseAsync();
                var donor = Session(player)!; var donorScope = Scope(player);
                SetWorkerObserver(player, native =>
                {
                    if (native is null) return;
                    localPlayer = native;
                    throw new IOException("synthetic-private-create-session-canary");
                });
                var rejected = Intent(donorScope);
                preparation = player.PrepareRuntimeMediaAsync(rejected,
                    authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Check(!preparation.IsCompleted && localPlayer is { NativeReference: var pointer } && pointer == IntPtr.Zero &&
                    releasedB == 1 && releasedA == 0 && ReferenceEquals(Session(player), donor),
                    "self-failing preparation drains its real local native player before waiting for the exact receipt");
                releaseFinish.TrySetResult();
                try
                {
                    await preparation.WaitAsync(TimeSpan.FromSeconds(8));
                    throw new InvalidOperationException("Failed CreateSession worker returned success");
                }
                catch (PlaybackException error) when (error.Code == PlaybackError.OpenFailed)
                { Check(error.Message == error.Code && error.InnerException is null, "fixed prepare failure without private diagnostics"); }
                Check(Field(player, "_handoffSession") is null && Field(player, "_handoffPreparation") is null &&
                    Field(player, "_runtimeHandoffWindow") is null && ReferenceEquals(Session(player), donor) &&
                    Scope(player) == donorScope && player.HasMedia && player.PauseRequested && releasedA == 0,
                    "catch completed through its own discard without a cycle or donor retirement");
                await player.DiscardRuntimeMediaAsync(rejected);
                SetWorkerObserver(player, null);
                var latest = Intent(donorScope);
                await player.PrepareRuntimeMediaAsync(latest,
                    authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                await player.CommitRuntimeMediaAsync(latest, () => { });
                Check(player.Identity == latest.Identity && releasedA == 1 && releasedB == 1,
                    "a healthy new candidate can still replace the preserved donor after the preparation fault");
            }
            finally
            {
                SetWorkerObserver(player, null); releaseFinish.TrySetResult();
                await player.DisposeAsync();
                if (preparation is not null) await Reject(() => preparation);
            }
        });
        foreach (string api in new[] { "try", "legacy" })
            yield return Test("WKF02 failed child keeps ready A B and fixed repeated " + api + " outcome", async () =>
            {
                await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
                using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
                int releasedA = 0, releasedB = 0, publications = 0;
                bool allowed = true;
                await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
                await using var authorityB = Authority(runtime.Token, _ => { releasedB++; return ValueTask.CompletedTask; });
                var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                var child = new FailingOutput();
                var rejectedChild = new OutputDrain(); rejectedChild.Finish.TrySetResult();
                var rejectedFailure = new FailingOutput(); rejectedFailure.Finish.TrySetResult();
                try
                {
                    var first = Intent();
                    await player.PrepareRuntimeMediaAsync(first,
                        authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                    await player.CommitRuntimeMediaAsync(first, () => { });
                    await player.PauseAsync(); await player.SeekAsync(7);
                    var donor = Session(player)!; var scope = Scope(player);
                    var donorPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
                    var owned = await player.OwnRuntimeOutputAsync(scope, child);
                    var intent = Intent(scope);
                    await player.PrepareRuntimeMediaAsync(intent,
                        authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                    var candidate = Field(player, "_handoffSession")!;
                    var candidatePlayer = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                    var engine = (LibVLCSharp.Shared.LibVLC)Field(player, "_engine")!;
                    Check(DisplayedPictures(donor) > 0 && DisplayedPictures(candidate) > 0 &&
                        Field(candidate, "RuntimeOutputReady") is true, "both A and B have real native ready pictures");
                    Task Commit() => api == "try"
                        ? player.TryCommitRuntimeMediaAsync(intent, () => allowed, () => publications++)
                        : player.CommitRuntimeMediaAsync(intent, () => publications++);
                    var committing = Commit();
                    await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    var handle = owned.DisposeAsync().AsTask();
                    Check(!committing.IsCompleted && !handle.IsCompleted &&
                        ReferenceEquals(handle, owned.DisposeAsync().AsTask()) && publications == 0,
                        "commit and owned handle join the same actual failed-child work");
                    allowed = false;
                    child.Finish.TrySetResult();
                    await ReleaseFailure(() => committing);
                    await ReleaseFailure(() => handle);
                    await ReleaseFailure(() => owned.DisposeAsync().AsTask());
                    await ReleaseFailure(() => player.TryCommitRuntimeMediaAsync(intent,
                        () => allowed, () => publications++));
                    allowed = true;
                    await ReleaseFailure(Commit);
                    // The other public commit API must observe the same failure, too.
                    await ReleaseFailure(() => api == "try"
                        ? player.CommitRuntimeMediaAsync(intent, () => publications++)
                        : player.TryCommitRuntimeMediaAsync(intent, static () => true, () => publications++));
                    await ReleaseFailure(() => player.OwnRuntimeOutputAsync(scope, rejectedChild));
                    await ReleaseFailure(() => player.OwnRuntimeOutputAsync(
                        scope with { Generation = scope.Generation + 1 }, rejectedFailure));
                    Check(child.Disposals == 1 && rejectedChild.Disposals == 1 && rejectedFailure.Disposals == 1 && publications == 0 &&
                        ReferenceEquals(Session(player), donor) && ReferenceEquals(Field(player, "_handoffSession"), candidate) &&
                        ReferenceEquals(Field(player, "_runtimeHandoffIntent"), intent) && Scope(player) == scope &&
                        donorPlayer.NativeReference != IntPtr.Zero && candidatePlayer.NativeReference != IntPtr.Zero &&
                        Field(candidate, "HasStarted") is false && releasedA == 0 && releasedB == 0 &&
                        player.HasMedia && player.PauseRequested && !player.IsPlaying && !donorPlayer.Mute &&
                        Math.Abs(player.PositionSeconds - 7) < .5 && player.Identity == first.Identity,
                        "failure never becomes Deferred or success, never swaps B, and leaves exact native A controls intact");
                    await player.DiscardRuntimeMediaAsync(intent);
                    await player.DiscardRuntimeMediaAsync(intent);
                    Check(releasedB == 1 && releasedA == 0 && candidatePlayer.NativeReference == IntPtr.Zero &&
                        ReferenceEquals(Session(player), donor), "exact B discard still drains without consuming the donor");
                    await ReleaseFailure(() => player.DisposeAsync().AsTask());
                    await ReleaseFailure(() => player.DisposeAsync().AsTask());
                    await ReleaseFailure(() => owned.DisposeAsync().AsTask());
                    Check(child.Disposals == 1 && releasedA == 1 && releasedB == 1 &&
                        donorPlayer.NativeReference == IntPtr.Zero && engine.NativeReference == IntPtr.Zero &&
                        Session(player) is null && Field(player, "_handoffSession") is null &&
                        Field(player, "_runtimeHandoffWindow") is null && Field(player, "_engine") is null &&
                        !player.HasMedia && player.LastErrorCode == PlaybackError.ReleaseFailed,
                        "later close reports the child failure only after every other owner physically drains");
                }
                finally
                {
                    child.Finish.TrySetResult();
                    await CleanupReleaseFailure(player);
                }
            });
        yield return Test("WKF02 shared music handoff keeps fixed child failure before swap and drains both inputs", async () =>
        {
            await using var a = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            int releases = 0, publications = 0;
            await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var child = new FailingOutput(); child.Finish.TrySetResult();
            var music = new RuntimeMediaTests.WaveLease();
            try
            {
                var first = Intent();
                await player.PrepareRuntimeMediaAsync(first,
                    authority.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                await player.CommitRuntimeMediaAsync(first, () => { }); await player.PauseAsync();
                var donor = Session(player)!; var donorScope = Scope(player);
                var owned = await player.OwnRuntimeOutputAsync(donorScope, child);
                await player.PrepareReadHandoffAsync(music, default);
                var candidate = Field(player, "_handoffSession")!;
                var native = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                Check(Field(player, "_handoffReady") is TaskCompletionSource { Task.IsCompletedSuccessfully: true },
                    "the shared-core music handoff is actually native-ready");
                await ReleaseFailure(() => player.CommitHandoffAsync(() => publications++, default));
                await ReleaseFailure(() => player.CommitHandoffAsync(() => publications++, default));
                await ReleaseFailure(() => owned.DisposeAsync().AsTask());
                Check(child.Disposals == 1 && publications == 0 && releases == 0 && music.Disposals == 0 &&
                    ReferenceEquals(Session(player), donor) && ReferenceEquals(Field(player, "_handoffSession"), candidate) &&
                    Scope(player) == donorScope && native.NativeReference != IntPtr.Zero,
                    "the legacy shared core preserves both exact native occurrences on child failure");
                await player.DiscardHandoffAsync();
                Check(native.NativeReference == IntPtr.Zero && music.Disposals == 1 && releases == 0,
                    "music discard releases only its exact rejected input");
                await ReleaseFailure(() => player.DisposeAsync().AsTask());
                Check(releases == 1 && Session(player) is null && Field(player, "_engine") is null,
                    "shared-core cleanup still drains the retained donor after the fixed child failure");
            }
            finally { child.Finish.TrySetResult(); await CleanupReleaseFailure(player); }
        });
        yield return Test("cancelling only commit after child drain begins cannot publish B or retire real A output", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource();
            using var lifetimeA = new CancellationTokenSource(); using var lifetimeB = new CancellationTokenSource();
            using var publication = new CancellationTokenSource();
            int releasedA = 0, releasedB = 0;
            await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
            await using var authorityB = Authority(runtime.Token, _ => { releasedB++; return ValueTask.CompletedTask; });
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent();
            var leaseA = authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetimeA.Token);
            await player.PrepareRuntimeMediaAsync(first, leaseA, Input("progressive"));
            await player.CommitRuntimeMediaAsync(first, () => { });
            await player.PauseAsync(); await player.SeekAsync(7);
            await Until(() => player.ObservedVideoOutput is { DisplayedPictures: > 0 } &&
                Math.Abs(player.PositionSeconds - 7) < .5, "real paused donor video and seek");
            var donorScope = Scope(player);
            var donor = Session(player)!;
            var donorPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
            var child = new OutputDrain();
            var owned = await player.OwnRuntimeOutputAsync(donorScope, child);
            var replacement = Intent(donorScope);
            var leaseB = authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetimeB.Token);
            await player.PrepareRuntimeMediaAsync(replacement, leaseB, Input("progressive"));
            var prepared = Field(player, "_handoffSession")!;
            var preparedPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(prepared, "Player")!;
            Check(DisplayedPictures(prepared) > 0, "B is a genuinely ready native input before commit");
            int publications = 0;
            var commit = player.CommitRuntimeMediaAsync(replacement, () => publications++, publication.Token);
            try
            {
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Check(!commit.IsCompleted && publications == 0 && ReferenceEquals(Session(player), donor),
                    "the commit is really awaiting A's child, not a preparation gate");
                publication.Cancel();
                Check(!lifetimeA.IsCancellationRequested && !lifetimeB.IsCancellationRequested &&
                    !runtime.IsCancellationRequested && !leaseA.Revoked.IsCancellationRequested &&
                    !leaseB.Revoked.IsCancellationRequested && !commit.IsCompleted,
                    "only the new commit token is cancelled; both admitted native input lifetimes remain valid");
                Console.WriteLine("OBSERVED video-kernel commit-gate: " +
                    $"publishedB={publications}; commitTokenCancelled={publication.IsCancellationRequested}; " +
                    $"inputACancelled={lifetimeA.IsCancellationRequested}; inputBCancelled={lifetimeB.IsCancellationRequested}; " +
                    $"runtimeCancelled={runtime.IsCancellationRequested}; " +
                    $"resourceARevoked={leaseA.Revoked.IsCancellationRequested}; resourceBRevoked={leaseB.Revoked.IsCancellationRequested}; " +
                    $"picturesA={DisplayedPictures(donor)}; picturesB={DisplayedPictures(prepared)}; childPhysicalDisposals={child.Disposals}");
                child.Finish.TrySetResult();
                try { await commit.WaitAsync(TimeSpan.FromSeconds(8)); throw new InvalidOperationException("Cancelled commit published."); }
                catch (OperationCanceledException) { }
                Check(publications == 0 && player.Identity == first.Identity && player.HasMedia &&
                    ReferenceEquals(Session(player), donor) && Scope(player) == donorScope &&
                    player.ObservedVideoOutput is { DisplayedPictures: > 0 } output && output.Identity == first.Identity &&
                    DisplayedPictures(donor) > 0 && donorPlayer.NativeReference != IntPtr.Zero &&
                    player.PauseRequested && !player.IsPlaying && Math.Abs(player.PositionSeconds - 7) < .5 &&
                    !donorPlayer.Mute && child.Disposals == 1 && releasedA == 0,
                    "failed publication leaves A's exact live native picture, pause, time and audibility intent intact");
                await player.DiscardRuntimeMediaAsync(replacement);
                Check(preparedPlayer.NativeReference == IntPtr.Zero && releasedB == 1 && releasedA == 0 &&
                    ReferenceEquals(Session(player), donor), "discard drains only the rejected B occurrence");
                long picturesBeforeResume = DisplayedPictures(donor);
                await player.SeekRuntimeAsync(donorScope, 9);
                await player.SetRuntimePausedAsync(donorScope, false);
                await Until(() => player.IsPlaying && player.ObservedVideoOutput is { DisplayedPictures: > 0 } current &&
                    current.Identity == first.Identity && player.PositionSeconds >= 8.5 &&
                    DisplayedPictures(donor) > picturesBeforeResume, "A's real controls and new video frames remain live");
                await player.SetRuntimePausedAsync(donorScope, true);
                await owned.DisposeAsync(); await owned.DisposeAsync();
                Check(child.Disposals == 1, "repeated attachment-handle disposal joins the already-drained child");
                Console.WriteLine("OBSERVED video-kernel commit-token-only: " +
                    $"publishedB={publications}; picturesA={DisplayedPictures(donor)}; " +
                    $"identityA={player.Identity}; scopeA={donorScope.Generation}; " +
                    $"actualPositionA={player.PositionSeconds}; pauseA={player.PauseRequested}; " +
                    $"childPhysicalDisposals={child.Disposals}; releasedA={releasedA}; releasedB={releasedB}; " +
                    $"nativeBReleased={preparedPlayer.NativeReference == IntPtr.Zero}");
                await player.StopRuntimeAsync(donorScope);
                Check(releasedA == 1 && donorPlayer.NativeReference == IntPtr.Zero && child.Disposals == 1,
                    "the accepted donor still has one final real retirement");
                Console.WriteLine("OBSERVED video-kernel commit-final-drain: " +
                    $"releasedA={releasedA}; releasedB={releasedB}; nativeAReleased={donorPlayer.NativeReference == IntPtr.Zero}; " +
                    $"childPhysicalDisposals={child.Disposals}");
            }
            finally
            {
                child.Finish.TrySetResult();
                try { await commit; } catch (OperationCanceledException) { }
                await player.DiscardRuntimeMediaAsync(replacement);
            }
        });
        yield return Test("an exact runtime occurrence physically drains its attached output before replacement publication", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            await using var authorityA = Authority(runtime.Token); await using var authorityB = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent();
            await player.PrepareRuntimeMediaAsync(first,
                authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(first, () => { });
            await player.PauseAsync();
            var old = Scope(player);
            var child = new OutputDrain();
            var owned = await player.OwnRuntimeOutputAsync(old, child);
            var replacement = Intent();
            await player.PrepareRuntimeMediaAsync(replacement,
                authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
            bool published = false;
            var commit = player.CommitRuntimeMediaAsync(replacement, () => published = true);
            try
            {
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Check(!commit.IsCompleted && !published && player.Identity == first.Identity,
                    "a replacing native output cannot publish before actual child drain");
                child.Finish.TrySetResult();
                await commit;
                Check(published && player.Identity == replacement.Identity && child.Disposals == 1,
                    "one native occurrence owns the external lifetime");
                await owned.DisposeAsync();
                Check(child.Disposals == 1, "caller/native retirement share one physical completion");
                var rejected = new OutputDrain(); rejected.Finish.TrySetResult();
                await Reject(() => player.OwnRuntimeOutputAsync(old, rejected));
                Check(rejected.Disposals == 1 && player.Identity == replacement.Identity,
                    "a rejected stale scope releases its offered child without touching the current owner");
            }
            finally { child.Finish.TrySetResult(); await commit; }
        });
        yield return Test("ordinary music replacement waits for the exact film child before accepting another output", async () =>
        {
            await using var origin = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent();
            await player.PrepareRuntimeMediaAsync(first,
                authority.CreateLease(ResourceId, origin.Uri(RootPath), lifetime.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(first, () => { });
            await player.PauseAsync();
            var child = new OutputDrain();
            _ = await player.OwnRuntimeOutputAsync(Scope(player), child);
            var replacement = player.OpenReadAsync("music", "music-next", new RuntimeMediaTests.WaveLease(), "music");
            try
            {
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Check(!replacement.IsCompleted && player.OwnerId != "music",
                    "ordinary Open must also join the old attached output");
                child.Finish.TrySetResult();
                await replacement;
                Check(player.OwnerId == "music" && child.Disposals == 1, "one actual media owner after takeover");
            }
            finally { child.Finish.TrySetResult(); await replacement; }
        });
        foreach (int checkpoint in new[] { 1, 2, 3 })
            yield return Test("deferred runtime commit retains its exact ready receipt at checkpoint " + checkpoint, async () =>
            {
                await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
                using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
                int releasedA = 0, releasedB = 0, publications = 0;
                await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
                await using var authorityB = Authority(runtime.Token, _ => { releasedB++; return ValueTask.CompletedTask; });
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                var first = Intent();
                var leaseA = authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token);
                await player.PrepareRuntimeMediaAsync(first, leaseA, Input("progressive"));
                await player.CommitRuntimeMediaAsync(first, () => { });
                await player.PauseAsync(); await player.SeekAsync(7);
                await Until(() => Math.Abs(player.PositionSeconds - 7) < .5, "ready donor time");
                var donorScope = Scope(player); var donor = Session(player)!;
                var donorPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
                var child = new OutputDrain();
                try
                {
                    _ = await player.OwnRuntimeOutputAsync(donorScope, child);
                    var replacement = Intent(donorScope);
                    var leaseB = authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token);
                    await player.PrepareRuntimeMediaAsync(replacement, leaseB, Input("progressive"));
                    var candidate = Field(player, "_handoffSession")!;
                    var candidatePlayer = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                    var ready = Field(player, "_handoffReady");
                    Check(DisplayedPictures(candidate) > 0 && Field(candidate, "RuntimeOutputReady") is true &&
                        Field(candidate, "HasStarted") is false, "one actual prepared picture, not a started surrogate");
                    await player.SeekRuntimeAsync(donorScope, 9);
                    await Until(() => Math.Abs(player.PositionSeconds - 9) < .5, "new donor position after preparation");
                    int gates = 0;
                    bool swapped = await player.TryCommitRuntimeMediaAsync(replacement,
                        () => ++gates < checkpoint, () => publications++);
                    Check(!swapped && gates == checkpoint && publications == 0 &&
                        ReferenceEquals(Session(player), donor) && Scope(player) == donorScope &&
                        ReferenceEquals(Field(player, "_handoffSession"), candidate) &&
                        ReferenceEquals(Field(player, "_runtimeHandoffIntent"), replacement) &&
                        ReferenceEquals(Field(player, "_handoffReady"), ready) &&
                        candidatePlayer.NativeReference != IntPtr.Zero && Field(candidate, "HasStarted") is false &&
                        !lifetime.IsCancellationRequested && !runtime.IsCancellationRequested && child.Disposals == 0 &&
                        releasedA == 0 && releasedB == 0 && !leaseA.Revoked.IsCancellationRequested &&
                        !leaseB.Revoked.IsCancellationRequested && player.LastErrorCode is null,
                        "each pre-dispose gate defers without replacing, releasing, re-preparing or failing either receipt");
                    await Reject(() => player.TryCommitRuntimeMediaAsync(replacement with { },
                        static () => false, () => publications++));
                    Check(ReferenceEquals(Field(player, "_handoffSession"), candidate) && releasedB == 0,
                        "a copied intent cannot use Deferred to adopt or discard this preparation");
                    // These operations also prove that false released the native command lock.
                    await player.SeekRuntimeAsync(donorScope, 11);
                    long pictures = DisplayedPictures(donor);
                    await player.SetRuntimePausedAsync(donorScope, false);
                    await Until(() => player.IsPlaying && player.PositionSeconds >= 10.5 &&
                        DisplayedPictures(donor) > pictures, "live donor controls after Deferred");
                    child.Finish.TrySetResult();
                    double donorTimeAtSwap = -1, candidateTimeAtSwap = -1;
                    swapped = await player.TryCommitRuntimeMediaAsync(replacement, static () => true, () =>
                    {
                        publications++; donorTimeAtSwap = donorPlayer.Time / 1000d;
                        candidateTimeAtSwap = candidatePlayer.Time / 1000d;
                    });
                    Check(swapped && publications == 1 && ReferenceEquals(Session(player), candidate) &&
                        Field(player, "_handoffSession") is null && Field(candidate, "HasStarted") is true &&
                        player.Identity == replacement.Identity && player.HasMedia && !player.PauseRequested &&
                        player.IsPlaying && Math.Abs(candidateTimeAtSwap - donorTimeAtSwap) <= .5 &&
                        donorTimeAtSwap >= 10.5 && child.Disposals == 1 && releasedA == 1 && releasedB == 0 &&
                        donorPlayer.NativeReference == IntPtr.Zero,
                        "explicit admission publishes the same prepared native candidate once at latest donor time and pause intent");
                    await player.StopAsync();
                    Check(releasedB == 1 && candidatePlayer.NativeReference == IntPtr.Zero,
                        "the preserved candidate receipt still has exactly one final retirement");
                }
                finally { child.Finish.TrySetResult(); }
            });
        yield return Test("gate closing during child disposal defers the same candidate and retains the live native donor", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            int releasedA = 0, releasedB = 0, publications = 0;
            await using var authorityA = Authority(runtime.Token, _ => { releasedA++; return ValueTask.CompletedTask; });
            await using var authorityB = Authority(runtime.Token, _ => { releasedB++; return ValueTask.CompletedTask; });
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent();
            var leaseA = authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token);
            await player.PrepareRuntimeMediaAsync(first, leaseA, Input("progressive"));
            await player.CommitRuntimeMediaAsync(first, () => { });
            await player.PauseAsync(); await player.SeekAsync(7);
            await Until(() => Math.Abs(player.PositionSeconds - 7) < .5, "actual paused donor");
            var donorScope = Scope(player); var donor = Session(player)!;
            var donorPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
            var child = new OutputDrain();
            var replacement = Intent(donorScope);
            bool allowed = true;
            Task<bool>? committing = null;
            try
            {
                var owned = await player.OwnRuntimeOutputAsync(donorScope, child);
                var leaseB = authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token);
                await player.PrepareRuntimeMediaAsync(replacement, leaseB, Input("progressive"));
                var candidate = Field(player, "_handoffSession")!;
                var candidatePlayer = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                Check(DisplayedPictures(candidate) > 0, "B has a real native picture before this commit");
                committing = player.TryCommitRuntimeMediaAsync(replacement, () => allowed, () => publications++);
                await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Check(!committing.IsCompleted && publications == 0 && ReferenceEquals(Session(player), donor),
                    "the gate will change during actual child disposal, not before commit entry");
                allowed = false;
                child.Finish.TrySetResult();
                Check(!await committing.WaitAsync(TimeSpan.FromSeconds(8)), "closing admission returns Deferred, not publication");
                Check(publications == 0 && child.Disposals == 1 &&
                    ReferenceEquals(Session(player), donor) && Scope(player) == donorScope &&
                    donorPlayer.NativeReference != IntPtr.Zero && !donorPlayer.Mute &&
                    player.HasMedia && player.PauseRequested && !player.IsPlaying &&
                    Math.Abs(player.PositionSeconds - 7) < .5 &&
                    ReferenceEquals(Field(player, "_handoffSession"), candidate) &&
                    ReferenceEquals(Field(player, "_runtimeHandoffIntent"), replacement) &&
                    candidatePlayer.NativeReference != IntPtr.Zero && Field(candidate, "HasStarted") is false &&
                    !lifetime.IsCancellationRequested && !runtime.IsCancellationRequested && releasedA == 0 && releasedB == 0 &&
                    !leaseA.Revoked.IsCancellationRequested && !leaseB.Revoked.IsCancellationRequested &&
                    player.LastErrorCode is null,
                    "child drain is irreversible, but neither exact native donor nor candidate receipt has been retired");
                await owned.DisposeAsync();
                Check(child.Disposals == 1, "Deferred does not resurrect the already-disposed child");
                await player.SeekRuntimeAsync(donorScope, 9);
                await Until(() => Math.Abs(player.PositionSeconds - 9) < .5, "donor lock and controls after child drain");
                allowed = true;
                Check(await player.TryCommitRuntimeMediaAsync(replacement, () => allowed, () => publications++),
                    "the same exact candidate can explicitly pass admission later");
                Check(publications == 1 && ReferenceEquals(Session(player), candidate) &&
                    Field(candidate, "HasStarted") is true && player.Identity == replacement.Identity &&
                    player.PauseRequested && Math.Abs(player.PositionSeconds - 9) < .5 &&
                    child.Disposals == 1 && releasedA == 1 && releasedB == 0,
                    "retry re-aligns latest paused native donor and publishes once without a second prepare or child drain");
                await player.StopAsync();
                Check(releasedB == 1 && candidatePlayer.NativeReference == IntPtr.Zero, "one final candidate release");
            }
            finally
            {
                child.Finish.TrySetResult();
                if (committing is not null) await committing;
            }
        });
        foreach (string invalidation in new[] { "cancel", "revoke", "stop" })
            yield return Test("false runtime admission cannot hide native " + invalidation, async () =>
            {
                await using var origin = Fixture("progressive");
                using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
                using var cancellation = new CancellationTokenSource();
                int releases = 0, publications = 0, gates = 0;
                await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                var intent = Intent();
                await player.PrepareRuntimeMediaAsync(intent,
                    authority.CreateLease(ResourceId, origin.Uri(RootPath), lifetime.Token), Input("progressive"));
                var candidate = Field(player, "_handoffSession")!;
                var native = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                Check(DisplayedPictures(candidate) > 0, "failure starts from a genuinely ready candidate");
                if (invalidation == "cancel") cancellation.Cancel();
                if (invalidation == "revoke") lifetime.Cancel();
                if (invalidation == "stop") await player.StopAsync();
                await Reject(() => player.TryCommitRuntimeMediaAsync(intent, () => { gates++; return false; },
                    () => publications++, cancellation.Token));
                Check(gates == 0 && publications == 0 && !player.HasMedia,
                    "native cancellation, authority or exact-intent failure takes precedence over Deferred");
                await player.DiscardRuntimeMediaAsync(intent);
                Check(releases == 1 && native.NativeReference == IntPtr.Zero,
                    "a terminal rejection still joins actual native and receipt retirement");
            });
        yield return Test("TryCommit preserves a failed donor release after actual publication instead of returning success or Deferred", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            int releasedA = 0, releasedB = 0, publications = 0;
            var failedAuthority = Authority(runtime.Token, _ =>
            {
                releasedA++;
                return ValueTask.FromException(new IOException("synthetic-deferred-release-canary"));
            });
            await using var authorityB = Authority(runtime.Token, _ => { releasedB++; return ValueTask.CompletedTask; });
            var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            try
            {
                var first = Intent();
                await player.PrepareRuntimeMediaAsync(first,
                    failedAuthority.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                await player.CommitRuntimeMediaAsync(first, () => { });
                await player.PauseAsync();
                var donor = Session(player)!;
                var donorPlayer = (LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!;
                var replacement = Intent(Scope(player));
                await player.PrepareRuntimeMediaAsync(replacement,
                    authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                var candidate = Field(player, "_handoffSession")!;
                Check(DisplayedPictures(candidate) > 0, "the replacement is a real prepared input");
                Check(!await player.TryCommitRuntimeMediaAsync(replacement, static () => false, () => publications++) &&
                    releasedA == 0 && releasedB == 0 && publications == 0,
                    "Deferred does not begin the failing donor release");
                await ReleaseFailure(() => player.TryCommitRuntimeMediaAsync(replacement, static () => true, () => publications++));
                Check(publications == 1 && releasedA == 1 && releasedB == 0 &&
                    ReferenceEquals(Session(player), candidate) && player.Identity == replacement.Identity &&
                    Field(candidate, "HasStarted") is true && donorPlayer.NativeReference == IntPtr.Zero,
                    "actual swap is preserved, but failed post-swap drain remains the exact fixed ReleaseFailed outcome");
                await ReleaseFailure(() => player.DisposeAsync().AsTask());
                Check(releasedB == 1, "a release fault cannot skip the committed replacement's final drain");
                await ReleaseFailure(() => failedAuthority.DisposeAsync().AsTask());
            }
            finally
            {
                await CleanupReleaseFailure(player);
                await CleanupReleaseFailure(failedAuthority);
            }
        });
        yield return Test("typed keys validate UUID bytes one-use and private diagnostics", () =>
        {
            using var keys = NativeClearKeySet.FromHex(new Dictionary<string, string>
            { ["00112233-4455-6677-8899-aabbccddeeff"] = VideoKey, [AudioKid] = AudioKey });
            Check(keys.ToString() == "NativeClearKeySet", "keys absent from normal diagnostic");
            using var input = new NativeVideoInput(NativeVideoFormat.Dash, keys);
            var take = typeof(NativeVideoInput).GetMethod("Take", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var material = ((byte[] Pairs, byte[]? Default))take.Invoke(input, null)!;
            try
            {
                Check(Convert.ToHexStringLower(material.Pairs.AsSpan(0, 16)) == VideoKid &&
                    Convert.ToHexStringLower(material.Pairs.AsSpan(16, 16)) == VideoKey &&
                    Convert.ToHexStringLower(material.Pairs.AsSpan(32, 16)) == AudioKid, "network byte order and pair order");
                try { take.Invoke(input, null); throw new InvalidOperationException("input was reusable"); }
                catch (TargetInvocationException e) when (e.InnerException is PlaybackException { Code: PlaybackError.InvalidRequest }) { }
            }
            finally { CryptographicOperations.ZeroMemory(material.Pairs); }
            foreach (string kid in new[] { "bad", "00112233-4455-6677-8899-aabbccddeef-", new string('z', 32) })
                Error(() => NativeClearKeySet.FromHex(new Dictionary<string, string> { [kid] = VideoKey }), PlaybackError.InvalidRequest);
            Error(() => NativeClearKeySet.FromHex(new Dictionary<string, string>
            { [VideoKid] = VideoKey, ["00112233-4455-6677-8899-aabbccddeeff"] = VideoKey }), PlaybackError.InvalidRequest);
            return Task.CompletedTask;
        });
        foreach (string format in new[] { "progressive", "hls", "hls-aes", "hls-range", "dash", "dash-cenc" })
            yield return Test("real video output seek and exact-owner drain " + format, async () =>
            {
                await using var origin = Fixture(format);
                using var runtime = new CancellationTokenSource();
                using var scope = new CancellationTokenSource();
                int releases = 0;
                await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                var lease = authority.CreateLease(ResourceId, origin.Uri(RootPath), scope.Token);
                using var input = Input(format);
                var intent = Intent();
                await player.PrepareRuntimeMediaAsync(intent, lease, input);
                Check(!player.HasMedia && player.ObservedVideoOutput is null && releases == 0, "prepare did not publish");
                var provisional = Field(player, "_handoffSession")!;
                Check(DisplayedPictures(provisional) > 0, "actual VLC display counter, not Playing");
                Check(Field(provisional, "FFmpeg") is not null && Field(provisional, "Adaptive") is null &&
                    Field(provisional, "Input") is null, "private demux path, no remux or existing proxy");
                int commits = 0;
                await player.CommitRuntimeMediaAsync(intent, () => commits++, default);
                Check(commits == 1 && player.HasMedia && player.ObservedVideoOutput is { DisplayedPictures: > 0 }, "one publication after real frame");
                await player.PauseAsync();
                await player.SeekAsync(13);
                await Until(() => Math.Abs(player.PositionSeconds - 13) < .5, "true time seek");
                if (format.StartsWith("dash", StringComparison.Ordinal))
                {
                    var tracks = await player.ReadTracksAsync();
                    Check(tracks is not null && tracks.Audio.Any(track => track.Id >= 0), "separate audio representation");
                    Check(origin.Requests.Any(r => r.Path.Contains("audio-", StringComparison.Ordinal)) &&
                        origin.Requests.Any(r => r.Path.Contains("video-", StringComparison.Ordinal)), "both representation IO");
                }
                Check(origin.Requests.All(r => r.Path.StartsWith(Prefix, StringComparison.Ordinal) && !r.AnyCallerHeader &&
                    !r.LocalCapability), "exact Node graph and no upstream headers/private HTTP service");
                await player.StopAsync();
                Check(releases == 1 && lease.Revoked.IsCancellationRequested, "one actual owned release");
                var native = (LibVLCSharp.Shared.MediaPlayer)Field(provisional, "Player")!;
                Check(native.NativeReference == IntPtr.Zero, "native player retired before stop completion");
                Console.WriteLine($"OBSERVED private demux {format}: real libVLC displayed pictures, actual time seek and release; not physical HDR/hardware proof");
            });
        yield return Test("bad encrypted candidate leaves same-episode donor and pause time intact", async () =>
        {
            await using var good = Fixture("progressive");
            await using var bad = Fixture("dash-cenc");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var goodAuthority = Authority(runtime.Token);
            int rejectedReleases = 0;
            await using var badAuthority = Authority(runtime.Token, _ => { rejectedReleases++; return ValueTask.CompletedTask; });
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var intent = Intent();
            await player.PrepareRuntimeMediaAsync(intent, goodAuthority.CreateLease(ResourceId, good.Uri(RootPath), scope.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(intent, () => { }, default);
            await player.PauseAsync(); await player.SeekAsync(6);
            var donor = Session(player);
            using var wrong = new NativeVideoInput(NativeVideoFormat.Dash, NativeClearKeySet.FromHex(
                new Dictionary<string, string> { [VideoKid] = new('0', 32), [AudioKid] = AudioKey }));
            await Reject(() => player.PrepareRuntimeMediaAsync(Intent(Scope(player)), badAuthority.CreateLease(ResourceId,
                bad.Uri(RootPath), scope.Token), wrong));
            Check(ReferenceEquals(Session(player), donor) && player.HasMedia && player.PauseRequested &&
                rejectedReleases == 1 && Field(player, "_handoffSession") is null, "failed preparation did not retire donor");
        });
        yield return Test("successful same-episode replacement preserves live time and paused intent", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("hls");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authorityA = Authority(runtime.Token); await using var authorityB = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent();
            await player.PrepareRuntimeMediaAsync(first, authorityA.CreateLease(ResourceId, a.Uri(RootPath), scope.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(first, () => { }, default);
            await player.PauseAsync(); await player.SeekAsync(7);
            await Until(() => Math.Abs(player.PositionSeconds - 7) < .5, "donor paused time");
            var replacement = Intent(Scope(player));
            await player.PrepareRuntimeMediaAsync(replacement, authorityB.CreateLease(ResourceId, b.Uri(RootPath), scope.Token), Input("hls"));
            await player.CommitRuntimeMediaAsync(replacement, () => { }, default);
            Check(player.PauseRequested && !player.IsPlaying && Math.Abs(player.PositionSeconds - 7) <= .25, "same episode retained actual native time and latest pause");
            Check(first.Identity != replacement.Identity && player.Identity == replacement.Identity,
                "new attempts are distinct while only their exact donor clock is inherited");
        });
        yield return Test("stale same-episode donor cannot prepare a new input after another occurrence committed", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authorityA = Authority(runtime.Token);
            await using var authorityB = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent();
            await player.PrepareRuntimeMediaAsync(first,
                authorityA.CreateLease(ResourceId, a.Uri(RootPath), scope.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(first, () => { });
            var oldScope = Scope(player);
            var second = Intent();
            await player.PrepareRuntimeMediaAsync(second,
                authorityB.CreateLease(ResourceId, b.Uri(RootPath), scope.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(second, () => { });
            await using var rejected = Fixture("progressive");
            int releases = 0;
            await using var rejectedAuthority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            await Reject(() => player.PrepareRuntimeMediaAsync(Intent(oldScope),
                rejectedAuthority.CreateLease(ResourceId, rejected.Uri(RootPath), scope.Token), Input("progressive")));
            Check(player.Identity == second.Identity && player.HasMedia && releases == 1 && rejected.Requests.IsEmpty,
                "stale donor fails before network and releases only its unaccepted capability");
        });
        yield return Test("late film commit and discard cannot adopt a prepared music replacement", async () =>
        {
            await using var video = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            await player.OpenReadAsync("music", "song", new RuntimeMediaTests.WaveLease(), "fixture");
            await Until(() => player.HasMedia, "music donor");
            var film = Intent();
            await player.PrepareRuntimeMediaAsync(film,
                authority.CreateLease(ResourceId, video.Uri(RootPath), scope.Token), Input("progressive"));
            var replacement = new RuntimeMediaTests.WaveLease();
            await player.PrepareReadHandoffAsync(replacement, default);
            var prepared = Field(player, "_handoffSession");
            bool wrongCommit = false;
            await Reject(() => player.CommitRuntimeMediaAsync(film, () => wrongCommit = true));
            await player.DiscardRuntimeMediaAsync(film);
            Check(!wrongCommit && replacement.Disposals == 0 && ReferenceEquals(prepared, Field(player, "_handoffSession")),
                "film callbacks are bound to their own retired preparation");
            await player.CommitHandoffAsync(() => { }, default);
            Check(player.OwnerId == "music" && player.Identity == "song" && player.HasMedia,
                "only music's normal commit publishes its candidate");
        });
        yield return Test("redirected HLS manifest resolves children against actual response location", async () =>
        {
            await using var origin = Fixture("hls"); var route = origin.Route;
            origin.Route = path => path == RootPath ? Reply.Redirect(RootPath + "/entry") :
                path == RootPath + "/entry" ? route(Prefix + Child + "/variant") : route(path);
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            await player.PrepareRuntimeMediaAsync(Intent(), authority.CreateLease(ResourceId, origin.Uri(RootPath), scope.Token), Input("hls"));
            Check(DisplayedPictures(Field(player, "_handoffSession")!) > 0 &&
                origin.Requests.Any(request => request.Path.StartsWith(RootPath + "/video-", StringComparison.Ordinal)),
                "custom AVIO exposes real redirect base to HLS");
        });
        yield return Test("new episode uses explicit initial position and paused intent", async () =>
        {
            await using var origin = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var intent = new RuntimeMediaPlaybackIntent("film", "new-episode", "", 4, true);
            await player.PrepareRuntimeMediaAsync(intent,
                authority.CreateLease(ResourceId, origin.Uri(RootPath), scope.Token), Input("progressive"));
            await player.CommitRuntimeMediaAsync(intent, () => { }, default);
            Check(player.Identity == "new-episode" && player.PauseRequested && !player.IsPlaying &&
                Math.Abs(player.PositionSeconds - 4) <= .25, "explicit initial controls reached actual native session");
        });
        yield return Test("commit before real frame readiness never publishes a blocked candidate", async () =>
        {
            await using var origin = Fixture("blocked");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var intent = Intent();
            var pending = player.PrepareRuntimeMediaAsync(intent, authority.CreateLease(ResourceId,
                origin.Uri(RootPath), scope.Token), Input("progressive"));
            await Until(() => origin.Blocked > 0, "candidate blocked");
            bool published = false;
            await Reject(() => player.CommitRuntimeMediaAsync(intent, () => published = true, default));
            await Reject(() => pending);
            Check(!published && !player.HasMedia && player.ObservedVideoOutput is null, "no paused/Playing surrogate for frames");
        });
        foreach (string action in new[] { "cancel", "revoke", "discard", "close" })
            yield return Test("blocked native AVIO drains on " + action, async () =>
            {
                await using var origin = Fixture("blocked");
                using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
                using var cancel = new CancellationTokenSource();
                int releases = 0;
                await using var authority = Authority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
                await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                var intent = Intent();
                var pending = player.PrepareRuntimeMediaAsync(intent, authority.CreateLease(ResourceId,
                    origin.Uri(RootPath), scope.Token), Input("progressive"), cancel.Token);
                await Until(() => origin.Blocked > 0, "root AVIO blocked");
                Task stop = Task.CompletedTask;
                if (action == "cancel") cancel.Cancel();
                if (action == "revoke") scope.Cancel();
                if (action == "discard") stop = player.DiscardRuntimeMediaAsync(intent);
                if (action == "close") stop = player.DisposeAsync().AsTask();
                await Reject(() => pending);
                await stop.WaitAsync(TimeSpan.FromSeconds(8));
                Check(releases == 1 && !player.HasMedia && Field(player, "_handoffSession") is null,
                    "completion waited for rejected native session and lease");
            });
        yield return Test("ready candidate discard preserves one failed retirement receipt and a healthy donor", async () =>
        {
            await using var good = Fixture("progressive"); await using var rejected = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            int donorReleases = 0, releases = 0;
            await using var donorAuthority = Authority(runtime.Token, _ => { donorReleases++; return ValueTask.CompletedTask; });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async ValueTask Release(string _)
            {
                Interlocked.Increment(ref releases); entered.TrySetResult();
                await finish.Task.ConfigureAwait(false);
                throw new IOException("synthetic-private-release-canary");
            }
            var failedAuthority = Authority(runtime.Token, Release);
            var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            try
            {
                var first = Intent();
                await player.PrepareRuntimeMediaAsync(first,
                    donorAuthority.CreateLease(ResourceId, good.Uri(RootPath), lifetime.Token), Input("progressive"));
                await player.CommitRuntimeMediaAsync(first, () => { });
                await player.PauseAsync(); await player.SeekAsync(7);
                await Until(() => Math.Abs(player.PositionSeconds - 7) < .5, "healthy donor time");
                var donor = Session(player)!; var donorScope = Scope(player);
                var intent = Intent(donorScope);
                await PrepareBeforeRelease(player.PrepareRuntimeMediaAsync(intent,
                    failedAuthority.CreateLease(ResourceId, rejected.Uri(RootPath), lifetime.Token), Input("progressive")),
                    entered.Task, finish);
                var candidate = Field(player, "_handoffSession")!;
                Check(DisplayedPictures(candidate) > 0, "the rejected candidate was actually ready, not a failed open");
                var native = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                var discard = player.DiscardRuntimeMediaAsync(intent);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var duplicate = player.DiscardRuntimeMediaAsync(intent);
                Check(!discard.IsCompleted && !duplicate.IsCompleted && ReferenceEquals(Session(player), donor),
                    "all callers join the exact in-progress release; the donor is untouched");
                finish.TrySetResult();
                await ReleaseFailure(() => discard);
                await ReleaseFailure(() => duplicate);
                await ReleaseFailure(() => player.DiscardRuntimeMediaAsync(intent));
                Check(releases == 1 && native.NativeReference == IntPtr.Zero &&
                    Field(player, "_handoffSession") is null && Field(player, "_runtimeHandoffWindow") is null,
                    "failure is reported only after candidate native and owned window drain");
                Check(donorReleases == 0 && ReferenceEquals(Session(player), donor) && Scope(player) == donorScope &&
                    ((LibVLCSharp.Shared.MediaPlayer)Field(donor, "Player")!).NativeReference != IntPtr.Zero &&
                    player.HasMedia && player.PauseRequested && Math.Abs(player.PositionSeconds - 7) < .5,
                    "candidate release failure retains the precise live donor, pause and actual time");
                await player.SeekRuntimeAsync(donorScope, 9);
                await Until(() => Math.Abs(player.PositionSeconds - 9) < .5, "healthy donor controls after failed discard");
                await ReleaseFailure(() => player.DisposeAsync().AsTask());
                Check(donorReleases == 1, "later close still drains the retained donor despite an earlier release fault");
                await ReleaseFailure(() => failedAuthority.DisposeAsync().AsTask());
            }
            finally
            {
                finish.TrySetResult();
                await CleanupReleaseFailure(player);
                await CleanupReleaseFailure(failedAuthority);
            }
        });
        foreach (bool withDonor in new[] { false, true })
            yield return Test("first close drains ready failed candidate and all remaining owners donor=" + withDonor, async () =>
            {
                await using var rejected = Fixture("progressive"); await using var good = Fixture("progressive");
                using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
                int releases = 0, donorReleases = 0;
                await using var donorAuthority = Authority(runtime.Token, _ => { donorReleases++; return ValueTask.CompletedTask; });
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async ValueTask Release(string _)
                {
                    Interlocked.Increment(ref releases); entered.TrySetResult();
                    await finish.Task.ConfigureAwait(false);
                    throw new IOException("synthetic-private-close-canary");
                }
                var failedAuthority = Authority(runtime.Token, Release);
                var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
                object? donor = null;
                var child = new OutputDrain();
                try
                {
                    if (withDonor)
                    {
                        var first = Intent();
                        await player.PrepareRuntimeMediaAsync(first,
                            donorAuthority.CreateLease(ResourceId, good.Uri(RootPath), lifetime.Token), Input("progressive"));
                        await player.CommitRuntimeMediaAsync(first, () => { });
                        await player.PauseAsync();
                        donor = Session(player);
                        _ = await player.OwnRuntimeOutputAsync(Scope(player), child);
                    }
                    var intent = Intent();
                    await PrepareBeforeRelease(player.PrepareRuntimeMediaAsync(intent,
                        failedAuthority.CreateLease(ResourceId, rejected.Uri(RootPath), lifetime.Token), Input("progressive")),
                        entered.Task, finish);
                    var candidate = Field(player, "_handoffSession")!;
                    Check(DisplayedPictures(candidate) > 0, "close starts from ready but uncommitted candidate");
                    var native = (LibVLCSharp.Shared.MediaPlayer)Field(candidate, "Player")!;
                    var close = player.DisposeAsync().AsTask();
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    var repeated = player.DisposeAsync().AsTask();
                    Check(!close.IsCompleted && !repeated.IsCompleted, "first and repeated close await candidate release");
                    finish.TrySetResult();
                    if (withDonor)
                    {
                        await child.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                        Check(!close.IsCompleted && !repeated.IsCompleted, "candidate failure cannot skip donor's actual child drain");
                        child.Finish.TrySetResult();
                    }
                    await ReleaseFailure(() => close);
                    await ReleaseFailure(() => repeated);
                    await ReleaseFailure(() => player.DisposeAsync().AsTask());
                    await ReleaseFailure(() => player.DiscardRuntimeMediaAsync(intent));
                    Check(releases == 1 && native.NativeReference == IntPtr.Zero && Session(player) is null &&
                        Field(player, "_handoffSession") is null && Field(player, "_runtimeHandoffWindow") is null &&
                        Field(player, "_engine") is null && !player.HasMedia && player.LastErrorCode == PlaybackError.ReleaseFailed,
                        "first close reports fixed failure after native, window, engine and logical state drain");
                    if (withDonor)
                        Check(donorReleases == 1 && child.Disposals == 1 &&
                            ((LibVLCSharp.Shared.MediaPlayer)Field(donor!, "Player")!).NativeReference == IntPtr.Zero,
                            "the donor and its attached output were not skipped by a candidate exception");
                    await ReleaseFailure(() => failedAuthority.DisposeAsync().AsTask());
                }
                finally
                {
                    finish.TrySetResult(); child.Finish.TrySetResult();
                    await CleanupReleaseFailure(player);
                    await CleanupReleaseFailure(failedAuthority);
                }
            });
        yield return Test("old runtime discard receipt cannot retire a newer ready candidate", async () =>
        {
            await using var a = Fixture("progressive"); await using var b = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int releases = 0;
            async ValueTask Release(string _)
            {
                releases++; entered.TrySetResult(); await finish.Task.ConfigureAwait(false);
            }
            await using var authorityA = Authority(runtime.Token, Release);
            await using var authorityB = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var first = Intent(); var second = Intent();
            Task discard = Task.CompletedTask, preparation = Task.CompletedTask, opening = Task.CompletedTask;
            try
            {
                opening = player.PrepareRuntimeMediaAsync(first,
                    authorityA.CreateLease(ResourceId, a.Uri(RootPath), lifetime.Token), Input("progressive"));
                await PrepareBeforeRelease(opening, entered.Task, finish);
                discard = player.DiscardRuntimeMediaAsync(first);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                preparation = player.PrepareRuntimeMediaAsync(second,
                    authorityB.CreateLease(ResourceId, b.Uri(RootPath), lifetime.Token), Input("progressive"));
                var duplicate = player.DiscardRuntimeMediaAsync(first);
                Check(!preparation.IsCompleted && !duplicate.IsCompleted,
                    "new preparation and old-intent duplicate both wait for the exact physical retirement");
                finish.TrySetResult();
                await discard; await duplicate; await preparation;
                var candidate = Field(player, "_handoffSession");
                Check(candidate is not null && DisplayedPictures(candidate) > 0, "new exact candidate is ready");
                await player.DiscardRuntimeMediaAsync(first);
                Check(releases == 1 && ReferenceEquals(candidate, Field(player, "_handoffSession")),
                    "late old-intent discard reuses its own outcome without touching the new candidate");
                await player.CommitRuntimeMediaAsync(second, () => { });
                Check(player.Identity == second.Identity && player.HasMedia, "new intent remains the only valid committer");
            }
            finally { finish.TrySetResult(); await Task.WhenAll(opening, discard, preparation); }
        });
        yield return Test("rapid preparation cannot publish or discard another candidate", async () =>
        {
            await using var blocked = Fixture("blocked"); await using var good = Fixture("progressive");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            int oldReleases = 0;
            await using var oldAuthority = Authority(runtime.Token, _ => { oldReleases++; return ValueTask.CompletedTask; });
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            var oldIntent = Intent();
            var nextIntent = Intent();
            var old = player.PrepareRuntimeMediaAsync(oldIntent, oldAuthority.CreateLease(ResourceId, blocked.Uri(RootPath), scope.Token), Input("progressive"));
            await Until(() => blocked.Blocked > 0, "old read blocked");
            var next = player.PrepareRuntimeMediaAsync(nextIntent, authority.CreateLease(ResourceId, good.Uri(RootPath), scope.Token), Input("progressive"));
            await Reject(() => old); await next;
            var prepared = Field(player, "_handoffSession");
            bool stalePublished = false;
            await Reject(() => player.CommitRuntimeMediaAsync(oldIntent, () => stalePublished = true));
            await player.DiscardRuntimeMediaAsync(oldIntent);
            var copiedIntent = nextIntent with { };
            await Reject(() => player.CommitRuntimeMediaAsync(copiedIntent, () => stalePublished = true));
            await player.DiscardRuntimeMediaAsync(copiedIntent);
            await Reject(() => player.CommitHandoffAsync(() => stalePublished = true, default));
            await player.DiscardHandoffAsync();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var rejectedMusic = new RuntimeMediaTests.WaveLease();
            await Reject(() => player.PrepareReadHandoffAsync(rejectedMusic, cancelled.Token));
            Check(!stalePublished && ReferenceEquals(prepared, Field(player, "_handoffSession")),
                "late runtime and music callbacks cannot commit or discard another owner's input");
            Check(rejectedMusic.Disposals == 1, "an already cancelled preparation releases only its own input");
            await player.CommitRuntimeMediaAsync(nextIntent, () => { }, default);
            Check(player.HasMedia && oldReleases == 1 && player.ObservedVideoOutput is { DisplayedPictures: > 0 }, "only latest candidate publishes");
        });
        yield return Test("FFmpeg HLS follows a child on another local port with one receipt", async () =>
        {
            await using var foreign = Fixture("hls"); await using var origin = Fixture("hls");
            origin.Route = _ => new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\n" + foreign.Uri(RootPath) + "\n"));
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            int releases = 0;
            await using var authority = new RuntimeMediaAuthority(runtime.Token, _ => { releases++; return ValueTask.CompletedTask; });
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy); var intent = Intent();
            await player.PrepareRuntimeMediaAsync(intent, authority.CreateLease(ResourceId, origin.Uri(RootPath), scope.Token), Input("hls"));
            Check(foreign.Requests.Count > 1 && DisplayedPictures(Field(player, "_handoffSession")!) > 0 && releases == 0,
                "actual foreign manifest and segments, not a URL admission surrogate");
            await player.DiscardRuntimeMediaAsync(intent); Check(releases == 1, "only the exact offered receipt releases");
        });
        yield return Test("ordinary typed HTTP accepts loopback and redirects without a lease", async () =>
        {
            await using var origin = Fixture("progressive"); await using var redirect = Fixture("progressive");
            redirect.Route = _ => Reply.Redirect(origin.Uri(RootPath).AbsoluteUri);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy); var intent = Intent();
            await player.PrepareRuntimeMediaAsync(intent, redirect.Uri(RootPath), Input("progressive"));
            Check(origin.Requests.Count > 0 && redirect.Requests.Count > 0 &&
                DisplayedPictures(Field(player, "_handoffSession")!) > 0, "actual direct typed output without origin permissions");
            await player.DiscardRuntimeMediaAsync(intent);
        });
        yield return Test("FATE encrypted H264 is actually decoded using legal KID key pair", async () =>
        {
            byte[] file = FixtureFile("cenc.mp4", "c9604942731afc2d8b6eacf8e016fa7d1b35860b89f0e86d54dc528dfbd97b6e");
            await using var origin = Fixture("progressive"); origin.Route = _ => new Reply(file, "video/mp4");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            using var input = new NativeVideoInput(NativeVideoFormat.Progressive, NativeClearKeySet.FromHex(
                new Dictionary<string, string> { ["abba271e8bcf552bbd2e86a434a9a5d9"] = "12345678901234567890123456789012" }));
            await player.PrepareRuntimeMediaAsync(Intent(), authority.CreateLease(ResourceId, origin.Uri(RootPath), scope.Token), input);
            Check(DisplayedPictures(Field(player, "_handoffSession")!) > 0, "encrypted H264 decode, not just parser success");
        });
        yield return Test("HEVC Main10 coded pictures pass through private demux to actual VLC vout", async () =>
        {
            byte[] raw = FixtureFile("main10.bit", "96c4f1a6b824cc51433d87ebc11bffcc6c2fe3de3ed18339316803e8bbc9be17");
            byte[] transport = Main10TransportStream(raw);
            await using var origin = Fixture("progressive"); origin.Route = _ => new Reply(transport, "video/mp2t");
            using var runtime = new CancellationTokenSource(); using var scope = new CancellationTokenSource();
            await using var authority = Authority(runtime.Token);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            await player.PrepareRuntimeMediaAsync(Intent(), authority.CreateLease(ResourceId, origin.Uri(RootPath), scope.Token), Input("progressive"));
            Check(DisplayedPictures(Field(player, "_handoffSession")!) > 0, "real Main10 decode and display counter");
            Console.WriteLine("OBSERVED FATE DBLK_A_MAIN10_VIXS_3 coded HEVC through synthetic TS -> private demux -> VLC vout; no HDR or hardware inference");
        });
    }

    private sealed class OutputDrain : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Disposals++; Entered.TrySetResult();
            await Finish.Task;
        }
    }

    private sealed class HeaderReceipt(Uri address, IReadOnlyDictionary<string, string> headers,
        CancellationTokenSource revoked) : INativeMediaResourceLease
    {
        internal int Disposals;
        public Uri Address => address;
        public IReadOnlyDictionary<string, string> Headers => headers;
        public CancellationToken Revoked => revoked.Token;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals); revoked.Cancel();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PreflightReceipt(Uri address, bool fail) : INativeMediaResourceLease
    {
        private readonly CancellationTokenSource revoked = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool InvalidHeaders;
        internal int Disposals;
        public Uri Address => address;
        public IReadOnlyDictionary<string, string> Headers => InvalidHeaders ? new Dictionary<string, string> { ["Host"] = "fixture" } : new Dictionary<string, string>();
        public CancellationToken Revoked => revoked.Token;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals); revoked.Cancel(); Entered.TrySetResult();
            await Finish.Task.ConfigureAwait(false);
            if (fail) throw new IOException("synthetic-outer-receipt-private-canary");
        }
    }

    private sealed class PreflightReadReceipt(PreflightReceipt receipt) : INativeMediaReadLease
    {
        public Stream Content => throw new IOException("synthetic-rejected-read-canary");
        public long Length => 0;
        public CancellationToken Revoked => receipt.Revoked;
        public ValueTask DisposeAsync() => receipt.DisposeAsync();
    }

    private sealed class FailingOutput : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Disposals++; Entered.TrySetResult();
            await Finish.Task.ConfigureAwait(false);
            throw new IOException(@"C:\synthetic-private-child-canary\player.exe https://private.invalid/media?canary=secret");
        }
    }

    private static void SetWorkerObserver(NativePlayback player, Action<LibVLCSharp.Shared.MediaPlayer?>? observer) =>
        typeof(NativePlayback).GetField("_handoffWorkerCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(player, observer);

    private const string ResourceId = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Child = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Prefix = "/media/" + ResourceId + "/";
    private const string RootPath = Prefix + Child + "/root";
    private static RuntimeMediaPlaybackIntent Intent(NativePlaybackScope? donor = null) =>
        new("film", Guid.NewGuid().ToString("N"), "", 0, false, donor);
    private static NativePlaybackScope Scope(NativePlayback player)
    {
        var output = player.ObservedVideoOutput ?? throw new InvalidOperationException("Expected real donor output");
        return new(output.OwnerId, output.Identity, output.Generation);
    }
    private static NativeVideoInput Input(string kind) => new(kind.StartsWith("hls", StringComparison.Ordinal) ? NativeVideoFormat.Hls :
        kind.StartsWith("dash", StringComparison.Ordinal) ? NativeVideoFormat.Dash : NativeVideoFormat.Progressive,
        kind == "dash-cenc" ? NativeClearKeySet.FromHex(new Dictionary<string, string> { [VideoKid] = VideoKey, [AudioKid] = AudioKey }) : null);
    private static RuntimeMediaAuthority Authority(CancellationToken token, Func<string, ValueTask>? release = null) =>
        new(token, release ?? (_ => ValueTask.CompletedTask));
    private static Origin Fixture(string kind)
    {
        var origin = new Origin { RequireAuthorization = false };
        bool encrypted = kind == "dash-cenc";
        var video = Enumerable.Range(0, Seconds).Select(i => VideoFragment(i, encrypted)).ToArray();
        var audio = Enumerable.Range(0, Seconds).Select(i => AudioFragment(i, encrypted)).ToArray();
        byte[] initialization = VideoInit(encrypted);
        byte[] progressive = initialization.Concat(video.SelectMany(bytes => bytes)).ToArray();
        byte[] combined = initialization.Concat(video.SelectMany(bytes => bytes)).ToArray();
        string Hls()
        {
            string list = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n";
            if (kind == "hls-aes") list += "#EXT-X-KEY:METHOD=AES-128,URI=\"key\",IV=0x00000000000000000000000000000000\n";
            list += kind == "hls-range" ? $"#EXT-X-MAP:URI=\"blob\",BYTERANGE=\"{initialization.Length}@0\"\n" : "#EXT-X-MAP:URI=\"video-init\"\n";
            int at = initialization.Length;
            for (int i = 0; i < Seconds; i++)
            {
                list += "#EXTINF:1,\n";
                if (kind == "hls-range") { list += $"#EXT-X-BYTERANGE:{video[i].Length}@{at}\nblob\n"; at += video[i].Length; }
                else list += $"video-{i}\n";
            }
            return list + "#EXT-X-ENDLIST\n";
        }
        origin.Route = path =>
        {
            if (kind == "blocked") return new Reply(progressive, "video/mp4") { BlockBody = true };
            if (path == RootPath)
                return kind.StartsWith("hls", StringComparison.Ordinal)
                    ? new Reply(Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\n" + origin.Uri(Prefix + Child + "/variant") + "\n"))
                    : kind.StartsWith("dash", StringComparison.Ordinal) ? new Reply(Text(Dash()), "application/dash+xml")
                    : new Reply(progressive, "video/mp4");
            string file = path[(path.LastIndexOf('/') + 1)..];
            if (file == "variant") return new Reply(Text(Hls()));
            if (file == "blob") return new Reply(combined, "video/mp4");
            if (file == "key") return new Reply(Convert.FromHexString(VideoKey), "application/octet-stream");
            if (file == "video-init") return new Reply(kind == "hls-aes" ? Aes128(initialization) : initialization, "video/mp4");
            if (file == "audio-init") return new Reply(AudioInit(encrypted), "audio/mp4");
            if (file.StartsWith("video-", StringComparison.Ordinal) && int.TryParse(file[6..], out int v) && v >= 0 && v < video.Length)
                return new Reply(kind == "hls-aes" ? Aes128(video[v]) : video[v], "video/mp4");
            if (file.StartsWith("audio-", StringComparison.Ordinal) && int.TryParse(file[6..], out int a) && a >= 0 && a < audio.Length)
                return new Reply(audio[a], "audio/mp4");
            return new Reply([]) { Status = 404 };
        };
        return origin;
    }
    private static byte[] FixtureFile(string name, string hash)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestWorkspace.SourceRoot, ".cache/windows-video-kernel/fixtures", name));
        Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash, "locked public test sample");
        return bytes;
    }
    private static long DisplayedPictures(object session) =>
        ((LibVLCSharp.Shared.Media)Field(session, "Media")!).Statistics.DisplayedPictures;
    private static TestCase Test(string name, Func<Task> body) => new("video-kernel: " + name, () => Sta(body));
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException("Video kernel: " + reason); }
    private static void Error(Action action, string code)
    {
        try { action(); }
        catch (PlaybackException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (PlaybackException error)
        { Check(error.Message == error.Code && error.InnerException is null, "sanitized failure"); return; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Candidate was not rejected");
    }
    private static async Task ReleaseFailure(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed)
        {
            Check(error.Message == error.Code && error.InnerException is null, "release errors remain fixed and sanitized");
            return;
        }
        throw new InvalidOperationException("Expected the exact ReleaseFailed outcome");
    }
    private static async Task PrepareBeforeRelease(Task preparation, Task releaseEntered, TaskCompletionSource finish)
    {
        // A genuine preparation failure may already be awaiting its cleanup.
        // Open the test gate so that failure is reported instead of hanging.
        if (await Task.WhenAny(preparation, releaseEntered) != preparation) finish.TrySetResult();
        await preparation;
    }
    private static async Task CleanupReleaseFailure(IAsyncDisposable owner)
    {
        try { await owner.DisposeAsync(); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed) { }
    }
    private static async Task Until(Func<bool> predicate, string reason)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        { if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(reason); await Task.Delay(20); }
    }
}
