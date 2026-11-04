using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
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
        yield return Case("recommendation consumer FM retry deadline releases identical observation once under reentrant Changed",
            RecommendationRetryObservationRace);
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
        Equal(0, f.App.Observations.Count); // Open's initial false is not a Pause or a Started event.

        long future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        f.App.Publish(queue, future);
        await Drain();
        True(f.Module.HasActiveRecommendation && f.Module.CanRetryRecommendation, "active FM error snapshot was consumed");
        f.Playback.Start();
        await Drain();
        Equal(1, f.App.Observations.Count);
        var started = f.App.Observations[0];
        True(started.IsPlaying && started.HasStarted && started.MediaReady && started.IsLocal,
            "test uses an actual public playback Started event");

        for (int i = 0; i < 8; i++) f.Playback.Pulse();
        f.App.Publish(queue, future); // A newer revision is not a new retry deadline.
        await Drain();
        for (int i = 0; i < 8; i++) f.Playback.Pulse();
        await Drain();
        Equal(1, f.App.Observations.Count); // Zero duplicate delivery before retryAt.

        long expired = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        int reentries = 0;
        f.App.OnObservation = _ =>
        {
            // Bound a broken implementation's recursion, but do not hide it.
            // Exercise both the source Changed callback and synchronous media
            // reentry before OnPlaybackAsync returns to the UI consumer.
            if (++reentries <= 3)
            {
                f.App.Publish(queue, expired);
                f.Playback.Pulse();
            }
        };
        PropertyChangedEventHandler repeatFromBinding = (_, args) =>
        {
            if (args.PropertyName == nameof(MusicCapability.RecommendationStatus)) f.Playback.Pulse();
        };
        f.Module.PropertyChanged += repeatFromBinding;
        try
        {
            f.App.Publish(queue, expired); // No clock sleeps, production timers or high-frequency polling.
            await Drain();
            for (int i = 0; i < 8; i++) f.Playback.Pulse();
            await Drain();
            Equal(2, f.App.Observations.Count);
            Equal(1, reentries);
            Equal(started, f.App.Observations[1]); // Exactly the same started/playing/ready value is re-admitted.

            for (int i = 0; i < 3; i++) f.App.Publish(queue, expired);
            await Drain();
            for (int i = 0; i < 8; i++) f.Playback.Pulse();
            await Drain();
            Equal(2, f.App.Observations.Count);
            Equal(1, reentries);
            Equal(1, f.Playback.Opens.Count);
            Equal(1, f.App.ResolveCalls);
        }
        finally
        {
            f.Module.PropertyChanged -= repeatFromBinding;
            f.App.OnObservation = null;
        }
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
    private sealed class RecommendationRaceApplication(IProtectedDocumentStore store) : MusicApplicationTestDouble
    {
        private readonly MusicLibraryOwner library = new(store);
        private readonly HashSet<Task> selections = [];
        private Func<MusicRecommendationSelection, CancellationToken, Task<bool>>? callback;
        private long selectionEpoch, revision;
        private bool closed;
        private JsonElement? snapshot;
        internal readonly List<MusicRecommendationPlaybackState> Observations = [];
        internal Action<MusicRecommendationPlaybackState>? OnObservation;
        internal int ResolveCalls;
        public override event Action<JsonElement>? Changed;

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
        internal async Task<JsonElement> Replace(string id)
        {
            await Call("music.queue.replace", new
            {
                songs = new[] { MusicSongFixture.Online("wy", id, "竞态歌曲" + id, "合成歌手", "", 180,
                    native: JsonSerializer.SerializeToNode(new {
                        name = "竞态歌曲" + id, singer = "合成歌手", duration = 180
                    })!.AsObject()) },
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
        internal void Publish(JsonElement queue, long retryAt)
        {
            snapshot = JsonSerializer.SerializeToElement(new
            {
                ok = true, id = "consumer-race", revision = ++revision, kind = "fm", platform = "wy",
                active = true, status = "error", loading = false, waiting = false, @continue = true, play = true,
                error = "NETWORK", batch = 1, canRetry = true, canReturn = false, requiresReconcile = false,
                reconcileIds = Array.Empty<string>(), retryAt, queueVersion = queue.GetProperty("version").GetInt64(),
                currentId = queue.GetProperty("currentId").GetString(), anchorId = queue.GetProperty("currentId").GetString(),
                count = queue.GetProperty("total").GetInt32(), failures = 1
            });
            Changed?.Invoke(snapshot.Value);
        }
        public override Task OnPlaybackAsync(MusicRecommendationPlaybackState state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Observations.Add(state);
            OnObservation?.Invoke(state);
            return Task.CompletedTask;
        }
        public override Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
            CancellationToken ct = default) =>
            Task.FromResult(false);
        public override MusicRecommendationFeedback? GetFeedback(string entryId) => null;
        public override Task PrepareRecommendationsAsync(CancellationToken ct = default)
        { selectionEpoch++; return DrainSelections(); }
        public override Task PrepareShutdownAsync(CancellationToken ct = default) => library.PrepareShutdownAsync(ct);
        public override Task CancelShutdownPreparationAsync(CancellationToken ct = default) =>
            library.CancelShutdownPreparationAsync(ct);
        public override async ValueTask DisposeAsync()
        {
            if (closed) return;
            closed = true; selectionEpoch++;
            await DrainSelections(); await library.DisposeAsync();
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
            generation = 1, enabled = true, state = "ready", rollbackAvailable = false, lastFailure = (string?)null,
            capabilities = new[] { new { platform = "wy", actions = new[] { "musicUrl" }, qualitys = new[] { "320k" } } } });
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "music.quality.settings.get") return await ReadQualitySettings(cancellationToken);
            if (method.StartsWith("music.official.", StringComparison.Ordinal))
                return JsonSerializer.SerializeToElement(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
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
                var song = request.GetProperty("song");
                var quality = await ReadQualitySettings(cancellationToken);
                if (!quality.GetProperty("ok").GetBoolean()) return quality;
                if (request.GetProperty("quality").GetString() != "320k")
                    return JsonSerializer.SerializeToElement(new { ok = false, code = "CAPABILITY_UNAVAILABLE" });
                True(request.GetProperty("installationId").GetString() == "recommendation-consumer-race" &&
                    request.GetProperty("platform").GetString() == "wy" && request.GetProperty("generation").GetInt64() == 1,
                    "resolution uses the enabled fixture installation and its live declaration");
                string policy = quality.GetProperty("signature").GetString()!;
                var source = song.GetProperty("sources")[0];
                var songNode = JsonNode.Parse(song.GetRawText())!.AsObject();
                string cache = LJ.Digest(LJ.Obj(("contract", "music.quality.cache.v1"), ("policy", policy),
                    ("installation", request.GetProperty("installationId")), ("platform", request.GetProperty("platform")),
                    ("revision", 1), ("generation", request.GetProperty("generation")),
                    ("sourceKey", source.GetProperty("sourceKey")), ("recording", source.GetProperty("musicInfo")),
                    ("songId", songNode["songId"]),
                    ("duration", songNode["duration"]), ("quality", request.GetProperty("quality"))));
                if (request.TryGetProperty("qualityPolicySignature", out var expectedPolicy) && expectedPolicy.GetString() != policy)
                    return JsonSerializer.SerializeToElement(new { ok = false, code = "QUALITY_POLICY_CHANGED" });
                if (request.TryGetProperty("qualityCacheSignature", out var expectedCache) && expectedCache.GetString() != cache)
                    return JsonSerializer.SerializeToElement(new { ok = false, code = "QUALITY_CACHE_CHANGED" });
                return JsonSerializer.SerializeToElement(new
                {
                    ok = true, installationId = request.GetProperty("installationId").GetString(),
                    platform = request.GetProperty("platform").GetString(), generation = request.GetProperty("generation").GetInt64(),
                    songId = song.GetProperty("songId").GetString(), quality = request.GetProperty("quality").GetString(),
                    sourceKey = source.GetProperty("sourceKey").GetString(),
                    qualityPolicySignature = policy, qualityCacheSignature = cache, qualityVerified = false,
                    playbackVerified = false, url = "https://recommendation-consumer-race.invalid/synthetic.mp3",
                    headers = new Dictionary<string, string>()
                });
            }
            if (method == "music.recommendations.get" && snapshot is { } value) return value.Clone();
            // Unsupported incidental calls are real bounded errors, not an
            // invented success. Initialization/library/history remain native.
            return await library.ExecuteAsync(method, parameters, cancellationToken);
        }
    }

    private sealed class RecommendationRacePlayback : IMusicPlayback
    {
        private readonly PlaybackProbe inner = new();
        private readonly List<RecommendationOpenHold> holds = [];
        private RecommendationOpenHold? next;
        internal readonly List<string?> Stopped = [];
        internal List<(string Entry, Uri Uri)> Opens => inner.Opens;
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
