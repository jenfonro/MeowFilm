using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicOfficialSelectionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Official selection matching rejects other editions duration conflicts and quarantined recordings", () =>
        {
            var song = SongObject("wy", "1");
            var compatible = SongObject("tx", "FixtureMID");
            var quarantined = SongObject("tx", "Quarantined");
            song["quarantinedSources"] = quarantined["sources"]!.DeepClone();
            var plan = new MusicOfficialCandidates(song);
            plan.Discover(compatible, "tx");
            foreach (var other in new[] { SongObject("tx", "Live", title: "Same Song (Live)"),
                SongObject("tx", "Long", duration: 160), SongObject("tx", "Unknown", duration: 0), quarantined })
                plan.Discover(other, "tx");
            plan.Discover(SongObject("kg", "Fake"), "tx"); // A provider cannot publish another platform.
            var snapshot = plan.Snapshot(["wy", "tx", "kg"]);
            Check(snapshot["candidates"]!.AsArray().Count == 2);
            Check(snapshot["song"]!["songId"]!.GetValue<string>() == song["songId"]!.GetValue<string>());
            Check(snapshot["song"]!["source_data"]!["source"]!.GetValue<string>() == "wy");
            Check(song["sources"]!.AsArray().Count == 1, "matching must not mutate the input queue song");
            return Task.CompletedTask;
        });
        yield return new("Official selection source history is account policy request generation and TTL bound without touching reads", async () =>
        {
            await using var store = new MemoryStore(); var clock = new Clock();
            var cache = new MusicSourceCache(store, clock);
            var song = SongObject("tx", "FixtureMID");
            string signature = Hash('a'), policy = Hash('b');
            var info = SourceInfo("account", "song", signature, policy, clock.Now);
            info["quality"] = "flac";
            info["recording"] = song["sources"]![0]!.DeepClone();
            await cache.ConfirmAsync("account", "song", signature, info, default);
            int writes = store.Writes;
            var restarted = new MusicSourceCache(store, clock);
            var hit = await restarted.ReadHistoryAsync("account", "song", policy, "auto", 7, default);
            Check(hit is not null && hit["recording"] is not null && hit["confirmedAt"]!.GetValue<long>() == clock.Now);
            Check(hit!["url"] is null && hit["headers"] is null);
            Check(await restarted.ReadHistoryAsync("other-account", "song", policy, "auto", 7, default) is null);
            Check(await restarted.ReadHistoryAsync("account", "song", Hash('c'), "auto", 7, default) is null);
            Check(await restarted.ReadHistoryAsync("account", "song", policy, "320k", 7, default) is null);
            Check(await restarted.ReadHistoryAsync("account", "song", policy, "auto", 8, default) is null);
            Check(store.Writes == writes, "lookup must not renew history");
            clock.Now += MusicSourceCache.HistoryTtl;
            Check(await restarted.ReadHistoryAsync("account", "song", policy, "auto", 7, default) is null);
        });
        yield return new("Official selection remembers compatible confirmed source before primary but not foreign or quarantined history", () =>
        {
            var song = SongObject("wy", "1"); var other = SongObject("tx", "FixtureMID");
            var row = other["sources"]![0]!.AsObject();
            var history = new JsonObject { ["platform"] = "tx", ["sourceKey"] = row["sourceKey"]!.DeepClone(),
                ["recording"] = row.DeepClone(), ["confirmedAt"] = 100L };
            var plan = new MusicOfficialCandidates(song); plan.Remember(history, "tx");
            Check(plan.Snapshot(["wy", "tx"])["candidates"]![0]!["platform"]!.GetValue<string>() == "tx");
            var foreign = new MusicOfficialCandidates(song); foreign.Remember(history, "kg");
            Check(foreign.Snapshot(["wy", "tx"])["candidates"]!.AsArray().Count == 1);
            song["quarantinedSources"] = new JsonArray(row.DeepClone());
            var rejected = new MusicOfficialCandidates(song); rejected.Remember(history, "tx");
            Check(rejected.Snapshot(["wy", "tx"])["candidates"]!.AsArray().Count == 1);
            return Task.CompletedTask;
        });
        yield return new("Official candidate lookup is local and discovery uses native QQ catalog without Node or search history", async () =>
        {
            await using var scene = new Scene();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            int calls = scene.Handler.Calls, writes = scene.Store.Writes;
            var input = SongObject("wy", "1");
            var local = await scene.Candidates(input, policy, false);
            Check(local.GetProperty("candidates").GetArrayLength() == 0 && scene.Handler.Calls == calls &&
                scene.Store.Writes == writes);
            var discovered = await scene.Candidates(input, policy, true);
            Check(discovered.GetProperty("candidates").GetArrayLength() == 1 &&
                discovered.GetProperty("candidates")[0].GetProperty("platform").GetString() == "tx");
            Check(discovered.GetProperty("song").GetProperty("songId").GetString() == input["songId"]!.GetValue<string>());
            Check(scene.Handler.Searches == 1 && scene.Runtime.Calls == 0 && scene.Store.Writes == writes);
        });
        yield return new("Official candidate lookup reports incompatible current cache data without fallback or overwrite", async () =>
        {
            await using var scene = new Scene();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            byte[] incompatible = Encoding.UTF8.GetBytes("""{"schema":2,"scopes":{}}""");
            scene.Store.Data[MusicSourceCache.Namespace] = incompatible.ToArray();
            int calls = scene.Handler.Calls, writes = scene.Store.Writes;
            var result = await scene.App.ExecuteAsync("music.official.candidates", new {
                song = SongObject("wy", "1"), quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString(), discover = false
            });
            Check(!result.GetProperty("ok").GetBoolean() &&
                result.GetProperty("code").GetString() == "CACHE_DATA_INCOMPATIBLE");
            Check(scene.Handler.Calls == calls && scene.Store.Writes == writes &&
                scene.Store.Data[MusicSourceCache.Namespace].SequenceEqual(incompatible));
        });
        yield return new("Official source expansion bounds pages and carries provider pagination without writes", async () =>
        {
            await using var scene = new Scene(); await scene.Login();
            scene.Handler.PagedSearch = true;
            var policy = await scene.Ok("music.quality.settings.get");
            int writes = scene.Store.Writes;
            var first = await scene.Candidates(SongObject("wy", "1"), policy, true);
            Check(first.GetProperty("hasMore").GetBoolean());
            var next = await scene.Ok("music.official.candidates", new {
                song = first.GetProperty("song"), quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString(), discover = true, page = 2
            });
            Check(!next.GetProperty("hasMore").GetBoolean() && scene.Handler.LastPage == 2 &&
                next.GetProperty("candidates").GetArrayLength() == 1 && scene.Store.Writes == writes);
            var invalid = await scene.App.ExecuteAsync("music.official.candidates", new {
                song = first.GetProperty("song"), quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString(), discover = true, page = 4
            });
            Check(!invalid.GetProperty("ok").GetBoolean() && scene.Handler.Searches == 2);
        });
        yield return new("Official candidate discovery failure is not a successful empty platform and logout fences delayed search", async () =>
        {
            await using var scene = new Scene();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            scene.Handler.FailSearch = true;
            var failed = await scene.Candidates(SongObject("wy", "1"), policy, true);
            Check(failed.GetProperty("failures").GetArrayLength() == 1 &&
                failed.GetProperty("candidates").GetArrayLength() == 0);
            scene.Handler.FailSearch = false; scene.Handler.HoldSearch = true;
            var pending = scene.App.ExecuteAsync("music.official.candidates", new {
                song = SongObject("wy", "1"), quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString(), discover = true
            });
            await scene.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scene.Ok("music.accounts.logout", new { platform = "tx" });
            scene.Handler.Release.TrySetResult();
            try { Check(!(await pending).GetProperty("ok").GetBoolean(), "retired account must not publish candidates"); }
            catch (OperationCanceledException) { }
            Check(scene.Runtime.Calls == 0);
        });
        yield return new("Official confirmed discovered recording survives owner restart without URL reuse or new search", async () =>
        {
            await using var store = new MemoryStore();
            string songId;
            await using (var scene = new Scene(store))
            {
                await scene.Login();
                var policy = await scene.Ok("music.quality.settings.get");
                var plan = await scene.Candidates(SongObject("wy", "1"), policy, true);
                var song = plan.GetProperty("song"); songId = song.GetProperty("songId").GetString()!;
                var target = plan.GetProperty("candidates")[0];
                var input = new { platform = "tx", song, quality = "320k",
                    sourceKey = target.GetProperty("sourceKey").GetString(),
                    qualityPolicySignature = policy.GetProperty("signature").GetString() };
                await scene.Ok("music.official.resolve", input);
                Check(store.Root(MusicSourceCache.Namespace)["scopes"]!.AsObject().First().Value!["pointers"]!.AsObject().Count == 0);
                await scene.Ok("music.official.confirm", input);
            }
            await using (var scene = new Scene(store))
            {
                var policy = await scene.Ok("music.quality.settings.get");
                var plan = await scene.Candidates(SongObject("wy", "1"), policy, false);
                Check(plan.GetProperty("song").GetProperty("songId").GetString() == songId &&
                    plan.GetProperty("candidates").GetArrayLength() == 1 && scene.Handler.Calls == 0);
                var snapshots = plan.GetProperty("song").GetProperty("sources").EnumerateArray().ToArray();
                Check(snapshots.Any(row => row.GetProperty("source").GetString() == "tx"));
            }
        });
        yield return new("Official automatic module plays discovered recording with stable queue identity and manual selection stays exact", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var scene = new Scene();
            var output = new Output();
            await using var module = new MusicCapability(scene.App, scene.Store, output, new Dialogs());
            await module.InitializeAsync(default); await scene.Login();
            var original = SongObject("wy", "1");
            await scene.Ok("music.queue.replace", new { songs = new[] { original }, index = 0 });
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var row = module.QueueSurfaceRows.Single();
            string entry = row.EntryId;
            await module.PlayQueueSurfaceItemAsync(row);
            Check(output.Opens == 1 && output.Identity == entry && module.CurrentSource.Contains("QQ") &&
                output.Uri!.Host.EndsWith(".qqmusic.qq.com"),
                $"native auto consumer: status={module.Status}; opens={output.Opens}; identity={output.Identity}; " +
                $"source={module.CurrentSource}; uri={output.Uri}; calls={scene.Handler.Calls}; searches={scene.Handler.Searches}");
            Check(module.RecordingCandidates.Any(row => row.Platform == "tx") &&
                module.LyricSources.Any(row => row.Label.StartsWith("tx")), "accepted mappings reach UI consumers");
            int searches = scene.Handler.Searches;
            module.SelectedRecording = module.RecordingCandidates.Single(row => row.Platform == "wy");
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single()); // WY has no account: explicit choice cannot silently pick QQ.
            Check(output.Opens == 1 && scene.Handler.Searches == searches && scene.Runtime.Calls == 0);
            var queue = await scene.Ok("music.queue.get");
            Check(queue.GetProperty("current").GetProperty("song").GetProperty("source_data").GetProperty("source").GetString() == "wy");
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
        yield return new("Official native resolve rejects metadata duration mismatch before caching media", async () =>
        {
            await using var scene = new Scene(); await scene.Login();
            var result = await scene.App.ExecuteAsync("music.official.resolve", new {
                platform = "tx", song = SongObject("tx", "FixtureMID", duration: 180), quality = "320k"
            });
            Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "RECORDING_MISMATCH");
            Check(!scene.Store.Data.TryGetValue(MusicSourceCache.Namespace, out var bytes) ||
                !Encoding.UTF8.GetString(bytes).Contains("vkey="), "rejected recording never becomes a URL candidate");
        });
        yield return new("Official automatic denial reaches the user instead of a generic operation failure", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var scene = new Scene();
            var output = new Output();
            await using var module = new MusicCapability(scene.App, scene.Store, output, new Dialogs());
            await module.InitializeAsync(default); await scene.Login();
            scene.Handler.Member = false;
            await scene.Ok("music.queue.replace", new { songs = new[] { SongObject("tx", "FixtureMID") }, index = 0 });
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single());
            Check(module.Status == "当前官方音源需要有效会员", module.Status);
            Check(output.Opens == 0 && scene.Runtime.Calls == 0);
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
    }

    private static JsonObject SongObject(string platform, string id, string title = "Same Song", double duration = 100)
        => Song(platform, id, title, duration);
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MemoryStore Store;
        internal readonly NoRuntime Runtime = new();
        internal readonly Transport Handler = new();
        internal readonly MusicNativeApplication App;
        internal Scene(MemoryStore? store = null)
        { Store = store ?? new(); App = new(Store, Runtime, Handler); }
        internal Task<JsonElement> Login() => Ok("music.accounts.import",
            new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" });
        internal async Task<JsonElement> Ok(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input);
            Check(result.GetProperty("ok").GetBoolean(), method + ": " +
                (result.TryGetProperty("code", out var code) ? code.GetString() : "failed"));
            return result;
        }
        internal Task<JsonElement> Candidates(JsonObject song, JsonElement policy, bool discover) =>
            Ok("music.official.candidates", new { song, quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString(), discover });
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Official selection invoked Node"); }
    }
    private sealed class Transport : HttpMessageHandler
    {
        internal int Calls, Searches;
        internal bool FailSearch, HoldSearch;
        internal bool PagedSearch;
        internal bool Member = true;
        internal int LastPage;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (request.Content is null) return new(HttpStatusCode.ServiceUnavailable);
            var input = JsonNode.Parse(await request.Content.ReadAsStringAsync(token))!;
            string? method = input["req_0"]?["method"]?.GetValue<string>();
            if (method == "DoSearchForQQMusicMobile")
            {
                Searches++;
                LastPage = input["req_0"]!["param"]!["page_num"]!.GetValue<int>();
                if (HoldSearch) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
                if (FailSearch) return new(HttpStatusCode.ServiceUnavailable);
                return Qq(new { meta = new { sum = PagedSearch ? 31 : 1, nextpage = PagedSearch && LastPage == 1 ? 2 : -1 }, body = new { item_song = new[] {
                    new { mid = "FixtureMID", id = 10, type = 0, title = "Same Song", interval = 100,
                        singer = new[] { new { name = "Artist" } }, file = new { size_320mp3 = 120 } }
                } } });
            }
            return method switch {
                "GetLoginUserInfo" => Qq(new { code = 0 }),
                "GetHomepageHeader" => Qq(new { Info = new { BaseInfo = new { EncryptedUin = "FixtureEUIN", Name = "Synthetic", Avatar = "" } } }),
                "vip_login_base" => Qq(new { identity = new { vip = Member ? 1 : 0 }, userinfo = new { expire = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }),
                "CgiGetTrackInfo" => Qq(new { tracks = new[] { new { mid = "FixtureMID", interval = 100, file = new { size_320mp3 = 120 } } } }),
                "UrlGetVkey" => Qq(new { expiration = 120, midurlinfo = new[] { new { songmid = "FixtureMID", result = 0,
                    filename = "M800FixtureMIDFixtureMID.mp3", purl = "M800FixtureMIDFixtureMID.mp3?vkey=synthetic", ekey = "" } } }),
                _ => new(HttpStatusCode.ServiceUnavailable)
            };
        }
        private static HttpResponseMessage Qq(object body) => new(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(new { code = 0, req_0 = new { code = 0, data = body } }),
                Encoding.UTF8, "application/json") };
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId { get; private set; }
        public string? Identity { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 100;
        public int Volume { get; set; } = 50;
        internal int Opens;
        internal Uri? Uri;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token)
        { token.ThrowIfCancellationRequested(); OwnerId = owner; Identity = identity; Uri = uri; HasMedia = true; Opens++; return Task.CompletedTask; }
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() { HasMedia = false; OwnerId = Identity = null; return Task.CompletedTask; }
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
}
