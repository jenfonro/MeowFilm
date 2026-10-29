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
            var app = new MusicNativeApplication(f.Store, runtime, new Handler(async request =>
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
            await using var module = new MusicCapability(app, f.Store, new NativeMusicPlayback(player), new CacheDialogs(), playbackCacheRoot: f.Root);
            await module.InitializeAsync(default);
            Check((await app.ExecuteAsync("music.accounts.import",
                new { platform = "tx", cookie = "musicid=123; musickey=synthetic; encrypt_uin=FixtureEUIN" })).GetProperty("ok").GetBoolean());
            var oneSecondSong = JsonSerializer.SerializeToNode(Song("tx", "FixtureMID"))!.AsObject();
            oneSecondSong["duration"] = 1; // Match the actual one-second WAV fixture and protocol duration.
            oneSecondSong["sources"]![0]!["duration"] = 1;
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
            await app.ExecuteAsync("music.accounts.logout", new { platform = "tx" });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (player.HasMedia) await Task.Delay(10, deadline.Token);
            Check(runtime.Calls == 0);
        }));
        yield return new("Official native owner resolves and caches exact QQ recording then revokes on logout without Node", async () =>
        {
            await using var store = new MemoryStore(); var runtime = new NoRuntime();
            int vkeys = 0;
            await using var app = new MusicNativeApplication(store, runtime, new Handler(async request =>
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
            Check(store.Root(MusicSourceCache.Namespace)["scopes"]![scope]!["pointers"]!.AsObject().Count == 1);
            var logout = await app.ExecuteAsync("music.accounts.logout", new { platform = "tx" });
            Check(logout.GetProperty("ok").GetBoolean());
            Check(!app.IsOfficialPlaybackCurrent("tx", scope, generation));
            var revoked = await app.ExecuteAsync("music.official.resolve", input);
            Check(!revoked.GetProperty("ok").GetBoolean() && runtime.Calls == 0 && vkeys == 1);
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
            var result = await new OfficialMusicAudio(http).ResolveAsync(Session("tx"),
                new JsonObject { ["songmid"] = "FixtureMID" }, ["320k"], Rights(), default);
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
                try { await new OfficialMusicAudio(http).ResolveAsync(Session("tx"), new() { ["songmid"] = "FixtureMID" }, ["320k"], Rights(), default); }
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
                try
                {
                    var result = await new OfficialMusicAudio(http).ResolveAsync(Session("kg"),
                        new() { ["hash"] = hash }, ["128k"], Rights(), default);
                    Check(result.Full && !result.QualityVerified && result.ExpiresAt is null);
                }
                catch (MusicPlatformException e) when (e.Code == "TRIAL_ONLY") { denied = true; }
                Check(denied == trial && calls == 2);
            }
        });
        yield return new("Official audio requires real membership before any resource request", async () =>
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler(_ => throw new InvalidOperationException("Unexpected network")));
            foreach (string platform in new[] { "wy", "tx", "kg" })
            {
                bool denied = false;
                try { await new OfficialMusicAudio(http).ResolveAsync(Session(platform), new(), ["320k"],
                    Rights() with { Membership = new(false, "none", null) }, default); }
                catch (MusicPlatformException e) when (e.Code == "VIP_REQUIRED") { denied = true; }
                Check(denied);
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
        yield return new("Music source cache separates candidate and confirmed history and preserves restart TTL", async () =>
        {
            await using var store = new MemoryStore(); var clock = new Clock();
            var cache = new MusicSourceCache(store, clock);
            string signature = Hash('a'), policy = Hash('b');
            var info = SourceInfo("scope", "song", signature, policy, clock.Now);
            await cache.PutAsync("scope", "song", signature, info, default);
            Check(store.Root(MusicSourceCache.Namespace)["scopes"]!["scope"]!["pointers"]!.AsObject().Count == 0);
            var hit = await new MusicSourceCache(store, clock).ReadAsync("scope", "song", signature, default);
            Check(hit is not null && await cache.ReadAsync("scope", "song", Hash('c'), default) is null);
            info["recording"] = Recording();
            await cache.ConfirmAsync("scope", "song", signature, info, default);
            var pointer = store.Root(MusicSourceCache.Namespace)["scopes"]!["scope"]!["pointers"]!["song"]!;
            Check(pointer["info"]!["url"] is null);
            clock.Now += 51000;
            Check(await cache.ReadAsync("scope", "song", signature, default) is null);
            Check(store.Root(MusicSourceCache.Namespace)["scopes"]!["scope"]!["pointers"]!.AsObject().Count == 1);
        });
        yield return new("Music source cache failures are misses and do not overwrite library documents", async () =>
        {
            await using var store = new MemoryStore { Fail = true };
            var cache = new MusicSourceCache(store);
            string signature = Hash('a');
            Check(await cache.ReadAsync("scope", "song", signature, default) is null);
            await cache.PutAsync("scope", "song", signature,
                SourceInfo("scope", "song", signature, Hash('b'), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), default);
            Check(store.Data.Count == 0);
        });
        yield return new("Music protected cache data failure is explicit across source and chorus owners", async () =>
        {
            await using var store = new MemoryStore { Failure = DocumentStoreError.Protection };
            Check(await CacheDataError(() => new MusicSourceCache(store).ReadAsync(
                "scope", "song", Hash('a'), default)) == DocumentStoreError.Protection);
            Check(await CacheDataError(async () => _ = await new MusicChorusCache(store).ReadAsync(
                Hash('b'), 100, default)) == DocumentStoreError.Protection);
            Check(store.Writes == 0);
        });
        yield return new("Music source cache rejects every non-current document entry without side effects", async () =>
        {
            long now = new Clock().Now;
            string signature = Hash('a'), policy = Hash('b');
            var candidate = SourceInfo("scope", "song", signature, policy, now);
            var history = (JsonObject)candidate.DeepClone();
            history["recording"] = Recording();
            var current = SourceDocument(candidate, history, now);
            var malformedInfo = JsonNode.Parse(current)!.AsObject();
            malformedInfo["scopes"]!["scope"]!["urls"]!["song"]!["info"]!["extra"] = true;
            var missingInfo = JsonNode.Parse(current)!.AsObject();
            missingInfo["scopes"]!["scope"]!["urls"]!["song"]!["info"]!.AsObject().Remove("delivery");
            var nonCanonicalGeneration = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(current)
                .Replace("\"generation\":7", "\"generation\":7.0", StringComparison.Ordinal));
            foreach (var item in new (byte[] Bytes, DocumentStoreError Error, DocumentFingerprint? Fingerprint)[]
            {
                (Encoding.UTF8.GetBytes("""{"schema":2,"scopes":{}}"""), DocumentStoreError.UnsupportedVersion, null),
                (Encoding.UTF8.GetBytes("""{"schema":1.0,"scopes":{}}"""), DocumentStoreError.UnsupportedVersion, null),
                (Encoding.UTF8.GetBytes("""{"schema":1,"schema":1,"scopes":{}}"""), DocumentStoreError.Corrupt, null),
                (Encoding.UTF8.GetBytes("""{"schema":1,"scopes":{"scope":{"at":1.0,"urls":{},"pointers":{}}}}"""), DocumentStoreError.Corrupt, null),
                (JsonSerializer.SerializeToUtf8Bytes(malformedInfo), DocumentStoreError.Corrupt, null),
                (JsonSerializer.SerializeToUtf8Bytes(missingInfo), DocumentStoreError.Corrupt, null),
                (nonCanonicalGeneration, DocumentStoreError.Corrupt, null),
                (current, DocumentStoreError.Corrupt,
                    new DocumentFingerprint(true, current.Length, new string('0', 64)))
            })
                await RejectSourceDocument(item.Bytes, item.Error, item.Fingerprint, candidate, history);
        });
        yield return new("Music source cache rejects non-current producer shapes instead of sanitizing them", async () =>
        {
            await using var store = new MemoryStore();
            string signature = Hash('a');
            var candidate = SourceInfo("scope", "song", signature, Hash('b'), new Clock().Now);
            candidate["extra"] = true;
            Check(await CacheDataError(() => new MusicSourceCache(store).PutAsync(
                "scope", "song", signature, candidate, default)) == DocumentStoreError.Corrupt);
            var confirmation = SourceInfo("scope", "song", signature, Hash('b'), new Clock().Now);
            confirmation["recording"] = Recording();
            confirmation["recording"]!["cookie"] = "must-not-be-silently-removed";
            Check(await CacheDataError(() => new MusicSourceCache(store).ConfirmAsync(
                "scope", "song", signature, confirmation, default)) == DocumentStoreError.Corrupt);
            Check(store.Writes == 0 && store.Data.Count == 0);
        });
        yield return new("Music chorus cache preserves exact recording duration account and thirty day boundary", async () =>
        {
            await using var store = new MemoryStore(); var clock = new Clock();
            string key = MusicChorusCache.Key("account", "song", "wy", "1", 100);
            var cache = new MusicChorusCache(store, clock);
            await cache.PutAsync(key, 100, 20, 30, default);
            Check(await new MusicChorusCache(store, clock).ReadAsync(key, 100, default) == (20d, 30d));
            Check(await cache.ReadAsync(MusicChorusCache.Key("other", "song", "wy", "1", 100), 100, default) is null);
            Check(await cache.ReadAsync(MusicChorusCache.Key("account", "song", "wy", "2", 100), 100, default) is null);
            Check(await cache.ReadAsync(key, 25, default) is null);
            clock.Now += 30L * 86400000;
            Check(await cache.ReadAsync(key, 100, default) is null);
        });
        yield return new("Music chorus cache rejects every non-current document entry without side effects", async () =>
        {
            string key = Hash('a');
            byte[] current = ChorusDocument(key, """{"at":1,"start":20,"end":30}""");
            foreach (var item in new (byte[] Bytes, DocumentStoreError Error, DocumentFingerprint? Fingerprint)[]
            {
                (Encoding.UTF8.GetBytes("""{"schema":2,"entries":{}}"""), DocumentStoreError.UnsupportedVersion, null),
                (Encoding.UTF8.GetBytes("""{"schema":1.0,"entries":{}}"""), DocumentStoreError.UnsupportedVersion, null),
                (Encoding.UTF8.GetBytes("""{"schema":1,"schema":1,"entries":{}}"""), DocumentStoreError.Corrupt, null),
                (ChorusDocument(key, """{"at":1.0,"start":20,"end":30}"""), DocumentStoreError.Corrupt, null),
                (ChorusDocument(key, """{"at":1,"start":20.0,"end":30}"""), DocumentStoreError.Corrupt, null),
                (ChorusDocument(key, """{"at":1,"start":20}"""), DocumentStoreError.Corrupt, null),
                (ChorusDocument(key, """{"at":1,"start":20,"end":30,"extra":true}"""), DocumentStoreError.Corrupt, null),
                (current, DocumentStoreError.Corrupt,
                    new DocumentFingerprint(true, current.Length, new string('0', 64)))
            })
                await RejectChorusDocument(item.Bytes, item.Error, item.Fingerprint, key);
        });
        yield return new("Music protected cache owners reject unknown write receipts without retrying or inventing success", async () =>
        {
            string signature = Hash('a');
            await using (var store = new MemoryStore
            {
                SkipWrite = true,
                WriteReceipt = new DocumentFingerprint(true, 1, new string('0', 64))
            })
            {
                var clock = new Clock();
                var error = await CacheDataError(() => new MusicSourceCache(store, clock).PutAsync(
                    "scope", "song", signature,
                    SourceInfo("scope", "song", signature, Hash('b'), clock.Now), default));
                Check(error == DocumentStoreError.Storage && store.Writes == 1 &&
                    !store.Data.ContainsKey(MusicSourceCache.Namespace));
            }
            await using (var store = new MemoryStore
            {
                SkipWrite = true,
                WriteReceipt = new DocumentFingerprint(true, 1, new string('0', 64))
            })
            {
                var error = await CacheDataError(() =>
                    new MusicChorusCache(store).PutAsync(Hash('c'), 100, 20, 30, default));
                Check(error == DocumentStoreError.Storage && store.Writes == 1 &&
                    !store.Data.ContainsKey(MusicChorusCache.Namespace));
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
        long license = now + 3600000;
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
            ["licenseExpiresAt"] = license,
            ["delivery"] = platform + "-official",
            ["offlineAllowed"] = true,
            ["authorizationExpiresAt"] = license
        };
    }
    private static byte[] SourceDocument(JsonObject candidate, JsonObject confirmation, long at)
    {
        var history = new JsonObject
        {
            ["platform"] = confirmation["platform"]!.DeepClone(),
            ["songId"] = "song",
            ["sourceKey"] = confirmation["sourceKey"]!.DeepClone(),
            ["scope"] = "scope",
            ["generation"] = confirmation["generation"]!.DeepClone(),
            ["quality"] = confirmation["quality"]!.DeepClone(),
            ["actualQuality"] = confirmation["actualQuality"]?.DeepClone(),
            ["qualityVerified"] = confirmation["qualityVerified"]!.DeepClone(),
            ["duration"] = confirmation["duration"]?.DeepClone(),
            ["delivery"] = confirmation["delivery"]?.DeepClone(),
            ["requestedQuality"] = confirmation["requestedQuality"]!.DeepClone(),
            ["qualityPolicySignature"] = confirmation["qualityPolicySignature"]!.DeepClone(),
            ["recording"] = confirmation["recording"]!.DeepClone()
        };
        var root = new JsonObject
        {
            ["schema"] = 1,
            ["scopes"] = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["at"] = at,
                    ["urls"] = new JsonObject
                    {
                        ["song"] = new JsonObject
                        {
                            ["signature"] = candidate["qualityCacheSignature"]!.DeepClone(),
                            ["at"] = at,
                            ["until"] = at + 50000,
                            ["info"] = candidate.DeepClone()
                        }
                    },
                    ["pointers"] = new JsonObject
                    {
                        ["song"] = new JsonObject
                        {
                            ["signature"] = candidate["qualityCacheSignature"]!.DeepClone(),
                            ["at"] = at,
                            ["info"] = history
                        }
                    }
                }
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }
    private static async Task RejectSourceDocument(byte[] bytes, DocumentStoreError expected,
        DocumentFingerprint? fingerprint, JsonObject candidate, JsonObject confirmation)
    {
        await using var store = new MemoryStore { ReadFingerprint = fingerprint };
        store.Data[MusicSourceCache.Namespace] = bytes.ToArray();
        byte[] original = store.Data[MusicSourceCache.Namespace].ToArray();
        var cache = new MusicSourceCache(store, new Clock());
        string signature = candidate["qualityCacheSignature"]!.GetValue<string>();
        string policy = confirmation["qualityPolicySignature"]!.GetValue<string>();
        foreach (Func<Task> operation in new Func<Task>[]
        {
            async () => _ = await cache.ReadAsync("scope", "song", signature, default),
            async () => _ = await cache.ReadHistoryAsync("scope", "song", policy, "auto", 7, default),
            () => cache.PutAsync("scope", "song", signature, (JsonObject)candidate.DeepClone(), default),
            () => cache.ConfirmAsync("scope", "song", signature, (JsonObject)confirmation.DeepClone(), default),
            () => cache.InvalidateAsync("scope", "song", default)
        })
            Check(await CacheDataError(operation) == expected);
        Check(store.Writes == 0 && store.Data[MusicSourceCache.Namespace].SequenceEqual(original));
    }
    private static async Task RejectChorusDocument(byte[] bytes, DocumentStoreError expected,
        DocumentFingerprint? fingerprint, string key)
    {
        await using var store = new MemoryStore { ReadFingerprint = fingerprint };
        store.Data[MusicChorusCache.Namespace] = bytes.ToArray();
        byte[] original = store.Data[MusicChorusCache.Namespace].ToArray();
        var cache = new MusicChorusCache(store, new Clock());
        Check(await CacheDataError(async () => _ = await cache.ReadAsync(key, 100, default)) == expected);
        Check(await CacheDataError(() => cache.PutAsync(key, 100, 20, 30, default)) == expected);
        Check(store.Writes == 0 && store.Data[MusicChorusCache.Namespace].SequenceEqual(original));
    }
    private static byte[] ChorusDocument(string key, string value) => Encoding.UTF8.GetBytes(
        """{"schema":1,"entries":{"__KEY__":__VALUE__}}"""
            .Replace("__KEY__", key, StringComparison.Ordinal)
            .Replace("__VALUE__", value, StringComparison.Ordinal));
    internal static JsonObject Song(string platform, string id = "1", string? title = null, double duration = 100)
    {
        var native = new JsonObject();
        if (platform == "kg") native["hash"] = "ABCDEF0123456789ABCDEF0123456789";
        return MusicSongFixture.Online(platform, id, title ?? "Fixture " + id,
            "Artist", "", duration, native: native);
    }
    internal static void Check(bool value, string message = "Official audio assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task<DocumentStoreError> CacheDataError(Func<Task> operation)
    {
        try { await operation(); }
        catch (MusicPlaybackCacheDataException error) { return error.Error; }
        catch (MusicPlaybackCacheCommitException) { return DocumentStoreError.Storage; }
        throw new InvalidOperationException("Cache document was accepted");
    }
    private static MusicPlatformSession Session(string platform) => new(platform, new string('a', 64), 1, "123",
        platform switch { "tx" => "musicid=123; musickey=synthetic", "kg" => "userid=123; token=synthetic; KUGOU_API_MID=123456", _ => "MUSIC_U=synthetic; deviceId=fixture" });
    private static MusicAccountEntitlements Rights() => new(new(true, "vip", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()),
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), ["standard", "exhigh"]);
    private static HttpResponseMessage Qq(object value) => Json(new { code = 0, req_0 = new { code = 0, data = value } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request);
    }
    internal sealed class MemoryStore : IProtectedDocumentStore
    {
        internal readonly Dictionary<string, byte[]> Data = [];
        internal bool Fail; internal DocumentStoreError? Failure; internal int Writes;
        internal DocumentFingerprint? ReadFingerprint, WriteReceipt;
        internal bool SkipWrite;
        internal JsonObject Root(string ns) => JsonNode.Parse(Data[ns])!.AsObject();
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw new ProtectedDocumentException(failure);
            if (Fail) throw new ProtectedDocumentException(DocumentStoreError.Storage);
            lock (Data) return Task.FromResult(Data.TryGetValue(ns, out var bytes)
                ? new ProtectedDocument(bytes.ToArray(), ReadFingerprint ?? DocumentFingerprint.FromBytes(bytes))
                : new(null, ReadFingerprint ?? DocumentFingerprint.Missing));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> bytes, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw new ProtectedDocumentException(failure);
            if (Fail) throw new ProtectedDocumentException(DocumentStoreError.Storage);
            lock (Data)
            {
                var before = Data.TryGetValue(ns, out var old) ? DocumentFingerprint.FromBytes(old) : DocumentFingerprint.Missing;
                if (before != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
                Writes++;
                if (!SkipWrite) Data[ns] = bytes.ToArray();
                return Task.FromResult(WriteReceipt ?? DocumentFingerprint.FromBytes(bytes.Span));
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Official business called Node"); }
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
