using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Desktop;
using MeowFilm.Media;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicOfficialStandardTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("WY healthy cached file plays without another online permission request", () =>
            MusicQualityConsumerTests.Sta(async () =>
        {
            await using var f = new MusicPlaybackCacheTests.Fixture();
            f.Clock.Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await using var runtime = AudioPeer(1, f.Bytes.Length, "128k");
            var app = new MusicNativeApplication(f.Store, MusicTestStores.For(f.Store), runtime);
            await using var player = new NativeAudioPlayback(audioOutput: NativeAudioOutput.Dummy);
            await using var module = new MusicCapability(app, f.Store, player, playbackCacheRoot: f.Root);
            await module.InitializeAsync(default);
            await module.ActivateQualitySettingsAsync();
            await module.SetPlaybackQualityRequestAsync("128k");
            var song = await MusicSongFixture.AdoptAsync(app, Song("wy", duration: 1));
            var result = await app.ExecuteAsync("music.official.resolve", new { platform = "wy", song, quality = "128k" });
            Check(result.GetProperty("ok").GetBoolean(), result.GetRawText());
            var receipt = JsonNode.Parse(result.GetRawText())!.AsObject();
            receipt["url"] = new Uri(f.Origin, "track.wav").AbsoluteUri;
            string scope = result.GetProperty("scope").GetString()!, id = result.GetProperty("songId").GetString()!,
                signature = result.GetProperty("qualityCacheSignature").GetString()!;
            await f.Cache.StorePlayedAsync(scope, id, signature, JsonSerializer.SerializeToElement(receipt), () => true, default);
            Check(await f.Cache.ReadInfoAsync(scope, id, signature, default) is not null);
            // Isolate complete audio reuse from the independent chorus metadata request.
            await new MusicChorusCache(f.CacheStore).PutAsync(
                MusicChorusCache.Key(scope, id, "wy", "1", 1), 1, 0, 0.5, default);
            await app.ExecuteAsync("music.queue.replace", new { songs = new[] { song }, index = 0 });
            await module.ActivateGlobalSurfaceAsync(DesktopGlobalSurface.PlayQueue, default);
            var queueRow = module.QueueSurfaceRows.Single();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Started += (_, _) => started.TrySetResult();
            int before = runtime.Requests.Count;
            await module.PlayQueueSurfaceItemAsync(queueRow);
            Check(player.HasMedia, module.Status);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await player.PauseAsync();
            Check(runtime.Requests.Count == before, "healthy bytes must not request another URL");
            runtime.OnRequest = (_, _) => throw new MusicPlatformException("TRIAL_ONLY");
            await module.PlayQueueSurfaceItemAsync(module.QueueSurfaceRows.Single());
            Check(player.HasMedia && runtime.Requests.Count == before, "a changed online response cannot invalidate complete cached bytes: " + module.Status);
            module.DeactivateGlobalSurface(DesktopGlobalSurface.PlayQueue);
        }));
        yield return new("WY standard guest candidate and native resolve require neither login nor script and grant no offline license", async () =>
        {
            await using var store = new MemoryStore();
            await using var runtime = AudioPeer(quality: "128k");
            await using var app = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            var policy = await app.ExecuteAsync("music.quality.settings.get");
            var song = await MusicSongFixture.AdoptAsync(app, Song("wy"));
            var input = new { platform = "wy", song, quality = "128k" };
            var candidates = await app.ExecuteAsync("music.official.candidates", new {
                song, quality = "128k",
                qualityPolicySignature = policy.GetProperty("signature").GetString()
            });
            Check(candidates.GetProperty("ok").GetBoolean(), candidates.GetRawText());
            Check(candidates.GetProperty("candidates").GetArrayLength() == 1 && runtime.Requests.Count == 0);
            Check(!candidates.GetProperty("authorizationRequired").GetBoolean());
            var result = await app.ExecuteAsync("music.official.resolve", input);
            Check(result.GetProperty("ok").GetBoolean(), result.GetRawText());
            Check(result.GetProperty("actualQuality").GetString() == "128k");
            string scope = result.GetProperty("scope").GetString()!;
            long generation = result.GetProperty("generation").GetInt64();
            Check(app.IsOfficialPlaybackCurrent("wy", scope, generation));
            Check((await store.ReadAccountsAsync()).Revision == StorageRevision.Empty, "guest must not manufacture an account");
            Check((await app.ExecuteAsync("music.official.confirm", input)).GetProperty("ok").GetBoolean());
            Check(runtime.Requests.Count == 1, "confirmation does not fetch a second URL");
            Check((await app.ExecuteAsync("music.accounts.logout", new { platform = "wy" })).GetProperty("ok").GetBoolean());
            Check(!app.IsOfficialPlaybackCurrent("wy", scope, generation));
        });
        yield return new("WY guest lease cannot authenticate catalog or survive account fence", async () =>
        {
            await using var store = new MemoryStore();
            await using var runtime = AudioPeer();
            await using var owner = runtime.CreateAccountOwner(store);
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
            await using var runtime = AudioPeer(quality: "128k");
            await using var app = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            var song = await MusicSongFixture.AdoptAsync(app, Song("wy"));
            var policy = await app.ExecuteAsync("music.quality.settings.get");
            var reply = await app.ExecuteAsync("music.official.resolve", new { platform = "wy", song, quality = "320k" });
            Check(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("code").GetString() == "AUTH_REQUIRED");
            var plan = await app.ExecuteAsync("music.official.candidates", new {
                song, quality = "320k",
                qualityPolicySignature = policy.GetProperty("signature").GetString()
            });
            Check(plan.GetProperty("ok").GetBoolean() && plan.GetProperty("authorizationRequired").GetBoolean());
            Check(runtime.Requests.Count == 0);
        });
        yield return new("WY logout fences delayed guest standard response", async () =>
        {
            await using var store = new MemoryStore();
            await using var runtime = AudioPeer(quality: "128k");
            var entered = MusicPlatformRuntimeFixtureSignal(); var release = MusicPlatformRuntimeFixtureSignal();
            var respond = runtime.OnRequest;
            runtime.OnRequest = async (call, token) =>
            {
                if (call.Operation != "audio") return await respond(call, token);
                entered.TrySetResult();
                await release.Task;
                return await respond(call, token);
            };
            await using var app = new MusicNativeApplication(store, MusicTestStores.For(store), runtime);
            var song = await MusicSongFixture.AdoptAsync(app, Song("wy"));
            var pending = app.ExecuteAsync("music.official.resolve", new { platform = "wy", song, quality = "128k" });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check((await app.ExecuteAsync("music.accounts.logout", new { platform = "wy" })).GetProperty("ok").GetBoolean());
            }
            finally { release.TrySetResult(); }
            try { Check(!(await pending).GetProperty("ok").GetBoolean(), "retired response published"); }
            catch (OperationCanceledException) { }
        });
    }
    private static TaskCompletionSource MusicPlatformRuntimeFixtureSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
