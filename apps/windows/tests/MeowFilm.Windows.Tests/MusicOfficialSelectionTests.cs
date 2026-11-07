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
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicOfficialSelectionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Official selection checks every retained recording including non-official mappings", () =>
        {
            JsonObject Track(string platform, string id, double duration) =>
                MusicSongFixture.Online(platform, id, "Pairwise", "Artist", duration: duration);
            var song = Track("wy", "root", 103);
            song["sources"]!.AsArray().Add(Track("kw", "mapped", 100)["sources"]![0]!.DeepClone());
            song = NativeMusicSong.Normalize(song);
            song = Merged(song, Track("tx", "compatible", 103), Track("kg", "conflict", 106));
            var plan = new MusicOfficialCandidates(song);
            var conflict = Track("tx", "remembered-conflict", 106)["sources"]![0]!.AsObject();
            plan.Remember(new JsonObject { ["platform"] = "tx", ["sourceKey"] = conflict["sourceKey"]!.DeepClone(),
                ["recording"] = conflict.DeepClone(), ["confirmedAt"] = 100L }, "tx");
            var snapshot = plan.Snapshot(["wy", "tx", "kg"]);
            Check(snapshot["candidates"]!.AsArray().Count == 2 &&
                !snapshot["candidates"]!.AsArray().Any(candidate => candidate!["platform"]!.GetValue<string>() == "kg"));
            Check(snapshot["song"]!["sources"]!.AsArray().Count == 3);
            Check(JsonNode.DeepEquals(snapshot["song"], NativeMusicSong.Normalize(snapshot["song"])));
            _ = SongRow.Read(JsonSerializer.SerializeToElement(snapshot["song"]));
            return Task.CompletedTask;
        });
        yield return new("Official selection candidate limits never discard original song mappings", () =>
        {
            var song = SongObject("wy", "9");
            for (int i = 1; i <= 8; i++) song["sources"]!.AsArray().Add(SongObject("wy", i.ToString())["sources"]![0]!.DeepClone());
            song = NativeMusicSong.Normalize(song);
            var plan = new MusicOfficialCandidates(song);
            var original = song.DeepClone(); song["title"] = "external edit";
            var snapshot = plan.Snapshot(["wy"]);
            Check(snapshot["candidates"]!.AsArray().Count == 8 && snapshot["song"]!["sources"]!.AsArray().Count == 9);
            Check(JsonNode.DeepEquals(snapshot["song"], original));
            return Task.CompletedTask;
        });
        yield return new("Official selection recordings reject other editions and duration conflicts", () =>
        {
            var song = SongObject("wy", "1");
            var merged = Merged(song, SongObject("tx", "FixtureMID"), SongObject("tx", "Live", title: "Same Song (Live)"),
                SongObject("tx", "Long", duration: 160), SongObject("tx", "Unknown", duration: 0));
            var plan = new MusicOfficialCandidates(merged);
            var snapshot = plan.Snapshot(["wy", "tx", "kg"]);
            Check(snapshot["candidates"]!.AsArray().Count == 2);
            Check(snapshot["song"]!["songId"]!.GetValue<string>() == song["songId"]!.GetValue<string>());
            Check(MusicSongFixture.Info(snapshot["song"]!)["source"]!.GetValue<string>() == "wy");
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
        yield return new("Official selection remembers compatible confirmed source before primary but not foreign history", () =>
        {
            var song = SongObject("wy", "1"); var other = SongObject("tx", "FixtureMID");
            var row = other["sources"]![0]!.AsObject();
            var history = new JsonObject { ["platform"] = "tx", ["sourceKey"] = row["sourceKey"]!.DeepClone(),
                ["recording"] = row.DeepClone(), ["confirmedAt"] = 100L };
            var plan = new MusicOfficialCandidates(song); plan.Remember(history, "tx");
            Check(plan.Snapshot(["wy", "tx"])["candidates"]![0]!["platform"]!.GetValue<string>() == "tx");
            var foreign = new MusicOfficialCandidates(song); foreign.Remember(history, "kg");
            Check(foreign.Snapshot(["wy", "tx"])["candidates"]!.AsArray().Count == 1);
            return Task.CompletedTask;
        });
        yield return new("Official candidate lookup is local and discovery uses native QQ catalog without Node or search history", async () =>
        {
            await using var scene = await Scene.CreateAsync();
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
        yield return new("Disabled official QQ is excluded from discovery and rejects every audio and cache entry without HTTP", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = SongObject("tx", "FixtureMID");
            var input = new { platform = "tx", song, quality = "320k" };
            var accepted = await scene.Ok("music.official.resolve", input);
            string scope = accepted.GetProperty("scope").GetString()!;
            long generation = accepted.GetProperty("generation").GetInt64();
            Check(scene.App.IsOfficialPlaybackCurrent("tx", scope, generation));
            await scene.Ok("music.official.confirm", input);
            await scene.Ok("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = new[] { "wy", "kg" } });
            Check(!scene.App.IsOfficialPlaybackCurrent("tx", scope, generation));
            var policy = await scene.Ok("music.quality.settings.get");
            int calls = scene.Handler.Calls, writes = scene.Store.Writes, vkeys = scene.Handler.Vkeys;
            byte[] cached = scene.Store.Data[MusicSourceCache.Namespace].ToArray();
            var candidates = await scene.Candidates(song, policy, true);
            Check(candidates.GetProperty("candidates").GetArrayLength() == 0 && scene.Handler.Searches == 0);
            foreach (string method in new[] { "resolve", "cacheContext", "authorize", "confirm" })
                Code(await scene.App.ExecuteAsync("music.official." + method, input), "OFFICIAL_SOURCE_DISABLED");
            Check(scene.Handler.Calls == calls && scene.Handler.Vkeys == vkeys && scene.Store.Writes == writes);
            Check(scene.Store.Data[MusicSourceCache.Namespace].SequenceEqual(cached),
                "disabled cache entry points cannot create candidates, authorization or confirmation");
            await scene.Ok("music.officialSources.settings.save",
                new { ifRevision = 1, enabled = new[] { "wy", "tx", "kg" } });
            await scene.Ok("music.official.cacheContext", input);
            Check(scene.App.IsOfficialPlaybackCurrent("tx", scope, generation) && scene.Handler.Calls == calls,
                "reenabling must reuse the untouched account without a new login");
            Check(scene.Runtime.Calls == 0);
        });
        yield return new("Empty official source selection retains independent QQ search account script and metadata settings", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var before = await scene.Ok("music.accounts.list");
            var quality = await scene.Ok("music.quality.settings.get");
            var metadata = await scene.Ok("music.metadata.settings.get");
            var online = await scene.Ok("music.settings.get");
            var scripts = await scene.Ok("music.sources.list");
            await scene.Ok("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = Array.Empty<string>() });
            Check((await scene.Ok("music.accounts.list")).GetRawText() == before.GetRawText(),
                "official source switches must not revoke an account");
            Check((await scene.Ok("music.quality.settings.get")).GetRawText() == quality.GetRawText());
            Check((await scene.Ok("music.metadata.settings.get")).GetRawText() == metadata.GetRawText());
            Check((await scene.Ok("music.settings.get")).GetRawText() == online.GetRawText());
            Check((await scene.Ok("music.sources.list")).GetRawText() == scripts.GetRawText());
            var empty = await scene.Candidates(SongObject("tx", "FixtureMID"), quality, true);
            Check(empty.GetProperty("candidates").GetArrayLength() == 0 && scene.Handler.Searches == 0);
            var search = await scene.Ok("music.catalog.search", new {
                platform = "tx", keyword = "independent search", kind = "songs"
            });
            Check(search.GetProperty("items").GetArrayLength() == 1 && scene.Handler.Searches == 1);
            await scene.Ok("music.accounts.validate", new { platform = "tx" });
            var account = (await scene.Ok("music.accounts.list")).GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("platform").GetString() == "tx");
            Check(account.GetProperty("state").GetString() == "verified" && scene.Runtime.Calls == 0);
        });
        yield return new("Official source disable discards a late QQ media URL and cannot republish cache authority", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var input = new { platform = "tx", song = SongObject("tx", "FixtureMID"), quality = "320k" };
            var context = await scene.Ok("music.official.cacheContext", input);
            scene.Handler.HoldMethod = "UrlGetVkey";
            var pending = scene.App.ExecuteAsync("music.official.resolve", input);
            try
            {
                await scene.Handler.MethodEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await scene.Ok("music.officialSources.settings.save",
                    new { ifRevision = 0, enabled = new[] { "wy", "kg" } });
            }
            finally { scene.Handler.MethodRelease.TrySetResult(); }
            Code(await pending.WaitAsync(TimeSpan.FromSeconds(5)), "CANCELLED");
            Check(scene.Handler.Vkeys == 1 && !scene.Store.Data.ContainsKey(MusicSourceCache.Namespace));
            Check(!scene.App.IsOfficialPlaybackCurrent("tx", context.GetProperty("scope").GetString()!,
                context.GetProperty("generation").GetInt64()), "late native success cannot restore cache access");
            Check(scene.Runtime.Calls == 0);
        });
        yield return new("Unknown official source CAS fails native admission closed until an explicit settings read", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = SongObject("tx", "FixtureMID");
            var input = new { platform = "tx", song, quality = "320k" };
            var context = await scene.Ok("music.official.cacheContext", input);
            var policy = await scene.Ok("music.quality.settings.get");
            scene.Store.WriteReceipt = new DocumentFingerprint(true, 1, new string('0', 64));
            Code(await scene.App.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = new[] { "wy", "kg" } }), "COMMIT_UNKNOWN");
            int calls = scene.Handler.Calls, writes = scene.Store.Writes;
            Check(!scene.App.IsOfficialPlaybackCurrent("tx", context.GetProperty("scope").GetString()!,
                context.GetProperty("generation").GetInt64()));
            Code(await scene.App.ExecuteAsync("music.official.candidates", new {
                song, quality = "320k", qualityPolicySignature = policy.GetProperty("signature").GetString()
            }), "PENDING_WRITE");
            foreach (string method in new[] { "resolve", "cacheContext", "authorize", "confirm" })
                Code(await scene.App.ExecuteAsync("music.official." + method, input), "PENDING_WRITE");
            Check(scene.Handler.Calls == calls && scene.Store.Writes == writes,
                "native readers must neither reconcile nor replay an unknown settings write");
            scene.Store.WriteReceipt = null;
            var settings = (await scene.Ok("music.officialSources.settings.get")).GetProperty("settings");
            Check(settings.GetProperty("revision").GetInt64() == 1 &&
                settings.GetProperty("enabled").EnumerateArray().All(value => value.GetString() != "tx"));
            foreach (string method in new[] { "resolve", "cacheContext", "authorize", "confirm" })
                Code(await scene.App.ExecuteAsync("music.official." + method, input), "OFFICIAL_SOURCE_DISABLED");
            Check(scene.Handler.Calls == calls && scene.Store.Writes == writes && scene.Runtime.Calls == 0);
        });
        yield return new("Official candidate lookup reports incompatible current cache data without fallback or overwrite", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            byte[] incompatible = Encoding.UTF8.GetBytes("""{"schema":2,"scopes":{}}""");
            scene.Store.Data[MusicSourceCache.Namespace] = incompatible.ToArray();
            int calls = scene.Handler.Calls, writes = scene.Store.Writes;
            var result = await scene.App.ExecuteAsync("music.official.candidates", new {
                song = SongObject("wy", "1"), quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString()
            });
            Check(!result.GetProperty("ok").GetBoolean() &&
                result.GetProperty("code").GetString() == "CACHE_DATA_INCOMPATIBLE");
            Check(scene.Handler.Calls == calls && scene.Store.Writes == writes &&
                scene.Store.Data[MusicSourceCache.Namespace].SequenceEqual(incompatible));
        });
        yield return new("Shared supplementary matching isolates each requested platform account frame", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = SongObject("wy", "1");
            var result = await scene.Ok("music.songs.complete", new { song, platforms = new[] { "tx", "kg" } });
            Check(result.GetProperty("song").GetProperty("songId").GetString() == song["songId"]!.GetValue<string>() &&
                result.GetProperty("song").GetProperty("sources").EnumerateArray().Any(row =>
                    row.GetProperty("source").GetString() == "tx") && scene.Handler.Searches == 1 &&
                scene.Runtime.Calls == 0, "a second platform cannot reuse QQ's account frame or abort its match");
        });
        yield return new("Official candidate discovery failure is not a successful empty platform and logout fences delayed search", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            scene.Handler.FailSearch = true;
            var failed = await scene.Candidates(SongObject("wy", "1"), policy, true);
            Check(failed.GetProperty("failures").GetArrayLength() == 1 &&
                failed.GetProperty("candidates").GetArrayLength() == 0);
            scene.Handler.FailSearch = false; scene.Handler.HoldSearch = true;
            var pending = scene.App.ExecuteAsync("music.songs.complete", new { song = SongObject("wy", "1"), platforms = new[] { "tx" } });
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
            await using (var scene = await Scene.CreateAsync(store))
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
            await using (var scene = await Scene.CreateAsync(store))
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
            await using var scene = await Scene.CreateAsync();
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
            Check(MusicSongFixture.Info(queue.GetProperty("current").GetProperty("song")).GetProperty("source").GetString() == "wy");
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
        yield return new("Official native resolve rejects metadata duration mismatch before caching media", async () =>
        {
            await using var scene = await Scene.CreateAsync(); await scene.Login();
            var result = await scene.App.ExecuteAsync("music.official.resolve", new {
                platform = "tx", song = SongObject("tx", "FixtureMID", duration: 180), quality = "320k"
            });
            Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "RECORDING_MISMATCH");
            Check(!scene.Store.Data.TryGetValue(MusicSourceCache.Namespace, out var bytes) ||
                !Encoding.UTF8.GetString(bytes).Contains("vkey="), "rejected recording never becomes a URL candidate");
        });
        yield return new("Official automatic upstream permission denial reaches the user without a local membership gate", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var scene = await Scene.CreateAsync();
            var output = new Output();
            await using var module = new MusicCapability(scene.App, scene.Store, output, new Dialogs());
            await module.InitializeAsync(default); await scene.Login();
            scene.Handler.Member = false;
            scene.Handler.DenyMedia = true;
            await module.ActivateQualitySettingsAsync();
            await module.SetPlaybackQualityRequestAsync("320k");
            await scene.Ok("music.queue.replace", new { songs = new[] { SongObject("tx", "FixtureMID") }, index = 0 });
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single());
            Check(module.Status == "当前歌曲暂无完整播放权限" && scene.Handler.Vkeys > 0, module.Status);
            Check(output.Opens == 0 && scene.Runtime.Calls == 0);
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
    }

    private static JsonObject Merged(JsonObject song, params JsonObject[] others)
    {
        var recordings = new NativeMusicSong.Recordings(NativeMusicSong.Normalize(song));
        foreach (var other in others)
        {
            var incoming = new NativeMusicSong.Recordings(NativeMusicSong.Normalize(other));
            if (recordings.CanMerge(incoming)) recordings.TryMerge(incoming);
        }
        return NativeMusicSong.Normalize(recordings.ToSong());
    }
    private static JsonObject SongObject(string platform, string id, string title = "Same Song", double duration = 100)
        => Song(platform, id, title, duration);
    private static void Code(JsonElement result, string expected) =>
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == expected,
            $"Expected {expected}: {result.GetRawText()}");
    private sealed class Scene : IAsyncDisposable
    {
        internal readonly MemoryStore Store;
        internal readonly NoRuntime Runtime = new();
        internal readonly Transport Handler = new();
        internal readonly MusicNativeApplication App;
        private Scene(MemoryStore store)
        { Store = store; App = new(Store, Runtime, Handler); }
        internal static async Task<Scene> CreateAsync(MemoryStore? store = null)
        {
            store ??= new();
            await MusicClientContextFixture.SeedQqAsync(store);
            return new(store);
        }
        internal Task<JsonElement> Login() => Ok("music.accounts.import",
            new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" });
        internal async Task<JsonElement> Ok(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input);
            Check(result.GetProperty("ok").GetBoolean(), method + ": " +
                (result.TryGetProperty("code", out var code) ? code.GetString() : "failed"));
            return result;
        }
        /// <summary>Built-in candidates of the song; a discovering lookup first completes the
        /// missing QQ recording through the one shared supplementary search.</summary>
        internal async Task<JsonElement> Candidates(JsonObject song, JsonElement policy, bool discover)
        {
            JsonNode subject = song;
            JsonNode? failures = null;
            if (discover)
            {
                var complete = await Ok("music.songs.complete", new { song, platforms = new[] { "tx" } });
                subject = JsonNode.Parse(complete.GetProperty("song").GetRawText())!;
                failures = JsonNode.Parse(complete.GetProperty("failures").GetRawText());
            }
            var result = JsonNode.Parse((await Ok("music.official.candidates", new { song = subject, quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString() })).GetRawText())!.AsObject();
            result["failures"] = failures ?? new JsonArray();
            return JsonSerializer.SerializeToElement(result);
        }
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Native business must not upload script sources"); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            if (method == "music.script.sources.list")
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, contract = "music.script.v3", value = new { items = Array.Empty<object>() } }));
            Calls++; throw new InvalidOperationException("Official selection invoked a script");
        }
    }
    private sealed class Transport : HttpMessageHandler
    {
        internal int Calls, Searches, MediaRequests, Vkeys;
        internal bool FailSearch, HoldSearch;
        internal bool Member = true;
        internal bool DenyMedia;
        internal int LastPage;
        internal string? HoldMethod;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource MethodEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource MethodRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                if (HoldSearch)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(token);
                }
                if (FailSearch) return new(HttpStatusCode.ServiceUnavailable);
                return Qq(new { meta = new { sum = 1, nextpage = -1 }, body = new { item_song = new[] {
                    new { mid = "FixtureMID", id = 10, type = 0, title = "Same Song", interval = 100,
                        singer = new[] { new { name = "Artist" } }, file = new { size_320mp3 = 120 } }
                } } });
            }
            if (method is "CgiGetTrackInfo" or "UrlGetVkey") Interlocked.Increment(ref MediaRequests);
            if (method == "UrlGetVkey") Interlocked.Increment(ref Vkeys);
            if (method == HoldMethod && method is not null)
            {
                MethodEntered.TrySetResult();
                // Deliberately ignore cancellation to exercise native publication fencing.
                await MethodRelease.Task;
            }
            return method switch {
                "GetLoginUserInfo" => Qq(new { code = 0 }),
                "GetHomepageHeader" => Qq(new { Info = new { BaseInfo = new { EncryptedUin = "FixtureEUIN", Name = "Synthetic", Avatar = "" } } }),
                "vip_login_base" => Qq(new { identity = new { vip = Member ? 1 : 0 }, userinfo = new { expire = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }),
                "CgiGetTrackInfo" => Qq(new { tracks = new[] { new { mid = "FixtureMID", interval = 100, file = new { size_320mp3 = 120 } } } }),
                "UrlGetVkey" => Qq(new { expiration = 120, midurlinfo = new[] { new { songmid = "FixtureMID", result = 0,
                    filename = "M800FixtureMIDFixtureMID.mp3", purl = DenyMedia ? "" : "M800FixtureMIDFixtureMID.mp3?vkey=synthetic", ekey = "" } } }),
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
