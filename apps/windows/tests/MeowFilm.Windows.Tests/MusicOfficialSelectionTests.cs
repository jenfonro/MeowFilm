using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
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
        yield return new("Official selection uses the fixed recording basis including non-official mappings", async () =>
        {
            await using var store = new MemoryStore();
            await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
            JsonObject Track(string platform, string id, double duration) =>
                MusicSongFixture.Online(platform, id, "Pairwise", "Artist", duration: duration);
            var song = Track("wy", "root", 103);
            song["sources"]!.AsArray().Add(Track("kw", "mapped", 100)["sources"]![0]!.DeepClone());
            song = await MusicSongFixture.AdoptAsync(library, song);
            song = Merged(song, Track("tx", "compatible", 103), Track("kg", "conflict", 106));
            var plan = new MusicOfficialCandidates(song);
            var conflict = Track("tx", "remembered-conflict", 106)["sources"]![0]!.AsObject();
            plan.Remember(new JsonObject { ["platform"] = "tx", ["sourceKey"] = conflict["sourceKey"]!.DeepClone(),
                ["recording"] = conflict.DeepClone(), ["confirmedAt"] = 100L }, "tx");
            var snapshot = plan.Snapshot(["wy", "tx", "kg"]);
            Check(snapshot["candidates"]!.AsArray().Count == 4 &&
                snapshot["candidates"]!.AsArray().Any(candidate => candidate!["platform"]!.GetValue<string>() == "kg"));
            Check(snapshot["song"]!["sources"]!.AsArray().Count == 5);
            Check(JsonNode.DeepEquals(snapshot["song"], NativeMusicSong.Normalize(snapshot["song"])));
            _ = SongRow.Read(JsonSerializer.SerializeToElement(snapshot["song"]));
        });
        yield return new("Official selection keeps every accepted recording selectable without another catalog request", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var scene = await Scene.CreateAsync();
            var song = SongObject("wy", "primary");
            for (int i = 0; i < 34; i++)
                song["sources"]!.AsArray().Add(SongObject("wy", "recording-" + i)["sources"]![0]!.DeepClone());
            song = await MusicSongFixture.AdoptAsync(scene.App, song);
            var plan = new MusicOfficialCandidates(song);
            var original = song.DeepClone(); song["title"] = "external edit";
            var snapshot = plan.Snapshot(["wy", "tx", "kg"]);
            string primary = original["sources"]![0]!["sourceKey"]!.GetValue<string>();
            var keys = original["sources"]!.AsArray().Select(row => row!["sourceKey"]!.GetValue<string>())
                .OrderBy(key => key == primary ? 0 : 1).ThenBy(key => key, StringComparer.Ordinal).ToArray();
            Check(snapshot["candidates"]!.AsArray().Select(row => row!["sourceKey"]!.GetValue<string>()).SequenceEqual(keys) &&
                snapshot["song"]!["sources"]!.AsArray().Count == keys.Length);
            Check(JsonNode.DeepEquals(snapshot["song"], original));
            var policy = await scene.Ok("music.quality.settings.get");
            var candidates = await scene.Ok("music.official.candidates", new { song = original, quality = "128k",
                qualityPolicySignature = policy.GetProperty("signature").GetString() });
            Check(candidates.GetProperty("candidates").EnumerateArray()
                .Select(row => row.GetProperty("sourceKey").GetString()).SequenceEqual(keys) && scene.Runtime.Requests.Count == 0,
                "the public local candidate consumer must retain the complete ordered collection");
            var last = candidates.GetProperty("candidates").EnumerateArray().Last();
            scene.Runtime.OnRequest = (call, _) =>
            {
                Check(call.Operation == "audio" && call.Platform == last.GetProperty("platform").GetString());
                return Task.FromResult(AudioValue(quality: "128k"));
            };
            var media = await scene.Ok("music.official.resolve", new { song = original, quality = "128k",
                platform = last.GetProperty("platform").GetString(), sourceKey = last.GetProperty("sourceKey").GetString() });
            Check(media.GetProperty("sourceKey").GetString() == last.GetProperty("sourceKey").GetString() &&
                media.GetProperty("songId").GetString() == original["songId"]!.GetValue<string>() &&
                scene.Runtime.Requests.Count == 1 && scene.Handler.Searches == 0 && scene.ScriptCalls == 0);
            var settings = JsonNode.Parse(policy.GetProperty("settings").GetRawText())!.AsObject();
            settings["ifRevision"] = settings["revision"]!.DeepClone();
            settings.Remove("schema"); settings.Remove("revision"); settings["request"] = "128k";
            await scene.Ok("music.quality.settings.save", settings);
            var output = new Output();
            await using var module = new MusicCapability(scene.App, scene.Store, output, new Dialogs());
            await module.InitializeAsync(default);
            await scene.Ok("music.settings.save", new { ifRevision = 0, lyrics = false });
            await scene.Ok("music.queue.replace", new { songs = new[] { original }, index = 0 });
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single());
            Check(output.HasMedia && output.Opens == 1 && scene.Runtime.Requests.Count == 1 && scene.Handler.Searches == 0 &&
                scene.ScriptCalls == 0, "the long collection also reaches the real source session and reuses its last cached recording:" + module.Status);
        }));
        yield return new("Official selection recordings reject other editions and duration conflicts", async () =>
        {
            await using var store = new MemoryStore();
            await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
            var song = await MusicSongFixture.AdoptAsync(library, SongObject("wy", "1"));
            var merged = Merged(song, SongObject("tx", "FixtureMID"), SongObject("tx", "Live", title: "Same Song (Live)"),
                SongObject("tx", "Long", duration: 160), SongObject("tx", "Unknown", duration: 0));
            var plan = new MusicOfficialCandidates(merged);
            var snapshot = plan.Snapshot(["wy", "tx", "kg"]);
            Check(snapshot["candidates"]!.AsArray().Count == 2);
            Check(snapshot["song"]!["songId"]!.GetValue<string>() == song["songId"]!.GetValue<string>());
            Check(MusicSongFixture.Info(snapshot["song"]!)["source"]!.GetValue<string>() == "wy");
            Check(song["sources"]!.AsArray().Count == 1, "matching must not mutate the input queue song");
        });
        yield return new("Official selection source history is account policy request generation and TTL bound without touching reads", async () =>
        {
            await using var store = new MemoryStore(); var clock = new Clock();
            await using var cacheStore = new NativeTestDatabase(store);
            var cache = new MusicSourceCache(cacheStore, clock);
            var song = SongObject("tx", "FixtureMID");
            string signature = Hash('a'), policy = Hash('b');
            var info = SourceInfo("account", "song", signature, policy, clock.Now);
            info["quality"] = "flac";
            info["recording"] = song["sources"]![0]!.DeepClone();
            await cache.ConfirmAsync("account", "song", signature, info, default);
            await cacheStore.DrainAsync();
            int writes = store.Writes;
            await using var reopened = new NativeTestDatabase(store);
            var restarted = new MusicSourceCache(reopened, clock);
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
        yield return new("Official selection remembers compatible confirmed source before primary but not foreign history", async () =>
        {
            await using var store = new MemoryStore();
            await using var library = new MusicLibraryOwner(MusicTestStores.For(store), database: MusicTestStores.For(store).Storage);
            var song = await MusicSongFixture.AdoptAsync(library, SongObject("wy", "1")); var other = SongObject("tx", "FixtureMID");
            var row = other["sources"]![0]!.AsObject();
            var history = new JsonObject { ["platform"] = "tx", ["sourceKey"] = row["sourceKey"]!.DeepClone(),
                ["recording"] = row.DeepClone(), ["confirmedAt"] = 100L };
            var plan = new MusicOfficialCandidates(song); plan.Remember(history, "tx");
            Check(plan.Snapshot(["wy", "tx"])["candidates"]![0]!["platform"]!.GetValue<string>() == "tx");
            var foreign = new MusicOfficialCandidates(song); foreign.Remember(history, "kg");
            Check(foreign.Snapshot(["wy", "tx"])["candidates"]!.AsArray().Count == 1);
        });
        yield return new("Official candidate lookup is local and discovery uses canonical QQ without scripts or search history", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            int calls = scene.Handler.Calls, writes = (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes);
            var input = SongObject("wy", "1");
            var local = await scene.Candidates(input, policy, false);
            Check(local.GetProperty("candidates").GetArrayLength() == 0 && scene.Handler.Calls == calls &&
                (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes) > writes);
            string identity = local.GetProperty("song").GetProperty("songId").GetString()!;
            Check(Guid.TryParse(identity, out _));
            var discovered = await scene.Candidates(input, policy, true);
            Check(discovered.GetProperty("candidates").GetArrayLength() == 1 &&
                discovered.GetProperty("candidates")[0].GetProperty("platform").GetString() == "tx");
            Check(discovered.GetProperty("song").GetProperty("songId").GetString() == identity);
            Check(scene.Handler.Searches == 1 && scene.ScriptCalls == 0 && (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes) > writes);
        });
        yield return new("Disabled official QQ is excluded from discovery and rejects every audio and cache entry without HTTP", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = await MusicSongFixture.AdoptAsync(scene.App, SongObject("tx", "FixtureMID"));
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
            int calls = scene.Handler.Calls, writes = (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes), vkeys = scene.Handler.Vkeys;
            byte[] cached = JsonSerializer.SerializeToUtf8Bytes(scene.CacheBacking.Root(MusicSourceCache.Namespace));
            var candidates = await scene.Candidates(song, policy, true);
            Check(candidates.GetProperty("candidates").GetArrayLength() == 0 && scene.Handler.Searches == 0);
            foreach (string method in new[] { "resolve", "cacheContext", "confirm" })
                Code(await scene.App.ExecuteAsync("music.official." + method, input), "OFFICIAL_SOURCE_DISABLED");
            await scene.CacheStore.DrainAsync();
            Check(scene.Handler.Calls == calls && scene.Handler.Vkeys == vkeys && (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes) == writes);
            Check(JsonSerializer.SerializeToUtf8Bytes(scene.CacheBacking.Root(MusicSourceCache.Namespace)).SequenceEqual(cached),
                "disabled cache entry points cannot create candidates, authorization or confirmation");
            await scene.Ok("music.officialSources.settings.save",
                new { ifRevision = 1, enabled = new[] { "wy", "tx", "kg" } });
            await scene.Ok("music.official.cacheContext", input);
            Check(scene.App.IsOfficialPlaybackCurrent("tx", scope, generation) && scene.Handler.Calls == calls,
                "reenabling must reuse the untouched account without a new login");
            Check(scene.ScriptCalls == 0);
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
            Check(account.GetProperty("state").GetString() == "verified" && scene.ScriptCalls == 0);
        });
        yield return new("Official source disable discards a late QQ media URL and cannot republish cache authority", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = await MusicSongFixture.AdoptAsync(scene.App, SongObject("tx", "FixtureMID"));
            var input = new { platform = "tx", song, quality = "320k" };
            var context = await scene.Ok("music.official.cacheContext", input);
            scene.Handler.HoldMethod = "audio";
            var pending = scene.App.ExecuteAsync("music.official.resolve", input);
            try
            {
                await scene.Handler.MethodEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await scene.Ok("music.officialSources.settings.save",
                    new { ifRevision = 0, enabled = new[] { "wy", "kg" } });
            }
            finally { scene.Handler.MethodRelease.TrySetResult(); }
            Code(await pending.WaitAsync(TimeSpan.FromSeconds(5)), "CANCELLED");
            Check(scene.Handler.Vkeys == 1);
            string scope = context.GetProperty("scope").GetString()!;
            long generation = context.GetProperty("generation").GetInt64();
            string songId = (await scene.Candidates(input.song, await scene.Ok("music.quality.settings.get"), false))
                .GetProperty("song").GetProperty("songId").GetString()!;
            await scene.CacheStore.DrainAsync();
            await using var reopened = new NativeTestDatabase(scene.CacheBacking);
            // An empty invalidation may persist as a fence; it must not publish a URL or history.
            foreach (var cache in new[] { new MusicSourceCache(scene.CacheStore), new MusicSourceCache(reopened) })
            {
                Check(await cache.ReadAsync(scope, songId,
                    context.GetProperty("qualityCacheSignature").GetString()!, default) is null,
                    "late native success cannot publish a reusable media URL");
                Check(await cache.ReadHistoryAsync(scope, songId,
                    context.GetProperty("qualityPolicySignature").GetString()!, "320k", generation, default) is null,
                    "late native success cannot publish confirmed source history");
            }
            Check(!scene.App.IsOfficialPlaybackCurrent("tx", scope, generation),
                "late native success cannot restore cache access");
            Check(scene.ScriptCalls == 0);
        });
        yield return new("Unknown official source CAS fails native admission closed until an explicit settings read", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = await MusicSongFixture.AdoptAsync(scene.App, SongObject("tx", "FixtureMID"));
            var input = new { platform = "tx", song, quality = "320k" };
            var context = await scene.Ok("music.official.cacheContext", input);
            var policy = await scene.Ok("music.quality.settings.get");
            MusicTestStores.For(scene.Store).WriteReceipt = new MusicLibraryRevision(1, Guid.NewGuid().ToString("D"));
            Code(await scene.App.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = 0, enabled = new[] { "wy", "kg" } }), "COMMIT_UNKNOWN");
            int calls = scene.Handler.Calls, writes = MusicTestStores.For(scene.Store).Writes;
            Check(!scene.App.IsOfficialPlaybackCurrent("tx", context.GetProperty("scope").GetString()!,
                context.GetProperty("generation").GetInt64()));
            Code(await scene.App.ExecuteAsync("music.official.candidates", new {
                song, quality = "320k", qualityPolicySignature = policy.GetProperty("signature").GetString()
            }), "PENDING_WRITE");
            foreach (string method in new[] { "resolve", "cacheContext", "confirm" })
                Code(await scene.App.ExecuteAsync("music.official." + method, input), "PENDING_WRITE");
            Check(scene.Handler.Calls == calls && MusicTestStores.For(scene.Store).Writes == writes,
                "native readers must neither reconcile nor replay an unknown settings write");
            MusicTestStores.For(scene.Store).WriteReceipt = null;
            var settings = (await scene.Ok("music.officialSources.settings.get")).GetProperty("settings");
            Check(settings.GetProperty("revision").GetInt64() == 1 &&
                settings.GetProperty("enabled").EnumerateArray().All(value => value.GetString() != "tx"));
            foreach (string method in new[] { "resolve", "cacheContext", "confirm" })
                Code(await scene.App.ExecuteAsync("music.official." + method, input), "OFFICIAL_SOURCE_DISABLED");
            Check(scene.Handler.Calls == calls && MusicTestStores.For(scene.Store).Writes == writes && scene.ScriptCalls == 0);
        });
        yield return new("Official candidate lookup treats absent current cache as missing without network fallback", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var policy = await scene.Ok("music.quality.settings.get");
            await scene.Store.WriteAsync(db => db.Execute("DELETE FROM windows_playback_source"));
            await scene.CacheStore.DrainAsync();
            var song = await MusicSongFixture.AdoptAsync(scene.App, SongObject("wy", "1"));
            int calls = scene.Handler.Calls, writes = (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes);
            var result = await scene.App.ExecuteAsync("music.official.candidates", new {
                song, quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString()
            });
            Check(result.GetProperty("ok").GetBoolean());
            await scene.CacheStore.DrainAsync();
            Check(scene.Handler.Calls == calls && (scene.Store.Writes + MusicTestStores.For(scene.Store).Writes) == writes &&
                await scene.Store.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_playback_source")) == 0);
        });
        yield return new("Shared supplementary matching isolates each requested platform account frame", async () =>
        {
            await using var scene = await Scene.CreateAsync();
            await scene.Login();
            var song = SongObject("wy", "1");
            var result = await scene.Ok("music.songs.complete", new { song, platforms = new[] { "tx", "kg" } });
            var completed = result.GetProperty("song");
            var sources = completed.GetProperty("sources").EnumerateArray().ToArray();
            Check(Guid.TryParse(completed.GetProperty("songId").GetString(), out _) &&
                sources.Single(row => row.GetProperty("source").GetString() == "wy")
                    .GetProperty("musicInfo").GetProperty("songmid").GetString() == "1" &&
                sources.Single(row => row.GetProperty("source").GetString() == "tx")
                    .GetProperty("musicInfo").GetProperty("songmid").GetString() == "FixtureMID" &&
                sources.Single(row => row.GetProperty("source").GetString() == "kg")
                    .GetProperty("musicInfo").GetProperty("hash").GetString() == "ABCDEF0123456789ABCDEF0123456789" &&
                result.GetProperty("failures").GetArrayLength() == 0 && scene.Handler.Searches == 2 &&
                scene.ScriptCalls == 0, "a second platform cannot reuse QQ's account frame or abort its match");
            var requests = scene.Runtime.Requests.Where(call => call.Operation == "search").ToArray();
            Check(requests.Length == 2);
            var tx = requests.Single(call => call.Platform == "tx").Payload.GetProperty("expectedSession");
            var published = scene.Runtime.Calls.Last(call => call.Method == "music.platform.session.update" &&
                call.Platform == "tx" && call.Payload.GetProperty("session").ValueKind == JsonValueKind.Object)
                .Payload.GetProperty("session");
            Check(tx.GetProperty("scope").GetString() == published.GetProperty("scope").GetString() &&
                tx.GetProperty("generation").GetInt64() == published.GetProperty("generation").GetInt64() &&
                requests.Single(call => call.Platform == "kg").Payload.GetProperty("expectedSession").ValueKind == JsonValueKind.Null);
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
            try
            {
                await scene.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await scene.Ok("music.accounts.logout", new { platform = "tx" });
            }
            finally { scene.Handler.Release.TrySetResult(); }
            try { Check(!(await pending).GetProperty("ok").GetBoolean(), "retired account must not publish candidates"); }
            catch (OperationCanceledException) { }
            Check(scene.ScriptCalls == 0);
        });
        yield return new("Official confirmed discovered recording survives owner restart without URL reuse or new search", async () =>
        {
            await using var store = new MemoryStore();
            await using var cacheBacking = new MemoryStore();
            string songId;
            await using (var scene = await Scene.CreateAsync(store, cacheBacking))
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
                Check(cacheBacking.Root(MusicSourceCache.Namespace)["scopes"]!.AsObject().First().Value!["pointers"]!.AsObject().Count == 0);
                await scene.Ok("music.official.confirm", input);
            }
            await using (var scene = await Scene.CreateAsync(store, cacheBacking))
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
            var before = await scene.Ok("music.queue.get");
            var originalEntry = before.GetProperty("current");
            string songId = originalEntry.GetProperty("song").GetProperty("songId").GetString()!;
            Check(Guid.TryParse(songId, out _) && originalEntry.GetProperty("key").GetString() == songId);
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
            scene.Handler.DenyWyMedia = true;
            module.SelectedRecording = module.RecordingCandidates.Single(row => row.Platform == "wy");
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single()); // Actual WY resource denial cannot silently select QQ.
            Check(output.Opens == 1 && scene.Handler.Searches == searches && scene.ScriptCalls == 0);
            var queue = await scene.Ok("music.queue.get");
            var current = queue.GetProperty("current");
            var projected = current.GetProperty("song");
            Check(queue.GetProperty("currentId").GetString() == before.GetProperty("currentId").GetString() &&
                queue.GetProperty("version").GetInt64() == before.GetProperty("version").GetInt64() &&
                queue.GetProperty("mode").GetInt32() == before.GetProperty("mode").GetInt32() &&
                queue.GetProperty("total").GetInt32() == before.GetProperty("total").GetInt32() &&
                queue.GetProperty("source").GetRawText() == before.GetProperty("source").GetRawText() &&
                current.GetProperty("id").GetString() == originalEntry.GetProperty("id").GetString() &&
                current.GetProperty("key").GetString() == songId &&
                current.GetProperty("context").GetRawText() == originalEntry.GetProperty("context").GetRawText() &&
                projected.GetProperty("songId").GetString() == songId,
                "supplementary metadata and a rejected exact manual choice cannot replace queue control facts");
            var recordings = projected.GetProperty("sources").EnumerateArray().ToArray();
            Check(recordings.Single(source => source.GetProperty("source").GetString() == "wy")
                    .GetProperty("musicInfo").GetProperty("songmid").GetString() == "1" &&
                recordings.Single(source => source.GetProperty("source").GetString() == "tx")
                    .GetProperty("musicInfo").GetProperty("songmid").GetString() == "FixtureMID",
                "the current repository projection retains the original locator and the discovered recording");
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
        yield return new("Official native resolve uses actual audio facts instead of a metadata duration gate", async () =>
        {
            await using var scene = await Scene.CreateAsync(); await scene.Login();
            var song = await MusicSongFixture.AdoptAsync(scene.App, SongObject("tx", "FixtureMID", duration: 180));
            var result = await scene.App.ExecuteAsync("music.official.resolve", new {
                platform = "tx", song, quality = "320k"
            });
            Check(result.GetProperty("ok").GetBoolean() && result.GetProperty("duration").GetDouble() == 100);
            await scene.CacheStore.DrainAsync();
            Check(scene.Handler.Vkeys == 1, "Usable canonical media was not requested");
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
            Check(output.Opens == 0 && scene.ScriptCalls == 0);
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
        internal readonly MemoryStore CacheBacking;
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly Transport Handler = new();
        internal readonly MusicNativeApplication App;
        internal readonly NativeTestDatabase CacheStore;
        internal int ScriptCalls => Runtime.Calls.Count(c => !c.Method.StartsWith("music.platform.", StringComparison.Ordinal) &&
            c.Method != "music.script.sources.list");
        private Scene(MemoryStore store, MemoryStore cacheBacking)
        {
            Store = store; CacheBacking = cacheBacking; CacheStore = new(CacheBacking);
            Runtime.OnRequest = Handler.Read;
            Runtime.OnOtherCall = (call, _) => call.Method == "music.script.sources.list"
                ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { ok = true, contract = "music.script.v3", value = new { items = Array.Empty<object>() } }))
                : throw new InvalidOperationException(call.Method);
            App = new(Store, MusicTestStores.For(Store), Runtime);
        }
        internal static Task<Scene> CreateAsync(MemoryStore? store = null, MemoryStore? cacheBacking = null)
        {
            store ??= new();
            return Task.FromResult(new Scene(store, cacheBacking ?? new()));
        }
        internal Task<JsonElement> Login() => Ok("music.accounts.import",
            new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" });
        internal async Task<JsonElement> Ok(string method, object? input = null)
        {
            var result = await App.ExecuteAsync(method, input);
            Check(result.GetProperty("ok").GetBoolean(), method + ": " +
                (result.TryGetProperty("code", out var code) ? code.GetString() : "failed"));
            await CacheStore.DrainAsync();
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
        public async ValueTask DisposeAsync()
        {
            try { await App.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await CacheStore.DisposeAsync(); }
        }
    }
    private sealed class Transport
    {
        internal int Calls, Searches, MediaRequests, Vkeys;
        internal bool FailSearch, HoldSearch;
        internal bool Member = true;
        internal bool DenyMedia;
        internal bool DenyWyMedia;
        internal int LastPage;
        internal string? HoldMethod;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource MethodEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource MethodRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            string method = call.Operation;
            if (method == "search")
            {
                Interlocked.Increment(ref Searches);
                LastPage = call.Parameters.GetProperty("page").GetInt32();
                if (HoldSearch)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(token);
                }
                if (FailSearch) throw new MusicPlatformException("UPSTREAM_ERROR");
                return MusicPlatformRuntimeFixture.Json(new { platform = call.Platform, kind = "songs",
                    items = new[] { SongObject(call.Platform, call.Platform == "tx" ? "FixtureMID" : "kg-id") },
                    more = false, continuation = (object?)null });
            }
            if (method == "audio") { Interlocked.Increment(ref MediaRequests); Interlocked.Increment(ref Vkeys); }
            if (method == HoldMethod && method is not null)
            {
                MethodEntered.TrySetResult();
                // Deliberately ignore cancellation to exercise native publication fencing.
                await MethodRelease.Task;
            }
            if (method is "account.verify" or "account.profile")
            {
                var observation = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(call.Platform, "123", vip: Member).GetRawText())!.AsObject();
                if (method == "account.verify")
                    observation["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                return MusicPlatformRuntimeFixture.Json(observation);
            }
            return method switch {
                "audio" when call.Platform == "wy" && DenyWyMedia => throw new MusicPlatformException("TRIAL_ONLY"),
                "audio" when DenyMedia => throw new MusicPlatformException("TRIAL_ONLY"),
                "audio" => MusicPlatformRuntimeFixture.Json(new { url = "https://isure.stream.qqmusic.qq.com/SYNTHETIC.mp3?vkey=synthetic",
                    requestQuality = "320k", actualQuality = "320k", qualityVerified = true, duration = 100, size = 120,
                    full = true, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() }),
                _ => throw new InvalidOperationException(method)
            };
        }
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
