using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.RecommendationTest;

namespace MeowFilm.Windows.Tests;

public static partial class MusicRecommendationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Recommendation per-occurrence source facts preserve all similar seeds and FM scene", SourceFacts);
        yield return new("Recommendation random non-tail independent take branch once per playback", () => RandomTrigger(true, false));
        yield return new("Recommendation random tail independent skip branch without tail fallback", () => RandomTrigger(false, true));
        foreach (bool active in new[] { false, true }) foreach (bool next in new[] { false, true })
        {
            bool started = active, priority = next;
            yield return new("Recommendation append boundary active=" + active + " next=" + next, () => SearchAppendBoundary(started, priority));
        }
        foreach (bool next in new[] { false, true })
        {
            bool priority = next;
            yield return new("Recommendation initial read ownership during append next=" + next, () => SearchInFlight(priority));
        }
        yield return new("Recommendation failed search preview retry after play-next keeps the original seed", SearchInitialRetry);
        foreach (bool enter in new[] { false, true })
        {
            bool selected = enter;
            yield return new("Recommendation shuffled candidate append activates only on a recommendation selection=" + enter,
                () => QueueSelectionActivation(selected));
        }
        foreach (int mode in new[] { 1, 3 })
        {
            int loop = mode;
            yield return new("Recommendation tail trigger is immediate and nonblocking in global mode " + mode, () => LoopTail(loop));
        }
        yield return new("Recommendation source replacement failure preserves the committed source and account retirement stops it", StagedSourceFailure);
        yield return new("Recommendation active queue cursor follows response offsets while seed never changes", FixedSeedPages);
        yield return new("Recommendation FM first batch replaces only after success", First);
        yield return new("Recommendation initial failure leaves ordinary queue and media intact", InitialFailure);
        yield return new("Recommendation FM modes append preserve occurrences and select new batch", Switch);
        yield return new("Recommendation FM channel keeps an old first recording as a new occurrence", () => FmSwitchRepeated(false));
        yield return new("Recommendation FM all-repeated channel is a successful new occurrence batch", () => FmSwitchRepeated(true));
        yield return new("Recommendation FM committed projection retains four modes and HOT_SONG scene", FmCommittedModes);
        yield return new("Recommendation native identity dedup does not merge same title recordings", Identity);
        yield return new("Recommendation FM rule batch admits five and preserves actual provenance", Rule);
        yield return new("Recommendation tail pending is single flight and pause fences late autoplay", TailPause);
        yield return new("Recommendation repeated Ended coalesces one advance", DuplicateEnded);
        yield return new("Recommendation failure has no busy retry and manual retry remains", Backoff);
        yield return new("Recommendation similar source type replaces queue with continuation and no checkpoint", Similar);
        yield return new("Recommendation similar continuation retains artist seed and excludes the actual admitted queue", SimilarContinuation);
        yield return new("Recommendation heartbeat has no invented continuation and loop remains independent", Heartbeat);
        yield return new("Recommendation heartbeat over one hundred retains real seed and original checkpoint", HeartbeatComplete);
        yield return new("Recommendation heartbeat favorites uses resolved context and defaults continuation off", HeartbeatFavorites);
        yield return new("Recommendation heartbeat current rejects stale recording before provider read", HeartbeatStale);
        yield return new("Recommendation heartbeat current preserves actual repeated recording occurrence", HeartbeatOccurrence);
        yield return new("Recommendation heartbeat returns ABC from B as ABEC without a media selection", () => HeartbeatReturn(true));
        yield return new("Recommendation heartbeat return preserves paused intent and retires feedback proof", () => HeartbeatReturn(false));
        yield return new("Recommendation heartbeat return preserves different occurrences of the same recording", () => HeartbeatReturn(true, true));
        yield return new("Recommendation heartbeat failed return preserves media and can retry after reconcile", () => HeartbeatReturnUnknown(false));
        yield return new("Recommendation heartbeat lost return ACK reconciles ABEC without media selection or another write", () => HeartbeatReturnUnknown(true));
        yield return new("Recommendation heartbeat return waiting for lane cannot restore over an ordinary replacement", HeartbeatReturnStale);
        foreach (string queue in new[] { "empty", "different-wy", "non-wy" })
        {
            string original = queue;
            yield return new("Recommendation heartbeat browser seed accepts " + original + " queue and returns with current occurrence",
                () => HeartbeatBrowser(original));
        }
        yield return new("Recommendation heartbeat explicit seed rejects noncurrent duplicate occurrence", HeartbeatStaleOccurrence);
        foreach (string code in new[] { "NOT_FOUND", "AUTH_REQUIRED" })
        {
            string failure = code;
            yield return new("Recommendation heartbeat " + failure + " preserves original queue", () => HeartbeatFailure(failure));
        }
        yield return new("Recommendation queue preview preserves complete queue source and current occurrence", QueuePreview);
        yield return new("Recommendation queue keeps duplicate occurrences while all pages use the fixed seed", QueueRepeatedOccurrences);
        yield return new("Recommendation queue final preview is consumed once by continuation without media restart", QueueFinalPreview);
        yield return new("Recommendation queue final preview cancellation before CAS keeps batch available", QueueFinalPreviewCancel);
        yield return new("Recommendation queue disabling admitted final preview CAS retains songs without media restart", QueueFinalPreviewDisableCommit);
        yield return new("Recommendation queue play-next retains preview and has priority over recommendations", QueueFinalPreviewCas);
        yield return new("Recommendation queue choose appends complete batch and selects requested candidate once", QueueChoose);
        yield return new("Recommendation queue candidate tokens retire on retry but survive valid queue navigation", QueueCandidates);
        yield return new("Recommendation queue candidate commit checks actual queue version", QueueCandidateCas);
        yield return new("Recommendation queue actual tail fetches immediately in single loop without late selection", QueueTail);
        yield return new("Recommendation queue next during preview does not await HTTP", () => QueueInitialNext(false));
        yield return new("Recommendation queue real Ended during preview continues existing loop", () => QueueInitialNext(true));
        yield return new("Recommendation queue paused preview completion never autoplays", () => QueueInitialNext(true, pause: true));
        yield return new("Recommendation queue disabling retains uncommitted candidates and appended songs", QueueDisable);
        yield return new("Recommendation queue rapid off on retains one tail flight without blocking next", QueueResumeNext);
        yield return new("Recommendation queue off on and play-next keep valid flight and explicit priority", QueueResumePendingNext);
        yield return new("Recommendation queue disabling after CAS admission preserves committed facts without autoplay", QueueDisableCommit);
        foreach (string terminal in new[] { "empty", "duplicate", "has-more-false" })
        {
            string outcome = terminal;
            yield return new("Recommendation queue " + outcome + " terminates automatic chain", () => QueueTerminal(outcome));
        }
        yield return new("Recommendation queue failed automatic batch can explicitly retry", QueueRetry);
        yield return new("Recommendation queue unknown choose reconciles without duplicate append or autoplay", QueueUnknown);
        yield return new("Recommendation queue replacement and account invalidation reject late preview", QueueRetired);
        yield return new("Recommendation queue invalidation retires proof without stopping original playback", QueueOriginalPlayback);
        yield return new("Recommendation queue non WY current never dispatches recommendation provider", QueueUnsupportedCurrent);
        foreach (int index in new[] { 0, 1, 2 })
        {
            int current = index;
            yield return new("Recommendation queue mixed platform admission checks only fixed seed at index " + current,
                () => QueueMixed(current));
        }
        yield return new("Recommendation queue admission uses the current seed independently of later entries", QueueMixedPaged);
        yield return new("Recommendation continuation keeps its fixed seed after a foreign recording is appended", QueueMixedContinuation);
        yield return new("Recommendation active edit retains fixed seed and valid in-flight response", MiddleEdit);
        yield return new("Recommendation external selection preserves valid late append without selecting it", SelectionFence);
        yield return new("Recommendation return to empty checkpoint retains current occurrence", ReturnEmpty);
        yield return new("Recommendation active recommendation return retains current and latest loop", ReturnPaused);
        yield return new("Recommendation direct catalog commit wrapper retires session", CatalogCommit);
        yield return new("Recommendation account invalidation stops only live owned playback", Account);
        yield return new("Recommendation cancellation physically drains and reopening never revives", CancelClose);
        yield return new("Recommendation identical in-flight start reuses one physical request and session", DuplicateStart);
        yield return new("Recommendation new start waits old physical read no overlap", Replacement);
        yield return new("Recommendation commit unknown only explicit reconcile no duplicate write", Unknown);
        yield return new("Recommendation cancelled reconciliation keeps its result replayable until the next commit", ReconcileCancellation);
        yield return new("Recommendation preview malformed identity and unsupported platform do not replace", Invalid);
        yield return new("Recommendation observers repeating cancel produce no notification loop", ObserverCancel);
        yield return new("Recommendation Changed synchronous dispose does not await itself", ObserverDispose);
        yield return new("Recommendation independent dispose waits active observer", ObserverBarrier);
        yield return new("Recommendation real native Catalog HTTP to Library and ephemeral feedback", NativeCatalog);
        yield return new("Recommendation real native Catalog read settles before retired session drains", NativeHttpDrain);
        yield return new("Recommendation initial FM request does not block ordinary next", InitialNext);
        yield return new("Recommendation return while seed remains current never restarts media", ReturnSeed);
        yield return new("Recommendation asynchronous selection pause invalidates autoplay admission", LateSelection);
        yield return new("Recommendation pending commit blocks close until explicit reconciliation", UnknownClose);
        yield return new("Recommendation unknown settled receipt fails ordinary close but permits explicit abort", UnknownAbort);
        yield return new("Recommendation replacement lost ACK remains addressable without an invented similar checkpoint", ReplacementUnknown);
        yield return new("Recommendation FM mode switch keeps live session and prior batch provenance", FmLineage);
        yield return new("Recommendation failed FM mode switch retains committed mode", FmSwitchFailure);
        yield return new("Recommendation overlapping FM switches retain last committed mode after failure", FmSwitchOverlap);
        yield return new("Recommendation same FM id reconciles retired switch ACK into current facts without autoplay", FmSwitchUnknown);
        yield return new("Recommendation FM selection during channel read fences late playback but retains valid batch", FmSwitchEdit);
        yield return new("Recommendation Dispose admission cannot be reversed by Resume", DisposalAdmission);
    }
    private static async Task RandomTrigger(bool take, bool tail)
    {
        int decisions = 0;
        await using var f = new Fixture(randomFetch: () => { decisions++; return take; });
        f.Catalog.Reply("fm", "10", "11", "12"); await f.Start("fm");
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 2 });
        var q = await f.Q(); string selected = tail ? S(q.GetProperty("entries")[2], "id") : Current(q);
        await f.Select(selected); f.Catalog.Reply("fm", "13");
        await f.Rec.OnPlaybackAsync(new(selected, true, true, true, PlaybackEvent: 1));
        await f.Rec.OnPlaybackAsync(new(selected, false, true, true, PlaybackEvent: 1));
        await f.Rec.OnPlaybackAsync(new(selected, true, true, true, PlaybackEvent: 1));
        Check(decisions == 1 && f.Catalog.Calls.Count == (take ? 2 : 1) && Current(await f.Q()) == selected);
        Check(!await f.Rec.TryAdvanceAsync(selected, true, true));
        var next = await f.Port.ExecuteAsync("music.queue.next", new { reason = "ended" });
        Check(next.GetProperty("targetId").ValueKind == JsonValueKind.String);
        await f.Wait(x => S(x, "currentId") == S(next, "targetId"));
        f.Catalog.Reply("fm", "14");
        await f.Rec.OnPlaybackAsync(new(S(next, "targetId"), true, true, true, PlaybackEvent: 2));
        Check(decisions == 2);
    }
    private static async Task SearchAppendBoundary(bool active, bool next)
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var start = active ? await f.ActivateQueue() : await f.Start("queue");
        await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next });
        var state = await f.State(); Check(state.GetProperty("active").GetBoolean() == (active || next));
        if (active || next) Check(S(state, "seedSongId") == "1");
    }
    private static async Task QueueSelectionActivation(bool enter)
    {
        await using var f = new Fixture(randomFetch: () => false);
        await Ok(f.Library, "music.queue.replace", new { songs = new[] { Song("1") }, index = 0,
            context = new { sourceType = "search", searchKeyword = "原搜索", queueSource =
                new { id = "search", title = "原搜索", kind = "search", platform = "wy" } } });
        var original = await f.Q(); string seed = Current(original);
        f.Catalog.Reply("queue-recommendations", "2", "3");
        var preview = await f.Start("queue");
        var added = await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next = true });
        string inserted = added.GetProperty("entryIds")[0].GetString()!;
        // The user's explicit priority is consumed before candidates can enter.
        var next = await f.Port.ExecuteAsync("music.queue.next");
        Check(S(next, "targetId") == inserted);
        await f.Wait(state => S(state, "currentId") == inserted);
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 2 });
        Check(!await f.Rec.TryAdvanceAsync(inserted, true, true));
        var queued = await f.Q();
        Check(N(queued, "total") == 4 && Current(queued) == inserted && f.Selections.IsEmpty);
        Check(!(await f.State()).GetProperty("started").GetBoolean() &&
            queued.GetProperty("source").GetRawText() == original.GetProperty("source").GetRawText(),
            "append without choosing a candidate cannot change the search source");
        // These are controlled outcomes of choosing an existing shuffle item,
        // not probabilistic assertions about Random.Shared.
        await f.Select(seed);
        await f.Rec.OnPlaybackAsync(new(seed, true, true, true, PlaybackEvent: 1));
        await f.Select(inserted);
        await f.Rec.OnPlaybackAsync(new(inserted, true, true, true, PlaybackEvent: 2));
        Check(!(await f.State()).GetProperty("started").GetBoolean() && f.Catalog.Calls.Count == 1);
        if (enter)
        {
            string recommendation = S(queued.GetProperty("entries")[3], "id");
            await f.Port.ExecuteAsync("music.queue.select", new { id = recommendation });
            // Do not wait for an asynchronous display refresh before this append.
            await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("8") } });
            var state = await f.State(); var actual = await f.Q();
            Check(state.GetProperty("active").GetBoolean() && state.GetProperty("started").GetBoolean() &&
                S(state, "seedSongId") == "1" && Current(actual) == recommendation);
            Check(S(actual.GetProperty("source"), "kind") == "queue" &&
                S(actual.GetProperty("source"), "id") == S(preview, "id"));
        }
        else
        {
            await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("8") } });
            Check(!(await f.State()).GetProperty("active").GetBoolean() &&
                (await f.Q()).GetProperty("source").GetRawText() == original.GetProperty("source").GetRawText(),
                "ordinary append still cancels before any recommendation occurrence was selected");
        }
        Check(f.Catalog.Calls.Count == 1 && f.Selections.IsEmpty);
    }
    private static async Task SearchInFlight(bool next)
    {
        await using var f = new Fixture(); await f.Replace("1");
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var start = f.Start("queue"); await hold.Entered.Task;
        var appended = await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next });
        string inserted = appended.GetProperty("entryIds")[0].GetString()!;
        if (!next) await hold.Cancelled.Task;
        else
        {
            foreach (int mode in new[] { 3, 2, 1 }) await f.Port.ExecuteAsync("music.queue.mode", new { mode });
            await f.Select(inserted);
            Check(!hold.Cancelled.Task.IsCompleted);
        }
        hold.Complete(QueueBatch(true, "2", "3")); await start;
        var state = await f.State(); var q = await f.Q();
        Check(N(q, "total") == 2 && f.Selections.IsEmpty && S(state, "seedSongId") == "1");
        Check(state.GetProperty("active").GetBoolean() == next && state.GetProperty("candidates").GetArrayLength() == (next ? 2 : 0));
        if (next)
        {
            Check(Current(q) == inserted && !await f.Rec.TryAdvanceAsync(inserted, false, true));
            Check(N(await f.Q(), "total") == 4 && Current(await f.Q()) == inserted &&
                f.Catalog.Calls.Single().QueueRecommendations is { SeedSongId: "1", Offset: 0 });
        }
    }
    private static async Task LoopTail(int mode)
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11", "12"); await f.Start("fm");
        await f.Port.ExecuteAsync("music.queue.mode", new { mode });
        var q = await f.Q(); string first = Current(q), tail = S(q.GetProperty("entries")[2], "id");
        await f.Select(first); await f.Rec.OnPlaybackAsync(new(first, true, true, true, PlaybackEvent: 1));
        Check(f.Catalog.Calls.Count == 1);
        await f.Select(tail); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var pending = f.Rec.OnPlaybackAsync(new(tail, true, true, true, PlaybackEvent: 2)); await hold.Entered.Task;
        Check(!await f.Rec.TryAdvanceAsync(tail, true, true));
        hold.Complete("fm", "13", "14"); await pending;
        Check(N(await f.Q(), "total") == 5 && Current(await f.Q()) == tail && f.Selections.Count == 1);
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true, PlaybackEvent: 3));
        Check(f.Catalog.Calls.Count == 2 && N(await f.Q(), "mode") == mode);
    }
    private static async Task SearchInitialRetry()
    {
        await using var f = new Fixture(); await f.Replace("1");
        f.Catalog.Handle = (_, _) => throw new MusicPlatformException("NETWORK");
        var failed = await f.Start("queue");
        Check(S(failed, "error") == "NETWORK" && failed.GetProperty("canRetry").GetBoolean());
        var appended = await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next = true });
        string inserted = appended.GetProperty("entryIds")[0].GetString()!;
        await f.Select(inserted);
        f.Catalog.Reply("queue-recommendations", "2", "3");
        var retry = await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(failed, "id") });
        Check(S(retry, "error") == "" && S(retry, "seedSongId") == "1" &&
            retry.GetProperty("candidates").GetArrayLength() == 2);
        Check(f.Catalog.Calls.Count == 2 && f.Catalog.Calls.All(request =>
            request.QueueRecommendations is { SeedSongId: "1", Offset: 0 }));
        Check(Current(await f.Q()) == inserted && N(await f.Q(), "total") == 2 && f.Selections.IsEmpty);
    }
    private static async Task StagedSourceFailure()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11"); var fm = await f.Start("fm");
        string current = Current(await f.Q()); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var switching = f.Start("similar"); await hold.Entered.Task;
        var pending = await f.State();
        Check(S(pending, "kind") == "fm" && S(pending, "mode") == "default" && f.Rec.GetFeedback(current) is { Kind: "fm" });
        hold.Complete("similar"); var failed = await switching;
        Check(S(failed, "id") == S(fm, "id") && S(failed, "kind") == "fm" && N(await f.Q(), "total") == 2 && f.Selections.Count == 1);
        var held = new Hold(); f.Catalog.Handle = held.Read; switching = f.Start("similar"); await held.Entered.Task;
        f.Rec.Invalidate("wy"); held.Complete("similar", "99"); await switching; await f.Rec.PrepareCloseAsync();
        Check(f.Selections.Any(selection => selection.Action == MusicRecommendationPlaybackAction.Stop && selection.EntryId == current));
        Check(!(await f.State()).GetProperty("active").GetBoolean());
    }
    private static async Task FixedSeedPages()
    {
        await using var f = new Fixture(); await f.Replace("1");
        f.Catalog.Handle = (_, _) => Task.FromResult(QueuePage(17, true, "2"));
        await f.ActivateQueue(); string current = Current(await f.Q());
        await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next = true });
        await f.Select(S((await f.Q()).GetProperty("entries")[2], "id"));
        f.Catalog.Handle = (_, _) => Task.FromResult(QueuePage(41, true, "3"));
        await f.Rec.OnPlaybackAsync(new(Current(await f.Q()), true, true, true));
        Check(f.Catalog.Calls.Last().QueueRecommendations is { SeedSongId: "1", Offset: 17 } && S(await f.State(), "seedSongId") == "1");
        await f.Select(S((await f.Q()).GetProperty("entries")[3], "id"));
        f.Catalog.Handle = (_, _) => Task.FromResult(QueuePage(58, false, "4"));
        await f.Rec.OnPlaybackAsync(new(Current(await f.Q()), true, true, true));
        Check(f.Catalog.Calls.Last().QueueRecommendations is { SeedSongId: "1", Offset: 41 } &&
            (await f.State()).GetProperty("exhausted").GetBoolean() && N(await f.Q(), "total") == 5);
    }
    private static async Task First()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        var read = new Hold(); f.Catalog.Handle = read.Read;
        var start = f.Start("fm"); await read.Entered.Task;
        Check((await f.Q()).GetProperty("total").GetInt32() == 2 && f.Selections.IsEmpty);
        read.Complete("fm", "10", "11", "12");
        var state = await start; Check(S(state, "status") == "ready" && N(state, "batch") == 1);
        Check((await f.Q()).GetProperty("total").GetInt32() == 3 && f.Selections.Count == 1);
        Check(f.Selections.Single().IsCurrent());
    }
    private static async Task InitialFailure()
    {
        await using var f = new Fixture(); await f.Replace("1");
        var before = (await f.Q()).GetProperty("entries").GetRawText(); int writes = f.Store.Writes;
        f.Catalog.Handle = (_, _) => throw new MusicPlatformException("NETWORK");
        var state = await f.Start("fm");
        Check(S(state, "error") == "NETWORK" && f.Store.Writes == writes && f.Selections.IsEmpty);
        Check((await f.Q()).GetProperty("entries").GetRawText() == before);
    }
    private static async Task Switch()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11");
        var first = await f.Start("fm"); string entry = Current(await f.Q());
        f.Catalog.Reply("fm", "12", "13");
        var switched = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        Check(N(switched, "count") == 4 && Current(await f.Q()) != entry);
        Check(S(first, "mode") == "default" && S(switched, "mode") == "explore" && S(switched, "submode") == "");
        Check((await f.Q()).GetProperty("entries")[0].GetProperty("id").GetString() == entry);
        Check(f.Rec.GetFeedback(Current(await f.Q())) is { Mode: "explore", Provider: "fm" });
        int reads = f.Catalog.Calls.Count;
        await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        Check(f.Catalog.Calls.Count == reads && S(first, "id") == S(switched, "id"));
    }
    private static async Task FmSwitchRepeated(bool allRepeated)
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "101", "102");
        var first = await f.Start("fm"); var before = await f.Q();
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 3 });
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id = S(first, "id"), enabled = false });
        f.Catalog.Reply("fm", allRepeated ? ["102", "101"] : ["102", "103"]);
        var switched = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "fm", platform = "wy", mode = "explore" });
        var after = await f.Q(); var rows = after.GetProperty("entries");
        Check(switched.GetProperty("active").GetBoolean() && S(switched, "error") == "" && N(after, "total") == 4);
        Check(rows.EnumerateArray().Select(row => SongId(row.GetProperty("song")))
            .SequenceEqual(allRepeated ? ["101", "102", "102", "101"] : ["101", "102", "102", "103"]));
        Check(rows[0].GetRawText() == before.GetProperty("entries")[0].GetRawText() &&
            rows[1].GetRawText() == before.GetProperty("entries")[1].GetRawText());
        Check(Current(after) == S(rows[2], "id") && S(rows[1], "id") != S(rows[2], "id") &&
            f.Selections.Last().EntryId == S(rows[2], "id"), "the channel's real first recording gets a new selected occurrence");
        Check(N(after, "mode") == 3 && !switched.GetProperty("continue").GetBoolean() &&
            S(switched, "mode") == "explore" && S(switched, "id") == S(first, "id"));
        f.Catalog.Reply("fm", "102", "104");
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id = S(first, "id"), enabled = true });
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(first, "id") });
        var continued = await f.Q();
        Check(N(continued, "total") == 5 && Current(continued) == Current(after) && f.Selections.Count == 2 &&
            SongId(continued.GetProperty("entries")[4].GetProperty("song")) == "104",
            "normal continuation after the channel switch still removes already queued recordings");
    }
    private static async Task Identity()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11");
        var state = await f.Start("fm");
        f.Catalog.Reply("fm", "10", "12");
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(state, "id") });
        var q = await f.Q(); Check(N(q, "total") == 3);
        Check(q.GetProperty("entries").EnumerateArray().Select(e => SongId(e.GetProperty("song"))).SequenceEqual(["10", "11", "12"]));
    }
    private static async Task FmCommittedModes()
    {
        await using var f = new Fixture(); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var starting = f.Start("fm"); await hold.Entered.Task;
        Check(S(await f.State(), "mode") == "" && S(await f.State(), "submode") == "");
        hold.Complete("fm", "10", "11"); var first = await starting;
        int next = 12;
        foreach (var choice in new (string Mode, string Submode)[] { ("familiar", ""), ("explore", ""), ("puzzle", ""), ("scene", "HOT_SONG") })
        {
            f.Catalog.Reply("fm", (next++).ToString(), (next++).ToString());
            var state = await f.Rec.ExecuteAsync("music.recommendations.start",
                new { kind = "fm", platform = "wy", mode = choice.Mode, submode = choice.Submode });
            Check(S(state, "id") == S(first, "id") && S(state, "mode") == choice.Mode && S(state, "submode") == choice.Submode);
            Check(f.Rec.GetFeedback(Current(await f.Q())) is { } proof &&
                proof.Mode == choice.Mode && proof.Submode == choice.Submode);
        }
    }
    private static async Task Rule()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11", "12", "13", "14");
        var state = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", ruleId = "123" });
        Check(N(state, "count") == 5);
        Check(S(state, "ruleId") == "123" && S(state, "tabTagId") == "");
        var q = await f.Q(); var feedback = f.Rec.GetFeedback(Current(q))!;
        Check(feedback.Provider == "rule" && feedback.SourceId == "123" && feedback.Algorithm == "alg-10");
        Check(!q.GetProperty("entries")[0].GetProperty("context").TryGetProperty("fm", out _));
        Check(S(q.GetProperty("entries")[0].GetProperty("context"), "sourceType") == "fm");
        Check(S(q.GetProperty("entries")[0].GetProperty("context"), "sourceId") == S(state, "id"),
            "FM entries carry their real session identity, not an empty song seed");
    }
    private static async Task TailPause()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10");
        await f.Start("fm"); string current = Current(await f.Q());
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var observe = f.Rec.OnPlaybackAsync(new(current, true, true, true)); await hold.Entered.Task;
        Check(!await f.Rec.TryAdvanceAsync(current, true, true), "pending HTTP must leave ordinary looping available");
        await f.Rec.OnPlaybackAsync(new(current, false, true, true));
        Check(f.Catalog.Calls.Count == 2);
        hold.Complete("fm", "11"); await observe;
        Check(Current(await f.Q()) == current && N(await f.Q(), "total") == 2 && f.Selections.Count == 1);
        Check(!(await f.State()).GetProperty("play").GetBoolean());
    }
    private static async Task DuplicateEnded()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10"); await f.Start("fm");
        string current = Current(await f.Q()); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var fetch = f.Rec.OnPlaybackAsync(new(current, true, true, true)); await hold.Entered.Task;
        Check(!await f.Rec.TryAdvanceAsync(current, true, true));
        Check(!await f.Rec.TryAdvanceAsync(current, true, true));
        Check(f.Catalog.Calls.Count == 2);
        hold.Complete("fm", "11", "12"); await fetch;
        Check(f.Selections.Count == 1 && Current(await f.Q()) == current && N(await f.Q(), "total") == 3);
    }
    private static async Task Backoff()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10"); var start = await f.Start("fm");
        string current = Current(await f.Q());
        f.Catalog.Handle = (_, _) => throw new MusicPlatformException("NETWORK");
        await f.Rec.OnPlaybackAsync(new(current, true, true, true)); Check(f.Catalog.Calls.Count == 2);
        f.Clock.Advance(100);
        await f.Rec.OnPlaybackAsync(new(current, true, true, true));
        await f.Rec.OnPlaybackAsync(new(current, false, true, true));
        await f.Rec.OnPlaybackAsync(new(current, true, true, true));
        Check(f.Catalog.Calls.Count == 2 && !await f.Rec.TryAdvanceAsync(current, true, true));
        f.Catalog.Reply("fm", "11");
        await f.Rec.OnPlaybackAsync(new(current, false, true, true));
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(start, "id") });
        Check(f.Catalog.Calls.Count == 3 && f.Selections.Count == 1 && !(await f.State()).GetProperty("play").GetBoolean());
    }
    private static async Task Similar()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 3 });
        f.Catalog.Reply("similar", "10", "11", "12");
        var state = await f.Start("similar");
        Check(N(state, "count") == 3 && state.GetProperty("continue").GetBoolean() && !state.GetProperty("canReturn").GetBoolean());
        Check(N(await f.Q(), "mode") == 3 && SongId((await f.Q()).GetProperty("current").GetProperty("song")) == "10");
        Check(f.Catalog.Calls.Single().Similar is { SourceType: "song" });
        f.Catalog.Reply("similar", "20", "21");
        var artist = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "similar", platform = "wy", sourceIds = new[] { "2" }, sourceType = "artist" });
        Check(N(await f.Q(), "total") == 2 && S(artist, "sourceType") == "artist" && N(await f.Q(), "mode") == 3);
        Check(f.Catalog.Calls.Last().Similar is { SourceType: "artist" } && f.Selections.Count == 2);
        var back = await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(artist, "id") });
        Check(S(back, "code") == "CHECKPOINT_NOT_FOUND");
    }
    private static async Task SimilarContinuation()
    {
        await using var f = new Fixture();
        f.Catalog.Reply("similar", "10", "11", "12");
        await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "similar", platform = "wy", sourceIds = new[] { "7" }, sourceType = "artist" });
        var initial = f.Catalog.Calls.Single().Similar!;
        Check(initial.SourceType == "artist" && initial.SourceIds.SequenceEqual(["7"]) && initial.UnplaySongIds!.Count == 0);
        var before = await f.Q(); string tail = S(before.GetProperty("entries")[2], "id");
        await f.Select(tail);
        f.Catalog.Reply("similar", "13", "14");
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true, PlaybackEvent: 1));
        var continued = f.Catalog.Calls.Last().Similar!;
        Check(f.Catalog.Calls.Count == 2 && continued.SourceType == "artist" && continued.SourceIds.SequenceEqual(["7"]) &&
            continued.UnplaySongIds!.SequenceEqual(["10", "11", "12"]));
        Check(N(await f.Q(), "total") == 5 && Current(await f.Q()) == tail && f.Selections.Count == 1);
    }
    private static async Task Heartbeat()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        f.Catalog.Reply("heartbeat", "1", "10", "11", "12", "13", "14");
        var state = await f.Heartbeat();
        Check(N(await f.Q(), "total") == 6 && f.Selections.IsEmpty && !state.GetProperty("continue").GetBoolean());
        Check(f.Catalog.Calls.Single().Heartbeat is { Entry: MusicHeartbeatEntry.CurrentSong, SongId: "1", PlaylistId: "100" });
        string tail = S((await f.Q()).GetProperty("entries")[5], "id");
        await f.Port.ExecuteAsync("music.queue.select", new { id = tail }); await f.Wait(x => S(x, "currentId") == tail);
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true));
        var unsupported = await f.Rec.ExecuteAsync("music.recommendations.continue", new { id = S(state, "id"), enabled = true });
        Check(S(unsupported, "code") == "UNSUPPORTED" && f.Catalog.Calls.Count == 1);
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 2 });
        Check((await f.State()).GetProperty("active").GetBoolean() && N(await f.Q(), "mode") == 2);
        Check(!await f.Rec.TryAdvanceAsync(tail, true, true));
    }
    private static async Task HeartbeatComplete()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        var before = await f.Q(); string seed = Current(before);
        f.Catalog.Reply("heartbeat", Enumerable.Range(10, 120).Select(i => i.ToString()).ToArray());
        var state = await f.Heartbeat();
        Check(N(await f.Q(), "total") == 121 && Current(await f.Q()) == seed && f.Selections.IsEmpty);
        var last = await Ok(f.Library, "music.queue.get", new { offset = 120, limit = 100 });
        Check(SongId(last.GetProperty("entries")[0].GetProperty("song")) == "129");
        Check(f.Rec.GetFeedback(S(last.GetProperty("entries")[0], "id")) is { FeedbackSource: "", SourceId: "100" });
        await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        Check((await f.Q()).GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText() &&
            Current(await f.Q()) == seed);
    }
    private static async Task HeartbeatFavorites()
    {
        await using var f = new Fixture(); await f.Replace("1", "2"); var before = await f.Q();
        f.Catalog.Handle = (_, _) => Task.FromResult(HeartbeatBatch(true, "10", "11", "12", "13"));
        var state = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "heartbeat", platform = "wy", entry = "favorites" });
        Check(f.Catalog.Calls.Single().Heartbeat is { Entry: MusicHeartbeatEntry.Favorites, SongId: null });
        Check(N(await f.Q(), "total") == 4 && Current(await f.Q()) != Current(before) && !state.GetProperty("continue").GetBoolean());
        Check(f.Rec.GetFeedback(Current(await f.Q())) is
            { FromHome: true, FeedbackSource: "", SourceId: "100", Algorithm: "alg-10" });
        var recommendation = await f.Q(); int selections = f.Selections.Count;
        await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        var restored = await f.Q();
        Check(restored.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText()).SequenceEqual(
            new[] { before.GetProperty("entries")[0], recommendation.GetProperty("current"), before.GetProperty("entries")[1] }
                .Select(e => e.GetRawText())));
        Check(Current(restored) == Current(recommendation) && f.Selections.Count == selections &&
            f.Rec.GetFeedback(Current(restored)) is null);
    }
    private static async Task HeartbeatStale()
    {
        await using var f = new Fixture(); await f.Replace("2");
        var before = await f.Q(); int writes = f.Store.Writes;
        var state = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "heartbeat", platform = "wy", entry = "current", songId = "1", playlistId = "100", seedEntryId = Current(before) });
        Check(S(state, "error") == "STALE_QUEUE" && f.Catalog.Calls.IsEmpty && f.Store.Writes == writes);
        Check(Current(await f.Q()) == Current(before));
        var old = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "heartbeat", platform = "wy", playlistId = "100", startMusicId = "1", songId = "2" });
        Check(!old.GetProperty("ok").GetBoolean() && S(old, "code") == "INVALID_REQUEST");
    }
    private static async Task HeartbeatOccurrence()
    {
        await using var f = new Fixture(); await f.Replace("1", "1", "2");
        string seed = S((await f.Q()).GetProperty("entries")[1], "id");
        await f.Port.ExecuteAsync("music.queue.select", new { id = seed }); var before = await f.Q();
        f.Catalog.Reply("heartbeat", "1", "10", "11", "12", "13");
        var state = await f.Heartbeat(); var after = await f.Q();
        Check(Current(after) == seed && N(after, "total") == 5 && f.Selections.IsEmpty);
        Check(after.GetProperty("entries")[0].GetRawText() == before.GetProperty("entries")[1].GetRawText() &&
            f.Rec.GetFeedback(seed) is null);
        await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        Check(Current(await f.Q()) == seed && (await f.Q()).GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText());
    }
    private static async Task HeartbeatBrowser(string original)
    {
        await using var f = new Fixture();
        if (original == "different-wy") await f.Replace("2", "3");
        else if (original == "non-wy")
            await Ok(f.Library, "music.queue.replace", new { songs = new[] {
                MusicSongFixture.Element(MusicSongFixture.Online("tx", "qq123")) }, index = 0 });
        var before = await f.Q();
        f.Catalog.Handle = (_, _) => Task.FromResult(HeartbeatBatch(true, "10", "11", "12", "13"));
        var state = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "heartbeat", platform = "wy", entry = "favorites", songId = "10" }); var after = await f.Q();
        Check(S(state, "status") == "ready" && S(state, "error") == "" &&
            f.Catalog.Calls.Single().Heartbeat is { Entry: MusicHeartbeatEntry.Favorites, SongId: "10" });
        Check(N(after, "total") == 4 && SongId(after.GetProperty("current").GetProperty("song")) == "10" &&
            Current(after) != Current(before) && !state.GetProperty("continue").GetBoolean());
        Check(f.Selections.Single() is { Play: true, Automatic: false } selection && selection.EntryId == Current(after) &&
            f.Rec.GetFeedback(Current(after)) is { FromHome: true, FeedbackSource: "", SourceId: "100" });
        var returned = await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        var restored = await f.Q();
        JsonElement[] expected = original switch
        {
            "empty" => [after.GetProperty("current")],
            "different-wy" => [before.GetProperty("entries")[0], after.GetProperty("current"), before.GetProperty("entries")[1]],
            _ => [before.GetProperty("entries")[0], after.GetProperty("current")]
        };
        Check(S(returned, "status") == "returned" && Current(restored) == Current(after) &&
            restored.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText()).SequenceEqual(expected.Select(e => e.GetRawText())) &&
            restored.GetProperty("source").GetRawText() == before.GetProperty("source").GetRawText());
        Check(f.Selections.Count == 1 && f.Rec.GetFeedback(Current(restored)) is null);
    }
    private static async Task HeartbeatReturn(bool playing, bool duplicate = false)
    {
        await using var f = new Fixture();
        await Ok(f.Library, "music.queue.replace", new { songs = new[] { Song("2"), Song("1"), Song(duplicate ? "11" : "3") },
            index = 1, context = new { sourceTitle = "Original", sourceType = "search", searchKeyword = "needle",
                queueSource = new { id = "source", title = "Original", kind = "search", platform = "wy" } } });
        var before = await f.Q();
        f.Catalog.Reply("heartbeat", "10", "11", "12"); var state = await f.Heartbeat();
        string current = S((await f.Q()).GetProperty("entries")[2], "id");
        await f.Port.ExecuteAsync("music.queue.select", new { id = current });
        await f.Wait(s => S(s, "currentId") == current);
        await f.Rec.OnPlaybackAsync(new(current, playing, true, true));
        var recommendation = await f.Q(); int writes = f.Store.Writes;
        Check(f.Rec.GetFeedback(current) is { Kind: "heartbeat" });
        var returned = await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        var restored = await f.Q();
        Check(S(returned, "status") == "returned" && !returned.GetProperty("active").GetBoolean() &&
            !returned.GetProperty("canReturn").GetBoolean() && returned.GetProperty("play").GetBoolean() == playing);
        Check(Current(restored) == current && S(returned, "currentId") == current &&
            N(returned, "queueVersion") == N(restored, "version") && N(returned, "count") == 4);
        Check(restored.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText()).SequenceEqual(
            new[] { before.GetProperty("entries")[0], before.GetProperty("entries")[1],
                recommendation.GetProperty("current"), before.GetProperty("entries")[2] }.Select(e => e.GetRawText())));
        Check(restored.GetProperty("source").GetRawText() == before.GetProperty("source").GetRawText() &&
            f.Store.Writes == writes + 1 && f.Selections.IsEmpty);
        Check(recommendation.GetProperty("entries").EnumerateArray().All(e => f.Rec.GetFeedback(S(e, "id")) is null));
    }
    private static async Task HeartbeatReturnUnknown(bool applied)
    {
        await using var f = new Fixture();
        await Ok(f.Library, "music.queue.replace", new { songs = new[] { Song("2"), Song("1"), Song("3") }, index = 1 });
        var before = await f.Q();
        f.Catalog.Reply("heartbeat", "10", "11", "12"); var state = await f.Heartbeat(true);
        string current = S((await f.Q()).GetProperty("entries")[2], "id");
        await f.Port.ExecuteAsync("music.queue.select", new { id = current });
        await f.Wait(s => S(s, "currentId") == current);
        await f.Rec.OnPlaybackAsync(new(current, true, true, true));
        var recommendation = await f.Q();
        var proof = f.Rec.GetFeedback(current); Check(proof is not null);
        int writes = f.Store.Writes; f.Store.Failure = applied ? 2 : 1;
        var pending = await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        Check(S(pending, "status") == "commit-unknown" && pending.GetProperty("requiresReconcile").GetBoolean() &&
            pending.GetProperty("active").GetBoolean() && pending.GetProperty("play").GetBoolean());
        Check(f.Store.Writes == writes + 1 && f.Selections.IsEmpty && f.Rec.GetFeedback(current) == proof);
        var blocked = await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        Check(!blocked.GetProperty("ok").GetBoolean() && S(blocked, "code") == "COMMIT_UNKNOWN" && f.Store.Writes == writes + 1);
        var settled = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id = S(state, "id") });
        Check(!settled.GetProperty("requiresReconcile").GetBoolean() && settled.GetProperty("play").GetBoolean() &&
            f.Store.Writes == writes + 1 && f.Selections.IsEmpty);
        if (!applied)
        {
            Check(S(settled, "status") == "not-applied" && settled.GetProperty("canReturn").GetBoolean() &&
                f.Rec.GetFeedback(current) == proof && Current(await f.Q()) == current &&
                (await f.Q()).GetProperty("entries").GetRawText() == recommendation.GetProperty("entries").GetRawText());
            settled = await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        }
        var restored = await f.Q();
        Check(S(settled, "status") == "returned" && !settled.GetProperty("active").GetBoolean() &&
            !settled.GetProperty("continue").GetBoolean()  &&
            Current(restored) == current && f.Rec.GetFeedback(current) is null && f.Selections.IsEmpty);
        Check(restored.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText()).SequenceEqual(
            new[] { before.GetProperty("entries")[0], before.GetProperty("entries")[1],
                recommendation.GetProperty("current"), before.GetProperty("entries")[2] }.Select(e => e.GetRawText())));
        Check(f.Store.Writes == writes + (applied ? 1 : 2));
    }
    private static async Task HeartbeatReturnStale()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        f.Catalog.Reply("heartbeat", "10", "11"); var state = await f.Heartbeat();
        var entered = NewSignal(); var release = NewSignal();
        var lane = f.Port.InLaneAsync(async (_, _) => { entered.TrySetResult(); await release.Task; return true; }, CancellationToken.None);
        await entered.Task;
        int writes = f.Store.Writes;
        var returning = f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        var replacement = f.Port.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("99") }, index = 0 });
        release.TrySetResult();
        await lane; await returning; await replacement; await f.Rec.PrepareCloseAsync();
        var actual = await f.Q();
        Check(N(actual, "total") == 1 && SongId(actual.GetProperty("current").GetProperty("song")) == "99" &&
            f.Store.Writes == writes + 1 && f.Selections.IsEmpty);
    }
    private static async Task HeartbeatStaleOccurrence()
    {
        await using var f = new Fixture(); await f.Replace("1", "1");
        var before = await f.Q(); int writes = f.Store.Writes;
        var state = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "heartbeat", platform = "wy", entry = "current", songId = "1", playlistId = "100",
                seedEntryId = S(before.GetProperty("entries")[1], "id") });
        Check(S(state, "error") == "STALE_QUEUE" && f.Catalog.Calls.IsEmpty && f.Selections.IsEmpty &&
            f.Store.Writes == writes && Current(await f.Q()) == Current(before));
    }
    private static async Task HeartbeatFailure(string code)
    {
        await using var f = new Fixture(); await f.Replace("1", "2"); var before = await f.Q(); int writes = f.Store.Writes;
        f.Catalog.Handle = (_, _) => throw new MusicPlatformException(code);
        var state = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "heartbeat", platform = "wy", entry = "favorites" });
        Check(S(state, "error") == code && (code != "AUTH_REQUIRED" || !state.GetProperty("active").GetBoolean()));
        Check(f.Store.Writes == writes && f.Selections.IsEmpty && !state.GetProperty("canReturn").GetBoolean() &&
            (await f.Q()).GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText());
    }
    private static async Task QueuePreview()
    {
        await using var f = new Fixture();
        var replaced = await f.Library.ReplaceQueueAsync(Enumerable.Range(1, 1001).Select(i => Song(i.ToString())).ToArray(), 47,
            J(new { sourceTitle = "Original search", sourceType = "search", searchKeyword = "original query",
                queueSource = new { id = "original", title = "Original search", kind = "search", platform = "wy" } }), N(await f.Q(), "version"));
        Check(replaced.GetProperty("ok").GetBoolean());
        var before = await f.Q(); int writes = f.Store.Writes; f.Catalog.Reply("queue-recommendations", "1", "1002", "1003", "1004", "1005");
        var state = await f.Start("queue"); var after = await f.Q();
        Check(f.Store.Writes == writes && f.Selections.IsEmpty && N(after, "version") == N(before, "version"));
        Check(Current(after) == Current(before) && after.GetProperty("entries").GetRawText() == before.GetProperty("entries").GetRawText());
        Check(after.GetProperty("source").GetRawText() == before.GetProperty("source").GetRawText());
        Check(state.GetProperty("candidates").GetArrayLength() == 4 && state.GetProperty("continue").GetBoolean() &&
            !state.GetProperty("canReturn").GetBoolean() && !state.GetProperty("exhausted").GetBoolean());
        Check(S(state, "mode") == "" && f.Rec.GetFeedback(Current(after)) is null);
        var request = f.Catalog.Calls.Single().QueueRecommendations!;
        Check(request.SeedSongId == "48" && request.Offset == 0);
        Check(state.GetProperty("candidates").EnumerateArray().All(c => S(c, "id") != SongId(c.GetProperty("song"))));
    }
    private static async Task QueueChoose()
    {
        await using var f = new Fixture(); await f.Replace("1", "2"); var before = await f.Q();
        f.Catalog.Handle = (_, _) => Task.FromResult(QueueBatch(true, "1", "3", "4", "5"));
        var start = await f.Start("queue"); string candidate = S(start.GetProperty("candidates")[1], "id");
        int writes = f.Store.Writes;
        var chosen = await f.Rec.ExecuteAsync("music.recommendations.choose", new { id = S(start, "id"), candidateId = candidate });
        var q = await f.Q();
        Check(f.Store.Writes == writes + 1 && N(q, "total") == 5 && SongId(q.GetProperty("current").GetProperty("song")) == "4");
        Check(q.GetProperty("entries")[0].GetRawText() == before.GetProperty("entries")[0].GetRawText() &&
            q.GetProperty("entries")[1].GetRawText() == before.GetProperty("entries")[1].GetRawText());
        Check(f.Selections.Single() is { Play: true, Automatic: false } selection && selection.EntryId == Current(q) && selection.IsCurrent());
        Check(f.Rec.GetFeedback(Current(q)) is { Kind: "queue", Provider: "queue-recommendations", Algorithm: "alg-4", FeedbackSource: "feedback-4" });
        Check(f.Rec.GetFeedback(Current(before)) is null && chosen.GetProperty("candidates").GetArrayLength() == 0);
        var stale = await f.Rec.ExecuteAsync("music.recommendations.choose", new { id = S(start, "id"), candidateId = candidate });
        Check(S(stale, "code") == "STALE_CANDIDATE" && f.Store.Writes == writes + 1 && f.Selections.Count == 1);
    }
    private static async Task QueueRepeatedOccurrences()
    {
        await using var f = new Fixture(); await f.Replace("1", "2", "1");
        var before = await f.Q(); f.Catalog.Reply("queue-recommendations", "1", "2", "3", "4");
        var preview = await f.Start("queue");
        Check(f.Catalog.Calls.Single().QueueRecommendations is { SeedSongId: "1", Offset: 0 });
        Check(preview.GetProperty("candidates").GetArrayLength() == 2);
        string tail = S(before.GetProperty("entries")[2], "id");
        await f.Select(tail);
        Check(!await f.Rec.TryAdvanceAsync(tail, false, true));
        var appended = await f.Q();
        Check(N(appended, "total") == 5 && Current(appended) == tail && f.Selections.IsEmpty);
        Check(appended.GetProperty("entries").EnumerateArray().Take(3).Select(e => e.GetRawText())
            .SequenceEqual(before.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText())));
        await f.Select(S(appended.GetProperty("entries")[4], "id"));
        f.Catalog.Reply("queue-recommendations", "5", "6");
        await f.Rec.OnPlaybackAsync(new(Current(await f.Q()), true, true, true));
        Check(f.Catalog.Calls.Last().QueueRecommendations is { SeedSongId: "1", Offset: 15 });
    }
    private static async Task QueueFinalPreview()
    {
        await using var f = new Fixture(); await f.Replace("1"); var before = await f.Q();
        f.Catalog.Handle = (_, _) => Task.FromResult(QueueBatch(false, "2", "3"));
        var preview = await f.Start("queue"); string current = Current(before);
        Check(preview.GetProperty("continue").GetBoolean() && preview.GetProperty("candidates").GetArrayLength() == 2);
        await f.Rec.OnPlaybackAsync(new(current, true, true, true));
        Check(N(await f.Q(), "total") == 1 && f.Catalog.Calls.Count == 1);
        Check(!await f.Rec.TryAdvanceAsync(current, false, true));
        var after = await f.Q();
        Check(N(after, "total") == 3 && Current(after) == current && f.Selections.IsEmpty &&
            after.GetProperty("entries")[0].GetRawText() == before.GetProperty("entries")[0].GetRawText());
        Check((await f.State()).GetProperty("exhausted").GetBoolean());
        await f.Select(S(after.GetProperty("entries")[2], "id"));
        await f.Rec.OnPlaybackAsync(new(Current(await f.Q()), true, true, true));
        Check(f.Catalog.Calls.Count == 1 && !await f.Rec.TryAdvanceAsync(Current(await f.Q()), true, true));
    }
    private static async Task QueueFinalPreviewCancel()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2", "3");
        var preview = await f.Start("queue"); int writes = f.Store.Writes;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await f.Rec.ExecuteAsync("music.recommendations.choose",
            new { id = S(preview, "id"), candidateId = S(preview.GetProperty("candidates")[0], "id") }, cancel.Token);
        Check(S(result, "code") == "CANCELLED" && f.Store.Writes == writes &&
            (await f.State()).GetProperty("candidates").GetArrayLength() == 2 && f.Selections.IsEmpty);
    }
    private static async Task QueueFinalPreviewCas()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2", "3");
        var preview = await f.Start("queue"); string current = Current(await f.Q());
        var next = await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next = true });
        string inserted = next.GetProperty("entryIds")[0].GetString()!;
        Check(!await f.Rec.TryAdvanceAsync(current, false, true));
        var ordinary = await f.Port.ExecuteAsync("music.queue.next");
        Check(S(ordinary, "targetId") == inserted && N(await f.Q(), "total") == 2);
        await f.Wait(x => S(x, "currentId") == inserted);
        Check((await f.State()).GetProperty("candidates").GetArrayLength() == 2);
        Check(!await f.Rec.TryAdvanceAsync(inserted, false, true));
        Check(N(await f.Q(), "total") == 4 && Current(await f.Q()) == inserted && f.Catalog.Calls.Count == 1);
    }
    private static async Task QueueFinalPreviewDisableCommit()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Handle = (_, _) => Task.FromResult(QueueBatch(false, "2", "3"));
        var preview = await f.Start("queue"); string id = S(preview, "id"), current = Current(await f.Q());
        int writes = f.Store.Writes; f.Store.Entered = NewSignal(); f.Store.Release = NewSignal();
        var advance = f.Rec.TryAdvanceAsync(current, true, true); await f.Store.Entered.Task;
        var state = await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = false });
        Check(!state.GetProperty("continue").GetBoolean());
        f.Store.Release.TrySetResult(); Check(!await advance);
        Check(f.Store.Writes == writes + 1 && N(await f.Q(), "total") == 3 && Current(await f.Q()) == current &&
            (await f.State()).GetProperty("exhausted").GetBoolean() && f.Selections.IsEmpty);
    }
    private static async Task QueueCandidates()
    {
        await using var f = new Fixture(); await f.Replace("1", "2"); f.Catalog.Reply("queue-recommendations", "3", "4");
        var first = await f.Start("queue"); string id = S(first, "id"), token = S(first.GetProperty("candidates")[0], "id");
        var next = await f.Rec.ExecuteAsync("music.recommendations.retry", new { id });
        Check(S(next.GetProperty("candidates")[0], "id") != token);
        var stale = await f.Rec.ExecuteAsync("music.recommendations.choose", new { id, candidateId = token });
        Check(S(stale, "code") == "STALE_CANDIDATE");
        await f.Select(S((await f.Q()).GetProperty("entries")[1], "id"));
        var chosen = await f.Rec.ExecuteAsync("music.recommendations.choose",
            new { id, candidateId = S(next.GetProperty("candidates")[0], "id") });
        Check(S(chosen, "error") == "" && N(await f.Q(), "total") == 4 && f.Selections.Count == 1);
    }
    private static async Task QueueCandidateCas()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "3");
        var start = await f.Start("queue"); int writes = f.Store.Writes;
        f.Store.Failure = 1;
        var chosen = await f.Rec.ExecuteAsync("music.recommendations.choose",
            new { id = S(start, "id"), candidateId = S(start.GetProperty("candidates")[0], "id") });
        Check(chosen.GetProperty("requiresReconcile").GetBoolean() && f.Store.Writes == writes + 1);
        var settled = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id = S(start, "id") });
        Check(S(settled, "status") == "not-applied" && N(await f.Q(), "total") == 1 && f.Selections.IsEmpty);
    }
    private static async Task QueueTail()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var state = await f.ActivateQueue(); string tail = Current(await f.Q());
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 3 });
        await f.Select(tail);
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var observe = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await hold.Entered.Task;
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true));
        Check(!await f.Rec.TryAdvanceAsync(tail, true, true) && f.Catalog.Calls.Count == 2);
        await f.Rec.OnPlaybackAsync(new(tail, false, true, true));
        hold.Complete("queue-recommendations", "3", "4"); await observe;
        Check(N(await f.Q(), "total") == 4 && Current(await f.Q()) == tail && f.Selections.Count == 1);
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true, PlaybackEvent: 2));
        Check(f.Catalog.Calls.Count == 2 && f.Catalog.Calls.Last().QueueRecommendations is { SeedSongId: "1", Offset: 15 });
    }
    private static async Task QueueDisable()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var start = await f.ActivateQueue(); string id = S(start, "id"), tail = Current(await f.Q());
        var hold = new Hold(); f.Catalog.Handle = hold.Read; int writes = f.Store.Writes;
        var fetch = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await hold.Entered.Task;
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = false });
        hold.Complete("queue-recommendations", "3"); await fetch;
        Check(f.Store.Writes == writes && N(await f.Q(), "total") == 2 && Current(await f.Q()) == tail);
        Check((await f.State()).GetProperty("candidates").GetArrayLength() == 1);
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = true });
        Check(N(await f.Q(), "total") == 3 && f.Catalog.Calls.Count == 2 && f.Selections.Count == 1);
    }
    private static async Task QueueInitialNext(bool ended, bool pause = false)
    {
        await using var f = new Fixture(); await f.Replace("1");
        var hold = new Hold(); f.Catalog.Handle = hold.Read; var start = f.Start("queue"); await hold.Entered.Task;
        string tail = Current(await f.Q());
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true));
        if (pause) await f.Rec.OnPlaybackAsync(new(tail, false, true, true));
        Check(!await f.Rec.TryAdvanceAsync(tail, ended, true), "initial preview must not hold existing queue navigation");
        hold.Complete(QueueBatch(false, "2", "3")); var state = await start;
        Check(N(await f.Q(), "total") == 1 && Current(await f.Q()) == tail && f.Selections.IsEmpty &&
            state.GetProperty("candidates").GetArrayLength() == 2);
    }
    private static async Task QueueResumeNext()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var start = await f.ActivateQueue(); string id = S(start, "id"), tail = Current(await f.Q());
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var fetch = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await hold.Entered.Task;
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = false });
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = true });
        Check(!await f.Rec.TryAdvanceAsync(tail, false, true) && f.Catalog.Calls.Count == 2 && !hold.Cancelled.Task.IsCompleted);
        hold.Complete("queue-recommendations", "3"); await fetch;
        Check(N(await f.Q(), "total") == 3 && Current(await f.Q()) == tail && f.Selections.Count == 1 && f.Catalog.MaxActive == 1);
    }
    private static async Task QueueDisableCommit()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var start = await f.ActivateQueue(); string id = S(start, "id"), tail = Current(await f.Q());
        f.Store.Entered = NewSignal(); f.Store.Release = NewSignal(); f.Catalog.Reply("queue-recommendations", "3");
        var fetch = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await f.Store.Entered.Task;
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = false });
        f.Store.Release.TrySetResult(); await fetch;
        var state = await f.State(); var after = await f.Q();
        Check(N(state, "count") == 3 && N(state, "queueVersion") == N(after, "version") && Current(after) == tail &&
            !state.GetProperty("continue").GetBoolean() && f.Selections.Count == 1);
        Check(f.Rec.GetFeedback(S(after.GetProperty("entries")[2], "id")) is { Kind: "queue", FeedbackSource: "" });
    }
    private static async Task QueueResumePendingNext()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var start = await f.ActivateQueue(); string id = S(start, "id"), tail = Current(await f.Q());
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var fetch = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await hold.Entered.Task;
        Check(!await f.Rec.TryAdvanceAsync(tail, true, true));
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = false });
        await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = true });
        var inserted = await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("9") }, next = true });
        Check(!await f.Rec.TryAdvanceAsync(tail, false, true));
        var next = await f.Port.ExecuteAsync("music.queue.next");
        string selected = S(next, "targetId"); await f.Wait(x => S(x, "currentId") == selected);
        Check(selected == inserted.GetProperty("entryIds")[0].GetString());
        hold.Complete("queue-recommendations", "3"); await fetch;
        Check(Current(await f.Q()) == selected && N(await f.Q(), "total") == 4 && f.Catalog.Calls.Count == 2 && f.Selections.Count == 1);
    }
    private static async Task QueueTerminal(string terminal)
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "9");
        await f.ActivateQueue(); string tail = Current(await f.Q());
        f.Catalog.Handle = (request, _) => Task.FromResult(QueuePage(request.QueueRecommendations!.Offset + 15, terminal != "has-more-false",
            terminal == "empty" ? [] : terminal == "duplicate" ? ["1"] : ["2"]));
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true));
        var state = await f.State();
        Check(state.GetProperty("exhausted").GetBoolean() && S(state, "error") == "" && f.Catalog.Calls.Count == 2);
        Check(N(await f.Q(), "total") == (terminal == "has-more-false" ? 3 : 2) && Current(await f.Q()) == tail && f.Selections.Count == 1);
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true, PlaybackEvent: 2));
        Check(!await f.Rec.TryAdvanceAsync(tail, true, true) && f.Catalog.Calls.Count == 2);
    }
    private static async Task QueueRetry()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        var state = await f.ActivateQueue(); string id = S(state, "id"), tail = Current(await f.Q());
        f.Catalog.Handle = (_, _) => throw new MusicPlatformException("NETWORK");
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true));
        Check(S(await f.State(), "error") == "NETWORK" && (await f.State()).GetProperty("canRetry").GetBoolean());
        await f.Rec.OnPlaybackAsync(new(tail, true, true, true)); Check(f.Catalog.Calls.Count == 2);
        f.Catalog.Reply("queue-recommendations", "3"); await f.Rec.ExecuteAsync("music.recommendations.retry", new { id });
        Check(f.Catalog.Calls.Count == 3 && N(await f.Q(), "total") == 3 && Current(await f.Q()) == tail && f.Selections.Count == 1);
    }
    private static async Task QueueUnknown()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2", "3");
        var start = await f.Start("queue"); string id = S(start, "id"); int writes = f.Store.Writes;
        f.Store.Failure = 2;
        var chosen = await f.Rec.ExecuteAsync("music.recommendations.choose", new { id, candidateId = S(start.GetProperty("candidates")[1], "id") });
        Check(chosen.GetProperty("requiresReconcile").GetBoolean() && !chosen.GetProperty("canRetry").GetBoolean());
        var blocked = await f.Rec.ExecuteAsync("music.recommendations.retry", new { id });
        Check(S(blocked, "code") == "COMMIT_UNKNOWN");
        blocked = await f.Rec.ExecuteAsync("music.recommendations.continue", new { id, enabled = true });
        Check(S(blocked, "code") == "COMMIT_UNKNOWN");
        blocked = await f.Rec.ExecuteAsync("music.recommendations.choose",
            new { id, candidateId = S(start.GetProperty("candidates")[0], "id") });
        Check(S(blocked, "code") == "COMMIT_UNKNOWN" && f.Catalog.Calls.Count == 1);
        var confirmed = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id });
        Check(f.Store.Writes == writes + 1 && N(await f.Q(), "total") == 3 && SongId((await f.Q()).GetProperty("current").GetProperty("song")) == "3");
        Check(f.Selections.IsEmpty && !confirmed.GetProperty("play").GetBoolean() && f.Rec.GetFeedback(Current(await f.Q())) is { Kind: "queue" });
    }
    private static async Task QueueRetired()
    {
        foreach (bool account in new[] { false, true })
        {
            await using var f = new Fixture(); await f.Replace("1"); var hold = new Hold(); f.Catalog.Handle = hold.Read;
            var start = f.Start("queue"); await hold.Entered.Task;
            if (account) f.Rec.Invalidate("wy");
            else await f.Port.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("7") }, index = 0 });
            int writes = f.Store.Writes;
            await hold.Cancelled.Task; hold.Complete("queue-recommendations", "99"); await start;
            var state = await f.State();
            Check(!state.GetProperty("active").GetBoolean() && state.GetProperty("candidates").GetArrayLength() == 0 &&
                f.Store.Writes == writes && f.Selections.IsEmpty && N(await f.Q(), "total") == 1);
        }
    }
    private static async Task QueueUnsupportedCurrent()
    {
        await using var f = new Fixture();
        await Ok(f.Library, "music.queue.replace", new { songs = new[] {
            MusicSongFixture.Element(MusicSongFixture.Online("tx", "qq123", "Song", "Artist", "Album", 180)) }, index = 0 });
        var state = await f.Start("queue");
        Check(S(state, "error") == "NATIVE_ID_REQUIRED" && f.Catalog.Calls.IsEmpty && N(await f.Q(), "total") == 1);
    }
    private static async Task QueueMixed(int index)
    {
        await using var f = new Fixture(); f.Catalog.Reply("queue-recommendations", "8");
        await Ok(f.Library, "music.queue.replace", new { songs = new[] { Song("1"),
            MusicSongFixture.Element(MusicSongFixture.Online("tx", "qq123")), Song("2") }, index });
        var before = await f.Q(); int writes = f.Store.Writes;
        var state = await f.Start("queue");
        Check(index == 1 ? S(state, "error") == "NATIVE_ID_REQUIRED" && f.Catalog.Calls.IsEmpty :
            S(state, "error") == "" && f.Catalog.Calls.Single().QueueRecommendations!.SeedSongId == (index == 0 ? "1" : "2"));
        Check(f.Store.Writes == writes && Current(await f.Q()) == Current(before) && f.Selections.IsEmpty);
    }
    private static async Task QueueMixedPaged()
    {
        await using var f = new Fixture();
        var songs = Enumerable.Range(1, 100).Select(i => Song(i.ToString())).Append(MusicSongFixture.Element(MusicSongFixture.Local("local-tail"))).ToArray();
        Check((await f.Library.ReplaceQueueAsync(songs, 0, null, N(await f.Q(), "version"))).GetProperty("ok").GetBoolean());
        f.Catalog.Reply("queue-recommendations", "102"); int writes = f.Store.Writes;
        var state = await f.Start("queue");
        Check(S(state, "error") == "" && f.Catalog.Calls.Single().QueueRecommendations is { SeedSongId: "1" } &&
            f.Store.Writes == writes && N(await f.Q(), "total") == 101 && state.GetProperty("candidates").GetArrayLength() == 1);
    }
    private static async Task QueueMixedContinuation()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        await f.ActivateQueue(); string tail = Current(await f.Q());
        await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { MusicSongFixture.Element(MusicSongFixture.Online("tx", "qq123")) } });
        Check((await f.State()).GetProperty("active").GetBoolean());
        await f.Select(S((await f.Q()).GetProperty("entries")[2], "id"));
        f.Catalog.Reply("queue-recommendations", "3"); await f.Rec.OnPlaybackAsync(new(Current(await f.Q()), true, true, true));
        Check(f.Catalog.Calls.Last().QueueRecommendations is { SeedSongId: "1", Offset: 15 } && N(await f.Q(), "total") == 4);
    }
    private static async Task QueueOriginalPlayback()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2");
        await f.Start("queue"); string current = Current(await f.Q());
        Check(!await f.Rec.TryAdvanceAsync(current, false, true));
        string added = S((await f.Q()).GetProperty("entries")[1], "id");
        Check(f.Rec.GetFeedback(current) is null && f.Rec.GetFeedback(added) is not null);
        f.Rec.Invalidate("wy"); await f.Rec.PrepareCloseAsync();
        Check(f.Selections.IsEmpty && Current(await f.Q()) == current && f.Rec.GetFeedback(added) is null);
    }
    private static async Task MiddleEdit()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Catalog.Reply("queue-recommendations", "2", "3");
        await f.ActivateQueue(); var q = await f.Q(); string tail = Current(q);
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var pending = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await hold.Entered.Task;
        await f.Port.ExecuteAsync("music.queue.remove", new { id = S(q.GetProperty("entries")[1], "id") });
        hold.Complete("queue-recommendations", "99"); await pending;
        Check((await f.Q()).GetProperty("entries").EnumerateArray().Any(e => SongId(e.GetProperty("song")) == "99"));
        Check(Current(await f.Q()) == tail && f.Catalog.Calls.Last().QueueRecommendations!.SeedSongId == "1");
    }
    private static async Task SelectionFence()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11"); await f.Start("fm");
        var q = await f.Q(); string first = Current(q), tail = S(q.GetProperty("entries")[1], "id");
        await f.Select(tail); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var fetch = f.Rec.OnPlaybackAsync(new(tail, true, true, true)); await hold.Entered.Task;
        await f.Select(first); hold.Complete("fm", "12"); await fetch;
        Check(Current(await f.Q()) == first && N(await f.Q(), "total") == 3 && f.Selections.Count == 1 && !f.Selections.First().IsCurrent());
    }
    private static async Task ReturnEmpty()
    {
        await using var f = new Fixture();
        f.Catalog.Handle = (_, _) => Task.FromResult(HeartbeatBatch(true, "10", "11"));
        var state = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "heartbeat", platform = "wy", entry = "favorites", songId = "10" });
        string current = Current(await f.Q());
        await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        Check(N(await f.Q(), "total") == 1 && Current(await f.Q()) == current && f.Selections.Count == 1);
    }
    private static async Task ReturnPaused()
    {
        await using var f = new Fixture(); await f.Replace("1", "2", "3");
        f.Catalog.Reply("queue-recommendations", "10", "11");
        var state = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "queue", platform = "wy", seedEntryId = Current(await f.Q()), restore = true, play = false });
        string current = S((await f.Q()).GetProperty("entries")[1], "id"); await f.Select(current);
        await f.Rec.OnPlaybackAsync(new(current, false, true, true));
        await f.Port.ExecuteAsync("music.queue.mode", new { mode = 2 });
        await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(state, "id") });
        Check(Current(await f.Q()) == current && N(await f.Q(), "total") == 4 && N(await f.Q(), "mode") == 2 && f.Selections.IsEmpty);
    }
    private static async Task CatalogCommit()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10"); await f.Start("fm");
        var q = await f.Q();
        await f.Port.ExecuteExternalAsync(MusicRecommendationQueueChange.Replace,
            ct => f.Library.ReplaceQueueAsync([Song("77")], 0, null, N(q, "version"), ct));
        Check(!((await f.State()).GetProperty("active").GetBoolean()) && f.Rec.GetFeedback(Current(q)) is null);
        Check(SongId((await f.Q()).GetProperty("current").GetProperty("song")) == "77");
    }
    private static async Task Account()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10"); await f.Start("fm");
        string entry = Current(await f.Q()); f.Rec.Invalidate("tx"); Check(f.Rec.GetFeedback(entry) is not null);
        var stopped = NewSignal(); f.OnSelection = value => { if (value.Action == MusicRecommendationPlaybackAction.Stop) stopped.TrySetResult(); };
        f.Rec.Invalidate("wy"); Check(f.Rec.GetFeedback(entry) is null);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Rec.PrepareCloseAsync(); Check(f.Selections.Any(s => s.Action == MusicRecommendationPlaybackAction.Stop));
        f.Rec.Resume(); Check(!(await f.State()).GetProperty("active").GetBoolean());
    }
    private static async Task CancelClose()
    {
        await using var f = new Fixture(); await f.Replace("1");
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var start = f.Start("fm"); await hold.Entered.Task;
        var close = f.Rec.PrepareCloseAsync(); await hold.Cancelled.Task; Check(!close.IsCompleted);
        try { f.Rec.Resume(); throw new InvalidOperationException("early resume"); } catch (RecommendationException e) { Check(e.Code == "NOT_DRAINED"); }
        hold.Complete("fm", "99"); await start; await close; f.Rec.Resume();
        Check(N(await f.Q(), "total") == 1 && f.Selections.IsEmpty && S(await f.State(), "id") == "");
    }
    private static async Task DuplicateStart()
    {
        await using var f = new Fixture(); await f.Replace("1");
        var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var first = f.Start("similar"); await hold.Entered.Task;
        var second = f.Start("similar");
        Check(!second.IsCompleted && f.Catalog.Calls.Count == 1 && !hold.Cancelled.Task.IsCompleted,
            "identical start replaced or cancelled the active physical request");
        hold.Complete("similar", "10", "11");
        var results = await Task.WhenAll(first, second);
        Check(S(results[0], "id") == S(results[1], "id") && N(results[0], "batch") == 1 &&
            N(results[1], "batch") == 1 && f.Catalog.Calls.Count == 1 && f.Catalog.MaxActive == 1,
            "identical start did not publish the shared session result");
    }
    private static async Task Replacement()
    {
        await using var f = new Fixture();
        var old = new Hold(); f.Catalog.Handle = old.Read;
        var a = f.Start("fm"); await old.Entered.Task;
        f.Catalog.Reply("similar", "10");
        var b = f.Start("similar"); await old.Cancelled.Task; Check(f.Catalog.Calls.Count == 1);
        old.Complete("fm", "99"); await a; await b;
        Check(f.Catalog.MaxActive == 1 && SongId((await f.Q()).GetProperty("current").GetProperty("song")) == "10");
    }
    private static async Task Unknown()
    {
        await using var f = new Fixture(); await f.Replace("1"); f.Store.Failure = 2; f.Catalog.Reply("similar", "10");
        var start = await f.Start("similar"); string id = S(start, "id");
        Check(start.GetProperty("requiresReconcile").GetBoolean() && f.Selections.IsEmpty);
        int writes = f.Store.Writes, reads = f.Catalog.Calls.Count;
        var retry = await f.Rec.ExecuteAsync("music.recommendations.retry", new { id });
        Check(S(retry, "code") == "COMMIT_UNKNOWN" && f.Store.Writes == writes && f.Catalog.Calls.Count == reads);
        var result = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id });
        Check(!result.GetProperty("requiresReconcile").GetBoolean() && N(result, "batch") == 1 && f.Store.Writes == writes);
        var back = await f.Rec.ExecuteAsync("music.recommendations.return", new { id });
        Check(S(back, "code") == "CHECKPOINT_NOT_FOUND" && N(await f.Q(), "total") == 1);
    }
    private static async Task Invalid()
    {
        await using var f = new Fixture(); await f.Replace("1"); int writes = f.Store.Writes;
        var unsupported = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "mg" });
        Check(S(unsupported, "code") == "UNSUPPORTED" && f.Catalog.Calls.IsEmpty);
        f.Catalog.Handle = (_, _) => Task.FromResult(J(new { platform = "wy", kind = "fm-preview", cover = "" }));
        Check(S(await f.Start("fm"), "error") == "IDENTITY_MISMATCH" && f.Store.Writes == writes);
        var remote = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", local = false });
        Check(S(remote, "code") == "REMOTE_NOT_SUPPORTED");
    }
    private static async Task ReconcileCancellation()
    {
        await using var f = new Fixture(); await f.Replace("1");
        f.Store.Failure = 2; f.Catalog.Reply("similar", "10");
        string id = S(await f.Start("similar"), "id");
        using var cancellation = new CancellationTokenSource();
        f.Store.AfterRead = cancellation.Cancel;
        await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id }, cancellation.Token);
        f.Store.AfterRead = null;
        Check((await f.State()).GetProperty("requiresReconcile").GetBoolean());
        int writes = f.Store.Writes, reads = f.Catalog.Calls.Count;
        await f.Port.ExecuteAsync("music.queue.get");
        var accepted = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id });
        Check(!accepted.GetProperty("requiresReconcile").GetBoolean());
        Check(N(accepted, "batch") == 1 && f.Store.Writes == writes && f.Catalog.Calls.Count == reads && f.Selections.IsEmpty);
    }
    private static async Task ObserverCancel()
    {
        await using var f = new Fixture(); int notices = 0;
        f.Rec.Changed += state =>
        {
            if (S(state, "id").Length == 0) return;
            if (Interlocked.Increment(ref notices) > 8) throw new InvalidOperationException("cancel notification loop");
            f.Rec.ExecuteAsync("music.recommendations.cancel", new { id = S(state, "id") }).GetAwaiter().GetResult();
        };
        await f.Start("fm"); await f.Rec.PrepareCloseAsync();
        Check(notices is > 0 and <= 3);
    }
    private static async Task ObserverDispose()
    {
        await using var f = new Fixture(); var disposed = NewSignal();
        f.Rec.Changed += _ => { f.Rec.DisposeAsync().AsTask().GetAwaiter().GetResult(); disposed.TrySetResult(); };
        await f.Start("fm"); await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task ObserverBarrier()
    {
        await using var f = new Fixture(); var entered = NewSignal(); var release = NewSignal();
        f.Rec.Changed += _ => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        var start = f.Start("fm"); await entered.Task;
        var close = f.Rec.DisposeAsync().AsTask(); Check(!close.IsCompleted);
        release.TrySetResult(); await close; await start;
    }
    private static async Task NativeCatalog()
    {
        using var handler = new HttpFixture();
        await using var clients = new MusicClientContextFixture();
        using var catalog = MusicPlatformCatalog.CreateWithHandler(new Sessions(), handler, clients.Owner);
        await using var f = new Fixture(catalog);
        var result = await f.Start("fm"); Check(S(result, "status") == "ready" && handler.Calls == 1);
        Check(N(await f.Q(), "total") == 2 && f.Rec.GetFeedback(Current(await f.Q())) is { Algorithm: "native-alg" });
    }
    private static async Task NativeHttpDrain()
    {
        using var handler = new HttpFixture { Release = NewSignal() };
        await using var clients = new MusicClientContextFixture();
        using var catalog = MusicPlatformCatalog.CreateWithHandler(new Sessions(), handler, clients.Owner);
        await using var f = new Fixture(catalog);
        var start = f.Start("fm"); await handler.Entered.Task;
        var close = f.Rec.PrepareCloseAsync();
        try
        {
            Check(!close.IsCompleted && !handler.Cancelled.Task.IsCompleted);
        }
        finally { handler.Release.TrySetResult(); }
        await start; await close; Check(N(await f.Q(), "total") == 0);
    }
    private static async Task InitialNext()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        string old = Current(await f.Q()); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var start = f.Start("fm"); await hold.Entered.Task;
        Check(!await f.Rec.TryAdvanceAsync(old, false, true));
        hold.Complete("fm", "10", "11"); await start;
        Check(SongId((await f.Q()).GetProperty("current").GetProperty("song")) == "10" && f.Selections.Count == 1);
    }
    private static async Task ReturnSeed()
    {
        await using var f = new Fixture(); await f.Replace("1", "2"); string current = Current(await f.Q());
        f.Catalog.Reply("queue-recommendations", "10");
        var start = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "queue", platform = "wy", seedEntryId = current, restore = true });
        await f.Rec.ExecuteAsync("music.recommendations.return", new { id = S(start, "id") });
        Check(f.Selections.IsEmpty && Current(await f.Q()) == current && N(await f.Q(), "total") == 2);
    }
    private static async Task LateSelection()
    {
        var entered = new TaskCompletionSource<MusicRecommendationSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewSignal();
        await using var f = new Fixture(selectionAsync: async (value, _) =>
        { entered.TrySetResult(value); await release.Task; return value.IsCurrent(); });
        var start = f.Start("fm"); var selection = await entered.Task;
        Check(selection.Play && selection.IsCurrent());
        await f.Rec.OnPlaybackAsync(new(selection.EntryId, false, false, false));
        Check(!selection.IsCurrent());
        var close = f.Rec.PrepareCloseAsync(); Check(!close.IsCompleted);
        release.TrySetResult(); await start; await close;
    }
    private static async Task UnknownClose()
    {
        await using var f = new Fixture(); f.Store.Failure = 2;
        var start = await f.Start("fm");
        try { await f.Rec.PrepareCloseAsync(); throw new InvalidOperationException("unknown close accepted"); }
        catch (RecommendationException e) { Check(e.Code == "COMMIT_UNKNOWN"); }
        var resolved = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id = S(start, "id") });
        Check(!resolved.GetProperty("requiresReconcile").GetBoolean());
        await f.Rec.PrepareCloseAsync(); f.Rec.Resume();
    }
    private static async Task UnknownAbort()
    {
        await using var f = new Fixture(); f.Store.Failure = 2;
        var state = await f.Start("fm");
        Check(state.GetProperty("requiresReconcile").GetBoolean());
        foreach (var close in new Func<Task>[] { () => f.Rec.PrepareCloseAsync(), () => f.Rec.DisposeAsync().AsTask() })
        {
            try { await close(); throw new InvalidOperationException("Unknown receipt accepted by ordinary close"); }
            catch (RecommendationException error) { Check(error.Code == "COMMIT_UNKNOWN"); }
        }
        await f.Rec.AbortAsync();
        await f.Rec.AbortAsync();
        var rejected = await f.Start("fm");
        Check(!rejected.GetProperty("ok").GetBoolean() && S(rejected, "code") == "CLOSED");
    }
    private static async Task ReplacementUnknown()
    {
        await using var f = new Fixture(); await f.Replace("1", "2");
        f.Store.Entered = NewSignal(); f.Store.Release = NewSignal(); f.Store.Failure = 2; f.Catalog.Reply("similar", "10");
        var first = f.Start("similar"); await f.Store.Entered.Task;
        var second = f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "similar", platform = "wy", sourceIds = new[] { "2" }, sourceType = "song" });
        f.Store.Release.TrySetResult(); var a = await first; await second;
        var state = await f.State(); Check(state.GetProperty("reconcileIds").GetArrayLength() == 1);
        string unresolved = state.GetProperty("reconcileIds")[0].GetString()!;
        await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id = unresolved });
        f.Catalog.Reply("similar", "11");
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(await f.State(), "id") });
        Check(N(await f.Q(), "total") == 1 && !(await f.State()).GetProperty("canReturn").GetBoolean() && f.Catalog.MaxActive == 1);
    }
    private static async Task FmLineage()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11");
        var first = await f.Start("fm"); string prior = Current(await f.Q());
        var original = f.Rec.GetFeedback(prior);
        f.Catalog.Reply("fm", "12", "13");
        var next = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        Check(f.Rec.GetFeedback(prior) == original && original is not null, "old live FM batch provenance lost by mode switch");
        Check(S(next, "id") == S(first, "id") && N(next, "batch") == 2, "FM configuration switch must not replace live session identity");
        Check(f.Rec.GetFeedback(Current(await f.Q())) is { Mode: "explore" } feedback && feedback.SessionId == original!.SessionId && feedback.BatchId != original.BatchId);
    }
    private static async Task FmSwitchFailure()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11");
        var first = await f.Start("fm");
        f.Catalog.Handle = (r, _) => r.Fm!.Mode == MusicFmMode.Explore
            ? throw new MusicPlatformException("NETWORK") : Task.FromResult(Batch("fm", "12"));
        var failed = await f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        Check(S(failed, "error") == "NETWORK" && N(await f.Q(), "total") == 2 && S(failed, "mode") == "default");
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(failed, "id") });
        Check(f.Catalog.Calls.Last().Fm!.Mode == MusicFmMode.Default, "failed mode target incorrectly became committed configuration");
        Check(N(await f.Q(), "total") == 3 && f.Selections.Count == 1 && S(first, "id") == S(failed, "id"));
    }
    private static async Task FmSwitchOverlap()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11");
        var first = await f.Start("fm"); string current = Current(await f.Q());
        var hold = new Hold();
        f.Catalog.Handle = (request, ct) => request.Fm!.Mode switch
        {
            MusicFmMode.Explore => hold.Read(request, ct),
            MusicFmMode.Familiar => throw new MusicPlatformException("NETWORK"),
            _ => Task.FromResult(Batch("fm", "12"))
        };
        var explore = f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        await hold.Entered.Task;
        Check(S(await f.State(), "mode") == "default", "pending target must not appear as committed mode");
        var familiar = f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "familiar" });
        await hold.Cancelled.Task;
        int readsBeforeDrain = f.Catalog.Calls.Count;
        hold.Complete("fm", "99");
        await explore; var failed = await familiar;
        Check(readsBeforeDrain == 2 && f.Catalog.MaxActive == 1, "replacement must await the prior physical read");
        Check(S(failed, "error") == "NETWORK" && Current(await f.Q()) == current && N(await f.Q(), "total") == 2 &&
            S(failed, "mode") == "default");
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id = S(failed, "id") });
        Check(f.Catalog.Calls.Last().Fm!.Mode == MusicFmMode.Default, "pending switch target replaced the last committed configuration");
        Check(S(first, "id") == S(failed, "id") && N(await f.Q(), "total") == 3 && f.Selections.Count == 1);
        Check(f.Rec.GetFeedback(current) is { Mode: "default" });
    }
    private static async Task FmSwitchUnknown()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11");
        var first = await f.Start("fm"); string id = S(first, "id");
        f.Store.Entered = NewSignal(); f.Store.Release = NewSignal(); f.Store.Failure = 2;
        f.Catalog.Reply("fm", "12", "13");
        var explore = f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        await f.Store.Entered.Task;
        var familiar = f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "familiar" });
        f.Store.Release.TrySetResult(); await explore; var blocked = await familiar;
        Check(S(blocked, "error") == "COMMIT_UNKNOWN" && S(blocked, "id") == id && f.Catalog.Calls.Count == 2);
        Check(blocked.GetProperty("requiresReconcile").GetBoolean() && !blocked.GetProperty("canRetry").GetBoolean(),
            "same-id unresolved commit must expose explicit reconciliation, not ordinary retry");
        int writes = f.Store.Writes;
        var reconciled = await f.Rec.ExecuteAsync("music.recommendations.reconcile", new { id });
        var actual = await f.Q();
        Check(reconciled.GetProperty("ok").GetBoolean() && reconciled.GetProperty("reconcileIds").GetArrayLength() == 0);
        Check(N(reconciled, "count") == N(actual, "total") && S(reconciled, "currentId") == Current(actual) &&
            N(reconciled, "queueVersion") == N(actual, "version"), "same-id reconciliation returned stale queue facts");
        Check(N(reconciled, "batch") == 2 && S(reconciled, "mode") == "explore" && f.Rec.GetFeedback(Current(actual)) is { Mode: "explore" },
            "explicitly confirmed batch lost its actual provenance");
        Check(f.Store.Writes == writes && f.Selections.Count == 1 && !reconciled.GetProperty("play").GetBoolean());
        f.Catalog.Reply("fm", "14");
        await f.Rec.ExecuteAsync("music.recommendations.retry", new { id });
        Check(f.Catalog.Calls.Last().Fm!.Mode == MusicFmMode.Explore && N(await f.Q(), "total") == 5 &&
            f.Selections.Count == 1, "retry must continue the confirmed mode, not the failed replacement target");
    }
    private static async Task FmSwitchEdit()
    {
        await using var f = new Fixture(); f.Catalog.Reply("fm", "10", "11"); await f.Start("fm");
        string current = Current(await f.Q()); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var explore = f.Rec.ExecuteAsync("music.recommendations.start", new { kind = "fm", platform = "wy", mode = "explore" });
        await hold.Entered.Task; await f.Port.ExecuteAsync("music.queue.select", new { id = current });
        hold.Complete("fm", "99"); var state = await explore;
        Check(Current(await f.Q()) == current && N(await f.Q(), "total") == 3 && f.Selections.Count == 1 && S(state, "mode") == "explore");
    }
    private static async Task DisposalAdmission()
    {
        await using var f = new Fixture(); var hold = new Hold(); f.Catalog.Handle = hold.Read;
        var start = f.Start("fm"); await hold.Entered.Task;
        var dispose = f.Rec.DisposeAsync().AsTask(); await hold.Cancelled.Task;
        string code = "";
        try { f.Rec.Resume(); }
        catch (RecommendationException error) { code = error.Code; }
        finally { hold.Complete("fm", "10"); await start; await dispose; }
        Check(code == "CLOSED", "Dispose admission must permanently close Resume, not merely wait for preparation");
    }
    private sealed class Sessions : IMusicPlatformSessionSource
    {
        private readonly MusicPlatformSession value = new("wy", "synthetic-recommendation", 1, "123", "MUSIC_U=synthetic;");
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken ct) => ValueTask.FromResult<MusicPlatformSession?>(value);
        public bool IsCurrent(MusicPlatformSession session) => ReferenceEquals(session, value);
    }
    private sealed class HttpFixture : HttpMessageHandler
    {
        internal int Calls;
        internal readonly TaskCompletionSource Entered = NewSignal(), Cancelled = NewSignal();
        internal TaskCompletionSource? Release;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/v1/radio/get", StringComparison.Ordinal));
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult(); if (Release is { } signal) await signal.Task;
            var rows = new[] { "10", "11" }.Select(id => new { id = int.Parse(id), name = "Same title",
                ar = new[] { new { name = "Artist" } }, al = new { id = 1, name = "Album" }, dt = 180000, alg = "native-alg" });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 200, data = rows }), Encoding.UTF8, "application/json") };
        }
    }
}

