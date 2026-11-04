using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicPlaybackFeedbackTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("default-off-and-passive-dispose", async () =>
        {
            var f = new Fixture();
            Check(f.Store.Reads == 0 && f.Protocol.Starts.Count == 0);
            await f.DisposeAsync(); Check(f.Store.Reads == 0 && f.Store.Writes == 0 && f.Store.Disposals == 0);
        });
        yield return Case("resolution-queue-and-observation-cannot-start-listening", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Controller.Observe("entry-1", 100, 200, true);
            f.Controller.Select(Selection());
            f.Controller.Observe("entry-1", 100, 200, true);
            await f.Drain(); Check(f.Protocol.Starts.IsEmpty);
            f.Controller.Started("wrong-entry", 0, 200); await f.Drain(); Check(f.Protocol.Starts.IsEmpty);
            f.Controller.Started("entry-1", 0, 200); await f.Drain(); Check(f.Protocol.Starts.Count == 1);
        });
        yield return Case("real-start-default-off-does-not-acquire-accounts", async () =>
        {
            await using var f = new Fixture(); f.Play();
            await f.Drain(); Check(f.Sessions.Acquires == 0 && f.Protocol.Starts.IsEmpty && f.Store.Writes == 0);
        });
        yield return Case("wall-clock-and-media-delta-exclude-pause-stall-seek", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Tick(10, 10); f.Tick(10, 10); // No position advancement while buffered/stalled.
            f.Controller.Observe("entry-1", 10, 200, false);
            f.Tick(40, 10, false); f.Controller.Observe("entry-1", 10, 200, true);
            f.Tick(2, 100); // Explicit large discontinuity is not listening.
            f.Tick(5, 105); f.Controller.Finish("ui"); await f.Drain();
            var end = f.Protocol.Finished.Single(); Check(end.Listened == 15 && end.MediaListened == 15 && end.Reason == "ui");
        });
        yield return Case("unknown-duration-waits-for-one-real-eligible-sample", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Controller.Select(Selection());
            f.Controller.Started("entry-1", 0, 0); await f.Drain();
            foreach (double duration in new[] { 0, double.NaN, double.PositiveInfinity, -1, 86401d })
                f.Controller.Observe("entry-1", 0, duration, true);
            f.Controller.Observe("entry-1", 201, 200, true);
            await f.Drain(); Check(f.Sessions.Acquires == 0 && f.Protocol.Starts.IsEmpty);
            f.Controller.Observe("entry-1", 10, 200, true); await f.Drain();
            for (int i = 0; i < 10; i++) f.Controller.Observe("entry-1", 10, 200, true);
            await f.Drain(); Check(f.Sessions.Acquires == 1 && f.Protocol.Starts.Count == 1);
            f.Tick(5, 15); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.Single().Listened == 5 && f.Protocol.Starts.Single().Position == 10);
        });
        yield return Case("identity-acceptance-wait-is-not-listening", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Protocol.Arrived = Signal(); f.Protocol.Release = Signal(); f.Play();
            await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Tick(30, 30); f.Tick(10, 40);
            f.Protocol.Release.SetResult(); await f.Drain();
            f.Tick(7, 47); f.Controller.Finish(); await f.Drain();
            var end = f.Protocol.Finished.Single();
            Check(end.Listened == 7 && end.MediaListened == 7 && end.Position == 47);
        });
        yield return Case("invalid-samples-break-the-listening-interval", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Tick(5, 5); f.Controller.Observe("entry-1", 5, double.NaN, true);
            f.Tick(10, 15); f.Tick(3, 18); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.Single().Listened == 8);
        });
        foreach (var (native, expected) in new[] { ("end", "playend"), ("error", "exception"),
            ("ui", "ui"), ("interrupt", "interrupt") })
            yield return Case("native-termination-" + native, async () =>
            {
                await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
                f.Tick(200, 200); f.Controller.Finish(native); await f.Drain();
                var end = f.Protocol.Finished.Single();
                Check(end.Reason == expected && end.Position == 200 && end.Listened == 200);
            });
        yield return Case("long-accepted-session-keeps-lifetime-revocation", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            var token = f.Protocol.StartTokens.Single();
            f.Tick(60, 60); Check(!token.IsCancellationRequested);
            await f.Enable(); await f.Drain();
            Check(token.IsCancellationRequested && f.Protocol.Finished.IsEmpty);
        });
        yield return Case("short-seek-native-discontinuity-does-not-credit-time", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Tick(1, 1); f.Controller.Observe("entry-1", 1, 200, false);
            f.Controller.Observe("entry-1", 2, 200, true);
            f.Tick(1, 3); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.Single().Listened == 2);
        });
        yield return Case("rate-sampling-counts-wall-and-media-separately", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Controller.Observe("entry-1", 0, 200, true, 2);
            f.Tick(10, 20, rate: 2); f.Controller.Finish(); await f.Drain();
            var end = f.Protocol.Finished.Single(); Check(end.Listened == 10 && end.MediaListened == 20 && end.Rate == 2);
        });
        yield return Case("duplicate-start-no-duplicate-upstream", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            for (int i = 0; i < 20; i++) f.Controller.Started("entry-1", 0, 200);
            await f.Drain(); Check(f.Protocol.Starts.Count == 1);
            f.Controller.Finish(); f.Controller.Finish(); await f.Drain(); Check(f.Protocol.Finished.Count == 1);
        });
        yield return Case("no-account-no-report-no-poll-loop", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Sessions.Values.Clear(); f.Play(); await f.Drain();
            for (int i = 0; i < 100; i++) f.Tick(1, i);
            await f.Drain(); Check(f.Sessions.Acquires == 1 && f.Protocol.Starts.IsEmpty);
        });
        yield return Case("exact-multi-platform-recordings-not-delivery-platform", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy", "tx", "kg");
            f.Play(Merged()); await f.Drain();
            Check(f.Protocol.Starts.Select(r => r.Recording.Platform).ToHashSet().SetEquals(["wy", "tx", "kg"]));
            Check(f.Protocol.Starts.Single(r => r.Recording.Platform == "wy").Recording.Id == "123");
            Check(f.Protocol.Starts.Single(r => r.Recording.Platform == "kg").Recording.MixSongId == "900");
        });
        yield return Case("authoritative-empty-source-map-never-falls-back-to-primary", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Play(Song(sources: Array.Empty<JsonObject>())); await f.Drain(); Check(f.Protocol.Starts.IsEmpty);
        });

        yield return Case("mapping-revocation-cancels-live-feedback", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Tick(15, 15); f.Controller.UpdateSelection(Selection(Song(sources: Array.Empty<JsonObject>())));
            await f.Drain(); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.IsEmpty && f.Protocol.DisposedSessions == 1);
        });
        yield return Case("account-replacement-cannot-finish-old-listening-instance", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Tick(20, 20); f.Sessions.Replace("wy"); f.Controller.AccountChanged("wy");
            await f.Drain(); f.Controller.Finish(); await f.Drain(); Check(f.Protocol.Finished.IsEmpty);
            Check(f.Protocol.StartTokens.Single().IsCancellationRequested);
        });
        yield return Case("enable-mid-listen-is-prospective-and-draft-has-no-effect", async () =>
        {
            await using var f = new Fixture(); f.Play(); await f.Drain(); f.Tick(30, 30);
            await f.Enable("wy"); await f.Drain(); f.Tick(5, 35); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.Single().Listened == 5 && f.Protocol.Starts.Single().Position == 30);
        });
        yield return Case("settings-disable-cancels-with-no-final-send", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain(); f.Tick(40, 40);
            await f.Enable(); await f.Drain(); f.Controller.Finish(); await f.Drain();
            Check(f.Protocol.Finished.IsEmpty && f.Protocol.DisposedSessions == 1);
        });
        yield return Case("lost-settings-ack-requires-explicit-read-no-replay", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Play(); await f.Drain();
            f.Store.Lose = true;
            await Error("COMMIT_UNKNOWN", () => f.Enable());
            Check(!f.Settings.IsEnabled("wy")); await f.Drain();
            int writes = f.Store.Writes;
            await Error("COMMIT_UNKNOWN", () => f.Settings.ReadAsync());
            await f.Settings.ReadAsync(true); Check(f.Store.Writes == writes && !f.Settings.IsEnabled("wy"));
            Check(f.Protocol.Finished.IsEmpty);
        });
        yield return Case("settings-cas-conflict-preserves-other-writer", async () =>
        {
            var store = new MemoryStore(); await using var a = new MusicFeedbackSettingsOwner(store);
            await using var b = new MusicFeedbackSettingsOwner(store);
            await a.ReadAsync(); await b.ReadAsync(); await a.SaveAsync(0, ["wy"]);
            await Error("STORAGE_CONFLICT", () => b.SaveAsync(0, ["tx"]));
            Check(!b.IsEnabled("tx")); var result = await b.ReadAsync(true);
            Check(result.Enabled.SequenceEqual(["wy"]) && result.Revision == 1 && store.Writes == 2);
        });
        foreach (string data in new[]
        {
            "null",
            "{}",
            "{\"version\":1,\"revision\":0,\"enabled\":[],\"enabled\":[]}",
            "{\"version\":1,\"revision\":0,\"enabled\":[],\"extra\":true}",
            "{\"version\":1,\"enabled\":[]}",
            "{\"version\":1.0,\"revision\":0,\"enabled\":[]}",
            "{\"version\":01,\"revision\":0,\"enabled\":[]}",
            "{\"version\":1,\"revision\":0.0,\"enabled\":[]}",
            "{\"version\":1,\"revision\":1e0,\"enabled\":[]}",
            "{\"version\":1,\"revision\":00,\"enabled\":[]}",
            "{\"version\":1,\"revision\":0,\"enabled\":[\"wy\",\"wy\"]}",
            "{\"version\":1,\"revision\":0,\"enabled\":[\"wy\",\"kg\"]}",
            "{\"version\":1,\"revision\":0,\"enabled\":[1]}",
            "{\"version\":1,\"revision\":0,\"enabled\":[\"unknown\"]}"
        })
            yield return Case("corrupt-settings-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..8],
                async () =>
                {
                    var store = new MemoryStore { Data = Encoding.UTF8.GetBytes(data) };
                    byte[] original = store.Data.ToArray();
                    await using var settings = new MusicFeedbackSettingsOwner(store);
                    await Error("CORRUPT_STORAGE", () => settings.ReadAsync());
                    await Error("CORRUPT_STORAGE", () => settings.SaveAsync(0, ["wy"]));
                    Check(!settings.IsEnabled("wy") && store.Writes == 0 &&
                        store.Data.SequenceEqual(original));
                });
        yield return Case("settings-reject-unknown-write-receipt-without-retry-or-success", async () =>
        {
            var store = new MemoryStore
            {
                SkipWrite = true,
                WriteReceipt = new DocumentFingerprint(true, 1, new string('0', 64))
            };
            await using var settings = new MusicFeedbackSettingsOwner(store);
            await settings.ReadAsync();
            await Error("COMMIT_UNKNOWN", () => settings.SaveAsync(0, ["wy"]));
            await Error("COMMIT_UNKNOWN", () => settings.ReadAsync());
            Check(store.Writes == 1 && store.Data is null && !settings.IsEnabled("wy"));
        });
        yield return Case("durable-policy-reopen-only-native-document", async () =>
        {
            string root = DataPath();
            Directory.CreateDirectory(root);
            await using (var store = new LocalDocumentStore(root, [MusicFeedbackSettingsOwner.DocumentNamespace]))
            await using (var settings = new MusicFeedbackSettingsOwner(store))
            {
                await settings.ReadAsync(); await settings.SaveAsync(0, ["kg", "wy"]);
            }
            await using var second = new LocalDocumentStore(root, [MusicFeedbackSettingsOwner.DocumentNamespace]);
            await using var reopened = new MusicFeedbackSettingsOwner(second);
            var value = await reopened.ReadAsync();
            Check(value.Revision == 1 && value.Enabled.ToHashSet().SetEquals(["kg", "wy"]));
        });
        yield return Case("admitted-save-cancel-waits-physical-commit", async () =>
        {
            var store = new MemoryStore { Arrived = Signal(), Release = Signal() };
            await using var settings = new MusicFeedbackSettingsOwner(store); await settings.ReadAsync();
            using var cancel = new CancellationTokenSource();
            var save = settings.SaveAsync(0, ["wy"], cancel.Token); await store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel(); Check(!save.IsCompleted && !settings.IsEnabled("wy"));
            store.Release.SetResult(); var result = await save;
            Check(result.Revision == 1 && settings.IsEnabled("wy") && store.Writes == 1);
        });
        yield return Case("late-start-after-revoke-disposed-no-end-or-retry", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Protocol.Arrived = Signal(); f.Protocol.Release = Signal(); f.Play();
            await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Enable(); f.Protocol.Release.SetResult(); await f.Drain();
            Check(f.Protocol.Finished.IsEmpty && f.Protocol.DisposedSessions == 1);
            for (int i = 0; i < 10; i++) f.Tick(1, i);
            await f.Drain(); Check(f.Protocol.Starts.Count == 1);
        });
        yield return Case("pending-start-end-stays-bound-to-old-selection", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Protocol.Arrived = Signal(); f.Protocol.Release = Signal(); f.Play();
            await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Tick(10, 10);
            f.Controller.Finish("ui"); f.Controller.Select(new("entry-2", Song("456")));
            f.Protocol.Release.SetResult(); await f.Drain();
            var end = f.Protocol.Finished.Single();
            Check(f.Protocol.Starts.Single().Recording.Id == "123" && end.Position == 10 &&
                end.Listened == 0 && end.MediaListened == 0);
        });
        yield return Case("upstream-failure-no-repeat-on-timeupdates", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy"); f.Protocol.Fail = true; f.Play(); await f.Drain();
            for (int i = 0; i < 100; i++) f.Tick(1, i);
            await f.Drain(); Check(f.Protocol.Starts.Count == 1 && f.Controller.LastError == "FEEDBACK_FAILED");
        });
        yield return Case("shutdown-drains-pending-start-and-never-disposes-borrowed-store", async () =>
        {
            var f = new Fixture(); await f.Enable("wy");
            f.Protocol.Arrived = Signal(); f.Protocol.Release = Signal(); f.Play();
            await f.Protocol.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = f.Controller.PrepareShutdownAsync(); Check(!closing.IsCompleted);
            f.Protocol.Release.SetResult(); await closing; await f.DisposeAsync();
            Check(f.Protocol.DisposedSessions == 1 && f.Protocol.Disposals == 1 && f.Store.Disposals == 0);
        });
        yield return Case("ordinary-provenance-and-no-stale-FM-attribution", async () =>
        {
            await using var f = new Fixture(); await f.Enable("wy");
            f.Controller.Select(Selection(context: new { sourcePlatform = "wy", sourceType = "playlist", sourceId = "500" }));
            f.Controller.Started("entry-1", 0, 200); await f.Drain();
            var request = f.Protocol.Starts.Single();
            Check(request.Source == "playlist" && request.SourceId == "500" && request.SourceType == "list");
            f.Controller.Finish(); await f.Drain();
            f.Controller.Select(Selection(context: new { sourcePlatform = "wy", sourceType = "fm", fm = new { automatic = true } }));
            f.Controller.Started("entry-1", 0, 200); await f.Drain();
            Check(f.Protocol.Starts.Last().Source == "unknown");
        });
        yield return Case("foreign-or-missing-platform-never-relabels-playlist-provenance", async () =>
        {
            foreach (object context in new object[] {
                new { sourcePlatform = "tx", sourceType = "playlist", sourceId = "500" },
                new { sourceType = "playlist", sourceId = "500" } })
            {
                await using var f = new Fixture(); await f.Enable("wy");
                f.Controller.Select(Selection(context: context)); f.Controller.Started("entry-1", 0, 200);
                await f.Drain();
                Check(f.Protocol.Starts.Single().Source == "unknown" && f.Protocol.Starts.Single().SourceId == "");
            }
        });
        foreach (string reason in new[] { "end", "error" })
            yield return Case("real-native-protocol-consumes-player-" + reason, async () =>
            {
                var accounts = new Sessions();
                accounts.Values["wy"] = new("wy", Guid.NewGuid().ToString("D"), 1, "42",
                    "MUSIC_U=synthetic-session; __csrf=synthetic-csrf");
                var wire = new NativeWire();
                using var http = MusicPlatformHttp.CreateWithHandler(wire);
                await using var clients = new MusicClientContextFixture(http);
                var clock = new Clock();
                var settings = new MusicFeedbackSettingsOwner(new MemoryStore());
                await using var controller = new MusicFeedbackController(settings,
                    new NativeMusicFeedbackProtocol(accounts, http, clients.Owner, clock), accounts, clock);
                await settings.ReadAsync(); await settings.SaveAsync(0, ["wy"]);
                controller.Select(Selection());
                controller.Started("entry-1", 0, 0);
                await controller.DrainAsync(); Check(wire.Paths.IsEmpty);
                controller.Observe("entry-1", 0, 200, true);
                await controller.DrainAsync();
                Check(wire.Paths.Count == 3 && controller.LastError is null);
                clock.Advance(200); controller.Observe("entry-1", 200, 200, true);
                controller.Finish(reason); await controller.DrainAsync();
                Check(controller.LastError is null && wire.Paths.SequenceEqual(new[] {
                    "/weapi/w/nuser/account/get", "/weapi/v3/song/detail", "/eapi/feedback/weblog",
                    "/weapi/w/nuser/account/get", "/eapi/feedback/weblog" }));
            });
    }

    private static TestCase Case(string name, Func<Task> run) => new("music.playback-feedback." + name, run);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("feedback assertion"); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("expected " + code); }
        catch (MusicFeedbackException error) { Check(error.Code == code); }
    }
    private static string DataPath() => Path.Combine(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
        ?? throw new InvalidOperationException("Isolated root required"), "feedback-" + Guid.NewGuid().ToString("N"));
    private static JsonObject Raw(string platform, string id, string title = "same")
    {
        var native = new JsonObject();
        if (platform == "kg")
        {
            native["hash"] = id;
            native["mixsongid"] = "900";
        }
        return MusicSongFixture.Online(platform, id, title, "artist", "", 200,
            native: native)["sources"]![0]!.DeepClone().AsObject();
    }
    private static JsonObject SongNode(string id = "123", IEnumerable<JsonObject>? sources = null)
    {
        var song = MusicSongFixture.Online("wy", id, "same", "artist", "", 200);
        if (sources is not null)
            song["sources"] = new JsonArray(sources.Select(source => (JsonNode)source.DeepClone()).ToArray());
        return song;
    }
    private static JsonElement Song(string id = "123", IEnumerable<JsonObject>? sources = null) =>
        MusicSongFixture.Element(SongNode(id, sources));
    private static JsonElement Merged() => Song(sources: [Raw("wy", "123"), Raw("tx", "mid123"), Raw("kg", new string('A', 32))]);
    private static MusicFeedbackSelection Selection(JsonElement? song = null, object? context = null) =>
        new("entry-1", song ?? Song(), context is null ? null : JsonSerializer.SerializeToElement(context));

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(double seconds) => Interlocked.Add(ref ticks, (long)(seconds * 1000));
    }
    private sealed class Sessions : IMusicPlatformSessionSource
    {
        internal readonly Dictionary<string, MusicPlatformSession> Values = new(StringComparer.Ordinal);
        internal int Acquires;
        internal Sessions() { foreach (string p in new[] { "wy", "tx", "kg" }) Replace(p); }
        internal void Replace(string platform) => Values[platform] = new(platform, Guid.NewGuid().ToString("D"), 1, "42", "session=SYNTHETIC");
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Acquires); return ValueTask.FromResult(Values.GetValueOrDefault(platform)); }
        public bool IsCurrent(MusicPlatformSession session) => ReferenceEquals(Values.GetValueOrDefault(session.Platform), session);
    }
    private sealed class Protocol : IMusicFeedbackProtocol
    {
        internal readonly ConcurrentQueue<MusicFeedbackStart> Starts = new();
        internal readonly ConcurrentQueue<MusicFeedbackEnd> Finished = new();
        internal readonly ConcurrentQueue<CancellationToken> StartTokens = new();
        internal int DisposedSessions, Disposals;
        internal bool Fail;
        internal TaskCompletionSource? Arrived, Release;
        public async Task<IMusicFeedbackSession> StartAsync(MusicPlatformSession account, MusicFeedbackStart request, CancellationToken token)
        {
            Starts.Enqueue(request); StartTokens.Enqueue(token); Arrived?.TrySetResult();
            if (Release is not null) await Release.Task; // Deliberately late trusted transport.
            if (Fail) throw new IOException("synthetic failure");
            return new Session(this, request.Recording);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        private sealed class Session(Protocol owner, MusicFeedbackRecording recording) : IMusicFeedbackSession
        {
            public MusicFeedbackRecording Recording => recording;
            public Task FinishAsync(MusicFeedbackEnd end, CancellationToken token)
            { token.ThrowIfCancellationRequested(); owner.Finished.Enqueue(end); return Task.CompletedTask; }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.DisposedSessions); return ValueTask.CompletedTask; }
        }
    }
    private sealed class NativeWire : System.Net.Http.HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Paths = new();
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.Method == System.Net.Http.HttpMethod.Post && request.RequestUri!.Scheme == "https");
            string path = request.RequestUri!.AbsolutePath;
            string body = path switch
            {
                "/weapi/w/nuser/account/get" => "{\"code\":200,\"profile\":{\"userId\":42,\"nickname\":\"synthetic\"},\"account\":{\"id\":42}}",
                "/weapi/v3/song/detail" => "{\"code\":200,\"songs\":[{\"id\":123,\"dt\":200000}]}",
                "/eapi/feedback/weblog" => "{\"code\":200}",
                _ => throw new InvalidOperationException("Unexpected synthetic endpoint")
            };
            Check(request.RequestUri.Host == (path == "/eapi/feedback/weblog"
                ? "clientlog.music.163.com" : "music.163.com"));
            Paths.Enqueue(path);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class MemoryStore : IDocumentStore
    {
        internal byte[]? Data;
        internal int Reads, Writes, Disposals;
        internal bool Lose, SkipWrite;
        internal DocumentFingerprint? WriteReceipt;
        internal TaskCompletionSource? Arrived, Release;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            Check(ns == MusicFeedbackSettingsOwner.DocumentNamespace); Interlocked.Increment(ref Reads);
            return Task.FromResult(new DocumentSnapshot(Data?.ToArray(),
                Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data)));
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            Check(ns == MusicFeedbackSettingsOwner.DocumentNamespace); Interlocked.Increment(ref Writes);
            Arrived?.TrySetResult(); if (Release is not null) await Release.Task;
            var before = Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data);
            if (before != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            if (!SkipWrite) Data = data.ToArray();
            if (Lose) { Lose = false; throw new DocumentStoreException(DocumentStoreError.Storage); }
            return WriteReceipt ?? DocumentFingerprint.FromBytes(data.Span);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly MemoryStore Store = new();
        internal readonly Sessions Sessions = new();
        internal readonly Protocol Protocol = new();
        internal readonly Clock Clock = new();
        internal readonly MusicFeedbackSettingsOwner Settings;
        internal readonly MusicFeedbackController Controller;
        internal Fixture()
        {
            Settings = new(Store); Controller = new(Settings, Protocol, Sessions, Clock);
        }
        internal async Task Enable(params string[] platforms)
        { var old = await Settings.ReadAsync(); await Settings.SaveAsync(old.Revision, platforms); }
        internal void Play(JsonElement? song = null)
        { Controller.Select(Selection(song)); Controller.Started("entry-1", 0, 200); }
        internal void Tick(double seconds, double position, bool running = true, double rate = 1)
        { Clock.Advance(seconds); Controller.Observe("entry-1", position, 200, running, rate); }
        internal Task Drain() => Controller.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        public ValueTask DisposeAsync() { Protocol.Release?.TrySetResult(); Store.Release?.TrySetResult(); return Controller.DisposeAsync(); }
    }
}
