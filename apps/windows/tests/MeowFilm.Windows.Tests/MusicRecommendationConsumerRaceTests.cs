using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

/// <summary>UI-port races only; does not repeat the real NativeApplication/provider consumer suite.</summary>
public static class MusicRecommendationConsumerRaceTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.RecommendationConsumerRaceCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> RecommendationConsumerRaceCases()
    {
        yield return Case("recommendation consumer old Stop draining recovery cannot stop or clear a newer selection",
            () => RecommendationRetirementRace(restoreOnly: false));
        yield return Case("recommendation consumer old RestoreOnly draining recovery cannot stop or replace a newer selection",
            () => RecommendationRetirementRace(restoreOnly: true));
        yield return Case("recommendation consumer reentrant UI updates and elapsed time never create a new playback observation",
            RecommendationRetryObservationRace);
        yield return Case("recommendation consumer committed FM configuration rejects old snapshots and retired action keys",
            RecommendationCommittedFmRace);
        yield return Case("recommendation consumer candidate replacement rejects old tokens before a native choose command",
            RecommendationCandidateRace);
        yield return Case("recommendation consumer heartbeat return refreshes ABEC and retires proof without restarting playing media",
            () => RecommendationSourceReturn("heartbeat", paused: false));
        yield return Case("recommendation consumer heartbeat return preserves paused media position without Stop Open or Resume",
            () => RecommendationSourceReturn("heartbeat", paused: true));
        yield return Case("recommendation consumer active recommendation return preserves playing ABEC media and latest loop",
            () => RecommendationSourceReturn("queue", paused: false));
        yield return Case("recommendation consumer active recommendation return preserves paused ABEC media and latest loop",
            () => RecommendationSourceReturn("queue", paused: true));
    }

    private static async Task RecommendationSourceReturn(string kind, bool paused)
    {
        await using var f = await RecommendationRaceScene.Create();
        f.App.UseRecommendationCoordinator(kind);
        True((await f.App.ExecuteAsync("music.queue.replace", new {
            songs = new[] { "2", "1", "3" }.Select(RecommendationTest.Song).ToArray(), index = 1,
            context = new { sourceTitle = "原歌单", sourcePlatform = "wy", sourceType = "playlist", sourceId = "100",
                queueSource = new { id = "100", title = "原歌单", kind = "playlist", platform = "wy" } }
        })).GetProperty("ok").GetBoolean(), "establish original playlist");
        var original = await f.App.Queue();
        string seed = original.GetProperty("entries")[1].GetProperty("id").GetString()!;
        True(await f.App.Select(original), "original B reaches the real module selection consumer");
        f.Playback.Start(); await f.Module.SeekAsync(14);
        if (paused) await f.Playback.PauseAsync();
        await Drain();
        int seedOpens = f.Playback.Opens.Count, seedStops = f.Playback.Stopped.Count, seedResumes = f.Playback.Resumes;
        var started = kind == "heartbeat"
            ? await f.App.ExecuteAsync("music.recommendations.start",
                new { kind, platform = "wy", entry = "current", songId = "1", playlistId = "100", seedEntryId = seed })
            : await f.App.ExecuteAsync("music.recommendations.start",
                new { kind, platform = "wy", seedEntryId = seed, restore = true });
        Equal("ready", started.GetProperty("status").GetString());
        Equal(seed, (await f.App.Queue()).GetProperty("currentId").GetString());
        Equal(seed, f.Playback.Identity);
        Equal(14d, f.Playback.PositionSeconds);
        True(f.Playback.IsPlaying == !paused && f.Playback.HasMedia, "entering preserves current media and intent");
        Equal(seedOpens, f.Playback.Opens.Count); Equal(seedStops, f.Playback.Stopped.Count); Equal(seedResumes, f.Playback.Resumes);
        if (kind == "queue")
            True((await f.App.ExecuteAsync("music.recommendations.continue",
                new { id = started.GetProperty("id").GetString(), enabled = false })).GetProperty("ok").GetBoolean(), "disable automatic fetch");
        await f.Module.ChangeModeAsync(2);
        var recommendations = await f.App.Queue();
        string current = recommendations.GetProperty("entries")[2].GetProperty("id").GetString()!;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueue(JsonElement value)
        {
            if (value.TryGetProperty("currentId", out var entry) && entry.GetString() == current) observed.TrySetResult();
        }
        f.App.Changed += OnQueue;
        try
        {
            True((await f.App.ExecuteAsync("music.queue.select", new { id = current })).GetProperty("ok").GetBoolean(), "select E");
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { f.App.Changed -= OnQueue; }
        var playingQueue = await f.App.Queue();
        True(await f.App.Select(playingQueue), "current E reaches the real module selection consumer");
        f.Playback.Start();
        await f.Playback.SeekAsync(43);
        if (paused) await f.Playback.PauseAsync();
        await Drain();
        True(f.Module.CanReturnRecommendation && f.Module.HasActiveRecommendation, "source return is available after the committed snapshot");
        Equal("随机播放", f.Module.PlaybackModeText);
        var proof = f.App.FeedbackProbe.Selection?.Recommendation;
        True(proof is not null && proof.Value.Kind == kind && proof.IsCurrent(), "UI feedback carries live source occurrence proof");
        int opens = f.Playback.Opens.Count, stops = f.Playback.Stopped.Count, resumes = f.Playback.Resumes;
        int resolves = f.App.ResolveCalls, callbacks = f.App.CoordinatorSelections;
        int commands = f.App.RecommendationCalls.Count, feedbackStarts = f.App.FeedbackProbe.Starts, feedbackFinishes = f.App.FeedbackProbe.Finishes;

        await f.Module.ReturnRecommendationAsync();
        await Drain();

        var restored = await f.App.Queue();
        True(restored.GetProperty("entries").EnumerateArray().Select(e => e.GetRawText()).SequenceEqual(
            new[] { original.GetProperty("entries")[0], original.GetProperty("entries")[1],
                playingQueue.GetProperty("current"), original.GetProperty("entries")[2] }.Select(e => e.GetRawText())), "ABC returns as ABEC");
        Equal(current, restored.GetProperty("currentId").GetString());
        Equal(2, restored.GetProperty("mode").GetInt32());
        Equal(original.GetProperty("source").GetRawText(), restored.GetProperty("source").GetRawText());
        Equal("随机播放", f.Module.PlaybackModeText);
        Equal("网络歌单", f.Module.PlaybackQueueSourceText);
        Equal(current, f.Playback.Identity);
        Equal(43d, f.Playback.PositionSeconds);
        True(f.Playback.HasMedia && f.Playback.IsPlaying == !paused && f.Module.CanSeek, "same media and playback state survive return");
        Equal(opens, f.Playback.Opens.Count);
        Equal(stops, f.Playback.Stopped.Count);
        Equal(resumes, f.Playback.Resumes);
        Equal(resolves, f.App.ResolveCalls);
        Equal(callbacks, f.App.CoordinatorSelections);
        Equal(feedbackStarts, f.App.FeedbackProbe.Starts);
        Equal(feedbackFinishes, f.App.FeedbackProbe.Finishes);
        True(!f.Module.HasActiveRecommendation && !f.Module.CanReturnRecommendation, "returned snapshot retires UI controls");
        Equal(playingQueue.GetProperty("current").GetProperty("song").GetProperty("title").GetString(), f.Module.CurrentTitle);
        True(!proof!.IsCurrent() && f.App.GetFeedback(current) is null &&
            f.App.FeedbackProbe.Selection is { EntryId: var entryId, Recommendation: null } && entryId == current,
            "queue refresh drops the old UI feedback proof without finishing or reopening media");
        Equal(1, f.App.RecommendationCalls.Skip(commands).Count(call => call.Method == "music.recommendations.return"));
    }

    private static async Task RecommendationCommittedFmRace()
    {
        await using var f = await RecommendationRaceScene.Create();
        var queue = await f.App.Replace("851");
        f.App.PublishConfiguration(queue, "default", "");
        await Drain();
        True(f.Module.HasPlaybackFm, "committed WY FM is available to the footer");
        var old = f.App.CurrentSnapshot;
        string action = f.Module.PlaybackFmActionKey;
        f.Module.CatalogFmMode = "puzzle";
        Equal("default", f.Module.PlaybackFmMode);
        f.App.PublishConfiguration(queue, "scene", "HOT_SONG");
        await Drain();
        Equal("热歌", f.Module.PlaybackFmTitle);
        Equal("列表循环", f.Module.PlaybackModeText);
        f.App.Emit(old);
        await Drain();
        Equal("HOT_SONG", f.Module.PlaybackFmScene);
        int calls = f.App.RecommendationCalls.Count;
        await f.Module.ChangePlaybackFmAsync("familiar", "", action);
        Equal(calls, f.App.RecommendationCalls.Count);
        string current = f.Module.PlaybackFmActionKey;
        f.App.PublishConfiguration(queue, "scene", "HOT_SONG", active: false);
        await Drain();
        True(!f.Module.HasPlaybackFm, "inactive snapshots retire footer actions");
        await f.Module.ChangePlaybackFmAsync("familiar", "", current);
        Equal(calls, f.App.RecommendationCalls.Count);
    }

    private static async Task RecommendationCandidateRace()
    {
        await using var f = await RecommendationRaceScene.Create();
        var queue = await f.App.Replace("861");
        f.App.PublishCandidates(queue, "old-candidate", "862");
        await Drain();
        var old = f.Module.RecommendationCandidates.Single();
        var stale = f.App.CurrentSnapshot;
        f.App.Emit(stale); await Drain();
        True(ReferenceEquals(old, f.Module.RecommendationCandidates.Single()), "unchanged public candidates retain row identity");
        f.App.PublishCandidates(queue, "new-candidate", "863");
        await Drain();
        var current = f.Module.RecommendationCandidates.Single();
        f.App.Emit(stale); await Drain();
        Equal("new-candidate", f.Module.RecommendationCandidates.Single().Id);
        int chooses = f.App.RecommendationCalls.Count(row => row.Method == "music.recommendations.choose");
        await f.Module.ChooseRecommendationAsync(old);
        Equal(chooses, f.App.RecommendationCalls.Count(row => row.Method == "music.recommendations.choose"));
        await f.Module.ChooseRecommendationAsync(current);
        var call = f.App.RecommendationCalls.Last(row => row.Method == "music.recommendations.choose");
        Equal("consumer-race", call.Parameters.GetProperty("id").GetString());
        Equal("new-candidate", call.Parameters.GetProperty("candidateId").GetString());
        Equal(chooses + 1, f.App.RecommendationCalls.Count(row => row.Method == "music.recommendations.choose"));
        f.App.PublishCandidates(queue, "new-candidate", "863", active: false); await Drain();
        await f.Module.ChooseRecommendationAsync(current);
        Equal(chooses + 1, f.App.RecommendationCalls.Count(row => row.Method == "music.recommendations.choose"));
    }

    private static async Task RecommendationRetirementRace(bool restoreOnly)
    {
        await using var f = await RecommendationRaceScene.Create();
        var held = f.Playback.HoldNextOpen();
        var firstQueue = await f.App.Replace("701");
        string firstEntry = firstQueue.GetProperty("currentId").GetString()!;
        var firstSelection = f.App.Select(firstQueue);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(firstEntry, f.Playback.Identity);
        True(!firstSelection.IsCompleted, "the initial physical media open still owns its actual Recovery.Opened");

        // RestoreOnly refers to a different, already committed occurrence, as a
        // real return-to-queue callback would. Stop instead names the old input.
        var retirementQueue = restoreOnly ? await f.App.Replace("702") : firstQueue;
        var retirement = f.App.Select(retirementQueue,
            restoreOnly ? MusicRecommendationPlaybackAction.Select : MusicRecommendationPlaybackAction.Stop,
            play: false, restoreOnly: restoreOnly);
        await held.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Drain();
        True(!retirement.IsCompleted && !held.Settled.Task.IsCompleted,
            "cancellation is not the old Recovery's physical settlement");

        var nextQueue = await f.App.Replace("703");
        string nextEntry = nextQueue.GetProperty("currentId").GetString()!;
        True(await f.App.Select(nextQueue).WaitAsync(TimeSpan.FromSeconds(5)), "new exact selection admitted while old drain waits");
        f.Playback.Start();
        await Drain();
        Equal(nextEntry, f.Playback.Identity);
        Equal("竞态歌曲703", f.Module.CurrentTitle);
        True(f.Playback.IsPlaying && f.Playback.HasMedia && f.Module.CanSeek, "new media is actually open and started");
        True(!retirement.IsCompleted, "new playback did not pretend the old physical work was settled");
        int stopped = f.Playback.Stopped.Count;
        int observations = f.App.Observations.Count;
        Equal(2, f.Playback.Opens.Count);
        True(observations > 0 && f.App.Observations[^1] is { IsPlaying: true, HasStarted: true, MediaReady: true },
            "new occurrence has real Started/ready observations before old completion");

        held.Release.TrySetResult();
        var answers = await Task.WhenAll(firstSelection, retirement).WaitAsync(TimeSpan.FromSeconds(5));
        await Drain();
        True(held.Settled.Task.IsCompletedSuccessfully, "old noncooperative opening physically settled");
        True(answers.All(accepted => !accepted), "superseded selection and retirement both reject admission");
        Equal(stopped, f.Playback.Stopped.Count);
        True(!f.Playback.Stopped.Contains(nextEntry), "old callback never issued Stop for the new media");
        Equal(nextEntry, f.Playback.Identity);
        Equal(nextEntry, (await f.App.Queue()).GetProperty("currentId").GetString());
        Equal("竞态歌曲703", f.Module.CurrentTitle);
        Equal(2, f.Playback.Opens.Count);
        True(f.Playback.IsPlaying && f.Playback.HasMedia && f.Module.CanSeek, "old callback leaves new playback intact");

        // Identity alone would miss clearing openedEntry/recorded after the old
        // await. A fresh observable Pause must still carry the new Started proof.
        await f.Playback.PauseAsync();
        await Drain();
        Equal(observations + 1, f.App.Observations.Count);
        var paused = f.App.Observations[^1];
        Equal(nextEntry, paused.EntryId);
        True(!paused.IsPlaying && paused.HasStarted && paused.MediaReady,
            "old retirement did not clear the new entry's media bookkeeping");
    }

    private static async Task RecommendationRetryObservationRace()
    {
        await using var f = await RecommendationRaceScene.Create();
        var queue = await f.App.Replace("801");
        True(await f.App.Select(queue).WaitAsync(TimeSpan.FromSeconds(5)), "initial callback reached the real module");
        Equal(0, f.App.Observations.Count);
        f.App.Publish(queue); await Drain();
        int reentries = 0;
        f.App.OnObservation = _ =>
        {
            if (++reentries <= 3) { f.App.Publish(queue); f.Playback.Pulse(); }
        };
        PropertyChangedEventHandler repeatFromBinding = (_, args) =>
        {
            if (args.PropertyName == nameof(MusicCapability.PlaybackQueueSourceText)) f.Playback.Pulse();
        };
        f.Module.PropertyChanged += repeatFromBinding;
        try
        {
            f.Playback.Start(); await Drain();
            var started = f.App.Observations.Single();
            True(started.IsPlaying && started.HasStarted && started.MediaReady && started.PlaybackEvent > 0, "actual Started creates one playback event");
            for (int i = 0; i < 8; i++) { f.App.Publish(queue); f.Playback.Pulse(); }
            await Drain(); Equal(1, f.App.Observations.Count); Equal(1, reentries);
            f.App.OnObservation = null;
            await f.Playback.PauseAsync(); await Drain();
            await f.Playback.ResumeAsync(); await Drain();
            await f.Module.SeekAsync(37); await Drain();
            Equal(37d, f.Playback.PositionSeconds);
            True(f.App.Observations.All(value => value.PlaybackEvent == started.PlaybackEvent), "pause/resume and seek preserve the event identity");
            Equal(1, f.Playback.Opens.Count); Equal(1, f.App.ResolveCalls);
        }
        finally { f.Module.PropertyChanged -= repeatFromBinding; f.App.OnObservation = null; }
    }

    private sealed class RecommendationRaceScene : IAsyncDisposable
    {
        private readonly DocumentProbe store = new();
        internal readonly RecommendationRacePlayback Playback = new();
        internal readonly RecommendationRaceApplication App;
        internal readonly MusicCapability Module;
        private RecommendationRaceScene()
        {
            App = new(store);
            Module = new(App, store, Playback, new Dialogs());
        }
        internal static async Task<RecommendationRaceScene> Create()
        {
            var scene = new RecommendationRaceScene();
            try { await scene.Module.InitializeAsync(default); await Drain(); return scene; }
            catch { await scene.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Playback.ReleaseAll();
            await App.DrainSelections();
            try { await Module.DisposeAsync(); }
            finally { await store.DisposeAsync(); }
        }
    }

    // Only app/source/media ports are substituted. Queue versions, occurrences,
    // persistence flags and all library/history reads and writes use one actual
    // MusicLibraryOwner. No Node, network, reflection or second writer.
    private sealed class RecommendationRaceApplication(IDocumentStore store) : MusicApplicationTestDouble
    {
        private const long Generation = 1;
        private readonly MusicLibraryOwner library = new(store);
        private MusicRecommendationCoordinator? coordinator;
        private MusicRecommendationQueuePort? queue;
        private readonly HashSet<Task> selections = [];
        private Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? callback;
        private long selectionEpoch, revision;
        private bool closed;
        private JsonElement? snapshot;
        internal readonly List<MusicRecommendationPlaybackState> Observations = [];
        internal Action<MusicRecommendationPlaybackState>? OnObservation;
        internal int ResolveCalls;
        internal int CoordinatorSelections;
        internal readonly RecommendationFeedbackProbe FeedbackProbe = new();
        internal readonly List<(string Method, JsonElement Parameters)> RecommendationCalls = [];
        internal JsonElement CurrentSnapshot => snapshot!.Value.Clone();
        public override event Action<JsonElement>? Changed;
        public override IMusicPlaybackFeedback Feedback => FeedbackProbe;

        internal void UseRecommendationCoordinator(string kind)
        {
            var catalog = new RecommendationTest.Catalog();
            catalog.Reply(kind == "heartbeat" ? "heartbeat" : "queue-recommendations", "10", "11", "12");
            queue = new(library);
            coordinator = new(catalog, queue, (selection, token) =>
            {
                CoordinatorSelections++;
                return callback!(selection, token);
            }, readAsync: catalog.Read);
            coordinator.Changed += value => Changed?.Invoke(value);
        }

        public override IDisposable BindSelection(
            Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync)
        {
            True(callback is null && !closed, "one real module media binding");
            callback = selectAsync;
            return new RaceBinding(() => { selectionEpoch++; callback = null; });
        }
        private sealed class RaceBinding(Action release) : IDisposable
        {
            private Action? action = release;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
        internal Task<bool> Select(JsonElement queue, MusicRecommendationPlaybackAction action = MusicRecommendationPlaybackAction.Select,
            bool play = true, bool restoreOnly = false)
        {
            long epoch = ++selectionEpoch;
            string entry = queue.GetProperty("currentId").GetString()!;
            var selected = callback ?? throw new InvalidOperationException("Module has not bound its selection consumer");
            var selection = new MusicRecommendationSelection(action, "consumer-race", entry,
                queue.GetProperty("version").GetInt64(), play, automatic: false, restoreOnly, feedback: null,
                current: () => !closed && selectionEpoch == epoch && ReferenceEquals(callback, selected));
            var task = Deliver(); selections.Add(task);
            _ = Remove(task);
            return task;
            async Task<bool> Deliver()
            {
                await Task.Yield(); // Track before invoking the real Dispatcher callback.
                return await selected(selection, CancellationToken.None);
            }
            async Task Remove(Task pending)
            {
                try { await pending; }
                finally { selections.Remove(pending); }
            }
        }
        internal Task DrainSelections() => Task.WhenAll(selections.ToArray());
        internal async Task<JsonElement> Replace(params string[] ids)
        {
            await Call("music.queue.replace", new
            {
                songs = ids.Select(id => MusicSongFixture.Online("wy", id, "竞态歌曲" + id, "合成歌手", "", 180,
                    native: JsonSerializer.SerializeToNode(new {
                        name = "竞态歌曲" + id, singer = "合成歌手", duration = 180
                    })!.AsObject())).ToArray(),
                index = 0
            });
            return await Queue();
        }
        internal Task<JsonElement> Queue() => Call("music.queue.get", new { offset = 0, limit = 100 });
        private async Task<JsonElement> Call(string method, object? parameters = null)
        {
            var result = await ExecuteAsync(method, parameters);
            True(result.GetProperty("ok").GetBoolean(), method + ": " + result.GetRawText());
            return result;
        }
        internal void Publish(JsonElement queue)
        {
            snapshot = JsonSerializer.SerializeToElement(new
            {
                ok = true, id = "consumer-race", revision = ++revision, kind = "fm", platform = "wy",
                active = true, status = "error", loading = false, @continue = true, play = true,
                error = "NETWORK", batch = 1, canRetry = true, canReturn = false, requiresReconcile = false,
                reconcileIds = Array.Empty<string>(), queueVersion = queue.GetProperty("version").GetInt64(),
                currentId = queue.GetProperty("currentId").GetString(), anchorId = queue.GetProperty("currentId").GetString(),
                count = queue.GetProperty("total").GetInt32(), failures = 1,
                mode = "default", submode = "", ruleId = "", tabTagId = ""
            });
            Changed?.Invoke(snapshot.Value);
        }
        internal void Emit(JsonElement value) => Changed?.Invoke(value);
        internal void PublishConfiguration(JsonElement queue, string mode, string submode, bool active = true)
        {
            snapshot = JsonSerializer.SerializeToElement(new {
                ok = true, id = "consumer-race", revision = ++revision, kind = "fm", platform = "wy", active,
                mode, submode, ruleId = "", tabTagId = "", loading = false, @continue = true,
                queueVersion = queue.GetProperty("version").GetInt64(), candidates = Array.Empty<object>()
            });
            Changed?.Invoke(snapshot.Value);
        }
        internal void PublishCandidates(JsonElement queue, string candidate, string songId, bool active = true)
        {
            snapshot = JsonSerializer.SerializeToElement(new {
                ok = true, id = "consumer-race", revision = ++revision, kind = "queue", platform = "wy", active,
                loading = false, @continue = false, canRetry = true,
                queueVersion = queue.GetProperty("version").GetInt64(),
                candidates = new[] { new { id = candidate,
                    song = MusicSongFixture.Online("wy", songId, "候选歌曲" + songId, "合成歌手", "", 180) } }
            });
            Changed?.Invoke(snapshot.Value);
        }
        public override Task OnPlaybackAsync(MusicRecommendationPlaybackState state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Observations.Add(state);
            OnObservation?.Invoke(state);
            return coordinator?.OnPlaybackAsync(state, ct) ?? Task.CompletedTask;
        }
        public override Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
            CancellationToken ct = default) =>
            coordinator?.TryAdvanceAsync(entryId, naturalEnd, play, ct) ?? Task.FromResult(false);
        public override MusicRecommendationFeedback? GetFeedback(string entryId) => coordinator?.GetFeedback(entryId);
        public override async Task PrepareRecommendationsAsync(CancellationToken ct = default)
        {
            selectionEpoch++;
            if (coordinator is not null) await coordinator.PrepareCloseAsync(ct);
            await DrainSelections();
        }
        public override Task PrepareShutdownAsync(CancellationToken ct = default) => library.PrepareShutdownAsync(ct);
        public override Task CancelShutdownPreparationAsync(CancellationToken ct = default) =>
            library.CancelShutdownPreparationAsync(ct);
        public override async ValueTask DisposeAsync()
        {
            if (closed) return;
            closed = true; selectionEpoch++;
            await DrainSelections();
            if (coordinator is not null) await coordinator.DisposeAsync();
            await library.DisposeAsync();
        }
        private async Task<JsonElement> ReadQualitySettings(CancellationToken token)
        {
            var reply = await library.ExecuteAsync("music.quality.settings.get", cancellationToken: token);
            if (!reply.GetProperty("ok").GetBoolean()) return reply;
            var result = JsonNode.Parse(reply.GetRawText())!.AsObject();
            // Keep lyrics disabled without adding a library write or replacing
            // the real owner's quality policy signature.
            result["onlineSettings"] = JsonSerializer.SerializeToNode(new {
                schema = 3, revision = 0, lyrics = false });
            return JsonSerializer.SerializeToElement(result);
        }
        private static JsonElement ScriptSource() => JsonSerializer.SerializeToElement(new {
            installationId = "recommendation-consumer-race", name = "Recommendation race fixture", version = "1",
            contentHash = new string('c', 64), executionHash = new string('d', 64), sourceRevision = 1,
            generation = Generation, enabled = true, state = "ready", rollbackAvailable = false, lastFailure = (string?)null,
            sites = new { wy = new { actions = new[] { "musicUrl" }, qualitys = new[] { "320k" } } } });
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "music.accounts.list")
                return JsonSerializer.SerializeToElement(new { ok = true,
                    items = new[] { "wy", "tx", "kg" }.Select(platform =>
                        new { platform, state = "none", generation = 0, profile = (object?)null }).ToArray() });
            if (method.StartsWith("music.recommendations.", StringComparison.Ordinal))
            {
                RecommendationCalls.Add((method, JsonSerializer.SerializeToElement(parameters ?? new { })));
                if (coordinator is not null) return await coordinator.ExecuteAsync(method, parameters, cancellationToken);
            }
            if (method == "music.quality.settings.get") return await ReadQualitySettings(cancellationToken);
            if (method == "music.official.candidates")
            {
                var request = JsonSerializer.SerializeToElement(parameters);
                var input = JsonNode.Parse(request.GetRawText())!.AsObject();
                LJ.Fields(input, "song quality qualityPolicySignature manualSource", "song quality qualityPolicySignature");
                LJ.Require(input["manualSource"] is null || LJ.IsBool(input["manualSource"]), "INVALID_REQUEST");
                bool manualSource = LJ.B(input["manualSource"]);
                _ = NativeMusicSong.Normalize(input["song"]);
                string level = LJ.S(input["quality"]);
                LJ.Require(level == "auto" || MusicQualityPolicy.OfficialTypes.Contains(level), "QUALITY_UNAVAILABLE");
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) return quality;
                string signature = quality.GetProperty("signature").GetString()!;
                LJ.Require(LJ.S(input["qualityPolicySignature"]) == signature, "QUALITY_POLICY_CHANGED");
                var policy = JsonNode.Parse(quality.GetProperty("settings").GetRawText())!.AsObject();
                LJ.Require(manualSource && level != "auto" || (level == "auto" ? MusicQualityPolicy.HasEnabled(policy) :
                    MusicQualityPolicy.Enabled(level, policy)), "QUALITY_DISABLED");
                return JsonSerializer.SerializeToElement(new { ok = true, song = request.GetProperty("song"),
                    candidates = Array.Empty<object>(), qualityPolicySignature = signature, authorizationRequired = false });
            }
            if (method.StartsWith("music.official.", StringComparison.Ordinal))
                return JsonSerializer.SerializeToElement(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
            if (method == "music.songs.complete")
            {
                var request = JsonSerializer.SerializeToElement(parameters);
                var input = JsonNode.Parse(request.GetRawText())!.AsObject();
                LJ.Fields(input, "song platforms contentFailed", "song platforms");
                _ = NativeMusicSong.Normalize(input["song"]);
                var platforms = LJ.Array(input["platforms"]).Select(value => LJ.Text(value)).Distinct(StringComparer.Ordinal).ToArray();
                LJ.Require(platforms.Length is > 0 and <= 5 && platforms.All(NativeMusicSong.Platforms.Contains));
                if (input.ContainsKey("contentFailed"))
                    LJ.Require(LJ.Array(input["contentFailed"]).Select(value => LJ.Text(value)).All(NativeMusicSong.Platforms.Contains));
                return JsonSerializer.SerializeToElement(new { ok = true, song = request.GetProperty("song"),
                    failures = Array.Empty<object>() });
            }
            if (method == "music.sources.list")
                return JsonSerializer.SerializeToElement(new { ok = true, revision = 1, items = new[] { ScriptSource() } });
            if (method == "music.sources.detail")
                return JsonSerializer.SerializeToElement(parameters).GetProperty("installationId").GetString() == "recommendation-consumer-race"
                    ? JsonSerializer.SerializeToElement(new { ok = true, item = ScriptSource(), permissions = new { storage = Array.Empty<string>() } })
                    : JsonSerializer.SerializeToElement(new { ok = false, code = "NOT_FOUND" });
            if (method == "music.online.resolve")
            {
                ResolveCalls++;
                var request = JsonSerializer.SerializeToElement(parameters);
                var input = JsonNode.Parse(request.GetRawText())!.AsObject();
                LJ.Fields(input, "installationId platform song sourceKey quality qualityPolicySignature qualityCacheSignature",
                    "installationId platform song quality");
                var installation = ScriptSource();
                string platform = request.GetProperty("platform").GetString()!;
                True(request.GetProperty("installationId").GetString() == installation.GetProperty("installationId").GetString() &&
                    platform == "wy" && installation.GetProperty("sites").TryGetProperty(platform, out _),
                    "resolution uses the enabled fixture installation and its live declaration");
                var song = request.GetProperty("song");
                var songNode = NativeMusicSong.Normalize(input["song"]);
                var source = songNode["sources"]!.AsArray().OfType<JsonObject>().Single(row =>
                    LJ.S(row["source"]) == platform &&
                    (input["sourceKey"] is null || LJ.S(row["sourceKey"]) == LJ.Text(input["sourceKey"])));
                string sourceKey = LJ.Text(source["sourceKey"]);
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) return quality;
                if (request.GetProperty("quality").GetString() != "320k")
                    return JsonSerializer.SerializeToElement(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
                string policy = quality.GetProperty("signature").GetString()!;
                string cache = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v2"), ("policy", policy),
                    ("installation", installation.GetProperty("installationId")), ("platform", platform),
                    ("content", installation.GetProperty("contentHash")), ("sourceKey", sourceKey),
                    ("recording", NativeMusicSong.SourceData(source)),
                    ("songId", songNode["songId"]),
                    ("duration", songNode["duration"]), ("quality", request.GetProperty("quality"))));
                if (request.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                    return JsonSerializer.SerializeToElement(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
                if (request.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                    return JsonSerializer.SerializeToElement(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
                return JsonSerializer.SerializeToElement(new
                {
                    ok = true, installationId = installation.GetProperty("installationId").GetString(),
                    platform, generation = installation.GetProperty("generation").GetInt64(),
                    contentHash = installation.GetProperty("contentHash").GetString(),
                    songId = song.GetProperty("songId").GetString(), quality = request.GetProperty("quality").GetString(),
                    requestedQuality = request.GetProperty("quality").GetString(), expiresAt = (long?)null, sourceKey,
                    qualityPolicySignature = policy, qualityCacheSignature = cache, qualityVerified = false,
                    playbackVerified = false, url = "https://recommendation-consumer-race.invalid/synthetic.mp3",
                    headers = new Dictionary<string, string>()
                });
            }
            if (method == "music.recommendations.get" && snapshot is { } value) return value.Clone();
            if (method == "music.recommendations.choose" && snapshot is { } candidates) return candidates.Clone();
            // Unsupported incidental calls are real bounded errors, not an
            // invented success. Library/history remain native.
            return queue is not null ? await queue.ExecuteAsync(method, parameters, cancellationToken) :
                await library.ExecuteAsync(method, parameters, cancellationToken);
        }
    }

    private sealed class RecommendationFeedbackProbe : IMusicPlaybackFeedback
    {
        internal MusicFeedbackSelection? Selection;
        internal int Starts, Finishes;
        public string? LastError => null;
        public Task<MusicFeedbackSettings> ReadSettingsAsync(bool refresh = false, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MusicFeedbackSettings(0, []));
        }
        public Task<MusicFeedbackSettings> SaveSettingsAsync(long revision, IReadOnlyList<string> enabled, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MusicFeedbackSettings(revision + 1, enabled.ToArray()));
        }
        public void Select(MusicFeedbackSelection value) => Selection = value;
        public void UpdateSelection(MusicFeedbackSelection value) => Selection = value;
        public void Started(string entryId, double position, double duration, double rate = 1, int mode = 1) => Starts++;
        public void Observe(string entryId, double position, double duration, bool isPlaying, double rate = 1, int mode = 1) { }
        public void Finish(string reason = "ui") => Finishes++;
    }

    private sealed class RecommendationRacePlayback : IMusicPlayback
    {
        private readonly PlaybackProbe inner = new();
        private readonly List<RecommendationOpenHold> holds = [];
        private RecommendationOpenHold? next;
        internal readonly List<string?> Stopped = [];
        internal List<(string Entry, Uri Uri)> Opens => inner.Opens;
        internal int Resumes => inner.Resumes;
        public string? OwnerId => inner.OwnerId;
        public string? Identity => inner.Identity;
        public bool HasMedia => inner.HasMedia;
        public bool IsPlaying => inner.IsPlaying;
        public double PositionSeconds => inner.PositionSeconds;
        public double DurationSeconds => inner.DurationSeconds;
        public int Volume { get => inner.Volume; set => inner.Volume = value; }
        public event PropertyChangedEventHandler? PropertyChanged { add => inner.PropertyChanged += value; remove => inner.PropertyChanged -= value; }
        public event EventHandler? Started { add => inner.Started += value; remove => inner.Started -= value; }
        public event EventHandler? Ended { add => inner.Ended += value; remove => inner.Ended -= value; }
        public event EventHandler? Failed { add => inner.Failed += value; remove => inner.Failed -= value; }
        internal RecommendationOpenHold HoldNextOpen()
        {
            True(next is null, "one held physical opening");
            next = new(); holds.Add(next); return next;
        }
        internal void ReleaseAll() { foreach (var hold in holds) hold.Release.TrySetResult(); }
        internal void Start() => inner.Start();
        internal void Pulse() => True(inner.SeekAsync(inner.Seek).IsCompletedSuccessfully, "synchronous unchanged media observation");
        public async Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        {
            var held = next; next = null;
            await inner.OpenAsync(owner, identity, uri, title, token);
            if (held is not null) await held.Wait(token);
        }
        public Task PauseAsync() => inner.PauseAsync();
        public Task ResumeAsync() => inner.ResumeAsync();
        public Task StopAsync() { Stopped.Add(Identity); return inner.StopAsync(); }
        public Task SeekAsync(double seconds) => inner.SeekAsync(seconds);
    }

    private sealed class RecommendationOpenHold
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task Wait(CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            try { await Release.Task; } // Deliberately ignore cancellation until physical completion.
            finally { Settled.TrySetResult(); }
        }
    }
}