internal static class RecommendationTest
{
    internal static void Check(bool condition, string message = "assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    internal static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    internal static string S(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    internal static long N(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    internal static string Current(JsonElement q) => q.GetProperty("currentId").ValueKind == JsonValueKind.Null ? "" : S(q, "currentId");
    internal static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static JsonElement Song(string id) => MusicSongFixture.Element(MusicSongFixture.Online(
        "wy", id, "Same title", "Artist", "Album", 180,
        native: JsonSerializer.SerializeToNode(new { name = "Same title", singer = "Artist", duration = 180 })!.AsObject()));
    internal static string SongId(JsonElement song) => MusicSongFixture.NativeId(song);
    internal static JsonElement Batch(string kind, params string[] ids) => J(new { platform = "wy", kind,
        items = ids.Select(Song).ToArray(), count = ids.Length, empty = ids.Length == 0, context = new { },
        recommendations = ids.Select(id => new { id, alg = "alg-" + id, source = kind }).ToArray() });
    internal static JsonElement HeartbeatBatch(bool fromHome, params string[] ids)
    {
        var value = JsonNode.Parse(Batch("heartbeat", ids).GetRawText())!.AsObject();
        value["context"] = JsonSerializer.SerializeToNode(new
            { playlistId = "100", startMusicId = fromHome ? "10" : "1", songId = fromHome ? "10" : "1", fromHome, count = 120 });
        value["entrySong"] = JsonNode.Parse(Song(fromHome ? "10" : "1").GetRawText());
        return J(value);
    }
    internal static JsonElement QueueBatch(bool hasMore, params string[] ids) => QueuePage(15, hasMore, ids);
    internal static JsonElement QueuePage(int offset, bool hasMore, params string[] ids)
    {
        var value = JsonNode.Parse(Batch("queue-recommendations", ids).GetRawText())!.AsObject();
        value["hasMore"] = hasMore; value["offset"] = offset;
        value["recommendations"] = JsonSerializer.SerializeToNode(ids.Select(id => new
            { id, source = "queue-recommendations", alg = "alg-" + id, feedbackSource = "feedback-" + id }));
        return J(value);
    }
    internal static async Task<JsonElement> Ok(MusicLibraryOwner owner, string method, object? input = null)
    {
        var result = await owner.ExecuteAsync(method, input); Check(result.GetProperty("ok").GetBoolean(), result.GetRawText()); return result;
    }
    internal sealed class Clock : TimeProvider
    {
        private long seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1800000000 + Interlocked.Read(ref seconds));
        internal void Advance(int value) => Interlocked.Add(ref seconds, value);
    }
    internal sealed class Hold
    {
        internal readonly TaskCompletionSource Entered = NewSignal(), Cancelled = NewSignal();
        private readonly TaskCompletionSource<JsonElement> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<JsonElement> Read(MusicRecommendationRequest request, CancellationToken ct)
        { using var registration = ct.Register(() => Cancelled.TrySetResult()); Entered.TrySetResult(); return await result.Task; }
        internal void Complete(string kind, params string[] ids) => result.TrySetResult(Batch(kind, ids));
        internal void Complete(JsonElement value) => result.TrySetResult(value);
    }
    internal sealed class Store : IDocumentStore
    {
        private readonly object gate = new();
        internal byte[]? Bytes;
        internal int Writes, Failure;
        internal Action? AfterRead;
        internal TaskCompletionSource? Entered, Release;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Check(ns == MusicLibraryOwner.DocumentNamespace);
            DocumentSnapshot result;
            lock (gate) result = new(Bytes?.ToArray(), Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes));
            AfterRead?.Invoke();
            return Task.FromResult(result);
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken ct = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace);
            if (Release is { } signal) { Entered!.TrySetResult(); await signal.Task; }
            lock (gate)
            {
                Check((Bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Bytes)) == expected, "CAS expected");
                int fail = Failure; Failure = 0; Writes++;
                if (fail == 1) throw new DocumentStoreException(DocumentStoreError.Storage);
                Bytes = bytes.ToArray();
                if (fail == 2) throw new DocumentStoreException(DocumentStoreError.Storage);
                return DocumentFingerprint.FromBytes(Bytes);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new();
        internal readonly Clock Clock = new();
        internal readonly Catalog Catalog = new();
        internal readonly MusicLibraryOwner Library;
        internal readonly MusicRecommendationQueuePort Port;
        internal readonly MusicRecommendationCoordinator Rec;
        internal readonly ConcurrentQueue<MusicRecommendationSelection> Selections = new();
        internal Action<MusicRecommendationSelection>? OnSelection;
        internal Fixture(IMusicPlatformCatalog? actual = null,
            Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? selectionAsync = null, Func<bool>? randomFetch = null)
        {
            Library = new(Store); Port = new(Library);
            Rec = new(actual ?? Catalog, Port, selectionAsync ?? ((value, _) =>
            { if (value.IsCurrent()) { Selections.Enqueue(value); OnSelection?.Invoke(value); } return Task.FromResult(value.IsCurrent()); }), Clock,
                actual is null ? Catalog.Read : null, randomFetch);
        }
        internal async Task<JsonElement> Start(string kind)
        {
            object input = kind switch
            {
                "queue" => new { kind, platform = "wy", seedEntryId = Current(await Q()) },
                "similar" => new { kind, platform = "wy", sourceIds = new[] { "1" }, sourceType = "song" },
                _ => new { kind, platform = "wy" }
            };
            return await Rec.ExecuteAsync("music.recommendations.start", input);
        }
        internal Task<JsonElement> Heartbeat(bool continuation = false) => Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "heartbeat", platform = "wy", entry = "current", songId = "1", playlistId = "100" });
        internal async Task Select(string id)
        {
            await Port.ExecuteAsync("music.queue.select", new { id });
            await Wait(state => S(state, "currentId") == id);
        }
        internal async Task<JsonElement> ActivateQueue()
        {
            var state = await Start("queue");
            return await Rec.ExecuteAsync("music.recommendations.choose", new {
                id = S(state, "id"), candidateId = S(state.GetProperty("candidates").EnumerateArray().Last(), "id") });
        }
        internal Task<JsonElement> Q() => Ok(Library, "music.queue.get");
        internal Task<JsonElement> State() => Rec.ExecuteAsync("music.recommendations.get");
        internal Task<JsonElement> Replace(params string[] ids) =>
            Ok(Library, "music.queue.replace", new { songs = ids.Select(Song).ToArray(), index = 0 });
        internal async Task<JsonElement> Wait(Func<JsonElement, bool> condition)
        {
            var signal = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observer(JsonElement state) { if (condition(state)) signal.TrySetResult(state); }
            Rec.Changed += Observer;
            try { var now = await State(); if (condition(now)) return now; return await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Rec.Changed -= Observer; }
        }
        public async ValueTask DisposeAsync()
        {
            var state = await State();
            if (state.TryGetProperty("reconcileIds", out var pending))
                foreach (var id in pending.EnumerateArray()) await Rec.ExecuteAsync("music.recommendations.reconcile", new { id = id.GetString() });
            await Rec.DisposeAsync(); await Library.AbortAsync();
        }
    }
    internal sealed class Catalog : IMusicPlatformCatalog
    {
        internal Func<MusicRecommendationRequest, CancellationToken, Task<JsonElement>> Handle = (_, _) => Task.FromResult(Batch("fm", "10", "11"));
        internal readonly ConcurrentQueue<MusicRecommendationRequest> Calls = new();
        private int active;
        internal int MaxActive;
        internal void Reply(string kind, params string[] ids) => Handle = (_, _) =>
            Task.FromResult(kind == "heartbeat" ? HeartbeatBatch(false, ids) : Batch(kind, ids));
        internal async Task<JsonElement> Read(MusicRecommendationRequest request, CancellationToken ct)
        {
            Calls.Enqueue(request); int count = Interlocked.Increment(ref active); MaxActive = Math.Max(MaxActive, count);
            try
            {
                var response = await Handle(request, ct);
                if (request.QueueRecommendations is { } page && !response.TryGetProperty("offset", out _))
                {
                    var value = JsonNode.Parse(response.GetRawText())!.AsObject();
                    value["offset"] = page.Offset + 15; value["hasMore"] = true;
                    return J(value);
                }
                return response;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task<JsonElement> FmAsync(MusicFmRequest r, CancellationToken ct = default) => Read(new(r), ct);
        public Task<JsonElement> SimilarAsync(MusicSimilarRequest r, CancellationToken ct = default) => Read(new(r), ct);
        public Task<JsonElement> HeartbeatAsync(MusicHeartbeatRequest r, CancellationToken ct = default) => Read(new(r), ct);
        public Task<JsonElement> QueueRecommendationsAsync(MusicQueueRecommendationsRequest r, CancellationToken ct = default) => Read(new(r), ct);
        public Task<JsonElement> SearchAsync(MusicSearchRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> SongAsync(MusicSongRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> PlaylistAsync(MusicPlaylistRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> HomeAsync(MusicHomeRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> ChannelsAsync(MusicChannelsRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> ChannelAsync(MusicChannelRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> PersonalPlaylistsAsync(MusicPersonalPlaylistsRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> DailyAsync(MusicDailyRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> AlbumAsync(MusicAlbumRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public JsonElement Describe() => J(new { });
        public void Invalidate(string? platform = null) { }
        public void Dispose() { }
    }
}
