using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmPlaybackSessionTests
{
    private static TestCase Test(string name, Func<Task> action) =>
        new("film-session: " + name, () => MusicQualityConsumerTests.Sta(action));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ReleaseFailure(Func<Task> action)
    {
        try { await action(); }
        catch (PlaybackException error) when (error.Code == PlaybackError.ReleaseFailed) { return; }
        throw new InvalidOperationException("Expected fixed failure " + PlaybackError.ReleaseFailed);
    }
    private static bool Called(FilmPlaybackRig rig, string action, string? attempt = null) =>
        rig.Runtime.Calls.Any(call => call.Method == "film.session.command" &&
            FilmJson.Text(call.Parameters, "action") == action &&
            (attempt is null || FilmJson.Text(call.Parameters, "attemptId") == attempt));
    private static async Task Play(FilmPlaybackRig rig, string id = "first", int episode = 1)
    {
        rig.Runtime.Attempt(id, Candidate(id, episode), target: rig.Runtime.Target with
        { Season = 1, Episode = episode, GlobalEpisode = episode });
        await Until(() => rig.Native.SettledIdentity == id && Called(rig, "media.started", id));
        await Until(() => rig.Session.HasOutput);
    }

    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("headerless direct receipt consumes exact format scope native controls and release without a local lease", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            rig.Runtime.Controlled = false;
            await rig.OpenAsync();
            var scope = FilmJson.Encode(new { sourceKey = "cat:fixture:site:1", sourceEngine = "cat", sourceRevision = 1 });
            rig.Runtime.Intercept = (method, input, _) =>
            {
                var reply = rig.Runtime.Handle(method, input);
                if (method == "film.session.attempt")
                {
                    var receipt = JsonNode.Parse(reply.GetProperty("value").GetRawText())!.AsObject();
                    receipt["format"] = "mp4"; receipt["scope"] = JsonNode.Parse(scope.GetRawText());
                    reply = FilmRuntimeProbe.Ok(FilmJson.Encode(receipt));
                }
                return Task.FromResult(reply);
            };
            await Play(rig, "direct-file", 2);
            var input = rig.Native.Inputs.Single();
            Check(input.Receipt.AttemptId == "direct-file" && input.Intent.Identity == input.Receipt.AttemptId &&
                input.Receipt.ResourceId == FilmJson.Text(rig.Runtime.Receipts["direct-file"], "id") &&
                input.Receipt.Address.AbsoluteUri == FilmJson.Text(rig.Runtime.Receipts["direct-file"], "url") &&
                input.Receipt.MediaOrigin is null && input.Lease is null && rig.Native.AcceptedLease is null &&
                JsonElement.DeepEquals(input.Receipt.Scope, scope) && input.Receipt.Format.GetString() == "mp4" &&
                rig.Page.CurrentCandidateId == "direct-file" && rig.Page.ActiveTarget.Episode == 2,
                "absence of mediaOrigin chooses the real direct native input without inventing a Node lease");
            using (var video = FilmVideoInput.Read(input.Receipt.Format, input.Receipt.Drm))
                Check(video.Format == NativeVideoFormat.Progressive, "the direct receipt still uses the production typed input adapter");
            await rig.Session.SeekAsync(81); await rig.Session.SetPausedAsync(true);
            Check(rig.Session.Position == 81 && !rig.Session.IsPlaying && rig.Native.Commits == 1 &&
                Called(rig, "media.started", "direct-file") && rig.Runtime.Calls.Any(call =>
                    FilmJson.Text(call.Parameters, "action") == "media.progress" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "direct-file" &&
                    FilmJson.Number(call.Parameters, "position") == 81 && FilmJson.Bool(call.Parameters, "paused")),
                "the direct occurrence owns real native controls and exact progress rather than only a URL");
            await rig.Session.CloseSessionAsync();
            Check(rig.Native.Stops == 1 && !rig.Session.HasOutput &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == input.Receipt.AttemptId &&
                    FilmJson.Text(call.Parameters, "resourceId") == input.Receipt.ResourceId) == 1 &&
                !Called(rig, "media.failed"), "direct media retains one precise resource release without a local lease");
        });
        foreach (bool releaseFails in new[] { false, true })
            yield return Test("open HTTP address preserves the exact native release receipt failure=" + releaseFails, async () =>
            {
                var rig = new FilmPlaybackRig();
                var releasing = Gate(); var released = Gate();
                var address = new Uri("https://fixture%20user:p%40ss%3Aword@other.local:41002/video.mp4?fixture=" +
                    new string('x', 17000));
                try
                {
                    await rig.OpenAsync();
                    rig.Runtime.Intercept = async (method, input, _) =>
                    {
                        if (method == "film.session.media.release")
                        {
                            releasing.TrySetResult(); await released.Task;
                            if (releaseFails) return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                        }
                        var response = rig.Runtime.Handle(method, input);
                        if (method == "film.session.attempt")
                        {
                            var receipt = JsonNode.Parse(response.GetProperty("value").GetRawText())!.AsObject();
                            receipt["url"] = address.AbsoluteUri;
                            response = FilmRuntimeProbe.Ok(FilmJson.Encode(receipt));
                        }
                        return response;
                    };
                    rig.Runtime.Attempt("open-url", Candidate("open-url"));
                    await Until(() => Called(rig, "media.started", "open-url") && rig.Session.HasOutput);
                    var lease = rig.Native.AcceptedLease!;
                    Check(lease.Address == address && lease.Headers.Count == 0 && !lease.Revoked.IsCancellationRequested &&
                        rig.Native.Commits == 1, "the session hands its exact non-origin long URL to native without URL admission");
                    string resourceId = FilmJson.Text(rig.Runtime.Receipts["open-url"], "id");
                    var nativeRelease = lease.DisposeAsync().AsTask();
                    await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var close = rig.Session.CloseSessionAsync(); var duplicate = rig.Session.CloseSessionAsync();
                    Check(ReferenceEquals(close, duplicate) && !nativeRelease.IsCompleted && !close.IsCompleted &&
                        lease.Revoked.IsCancellationRequested,
                        "native retirement and session close await the same actual release acknowledgement");
                    released.TrySetResult();
                    if (releaseFails)
                    {
                        await ReleaseFailure(() => nativeRelease);
                        await ReleaseFailure(() => close);
                        await ReleaseFailure(() => duplicate);
                        await ReleaseFailure(() => rig.Session.CloseSessionAsync());
                    }
                    else await Task.WhenAll(nativeRelease, close, duplicate);
                    var calls = rig.Runtime.Calls.Where(call => call.Method == "film.session.media.release").ToArray();
                    Check(calls.Length == 1 && FilmJson.Text(calls[0].Parameters, "attemptId") == "open-url" &&
                        FilmJson.Text(calls[0].Parameters, "resourceId") == resourceId &&
                        rig.Runtime.Calls.Count(call => call.Method == "film.session.release") == 1 &&
                        !Called(rig, "media.failed") && !rig.Session.HasSession,
                        "one exact receipt is released even on failure; close does not recreate or misreport the retired input");
                }
                finally
                {
                    released.TrySetResult();
                    if (releaseFails) await ReleaseFailure(() => rig.DisposeAsync().AsTask());
                    else await rig.DisposeAsync();
                }
            });
        foreach (string action in new[] { "browse", "play", "select.episode" })
            foreach (bool fails in new[] { false, true })
                yield return Test($"retired {action} drains its late {(fails ? "STORAGE_ERROR" : "success")} without UI publication",
                    () => RetiredCommandAsync(action, fails));
        foreach (bool changedGeneration in new[] { false, true })
            foreach (bool fails in new[] { false, true })
                yield return Test($"source {(changedGeneration ? "generation change" : "removal")} revokes an admitted attempt before its {(fails ? "error" : "receipt")}",
                    () => RevokedAttemptAsync(changedGeneration, fails, releaseFails: false));
        yield return Test("late revoked receipt release failure remains the same failure across close and disposal",
            () => RevokedAttemptAsync(changedGeneration: false, fails: false, releaseFails: true));
        yield return Test("the exact unchanged source does not revoke an admitted attempt receipt", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var entered = Gate(); var finish = Gate(); CancellationToken admitted = default;
            rig.Runtime.Intercept = async (method, input, token) =>
            {
                var response = rig.Runtime.Handle(method, input);
                if (method == "film.session.attempt")
                { admitted = token; entered.TrySetResult(); await finish.Task; }
                return response;
            };
            try
            {
                var candidate = Candidate("unchanged");
                rig.Runtime.Attempt("unchanged", candidate); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                rig.Session.SourcesChanged([candidate.GetProperty("source")]);
                Check(!admitted.IsCancellationRequested, "the exact owner generation remains admitted");
                finish.TrySetResult();
                await Until(() => Called(rig, "media.started", "unchanged") && rig.Session.HasOutput);
                Check(rig.Native.Commits == 1 && !Called(rig, "media.failed") &&
                    !rig.Runtime.Released.Contains("unchanged"), "unchanged ownership consumes the same receipt once");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("a rejected lease releases its original receipt without destroying the healthy donor", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            rig.Runtime.Intercept = (method, input, _) =>
            {
                var response = rig.Runtime.Handle(method, input);
                if (method == "film.session.attempt" && FilmJson.Text(input, "attemptId") == "invalid-http")
                {
                    var receipt = JsonNode.Parse(response.GetProperty("value").GetRawText())!.AsObject();
                    receipt["url"] = "ftp://example.org/fixture";
                    response = FilmRuntimeProbe.Ok(FilmJson.Encode(receipt));
                }
                return Task.FromResult(response);
            };
            rig.Runtime.Attempt("invalid-http", Candidate("invalid-http"));
            await Until(() => Called(rig, "media.failed", "invalid-http") && rig.Runtime.Released.Contains("invalid-http"));
            Check(rig.Native.Intents.Count == 1 && rig.Native.Commits == 1 && rig.Native.Identity == "first" &&
                rig.Session.HasOutput && !Called(rig, "media.failed", "first") &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "invalid-http") == 1,
                "HTTP format rejection before native prepare still owns the exact Node receipt");
            await rig.Session.CloseSessionAsync();
            Check(rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                FilmJson.Text(call.Parameters, "attemptId") == "invalid-http") == 1, "close cannot duplicate the original release");
        });
        yield return Test("prepared to play with the same identity consumes the receipt exactly once", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Attempt("prepared", Candidate("prepared"), prepareOnly: true);
            await Until(() => rig.Page.StatusText == "已准备");
            Check(rig.Native.Intents.Count == 0 && !rig.Session.HasOutput &&
                !rig.Runtime.Calls.Any(call => call.Method == "film.session.attempt"), "address preparation never opens native output");
            rig.Runtime.Intercept = (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "play")
                    rig.Runtime.Change(root => { root["attempt"]!["prepareOnly"] = false; root["stage"] = "pending"; });
                return Task.FromResult(rig.Runtime.Handle(method, input));
            };
            await rig.Page.PlayAsync();
            await Until(() => Called(rig, "media.started", "prepared") && rig.Native.Commits == 1);
            await rig.Session.ReconnectAsync();
            Check(rig.Native.Commits == 1 && rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt") == 1 &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.command" && FilmJson.Text(call.Parameters, "action") == "play") == 1,
                "the state transition, not seeing an id, gates its single native consumption");
        });
        foreach (string action in new[] { "play", "select.episode", "select.candidate" })
            yield return Test($"repeated {action} reaches Node and keeps an unchanged preparing receipt", async () =>
            {
                await using var rig = new FilmPlaybackRig();
                await rig.OpenAsync();
                var entered = Gate(); var finish = Gate(); CancellationToken prepareToken = default;
                rig.Native.Preparing = async token => { prepareToken = token; entered.TrySetResult(); await finish.Task; };
                try
                {
                    var target = action == "select.candidate" ? new FilmPlaybackTarget(rig.Runtime.Target.Id, "source",
                        rig.Runtime.Target.Title, Season: 1, Episode: 1, GlobalEpisode: 1,
                        Source: FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0, lineId = 0, candidateId = "pending-play" })) :
                        rig.Runtime.Target;
                    rig.Runtime.Attempt("pending-play", Candidate("pending-play"), target: target); await entered.Task;
                    for (int index = 0; index < 2; index++)
                        if (action == "play") await rig.Page.PlayAsync();
                        else await rig.Page.CommandAsync(action, action == "select.candidate"
                            ? (object)new { candidateId = "pending-play" } : new { episode = new { season = 1, episode = 1, globalEpisode = 1 } });
                    Check(rig.Runtime.Calls.Count(call => call.Method == "film.session.command" &&
                        FilmJson.Text(call.Parameters, "action") == action) == 2 &&
                        !prepareToken.IsCancellationRequested && !rig.Runtime.Released.Contains("pending-play") &&
                        rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt") == 1,
                        "every activation reaches the intent owner; an unchanged receipt retains one native consumer");
                    finish.TrySetResult();
                    await Until(() => Called(rig, "media.started", "pending-play") && rig.Session.HasOutput);
                    Check(rig.Native.Commits == 1 && !Called(rig, "media.failed"), "repeated activation yields one real output");
                }
                finally { finish.TrySetResult(); }
            });
        foreach (bool accepted in new[] { true, false })
            yield return Test($"a later page play {(accepted ? "accepted" : "rejected")} waits for Node before replacing a preparing receipt", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var entered = Gate(); var finish = Gate(); var prepared = Gate();
            var requested = Gate(); var response = Gate(); CancellationToken prepareToken = default;
            rig.Native.Preparing = async token =>
            {
                prepareToken = token; entered.TrySetResult(); await finish.Task; prepared.TrySetResult();
            };
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.command" && FilmJson.Text(input, "action") == "play")
                {
                    Check(input.EnumerateObject().Select(field => field.Name).Order().SequenceEqual(new[] { "action", "sessionId" }),
                        "page play asks Node to resolve its current selection without a second intent protocol");
                    requested.TrySetResult(); await response.Task;
                    if (!accepted) return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                    rig.Runtime.Attempt("new-selection", Candidate("new-selection", episode: 2),
                        target: rig.Runtime.Target with { Episode = 2, GlobalEpisode = 2 });
                }
                return rig.Runtime.Handle(method, input);
            };
            try
            {
                rig.Runtime.Attempt("old-selection", Candidate("old-selection")); await entered.Task;
                var playing = rig.Page.PlayAsync(); await requested.Task;
                Check(!prepareToken.IsCancellationRequested && !rig.Runtime.Released.Contains("old-selection"),
                    "a pending command cannot release an address before its owner decides whether to reuse it");
                finish.TrySetResult(); await prepared.Task;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(rig.Native.Commits == 0 && !Called(rig, "media.started", "old-selection"),
                    "a ready old input cannot publish while the newer page intent awaits admission");
                rig.Native.Preparing = null; response.TrySetResult();
                if (accepted) await playing;
                else await Error("STORAGE_ERROR", () => playing);
                string winner = accepted ? "new-selection" : "old-selection";
                await Until(() => rig.Native.SettledIdentity == winner && rig.Session.HasOutput &&
                    Called(rig, "media.started", winner));
                Check(prepareToken.IsCancellationRequested == accepted && rig.Native.Commits == 1 &&
                    rig.Runtime.Released.Contains("old-selection") == accepted && !Called(rig, "media.failed") &&
                    Called(rig, "media.started", "old-selection") == !accepted &&
                    rig.Runtime.Calls.Count(call => call.Method == "film.session.command" &&
                        FilmJson.Text(call.Parameters, "action") == "play") == 1 &&
                    rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt") == (accepted ? 2 : 1),
                    "only an accepted replacement retires the old input; a rejected intent reuses it with no duplicate read or false failure");
            }
            finally { finish.TrySetResult(); response.TrySetResult(); }
        });
        yield return Test("same episode keeps live position pause rate and aspect while next episode starts at zero", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            await rig.Session.SeekAsync(120); await rig.Session.SetPausedAsync(true);
            await rig.Session.RateAsync(1.5); await rig.Session.AspectAsync("4:3");
            var donorScope = rig.Native.Scope;
            var entered = Gate(); var finish = Gate();
            rig.Native.Preparing = async _ => { entered.TrySetResult(); await finish.Task; };
            try
            {
                rig.Runtime.Attempt("same", Candidate("same"));
                await entered.Task;
                Check(rig.Native.Identity == "first" && rig.Session.HasOutput, "the healthy donor stays accepted during prepare");
                Check(rig.Native.Intents[^1].PreserveFrom == donorScope && rig.Native.Intents[^1].Identity == "same",
                    "the new attempt carries the exact old donor occurrence rather than sharing its identity");
                await rig.Session.SeekAsync(137);
                finish.TrySetResult();
                await Until(() => Called(rig, "media.started", "same"));
                Check(rig.Native.Identity == "same" && rig.Native.Position == 137 && rig.Native.Paused &&
                    rig.Native.Rate == 1.5 && rig.Native.Aspect == "4:3", "same-episode handoff uses live donor intent");
                rig.Native.Preparing = null;
                await Play(rig, "next", 2);
                Check(rig.Native.Position == 0 && !rig.Native.Paused && !rig.Native.Intents[^1].PreserveCurrent,
                    "a real new episode does not inherit the preceding episode's clock or paused state");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("same episode continuity survives metadata to source while a new source episode resets its clock", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            await rig.Session.SeekAsync(120); await rig.Session.SetPausedAsync(true);
            var donor = rig.Native.Scope;
            var exact = new FilmPlaybackTarget(rig.Runtime.Target.Id, "source", rig.Runtime.Target.Title,
                Season: 1, Episode: 1, GlobalEpisode: 1,
                Source: FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0, lineId = 0, candidateId = "same-source-episode" }));
            rig.Runtime.Attempt("same-source-episode", Candidate("same-source-episode"), target: exact);
            await Until(() => rig.Native.SettledIdentity == "same-source-episode" && Called(rig, "media.started", "same-source-episode"));
            Check(rig.Native.Intents[^1].PreserveFrom == donor && rig.Native.Position == 120 && rig.Native.Paused,
                "execution changing to source does not erase the confirmed same content episode");
            rig.Runtime.Attempt("new-source-episode", Candidate("new-source-episode", episode: 2), target: exact with
            {
                Episode = 2, GlobalEpisode = 2,
                Source = FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0, lineId = 0, candidateId = "new-source-episode" })
            });
            await Until(() => rig.Native.SettledIdentity == "new-source-episode" && Called(rig, "media.started", "new-source-episode"));
            Check(!rig.Native.Intents[^1].PreserveCurrent && rig.Native.Position == 0 && !rig.Native.Paused,
                "new exact episode coordinates cannot inherit the old episode's clock or paused state");
        });
        foreach (string provider in new[] { "tmdb", "douban" })
            yield return Test($"{provider} confirmed movie keeps live playback state across source file changes and retry", async () =>
            {
                await using var rig = new FilmPlaybackRig();
                var card = Card("movie");
                var movie = card.PlaybackContext!.Target with { Id = provider + ":movie:100", MetadataProvider = provider };
                await rig.OpenAsync(card, new(movie, Metadata: Metadata(movie, card.Poster)));
                rig.Runtime.Attempt("movie-original", Candidate("movie-original", episode: null));
                await Until(() => Called(rig, "media.started", "movie-original") && rig.Session.HasOutput);
                await rig.Session.SeekAsync(120); await rig.Session.SetPausedAsync(true);
                await rig.Session.RateAsync(1.5); await rig.Session.AspectAsync("4:3");
                foreach (string id in new[] { "movie-file", "movie-other-file", "movie-retry" })
                {
                    string candidateId = id == "movie-retry" ? "movie-other-file" : id;
                    string action = id == "movie-retry" ? "media.retry" : "select.candidate";
                    var sourceTarget = new FilmPlaybackTarget(movie.Id, "source", movie.Title,
                        Source: FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0, lineId = 0, candidateId }));
                    var entered = Gate(); var finish = Gate();
                    var donor = rig.Native.Scope;
                    rig.Native.Preparing = async _ => { entered.TrySetResult(); await finish.Task; };
                    rig.Runtime.Intercept = (method, input, _) =>
                    {
                        if (method == "film.session.command" && FilmJson.Text(input, "action") == action)
                            rig.Runtime.Attempt(id, Candidate(candidateId, episode: null), target: sourceTarget);
                        return Task.FromResult(rig.Runtime.Handle(method, input));
                    };
                    try
                    {
                        if (action == "media.retry") await rig.Page.RetryMediaAsync();
                        else await rig.Page.CommandAsync(action, new { candidateId });
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Check(rig.Native.Intents[^1].PreserveFrom == donor && rig.Page.ActiveTarget.Kind == "source" &&
                            rig.Page.ShowMovieCandidates && rig.Page.ActiveTarget.MetadataProvider is null,
                            "the selected real movie metadata source survives the exact execution kind without guessing from its ID");
                        await rig.Session.SeekAsync(rig.Native.Position + 7);
                        double position = rig.Native.Position;
                        finish.TrySetResult();
                        await Until(() => rig.Native.SettledIdentity == id && Called(rig, "media.started", id));
                        Check(rig.Session.HasOutput && rig.Native.Position == position && rig.Native.Paused &&
                            rig.Native.Rate == 1.5 && rig.Native.Aspect == "4:3",
                            "movie file replacement and exact retry preserve the donor's live clock and controls");
                    }
                    finally { finish.TrySetResult(); }
                }
                Check(rig.Native.Commits == 4 && !Called(rig, "media.failed"),
                    "each accepted occurrence commits once without a false media failure");
            });
        yield return Test("source files without selected movie metadata do not infer continuation from title or content ID", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            var source = new FilmPlaybackTarget("tmdb:movie:100", "source", "电影同名",
                Source: FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0, lineId = 0 }));
            var candidate = Candidate("unknown-file", episode: null);
            await rig.OpenAsync(new(source.Id, source.Title, "", ""), new(source, Sources: [candidate.GetProperty("source")]));
            rig.Runtime.Change(root =>
            {
                root["metadataSources"] = JsonNode.Parse(FilmJson.Encode(new[]
                { new { id = "tmdb:100", provider = "tmdb", metadataId = "100", kind = "movie" } }).GetRawText());
                root["metadataSourceId"] = null;
            });
            rig.Runtime.Attempt("unknown-file", candidate);
            await Until(() => Called(rig, "media.started", "unknown-file") && rig.Session.HasOutput);
            await rig.Session.SeekAsync(90); await rig.Session.SetPausedAsync(true);
            rig.Runtime.Attempt("unknown-other", Candidate("unknown-other", episode: null), target: source with
            { Source = FilmJson.Encode(new { key = "cat:fixture:site:1", detailId = 0, lineId = 0, candidateId = "unknown-other" }) });
            await Until(() => rig.Native.SettledIdentity == "unknown-other" && Called(rig, "media.started", "unknown-other"));
            Check(!rig.Native.Intents[^1].PreserveCurrent && rig.Native.Position == 0 && !rig.Native.Paused &&
                !rig.Page.ShowMovieCandidates && rig.Native.Commits == 2 && !Called(rig, "media.failed"),
                "an unselected movie row, movie-like ID and title are not evidence that two coordinate-free files share a movie clock");
        });
        yield return Test("prepare failure after native lease disposal reports the exact pending attempt without destroying the donor", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            rig.Native.Preparing = async _ =>
            {
                await rig.Native.PendingLease!.DisposeAsync();
                throw new FilmPlaybackException(PlaybackError.OpenFailed);
            };
            rig.Runtime.Attempt("bad", Candidate("bad"));
            await Until(() => Called(rig, "media.failed", "bad"));
            await Until(() => rig.Runtime.Released.Contains("bad"));
            Check(rig.Native.Identity == "first" && rig.Session.HasOutput && rig.Native.Commits == 1 &&
                !Called(rig, "media.failed", "first"), "pending failure leaves the previous accepted output untouched");
            var calls = rig.Runtime.Calls.ToList();
            Check(calls.FindIndex(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "bad") <
                calls.FindIndex(call => FilmJson.Text(call.Parameters, "action") == "media.failed" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "bad"), "release can precede the truthful failure receipt");
        });
        yield return Test("rapid replacement fences a late provisional output and releases its resource", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            var entered = Gate(); var finish = Gate();
            rig.Native.Preparing = async _ => { entered.TrySetResult(); await finish.Task; };
            try
            {
                rig.Runtime.Attempt("late", Candidate("late"));
                await entered.Task;
                await rig.Page.CommandAsync("switch");
                rig.Runtime.Attempt("winner", Candidate("winner"));
                rig.Native.Preparing = null; finish.TrySetResult();
                await Until(() => Called(rig, "media.started", "winner"));
                Check(rig.Native.Commits == 2 && rig.Native.Identity == "winner" &&
                    !Called(rig, "media.started", "late") && !Called(rig, "media.failed", "late") &&
                    rig.Runtime.Released.Contains("late"), "cancellation is not failure and late output has no publication right");
            }
            finally { finish.TrySetResult(); }
        });
        foreach (string outcome in new[] { "STALE_SESSION", "STORAGE_ERROR", "accepted" })
            yield return Test($"block {outcome} waits for admission without prematurely revoking another pending receipt", async () =>
            {
                await using var rig = new FilmPlaybackRig();
                await rig.OpenAsync(); await Play(rig);
                await rig.Session.SeekAsync(137); await rig.Session.SetPausedAsync(true);
                var entered = Gate(); var finish = Gate(); var prepared = Gate();
                var requested = Gate(); var response = Gate();
                CancellationToken pendingToken = default;
                rig.Native.Preparing = async token =>
                {
                    pendingToken = token; entered.TrySetResult();
                    await finish.Task; prepared.TrySetResult();
                };
                rig.Runtime.Intercept = async (method, input, _) =>
                {
                    if (method == "film.session.command" && FilmJson.Text(input, "action") == "block")
                    {
                        Check(FilmJson.Text(input, "attemptId") == "first", "confirm exactly the playing donor");
                        requested.TrySetResult(); await response.Task;
                        if (outcome != "accepted") return FilmRuntimeProbe.Failure(outcome);
                        rig.Runtime.Attempt("after-block", Candidate("after-block"));
                    }
                    return rig.Runtime.Handle(method, input);
                };
                try
                {
                    rig.Runtime.Attempt("pending-block", Candidate("pending-block")); await entered.Task;
                    var blocking = rig.Page.CommandAsync("block", new { attemptId = "first" });
                    await requested.Task;
                    Check(!pendingToken.IsCancellationRequested && !rig.Runtime.Released.Contains("pending-block") &&
                        rig.Native.SettledIdentity == "first" && rig.Native.Paused && rig.Native.Position == 137,
                        "a confirmation awaiting its durable decision cannot revoke an admitted candidate or alter its donor");
                    finish.TrySetResult(); await prepared.Task;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Check(rig.Native.Commits == 1 && !rig.Runtime.Released.Contains("pending-block") &&
                        !Called(rig, "media.started", "pending-block"),
                        "the ready candidate waits at the commit boundary, not by releasing and reacquiring its receipt");
                    rig.Native.Preparing = null;
                    response.TrySetResult();
                    if (outcome == "accepted") await blocking;
                    else await Error(outcome, () => blocking);
                    string winner = outcome == "accepted" ? "after-block" : "pending-block";
                    await Until(() => rig.Native.SettledIdentity == winner && Called(rig, "media.started", winner));
                    Check(rig.Native.Commits == 2 && rig.Native.Paused && rig.Native.Position == 137 &&
                        !Called(rig, "media.failed") && rig.Native.Stops == 0 &&
                        rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt" &&
                            FilmJson.Text(call.Parameters, "attemptId") == "pending-block") == 1,
                        "the admitted outcome preserves same-episode intent, with no retry or false failure");
                    Check(rig.Runtime.Released.Contains("pending-block") == (outcome == "accepted"),
                        "only an accepted replacement snapshot retires the preceding pending receipt");
                }
                finally { finish.TrySetResult(); response.TrySetResult(); }
            });
        yield return Test("block admission also fences a ready candidate already queued behind another RPC", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            var browsing = Gate(); var browseReply = Gate();
            var preparing = Gate(); var finish = Gate(); var prepared = Gate();
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.command")
                {
                    if (FilmJson.Text(input, "action") == "browse")
                    { browsing.TrySetResult(); await browseReply.Task; }
                    if (FilmJson.Text(input, "action") == "block")
                    {
                        Check(rig.Native.Commits == 1 && !rig.Runtime.Released.Contains("queued"),
                            "a command admitted after native preparation still precedes its actual ownership swap");
                        return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                    }
                }
                return rig.Runtime.Handle(method, input);
            };
            rig.Native.Preparing = async _ =>
            { preparing.TrySetResult(); await finish.Task; prepared.TrySetResult(); };
            try
            {
                var browse = rig.Page.CommandAsync("browse", new { sourceId = (string?)null });
                await browsing.Task;
                rig.Runtime.Attempt("queued", Candidate("queued")); await preparing.Task;
                finish.TrySetResult(); await prepared.Task;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var block = rig.Page.CommandAsync("block", new { attemptId = "first" });
                browseReply.TrySetResult();
                await browse; await Error("STORAGE_ERROR", () => block);
                await Until(() => Called(rig, "media.started", "queued"));
                Check(rig.Native.Commits == 2 && !Called(rig, "media.failed") &&
                    !rig.Runtime.Released.Contains("queued"), "rejecting the block permits the same waiting input once");
            }
            finally { finish.TrySetResult(); browseReply.TrySetResult(); }
        });
        foreach (string outcome in new[] { "STALE_SESSION", "STORAGE_ERROR", "accepted" })
            yield return Test($"block {outcome} arriving during native commit defers without consuming the candidate", async () =>
            {
                await using var rig = new FilmPlaybackRig();
                await rig.OpenAsync(); await Play(rig);
                await rig.Session.SeekAsync(137); await rig.Session.SetPausedAsync(true);
                var aligning = Gate(); var aligned = Gate(); var requested = Gate(); var response = Gate();
                int alignments = 0;
                rig.Native.Committing = async _ =>
                { alignments++; aligning.TrySetResult(); await aligned.Task; };
                rig.Runtime.Intercept = async (method, input, _) =>
                {
                    if (method == "film.session.command" && FilmJson.Text(input, "action") == "block")
                    {
                        Check(rig.Native.Identity == "first" && rig.Native.Commits == 1 &&
                            rig.Native.Position == 137 && rig.Native.Paused &&
                            !rig.Runtime.Released.Contains("aligning"),
                            "native alignment must yield the lock, not the candidate or its playing donor");
                        requested.TrySetResult(); await response.Task;
                        if (outcome != "accepted") return FilmRuntimeProbe.Failure(outcome);
                        rig.Runtime.Attempt("after-alignment-block", Candidate("after-alignment-block"));
                    }
                    return rig.Runtime.Handle(method, input);
                };
                try
                {
                    rig.Runtime.Attempt("aligning", Candidate("aligning")); await aligning.Task;
                    var block = rig.Page.CommandAsync("block", new { attemptId = "first" });
                    Check(!requested.Task.IsCompleted, "the RPC waits until native commit defers");
                    aligned.TrySetResult(); await requested.Task;
                    Check(rig.Native.Commits == 1 && !Called(rig, "media.started", "aligning") &&
                        !Called(rig, "media.failed"), "a temporary business wait is not output or media failure");
                    response.TrySetResult();
                    if (outcome == "accepted") await block;
                    else await Error(outcome, () => block);
                    string winner = outcome == "accepted" ? "after-alignment-block" : "aligning";
                    await Until(() => rig.Native.SettledIdentity == winner && Called(rig, "media.started", winner));
                    Check(rig.Native.Commits == 2 && rig.Native.Position == 137 && rig.Native.Paused &&
                        alignments == 2 && !Called(rig, "media.failed") &&
                        rig.Native.Intents.Count(value => value.Identity == "aligning") == 1 &&
                        rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt" &&
                            FilmJson.Text(call.Parameters, "attemptId") == "aligning") == 1,
                        "rejected blocks realign the same prepared receipt; accepted blocks alone replace it");
                    Check(rig.Runtime.Released.Contains("aligning") == (outcome == "accepted"),
                        "the still-authorized B lease cannot be retired by an unsuccessful confirmation");
                }
                finally { aligned.TrySetResult(); response.TrySetResult(); }
            });
        yield return Test("a released donor is not reused for same-episode preservation before its terminal callback arrives", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            var entered = Gate(); var finish = Gate();
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.media.release" && FilmJson.Text(input, "attemptId") == "first")
                { entered.TrySetResult(); await finish.Task; }
                return rig.Runtime.Handle(method, input);
            };
            try
            {
                var terminal = rig.Native.TerminalAsync(false); await entered.Task;
                rig.Runtime.Attempt("replacement", Candidate("replacement"));
                await Until(() => Called(rig, "media.started", "replacement"));
                Check(!rig.Native.Intents[^1].PreserveCurrent, "a dead resource cannot be a live-clock donor merely because its coordinates match");
                finish.TrySetResult(); await terminal;
                await Until(() => rig.Native.SettledIdentity == "replacement" && rig.Session.HasOutput);
                Check(!Called(rig, "media.failed", "replacement"), "the retired native occurrence cannot fail its successor");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("closing owns a late admitted session open receipt and releases it before completing", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            var entered = Gate(); var finish = Gate();
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.open") { entered.TrySetResult(); await finish.Task; }
                return rig.Runtime.Handle(method, input);
            };
            try
            {
                var opening = rig.OpenAsync(); await entered.Task;
                var closing = rig.Session.CloseSessionAsync();
                Check(!closing.IsCompleted, "a cancellation is not an unknown session's release receipt");
                finish.TrySetResult(); await Task.WhenAll(opening, closing);
                Check(!rig.Session.HasSession && !rig.Session.HasOutput && rig.Native.Commits == 0 &&
                    rig.Runtime.Calls.Count(call => call.Method == "film.session.release") == 1,
                    "the late session id is released once and never reaches the retired page");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("leaving during a module's admitted open cannot navigate back after its late receipt", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await using var module = new FilmCapability(new EmptyHome(), rig.Session, rig.Library);
            var entered = Gate(); var finish = Gate();
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.open") { entered.TrySetResult(); await finish.Task; }
                return rig.Runtime.Handle(method, input);
            };
            try
            {
                var card = Card();
                var opening = module.OpenPlaybackAsync(card, card.PlaybackContext!); await entered.Task;
                var leaving = module.NavigateAsync(FilmCapability.HomeRoute, default);
                finish.TrySetResult(); await Task.WhenAll(opening, leaving);
                Check(module.IsHome && !rig.Session.HasSession &&
                    rig.Runtime.Calls.Count(call => call.Method == "film.session.release") == 1,
                    "a retired selection cannot resurrect navigation or strand its admitted business session");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("runtime epoch revocation reports disconnection and drains its former native authority", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            rig.Runtime.Owner.Cancel();
            await Until(() => rig.Page.ConnectionFailed && !rig.Session.HasOutput && rig.Native.Stops == 1);
            await rig.Session.CloseSessionAsync();
            Check(!Called(rig, "media.failed") && !rig.Session.HasSession, "revocation is not a media candidate failure or reusable paused output");
        });
        yield return Test("explicit reconnect after runtime revocation preserves the current target on the new epoch", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await using var next = new FilmRuntimeProbe();
            await rig.OpenAsync(); await Play(rig);
            var original = rig.Runtime.Calls.Single(call => call.Method == "film.session.open").Parameters;
            rig.Runtime.Owner.Cancel();
            await Until(() => rig.Page.ConnectionFailed && rig.Native.Stops == 1);
            rig.Runtime.Intercept = (_, _, _) => throw new MeowFilm.Core.Runtime.BridgeException("CLOSED");
            rig.Capture = next.CaptureConnectionAsync;
            await rig.Page.ReconnectAsync();
            Check(rig.Session.HasSession && !rig.Page.ConnectionFailed && !rig.Session.HasOutput,
                "explicit retry acquires the new epoch without reusing the old output");
            var reopened = next.Calls.Single(call => call.Method == "film.session.open").Parameters;
            Check(FilmJson.Text(FilmJson.Member(reopened, "target"), "id") == rig.Page.Request.Target.Id &&
                FilmJson.Integer(FilmJson.Member(reopened, "target"), "episode") == 1 &&
                JsonElement.DeepEquals(FilmJson.Member(original, "metadata"), FilmJson.Member(reopened, "metadata")) &&
                FilmJson.Array(reopened, "sources").Length == 0 &&
                FilmJson.Array(reopened, "results").Length == 0 && FilmJson.Array(reopened, "search").Length == 0 &&
                !next.Calls.Any(call => call.Method == "film.session.snapshot"),
                "a replacement gets the selected episode and metadata, not an old session id or a title-only request");
            next.Attempt("new-epoch", Candidate("new-epoch"));
            await Until(() => rig.Native.Identity == "new-epoch" && rig.Session.HasOutput);
        });
        yield return Test("explicit reconnect retries a failed capture without losing its selected target", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            rig.Capture = _ => throw new MeowFilm.Core.Runtime.BridgeException("CLOSED");
            await Error("CLOSED", () => rig.OpenAsync(Card(number: 201)));
            Check(rig.Page.ConnectionFailed && !rig.Session.HasSession, "initial capture failure remains observable");
            rig.Capture = null;
            await rig.Page.ReconnectAsync();
            Check(rig.Session.HasSession && rig.Page.ActiveTarget.Id == "tmdb:tv:201" &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.open") == 1,
                "the visible retry has a real fresh-open consumer");
        });
        yield return Test("closing a pending prepare waits for physical discard without publishing or reporting cancellation as failure", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            var entered = Gate(); var finish = Gate();
            rig.Native.Preparing = async _ => { entered.TrySetResult(); await finish.Task; };
            try
            {
                rig.Runtime.Attempt("pending", Candidate("pending")); await entered.Task;
                var close = rig.Session.CloseSessionAsync();
                Check(!close.IsCompleted && !rig.Page.CanPlay, "close revokes UI commands but still owns the running input");
                finish.TrySetResult(); await close;
                Check(rig.Native.Commits == 0 && rig.Runtime.Released.Contains("pending") && !Called(rig, "media.failed"),
                    "late preparation is physically discarded and never mislabeled a failed candidate");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("repeated source revocation interrupts one scope and waits for one actual stop", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            var entered = Gate(); var finish = Gate();
            rig.Native.Stopping = async () => { entered.TrySetResult(); await finish.Task; };
            try
            {
                rig.Session.SourcesChanged([]); rig.Session.SourcesChanged([]);
                await entered.Task;
                Check(rig.Native.AcceptedLease!.Revoked.IsCancellationRequested && !rig.Session.HasOutput,
                    "source revocation invalidates the live lease before its physical stop finishes");
                var close = rig.Session.CloseSessionAsync();
                Check(!close.IsCompleted, "close waits for the same owned physical stop");
                finish.TrySetResult(); await close;
                Check(rig.Native.Stops == 1 && !Called(rig, "media.failed") &&
                    rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release") == 1,
                    "ordinary stop and authority revocation are not media failure or duplicate resource release");
            }
            finally { finish.TrySetResult(); }
        });
        foreach (bool ended in new[] { false, true })
            yield return Test((ended ? "ended" : "failed") + " survives release hiding the snapshot and reports final progress with its original identity", async () =>
            {
                await using var rig = new FilmPlaybackRig();
                await rig.OpenAsync(); await Play(rig);
                await rig.Session.SeekAsync(233);
                await rig.Native.TerminalAsync(ended);
                string action = ended ? "media.ended" : "media.failed";
                await Until(() => Called(rig, action, "first") && rig.Session.MediaIdentity is null);
                var calls = rig.Runtime.Calls.ToList();
                int release = calls.FindIndex(call => call.Method == "film.session.media.release");
                int terminal = calls.FindIndex(call => FilmJson.Text(call.Parameters, "action") == action);
                Check(release >= 0 && terminal > release && calls.Skip(release + 1).Take(terminal - release - 1)
                    .Any(call => FilmJson.Text(call.Parameters, "action") == "media.progress" &&
                        FilmJson.Text(call.Parameters, "attemptId") == "first" && FilmJson.Number(call.Parameters, "position") == 233),
                    "a hidden receipt is not lost terminal ownership or a reusable address");
            });
        yield return Test("ordinary stop awaits the actual native drain and old pages cannot command a newly opened identity", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            var entered = Gate(); var finish = Gate();
            rig.Native.Stopping = async () => { entered.TrySetResult(); await finish.Task; };
            try
            {
                var stop = rig.Session.CommandAsync("stop"); await entered.Task;
                Check(!stop.IsCompleted, "business stop acknowledgement does not stand in for native resource release");
                finish.TrySetResult(); await stop;
                Check(!rig.Session.HasOutput && !Called(rig, "media.failed"), "the stop is truthful and not a candidate failure");
                var oldPage = rig.Page; rig.Native.Stopping = null;
                await rig.OpenAsync(Card(number: 200));
                int before = rig.Runtime.Calls.Count;
                await Error("STALE_SESSION", () => oldPage.CommandAsync("play"));
                Check(rig.Runtime.Calls.Count == before && !oldPage.CanPlay, "retired page commands never target the new session");
            }
            finally { finish.TrySetResult(); }
        });
        yield return Test("failed final progress does not prevent an accepted stop from releasing its actual output", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            rig.Runtime.Intercept = (method, input, _) => Task.FromResult(
                method == "film.session.command" && FilmJson.Text(input, "action") == "media.progress"
                    ? FilmRuntimeProbe.Failure("STORAGE_FAILED") : rig.Runtime.Handle(method, input));
            await Error("STORAGE_FAILED", () => rig.Session.CommandAsync("stop"));
            Check(Called(rig, "stop") && rig.Native.Stops == 1 && rig.Runtime.Released.Contains("first") &&
                !rig.Session.HasOutput && !Called(rig, "media.failed"),
                "save failure stays observable while physical stop and precise lease release still complete");
        });
        yield return Test("snapshot reconnect keeps one output and fences lower sequences without a second receipt", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            rig.Runtime.DisconnectEvents(); await Until(() => rig.Page.ConnectionFailed);
            await rig.Session.ReconnectAsync();
            Check(!rig.Page.ConnectionFailed && rig.Native.Commits == 1 &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.attempt") == 1,
                "observer recovery does not replay an actual play");
            var latest = rig.Runtime.Value;
            rig.Runtime.Intercept = (method, input, _) => Task.FromResult(method == "film.session.snapshot"
                ? FilmRuntimeProbe.Ok(Snapshot(rig.Runtime.SessionId, 0, rig.Runtime.Target with { Title = "过期资料" },
                    FilmJson.Encode(new { title = "过期资料" }))) : rig.Runtime.Handle(method, input));
            await rig.Session.ReconnectAsync();
            Check(rig.Page.Title != "过期资料" && rig.Native.Commits == 1, "a stale sequence cannot overwrite current metadata or output");
            rig.Runtime.Intercept = null; rig.Runtime.Value = latest;
        });
        yield return Test("a visible PiP owner retains observation and native same-episode controls plus next-episode zero", async () =>
        {
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync(); await Play(rig);
            await rig.Session.SeekAsync(81); await rig.Session.SetPausedAsync(true);
            await rig.Session.RateAsync(2); await rig.Session.AspectAsync("1:1");
            var owner = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, Content = rig.Native.View
            };
            try
            {
                owner.Show(); rig.Page.RetainOwner(owner);
                await rig.Session.LeaveAsync(retainedOwner: true);
                Check(rig.Session.HasSession && rig.Session.CanRetainMedia &&
                    rig.Runtime.Calls.Any(call => call.Method == "film.session.release" && FilmJson.Bool(call.Parameters, "keepMedia")),
                    "the visible PiP owner keeps the same stable native owner and snapshots");
                await Play(rig, "pip-same");
                Check(rig.Runtime.Released.Contains("first") && rig.Native.Position == 81 && rig.Native.Paused &&
                    rig.Native.Rate == 2 && rig.Native.Aspect == "1:1" && rig.Session.OwnsNativeMedia &&
                    rig.Session.CanRetainMedia && rig.Session.Position == 81 && !rig.Session.IsPlaying &&
                    ReferenceEquals(owner.Content, rig.Session.MediaView),
                    "observed same-episode replacement preserves native clock and controls in the same retained view");
                await Play(rig, "pip-next", 2);
                Check(rig.Runtime.Released.Contains("pip-same") && rig.Native.Position == 0 && !rig.Native.Paused &&
                    rig.Native.Rate == 2 && rig.Native.Aspect == "1:1" && rig.Session.CanRetainMedia,
                    "a new observed episode keeps the selected controls without inheriting progress");
                await rig.Session.CommandAsync("stop");
                Check(rig.Native.Stops == 1 && rig.Runtime.Released.Contains("pip-next") &&
                    !rig.Session.HasOutput && !rig.Session.CanRetainMedia && !Called(rig, "media.failed"),
                    "stop drains the retained native occurrence and its exact receipt");
            }
            finally { rig.Page.RetainOwner(null); owner.Close(); }
        });
        yield return Test("native direct output releases its exact receipt after music takeover and stale film controls cannot touch music", async () =>
        {
            var entered = Gate(); var finish = Gate();
            await using var rig = new FilmPlaybackRig();
            await rig.OpenAsync();
            rig.Runtime.Controlled = false;
            await Play(rig);
            rig.Runtime.Intercept = async (method, input, _) =>
            {
                if (method == "film.session.media.release")
                {
                    Check(FilmJson.Text(input, "attemptId") == "first", "takeover releases the exact Film occurrence");
                    entered.TrySetResult(); await finish.Task;
                }
                return rig.Runtime.Handle(method, input);
            };
            try
            {
                await rig.Native.TakeMusicAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!rig.Session.HasOutput && !rig.Runtime.Released.Contains("first"),
                    "retiring the old Film output does not fabricate its business receipt release");
                finish.TrySetResult();
                await Until(() => !rig.Session.HasSession);
                int musicVolume = rig.Native.Volume, controls = rig.Native.Controls.Count;
                rig.Session.Volume = 12;
                await Error("STALE_SESSION", () => rig.Session.SetPausedAsync(true));
                await Error("STALE_SESSION", () => rig.Session.SeekAsync(81));
                Check(rig.Native.Volume == musicVolume && rig.Native.Controls.Count == controls &&
                    !rig.Session.HasOutput && !rig.Session.CanRetainMedia && !Called(rig, "media.failed") &&
                    rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                        FilmJson.Text(call.Parameters, "attemptId") == "first") == 1,
                    "late Film controls cannot affect the replacing owner or duplicate the former receipt release");
            }
            finally { finish.TrySetResult(); }
        });
    }

    private static async Task RetiredCommandAsync(string action, bool fails)
    {
        await using var rig = new FilmPlaybackRig();
        await rig.OpenAsync();
        var oldPage = rig.Page; var oldTarget = oldPage.ActiveTarget;
        string oldStatus = oldPage.StatusText; string? oldSource = oldPage.SourceId;
        int notifications = 0;
        rig.Session.OperationFailed += (_, _) => notifications++;
        var entered = Gate(); var finish = Gate(); var releasing = Gate(); var released = Gate();
        rig.Runtime.Intercept = async (method, input, _) =>
        {
            if (method == "film.session.command" && FilmJson.Text(input, "action") == action)
            {
                entered.TrySetResult(); await finish.Task;
                if (fails) return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                if (action == "browse")
                    rig.Runtime.Change(root => root["browsing"]!["sourceId"] = "cat:fixture:site:1");
                else if (action == "play") rig.Runtime.Attempt("retired-play", Candidate("retired-play"));
                return rig.Runtime.Handle(method, input);
            }
            if (method == "film.session.release") { releasing.TrySetResult(); await released.Task; }
            return rig.Runtime.Handle(method, input);
        };
        try
        {
            object? parameters = action switch
            {
                "browse" => new { sourceId = "cat:fixture:site:1" },
                "select.episode" => new { episode = new { season = 1, episode = 2, globalEpisode = 2 } },
                _ => null
            };
            var command = oldPage.CommandAsync(action, parameters);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = rig.Session.LeaveAsync(retainedOwner: false);
            Check(!close.IsCompleted && !rig.Session.IsCurrentPage(oldPage) &&
                !rig.Runtime.Calls.Any(call => call.Method == "film.session.release"),
                "retirement revokes UI immediately but cannot release before the admitted command returns");
            finish.TrySetResult(); await command;
            await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!close.IsCompleted && oldPage.ActiveTarget == oldTarget && oldPage.StatusText == oldStatus &&
                oldPage.SourceId == oldSource && notifications == 0 && rig.Native.Intents.Count == 0 &&
                !Called(rig, "media.started") && !Called(rig, "media.failed"),
                "neither a late authoritative snapshot nor its business error can publish into the retired page");
            released.TrySetResult(); await close;
            Check(rig.Runtime.Calls.Count(call => call.Method == "film.session.release") == 1,
                "close joins the actual session release once");
            await rig.OpenAsync(Card(number: 201));
            Check(rig.Page.ActiveTarget.Id == "tmdb:tv:201" && rig.Native.Intents.Count == 0 &&
                oldPage.ActiveTarget == oldTarget && oldPage.StatusText == oldStatus && notifications == 0,
                "reopening cannot consume the old command's response or old attempt");
        }
        finally { finish.TrySetResult(); released.TrySetResult(); }
    }

    private static async Task RevokedAttemptAsync(bool changedGeneration, bool fails, bool releaseFails)
    {
        var rig = new FilmPlaybackRig();
        var entered = Gate(); var finish = Gate(); var releasing = Gate(); var released = Gate();
        CancellationToken admitted = default;
        try
        {
            await rig.OpenAsync();
            rig.Runtime.Intercept = async (method, input, token) =>
            {
                if (method == "film.session.attempt")
                {
                    var response = rig.Runtime.Handle(method, input);
                    admitted = token; entered.TrySetResult(); await finish.Task;
                    return fails ? FilmRuntimeProbe.Failure("STORAGE_ERROR") : response;
                }
                if (method == "film.session.media.release")
                {
                    releasing.TrySetResult(); await released.Task;
                    if (releaseFails) return FilmRuntimeProbe.Failure("STORAGE_ERROR");
                }
                return rig.Runtime.Handle(method, input);
            };
            var candidate = Candidate("source-late");
            rig.Runtime.Attempt("source-late", candidate); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = JsonNode.Parse(candidate.GetProperty("source").GetRawText())!.AsObject();
            replacement["generation"] = 2;
            rig.Session.SourcesChanged(changedGeneration ? [FilmJson.Encode(replacement)] : []);
            Check(admitted.IsCancellationRequested, "source revocation reaches the already-admitted receipt RPC without an Input");
            var close = rig.Session.CloseSessionAsync(); var duplicate = rig.Session.CloseSessionAsync();
            Check(ReferenceEquals(close, duplicate) && !close.IsCompleted && rig.Native.Intents.Count == 0,
                "close joins the exact late receipt, not merely its cancellation token");
            finish.TrySetResult();
            if (!fails)
            {
                await releasing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!close.IsCompleted && !duplicate.IsCompleted && rig.Native.Intents.Count == 0,
                    "late receipt cleanup has an independently awaited physical release");
            }
            released.TrySetResult();
            if (releaseFails)
            {
                await Error("STORAGE_ERROR", () => close); await Error("STORAGE_ERROR", () => duplicate);
                await Error("STORAGE_ERROR", () => rig.Session.CloseSessionAsync());
            }
            else await Task.WhenAll(close, duplicate);
            Check(rig.Native.Intents.Count == 0 && rig.Native.Commits == 0 && !Called(rig, "media.started") &&
                !Called(rig, "media.failed") && !rig.Session.HasSession &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.media.release" &&
                    FilmJson.Text(call.Parameters, "attemptId") == "source-late") == (fails ? 0 : 1) &&
                rig.Runtime.Calls.Count(call => call.Method == "film.session.release") == 1,
                "revocation is not media failure; only a returned receipt has an exact resource release");
        }
        finally
        {
            finish.TrySetResult(); released.TrySetResult();
            if (releaseFails) await Error("STORAGE_ERROR", () => rig.DisposeAsync().AsTask());
            else await rig.DisposeAsync();
        }
    }

    private sealed class EmptyHome : IFilmHomeSource
    {
        public Task<IReadOnlyList<FilmCardItem>> LoadAsync(FilmSectionDefinition section, int start, int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FilmCardItem>>([]);
        public void Dispose() { }
    }
}
