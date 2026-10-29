using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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
        yield return Case("actual native account details refresh updates the selected account and persists a cold local view", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            Equal("saved", f.Module.SelectedAccount!.State); Equal(0, f.Http.Requests);
            await row.RefreshDetailsAsync();
            Equal("Fresh native name", row.DisplayName);
            Equal("verified", f.Module.SelectedAccount.State);
            Equal("Fresh native name", f.Module.SelectedAccount.DisplayName);
            Equal(1L, f.Module.SelectedAccount.Generation);
            True(row.Membership.StartsWith("VIP · ", StringComparison.Ordinal), "actual entitlement wire consumed");
            Equal(2, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
            await f.Module.DeactivateSettingsAsync(); await f.Module.ActivateSettingsAsync();
            Equal("Fresh native name", row.DisplayName); Equal(2, f.Http.Requests);
            await f.Module.DisposeAsync();
            await using var cold = new MusicNativeApplication(f.Store, f.Runtime,
                new DetailsConsumerHttp { DenyAll = true });
            var cached = await cold.ReadDetailsAsync("wy");
            Equal("saved", cached.State); Equal("Fresh native name", cached.Profile?.DisplayName);
            True(cached.Membership.Vip == true && cached.Generation == 1, "cold cache never creates a verified login");
        });
        yield return Case("actual module shutdown drains account detail HTTP and hidden resume never refreshes remotely", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            f.Http.HoldRights = true;
            var refresh = row.RefreshDetailsAsync();
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepare = f.Module.PrepareShutdownAsync();
            await f.Http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!prepare.IsCompleted, "real HTTP still physically held");
            f.Http.Release.TrySetResult(); await Task.WhenAll(refresh, prepare);
            True(row.Membership == "", "closed settings surface retains no private entitlement display");
            await f.Module.CancelShutdownPreparationAsync();
            Equal(2, f.Http.Requests);
            True(row.Membership == "", "an unmounted settings surface was not silently activated");
            await f.Module.ActivateSettingsAsync();
            Equal(2, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("actual logout retires a late account-details response without restoring profile or rights", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            f.Http.HoldRights = true;
            var refresh = row.RefreshDetailsAsync();
            await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var logout = f.Module.LogoutAccountAsync();
            await f.Http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Http.Release.TrySetResult(); await Task.WhenAll(refresh, logout);
            await SearchUntil(() => !row.IsLoggedIn && row.DisplayName == "未登录");
            Equal("none", f.Module.SelectedAccount!.State);
            True(f.Module.SelectedAccount.Generation > 1 && row.ShowLogin && row.Membership == "",
                "old account generation never republishes");
            Equal(0, f.Runtime.Calls.Count);
        });
    }

    private sealed class DetailsScene : IAsyncDisposable
    {
        internal readonly DpapiDocumentStore Store = ServiceStore();
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly DetailsConsumerHttp Http = new();
        internal readonly MusicNativeApplication App;
        internal readonly MusicCapability Module;
        private DetailsScene()
        {
            App = new(Store, Runtime, Http); Runtime.Register(App);
            Module = new(App, Store, new PlaybackProbe(), new Dialogs());
        }
        internal static async Task<DetailsScene> Create()
        {
            var scene = new DetailsScene();
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 1,
                    entries = new[] { new {
                        key = "[\"meowfilm\",\"music\",1,\"platform-account\",\"wy\"]",
                        value = new { version = 1, generation = 1,
                            cookie = "MUSIC_U=SYNTHETIC_DETAILS_CONSUMER; __csrf=SYNTHETIC_CSRF",
                            profile = new { platform = "wy", userId = DetailsConsumerHttp.UserId, displayName = "Saved name" },
                            scope = "synthetic-consumer-account", validatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                    } }
                });
                try { await scene.Store.WriteAsync(MusicAccountOwner.DocumentNamespace, bytes, DocumentFingerprint.Missing); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                await scene.Module.InitializeAsync(default);
                scene.Module.SelectedAccount = scene.Module.Accounts.Single(a => a.Platform == "wy");
                await scene.Module.ReadAccountsAsync(); await Drain();
                return scene;
            }
            catch { await scene.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Http.Release.TrySetResult();
            try { await Module.DisposeAsync(); }
            finally { await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
        }
    }

    private sealed class DetailsConsumerHttp : HttpMessageHandler
    {
        internal const string UserId = "9007199254740997";
        internal bool HoldRights, DenyAll;
        internal int Requests;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            True(!DenyAll, "local restored view must not invoke any HTTP");
            Interlocked.Increment(ref Requests);
            Equal("music.163.com", request.RequestUri!.Host);
            Equal("https", request.RequestUri.Scheme);
            string path = request.RequestUri.AbsolutePath;
            object response;
            if (path == "/weapi/w/nuser/account/get")
                response = new { code = 200, account = new { id = UserId },
                    profile = new { userId = UserId, nickname = "Fresh native name" } };
            else
            {
                Equal("/weapi/music-vip-membership/client/vip/info", path);
                using var registration = token.Register(() => Cancelled.TrySetResult());
                if (HoldRights) { Entered.TrySetResult(); await Release.Task; }
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                response = new { code = 200, data = new { userId = UserId, now,
                    associator = new { expireTime = now + 600000 }, musicPackage = new { expireTime = 0 },
                    redplus = new { expireTime = 0 } } };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }
    }
}
