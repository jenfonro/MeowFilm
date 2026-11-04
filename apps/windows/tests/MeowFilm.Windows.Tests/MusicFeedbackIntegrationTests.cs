using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Capabilities;
using MeowFilm.AppHost;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicFeedbackIntegrationTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.NativeServiceIntegrationCases();
}

// Reuse this test assembly's STA runner and public playback/runtime probes.
// No private implementation from another business module is accessed.
public static partial class MusicUiTests
{
    private static DpapiDocumentStore ServiceStore() =>
        new(NewRoot(), AppIdentity.NativeNamespaces, AppIdentity.NativeCollections);

    internal static IEnumerable<TestCase> NativeServiceIntegrationCases()
    {
        yield return Case("assembled sync is native and disabled without account or script IO", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            await using var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            var reply = await ServiceCall(app, "music.librarySync.get");
            foreach (var platform in reply.GetProperty("platforms").EnumerateObject())
                True(!platform.Value.GetProperty("favorites").GetBoolean() &&
                    !platform.Value.GetProperty("playlists").GetBoolean(), "default-off");
            await ServiceCall(app, "music.librarySync.run");
            Equal(0, runtime.Calls.Count);
            True(!(await store.ReadAsync("music.accounts.v1")).Fingerprint.Exists, "no account writer");
            True(!(await store.ReadAsync("music.feedback.v1")).Fingerprint.Exists, "no implicit feedback consent");
        });
        yield return Case("assembled scheduler is startup-local and drains explicit work before library shutdown", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            await using var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int notifications = 0;
            app.LibrarySyncChanged += () => { Interlocked.Increment(ref notifications); observed.TrySetResult(); };
            await app.InitializeAsync(); await app.InitializeAsync();
            await Task.Delay(30);
            Equal(0, notifications);
            await ServiceCall(app, "music.librarySync.run");
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await app.PrepareShutdownAsync();
            Equal(1, notifications);
            var sealedReply = await app.ExecuteAsync("music.librarySync.get");
            Equal("CLOSED", sealedReply.GetProperty("code").GetString());
            Equal(0, runtime.Calls.Count);
        });
        yield return Case("assembled cancelled shutdown restores the same local library and sync routes", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            await using var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            var created = await ServiceCall(app, "music.library.create", new { name = "接续歌单" });
            string id = created.GetProperty("collection").GetProperty("id").GetString()!;
            await app.PrepareShutdownAsync(); await app.CancelShutdownPreparationAsync();
            await ServiceCall(app, "music.librarySync.get");
            var library = await ServiceCall(app, "music.library.detail", new { id, offset = 0, limit = 20 });
            Equal(id, library.GetProperty("collection").GetProperty("id").GetString());
            Equal(0, runtime.Calls.Count);
        });
        yield return Case("assembled sync refuses enabling without account while preserving native data", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            await using var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            var before = await ServiceCall(app, "music.librarySync.get");
            var denied = await app.ExecuteAsync("music.librarySync.set", new {
                platform = "wy", favorites = true, playlists = true,
                ifRevision = before.GetProperty("revision").GetInt64(), confirm = true });
            Equal("AUTH_REQUIRED", denied.GetProperty("code").GetString());
            var after = await ServiceCall(app, "music.librarySync.get");
            Equal(before.GetProperty("revision").GetInt64(), after.GetProperty("revision").GetInt64());
            True(!after.GetProperty("platforms").GetProperty("wy").GetProperty("favorites").GetBoolean(), "failed enable is not consent");
            Equal(0, runtime.Calls.Count);
        });
        yield return Case("module restores feedback preferences before opening settings and binds platform capsules to one sync owner", async () =>
        {
            await using var store = ServiceStore();
            await using (var preferences = new MusicFeedbackSettingsOwner(store))
            {
                await preferences.ReadAsync(); await preferences.SaveAsync(0, ["wy"]);
            }
            await using var runtime = new NativeMusicScriptTestRuntime();
            var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            await using var module = new MusicCapability(app, store, new PlaybackProbe(), new Dialogs());
            await module.InitializeAsync(default);
            True(module.FeedbackPlatforms.Single(p => p.Platform == "wy").Enabled, "saved consent is restored");
            True(module.FeedbackPlatforms.Where(p => p.Platform != "wy").All(p => !p.Enabled), "no other platform consent");
            module.SettingsView.Measure(new Size(1000, 1000));
            module.SettingsView.Arrange(new Rect(0, 0, 1000, 1000)); module.SettingsView.UpdateLayout();
            var accounts = Visuals(module.SettingsView).OfType<MusicAccountsView>().Single();
            True(ReferenceEquals(accounts.DataContext, module), "real settings consumer uses the capability");
            foreach (var row in module.PlatformSettings)
                True(ReferenceEquals(row.Sync,
                    module.LibrarySync.Platforms.Single(item => item.Platform == row.Platform)),
                    "platform capsule must not create another sync state");
            await module.ActivateSettingsAsync(); True(module.LibrarySync.CanEdit, "settings activated");
            await module.DeactivateSettingsAsync(); True(!module.LibrarySync.CanEdit, "settings retired");
            Equal(0, runtime.Calls.Count);
        });
        yield return Case("queue preserves bounded feedback provenance through native cold reopen", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            await using (var app = new MusicNativeApplication(store, runtime, new NoUpstream()))
            {
                await ServiceCall(app, "music.queue.replace", new { songs = new[] { Song("original-recording") }, index = 0,
                    context = new { sourcePlatform = "wy", sourceType = "playlist", sourceId = "500", sourceTitle = "列表" } });
            }
            await using var reopened = new MusicNativeApplication(store, runtime, new NoUpstream());
            var reply = await ServiceCall(reopened, "music.queue.get", new { offset = 0, limit = 1 });
            var context = reply.GetProperty("current").GetProperty("context");
            Equal("wy", context.GetProperty("sourcePlatform").GetString());
            Equal("playlist", context.GetProperty("sourceType").GetString());
            Equal("500", context.GetProperty("sourceId").GetString());
            Equal(0, runtime.Calls.Count);
        });
        yield return Case("queue rejects unsafe provenance without changing the existing queue", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            await using var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            await ServiceCall(app, "music.queue.replace", new { songs = new[] { Song("retained") }, index = 0 });
            var before = (await store.ReadAsync("music.library.v1")).Fingerprint;
            var invalid = await app.ExecuteAsync("music.queue.replace", new { songs = new[] { Song("rejected") }, index = 0,
                context = new { sourcePlatform = "wy", sourceType = "playlist", sourceId = "500\nheader" } });
            True(!invalid.GetProperty("ok").GetBoolean(), "unsafe context denied");
            Equal(before, (await store.ReadAsync("music.library.v1")).Fingerprint);
            Equal(0, runtime.Calls.Count);
        });
    }

    private static async Task<JsonElement> ServiceCall(MusicNativeApplication app, string method, object? input = null)
    {
        var result = await app.ExecuteAsync(method, input);
        True(result.GetProperty("ok").GetBoolean(), method + " failed");
        return result;
    }
}
