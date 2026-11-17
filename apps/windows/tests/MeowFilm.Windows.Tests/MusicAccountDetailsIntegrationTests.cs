using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountDetailsIntegrationTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.NativeAccountDetailsCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> NativeAccountDetailsCases()
    {
        yield return Case("native login saves profile and rights before any settings activation", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.NavigateAsync("favorites", default);
            await f.Module.ImportAccountAsync("MUSIC_U=SYNTHETIC_LOGIN_CONSUMER; __csrf=SYNTHETIC_CSRF");
            Equal("verified", f.Module.SelectedAccount!.State);
            Equal(1, f.Peer.Requests);
            var saved = await f.App.ReadDetailsAsync("wy");
            True(saved.Generation == 2 && saved.Membership.Vip == true &&
                saved.MembershipFreshness == MusicAccountDetailsFreshness.Fresh, "login already committed its rights");
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            Equal("Fresh native name", row.DisplayName);
            True(row.Membership.StartsWith("VIP有效 · ", StringComparison.Ordinal), "first settings reads the login cache");
            await f.Module.DeactivateSettingsAsync(); await f.Module.ActivateSettingsAsync();
            Equal(1, f.Peer.Requests); True(f.Runtime.Calls.All(call => call.Method.StartsWith("music.platform.", StringComparison.Ordinal)), "no script IO");
        });
        yield return Case("visible settings reads the completed native login without a second entitlement request", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            await f.Module.ImportAccountAsync("MUSIC_U=SYNTHETIC_VISIBLE_LOGIN; __csrf=SYNTHETIC_CSRF");
            await SearchUntil(() => row.CanRefresh && row.Membership.StartsWith("VIP有效 · ", StringComparison.Ordinal));
            Equal("Fresh native name", row.DisplayName);
            Equal(2L, f.Module.SelectedAccount!.Generation);
            Equal(1, f.Peer.Requests); True(f.Runtime.Calls.All(call => call.Method.StartsWith("music.platform.", StringComparison.Ordinal)), "no script IO");
        });
        yield return Case("actual native account details refresh updates the selected account and persists a cold local view", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            Equal("saved", f.Module.SelectedAccount!.State); Equal(0, f.Peer.Requests);
            await row.RefreshDetailsAsync();
            Equal("Fresh native name", row.DisplayName);
            Equal("saved", f.Module.SelectedAccount.State);
            Equal("Fresh native name", f.Module.SelectedAccount.DisplayName);
            Equal(1L, f.Module.SelectedAccount.Generation);
            True(row.Membership.StartsWith("VIP有效 · ", StringComparison.Ordinal), "canonical entitlement consumed");
            Equal(1, f.Peer.Requests); True(f.Runtime.Calls.All(call => call.Method.StartsWith("music.platform.", StringComparison.Ordinal)), "no script IO");
            await f.Module.DeactivateSettingsAsync(); await f.Module.ActivateSettingsAsync();
            Equal("Fresh native name", row.DisplayName); Equal(1, f.Peer.Requests);
            await f.Module.DisposeAsync();
            await using var cold = new MusicNativeApplication(f.Store, MusicTestStores.For(f.Store), f.Runtime, f.Cache);
            var cached = await cold.ReadDetailsAsync("wy");
            Equal("saved", cached.State); Equal("Fresh native name", cached.Profile?.DisplayName);
            True(cached.Membership.Vip == true && cached.Generation == 1, "cold cache never creates a verified login");
        });
        yield return Case("actual module shutdown drains account detail HTTP and hidden resume never refreshes remotely", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            f.Peer.HoldRights = true;
            var refresh = row.RefreshDetailsAsync();
            await f.Peer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepare = f.Module.PrepareShutdownAsync();
            await f.Peer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!prepare.IsCompleted, "real canonical work still physically held");
            f.Peer.Release.TrySetResult(); await Task.WhenAll(refresh, prepare);
            True(row.Membership == "", "closed settings surface retains no private entitlement display");
            await f.Module.CancelShutdownPreparationAsync();
            Equal(1, f.Peer.Requests);
            True(row.Membership == "", "an unmounted settings surface was not silently activated");
            await f.Module.ActivateSettingsAsync();
            Equal(1, f.Peer.Requests); True(f.Runtime.Calls.All(call => call.Method.StartsWith("music.platform.", StringComparison.Ordinal)), "no script IO");
        });
        yield return Case("actual logout retires a late account-details response without restoring profile or rights", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            f.Peer.HoldRights = true;
            var refresh = row.RefreshDetailsAsync();
            await f.Peer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var logout = f.Module.LogoutAccountAsync(f.Module.SelectedAccount!, confirmed: true);
            await f.Peer.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Peer.Release.TrySetResult(); await Task.WhenAll(refresh, logout);
            await SearchUntil(() => !row.IsLoggedIn && row.DisplayName == "未登录");
            Equal("none", f.Module.SelectedAccount!.State);
            True(f.Module.SelectedAccount.Generation > 1 && row.ShowLogin && row.Membership == "",
                "old account generation never republishes");
            True(f.Runtime.Calls.All(call => call.Method.StartsWith("music.platform.", StringComparison.Ordinal)), "no script IO");
        });
    }

    private sealed class DetailsScene : IAsyncDisposable
    {
        internal readonly LocalDocumentStore Store = ServiceStore();
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal readonly DetailsConsumerPeer Peer = new();
        internal readonly CacheDocumentStore Cache = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private DetailsScene()
        {
            Runtime.OnRequest = Peer.Respond;
            App = new(Store, MusicTestStores.For(Store), Runtime, Cache);
            Module = new(App, Store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<DetailsScene> Create()
        {
            var scene = new DetailsScene();
            try
            {
                await using (var seed = new MusicPlatformRuntimeFixture()) {
                    seed.OnRequest = (call, _) => {
                        if (call.Operation == "qr.cancel") return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { cancelled = true }));
                        True(call.Operation == "account.verify", "seed authenticates through owner");
                        var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("wy", DetailsConsumerPeer.UserId, vip: false, displayName: "Saved name").GetRawText())!.AsObject();
                        value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                        return Task.FromResult(MusicPlatformRuntimeFixture.Json(value));
                    };
                    await using var owner = seed.CreateAccountOwner(scene.Store);
                    True((await owner.CallAsync("music.accounts.import", new { platform = "wy",
                        cookie = "MUSIC_U=SYNTHETIC_DETAILS_CONSUMER; __csrf=SYNTHETIC_CSRF" })).GetProperty("ok").GetBoolean(), "seed current account through owner");
                }
                await scene.Module.InitializeAsync(default);
                scene.Module.SelectedAccount = scene.Module.Accounts.Single(a => a.Platform == "wy");
                await scene.Module.ReadAccountsAsync(); await Drain();
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Peer.Release.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Cache.DisposeAsync(); await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }

    private sealed class DetailsConsumerPeer
    {
        internal const string UserId = "9007199254740997";
        internal bool HoldRights;
        internal int Requests;
        internal string DisplayName = "Fresh native name", Avatar = "";
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            True(call.Operation is "account.verify" or "account.profile", "only account observations");
            Interlocked.Increment(ref Requests);
            using var registration = token.Register(() => Cancelled.TrySetResult());
            if (HoldRights) { Entered.TrySetResult(); await Release.Task; }
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var value = JsonNode.Parse(MusicPlatformRuntimeFixture.Json(new MusicAccountDetailsObservation(
                new("wy", UserId, DisplayName, Avatar), now,
                new(new(true, "vip", now + 600000), now, now + 600000, ["standard", "exhigh"]))).GetRawText())!.AsObject();
            if (call.Operation == "account.verify") value["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
            return MusicPlatformRuntimeFixture.Json(value);
        }
    }
}
