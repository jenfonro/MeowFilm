using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Desktop;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicOfficialStandardTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("WY online-only file cache reauthorizes exact recording and fails closed on real permission loss", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var f = new MusicPlaybackCacheTests.Fixture();
            f.Clock.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var handler = new StandardHandler { DurationMs = 1000, MediaSize = f.Bytes.Length };
            var app = new MusicNativeApplication(f.Store, new NoNode(), handler);
            await using var player = new NativePlayback(audioOutput: NativeAudioOutput.Dummy);
            await using var module = new MusicCapability(app, f.Store, player, playbackCacheRoot: f.Root);
            await module.InitializeAsync(default);
            await module.ActivateQualitySettingsAsync();
            await module.SetPlaybackQualityRequestAsync("128k");
            var song = Song("wy", duration: 1);
            var result = await app.ExecuteAsync("music.official.resolve", new { platform = "wy", song, quality = "128k" });
            Check(result.GetProperty("ok").GetBoolean(), result.GetRawText());
            var receipt = JsonNode.Parse(result.GetRawText())!.AsObject();
            receipt["url"] = new Uri(f.Origin, "track.wav").AbsoluteUri;
            string scope = result.GetProperty("scope").GetString()!, id = result.GetProperty("songId").GetString()!,
                signature = result.GetProperty("qualityCacheSignature").GetString()!;
            await f.Cache.StorePlayedAsync(scope, id, signature, JsonSerializer.SerializeToElement(receipt), () => true, default);
            Check(await f.Cache.ReadInfoAsync(scope, id, signature, default) is not null);
            await app.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 });
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var queueRow = module.QueueSurfaceRows.Single();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Started += (_, _) => started.TrySetResult();
            int before = handler.Calls;
            await module.PlayQueueSurfaceItemAsync(queueRow);
            Check(player.HasMedia, module.Status);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await player.PauseAsync();
            Check(handler.Calls > before, "online-only bytes require a new exact platform authorization");
            handler.Problem = "trial";
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single());
            Check(!player.HasMedia && module.Status == "当前歌曲暂无完整播放权限",
                "existing bytes cannot bypass a new trial-only response: " + module.Status);
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
        yield return new("WY standard guest candidate and native resolve require neither login nor Node and grant no offline license", async () =>
        {
            await using var store = new MemoryStore();
            var transport = new StandardHandler();
            await using var app = new MusicNativeApplication(store, new NoNode(), transport);
            var policy = await app.ExecuteAsync("music.quality.settings.get");
            var input = new { platform = "wy", song = Song("wy"), quality = "128k" };
            var candidates = await app.ExecuteAsync("music.official.candidates", new {
                song = Song("wy"), quality = "128k", discover = false,
                qualityPolicySignature = policy.GetProperty("signature").GetString()
            });
            Check(candidates.GetProperty("ok").GetBoolean(), candidates.GetRawText());
            Check(candidates.GetProperty("candidates").GetArrayLength() == 1 && transport.Calls == 0);
            Check(!candidates.GetProperty("authorizationRequired").GetBoolean());
            var result = await app.ExecuteAsync("music.official.resolve", input);
            Check(result.GetProperty("ok").GetBoolean(), result.GetRawText());
            Check(result.GetProperty("actualQuality").GetString() == "128k");
            Check(!result.GetProperty("offlineAllowed").GetBoolean() &&
                result.GetProperty("licenseExpiresAt").ValueKind == JsonValueKind.Null);
            string scope = result.GetProperty("scope").GetString()!;
            long generation = result.GetProperty("generation").GetInt64();
            Check(app.IsOfficialPlaybackCurrent("wy", scope, generation));
            Check(!store.Data.ContainsKey(MusicAccountOwner.DocumentNamespace), "guest must not manufacture an account");
            Check((await app.ExecuteAsync("music.official.confirm", input)).GetProperty("ok").GetBoolean());
            Check((await app.ExecuteAsync("music.official.authorize", input)).GetProperty("ok").GetBoolean());
            Check(transport.Calls == 2, "completed file authorization rechecks platform");
            Check((await app.ExecuteAsync("music.accounts.logout", new { platform = "wy" })).GetProperty("ok").GetBoolean());
            Check(!app.IsOfficialPlaybackCurrent("wy", scope, generation));
        });
        yield return new("WY standard handles non-member and expired membership without elevating actual bitrate", async () =>
        {
            foreach (var membership in new[] { new MusicAccountMembership(false, "none", null),
                new MusicAccountMembership(true, "vip", 1) })
            {
                var handler = new StandardHandler { Bitrate = 96000, ClientCookie = "NMTID=SYNTHETIC_STANDARD_CLIENT; Domain=.music.163.com" };
                using var http = MusicPlatformHttp.CreateWithHandler(handler);
                await using var clients = new MusicClientContextFixture(http);
                var before = await clients.Owner.AcquireWyAsync();
                var response = await new OfficialMusicAudio(http, clients.Owner).ResolveAsync(
                    new("wy", "fixture", 1, "123", "MUSIC_U=synthetic"), new() { ["songmid"] = "1" },
                    ["128k"], new(membership, 0, 0, []), default, () => { });
                Check(handler.Calls == 1 && !response.QualityVerified && response.ActualQuality == "");
                Check(response.RequestQuality == "128k");
                var current = await clients.Owner.AcquireWyAsync();
                Check(current.Generation == before.Generation + 1 && current.Nmtid == "SYNTHETIC_STANDARD_CLIENT");
            }
        });
        foreach (string problem in new[] { "trial", "no-url", "identity", "protected", "expired" })
        {
            var issue = problem;
            yield return new("WY standard refuses " + issue, async () =>
            {
                using var http = MusicPlatformHttp.CreateWithHandler(new StandardHandler
                { Problem = issue, ClientCookie = "NMTID=SYNTHETIC_REJECTED; Domain=.music.163.com" });
                await using var clients = new MusicClientContextFixture(http);
                var before = await clients.Owner.AcquireWyAsync();
                bool rejected = false;
                try
                {
                    await new OfficialMusicAudio(http, clients.Owner).ResolveAsync(
                        new("wy", "fixture", 0, "0", "__remember_me=true"), new() { ["songmid"] = "1" },
                        ["128k"], new(new(false, "none", null), 0, 0, []), default, () => { });
                }
                catch (MusicPlatformException) { rejected = true; }
                Check(rejected && ReferenceEquals(before, await clients.Owner.AcquireWyAsync()));
            });
        }
        yield return new("WY guest lease cannot authenticate catalog or survive account fence", async () =>
        {
            await using var store = new MemoryStore();
            await using var clients = new MusicClientContextFixture();
            await using var owner = clients.CreateAccountOwner(store);
            var guest = await owner.AcquireOfficialAsync("wy", true, default);
            Check(guest is not null && guest.Generation > 0 && owner.IsOfficialCurrent(guest));
            Check(!owner.IsCurrent(guest!) && await owner.AcquireAsync("wy") is null);
            Check(await owner.AcquireOfficialAsync("tx", true, default) is null);
            await owner.CallAsync("music.accounts.logout", new { platform = "wy" });
            Check(!owner.IsOfficialCurrent(guest!));
        });
        yield return new("WY high quality remains login gated and anonymous candidates report authorization rather than identity failure", async () =>
        {
            await using var store = new MemoryStore();
            var handler = new StandardHandler();
            await using var app = new MusicNativeApplication(store, new NoNode(), handler);
            var policy = await app.ExecuteAsync("music.quality.settings.get");
            var reply = await app.ExecuteAsync("music.official.resolve", new { platform = "wy", song = Song("wy"), quality = "320k" });
            Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "AUTH_REQUIRED");
            var plan = await app.ExecuteAsync("music.official.candidates", new {
                song = Song("wy"), quality = "320k", discover = false,
                qualityPolicySignature = policy.GetProperty("signature").GetString()
            });
            Check(plan.GetProperty("ok").GetBoolean() && plan.GetProperty("authorizationRequired").GetBoolean());
            Check(handler.Calls == 0);
        });
        yield return new("WY logout fences delayed guest standard response", async () =>
        {
            await using var store = new MemoryStore();
            var handler = new StandardHandler { Hold = true };
            await using var app = new MusicNativeApplication(store, new NoNode(), handler);
            var pending = app.ExecuteAsync("music.official.resolve", new { platform = "wy", song = Song("wy"), quality = "128k" });
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await app.ExecuteAsync("music.accounts.logout", new { platform = "wy" }); }
            finally { handler.Release.TrySetResult(); }
            try { Check(!(await pending).GetProperty("ok").GetBoolean(), "retired response published"); }
            catch (OperationCanceledException) { }
        });
    }
    private sealed class StandardHandler : HttpMessageHandler
    {
        internal int Calls, Bitrate = 128000, DurationMs = 100000, MediaSize = 1600000;
        internal string Problem = "";
        internal string? ClientCookie;
        internal bool Hold;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.RequestUri!.AbsolutePath == "/eapi/song/enhance/player/url", "only the standard official protocol");
            string encoded = (await request.Content!.ReadAsStringAsync()).Split('=', 2)[1];
            using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
            string decoded = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(Uri.UnescapeDataString(encoded)), PaddingMode.PKCS7));
            var payload = JsonNode.Parse(decoded.Split("-36cd479b6b5-")[1])!;
            Check(payload["br"]!.GetValue<int>() == 128000 && payload["ids"]!.GetValue<string>() == "[1]");
            Entered.TrySetResult();
            if (Hold) await Release.Task;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                code = 200, data = new[] { new {
                    id = Problem == "identity" ? 2 : 1, code = 200,
                    url = Problem == "no-url" ? null : "https://m701.music.126.net/fixture.mp3",
                    type = Problem == "protected" ? "encrypted" : "mp3", br = Bitrate, time = DurationMs,
                    size = MediaSize, expi = Problem == "expired" ? 0 : 600,
                    freeTrialPrivilege = new { resConsumable = false, userConsumable = false },
                    freeTrialInfo = Problem == "trial" ? new { start = 0, end = 30 } : null
                } }
            }), Encoding.UTF8, "application/json") };
            if (ClientCookie is not null) response.Headers.TryAddWithoutValidation("Set-Cookie", ClientCookie);
            return response;
        }
    }
    private sealed class NoNode : IRuntimeClient
    {
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Official standard request must not start Node");
    }
}
