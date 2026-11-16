using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackKernelTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static TestCase Test(string name, Func<Task> run) =>
        new("film-playback-kernel: " + name, () => MusicQualityConsumerTests.Sta(run));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("prepare stays silent and commit preserves paused intent", SilentPreparePausedCommit);
        yield return Test("native playing without a rendered picture cannot publish output", MissingFirstPicture);
        yield return Test("only commit acquires output and holds its permit through publication", OutputAdmission);
        yield return Test("output admission can asynchronously stop prepared film without gate inversion", AdmissionStopsFilm);
        yield return Test("output admission rechecks canCommit before starting native output", AdmissionRejected);
        yield return Test("cancelled late output admission releases its permit and leaves the gate usable", () => AdmissionCancelled(false));
        yield return Test("output admission cancellation is terminal rather than deferred", () => AdmissionCancelled(true));
        yield return Test("cancelled output admission cannot hide a concurrent native lease release failure",
            () => AdmissionRetirement(true));
        yield return Test("cancelled output admission waits for the exact native retirement receipt",
            () => AdmissionRetirement(false));
        yield return Test("output admission cannot publish a superseded candidate", AdmissionSuperseded);
        yield return Test("an invalid donor terminates even when business admission is deferred", InvalidDonor);
        yield return Test("stopping a prepared direct film drains its business attempt without retrying commit",
            () => PendingBusinessStop(false));
        yield return Test("cancelling direct film output admission drains its business attempt without reporting failure",
            () => PendingBusinessStop(true));
        yield return Test("a late duplicate admission cannot retire the published owner", () => DuplicateAdmission(false));
        yield return Test("a failed late duplicate admission cannot retire the published owner", () => DuplicateAdmission(true));
        yield return Test("cancelled prepare awaits native release before disposing its lease", CancelledPrepare);
        yield return Test("superseded late prepare cannot replace or commit the latest candidate", SupersededPrepare);
        yield return Test("same episode aligns before start and a queued duplicate cannot retire the published owner", AlignedCommit);
        foreach (bool sameEpisode in new[] { true, false })
            foreach (bool pause in new[] { true, false })
                yield return Test($"late donor pause={pause} same episode={sameEpisode} starts with the correct intent",
                    () => LatestPause(sameEpisode, pause));
        yield return Test("cancelled late alignment cannot start and restores the donor pause intent", CancelledAlignment);
        yield return Test("active failure clears output and awaits actual native retirement", ActiveFailure);
        yield return Test("natural end reports the exact scope and final clock after native retirement", NaturalEnd);
        yield return Test("scoped stop joins retiring natural end and suppresses stale terminal callbacks", () => StoppedTerminal(false));
        yield return Test("scoped stop joins retiring failure and suppresses stale terminal callbacks", () => StoppedTerminal(true));
        yield return Test("stop awaits both active and prepared native owners exactly once", () => StopOwners(true));
        yield return Test("unscoped stop awaits both active and prepared native owners exactly once", () => StopOwners(false));
        yield return Test("tracks use stream identities and subtitle disable clears the selected stream", TrackIdentity);
        yield return Test("invalid tracks and retired scopes never reach native selection", InvalidTrack);
        yield return Test("cancelled late track selection awaits retirement without publishing its result", CancelledTrack);
        yield return Test("a track read superseded by stop cannot publish its old snapshot", SupersededTracks);
    }

    private static RuntimeMediaPlaybackIntent Intent(string identity, double position = 0, bool paused = false,
        NativePlaybackScope? preserve = null) => new("film", identity, position, paused, preserve);

    private static async Task OutputAdmission()
    {
        var owner = new Owner("admission");
        var permit = new OutputPermit();
        int requests = 0;
        await using var rig = new Rig(async token =>
        {
            requests++;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return permit;
        }, owner);
        var intent = Intent("admission");
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        Check(requests == 0, "silent preparation never acquires app output");
        Check(!await rig.Playback.TryCommitAsync(intent, () => false, () => { }) && requests == 0,
            "a rejected commit does not request output");
        Check(await rig.Playback.TryCommitAsync(intent, () => true, () =>
        {
            Check(requests == 1 && permit.Disposals == 0 && rig.Playback.HasOutput &&
                rig.Kernel.Values(owner, "start").Length == 1,
                "the permit covers actual native start and commit publication");
        }).WaitAsync(Limit), "admitted commit succeeds");
        Check(permit.Disposals == 1, "the permit is released exactly once after publication");
    }

    private static async Task AdmissionStopsFilm()
    {
        var releasing = new NativeGate();
        var owner = new Owner("admission-stop") { ReleaseGate = releasing };
        var permit = new OutputPermit();
        FilmPlayback? playback = null;
        await using var rig = new Rig(async _ =>
        {
            await Task.Yield();
            await playback!.StopAsync();
            return permit;
        }, owner);
        playback = rig.Playback;
        var intent = Intent("admission-stop");
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        int publications = 0;
        var commit = playback.TryCommitAsync(intent, () => true, () => publications++);
        await releasing.Arrival();
        Check(!commit.IsCompleted && owner.Lease.DisposeCalls == 0,
            "reentrant stop reaches native retirement and waits for its actual completion");
        releasing.Open();
        await Superseded(commit);
        Check(publications == 0 && !playback.HasOutput &&
            permit.Disposals == 1 && rig.Kernel.Values(owner, "start").Length == 0,
            "stopping a prepared-only owner invalidates admission without deadlock or publication");
        Released(rig, owner);
    }

    private static async Task AdmissionRejected()
    {
        var owner = new Owner("admission-reject");
        var permit = new OutputPermit();
        bool allowed = true;
        await using var rig = new Rig(async _ =>
        {
            await Task.Yield();
            allowed = false;
            return permit;
        }, owner);
        var intent = Intent("admission-reject");
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        Check(!await rig.Playback.TryCommitAsync(intent, () => allowed,
            () => throw new InvalidOperationException("Stale admission published")).WaitAsync(Limit) &&
            permit.Disposals == 1 && rig.Kernel.Values(owner, "start").Length == 0,
            "canCommit is rechecked after external admission");
        await rig.Playback.DiscardAsync(intent).WaitAsync(Limit);
        Released(rig, owner);
    }

    private static async Task AdmissionCancelled(bool throwsCancellation)
    {
        var owner = new Owner("admission-cancel");
        var permit = new OutputPermit();
        using var cancellation = new CancellationTokenSource();
        await using var rig = new Rig(async token =>
        {
            await Task.Yield();
            cancellation.Cancel();
            Check(token.IsCancellationRequested, "admission receives the candidate's linked cancellation");
            if (throwsCancellation) token.ThrowIfCancellationRequested();
            return permit; // Deliberately grant late despite cancellation.
        }, owner);
        var intent = Intent("admission-cancel");
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        await Superseded(rig.Playback.TryCommitAsync(intent, () => true,
            () => throw new InvalidOperationException("Cancelled admission published"), cancellation.Token));
        Check(permit.Disposals == (throwsCancellation ? 0 : 1) && !rig.Playback.HasOutput &&
            rig.Kernel.Values(owner, "start").Length == 0, "a late grant cannot publish cancelled output");
        Released(rig, owner);
        await rig.Playback.StopAsync().WaitAsync(Limit);
    }

    private static async Task InvalidDonor()
    {
        var donor = new Owner("invalid-donor");
        var candidate = new Owner("invalid-donor-candidate");
        await using var rig = new Rig(donor, candidate);
        var initial = Intent("invalid-donor");
        await rig.Prepare(donor, initial);
        await rig.Commit(initial);
        var scope = rig.Playback.Scope!;
        var intent = Intent("invalid-donor-candidate", preserve: scope with { Generation = scope.Generation + 1 });
        await rig.Prepare(candidate, intent);
        int admissions = 0, publications = 0;
        await Superseded(rig.Playback.TryCommitAsync(intent, () => { admissions++; return false; },
            () => publications++));
        Check(admissions == 0 && publications == 0 && rig.Playback.Scope == scope &&
            rig.Playback.HasOutput && donor.Lease.DisposeCalls == 0 &&
            rig.Kernel.Values(candidate, "start").Length == 0,
            "an invalid donor is terminal before consulting defer and cannot disturb healthy output");
        Released(rig, candidate);
    }

    private static async Task AdmissionRetirement(bool failRelease)
    {
        var releasing = new NativeGate();
        var owner = new Owner("admission-retirement") { ReleaseGate = releasing };
        owner.Lease.FailRelease = failRelease;
        var entered = Signal();
        var waiting = Signal();
        var rig = new Rig(async token =>
        {
            entered.TrySetResult();
            await waiting.Task.WaitAsync(token);
            throw new InvalidOperationException("Cancelled admission cannot grant output");
        }, owner);
        try
        {
            var intent = Intent("admission-retirement");
            await rig.Prepare(owner, intent);
            int publications = 0;
            var commit = rig.Playback.TryCommitAsync(intent, () => true, () => publications++);
            await entered.Task.WaitAsync(Limit);
            var stop = rig.Playback.StopAsync();
            await releasing.Arrival();
            Check(!stop.IsCompleted && !commit.IsCompleted && owner.Lease.DisposeCalls == 0,
                "neither cancellation nor clearing pending completes admission before exact native retirement");
            releasing.Open();
            if (failRelease) await Failure(stop, PlaybackError.ReleaseFailed);
            else await stop.WaitAsync(Limit);
            await Failure(commit, failRelease ? PlaybackError.ReleaseFailed : PlaybackError.Superseded);
            Check(publications == 0 && !rig.Playback.HasOutput &&
                rig.Kernel.Values(owner, "start").Length == 0,
                "admission settles without publication and preserves the actual retirement outcome");
            Released(rig, owner);
        }
        finally
        {
            releasing.Open();
            try
            {
                if (failRelease) await Failure(rig.DisposeAsync().AsTask(), PlaybackError.ReleaseFailed);
                else await rig.DisposeAsync();
            }
            finally
            {
                // The injected failure is after the probe's native release; no native worker can still use this HWND.
                if (failRelease && owner.NativeReleased.Task.IsCompletedSuccessfully)
                    ((FilmVideoSurface)rig.Playback.VideoView).Dispose();
            }
        }
    }

    private static async Task PendingBusinessStop(bool duringAdmission)
    {
        var preparing = new NativeGate();
        var releasing = new NativeGate();
        var stopped = new Owner("pending-business-stop") { PrepareGate = preparing, ReleaseGate = releasing };
        var latest = new Owner("pending-business-latest");
        var admission = Signal(); var admissionWait = Signal();
        int requests = 0;
        await using var rig = new Rig(duringAdmission ? async token =>
        {
            if (++requests == 1)
            {
                admission.TrySetResult();
                await admissionWait.Task.WaitAsync(token);
            }
            return new OutputPermit();
        } : null, stopped, latest);
        await using var runtime = new FilmRuntimeProbe { Controlled = false };
        await using var library = new FilmLibrary(new FilmMemoryStore());
        await using var session = new FilmPlaybackSession(runtime.CaptureConnectionAsync, new FilmNativeMedia(rig.Playback));
        var card = Card();
        var page = new FilmPlaybackPage(card, card.PlaybackContext!, session, library);
        await session.OpenAsync(page, new { platform = "windows", target = card.PlaybackContext!.Target.Wire() }, default);
        var browsing = Signal(); var browseReply = Signal();
        var resourceReleasing = Signal(); var resourceReleased = Signal();
        var failures = new List<string>();
        session.OperationFailed += (_, code) => failures.Add(code);
        runtime.Intercept = async (method, input, _) =>
        {
            string id = FilmJson.Text(input, "attemptId");
            if (!duringAdmission && method == "film.session.command" && FilmJson.Text(input, "action") == "browse")
            { browsing.TrySetResult(); await browseReply.Task.WaitAsync(Limit); }
            if (method == "film.session.media.release" && id == "stopped")
            { resourceReleasing.TrySetResult(); await resourceReleased.Task.WaitAsync(Limit); }
            var response = runtime.Handle(method, input);
            if (method == "film.session.attempt")
            {
                var receipt = JsonNode.Parse(response.GetProperty("value").GetRawText())!.AsObject();
                receipt["url"] = (id == "stopped" ? stopped : latest).Address.AbsoluteUri;
                receipt["format"] = "mp4";
                response = FilmRuntimeProbe.Ok(FilmJson.Encode(receipt));
            }
            return response;
        };
        try
        {
            var browse = page.CommandAsync("browse", new { sourceId = (string?)null });
            if (!duringAdmission) await browsing.Task.WaitAsync(Limit);
            else await browse.WaitAsync(Limit);
            runtime.Attempt("stopped", Candidate("stopped"));
            await preparing.Arrival();
            preparing.Open();
            await Until(() => rig.Kernel.Values(stopped, "read").Length > 0);
            if (duringAdmission) await admission.Task.WaitAsync(Limit);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!session.HasOutput && runtime.Calls.Count(call => call.Method == "film.session.attempt") == 1,
                "the first direct candidate waits for admission or business control with no accepted donor");
            var stop = rig.Playback.StopAsync(); // The same native stop used by music output admission.
            await releasing.Arrival();
            Check(!stop.IsCompleted && !runtime.Released.Contains("stopped"),
                "native retirement is not complete merely because pending publication was cleared");
            releasing.Open();
            await stop.WaitAsync(Limit);
            browseReply.TrySetResult();
            await browse.WaitAsync(Limit);
            await resourceReleasing.Task.WaitAsync(Limit);
            Check(stopped.NativeReleased.Task.IsCompletedSuccessfully &&
                rig.Kernel.Values(stopped, "release").Length == 1 &&
                rig.Kernel.Values(stopped, "start").Length == 0 && !session.HasOutput &&
                !runtime.Calls.Any(call => call.Method == "film.session.command" &&
                    FilmJson.Text(call.Parameters, "action") is "media.started" or "media.failed"),
                "terminal native invalidation reaches exact business release without publication, failure or commit retry");
            resourceReleased.TrySetResult();
            await Until(() => runtime.Released.Contains("stopped"));
            runtime.Attempt("latest", Candidate("latest"));
            await Until(() => session.HasOutput && rig.Playback.Identity == "latest");
            await session.CloseSessionAsync().WaitAsync(Limit);
            Check(failures.Count == 0 && rig.Kernel.Values(latest, "start").Length == 1 &&
                rig.Kernel.Values(latest, "release").Length == 1 &&
                runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "stopped" &&
                    FilmJson.Text(call.Parameters, "resourceId") == FilmJson.Text(runtime.Receipts["stopped"], "id")) == 1,
                "business control remains usable and each direct occurrence retains its precise single release");
        }
        finally
        {
            preparing.Open(); releasing.Open();
            browseReply.TrySetResult(); resourceReleased.TrySetResult();
        }
    }

    private static async Task AdmissionSuperseded()
    {
        var old = new Owner("admission-old");
        var latest = new Owner("admission-latest");
        var latestIntent = Intent("admission-latest");
        var permits = new List<OutputPermit>();
        Rig? fixture = null;
        await using var rig = new Rig(async _ =>
        {
            var permit = new OutputPermit();
            permits.Add(permit);
            if (permits.Count == 1)
            {
                await Task.Yield();
                await fixture!.Prepare(latest, latestIntent).WaitAsync(Limit);
            }
            return permit;
        }, old, latest);
        fixture = rig;
        var intent = Intent("admission-old");
        await rig.Prepare(old, intent).WaitAsync(Limit);
        await Superseded(rig.Playback.TryCommitAsync(intent, () => true,
            () => throw new InvalidOperationException("Superseded admission published")));
        Check(permits[0].Disposals == 1 && rig.Kernel.Values(old, "start").Length == 0,
            "a newer prepare can reenter admission and prevents stale publication");
        Released(rig, old);
        await rig.Commit(latestIntent);
        Check(rig.Playback.Identity == latestIntent.Identity && permits.Count == 2 &&
            permits[1].Disposals == 1, "the newer candidate and command gate remain usable");
    }

    private static async Task DuplicateAdmission(bool fail)
    {
        var owner = new Owner("admission-duplicate");
        var entered = Signal();
        var resume = Signal();
        var firstPermit = new OutputPermit();
        var secondPermit = new OutputPermit();
        int requests = 0, publications = 0;
        await using var rig = new Rig(async _ =>
        {
            if (++requests == 1)
            {
                entered.TrySetResult();
                await resume.Task.WaitAsync(Limit);
                if (fail) throw new InvalidOperationException("Late obsolete admission failure");
                return firstPermit;
            }
            return secondPermit;
        }, owner);
        try
        {
            var intent = Intent("admission-duplicate");
            await rig.Prepare(owner, intent).WaitAsync(Limit);
            var first = rig.Playback.TryCommitAsync(intent, () => true, () => publications++);
            await entered.Task.WaitAsync(Limit);
            Check(await rig.Playback.TryCommitAsync(intent, () => true, () => publications++).WaitAsync(Limit),
                "a duplicate can acquire admission while the first callback awaits externally");
            resume.TrySetResult();
            await Failure(first, fail ? PlaybackError.CommandFailed : PlaybackError.Superseded);
            Check(publications == 1 && rig.Playback.HasOutput &&
                owner.Lease.DisposeCalls == 0 && rig.Kernel.Values(owner, "cancel").Length == 0 &&
                secondPermit.Disposals == 1 && firstPermit.Disposals == (fail ? 0 : 1),
                "late admission success or failure cannot retire another commit's published session");
        }
        finally { resume.TrySetResult(); }
    }

    private static async Task SilentPreparePausedCommit()
    {
        var owner = new Owner("paused");
        await using var rig = new Rig(owner);
        var playback = rig.Playback;
        var intent = Intent("paused", 12.5, paused: true);
        playback.Volume = 37;
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        Check(!playback.HasOutput && playback.Scope is null && !playback.PauseRequested,
            "silent preparation publishes neither output nor the candidate's pause state");
        Check(rig.Kernel.Calls.Select(call => call.Name).SequenceEqual(["create", "prepare", "read"]) &&
            rig.Kernel.Values(owner, "prepare").SequenceEqual([12.5]) && owner.Lease.DisposeCalls == 0,
            "prepare does not attach, start, pause or release the provisional owner");

        int committed = 0;
        Check(await playback.TryCommitAsync(intent, () => true, () => committed++).WaitAsync(Limit),
            "the prepared candidate commits");
        Check(committed == 1 && playback.HasOutput && playback.Identity == intent.Identity &&
            playback.Scope is { OwnerId: "film" } && playback.PauseRequested &&
            playback.PositionSeconds == 12.5 && playback.DurationSeconds == 600,
            "commit publishes the exact paused intent and prepared position");
        Check(rig.Kernel.Values(owner, "start").SequenceEqual([1d]) &&
            rig.Kernel.Values(owner, "volume").SequenceEqual([.37]) &&
            rig.Kernel.Values(owner, "pause").Length == 0,
            "paused playback starts paused rather than briefly playing and pausing afterwards");
        var calls = rig.Kernel.Calls.Select(call => call.Name).ToArray();
        Check(Array.IndexOf(calls, "attach") < Array.IndexOf(calls, "start"),
            "the picture target is attached before native start");
        await playback.RefreshAsync().WaitAsync(Limit);
        Check(owner.Snapshot.State == 3 && playback.PauseRequested, "polling retains paused output");
    }

    private static async Task MissingFirstPicture()
    {
        var owner = new Owner("no-picture") { SkipFirstPicture = true };
        await using var rig = new Rig(owner);
        var intent = Intent("no-picture");
        await rig.Prepare(owner, intent);
        int committed = 0;
        try
        {
            await rig.Playback.TryCommitAsync(intent, () => true, () => committed++).WaitAsync(Limit);
            throw new InvalidOperationException("Expected missing native picture rejection");
        }
        catch (PlaybackException error) when (error.Code == PlaybackError.OpenFailed) { }
        Check(committed == 0 && !rig.Playback.HasOutput && rig.Playback.Scope is null &&
            rig.Playback.Output is null, "a playing state alone cannot admit business output");
        Released(rig, owner);
    }

    private static async Task CancelledPrepare()
    {
        var preparing = new NativeGate();
        var releasing = new NativeGate();
        var owner = new Owner("cancelled") { PrepareGate = preparing, ReleaseGate = releasing };
        await using var rig = new Rig(owner);
        using var cancellation = new CancellationTokenSource();
        var intent = Intent("cancelled");
        var prepare = rig.Prepare(owner, intent, cancellation.Token);
        await preparing.Arrival();
        cancellation.Cancel();
        Check(rig.Kernel.Values(owner, "cancel").Length == 1 && !prepare.IsCompleted &&
            owner.Lease.DisposeCalls == 0, "cancellation interrupts native work but does not pretend it has joined");

        preparing.Open(); // Deliberately return native success after cancellation.
        await releasing.Arrival();
        Check(!prepare.IsCompleted && owner.Lease.DisposeCalls == 0 && !rig.Playback.HasOutput,
            "prepare cancellation remains incomplete while native release is blocked");
        int commits = 0;
        await Superseded(rig.Playback.TryCommitAsync(intent, () => true, () => commits++));
        releasing.Open();
        await Cancelled(prepare);
        Released(rig, owner);
        Check(commits == 0 && rig.Kernel.Values(owner, "start").Length == 0,
            "a cancelled candidate never starts or invokes the commit callback");
        await rig.Playback.DiscardAsync(intent).WaitAsync(Limit);
        Released(rig, owner);
    }

    private static async Task SupersededPrepare()
    {
        var preparing = new NativeGate();
        var old = new Owner("superseded") { PrepareGate = preparing };
        var latest = new Owner("latest");
        await using var rig = new Rig(old, latest);
        var oldIntent = Intent("superseded");
        var latestIntent = Intent("latest");
        var oldPrepare = rig.Prepare(old, oldIntent);
        await preparing.Arrival();
        var latestPrepare = rig.Prepare(latest, latestIntent);
        Check(!latestPrepare.IsCompleted && rig.Kernel.Values(old, "cancel").Length == 1,
            "the latest admission cancels the old candidate while waiting for native work");
        preparing.Open();
        await Cancelled(oldPrepare);
        await latestPrepare.WaitAsync(Limit);
        Released(rig, old);
        int oldCommits = 0;
        await Superseded(rig.Playback.TryCommitAsync(oldIntent, () => true, () => oldCommits++));
        Check(!rig.Playback.HasOutput && rig.Playback.Scope is null, "neither preparation publishes output");
        await rig.Commit(latestIntent);
        Check(oldCommits == 0 && rig.Playback.Identity == latestIntent.Identity &&
            rig.Kernel.Values(old, "attach").Length == 0 && rig.Kernel.Values(old, "start").Length == 0 &&
            latest.Lease.DisposeCalls == 0, "only the latest candidate is published and retains its lease");
    }

    private static async Task AlignedCommit()
    {
        var seeking = new NativeGate();
        var releasing = new NativeGate();
        var donor = new Owner("align-donor") { ReleaseGate = releasing };
        var candidate = new Owner("align-candidate") { SeekGate = seeking };
        await using var rig = new Rig(donor, candidate);
        var original = Intent("align-donor", 4, paused: true);
        await rig.Prepare(donor, original).WaitAsync(Limit);
        await rig.Commit(original);
        var intent = Intent("align-candidate", 4, paused: true, preserve: rig.Playback.Scope);
        await rig.Prepare(candidate, intent).WaitAsync(Limit);
        donor.Update(position: 137.75);

        int commits = 0;
        var commit = rig.Playback.TryCommitAsync(intent, () => true, () =>
        {
            commits++;
            Check(rig.Playback.Identity == intent.Identity && rig.Playback.HasOutput &&
                rig.Kernel.Values(donor, "release").Length == 0,
                "the replacement is published before the donor begins retirement");
        });
        await seeking.Arrival();
        Check(rig.Kernel.Values(donor, "pause").SequenceEqual([1d]) &&
            rig.Kernel.Values(candidate, "seek").SequenceEqual([137.75]) &&
            rig.Kernel.Values(candidate, "start").Length == 0,
            "alignment reads the silenced donor's latest native position before starting the replacement");
        var duplicate = rig.Playback.TryCommitAsync(intent, () => true, () => commits++);
        Check(!duplicate.IsCompleted, "a duplicate commit is queued behind the in-flight alignment");
        seeking.Open();
        await releasing.Arrival();
        Check(!commit.IsCompleted && !duplicate.IsCompleted && commits == 1 &&
            rig.Playback.Identity == intent.Identity && rig.Playback.PositionSeconds == 137.75 &&
            rig.Playback.PauseRequested && donor.Lease.DisposeCalls == 0,
            "publication preserves alignment and pause intent while donor release is still joining");
        releasing.Open();
        Check(await commit.WaitAsync(Limit), "the first queued commit publishes the candidate");
        await Superseded(duplicate);
        Released(rig, donor);
        Check(commits == 1 && rig.Kernel.Values(candidate, "start").SequenceEqual([1d]) &&
            rig.Kernel.Values(candidate, "cancel").Length == 0 && candidate.Lease.DisposeCalls == 0 &&
            rig.Playback.HasOutput, "the duplicate cannot cancel or retire the now-active owner");
    }

    private static async Task CancelledAlignment()
    {
        var seeking = new NativeGate();
        var donor = new Owner("cancel-align-donor");
        var candidate = new Owner("cancel-align-candidate") { SeekGate = seeking };
        await using var rig = new Rig(donor, candidate);
        var original = Intent("cancel-align-donor", 23);
        await rig.Prepare(donor, original).WaitAsync(Limit);
        await rig.Commit(original);
        var originalScope = rig.Playback.Scope!;
        var intent = Intent("cancel-align-candidate", preserve: originalScope);
        await rig.Prepare(candidate, intent).WaitAsync(Limit);
        using var cancellation = new CancellationTokenSource();
        int commits = 0;
        var commit = rig.Playback.TryCommitAsync(intent, () => true, () => commits++, cancellation.Token);
        await seeking.Arrival();
        Check(donor.Snapshot.State == 3, "the donor is temporarily silenced during alignment");
        cancellation.Cancel();
        seeking.Open(); // Seek also returns success after cancellation.
        await Superseded(commit);
        Released(rig, candidate);
        Check(commits == 0 && rig.Kernel.Values(candidate, "attach").Length == 0 &&
            rig.Kernel.Values(candidate, "start").Length == 0, "a late seek cannot attach or start cancelled output");
        Check(rig.Playback.Scope == originalScope && rig.Playback.HasOutput && !rig.Playback.PauseRequested &&
            donor.Snapshot.State == 2 && rig.Kernel.Values(donor, "pause").SequenceEqual([1d, 0d]) &&
            donor.Lease.DisposeCalls == 0, "failed handoff restores the still-owned donor's playing intent");
    }

    private static async Task LatestPause(bool sameEpisode, bool pause)
    {
        var preparing = new NativeGate();
        var donor = new Owner("pause-donor");
        var candidate = new Owner("pause-candidate") { PrepareGate = preparing };
        await using var rig = new Rig(donor, candidate);
        var original = Intent("pause-donor", 23, paused: !pause);
        await rig.Prepare(donor, original);
        await rig.Commit(original);
        var scope = rig.Playback.Scope!;
        var intent = Intent("pause-candidate", 4, paused: !pause, preserve: sameEpisode ? scope : null);
        var prepare = rig.Prepare(candidate, intent);
        await preparing.Arrival();
        var control = rig.Playback.SetPausedAsync(scope, pause);
        Check(!control.IsCompleted, "the user's newer pause command waits for actual native preparation");
        preparing.Open();
        await prepare.WaitAsync(Limit);
        await control.WaitAsync(Limit);
        donor.Update(position: 137.75);
        await rig.Commit(intent);
        bool expected = sameEpisode ? pause : intent.Paused;
        Check(rig.Playback.PauseRequested == expected && rig.Playback.Identity == intent.Identity &&
            candidate.Snapshot.State == (expected ? 3 : 2) &&
            rig.Kernel.Values(candidate, "start").SequenceEqual([expected ? 1d : 0d]) &&
            rig.Playback.PositionSeconds == (sameEpisode ? 137.75 : 4) &&
            rig.Kernel.Values(candidate, "seek").Length == (sameEpisode ? 1 : 0),
            "same-content start uses the latest actual donor pause in both directions; new content retains its own intent");
        Released(rig, donor);
    }

    private static async Task ActiveFailure()
    {
        var releasing = new NativeGate();
        var owner = new Owner("failed") { ReleaseGate = releasing };
        await using var rig = new Rig(owner);
        var intent = Intent("failed");
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        await rig.Commit(intent);
        var scope = rig.Playback.Scope;
        int failures = 0;
        rig.Playback.Failed += (_, _) =>
        {
            failures++;
            Check(owner.NativeReleased.Task.IsCompletedSuccessfully && rig.Playback.Scope == scope &&
                rig.Playback.Identity == intent.Identity && !rig.Playback.HasOutput,
                "failure preserves its exact occurrence until observers capture it, only after native release");
        };
        owner.Update(state: 6, error: 13);
        var refresh = rig.Playback.RefreshAsync();
        await releasing.Arrival();
        Check(failures == 0 && !rig.Playback.HasOutput && rig.Playback.Scope == scope && !refresh.IsCompleted &&
            owner.Lease.DisposeCalls == 0, "active failure unpublishes output but cannot acknowledge termination before native retirement");
        releasing.Open();
        await refresh.WaitAsync(Limit);
        Released(rig, owner);
        await rig.Playback.RefreshAsync().WaitAsync(Limit);
        Check(failures == 1 && rig.Playback.LastErrorCode == "FILM_DEVICE_LOST" && rig.Playback.Scope is null,
            "later polls do not repeat a retired owner's failure");
        Released(rig, owner);
    }

    private static async Task NaturalEnd()
    {
        var releasing = new NativeGate();
        var owner = new Owner("ended") { ReleaseGate = releasing };
        await using var rig = new Rig(owner);
        var intent = Intent("ended");
        await rig.Prepare(owner, intent);
        await rig.Commit(intent);
        var scope = rig.Playback.Scope;
        int ended = 0;
        rig.Playback.Ended += (_, _) =>
        {
            ended++;
            Check(owner.NativeReleased.Task.IsCompletedSuccessfully && rig.Playback.Scope == scope &&
                rig.Playback.Identity == intent.Identity && rig.Playback.PositionSeconds == 600 &&
                rig.Playback.DurationSeconds == 600 && !rig.Playback.HasOutput,
                "natural termination exposes its final scoped clock after actual release, not an empty successor state");
        };
        owner.Update(state: 5, position: 600);
        var refresh = rig.Playback.RefreshAsync();
        await releasing.Arrival();
        Check(ended == 0 && !refresh.IsCompleted && !rig.Playback.HasOutput,
            "end waits for the actual native retirement");
        releasing.Open();
        await refresh.WaitAsync(Limit);
        await rig.Playback.RefreshAsync();
        Check(ended == 1 && rig.Playback.Scope is null && rig.Playback.Output is null,
            "the exact terminal occurrence is then cleared and cannot end twice");
        Released(rig, owner);
    }

    private static async Task StoppedTerminal(bool failed)
    {
        var releasing = new NativeGate();
        string identity = failed ? "stop-retiring-failure" : "stop-retiring-end";
        var owner = new Owner(identity) { ReleaseGate = releasing };
        await using var rig = new Rig(owner);
        var intent = Intent(identity, position: 17);
        await rig.Prepare(owner, intent).WaitAsync(Limit);
        await rig.Commit(intent);
        var scope = rig.Playback.Scope!;
        int ended = 0, failures = 0;
        rig.Playback.Ended += (_, _) => ended++;
        rig.Playback.Failed += (_, _) => failures++;
        owner.Update(state: failed ? 6 : 5, error: failed ? 13 : 0, position: 600);
        var refresh = rig.Playback.RefreshAsync();
        await releasing.Arrival();
        Check(!refresh.IsCompleted && !rig.Playback.HasOutput && rig.Playback.Scope == scope &&
            rig.Playback.Identity == intent.Identity && ended == 0 && failures == 0 &&
            !owner.NativeReleased.Task.IsCompleted && owner.Lease.DisposeCalls == 0,
            "terminal retirement retains its occurrence without firing observers or releasing the lease early");

        var stop = rig.Playback.StopAsync(scope);
        Check(!stop.IsCompleted && !refresh.IsCompleted && !rig.Playback.HasOutput &&
            rig.Playback.Scope is null && rig.Playback.Identity is null && rig.Playback.Output is null &&
            !owner.NativeReleased.Task.IsCompleted && owner.Lease.DisposeCalls == 0,
            "scoped stop clears the retiring identity immediately but still waits for actual native release");
        releasing.Open();
        await Task.WhenAll(refresh, stop).WaitAsync(Limit);
        Released(rig, owner);
        Check(ended == 0 && failures == 0 && !rig.Playback.HasOutput && rig.Playback.Scope is null &&
            rig.Playback.Identity is null && rig.Playback.Output is null && rig.Playback.LastErrorCode is null,
            "stop suppresses both stale terminal events after joining the exact native retirement");
        await rig.Playback.RefreshAsync().WaitAsync(Limit);
        await rig.Playback.StopAsync(scope).WaitAsync(Limit);
        Check(ended == 0 && failures == 0, "later polls and stale scoped stops cannot revive terminal callbacks");
        Released(rig, owner);
    }

    private static async Task StopOwners(bool scoped)
    {
        var activeRelease = new NativeGate();
        var pendingRelease = new NativeGate();
        var active = new Owner("stop-active") { ReleaseGate = activeRelease };
        var pending = new Owner("stop-pending") { ReleaseGate = pendingRelease };
        await using var rig = new Rig(active, pending);
        var activeIntent = Intent("stop-active", 31);
        var pendingIntent = Intent("stop-pending");
        await rig.Prepare(active, activeIntent).WaitAsync(Limit);
        await rig.Commit(activeIntent);
        var scope = rig.Playback.Scope!;
        await rig.Prepare(pending, pendingIntent).WaitAsync(Limit);
        await rig.Playback.StopAsync(scope with { Generation = scope.Generation + 1 }).WaitAsync(Limit);
        Check(rig.Playback.HasOutput && pending.Lease.DisposeCalls == 0,
            "a stale scoped stop leaves both current and prepared owners untouched");
        var stop = scoped ? rig.Playback.StopAsync(scope) : rig.Playback.StopAsync();
        await activeRelease.Arrival();
        await pendingRelease.Arrival();
        Check(!stop.IsCompleted && !rig.Playback.HasOutput && rig.Playback.Scope is null &&
            rig.Playback.PositionSeconds == 0 && active.Lease.DisposeCalls == 0 && pending.Lease.DisposeCalls == 0,
            "stop immediately clears publication but awaits both native owners");
        int commits = 0;
        await Superseded(rig.Playback.TryCommitAsync(pendingIntent, () => true, () => commits++));
        activeRelease.Open();
        await active.Lease.Disposed.Task.WaitAsync(Limit);
        Check(!stop.IsCompleted && pending.Lease.DisposeCalls == 0,
            "joining one owner does not complete stop while the other still runs");
        pendingRelease.Open();
        await stop.WaitAsync(Limit);
        Released(rig, active);
        Released(rig, pending);
        await rig.Playback.StopAsync(scope).WaitAsync(Limit);
        await rig.Playback.DiscardAsync(pendingIntent).WaitAsync(Limit);
        await rig.Playback.DisposeAsync();
        await rig.Playback.DisposeAsync();
        Check(commits == 0 && rig.Kernel.Values(pending, "start").Length == 0,
            "stopped candidates cannot be revived by late publication");
        Released(rig, active);
        Released(rig, pending);
    }

    private static async Task TrackIdentity()
    {
        var owner = new Owner("tracks");
        await using var rig = new Rig(owner);
        var intent = Intent("tracks", paused: true);
        await rig.Prepare(owner, intent);
        await rig.Commit(intent);
        var scope = rig.Playback.Scope!;
        var tracks = await rig.Playback.ReadTracksAsync();
        Check(tracks is { AudioIndex: 2, SubtitleIndex: null } && tracks.Scope == scope &&
            tracks.Audio.Select(track => track.Index).SequenceEqual([2, 6]) &&
            tracks.Subtitles.Select(track => track.Index).SequenceEqual([9]) &&
            tracks.Audio.Single(track => track.Selected).Index == 2,
            "filtered ordinals are not stream identities and video is not an audio track");
        await rig.Playback.SelectAudioTrackAsync(scope, 6);
        await rig.Playback.SelectSubtitleTrackAsync(scope, 9);
        Check(rig.Playback.Tracks is { AudioIndex: 6, SubtitleIndex: 9 } selected &&
            selected.Audio.Single(track => track.Selected).Index == 6 &&
            selected.Subtitles.Single(track => track.Selected).Index == 9,
            "selection is re-read from the current native stream snapshot");
        await rig.Playback.SelectSubtitleTrackAsync(scope, null);
        Check(rig.Playback.Tracks is { SubtitleIndex: null } disabled &&
            disabled.Subtitles.All(track => !track.Selected) &&
            rig.Kernel.Values(owner, "subtitle-track").SequenceEqual([9d, -1d]),
            "null disables subtitles through native -1, not a fabricated track");
        await rig.Playback.StopAsync(scope);
        Check(rig.Playback.Tracks is null, "retirement clears the exact scope's track snapshot");
        Released(rig, owner);
    }

    private static async Task InvalidTrack()
    {
        var owner = new Owner("invalid-track");
        await using var rig = new Rig(owner);
        var intent = Intent("invalid-track");
        await rig.Prepare(owner, intent);
        await rig.Commit(intent);
        var scope = rig.Playback.Scope!;
        async Task Reject(Task operation, string code)
        {
            try { await operation; }
            catch (PlaybackException error) when (error.Code == code) { return; }
            throw new InvalidOperationException("Expected " + code);
        }
        await Reject(rig.Playback.SelectAudioTrackAsync(scope, 0), PlaybackError.InvalidTrack);
        await Reject(rig.Playback.SelectSubtitleTrackAsync(scope, 6), PlaybackError.InvalidTrack);
        await Reject(rig.Playback.SelectAudioTrackAsync(scope with { Generation = scope.Generation + 1 }, 6),
            PlaybackError.Superseded);
        Check(!rig.Kernel.Calls.Any(call => call.Name.EndsWith("-track", StringComparison.Ordinal)),
            "invalid stream kinds and a retired scope never call native selection");
        owner.Tracks = [owner.Tracks[0], owner.Tracks[1], owner.Tracks[1]];
        await Reject(rig.Playback.ReadTracksAsync(), PlaybackError.TrackMetadataInvalid);
        Check(rig.Playback.Tracks is null && rig.Playback.HasOutput,
            "duplicate metadata cannot publish a partial list or retire a healthy output");
    }

    private static async Task CancelledTrack()
    {
        var selecting = new NativeGate();
        var releasing = new NativeGate();
        var owner = new Owner("cancel-track") { TrackGate = selecting, ReleaseGate = releasing };
        await using var rig = new Rig(owner);
        var intent = Intent("cancel-track");
        await rig.Prepare(owner, intent);
        await rig.Commit(intent);
        await rig.Playback.ReadTracksAsync();
        using var cancellation = new CancellationTokenSource();
        var operation = rig.Playback.SelectAudioTrackAsync(rig.Playback.Scope!, 6, cancellation.Token);
        await selecting.Arrival();
        cancellation.Cancel();
        Check(!operation.IsCompleted && owner.Lease.DisposeCalls == 0,
            "cancellation waits for the native selection executor");
        selecting.Open(); // Native deliberately returns success after cancellation.
        await releasing.Arrival();
        Check(!operation.IsCompleted && !rig.Playback.HasOutput && rig.Playback.Tracks is null &&
            owner.Lease.DisposeCalls == 0, "cancelled selection cannot publish and still joins native retirement");
        releasing.Open();
        await Cancelled(operation);
        Released(rig, owner);
    }

    private static async Task SupersededTracks()
    {
        var reading = new NativeGate();
        var owner = new Owner("superseded-tracks") { TrackReadGate = reading };
        await using var rig = new Rig(owner);
        var intent = Intent("superseded-tracks");
        await rig.Prepare(owner, intent);
        await rig.Commit(intent);
        var scope = rig.Playback.Scope!;
        var operation = rig.Playback.ReadTracksAsync();
        await reading.Arrival();
        var stop = rig.Playback.StopAsync(scope);
        Check(!rig.Playback.HasOutput && !stop.IsCompleted, "stop invalidates output before a blocked read returns");
        reading.Open();
        try { await operation.WaitAsync(Limit); }
        catch (OperationCanceledException)
        {
            await stop.WaitAsync(Limit);
            Check(rig.Playback.Tracks is null, "late track data cannot repopulate a retired scope");
            Released(rig, owner);
            return;
        }
        throw new InvalidOperationException("Expected cancelled track read");
    }

    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(Limit); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancelled preparation");
    }

    private static Task Superseded(Task task) => Failure(task, PlaybackError.Superseded);

    private static async Task Failure(Task task, string code)
    {
        try { await task.WaitAsync(Limit); }
        catch (PlaybackException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }

    private static void Released(Rig rig, Owner owner) =>
        Check(owner.NativeReleased.Task.IsCompletedSuccessfully && owner.Lease.DisposeCalls == 1 &&
            !owner.Lease.DisposedBeforeNativeRelease && rig.Kernel.Values(owner, "release").Length == 1,
            "native workers join before the exact lease is disposed, once");

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OutputPermit : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class NativeGate
    {
        private readonly TaskCompletionSource entered = Signal(), resume = Signal();
        internal Task Arrival() => entered.Task.WaitAsync(Limit);
        internal void Open() => resume.TrySetResult();
        internal void Wait()
        {
            entered.TrySetResult();
            resume.Task.WaitAsync(Limit).GetAwaiter().GetResult();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly Owner[] owners;
        internal KernelProbe Kernel { get; }
        internal FilmPlayback Playback { get; }
        internal Rig(params Owner[] owners) : this(null, owners) { }
        internal Rig(Func<CancellationToken, Task<IDisposable>>? acquireOutput, params Owner[] owners)
        {
            this.owners = owners;
            Kernel = new(owners);
            Playback = new(Dispatcher.CurrentDispatcher, Kernel, acquireOutput);
        }
        internal Task Prepare(Owner owner, RuntimeMediaPlaybackIntent intent, CancellationToken token = default) =>
            Playback.PrepareAsync(intent, owner.Address, new NativeVideoInput(NativeVideoFormat.Progressive), owner.Lease, token);
        internal async Task Commit(RuntimeMediaPlaybackIntent intent) =>
            Check(await Playback.TryCommitAsync(intent, () => true, () => { }).WaitAsync(Limit), "fixture commit succeeds");
        public async ValueTask DisposeAsync()
        {
            // Release every test-owned barrier even when an assertion fails.
            foreach (var owner in owners)
            {
                owner.PrepareGate?.Open(); owner.SeekGate?.Open(); owner.ReleaseGate?.Open();
                owner.TrackGate?.Open(); owner.TrackReadGate?.Open();
            }
            await Playback.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Owner
    {
        private readonly object gate = new();
        private FilmKernelNative.Snapshot snapshot = new()
        {
            Size = (uint)Marshal.SizeOf<FilmKernelNative.Snapshot>(), Abi = 1, Duration = 600,
            Width = 1920, Height = 1080, Seekable = 1, AudioTrack = 2, SubtitleTrack = -1
        };
        internal readonly Uri Address;
        internal readonly Lease Lease;
        internal readonly TaskCompletionSource NativeReleased = Signal();
        internal NativeGate? PrepareGate, SeekGate, ReleaseGate, TrackGate, TrackReadGate;
        internal bool SkipFirstPicture;
        internal FilmKernelNative.Track[] Tracks =
            [Track(0, 1, "h264"), Track(2, 2, "aac"), Track(6, 2, "aac"), Track(9, 3, "ass")];
        internal Owner(string name)
        {
            Address = new Uri($"http://127.0.0.1:41003/{name}.mp4");
            Lease = new(this);
        }
        internal FilmKernelNative.Snapshot Snapshot { get { lock (gate) return snapshot; } }
        internal void Update(int? state = null, double? position = null, int? error = null,
            int? audio = null, int? subtitle = null, ulong? pictures = null)
        {
            lock (gate)
            {
                if (state is { } s) snapshot.State = s;
                if (position is { } p) snapshot.Position = p;
                if (error is { } e) snapshot.Error = e;
                if (audio is { } a) snapshot.AudioTrack = a;
                if (subtitle is { } t) snapshot.SubtitleTrack = t;
                if (pictures is { } count) snapshot.SubmittedPictures = count;
            }
        }
        private static FilmKernelNative.Track Track(int index, int kind, string codec)
        {
            var value = new FilmKernelNative.Track
            {
                Size = (uint)Marshal.SizeOf<FilmKernelNative.Track>(), Index = index, Kind = kind,
                Codec = new byte[32], Language = new byte[32], Title = new byte[128]
            };
            Encoding.UTF8.GetBytes(codec).CopyTo(value.Codec, 0);
            return value;
        }
    }

    private sealed class Lease(Owner owner) : INativeMediaResourceLease
    {
        private readonly CancellationTokenSource revoked = new();
        private int disposals;
        internal readonly TaskCompletionSource Disposed = Signal();
        internal int DisposeCalls => Volatile.Read(ref disposals);
        internal volatile bool DisposedBeforeNativeRelease;
        internal bool FailRelease;
        public Uri Address => owner.Address;
        public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public CancellationToken Revoked => revoked.Token;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref disposals) == 1)
            {
                DisposedBeforeNativeRelease = !owner.NativeReleased.Task.IsCompletedSuccessfully;
                revoked.Cancel(); revoked.Dispose();
                Disposed.TrySetResult();
            }
            if (FailRelease) throw new InvalidOperationException("Synthetic lease release failure");
            return ValueTask.CompletedTask;
        }
    }

    // No override calls base: no kernel DLL, HTTP request, decoder or audio device is used.
    private sealed class KernelProbe(Owner[] owners) : FilmKernelApi
    {
        internal sealed record Call(Owner Owner, string Name, double Value);
        private readonly ConcurrentQueue<Call> calls = new();
        internal Call[] Calls => calls.ToArray();
        internal double[] Values(Owner owner, string name) =>
            calls.Where(call => call.Owner == owner && call.Name == name).Select(call => call.Value).ToArray();
        private Owner Record(IntPtr handle, string name, double value = 0)
        {
            var owner = owners[handle.ToInt32() - 1];
            calls.Enqueue(new(owner, name, value));
            return owner;
        }
        internal override IntPtr Create(ref FilmKernelNative.Callbacks callbacks, Uri address,
            NativeVideoFormat format, byte[] keys, byte[]? defaultKey)
        {
            int index = Array.FindIndex(owners, owner => owner.Address == address);
            Check(index >= 0 && format == NativeVideoFormat.Progressive, "only explicit fixture inputs reach the kernel");
            var handle = new IntPtr(index + 1);
            Record(handle, "create");
            return handle;
        }
        internal override int Prepare(IntPtr owner, double position, IntPtr target)
        {
            Check(target != IntPtr.Zero, "prepare receives the actual parked picture target");
            var state = Record(owner, "prepare", position);
            state.PrepareGate?.Wait();
            state.Update(state: 1, position: position);
            return 0;
        }
        internal override int Attach(IntPtr owner, IntPtr target)
        {
            Record(owner, target == IntPtr.Zero ? "detach" : "attach");
            return 0;
        }
        internal override int Start(IntPtr owner, int paused)
        {
            var value = Record(owner, "start", paused);
            value.Update(state: paused == 1 ? 3 : 2, pictures: value.SkipFirstPicture ? 0UL : 1UL);
            return 0;
        }
        internal override int Pause(IntPtr owner, int paused)
        {
            Record(owner, "pause", paused).Update(state: paused == 1 ? 3 : 2);
            return 0;
        }
        internal override int Seek(IntPtr owner, double position)
        {
            var state = Record(owner, "seek", position);
            state.SeekGate?.Wait();
            state.Update(position: position);
            return 0;
        }
        internal override int Rate(IntPtr owner, double rate) { Record(owner, "rate", rate); return 0; }
        internal override int Volume(IntPtr owner, double volume) { Record(owner, "volume", volume); return 0; }
        internal override int Aspect(IntPtr owner, double ratio) { Record(owner, "aspect", ratio); return 0; }
        internal override FilmKernelNative.Snapshot Read(IntPtr owner) => Record(owner, "read").Snapshot;
        internal override int TrackCount(IntPtr owner) => Record(owner, "track-count").Tracks.Length;
        internal override FilmKernelNative.Track ReadTrack(IntPtr owner, int ordinal)
        {
            var state = Record(owner, "track-read", ordinal);
            state.TrackReadGate?.Wait();
            return state.Tracks[ordinal];
        }
        internal override int SelectTrack(IntPtr owner, FilmTrackKind kind, int index)
        {
            var state = Record(owner, kind == FilmTrackKind.Audio ? "audio-track" : "subtitle-track", index);
            state.TrackGate?.Wait();
            if (kind == FilmTrackKind.Audio) state.Update(audio: index);
            else state.Update(subtitle: index);
            return 0;
        }
        internal override void Cancel(IntPtr owner) => Record(owner, "cancel");
        internal override void Release(IntPtr owner)
        {
            var state = Record(owner, "release");
            state.ReleaseGate?.Wait();
            state.Update(state: 7);
            state.NativeReleased.TrySetResult();
        }
    }
}
