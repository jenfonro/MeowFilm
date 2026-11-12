using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicOfficialAudioTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Official native module plays cache through real decoder without vkey and logout revokes file access", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var f = new MusicPlaybackCacheTests.Fixture();
            f.Clock.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var runtime = new NoRuntime(); int vkeys = 0, offlineRequests = 0; bool offline = false;
            var app = new MusicNativeApplication(f.Store, runtime, f.CacheStore, new Handler(async request =>
            {
                if (offline) { offlineRequests++; throw new HttpRequestException("Synthetic offline"); }
                if (request.Content is null) return new(HttpStatusCode.ServiceUnavailable);
                var input = JsonNode.Parse(await request.Content.ReadAsStringAsync())!;
                string? method = input["req_0"]?["method"]?.GetValue<string>();
                return method switch {
                    "GetLoginUserInfo" => Qq(new { code = 0 }),
                    "GetHomepageHeader" => Qq(new { Info = new { BaseInfo = new { EncryptedUin = "FixtureEUIN", Name = "Synthetic", Avatar = "" } } }),
                    "vip_login_base" => Qq(new { identity = new { vip = 1 }, userinfo = new { expire = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }),
                    "CgiGetTrackInfo" => Qq(new { tracks = new[] { new { mid = "FixtureMID", interval = 1, file = new { size_320mp3 = f.Bytes.Length } } } }),
                    "UrlGetVkey" => Vkey(),
                    _ => new(HttpStatusCode.ServiceUnavailable)
                };
                HttpResponseMessage Vkey()
                {
                    vkeys++; return Qq(new { expiration = 120, midurlinfo = new[] { new { songmid = "FixtureMID", result = 0,
                        filename = "M800FixtureMIDFixtureMID.mp3", purl = "M800FixtureMIDFixtureMID.mp3", ekey = "" } } });
                }
            }));
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            await using var module = new MusicCapability(app, f.Store, new NativeMusicPlayback(player), new CacheDialogs(),
                playbackCacheRoot: f.Root, cacheStore: f.CacheStore);
            await module.InitializeAsync(default);
            await module.ActivateQualitySettingsAsync();
            await module.SetPlaybackQualityRequestAsync("320k");
            Check((await app.ExecuteAsync("music.accounts.import",
                new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" })).GetProperty("ok").GetBoolean());
            var oneSecondSong = JsonSerializer.SerializeToNode(Song("tx", "FixtureMID"))!.AsObject();
            oneSecondSong["duration"] = 1; // Match the actual one-second WAV fixture and protocol duration.
            oneSecondSong["sources"]![0]!["musicInfo"]!["duration"] = 1;
            var song = JsonSerializer.SerializeToElement(oneSecondSong);
            var response = await app.ExecuteAsync("music.official.resolve", new { platform = "tx", song, quality = "320k" });
            Check(response.GetProperty("ok").GetBoolean());
            var receipt = JsonNode.Parse(response.GetRawText())!.AsObject();
            receipt["url"] = new Uri(f.Origin, "track.wav").AbsoluteUri;
            receipt["duration"] = 1; receipt["size"] = f.Bytes.Length;
            // Synthetic transport data, real native account owner, cache persistence, module and decoder.
            await f.Cache.StorePlayedAsync(response.GetProperty("scope").GetString()!,
                response.GetProperty("songId").GetString()!, response.GetProperty("qualityCacheSignature").GetString()!,
                JsonSerializer.SerializeToElement(receipt), () => true, default);
            Check((await app.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 })).GetProperty("ok").GetBoolean());
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var queueRow = module.QueueSurfaceRows.Single();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Started += (_, _) => started.TrySetResult();
            offline = true;
            await module.PlayQueueSurfaceItemAsync(queueRow);
            Check(player.HasMedia, "module open:" + module.Status);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await player.PauseAsync();
            Check(player.HasMedia && player.OwnerId == "music" && vkeys == 1 && runtime.Calls == 0 && offlineRequests == 0,
                "licensed file-first must play with zero network and zero Node calls");
            Check(module.CurrentSource.Contains("QQ", StringComparison.Ordinal), "source is official not LX");
            Check(module.CurrentQualityLabel == "无损", "the decoded cache reports its actual higher quality, not the 320k request");
            await app.ExecuteAsync("music.accounts.logout", new { platform = "tx" });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (player.HasMedia) await Task.Delay(10, deadline.Token);
            Check(runtime.Calls == 0);
        }));
        yield return new("Official native owner resolves and caches exact QQ recording then revokes on logout without Node", async () =>
        {
            await using var store = new MemoryStore(); var runtime = new NoRuntime();
            await using var cacheStore = new CacheDocumentStore(store);
            int vkeys = 0;
            await using var app = new MusicNativeApplication(store, runtime, cacheStore, new Handler(async request =>
            {
                var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                string method = input["req_0"]!["method"]!.GetValue<string>();
                return method switch {
                    "GetLoginUserInfo" => Qq(new { code = 0 }),
                    "GetHomepageHeader" => Qq(new { Info = new { BaseInfo = new { EncryptedUin = "FixtureEUIN", Name = "Synthetic", Avatar = "" } } }),
                    "vip_login_base" => Qq(new { identity = new { vip = 1 }, userinfo = new { expire = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }),
                    "CgiGetTrackInfo" => Qq(new { tracks = new[] { new { mid = "FixtureMID", interval = 100, file = new { size_320mp3 = 120 } } } }),
                    "UrlGetVkey" => Reply(),
                    _ => throw new InvalidOperationException("Unexpected method " + method)
                };
                HttpResponseMessage Reply()
                {
                    vkeys++;
                    return Qq(new { expiration = 120, midurlinfo = new[] { new { songmid = "FixtureMID", result = 0,
                        filename = "M800FixtureMIDFixtureMID.mp3", purl = "M800FixtureMIDFixtureMID.mp3", ekey = "" } } });
                }
            }));
            var login = await app.ExecuteAsync("music.accounts.import",
                new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" });
            Check(login.GetProperty("ok").GetBoolean(), "native import");
            var input = new { platform = "tx", song = Song("tx", "FixtureMID"), quality = "320k" };
            var resolved = await app.ExecuteAsync("music.official.resolve", input);
            Check(resolved.GetProperty("ok").GetBoolean(), "native resolve:" + resolved.GetRawText());
            Check(!resolved.TryGetProperty("installationId", out _) && resolved.GetProperty("kind").GetString() == "official");
            var repeated = await app.ExecuteAsync("music.official.resolve", input);
            Check(repeated.GetProperty("ok").GetBoolean() && vkeys == 1);
            string scope = resolved.GetProperty("scope").GetString()!;
            long generation = resolved.GetProperty("generation").GetInt64();
            Check(app.IsOfficialPlaybackCurrent("tx", scope, generation));
            await app.ExecuteAsync("music.official.confirm", input);
            await cacheStore.DrainAsync();
            Check(store.Root(MusicSourceCache.Namespace)["scopes"]![scope]!["pointers"]!.AsObject().Count == 1);
            var logout = await app.ExecuteAsync("music.accounts.logout", new { platform = "tx" });
            Check(logout.GetProperty("ok").GetBoolean());
            Check(!app.IsOfficialPlaybackCurrent("tx", scope, generation));
            var revoked = await app.ExecuteAsync("music.official.resolve", input);
            Check(!revoked.GetProperty("ok").GetBoolean() && runtime.Calls == 0 && vkeys == 1);
        });
        yield return new("Official native cache-only resolve is local and reuses its admitted URL without membership requests", async () =>
        {
            await using var store = new MemoryStore(); var runtime = new NoRuntime();
            int calls = 0, vkeys = 0, rights = 0;
            await using var app = new MusicNativeApplication(store, runtime, new Handler(async request =>
            {
                Interlocked.Increment(ref calls);
                var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                string method = input["req_0"]!["method"]!.GetValue<string>();
                if (method == "vip_login_base") Interlocked.Increment(ref rights);
                if (method == "UrlGetVkey") Interlocked.Increment(ref vkeys);
                return method switch {
                    "GetLoginUserInfo" => Qq(new { code = 0 }),
                    "GetHomepageHeader" => Qq(new { Info = new { BaseInfo = new { EncryptedUin = "FixtureEUIN", Name = "Synthetic", Avatar = "" } } }),
                    "vip_login_base" => Qq(new { identity = new { vip = 1 }, userinfo = new { expire = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }),
                    "CgiGetTrackInfo" => Qq(new { tracks = new[] { new { mid = "FixtureMID", interval = 100, file = new { size_320mp3 = 120 } } } }),
                    "UrlGetVkey" => Qq(new { expiration = 120, midurlinfo = new[] { new { songmid = "FixtureMID", result = 0,
                        filename = "M800FixtureMIDFixtureMID.mp3", purl = "M800FixtureMIDFixtureMID.mp3", ekey = "" } } }),
                    _ => throw new InvalidOperationException("Unexpected method " + method)
                };
            }));
            Check((await app.ExecuteAsync("music.accounts.import",
                new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" })).GetProperty("ok").GetBoolean());
            var song = Song("tx", "FixtureMID");
            int before = calls;
            var miss = await app.ExecuteAsync("music.official.resolve", new { platform = "tx", song, quality = "320k", cacheOnly = true });
            Check(!miss.GetProperty("ok").GetBoolean() && miss.GetProperty("code").GetString() == "CACHE_MISS" && calls == before,
                "a cache-only miss performs no account, catalog or media request: " + miss.GetRawText());
            var resolved = await app.ExecuteAsync("music.official.resolve", new { platform = "tx", song, quality = "320k" });
            Check(resolved.GetProperty("ok").GetBoolean() && vkeys == 1, resolved.GetRawText());
            int checks = rights;
            var hit = await app.ExecuteAsync("music.official.resolve", new { platform = "tx", song, quality = "320k", cacheOnly = true });
            Check(hit.GetProperty("ok").GetBoolean() && vkeys == 1 &&
                hit.GetProperty("url").GetString() == resolved.GetProperty("url").GetString(), "a valid receipt is reused: " + hit.GetRawText());
            Check(rights == checks, "an admitted URL needs no membership-derived rights check");
            var invalid = await app.ExecuteAsync("music.official.resolve",
                new { platform = "tx", song, quality = "320k", cacheOnly = true, refresh = true });
            Check(!invalid.GetProperty("ok").GetBoolean() && invalid.GetProperty("code").GetString() == "INVALID_REQUEST");
            Check(runtime.Calls == 0);
        });
        yield return new("Official audio QQ vkey exact identity has no script target or credential CDN headers", async () =>
        {
            int calls = 0;
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async request =>
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                string method = body["req_0"]!["method"]!.GetValue<string>();
                calls++;
                if (method == "CgiGetTrackInfo") return Qq(new { tracks = new[] { new {
                    mid = "FixtureMID", interval = 180, file = new { size_320mp3 = 1234567 }
                } } });
                Check(method == "UrlGetVkey");
                var p = body["req_0"]!["param"]!;
                Check(p["songmid"]![0]!.GetValue<string>() == "FixtureMID");
                Check(p["filename"]![0]!.GetValue<string>() == "M800FixtureMIDFixtureMID.mp3");
                return Qq(new { expiration = 600, midurlinfo = new[] { new { songmid = "FixtureMID", result = 0,
                    filename = "M800FixtureMIDFixtureMID.mp3", purl = "M800FixtureMIDFixtureMID.mp3?vkey=synthetic", ekey = "" } } });
            }));
            await using var clients = new MusicClientContextFixture(http);
            var result = await new OfficialMusicAudio(http, clients.Owner).ResolveAsync(Session("tx"),
                new JsonObject { ["songmid"] = "FixtureMID" }, ["320k"], default, () => { });
            Check(calls == 2 && result.RequestQuality == "320k" && !result.QualityVerified && result.ActualQuality == "" &&
                result.Full && result.Size == 1234567 && result.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeMilliseconds());
        });
        yield return new("Official audio QQ refuses encrypted or mismatched vkey", async () =>
        {
            foreach (string wrong in new[] { "ekey", "songmid", "filename", "host" })
            {
                int n = 0;
                using var http = MusicPlatformHttp.CreateWithHandler(new Handler(_ => Task.FromResult(++n == 1
                    ? Qq(new { tracks = new[] { new { mid = "FixtureMID", interval = 10, file = new { size_320mp3 = 120 } } } })
                    : Qq(new { expiration = 120, midurlinfo = new[] { new { songmid = wrong == "songmid" ? "Other" : "FixtureMID", result = 0,
                        filename = wrong == "filename" ? "M800OtherOther.mp3" : "M800FixtureMIDFixtureMID.mp3",
                        purl = wrong == "host" ? "https://example.org/M800FixtureMIDFixtureMID.mp3" : "M800FixtureMIDFixtureMID.mp3",
                        ekey = wrong == "ekey" ? "protected" : "" } } }))));
                bool denied = false;
                await using var clients = new MusicClientContextFixture(http);
                try { await new OfficialMusicAudio(http, clients.Owner).ResolveAsync(Session("tx"), new() { ["songmid"] = "FixtureMID" }, ["320k"], default, () => { }); }
                catch (MusicPlatformException) { denied = true; }
                Check(denied, wrong);
            }
        });
        yield return new("Official audio KG tracker carries exact hash key and rejects trial flag", async () =>
        {
            foreach (bool trial in new[] { false, true })
            {
                const string hash = "ABCDEF0123456789ABCDEF0123456789";
                int calls = 0;
                using var http = MusicPlatformHttp.CreateWithHandler(new Handler(request =>
                {
                    calls++;
                    if (request.RequestUri!.AbsolutePath == "/v1/audio/audio")
                        return Task.FromResult(Json(new { status = 1, data = new[] { new { hash, filesize = 5555,
                            duration = 100, name = "Fixture - Song", album_id = 12, album_audio_id = 34 } } }));
                    Check(request.RequestUri.AbsolutePath == "/v5/url");
                    string query = Uri.UnescapeDataString(request.RequestUri.Query);
                    string key = PlatformProtocols.Md5(hash.ToLowerInvariant() + "57ae12eb6890223e355ccfcb74edf70d1005" + "123456" + "123");
                    Check(query.Contains("key=" + key) && query.Contains("signature=") && query.Contains("IsFreePart=0"));
                    Check(request.Headers.GetValues("x-router").Single() == "trackercdn.kugou.com");
                    return Task.FromResult(Json(new { status = 1, hash, fileSize = 5555, is_free_part = trial ? 1 : 0,
                        url = new[] { "https://audio.kugou.com/fixture.mp3" } }));
                }));
                bool denied = false;
                await using var clients = new MusicClientContextFixture(http);
                try
                {
                    var result = await new OfficialMusicAudio(http, clients.Owner).ResolveAsync(Session("kg"),
                        new() { ["hash"] = hash }, ["128k"], default, () => { });
                    Check(result.Full && !result.QualityVerified && result.ExpiresAt is null);
                }
                catch (MusicPlatformException e) when (e.Code == "TRIAL_ONLY") { denied = true; }
                Check(denied == trial && calls == 2);
            }
        });
        yield return new("Official audio never rejects a non-member locally; the platform response decides", async () =>
        {
            int requests = 0;
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler(_ => { requests++; throw new InvalidOperationException("synthetic upstream"); }));
            await using var clients = new MusicClientContextFixture(http);
            foreach (string platform in new[] { "tx", "kg" })
            {
                int before = requests;
                try
                {
                    await new OfficialMusicAudio(http, clients.Owner).ResolveAsync(Session(platform),
                        platform == "kg" ? new() { ["hash"] = new string('a', 32) } : new() { ["songmid"] = "FixtureMID" }, ["320k"], default, () => { });
                }
                catch (MusicPlatformException e) { Check(e.Code != "VIP_REQUIRED", e.Code); }
                catch (Exception) { }
                Check(requests > before, "a non-member request reaches the platform: " + platform);
            }
        });
        yield return new("Official audio application without account fails AUTH_REQUIRED without Node", async () =>
        {
            await using var store = new MemoryStore(); var runtime = new NoRuntime();
            await using var app = new MusicNativeApplication(store, runtime);
            foreach (string platform in new[] { "wy", "tx", "kg" })
            {
                var result = await app.ExecuteAsync("music.official.resolve", new { platform, song = Song(platform), quality = "320k" });
                Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "AUTH_REQUIRED");
            }
            Check(runtime.Calls == 0);
        });
        yield return new("Music source and chorus caches roundtrip current facts and honor identity and TTL", async () =>
        {
            await using var backing = new MemoryStore();
            await using var store = new CacheDocumentStore(backing);
            var clock = new Clock();
            var cache = new MusicSourceCache(store, clock);
            string signature = Hash('a'), policy = Hash('b');
            var info = SourceInfo("scope", "song", signature, policy, clock.Now);
            await cache.PutAsync("scope", "song", signature, info, default);
            Check(await cache.ReadHistoryAsync("scope", "song", policy, "auto", 7, default) is null);
            Check(await cache.ReadAsync("scope", "song", signature, default) is not null);
            Check(await cache.ReadAsync("scope", "song", Hash('c'), default) is null);
            info["recording"] = Recording();
            await cache.ConfirmAsync("scope", "song", signature, info, default);
            var history = await cache.ReadHistoryAsync("scope", "song", policy, "auto", 7, default);
            Check(history is not null && history["url"] is null && history["headers"] is null);
            Check(await cache.ReadHistoryAsync("scope", "song", policy, "auto", 8, default) is null);
            string key = MusicChorusCache.Key("account", "song", "wy", "1", 100);
            var chorus = new MusicChorusCache(store, clock);
            await chorus.PutAsync(key, 100, 20, 30, default);
            Check(await chorus.ReadAsync(key, 100, default) == (20d, 30d));
            Check(await chorus.ReadAsync(key, 25, default) is null);
            Check(await chorus.ReadAsync(MusicChorusCache.Key("other", "song", "wy", "1", 100), 100, default) is null);
            Check(await chorus.ReadAsync(MusicChorusCache.Key("account", "song", "wy", "2", 100), 100, default) is null);
            await store.DrainAsync();
            await using var reopened = new CacheDocumentStore(backing);
            Check(await new MusicSourceCache(reopened, clock).ReadAsync("scope", "song", signature, default) is not null);
            clock.Now += 51000;
            Check(await cache.ReadAsync("scope", "song", signature, default) is null);
            Check(await cache.ReadHistoryAsync("scope", "song", policy, "auto", 7, default) is not null);
            clock.Now += MusicSourceCache.HistoryTtl;
            Check(await cache.ReadHistoryAsync("scope", "song", policy, "auto", 7, default) is null);
            Check(await chorus.ReadAsync(key, 100, default) is null);
        });
        yield return new("Music source and chorus cache damaged content or storage failures miss and rebuild", async () =>
        {
            foreach (var error in new DocumentStoreError?[] { null, DocumentStoreError.Protection, DocumentStoreError.Storage })
            {
                await using var backing = new MemoryStore { Failure = error };
                backing.Data[MusicSourceCache.Namespace] = "{"u8.ToArray();
                backing.Data[MusicChorusCache.Namespace] = "{\"entries\":null}"u8.ToArray();
                backing.Data["music.library.v1"] = "business"u8.ToArray();
                await using var store = new CacheDocumentStore(backing);
                var clock = new Clock();
                var source = new MusicSourceCache(store, clock);
                var chorus = new MusicChorusCache(store, clock);
                Check(await source.ReadAsync("scope", "song", Hash('a'), default) is null);
                Check(await chorus.ReadAsync(Hash('b'), 100, default) is null);
                await source.PutAsync("scope", "song", Hash('a'), SourceInfo("scope", "song", Hash('a'), Hash('b'), clock.Now), default);
                await chorus.PutAsync(Hash('b'), 100, 20, 30, default);
                Check(await source.ReadAsync("scope", "song", Hash('a'), default) is not null);
                Check(await chorus.ReadAsync(Hash('b'), 100, default) == (20d, 30d));
                await store.DrainAsync();
                Check(Encoding.UTF8.GetString(backing.Data["music.library.v1"]) == "business");
            }
        });
        yield return new("Music source and chorus saves never wait for initial disk read or slow persistence", async () =>
        {
            var held = new HeldCacheStore();
            await using var store = new CacheDocumentStore(held);
            var clock = new Clock();
            var source = new MusicSourceCache(store, clock);
            var chorus = new MusicChorusCache(store, clock);
            var reading = source.ReadAsync("scope", "song", Hash('a'), default);
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await source.PutAsync("scope", "song", Hash('a'), SourceInfo("scope", "song", Hash('a'), Hash('b'), clock.Now), default)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                await chorus.PutAsync(Hash('b'), 100, 20, 30, default).WaitAsync(TimeSpan.FromSeconds(2));
                Check(await chorus.ReadAsync(Hash('b'), 100, default) == (20d, 30d));
                Check(!reading.IsCompleted);
            }
            finally { held.Release.TrySetResult(); }
            Check(await reading is not null, "late disk read must not replace a newer memory save");
            await store.DrainAsync();
        });
        yield return new("Music source invalidation and recording rejection fence delayed initial disk reads", async () =>
        {
            foreach (bool reject in new[] { false, true })
            {
                var clock = new Clock();
                var info = SourceInfo("scope", "song", Hash('a'), Hash('b'), clock.Now);
                await using var seed = new CacheDocumentStore();
                var seeded = new MusicSourceCache(seed, clock);
                await seeded.PutAsync("scope", "song", Hash('a'), info, default);
                info["recording"] = Recording();
                await seeded.ConfirmAsync("scope", "song", Hash('a'), info, default);
                var held = new HeldCacheStore { Initial = seed.ReadMemory(MusicSourceCache.Namespace).Data };
                await using var store = new CacheDocumentStore(held);
                var cache = new MusicSourceCache(store, clock);
                var reading = cache.ReadAsync("scope", "song", Hash('a'), default);
                var history = cache.ReadHistoryAsync("scope", "song", Hash('b'), "auto", 7, default);
                await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    Task invalidating = reject
                        ? cache.RejectRecordingAsync("scope", "song", info["sourceKey"]!.GetValue<string>(), default)
                        : cache.InvalidateAsync("scope", "song", default);
                    await invalidating.WaitAsync(TimeSpan.FromSeconds(2));
                    Check(store.ReadMemory(MusicSourceCache.Namespace).Data is not null,
                        "an empty invalidation still publishes its fence without waiting for disk");
                }
                finally { held.Release.TrySetResult(); }
                Check(await reading is null && await history is null);
                Check(await cache.ReadAsync("scope", "song", Hash('a'), default) is null);
                await store.DrainAsync();
            }
        });
        yield return new("Music playback cache complete structure rejects truncated WAV and protected MP4", () =>
        {
            byte[] wav = Wav();
            AudioCacheStructure.Validate(new MemoryStream(wav), default);
            foreach (var invalid in new[] { wav[..^1], Encoding.ASCII.GetBytes("\0\0\0\u0018ftypM4A \0\0\0\0M4A \0\0\0\0\0\0\0\bpssh") })
            {
                bool denied = false;
                try { AudioCacheStructure.Validate(new MemoryStream(invalid), default); } catch (InvalidDataException) { denied = true; }
                Check(denied);
            }
            return Task.CompletedTask;
        });
        yield return new("Music queue preload reserves shuffle next without moving queue or writing history", async () =>
        {
            await using var store = new MemoryStore(); await using var library = new MusicLibraryOwner(store);
            await library.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("wy", "1"), Song("wy", "2") }, index = 0 });
            await library.ExecuteAsync("music.queue.mode", new { mode = 2 });
            int writes = store.Writes;
            var peek = await library.ExecuteAsync("music.queue.peekNext");
            Check(peek.GetProperty("ok").GetBoolean());
            string next = peek.GetProperty("next").GetProperty("id").GetString()!;
            Check(store.Writes == writes);
            var again = await library.ExecuteAsync("music.queue.peekNext");
            Check(again.GetProperty("next").GetProperty("id").GetString() == next);
            var actual = await library.ExecuteAsync("music.queue.next", new { reason = "ended" });
            Check(actual.GetProperty("targetId").GetString() == next);
        });
    }
    internal static string Hash(char value) => new(value, 64);
    internal static JsonObject Recording(string platform = "tx", string id = "FixtureMID")
    {
        var song = NativeMusicSong.Normalize(JsonSerializer.SerializeToNode(Song(platform, id)));
        return song["sources"]!.AsArray().Single()!.DeepClone().AsObject();
    }
    internal static JsonObject SourceInfo(string scope, string song, string signature, string policy, long now,
        string platform = "tx")
    {
        var recording = Recording(platform);
        return new JsonObject
        {
            ["ok"] = true,
            ["kind"] = "official",
            ["platform"] = platform,
            ["songId"] = song,
            ["sourceKey"] = recording["sourceKey"]!.DeepClone(),
            ["scope"] = scope,
            ["generation"] = 7,
            ["quality"] = "320k",
            ["requestedQuality"] = "auto",
            ["actualQuality"] = "",
            ["qualityVerified"] = false,
            ["playbackVerified"] = false,
            ["qualityPolicySignature"] = policy,
            ["qualityCacheSignature"] = signature,
            ["url"] = "https://audio.example.test/test?token=not-persisted-to-history",
            ["headers"] = new JsonObject(),
            ["expiresAt"] = now + 60000,
            ["duration"] = 100d,
            ["size"] = 120L,
            ["full"] = true,
            ["delivery"] = platform + "-official",
        };
    }
    private sealed class HeldCacheStore : IDocumentStore
    {
        internal byte[]? Initial;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            return new(Initial?.ToArray(), Initial is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Initial));
        }
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken token = default)
        {
            await Release.Task.WaitAsync(token);
            return DocumentFingerprint.FromBytes(data.Span);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static JsonObject Song(string platform, string id = "1", string? title = null, double duration = 100)
    {
        var native = new JsonObject();
        if (platform == "kg") native["hash"] = "ABCDEF0123456789ABCDEF0123456789";
        return MusicSongFixture.Online(platform, id, title ?? "Fixture " + id,
            "Artist", "", duration, native: native);
    }
    internal static void Check(bool value, string message = "Official audio assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static MusicPlatformSession Session(string platform) => new(platform, new string('a', 64), 1, "123",
        platform switch { "tx" => "musicid=123; musickey=synthetic", "kg" => "userid=123; token=synthetic; KUGOU_API_MID=123456", _ => "MUSIC_U=synthetic; deviceId=fixture" });
    private static HttpResponseMessage Qq(object value) => Json(new { code = 0, req_0 = new { code = 0, data = value } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request);
    }
    internal sealed class MemoryStore : IDocumentStore
    {
        internal readonly Dictionary<string, byte[]> Data = [];
        internal DocumentStoreError? Failure; internal int Writes;
        internal DocumentFingerprint? WriteReceipt;
        internal JsonObject Root(string ns) => JsonNode.Parse(Data[ns])!.AsObject();
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw new DocumentStoreException(failure);
            lock (Data) return Task.FromResult(Data.TryGetValue(ns, out var bytes)
                ? new DocumentSnapshot(bytes.ToArray(), DocumentFingerprint.FromBytes(bytes))
                : new(null, DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw new DocumentStoreException(failure);
            lock (Data)
            {
                var before = Data.TryGetValue(ns, out var old) ? DocumentFingerprint.FromBytes(old) : DocumentFingerprint.Missing;
                if (before != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
                Writes++;
                Data[ns] = bytes.ToArray();
                return Task.FromResult(WriteReceipt ?? DocumentFingerprint.FromBytes(bytes.Span));
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
            Calls++; throw new InvalidOperationException("Official business invoked a script");
        }
    }
    private sealed class CacheDialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    internal sealed class Clock : TimeProvider
    {
        internal long Now = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    internal static byte[] Wav()
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write("RIFF"u8); w.Write(16036); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1);
        w.Write((short)1); w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(16000);
        for (int i = 0; i < 8000; i++) w.Write((short)(Math.Sin(i * .2) * 1000));
        return output.ToArray();
    }
}
