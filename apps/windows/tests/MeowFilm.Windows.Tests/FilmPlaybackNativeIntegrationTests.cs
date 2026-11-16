using System.Text.Json;
using System.IO;
using MeowFilm.AppHost;
using MeowFilm.Platform.Storage;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackNativeFixture;

namespace MeowFilm.Windows.Tests;

// Selected independently; these require the actual packaged aggregate, Cat
// Worker, film decode/render kernel and WPF video surface. No external network source.
public static class FilmPlaybackNativeIntegrationTests
{
    private static TestCase Case(string name, Func<Task> body) =>
        new("film-native-integration: " + name, () => MusicQualityConsumerTests.Sta(body));

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("source menu is cold, more only searches, selected station alone details, then native plays",
            BrowsingAsync);
        foreach (bool headers in new[] { false, true })
            yield return Case((headers ? "header-required" : "Cat-owned headerless") +
                " Windows auto play records history only after real video and honors explicit watch capability",
                () => AutoPlayAsync(headers, reports: headers));
        yield return Case("same-episode replacement really seeks and stays paused, malformed candidate keeps the donor",
            ReplacementAsync);
        yield return Case("close drains its real session and delayed receipt cannot stop a newer native owner",
            CloseRaceAsync);
        yield return Case("SSE disposal keeps real output alive but exact runtime death physically retires native media",
            () => RuntimeTerminalAsync(pending: false));
        yield return Case("runtime death revokes a real pending candidate and physically retires its playing donor",
            () => RuntimeTerminalAsync(pending: true));
        yield return Case("two real Node processes restore preferences manual blocks and native history from the same data root",
            DurableRestartAsync);
    }

    private static async Task BrowsingAsync()
    {
        await using var f = await FilmPlaybackNativeFixture.CreateAsync(longVideo: true);
        // Isolate manual E08 requests from the separately tested Windows auto chain.
        var owner = await f.OpenAsync(platform: "test");
        Check((await f.CallsAsync()).Length == 0 && (await f.HistoryAsync()).Length == 0,
            "a non-auto browse session starts without Cat search/detail/play or history");
        int beforeMenu = f.Traffic.BusinessCalls;
        var picker = f.Element<ComboBox>("SourcePicker");
        picker.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(picker.IsDropDownOpen, "the actual WPF source dropdown opened");
        picker.IsDropDownOpen = false;
        await f.SnapshotAsync(owner.Id); // Real endpoint barriers, not a guessed sleep.
        int menuCommands = f.Traffic.BusinessCalls - beforeMenu;
        Check(menuCommands == 0 && (await f.CallsAsync()).Length == 0,
            "opening the source menu dispatches no Film command or Cat operation");

        picker.ApplyTemplate();
        var popup = picker.Template.FindName("PART_Popup", picker) as Popup ??
            throw new InvalidOperationException("Missing source picker template popup");
        var more = picker.Template.FindName("SearchMoreSourcesButton", picker) as Button ??
            throw new InvalidOperationException("Missing source picker template search-more button");
        picker.IsDropDownOpen = true;
        await Until(() => picker.IsDropDownOpen && popup.IsOpen &&
            more.IsLoaded && more.IsVisible && more.IsEnabled,
            "the real source popup exposes its loaded visible enabled search-more button");
        more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => !picker.IsDropDownOpen && !popup.IsOpen,
            "the actual search-more action closes its source popup");
        await Until(async () =>
        {
            var state = await f.SnapshotAsync(owner.Id);
            return FilmJson.Bool(state, "searchComplete") && FilmJson.Array(state, "sources").Length == Sites.Length;
        }, "real more-search completes its source pages");
        await Until(() => owner.Page.Sources.Count(row => row.Id is not null) == Sites.Length, "SSE updates real source choices");
        var searches = await f.CallsAsync();
        Check(searches.Length == Sites.Length && searches.All(row => FilmJson.Text(row, "operation") == "search") &&
            searches.Select(row => FilmJson.Text(row, "site")).Order().SequenceEqual(Sites.Order()),
            "load more calls exactly search on each unfinished station, never detail/list/play");
        beforeMenu = f.Traffic.BusinessCalls;
        picker.IsDropDownOpen = true;
        await Until(() => picker.IsDropDownOpen && popup.IsOpen, "the populated real source menu opened");
        Check(picker.Items.Cast<FilmSelection>().SequenceEqual(owner.Page.Sources) &&
            picker.Items.Cast<FilmSelection>().Count(row => row.Id is not null) == Sites.Length &&
            picker.Items.Cast<FilmSelection>().Where(row => row.MetadataSourceId is not null)
                .SequenceEqual(owner.Page.MetadataSources),
            "the source menu contains the exact real station and metadata choices, without placeholders");
        picker.IsDropDownOpen = false;
        await f.SnapshotAsync(owner.Id);
        menuCommands += f.Traffic.BusinessCalls - beforeMenu;
        Check(menuCommands == 0 && (await f.CallsAsync()).Length == searches.Length,
            "opening a populated source menu also requests no detail or play");
        var snapshot = await f.SnapshotAsync(owner.Id);
        string selectedId = FilmJson.RequiredText(FilmJson.Array(snapshot, "sources").Single(row =>
            FilmJson.Text(FilmJson.Member(row, "source"), "key") == SourceKey("alpha")), "id");
        picker.SelectedItem = owner.Page.Sources.Single(row => row.Id == selectedId);
        await Until(async () =>
        {
            var browsing = FilmJson.Member(await f.SnapshotAsync(owner.Id), "browsing");
            return FilmJson.Text(browsing, "sourceId") == selectedId && !FilmJson.Bool(browsing, "loading") &&
                FilmJson.Array(browsing, "episodes").Length == 1;
        }, "selected station finishes real detail");
        await Until(() => owner.Page.Candidates.Count == 1, "selected source candidates reach the real page");
        var details = (await f.CallsAsync()).Skip(searches.Length).ToArray();
        Check(details.Length == 1 && FilmJson.Text(details[0], "operation") == "detail" &&
            FilmJson.Text(details[0], "site") == "alpha" &&
            FilmJson.Member(FilmJson.Member(details[0], "payload"), "id").GetInt32() == 0,
            "choosing A requests only A detail, preserving its numeric zero identity");
        var browseReads = FilmJson.Array(await f.WorkerStateAsync(), "reads");
        var browseHistory = await f.HistoryAsync();
        var browseReports = await f.ReportsAsync();
        Check(browseReads.Length == 0 && browseHistory.Length == 0 && browseReports.Length == 0 && !f.Native.HasOutput,
            "search and directory browsing are not media output or viewing");
        Observe("browse-counts", new
        {
            menuCommands, searchCalls = searches, selectedDetails = details,
            mediaReads = browseReads.Length, history = browseHistory.Length, watchReports = browseReports.Length
        });

        await owner.Page.PlayAsync();
        string attempt = await f.OutputAsync(owner);
        var resolved = (await f.CallsAsync()).Where(row => FilmJson.Text(row, "operation") is "detail" or "play").ToArray();
        Check(resolved.All(row => FilmJson.Text(row, "site") == "alpha") &&
            resolved.Count(row => FilmJson.Text(row, "operation") == "detail") == 1 &&
            resolved.Count(row => FilmJson.Text(row, "operation") == "play") == 1,
            "play reuses the selected complete detail without another detail request or touching other stations");
        await Until(async () => (await f.HistoryAsync()).Any(row => FilmJson.Text(row, "targetId") == owner.TargetId),
            "explicit native play persists actual viewing");
        await owner.Session.CloseSessionAsync();
        await f.SessionGoneAsync(owner.Id);
        await f.ResourceRetiredAsync(f.Traffic.Receipts(owner.Id).Single(row => row.AttemptId == attempt));
        f.NoBackgroundFailures();
    }

    private static async Task AutoPlayAsync(bool headers, bool reports)
    {
        await using var f = await FilmPlaybackNativeFixture.CreateAsync(headers, reports);
        var gate = f.Traffic.HoldNextReceipt();
        var owner = await f.OpenAsync();
        var receipt = await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
        var before = await f.WorkerStateAsync();
        var pending = await f.SnapshotAsync(owner.Id);
        var connection = await f.Runtime.CaptureConnectionAsync();
        if (headers)
            Check(receipt.MediaOrigin is { Scheme: "http", Host: "127.0.0.1" } origin &&
                receipt.Address.GetLeftPart(UriPartial.Authority) + "/" == origin.AbsoluteUri &&
                origin != connection.BusinessOrigin, "declared headers select the real controlled media stream");
        else
        {
            var status = Okay(await f.Runtime.CallAsync("cat.script.status", new { }), "cat.script.v2");
            var aggregate = new Uri(FilmJson.RequiredText(FilmJson.Member(status, "aggregate"), "origin"));
            Check(receipt.MediaOrigin is null && receipt.Address.IsLoopback &&
                receipt.Address.GetLeftPart(UriPartial.Authority) == aggregate.GetLeftPart(UriPartial.Authority) &&
                receipt.Address.AbsolutePath.StartsWith($"/proxy/{Installation}/{f.Generation}/", StringComparison.Ordinal) &&
                aggregate != connection.BusinessOrigin, "headerless media uses this exact live Cat aggregate without a film stream");
        }
        Check(FilmJson.Text(pending, "stage") == "pending" &&
            FilmJson.Text(FilmJson.Member(pending, "attempt"), "id") == receipt.AttemptId,
            "address receipt is pending, not claimed as media success");
        var beforeHistory = await f.HistoryAsync();
        var beforeReports = await f.ReportsAsync();
        var beforeReads = FilmJson.Array(before, "reads");
        Check(!f.Native.HasOutput && f.Native.Output is null &&
            beforeHistory.Length == 0 && beforeReports.Length == 0 &&
            beforeReads.Length == 0 && f.Traffic.Commands("media.started").Length == 0,
            "a real returned URL alone cannot read bytes, publish video, write history or report viewing");
        var initialCalls = FilmJson.Array(before, "calls");
        Check(initialCalls.Select(row => FilmJson.Text(row, "operation")).SequenceEqual(["search", "detail", "play"]) &&
            initialCalls.All(row => FilmJson.Text(row, "site") == "alpha") &&
            !FilmJson.Bool(pending, "searchComplete") &&
            FilmJson.Array(pending, "search").Where(row => FilmJson.Text(row, "sourceKey") != SourceKey("alpha"))
                .All(row => !FilmJson.Bool(row, "completed")),
            "Windows auto play stops new discovery at the pending address without exhausting untouched sources");
        Observe("before-native", new
        {
            headers, reports, generation = f.Generation, stage = FilmJson.Text(pending, "stage"),
            calls = initialCalls, videoPictures = f.Native.Output?.SubmittedPictures ?? 0,
            nativeHasOutput = f.Native.HasOutput, mediaReads = beforeReads.Length,
            history = beforeHistory.Length, watchReports = beforeReports.Length,
            searchComplete = FilmJson.Bool(pending, "searchComplete")
        });

        gate.Release(); // Deliver exactly the actual receipt, not a fabricated event or native result.
        string attempt = await f.OutputAsync(owner);
        Check(attempt == receipt.AttemptId, "the prepared resource is the actual native occurrence");
        var nativeAllocation = f.CurrentAllocation();
        await Until(async () => (await f.HistoryAsync()).Any(row => FilmJson.Text(row, "targetId") == owner.TargetId &&
            FilmJson.Text(row, "sourceKey") == SourceKey("alpha")), "actual Windows history after output");
        var after = await f.WorkerStateAsync();
        Check(FilmJson.Array(after, "reads") is { Length: > 0 } reads &&
            reads.All(row => FilmJson.Bool(row, "headerAccepted") && FilmJson.Text(row, "site") == "alpha"),
            "all real native media reads reach the Cat-owned local origin with required header policy");
        Check(FilmJson.Array(after, "calls").Count(row => FilmJson.Text(row, "operation") != "report") == initialCalls.Length,
            "actual output does not continue searching/detailing unused sources");
        if (reports)
            await Until(async () => (await f.ReportsAsync()).Any(row =>
                FilmJson.Text(FilmJson.Member(row, "payload"), "event") == "started"), "native-triggered formal watch started");
        else Check((await f.ReportsAsync()).Length == 0, "no report capability means no report even after actual video output");

        await Until(() => owner.Page.Duration > 1, "real media duration");
        await owner.Page.SeekAsync(.75);
        await owner.Session.SetPausedAsync(false);
        await Until(async () =>
        {
            var current = FilmJson.Member(await f.SnapshotAsync(owner.Id), "current");
            return FilmJson.Text(current, "attemptId") == attempt && !FilmJson.Bool(current, "paused");
        }, "real resumed media progress");
        await owner.Session.SetPausedAsync(true);
        await Until(async () => (await f.HistoryAsync()).Any(row => FilmJson.Bool(row, "paused") &&
            FilmJson.Number(row, "position") >= .5), "pause forces actual position history without waiting a guessed TTL");
        Observe("viewing", new { history = await f.HistoryAsync(), reads = FilmJson.Array(after, "reads"), reports = await f.ReportsAsync() });

        await owner.Page.SeekAsync(Math.Max(0, owner.Page.Duration - .5));
        await owner.Session.SetPausedAsync(false);
        await Until(async () => f.NativeEnded == 1 && f.Traffic.Commands("media.ended").Any(call => call.Completed) &&
            FilmJson.Text(await f.SnapshotAsync(owner.Id), "stage") == "ended", "real decoder/clock natural end reaches Node");
        await Until(async () => (await f.HistoryAsync()).Any(row => FilmJson.Bool(row, "ended") &&
            FilmJson.Number(row, "position") > 1 && FilmJson.Number(row, "duration") > 1), "real ended history");
        if (reports)
            await Until(async () => (await f.ReportsAsync()).Any(row =>
                FilmJson.Text(FilmJson.Member(row, "payload"), "event") == "stopped"), "real terminal watch report");
        var watch = await f.ReportsAsync();
        Check(reports ? watch.Length > 0 : watch.Length == 0, "watch capability is an explicit opt-in, not inferred from endpoints");
        if (reports)
            Check(new[] { "started", "progress", "stopped" }.All(required => watch.Any(row =>
                FilmJson.Text(FilmJson.Member(row, "payload"), "event") == required)),
                "real output, progress and natural end each drive the declared watch capability");
        Check(watch.All(row => CorrectReport(row, "alpha", attempt)),
            "every formal watch report preserves its actual source, original flag, opaque play id and native attempt");
        Check(f.Traffic.Commands("media.ended").Single().Attempt == attempt,
            "natural terminal feedback uses the exact original native identity");
        await owner.Session.CloseSessionAsync();
        await f.SessionGoneAsync(owner.Id);
        await f.ResourceRetiredAsync(receipt);
        Check(nativeAllocation.PhysicallyReleased && !f.Native.HasOutput, "close proves actual native retirement");
        Observe("natural-end", new
        {
            headers, reportsEnabled = reports, nativeEnded = f.NativeEnded,
            actualHistory = await f.HistoryAsync(), actualReports = watch,
            nativeReleased = nativeAllocation.PhysicallyReleased, resourceReleased = true,
            controlledStream404 = headers, sharedCatDirectRetained = !headers, sessionGone = true
        });
        f.NoBackgroundFailures();
    }

    private static async Task ReplacementAsync()
    {
        await using var f = await FilmPlaybackNativeFixture.CreateAsync(headers: true, longVideo: true);
        int activeFailures = 0;
        f.Native.Failed += (_, _) => activeFailures++;
        var owner = await f.OpenAsync();
        string first = await f.OutputAsync(owner);
        var firstAllocation = f.CurrentAllocation();
        await owner.Page.SeekAsync(7);
        await Until(async () => Math.Abs(f.Native.PositionSeconds - 7) < .5 &&
            Math.Abs(FilmJson.Number(FilmJson.Member(await f.SnapshotAsync(owner.Id), "current"), "position") - 7) < .5,
            "real paused donor seek and progress");
        await owner.Page.CommandAsync("switch");
        await Until(() => owner.Session.MediaIdentity is { } id && id != first && owner.Session.HasOutput &&
            f.Native.Identity == id, "replacement actually commits a new native occurrence");
        // Observe preserved pause before any test command could repair it.
        string replacement = await f.OutputAsync(owner, pauseAfterOutput: false);
        var donorAllocation = f.CurrentAllocation();
        Check(replacement != first && !firstAllocation.Matches(donorAllocation) &&
            firstAllocation.PhysicallyReleased && f.Native.PauseRequested && !owner.Session.IsPlaying &&
            Math.Abs(f.Native.PositionSeconds - 7) < .5, "same-episode replacement retained actual native time and pause, not merely a DTO");
        var good = f.Traffic.Receipts(owner.Id).Single(row => row.AttemptId == replacement);
        await f.ResourceRetiredAsync(f.Traffic.Receipts(owner.Id).Single(row => row.AttemptId == first));
        var currentCandidate = FilmJson.Member(FilmJson.Member(await f.SnapshotAsync(owner.Id), "current"), "candidate");
        Check(FilmJson.Text(FilmJson.Member(currentCandidate, "source"), "key") == SourceKey("beta"),
            "the new native occurrence is the selected alternate source");
        Observe("same-episode", new
        {
            oldAttempt = first, newAttempt = replacement, realPosition = f.Native.PositionSeconds,
            realPaused = f.Native.PauseRequested, oldNativeReleased = firstAllocation.PhysicallyReleased
        });

        await owner.Page.CommandAsync("switch");
        await Until(async () => f.Traffic.Commands("media.failed").Any(call => call.Completed) &&
            FilmJson.Text(await f.SnapshotAsync(owner.Id), "stage") is "unavailable" or "error",
            "malformed candidate fails through the real native decoder and exhausts remaining alternatives");
        var failed = f.Traffic.Commands("media.failed").Single(call => call.Completed);
        var failureSnapshot = await f.SnapshotAsync(owner.Id);
        Check(FilmJson.Text(failureSnapshot, "error") == PlaybackError.OpenFailed &&
            FilmJson.Text(failureSnapshot, "errorStage") == "prepare",
            "the intentionally malformed candidate reports its actual bounded prepare error");
        var rejected = f.Traffic.Receipts(owner.Id).Single(row => row.AttemptId == failed.Attempt);
        var rejectedReads = FilmJson.Array(await f.WorkerStateAsync(), "reads")
            .Where(row => FilmJson.Text(row, "site") == "broken").ToArray();
        Check(rejectedReads.Length > 0 && rejectedReads.All(row => FilmJson.Bool(row, "headerAccepted")),
            "the bad candidate reached the real demux through its authorized bytes, not an adapter stub failure");
        Check(f.Native.Identity == replacement && owner.Session.HasOutput && donorAllocation.IsCurrent &&
            donorAllocation.IsAlive && f.Native.PauseRequested && Math.Abs(f.Native.PositionSeconds - 7) < .5 &&
            !f.Traffic.Commands("media.started").Any(call => call.Attempt == failed.Attempt),
            "real failed preparation cannot retire or reset its still healthy same-episode donor");
        await f.ResourceRetiredAsync(rejected);
        var watch = await f.ReportsAsync();
        Check(watch.All(row => FilmJson.Text(row, "site") != "broken") &&
            (await f.HistoryAsync()).All(row => FilmJson.Text(row, "sourceKey") != SourceKey("broken")),
            "a fetched but undecodable movie never becomes viewed or reported");
        Observe("failed-candidate", new
        {
            rejectedAttempt = failed.Attempt, retainedAttempt = replacement,
            actualPosition = f.Native.PositionSeconds, actualPaused = f.Native.PauseRequested,
            badResource404 = true, rejectedReads, calls = await f.CallsAsync(), reports = watch
        });
        await owner.Session.CloseSessionAsync();
        await f.SessionGoneAsync(owner.Id);
        await f.ResourceRetiredAsync(good);
        Check(donorAllocation.PhysicallyReleased, "close really releases the preserved donor");
        Check(f.FailureCodes.SequenceEqual([PlaybackError.OpenFailed]) && activeFailures == 0,
            "only the intentional candidate prepare failure is notified; the healthy donor never emits active failure");
    }

    private static async Task CloseRaceAsync()
    {
        await using var f = await FilmPlaybackNativeFixture.CreateAsync(headers: true, longVideo: true);
        var old = await f.OpenAsync();
        string oldAttempt = await f.OutputAsync(old);
        var oldAllocation = f.CurrentAllocation();
        var gate = f.Traffic.HoldNextReceipt();
        await old.Page.CommandAsync("switch");
        var late = await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
        var closing = old.Session.CloseSessionAsync();
        Check(!closing.IsCompleted, "old close waits for its actually admitted native consumer receipt");
        // Independent stable Film owner, but still the App's SAME FilmPlayback.
        var next = await f.OpenAsync(targetId: "film:native-successor");
        string nextAttempt = await f.OutputAsync(next);
        var nextAllocation = f.CurrentAllocation();
        Check(nextAttempt != oldAttempt && nextAttempt != late.AttemptId, "successor is a distinct actual occurrence");
        gate.Release();
        await closing.WaitAsync(TimeSpan.FromSeconds(12));
        await old.Session.DisposeAsync();
        await f.SessionGoneAsync(old.Id);
        foreach (var receipt in f.Traffic.Receipts(old.Id)) await f.ResourceRetiredAsync(receipt);
        Check(oldAllocation.PhysicallyReleased && f.Native.Identity == nextAttempt &&
            next.Session.HasOutput && nextAllocation.IsCurrent && nextAllocation.IsAlive &&
            !f.Traffic.Commands("media.started").Any(call => call.Attempt == late.AttemptId),
            "settled old close/discard/release and its late real receipt cannot stop or publish over the newer owner");
        try { await old.Page.CommandAsync("stop"); throw new InvalidOperationException("Old page unexpectedly controlled the successor."); }
        catch (FilmPlaybackException error) when (error.Code == "STALE_SESSION") { }
        Observe("late-close", new
        {
            oldSession = old.Id, newSession = next.Id, lateAttempt = late.AttemptId, keptAttempt = nextAttempt,
            actualOldAllocationReleased = oldAllocation.PhysicallyReleased,
            newAllocationStillOwned = nextAllocation.IsAlive,
            oldSessionGone = true, oldResources404 = true, oldConsumerSettled = closing.IsCompletedSuccessfully
        });
        await next.Session.CloseSessionAsync();
        await f.SessionGoneAsync(next.Id);
        foreach (var receipt in f.Traffic.Receipts(next.Id)) await f.ResourceRetiredAsync(receipt);
        Check(nextAllocation.PhysicallyReleased && !f.Native.HasOutput, "the successor also has one real final drain");
        f.NoBackgroundFailures();
    }

    private static async Task RuntimeTerminalAsync(bool pending)
    {
        await using var f = await FilmPlaybackNativeFixture.CreateAsync(headers: true, longVideo: true);
        var owner = await f.OpenAsync();
        string attempt = await f.OutputAsync(owner, pauseAfterOutput: false);
        var allocation = f.CaptureNative();
        var epoch = await f.Runtime.CaptureConnectionAsync();
        long pictures = allocation.Pictures;
        Check(pictures > 0 && allocation.IsAlive &&
            owner.Session.IsPlaying && !f.Native.PauseRequested, "the terminal victim is a genuinely playing movie decoder/render occurrence");
        RuntimeTraffic.ReceiptGate? gate = null;
        string? candidate = null;
        try
        {
            if (pending)
            {
                gate = f.Traffic.HoldNextReceipt();
                await owner.Page.CommandAsync("switch");
                var receipt = await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
                candidate = receipt.AttemptId;
                var snapshot = await f.SnapshotAsync(owner.Id);
                Check(candidate != attempt && FilmJson.Text(snapshot, "stage") == "pending" &&
                    FilmJson.Text(FilmJson.Member(snapshot, "attempt"), "id") == candidate &&
                    FilmJson.Text(FilmJson.Member(snapshot, "current"), "attemptId") == attempt &&
                    f.Native.Identity == attempt && owner.Session.HasOutput &&
                    allocation.IsCurrent && !f.Native.PauseRequested &&
                    !f.Traffic.Commands("media.started").Any(call => call.Attempt == candidate),
                    "a real admitted Node candidate waits behind its receipt while the exact old donor still plays");
                Observe("terminal-pending", new
                {
                    currentAttempt = attempt, pendingAttempt = candidate, candidateReceiptReal = true,
                    donorPictures = allocation.Pictures, donorStillPlaying = owner.Session.IsPlaying
                });
            }
            pictures = allocation.Pictures;
            double position = f.Native.PositionSeconds;
            await f.Traffic.StopEventsAsync(owner.Id);
            await Until(() => allocation.Pictures > pictures && f.Native.PositionSeconds > position,
                "real frames and clock keep advancing after only the HTTP observation drains");
            Check(f.Runtime.IsConnected && !epoch.Revoked.IsCancellationRequested &&
                owner.Session.HasOutput && f.Native.Identity == attempt &&
                allocation.IsCurrent && allocation.IsAlive,
                "disposing a real SSE subscription cannot revoke the process or stop its current native output");
            Observe("sse-keeps-native", new
            {
                pendingScenario = pending, attemptId = attempt, picturesBefore = pictures,
                picturesAfter = allocation.Pictures, positionBefore = position, positionAfter = f.Native.PositionSeconds,
                epochRevoked = epoch.Revoked.IsCancellationRequested, nativeStillOwned = true
            });
            await f.KillRuntimeAsync();
        }
        finally { gate?.Release(); }

        await Until(() => allocation.PhysicallyReleased && !f.Native.HasOutput && !owner.Session.HasOutput &&
            f.CurrentSession is null &&
            AdaptiveMediaTests.Field(f.Native, "pending") is null,
            "runtime lifetime revocation alone joins actual movie decode/render/audio workers, readers and resource release");
        Check(epoch.Revoked.IsCancellationRequested && allocation.PhysicallyReleased,
            "physical native retirement precedes any test close/stop cleanup");
        // Only after physical retirement: a missing RPC ACK is not a claim of remote release.
        var receipts = f.Traffic.Receipts(owner.Id);
        Check(receipts.Length == (pending ? 2 : 1), "only the actual current/pending resources were admitted");
        string releaseCode = "";
        try
        {
            await new FilmPlaybackClient(f.Runtime).ReleaseMediaAsync(owner.Id, attempt,
                receipts.Single(row => row.AttemptId == attempt).ResourceId);
            throw new InvalidOperationException("An exited runtime cannot acknowledge a media release.");
        }
        catch (BridgeException error) when (error.Code == "CLOSED") { releaseCode = error.Code; }
        foreach (var receipt in receipts) await f.ResourceUnavailableAfterExitAsync(receipt);
        Check(candidate is null || !f.Traffic.Commands("media.started").Any(call => call.Attempt == candidate),
            "an already returned candidate cannot publish after its exact process epoch dies");
        // This is cleanup, deliberately after the independent physical-stop assertions.
        await owner.Session.CloseSessionAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Check(allocation.PhysicallyReleased && !f.Native.HasOutput &&
            (candidate is null || !f.Traffic.Commands("media.started").Any(call => call.Attempt == candidate)),
            "settled late consumers cannot revive a retired donor or commit the pending candidate");
        Observe("terminal-native-drain", new
        {
            pendingScenario = pending, oldAttempt = attempt, pendingAttempt = candidate,
            nativePointerReleased = allocation.PhysicallyReleased,
            demuxReadersAndResourceReleased = allocation.PhysicallyReleased,
            runtimeDrain = f.Runtime.Completion.IsCompletedSuccessfully, resourceCount = receipts.Length,
            releaseAck = false, releaseCode, failureCodes = f.FailureCodes
        });
    }

    private static async Task DurableRestartAsync()
    {
        await using var f = await FilmPlaybackNativeFixture.CreateAsync(headers: true, longVideo: true);
        var owner = await f.OpenAsync(targetId: "film:native-durable");
        string attempt = await f.OutputAsync(owner);
        var allocation = f.CaptureNative();
        await owner.Page.SeekAsync(7);
        await Until(async () => (await f.HistoryAsync()).Any(row =>
            FilmJson.Text(row, "targetId") == owner.TargetId && FilmJson.Bool(row, "paused") &&
            Math.Abs(FilmJson.Number(row, "position") - 7) < .5), "real native seek and pause are durably acknowledged");
        var selected = FilmJson.Member(FilmJson.Member(await f.SnapshotAsync(owner.Id), "current"), "candidate");
        var block = FilmJson.Encode(new
        {
            targetId = owner.TargetId, sourceKey = FilmJson.Text(FilmJson.Member(selected, "source"), "key"),
            detailId = FilmJson.Member(FilmJson.Member(selected, "source"), "detailId"),
            lineId = FilmJson.Member(selected, "lineId")
        });
        var gate = f.Traffic.HoldNextReceipt();
        try
        {
            await owner.Page.CommandAsync("block", new { attemptId = attempt });
            await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var saved = FilmJson.Array(await f.LibraryAsync(), "blocks").Single();
            Check(JsonElement.DeepEquals(saved, block), "formal block commits the exact confirmed current attempt identity");
            var closing = owner.Session.CloseSessionAsync();
            gate.Release();
            await closing.WaitAsync(TimeSpan.FromSeconds(12));
        }
        finally { gate.Release(); }
        await f.SessionGoneAsync(owner.Id);
        var receipts = f.Traffic.Receipts(owner.Id);
        foreach (var receipt in receipts) await f.ResourceRetiredAsync(receipt);
        Check(allocation.PhysicallyReleased && !f.Native.HasOutput, "actual first-process media and private inputs drain before cold restart");

        string root = Path.GetDirectoryName(f.DataRoot)!;
        await using var store = new LocalDocumentStore(Path.Combine(root, "documents"), AppIdentity.NativeNamespaces,
            AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
        var settings = new FilmPlaybackSettings(f.Runtime, store);
        var preferences = new FilmPreferences(["keyword", "pan", "quality"],
            ["baidu", "uc", "quark", "tianyi", "yidong"], ["原始优先词", "第二关键词"],
            ["本地净化词", "直播"], false);
        var committed = await settings.SavePreferencesAsync(preferences);
        Check(committed.RuleOrder.SequenceEqual(preferences.RuleOrder) && committed.PanOrder.SequenceEqual(preferences.PanOrder) &&
            committed.Keywords.SequenceEqual(preferences.Keywords) &&
            committed.SiteCleanKeywords.SequenceEqual(preferences.SiteCleanKeywords) && !committed.IosAutoPrepare,
            "native document receipt preserves all nondefault orders and values without a runtime acknowledgement");
        var siteManual = FilmJson.Encode(new
        {
            id = "native-site-manual", targetId = owner.TargetId, kind = "site", name = "本机站点",
            sourceKey = SourceKey("alpha"), detailId = 0, seasonHint = 0, enabled = true, autoDisable = true
        });
        var shareManual = FilmJson.Encode(new
        {
            id = "native-share-manual", targetId = "film:native-manual-only", kind = "share", name = "本机合成分享",
            provider = "quark", share = new { url = "https://example.invalid/fixture-share", password = "fixture-only" },
            seasonHint = 2, enabled = false, autoDisable = false
        });
        await settings.SaveManualAsync(siteManual);
        await settings.SaveManualAsync(shareManual);
        var before = await f.LibraryAsync();
        var history = FilmJson.Array(before, "history").Single();
        var manual = FilmJson.Array(before, "manual");
        var site = manual.Single(row => FilmJson.Text(row, "id") == "native-site-manual");
        var share = manual.Single(row => FilmJson.Text(row, "id") == "native-share-manual");
        Check(FilmJson.Text(history, "targetId") == owner.TargetId &&
            FilmJson.Text(history, "sourceKey") == SourceKey("alpha") &&
            history.GetProperty("detailId").ValueKind == JsonValueKind.Number && history.GetProperty("detailId").GetInt32() == 0 &&
            FilmJson.Bool(history, "paused") && Math.Abs(FilmJson.Number(history, "position") - 7) < .5 &&
            FilmJson.Number(history, "duration") > 7 &&
            FilmJson.Array(before, "blocks").Length == 1 && JsonElement.DeepEquals(FilmJson.Array(before, "blocks")[0], block),
            "history comes from the real paused donor, never the fetched replacement; block keeps its typed identity");
        Check(manual.Length == 2 &&
            site.GetProperty("detailId").ValueKind == JsonValueKind.Number && site.GetProperty("detailId").GetInt32() == 0 &&
            FilmJson.Integer(site, "seasonHint") == 0 && FilmJson.Bool(site, "enabled") && FilmJson.Bool(site, "autoDisable") &&
            FilmJson.Integer(share, "seasonHint") == 2 && !FilmJson.Bool(share, "enabled") &&
            FilmJson.Text(FilmJson.Member(share, "share"), "url") == "https://example.invalid/fixture-share" &&
            FilmJson.Text(FilmJson.Member(share, "share"), "password") == "fixture-only",
            "both formal manual variants preserve typed site, synthetic private share and special-season hints");
        string stored = File.ReadAllText(Path.Combine(f.DataRoot, "film-playback", "library.json"));
        Check(!stored.Contains("/media/", StringComparison.Ordinal) && !stored.Contains("/proxy/", StringComparison.Ordinal) &&
            !stored.Contains("meowfilm-ffmpeg:", StringComparison.Ordinal) &&
            receipts.All(receipt => !stored.Contains(receipt.Address.AbsoluteUri, StringComparison.Ordinal)) &&
            !System.Text.RegularExpressions.Regex.IsMatch(stored, @"[?&](?:sign(?:ature)?|token|access_token|auth_key|authorization|expires?)=",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "the durable library never stores this run's media capability, private demux URL or signed media address");
        f.NoBackgroundFailures();
        await f.DisposeAsync(); // Close the actual first Node, not merely another library object.
        await store.DisposeAsync();
        await using var next = await FilmMetadataRuntimeTests.RuntimeHost.OpenAsync(root);
        var restored = (Library: await next.FilmAsync("library.get", new { }),
            Preferences: await next.WaitSettingsAsync("film.preferences"));
        Check(next.ProcessId != f.Runtime.ProcessId && File.ReadAllText(Path.Combine(f.DataRoot, "film-playback", "library.json")) == stored,
            "the real cold native-settings runtime has a new process and its read-only business access preserves the durable library bytes");
        JsonElement Business(JsonElement value) => FilmJson.Encode(value.EnumerateObject()
            .Where(item => item.Name != "preferences").ToDictionary(item => item.Name, item => item.Value.Clone()));
        Check(JsonElement.DeepEquals(Business(before), Business(restored.Library)) &&
            JsonElement.DeepEquals(FilmJson.Encode(preferences), restored.Preferences),
            "a second actual process consumes native preferences and reads identical Node-owned manual blocks and history");
        Observe("durable-library", new
        {
            historyCount = FilmJson.Array(restored.Library, "history").Length,
            manualCount = FilmJson.Array(restored.Library, "manual").Length,
            blockCount = FilmJson.Array(restored.Library, "blocks").Length,
            position = FilmJson.Number(history, "position"), paused = FilmJson.Bool(history, "paused"),
            detailKind = history.GetProperty("detailId").ValueKind.ToString(),
            originalOrders = true, sameLibrary = true, transientMediaPersisted = false, realAccounts = false
        });
    }

    private static bool CorrectReport(JsonElement row, string site, string attempt)
    {
        var payload = FilmJson.Member(row, "payload");
        return FilmJson.Text(row, "site") == site && FilmJson.Text(row, "operation") == "report" &&
            FilmJson.Text(payload, "flag") == Flag(site) && FilmJson.Text(payload, "id") == PlayId(site) &&
            FilmJson.Text(payload, "sessionId") == attempt &&
            FilmJson.Text(payload, "event") is "started" or "progress" or "stopped";
    }
}
