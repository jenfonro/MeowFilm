using System.Text.Json;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmExternalHandoffTests
{
    private static TestCase Test(string name, Func<Task> body) =>
        new("film-external-handoff: " + name, () => MusicQualityConsumerTests.Sta(body));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Called(FilmPlaybackRig rig, string action, string id) =>
        rig.Runtime.Calls.Any(call => call.Method == "film.session.command" &&
            FilmJson.Text(call.Parameters, "action") == action && FilmJson.Text(call.Parameters, "attemptId") == id);
    private static void Observe(string phase, object evidence) =>
        Console.WriteLine("OBSERVED film-external-handoff " + JsonSerializer.Serialize(new { phase, evidence }));

    public static IEnumerable<TestCase> Cases()
    {
        foreach (bool paused in new[] { false, true })
        {
            yield return Test($"close during candidate volume activation prevents late unpause and joins child and receipt; paused={paused}",
                () => CloseDuringActivationAsync(paused));
            foreach (string interruption in new[] { "publication", "input", "donor-revoked" })
                yield return Test($"child drain then {interruption} preserves only authorized output; paused={paused}",
                    () => InterruptedCommitAsync(interruption, paused));
            yield return Test($"explicit new mpv rate failure keeps the live donor; paused={paused}",
                () => RateFailureAsync(paused));
            foreach (bool newCandidate in new[] { false, true })
                foreach (bool attachment in new[] { false, true })
                    yield return Test($"{(newCandidate ? "candidate" : "explicit mpv")} " +
                        $"{(attachment ? "attachment" : "activation")} failure drains before exact native fallback; paused={paused}",
                        () => FallbackAsync(newCandidate, attachment, paused));
            yield return Test($"successful same-episode transfer and repeated close dispose once; paused={paused}",
                () => SuccessfulAsync(paused));
            foreach (string outcome in new[] { "STALE_SESSION", "STORAGE_ERROR", "accepted" })
                yield return Test($"block {outcome} during child retirement preserves the exact donor and prepared receipt; paused={paused}",
                    () => DeferredBlockAsync(outcome, paused));
        }
        foreach (string action in new[] { "media.ended", "media.failed", "stop" })
            yield return Test($"{action} keeps the final external clock and cannot terminate a replacement", async () =>
            {
                await TerminalClockAsync(action);
                await DonorTerminalAsync(action);
                foreach (bool sameInput in new[] { true, false })
                    await ReplacedTerminalAsync(action, sameInput);
                if (action == "stop") await StopPendingAsync();
            });
        yield return Test("external probe counts every dispose call separately from physical completion", async () =>
        {
            var entered = Gate(); var finish = Gate();
            var receipt = new FilmMediaReceipt("counter", "counter-resource", new("https://example.org/video.mp4"),
                null, "", 0, false, default, default, default);
            var child = new FilmExternalProbe(receipt)
            { Draining = async () => { entered.TrySetResult(); await finish.Task; } };
            var first = child.DisposeAsync().AsTask();
            var second = child.DisposeAsync().AsTask();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.DisposeCalls == 2 && child.Disposals == 1 && child.DisposalCompletions == 0 &&
                    !first.IsCompleted && !second.IsCompleted, "raw invocations cannot disappear behind probe idempotence");
                finish.TrySetResult(); await Task.WhenAll(first, second);
                Check(child.DisposalCompletions == 1, "two callers join one actual physical completion");
                Observe("counter-contract", new { child.DisposeCalls, child.Disposals, child.DisposalCompletions });
            }
            finally { finish.TrySetResult(); }
        });
    }

    private static async Task StartAsync(FilmPlaybackRig rig, bool paused)
    {
        await rig.OpenAsync();
        rig.Runtime.Attempt("A", Candidate("A"));
        await Until(() => rig.Native.SettledIdentity == "A" && Called(rig, "media.started", "A"));
        await rig.Session.SeekAsync(11);
        await rig.Session.SetPausedAsync(paused);
        await rig.Session.RateAsync(1.5);
        await rig.Session.AspectAsync("4:3");
        await rig.Session.ExternalAsync("fixture-a-mpv.exe");
        await rig.Session.SeekAsync(81);
        Check(rig.Session.HasExternalPlayer && rig.Native.Paused && rig.Native.Position == 11 &&
            rig.Session.Position == 81 && rig.Session.IsPlaying == !paused,
            "the actual external clock differs from its paused native donor before handoff");
    }

    private static async Task TerminalClockAsync(string action)
    {
        var rateEntered = Gate(); var rateFinish = Gate();
        var priorEntered = Gate(); var priorFinish = Gate(); var finalEntered = Gate(); var finalFinish = Gate();
        var childEntered = Gate(); var childFinish = Gate(); var releaseEntered = Gate(); var releaseFinish = Gate();
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        try
        {
            await StartAsync(rig, paused: false);
            if (action == "stop") await rig.Session.LeaveAsync(retainedOwner: true);
            var child = players.Single();
            var failures = new List<string>();
            rig.Session.OperationFailed += (_, code) => failures.Add(code);
            int before = rig.Runtime.Calls.Count, writes = 0;
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "media.progress")
                {
                    if (++writes == 1) { priorEntered.TrySetResult(); await priorFinish.Task; }
                    else { finalEntered.TrySetResult(); await finalFinish.Task; }
                }
                if (method == "film.session.media.release" && FilmJson.Text(input, "attemptId") == "A")
                { releaseEntered.TrySetResult(); await releaseFinish.Task; }
                return rig.Runtime.Handle(method, input);
            };
            child.Draining = async () => { childEntered.TrySetResult(); await childFinish.Task; };
            child.SetClock(80, 230, true);
            rig.Native.Pulse("PositionSeconds");
            await priorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            child.SettingRate = async _ => { rateEntered.TrySetResult(); await rateFinish.Task; };
            var rate = rig.Session.RateAsync(2);
            await rateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            child.SetClock(81, 240, false);
            Check(rig.Native.Position == 11 && rig.Native.Duration == 600 && rig.Native.Paused,
                "native donor still has a different position, duration and pause state at the external terminal");
            child.Finish(action);
            Check(!child.Running, "terminal notification is emitted only after Running becomes false");
            child.SetClock(191, 999, true);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            rateFinish.TrySetResult();
            await rate.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(!finalEntered.Task.IsCompleted && !childEntered.Task.IsCompleted &&
                !rig.Runtime.Calls.Skip(before).Any(call => FilmJson.Text(call.Parameters, "action") == action),
                "the older progress write settles before final progress or terminal feedback");
            priorFinish.TrySetResult();
            await finalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var final = rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "media.progress").Parameters;
            Check(FilmJson.Text(final, "attemptId") == "A" && FilmJson.Number(final, "position") == 81 &&
                FilmJson.Number(final, "duration") == 240 && !FilmJson.Bool(final, "paused") &&
                !childEntered.Task.IsCompleted,
                "final progress uses the exact terminal sender snapshot, not native 11 or the later external 191");
            finalFinish.TrySetResult();
            await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var commands = rig.Runtime.Calls.Skip(before).Where(call => call.Method == "film.session.command").ToArray();
            Check(commands.Length == 3 && FilmJson.Text(commands[0].Parameters, "action") == "media.progress" &&
                FilmJson.Number(commands[0].Parameters, "position") == 80 &&
                FilmJson.Text(commands[1].Parameters, "action") == "media.progress" &&
                FilmJson.Text(commands[2].Parameters, "action") == action &&
                (action == "stop" || FilmJson.Text(commands[2].Parameters, "attemptId") == "A") &&
                child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 0 &&
                !releaseEntered.Task.IsCompleted,
                "one final snapshot precedes the exact terminal and real child drain, without premature receipt release");
            childFinish.TrySetResult();
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(child.DisposalCompletions == 1 && rig.Native.Stops == 1 && !rig.Runtime.Released.Contains("A"),
                "child completion is not the resource release acknowledgement");
            releaseFinish.TrySetResult();
            await Until(() => rig.Runtime.Released.Contains("A") && rig.Session.MediaIdentity is null &&
                !rig.Session.HasOutput && (action != "stop" || !rig.Session.HasSession));
            await rig.Session.CloseSessionAsync(); await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 1 &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "A") == 1 && failures.Count == 0 &&
                !Called(rig, "media.started", "B") && !Called(rig, "media.failed", "B"),
                "terminal and repeated close join one physical child and exact receipt release without false feedback");
            Observe("terminal-clock", new { action, final, nativePosition = rig.Native.Position,
                nativeDuration = rig.Native.Duration, nativePaused = rig.Native.Paused,
                child.DisposeCalls, child.Disposals, child.DisposalCompletions });
        }
        finally
        {
            rateFinish.TrySetResult(); priorFinish.TrySetResult(); finalFinish.TrySetResult();
            childFinish.TrySetResult(); releaseFinish.TrySetResult();
        }
    }

    private static async Task DonorTerminalAsync(string action)
    {
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        await StartAsync(rig, paused: false);
        var external = players.Single();
        var failures = new List<string>();
        rig.Session.OperationFailed += (_, code) => failures.Add(code);
        int before = rig.Runtime.Calls.Count;
        external.SetClock(81, 240, false); external.Finish(action);
        // The donor can retire before the already queued external notification is dispatched.
        await rig.Native.TerminalAsync(ended: action == "media.ended");
        await Until(() => rig.Session.MediaIdentity is null && external.DisposalCompletions == 1);
        var commands = rig.Runtime.Calls.Skip(before).Where(call => call.Method == "film.session.command").ToArray();
        Check(commands.Length == 2 && FilmJson.Text(commands[0].Parameters, "action") == "media.progress" &&
            FilmJson.Text(commands[0].Parameters, "attemptId") == "A" &&
            FilmJson.Number(commands[0].Parameters, "position") == 81 &&
            FilmJson.Number(commands[0].Parameters, "duration") == 240 && !FilmJson.Bool(commands[0].Parameters, "paused") &&
            FilmJson.Text(commands[1].Parameters, "action") == action &&
            (action == "stop" || FilmJson.Text(commands[1].Parameters, "attemptId") == "A") && failures.Count == 0,
            "a paused native donor's terminal cannot take ownership from the exact external terminal or report native 11");
        await rig.Session.CloseSessionAsync(); await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
        Check(external.DisposeCalls == 1 && external.Disposals == 1 && external.DisposalCompletions == 1 &&
            rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                FilmJson.Text(call.Parameters, "attemptId") == "A") == 1,
            "donor retirement and external terminal share the exact resource release without duplicate disposal");
        Observe("terminal-donor-retired", new { action, feedback = commands.Select(call => call.Parameters),
            external.DisposeCalls, external.Disposals, external.DisposalCompletions });
    }

    private static async Task ReplacedTerminalAsync(string action, bool sameInput)
    {
        var entered = Gate(); var finish = Gate();
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        try
        {
            await StartAsync(rig, paused: false);
            var old = players.Single();
            old.Draining = async () => { entered.TrySetResult(); await finish.Task; };
            var failures = new List<string>();
            rig.Session.OperationFailed += (_, code) => failures.Add(code);
            Task replacement = Task.CompletedTask;
            if (sameInput) replacement = rig.Session.ExternalAsync("fixture-b-mpv.exe");
            else rig.Runtime.Attempt("B", Candidate("B"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int before = rig.Runtime.Calls.Count;
            old.SetClock(81, 240, false);
            old.Finish(action);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(!replacement.IsCompleted || !sameInput,
                "the terminal waits behind the replacement's control lock while the old sender is still attached");
            finish.TrySetResult();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            string expected = sameInput ? "A" : "B";
            await Until(() => players.Count == 2 && rig.Native.SettledIdentity == expected &&
                rig.Session.HasExternalPlayer && players[1].Running && !players[1].Paused);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var next = players[1];
            Check(rig.Session.HasOutput && rig.Session.MediaIdentity == expected &&
                rig.Native.Scope?.Identity == expected && next.DisposeCalls == 0 && rig.Native.Stops == 0 &&
                !rig.Runtime.Calls.Skip(before).Any(call =>
                    call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == action) &&
                !rig.Runtime.Released.Contains(expected) && failures.Count == 0,
                "a queued old terminal cannot report or stop either a new player on A or the new attempt B");
            next.SetClock(193, 333, false);
            await rig.Session.SeekAsync(194);
            var feedback = rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "media.progress").Parameters;
            Check(next.Position == 194 && FilmJson.Text(feedback, "attemptId") == expected &&
                FilmJson.Number(feedback, "position") == 194 && FilmJson.Number(feedback, "duration") == 333 &&
                !FilmJson.Bool(feedback, "paused") && old.DisposeCalls == 1 && old.DisposalCompletions == 1,
                "replacement remains controllable and only its accurate attempt owns subsequent progress");
            await rig.Session.CloseSessionAsync(); await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 1) &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == expected) == 1,
                "ignoring a stale terminal neither leaks nor double-disposes either output");
            Observe("terminal-replaced", new { action, sameInput, expected, feedback,
                children = players.Select(child => new { child.DisposeCalls, child.Disposals, child.DisposalCompletions }) });
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task StopPendingAsync()
    {
        var entered = Gate(); var finish = Gate();
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        try
        {
            await StartAsync(rig, paused: false);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var child = players.Single();
            var failures = new List<string>();
            rig.Session.OperationFailed += (_, code) => failures.Add(code);
            CancellationToken preparing = default;
            rig.Native.Preparing = async token => { preparing = token; entered.TrySetResult(); await finish.Task; };
            rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
                method == "film.session.command" && FilmJson.Text(input, "action") == "media.progress"
                    ? FilmRuntimeProbe.Failure("STORAGE_FAILED") : rig.Runtime.Handle(method, input));
            rig.Runtime.Attempt("B", Candidate("B"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            child.SetClock(81, 240, false); child.Finish("stop");
            await Until(() => preparing.IsCancellationRequested && failures.Contains("STORAGE_FAILED") &&
                rig.Runtime.Calls.Any(call => FilmJson.Text(call.Parameters, "action") == "stop"));
            Check(!rig.Runtime.Released.Contains("A") && !rig.Runtime.Released.Contains("B") &&
                child.DisposeCalls == 0 && rig.Native.Stops == 0,
                "stop cancels B publication but still joins its admitted physical prepare before draining A");
            finish.TrySetResult();
            await Until(() => rig.Runtime.Released.Contains("A") && rig.Runtime.Released.Contains("B") &&
                rig.Session.MediaIdentity is null);
            var final = rig.Runtime.Calls.Last(call => FilmJson.Text(call.Parameters, "action") == "media.progress").Parameters;
            Check(FilmJson.Text(final, "attemptId") == "A" && FilmJson.Number(final, "position") == 81 &&
                FilmJson.Number(final, "duration") == 240 && !FilmJson.Bool(final, "paused") &&
                !Called(rig, "media.started", "B") && !Called(rig, "media.failed", "B") &&
                rig.Native.Stops == 1 && !rig.Session.HasOutput && child.DisposalCompletions == 1,
                "a failed final-progress receipt remains visible while stop still releases A and cancelled B exactly");
            await rig.Session.CloseSessionAsync(); await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 1 &&
                new[] { "A", "B" }.All(id => rig.Runtime.Calls.Count(call =>
                    call.Method == "film.session.media.release" && FilmJson.Text(call.Parameters, "attemptId") == id) == 1),
                "failed progress cannot hide a pending resource leak or duplicate the accepted child's physical drain");
            Observe("terminal-stop-progress-failure", new { final, failures, released = rig.Runtime.Released,
                child.DisposeCalls, child.Disposals, child.DisposalCompletions });
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task CloseDuringActivationAsync(bool paused)
    {
        var volumeEntered = Gate(); var volumeFinish = Gate();
        var childEntered = Gate(); var childFinish = Gate();
        var releaseEntered = Gate(); var releaseFinish = Gate();
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt);
            if (receipt.AttemptId == "B")
            {
                child.SettingVolume = async _ => { volumeEntered.TrySetResult(); await volumeFinish.Task; };
                child.Draining = async () => { childEntered.TrySetResult(); await childFinish.Task; };
            }
            players.Add(child); return Task.FromResult<IFilmExternalPlayer>(child);
        });
        try
        {
            await StartAsync(rig, paused);
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.media.release" && FilmJson.Text(input, "attemptId") == "B")
                { releaseEntered.TrySetResult(); await releaseFinish.Task; }
                return rig.Runtime.Handle(method, input);
            };
            int notifications = 0;
            rig.Session.OperationFailed += (_, _) => notifications++;
            rig.Runtime.Attempt("B", Candidate("B"));
            await volumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var candidate = players[1];
            Check(rig.Native.Commits == 2 && Called(rig, "media.started", "B") &&
                candidate.Paused && !candidate.Controls.Any(call => call.Command == "pause"),
                "B has committed, but its real external output remains provisionally paused");
            var close = rig.Session.CloseSessionAsync(); var duplicate = rig.Session.CloseSessionAsync();
            Check(ReferenceEquals(close, duplicate) && !close.IsCompleted,
                "close revokes activation while still joining the admitted volume command");
            volumeFinish.TrySetResult();
            await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && candidate.Paused && !candidate.Controls.Any(call => call.Command == "pause") &&
                candidate.DisposeCalls == 1 && candidate.DisposalCompletions == 0 && !releaseEntered.Task.IsCompleted,
                "a late volume response cannot unpause B or bypass its physical child drain");
            childFinish.TrySetResult();
            await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && !duplicate.IsCompleted && candidate.DisposalCompletions == 1,
                "child completion does not stand in for the exact resource release receipt");
            releaseFinish.TrySetResult(); await Task.WhenAll(close, duplicate);
            await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 1) &&
                new[] { "A", "B" }.All(id => rig.Runtime.Calls.Count(call =>
                    call.Method == "film.session.media.release" && FilmJson.Text(call.Parameters, "attemptId") == id) == 1) &&
                !Called(rig, "media.failed", "B") && notifications == 0 && !rig.Session.HasOutput,
                "close and repeated disposal share truthful one-time child and receipt drains without false failures");
        }
        finally { volumeFinish.TrySetResult(); childFinish.TrySetResult(); releaseFinish.TrySetResult(); }
    }

    private static async Task CheckNativeControlsAsync(FilmPlaybackRig rig, NativePlaybackScope expected, bool paused)
    {
        int before = rig.Native.Controls.Count;
        Check(rig.Session.OwnsNativeMedia && !rig.Session.HasExternalPlayer && rig.Session.HasOutput &&
            rig.Session.MediaIdentity == expected.Identity && rig.Native.Scope == expected &&
            rig.Native.Position == 81 && rig.Native.Paused == paused && rig.Session.Position == 81 &&
            rig.Session.IsPlaying == !paused && rig.Page.CanSeek && rig.Page.OwnsNativeMedia &&
            rig.Native.Rate == 1.5 && rig.Native.Aspect == "4:3",
            "fallback publishes the exact still-authorized native occurrence, clock and original pause intent");
        Observe("native-fallback-ready", new
        {
            expectedScope = expected, actualScope = rig.Native.Scope, originalPaused = paused,
            rig.Native.Position, rig.Native.Paused, rig.Native.Rate, rig.Native.Aspect,
            rig.Session.HasExternalPlayer, rig.Session.OwnsNativeMedia, rig.Session.IsPlaying
        });
        await rig.Page.SeekAsync(94);
        await rig.Session.SetPausedAsync(!paused);
        await rig.Session.RateAsync(2);
        await rig.Session.AspectAsync("1:1");
        Check(rig.Native.Position == 94 && rig.Native.Paused == !paused && rig.Native.Rate == 2 &&
            rig.Native.Aspect == "1:1" && rig.Native.Controls.Skip(before).Count() == 4 &&
            rig.Native.Controls.Skip(before).All(call => call.Scope == expected),
            "controls reach only the live fallback native scope, not a disposed external child");
    }

    private static async Task InterruptedCommitAsync(string interruption, bool paused)
    {
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        await StartAsync(rig, paused);
        var old = players.Single();
        var scope = rig.Native.Scope!;
        var donorLease = rig.Native.AcceptedLease!;
        var entered = Gate(); var finish = Gate();
        old.Draining = async () => { entered.TrySetResult(); await finish.Task; };
        CancellationToken candidateLifetime = default;
        rig.Native.Preparing = token => { candidateLifetime = token; return Task.CompletedTask; };
        Task cancelCommand = Task.CompletedTask;
        try
        {
            rig.Runtime.Attempt("B", Candidate("B"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(players.Count == 2 && old.DisposeCalls == 1 && old.DisposalCompletions == 0 &&
                !old.Running && rig.Native.Scope == scope && rig.Native.Position == 81 &&
                rig.Native.Commits == 1 && !Called(rig, "media.started", "B"),
                "commit is inside the old physical child drain, before B can publish");
            int controlsBeforeRevocation = rig.Native.Controls.Count;
            if (interruption == "publication")
            {
                // Command entry cancels publication before waiting for the held control lock.
                cancelCommand = rig.Page.CommandAsync("switch");
                Check(candidateLifetime.IsCancellationRequested && !donorLease.Revoked.IsCancellationRequested &&
                    !rig.Runtime.Owner.IsCancellationRequested && !cancelCommand.IsCompleted,
                    "only B publication is cancelled, not the donor or runtime scope");
            }
            else if (interruption == "input")
            {
                rig.Native.FailPendingInput(PlaybackError.HttpTruncated);
                Check(!candidateLifetime.IsCancellationRequested && !donorLease.Revoked.IsCancellationRequested,
                    "the pending input failed without publication or donor revocation");
            }
            else
            {
                rig.Session.SourcesChanged([]);
                await Until(() => donorLease.Revoked.IsCancellationRequested);
            }
            Observe("interrupted-gate", new
            {
                interruption, originalPaused = paused, scope = rig.Native.Scope, rig.Native.Position, rig.Native.Paused,
                rig.Native.Commits, candidateCancelled = candidateLifetime.IsCancellationRequested,
                donorRevoked = donorLease.Revoked.IsCancellationRequested,
                runtimeRevoked = rig.Runtime.Owner.IsCancellationRequested,
                old.DisposeCalls, old.Disposals, old.DisposalCompletions, old.Running
            });
            finish.TrySetResult();
            await cancelCommand.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => rig.Native.DiscardedIdentity == "B" && rig.Runtime.Released.Contains("B") &&
                players[1].DisposalCompletions == 1);
            Check(rig.Native.Commits == 1 && !Called(rig, "media.started", "B") &&
                old.DisposeCalls == 1 && old.Disposals == 1 && old.DisposalCompletions == 1 &&
                players[1].DisposeCalls == 1 && players[1].Disposals == 1,
                "an interrupted commit never publishes B and retires each offered external exactly once");
            if (interruption == "donor-revoked")
            {
                await Until(() => !rig.Session.HasOutput && !rig.Native.HasOutput && rig.Runtime.Released.Contains("A"));
                Check(rig.Native.Controls.Count == controlsBeforeRevocation && !rig.Session.HasExternalPlayer &&
                    !Called(rig, "media.failed", "A") && !Called(rig, "media.started", "B"),
                    "rollback cannot seek, unpause or resurrect a revoked donor");
                await Error("STALE_SESSION", () => rig.Session.SeekAsync(94));
            }
            else
            {
                Check(Called(rig, "media.failed", "B") == (interruption == "input") &&
                    !Called(rig, "media.failed", "A"), "failure feedback belongs to B, never its accepted donor");
                await CheckNativeControlsAsync(rig, scope, paused);
            }
            Observe("interrupted-commit", new
            {
                interruption, originalPaused = paused, rig.Native.Identity, rig.Native.Commits,
                candidateCancelled = candidateLifetime.IsCancellationRequested,
                donorRevoked = donorLease.Revoked.IsCancellationRequested, scope = rig.Native.Scope,
                controls = rig.Native.Controls.Select(call => new { call.Scope, call.Command, call.Value }),
                children = players.Select(child => new
                { child.DisposeCalls, child.Disposals, child.DisposalCompletions }),
                feedback = rig.Runtime.Calls.Where(call => call.Method == "film.session.command")
                    .Select(call => call.Parameters).ToArray()
            });
            await rig.Session.CloseSessionAsync();
            await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 &&
                child.DisposalCompletions == 1), "close and repeated session disposal cannot redispose retired children");
        }
        finally { finish.TrySetResult(); }
    }

    private static async Task RateFailureAsync(bool paused)
    {
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt);
            if (players.Count == 1)
                child.SettingRate = _ => Task.FromException(new FilmPlaybackException("EXTERNAL_RATE_FAILED"));
            players.Add(child); return Task.FromResult<IFilmExternalPlayer>(child);
        });
        await StartAsync(rig, paused);
        var old = players.Single();
        var scope = rig.Native.Scope;
        int oldControls = old.Controls.Count, nativeControls = rig.Native.Controls.Count;
        await Error("EXTERNAL_RATE_FAILED", () => rig.Session.ExternalAsync("fixture-b-mpv.exe"));
        var failed = players[1];
        Check(old.Running && old.DisposeCalls == 0 && old.Disposals == 0 &&
            old.Controls.Count == oldControls && rig.Native.Controls.Count == nativeControls &&
            rig.Native.Scope == scope && rig.Session.HasExternalPlayer && rig.Session.Position == 81 &&
            rig.Session.IsPlaying == !paused && failed.DisposeCalls == 1 && failed.Disposals == 1 &&
            failed.DisposalCompletions == 1 && !Called(rig, "media.failed", "A"),
            "new mpv validation fails before freezing, detaching or retiring usable external A");
        Observe("rate-failure-donor-live", new
        {
            originalPaused = paused, nativeScope = scope, old.Position, old.Paused, old.Running,
            old.DisposeCalls, old.Disposals, old.DisposalCompletions,
            nativePosition = rig.Native.Position, nativePaused = rig.Native.Paused
        });
        await rig.Page.SeekAsync(92); await rig.Session.SetPausedAsync(!paused);
        Check(old.Position == 92 && old.Paused == !paused && rig.Native.Position == 11,
            "after new mpv rate rejection, actual controls still target the live external A");
        await rig.Session.CloseSessionAsync(); await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
        Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 1),
            "successful donor close and repeated disposal do not re-dispose the rejected process");
        Observe("rate-failure", new
        {
            originalPaused = paused, nativeScope = scope,
            oldControls = old.Controls.Select(call => new { call.Command, call.Value }),
            children = players.Select(child => new { child.DisposeCalls, child.Disposals, child.DisposalCompletions })
        });
    }

    private static async Task DeferredBlockAsync(string outcome, bool paused)
    {
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        await StartAsync(rig, paused);
        var old = players.Single();
        var scope = rig.Native.Scope;
        var donorLease = rig.Native.AcceptedLease;
        var retiring = Gate(); var retired = Gate(); var requested = Gate(); var response = Gate();
        old.Draining = async () => { retiring.TrySetResult(); await retired.Task; };
        rig.Runtime.Intercept = async (method, input, _) =>
        {
            if (method == "film.session.command" && FilmJson.Text(input, "action") == "block")
            {
                Check(FilmJson.Text(input, "attemptId") == "A" && rig.Native.Scope == scope &&
                    rig.Native.Commits == 1 && rig.Session.OwnsNativeMedia && !rig.Session.HasExternalPlayer &&
                    rig.Native.Position == 81 && rig.Native.Paused == paused &&
                    ReferenceEquals(rig.Native.AcceptedLease, donorLease) &&
                    !rig.Runtime.Released.Contains("B") && players[1].Running && players[1].Paused,
                    "a drained external child is not resurrected; exact A native output is restored before block can run");
                requested.TrySetResult(); await response.Task;
                if (outcome != "accepted") return FilmRuntimeProbe.Failure(outcome);
                rig.Runtime.Attempt("C", Candidate("C"));
            }
            return rig.Runtime.Handle(method, input);
        };
        try
        {
            rig.Runtime.Attempt("B", Candidate("B")); await retiring.Task;
            Check(players.Count == 2 && old.DisposeCalls == 1 && !old.Running && rig.Native.Commits == 1,
                "B is prepared but not committed when the old child begins physical retirement");
            var block = rig.Page.CommandAsync("block", new { attemptId = "A" });
            retired.TrySetResult(); await requested.Task;
            Check(!Called(rig, "media.started", "B") && !Called(rig, "media.failed", "B") &&
                players[1].DisposeCalls == 0 && !rig.Runtime.Released.Contains("A"),
                "waiting does not release B, fail it or retire the still-authorized native donor");
            response.TrySetResult();
            if (outcome == "accepted") await block;
            else await Error(outcome, () => block);
            string winner = outcome == "accepted" ? "C" : "B";
            await Until(() => rig.Native.SettledIdentity == winner && Called(rig, "media.started", winner));
            Check(rig.Native.Commits == 2 && rig.Session.Position == 81 && rig.Session.IsPlaying == !paused &&
                !Called(rig, "media.failed", "A") && !Called(rig, "media.failed", "B") &&
                rig.Native.Intents.Count(value => value.Identity == "B") == 1 &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "B") == 1 &&
                old.DisposeCalls == 1 && old.DisposalCompletions == 1,
                "resume uses the same receipt and latest donor intent, never reopens the disposed external A");
            Check(rig.Runtime.Released.Contains("B") == (outcome == "accepted"),
                "only the accepted block replaces B");
            if (outcome == "accepted")
            {
                await Until(() => players[1].DisposalCompletions == 1);
                Check(!rig.Session.HasExternalPlayer && rig.Session.OwnsNativeMedia,
                    "C uses the actual native owner restored before the accepted block");
            }
            else
                Check(rig.Session.HasExternalPlayer && players[1].Running &&
                    players[1].Position == 81 && players[1].Paused == paused && players.Count == 2,
                    "rejected confirmation reuses prepared B external output, not another process");
            await rig.Session.CloseSessionAsync();
            Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 &&
                child.DisposalCompletions == 1), "both external lifetimes physically drain once");
        }
        finally { retired.TrySetResult(); response.TrySetResult(); }
    }

    private static async Task FallbackAsync(bool newCandidate, bool attachment, bool paused)
    {
        var oldEntered = Gate(); var oldFinish = Gate(); var failedEntered = Gate(); var failedFinish = Gate();
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt);
            if (players.Count == 1)
            {
                child.Draining = async () => { failedEntered.TrySetResult(); await failedFinish.Task; };
                if (!attachment) child.SettingVolume = _ =>
                    Task.FromException(new FilmPlaybackException("EXTERNAL_ACTIVATION_FAILED"));
            }
            players.Add(child); return Task.FromResult<IFilmExternalPlayer>(child);
        });
        await StartAsync(rig, paused);
        var old = players.Single(); var originalScope = rig.Native.Scope!;
        var failures = new List<string>();
        rig.Session.OperationFailed += (_, code) => failures.Add(code);
        old.Draining = async () => { oldEntered.TrySetResult(); await oldFinish.Task; };
        if (attachment) rig.Native.Attaching = _ =>
            Task.FromException(new FilmPlaybackException("EXTERNAL_ATTACH_FAILED"));
        string code = attachment ? "EXTERNAL_ATTACH_FAILED" : "EXTERNAL_ACTIVATION_FAILED";
        Task operation = Task.CompletedTask;
        try
        {
            if (newCandidate) rig.Runtime.Attempt("B", Candidate("B"));
            else operation = rig.Session.ExternalAsync("fixture-b-mpv.exe");
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(old.DisposeCalls == 1 && old.DisposalCompletions == 0 && rig.Native.Scope == originalScope &&
                rig.Native.Commits == 1 && !Called(rig, "media.started", "B"),
                "attachment and activation cannot skip the old child's physical drain");
            oldFinish.TrySetResult();
            await failedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var failed = players[1]; var fallbackScope = rig.Native.Scope!;
            Check(old.DisposalCompletions == 1 && failed.DisposeCalls == 1 && failed.DisposalCompletions == 0 &&
                !old.Running && !failed.Running && rig.Native.Paused && rig.Native.Position == 81 &&
                fallbackScope.Identity == (newCandidate ? "B" : "A") &&
                rig.Native.Commits == (newCandidate ? 2 : 1),
                "the exact native fallback remains silent until the failed attached child has really drained");
            Observe("failed-child-gate", new
            {
                newCandidate, attachment, originalPaused = paused, originalScope, fallbackScope,
                oldCompleted = old.DisposalCompletions, failed.DisposeCalls, failed.Disposals,
                failed.DisposalCompletions, failed.Running, rig.Native.Position, rig.Native.Paused, rig.Native.Commits
            });
            int nativeBeforeFailedDrain = rig.Native.Controls.Count;
            failedFinish.TrySetResult();
            if (!newCandidate) await Error(code, () => operation);
            else await Until(() => rig.Page.ConnectionFailed && failures.Contains("播放状态提交失败"));
            await Until(() => failed.DisposalCompletions == 1 && rig.Session.OwnsNativeMedia && !rig.Session.HasExternalPlayer);
            if (newCandidate)
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(rig.Page.ConnectionFailed && rig.Page.CurrentAttemptId == "B" &&
                    FilmJson.Member(rig.Runtime.Value, "attempt").ValueKind == JsonValueKind.Null &&
                    failures.Count(message => message == "播放状态提交失败") == 1,
                    "settled candidate keeps its local failure visible without replaying an empty attempt or losing accepted B");
            }
            Check(rig.Native.Controls.Skip(nativeBeforeFailedDrain).Any(call =>
                call.Scope == fallbackScope && call.Command == "pause" && Equals(call.Value, paused)) &&
                !Called(rig, "media.failed", newCandidate ? "B" : "A") &&
                (!newCandidate || Called(rig, "media.started", "B")),
                "only after physical drain does native restore original pause, without fabricating decode failure");
            await CheckNativeControlsAsync(rig, fallbackScope, paused);
            await rig.Session.CloseSessionAsync(); await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 && child.DisposalCompletions == 1),
                "attachment ownership transfers once, including failure and repeated session disposal");
            Observe("fallback", new
            {
                newCandidate, attachment, originalPaused = paused, originalScope, fallbackScope,
                nativeControls = rig.Native.Controls.Select(call => new { call.Scope, call.Command, call.Value }), failures,
                children = players.Select(child => new { child.DisposeCalls, child.Disposals, child.DisposalCompletions })
            });
        }
        finally { oldFinish.TrySetResult(); failedFinish.TrySetResult(); }
    }

    private static async Task SuccessfulAsync(bool paused)
    {
        var players = new List<FilmExternalProbe>();
        await using var rig = new FilmPlaybackRig((_, receipt, _, _) =>
        {
            var child = new FilmExternalProbe(receipt); players.Add(child);
            return Task.FromResult<IFilmExternalPlayer>(child);
        });
        await StartAsync(rig, paused);
        var old = players.Single(); var scope = rig.Native.Scope!;
        var entered = Gate(); var finish = Gate();
        old.Draining = async () => { entered.TrySetResult(); await finish.Task; };
        try
        {
            rig.Runtime.Attempt("B", Candidate("B"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(rig.Native.Scope == scope && rig.Native.Commits == 1 && !Called(rig, "media.started", "B"),
                "even successful publication waits for the real old child");
            finish.TrySetResult();
            await Until(() => players.Count == 2 && rig.Native.SettledIdentity == "B" &&
                Called(rig, "media.started", "B") && rig.Session.HasExternalPlayer && players[1].Volume == rig.Native.Volume);
            var next = players[1];
            Check(next.Position == 81 && next.Paused == paused && next.Rate == 1.5 && next.Aspect == "4:3" &&
                rig.Native.Paused && rig.Native.Position == 81 && rig.Session.IsPlaying == !paused &&
                old.DisposeCalls == 1 && old.Disposals == 1 && old.DisposalCompletions == 1 &&
                next.DisposeCalls == 0 && next.Running && rig.Native.Scope?.Identity == "B",
                "one precise new occurrence inherits the actual clock and controls only after old physical retirement");
            Observe("successful-transfer-live", new
            {
                originalPaused = paused, scope = rig.Native.Scope,
                next.Position, next.Paused, next.Rate, next.Aspect, next.Running,
                nativePosition = rig.Native.Position, nativePaused = rig.Native.Paused,
                old.DisposeCalls, old.Disposals, old.DisposalCompletions
            });
            await rig.Page.SeekAsync(95); await rig.Session.SetPausedAsync(!paused);
            Check(next.Position == 95 && next.Paused == !paused && rig.Native.Position == 81,
                "successful controls target B external output rather than either native donor or disposed A");
            await rig.Session.CloseSessionAsync(); await rig.Session.CloseSessionAsync();
            await rig.Session.DisposeAsync(); await rig.Session.DisposeAsync();
            Check(players.All(child => child.DisposeCalls == 1 && child.Disposals == 1 &&
                child.DisposalCompletions == 1) && !rig.Native.HasOutput && !rig.Session.HasOutput,
                "repeat close and disposal join the same real child retirement");
            Observe("success", new
            {
                originalPaused = paused, rig.Native.Commits,
                children = players.Select(child => new { child.DisposeCalls, child.Disposals, child.DisposalCompletions }),
                released = rig.Runtime.Released
            });
        }
        finally { finish.TrySetResult(); }
    }
}
