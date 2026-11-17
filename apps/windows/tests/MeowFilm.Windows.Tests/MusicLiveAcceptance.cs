using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Media;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Deliberately neither a *Tests class nor a Cases provider. Never runs in the normal inventory.
internal static class MusicLiveAcceptance
{
    internal static async Task<int> RunAsync(bool kugouHomeOnly = false, bool catalogOnly = false)
    {
        using var report = new Report();
        try
        {
            Require(OperatingSystem.IsWindows(), "UNSUPPORTED");
            if (catalogOnly)
            {
                Require(Program.CheckDesktopContext(requireTarget: true), "PERMISSION_DENIED");
                report.Record("desktop.identity", "PASS", count: 1);
            }
            else
            {
                using var process = Process.GetCurrentProcess();
                using var identity = WindowsIdentity.GetCurrent();
                report.WriteIdentity(process.Id, identity.Name, process.SessionId, Environment.UserInteractive);
                Require(identity.Name.Split('\\')[^1] == "79917" &&
                    process.SessionId == 1 && Environment.UserInteractive, "PERMISSION_DENIED");
            }
            TestWorkspace.Initialize();
            string root = TestWorkspace.DataPath("live-music-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            report.Open(Path.Combine(root, "result.log"));
            await OnDesktopAsync(() => RunOwnedAsync(root, report, kugouHomeOnly, catalogOnly), report);
        }
        catch (Exception error) { report.Record("live.bootstrap", "FAIL", ErrorCode(error)); }
        report.Record("live.complete", report.Success ? "PASS" : "FAIL",
            report.Success ? null : "NOT_COVERED", report.Failures + report.NotCovered);
        // Settle the output receipt before choosing the process result, not in the implicit finally.
        report.Dispose();
        return report.Success ? 0 : 1;
    }

    private static async Task OnDesktopAsync(Func<Task> action, Report report)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception error) { report.Record("live.native", "FAIL", ErrorCode(error)); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            try { Dispatcher.Run(); }
            catch (Exception error) { report.Record("desktop.dispatcher", "FAIL", ErrorCode(error)); }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "music-live-acceptance" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await stopped.Task;
    }

    private static async Task RunOwnedAsync(string root, Report report, bool kugouHomeOnly, bool catalogOnly)
    {
        string data = TestWorkspace.ValidatePath(root, Path.Combine(root, "data"));
        Directory.CreateDirectory(data);
        Require(!Directory.EnumerateFileSystemEntries(data).Any(), "STORAGE_CONFLICT");
        LocalDocumentStore? store = null, cacheBacking = null;
        CacheDocumentStore? cache = null;
        NodeRuntime? runtime = null;
        NativeAudioPlayback? player = null;
        MusicNativeApplication? application = null;
        MusicCapability? module = null;
        Window? window = null;
        bool importAttempted = false, logoutAttempted = false;
        try
        {
            report.Stage = "live.startup";
            string bundleRoot = Environment.GetEnvironmentVariable("MEOWFILM_TEST_RUNTIME_ROOT")
                ?? throw new LiveFailure("NOT_COVERED");
            var bundle = RuntimeBundle.Verify(Path.Combine(
                TestWorkspace.ValidatePath(TestWorkspace.ResultsRoot, bundleRoot), AppIdentity.RuntimeId),
                AppIdentity.RuntimeId);
            Directory.CreateDirectory(Path.Combine(data, "native"));
            Directory.CreateDirectory(Path.Combine(data, "cache"));
            store = new(Path.Combine(data, "native"), AppIdentity.NativeNamespaces,
                AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces);
            cacheBacking = new(Path.Combine(data, "cache"), AppIdentity.CacheNamespaces,
                protectedNamespaces: AppIdentity.CacheProtectedNamespaces);
            cache = new(cacheBacking);
            runtime = await NodeRuntime.StartAsync(TestWorkspace.NodePath, bundle, AppIdentity.ScriptModules,
                Path.Combine(data, "scripts"));
            report.Record("live.runtime-pid", "PASS", count: runtime.ProcessId);
            var observed = new ObservedRuntime(runtime, report);
            MusicTestStores.Use(store, Path.Combine(data, "native", "music-library"));
            application = new(store, MusicTestStores.For(store), observed, cache,
                presentationCacheRoot: Path.Combine(data, "music-presentation-v2"));
            player = new(audioOutput: NativeAudioOutput.SystemDefault);
            module = new(application, store, player, playbackCacheRoot: Path.Combine(data, "media"), cacheStore: cache);

            var layout = new Grid();
            layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
            layout.Children.Add(module.View);
            Grid.SetRow(module.PlayerView, 1);
            layout.Children.Add(module.PlayerView);
            window = new Window { Title = "MeowFilm music live acceptance", Width = 1100, Height = 800,
                Content = layout, ShowActivated = false };
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ContentRendered += (_, _) => rendered.TrySetResult();
            window.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Require(window.IsVisible && PresentationSource.FromVisual(module.View) is not null &&
                module.View.ActualWidth > 0 && module.View.ActualHeight > 0, "NOT_COVERED");
            report.Record("desktop.window.programmatic-no-input", "PASS", count: 1);
            if (catalogOnly)
            {
                await module.PrepareStartupAsync(CancellationToken.None);
                await CatalogAsync(application, module, player, observed, report, window);
                return;
            }
            await module.InitializeAsync(CancellationToken.None);
            await module.ActivateQualitySettingsAsync();
            await module.SetPlaybackQualityRequestAsync("128k");
            Require(module.PlaybackQualityRequest == "128k", "NOT_COVERED");
            report.Stage = "live.official-sources.wy-only";
            var sources = Reply(await application.ExecuteAsync("music.officialSources.settings.get")).GetProperty("settings");
            var savedSources = Reply(await application.ExecuteAsync("music.officialSources.settings.save",
                new { ifRevision = sources.GetProperty("revision").GetInt64(), enabled = new[] { "wy" } })).GetProperty("settings");
            var enabledSources = savedSources.GetProperty("enabled");
            Require(enabledSources.GetArrayLength() == 1 && enabledSources[0].GetString() == "wy", "INVALID_RESPONSE");
            report.Record("live.official-sources.wy-only", "PASS", count: 1);
            await CheckSafetyAsync(application);
            Require((await AccountStatesAsync(application)).Values.All(state => state == "none"), "STORAGE_CONFLICT");
            report.Record("guest.empty-stores.sync-feedback-off", "PASS", count: 1);

            if (kugouHomeOnly)
            {
                await report.StepAsync("guest.kg.home", async () =>
                {
                    int before = observed.Successes("kg", "home");
                    int cards = await HomeAsync(module, observed, report, window, "kg", refresh: false);
                    Require(observed.Successes("kg", "home") > before, "NOT_COVERED");
                    return cards;
                });
                return;
            }

            await ExerciseWyAsync("guest", application, module, player, observed, report, window);
            await report.StepAsync("guest.kg.home", () => HomeAsync(module, observed, report, window, "kg", refresh: true));

            var imported = new HashSet<string>(StringComparer.Ordinal);
            int authenticatedPublications = observed.AuthenticatedPublications("wy");
            await report.StepAsync("account.file-import", async () =>
            {
                // This is the only credential-file read; no path or file bytes enter the report.
                string? path = Environment.GetEnvironmentVariable("MEOWFILM_LIVE_MUSIC_ACCOUNT_FILE");
                Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path), "NOT_COVERED");
                importAttempted = true;
                using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var file = await MusicAccountTransferFile.ReadAsync(stream);
                using var plan = await application.PrepareAccountTransferAsync(file);
                Require(plan.Conflicts.Count == 0, "STORAGE_CONFLICT");
                var summary = await application.ImportAccountTransferAsync(plan, MusicAccountTransferPolicy.Skip);
                foreach (var item in summary.Items)
                {
                    if (item.Status == "imported")
                    {
                        imported.Add(item.Platform);
                        report.Record("account.import." + Platform(item.Platform), "PASS", count: 1);
                    }
                    else report.Record("account.import." + Platform(item.Platform), "FAIL", SafeCode(item.Code));
                }
                Require(summary.Items.Count > 0, "NOT_COVERED");
                await module.ReadAccountsAsync();
                var states = await AccountStatesAsync(application);
                Require(imported.All(platform => states[platform] == "verified"), "NOT_COVERED");
                await CheckSafetyAsync(application);
                return imported.Count;
            });
            if (imported.Contains("wy") && (await AccountStatesAsync(application))["wy"] == "verified")
            {
                await ExerciseWyAsync("imported", application, module, player, observed, report, window);
                await report.StepAsync("imported.wy.real-session-publication", () =>
                {
                    Require(observed.AuthenticatedPublications("wy") > authenticatedPublications, "NOT_COVERED");
                    return Task.FromResult(observed.AuthenticatedPublications("wy") - authenticatedPublications);
                });
            }
            else report.Record("imported.wy.chain", "NOT_COVERED", "NOT_COVERED");
            if (imported.Contains("kg"))
                await report.StepAsync("imported.kg.home", () => HomeAsync(module, observed, report, window, "kg", refresh: true));

            int guestPublications = observed.GuestPublications("wy");
            logoutAttempted = true;
            bool loggedOut = await LogoutAsync(application, module, player, report);
            await report.StepAsync("logout.wy.home", () => HomeAsync(module, observed, report, window, "wy", refresh: true));
            await report.StepAsync("logout.wy.real-guest-publication", () =>
            {
                Require(loggedOut && observed.GuestPublications("wy") > guestPublications, "NOT_COVERED");
                return Task.FromResult(observed.GuestPublications("wy") - guestPublications);
            });
        }
        finally
        {
            if (importAttempted && !logoutAttempted && application is not null && module is not null && player is not null)
                await LogoutAsync(application, module, player, report);
            // Module owns application shutdown, but borrows the real player/runtime/stores.
            bool closed = true;
            if (module is not null) closed &= await report.StepAsync("close.module", async () => { await module.DisposeAsync(); return 1; });
            else if (application is not null) closed &= await report.StepAsync("close.application", async () => { await application.DisposeAsync(); return 1; });
            if (player is not null) closed &= await report.StepAsync("close.native-media", async () => { await player.DisposeAsync(); return 1; });
            if (runtime is not null) closed &= await report.StepAsync("close.runtime-drain", async () =>
            { await runtime.DisposeAsync(); await runtime.Completion; return 1; });
            if (cache is not null) closed &= await report.StepAsync("close.cache-drain", async () =>
            { await cache.DisposeAsync(); return 1; });
            if (cacheBacking is not null) closed &= await report.StepAsync("close.cache-store", async () => { await cacheBacking.DisposeAsync(); return 1; });
            if (store is not null) closed &= await report.StepAsync("close.native-store", async () => { await MusicTestStores.For(store).DisposeAsync(); await store.DisposeAsync(); return 1; });
            if (window is not null) window.Close();
            // Only this newly created data subtree, and only after every owner has physically closed.
            if (closed) await report.StepAsync("close.owned-data", () =>
            {
                TestWorkspace.ValidatePath(root, data);
                Directory.Delete(data, recursive: true);
                return Task.FromResult(1);
            });
            else report.Record("close.owned-data", "NOT_COVERED", "NOT_COVERED");
        }
    }

    private static async Task CatalogAsync(MusicNativeApplication application, MusicCapability module, NativeAudioPlayback player,
        ObservedRuntime runtime, Report report, Window window)
    {
        Require((await AccountStatesAsync(application)).Values.All(state => state == "none"), "STORAGE_CONFLICT");
        foreach (string platform in new[] { "wy", "kg" })
        {
            bool home = await report.StepAsync("guest." + platform + ".home.ui",
                () => HomeAsync(module, runtime, report, window, platform, refresh: true));
            if (home)
                await report.StepAsync("guest." + platform + ".home.artwork", () => WaitForVisibleArtworkAsync(window));
            if (platform != "wy") continue;
            int directoryReads = runtime.Successes("wy", "channels"), channelReads = runtime.Successes("wy", "channel");
            int errors = report.Failures;
            MusicCatalogCard? channel = null;
            bool directory = await report.StepAsync("guest.wy.channels.ui", async () =>
            {
                await module.NavigateAsync("catalog-channels", CancellationToken.None);
                await RenderAsync(window);
                Require(runtime.Successes("wy", "channels") > directoryReads, "NOT_COVERED");
                channel = module.VisibleChannelTabs.Concat(module.MoreChannelTabs).FirstOrDefault(card => card.CanOpen);
                Require(channel is not null, report.Failures > errors ? report.LastError : "NOT_COVERED");
                return module.ChannelTabs.Count;
            });
            if (directory)
            {
                var selected = channel!;
                await report.StepAsync("guest.wy.channel.ui", async () =>
                {
                    await RenderAsync(window);
                    Require(runtime.Successes("wy", "channel") > channelReads &&
                        module.CaptureNavigationLocation() == "catalog-channel/wy/" + Uri.EscapeDataString(selected.ActionId) &&
                        Cards(module).Any() && module.Status.Length == 0,
                        report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
                    return Cards(module).Count();
                });
                await report.StepAsync("guest.wy.channel.artwork", () => WaitForVisibleArtworkAsync(window));
                string firstRoute = module.CaptureNavigationLocation();
                var alternatives = module.VisibleChannelTabs.Concat(module.MoreChannelTabs)
                    .Where(card => card.CanOpen && !card.RequiresAccount && card.Kind == "channel" &&
                        card.Song is null && card.Locator is null && card.ActionId != selected.ActionId)
                    .Take(2).ToArray();
                for (int index = 0; index < 2; index++)
                {
                    int position = index;
                    bool opened = await report.StepAsync("guest.wy.channel.switch-" + (index + 1), async () =>
                    {
                        Require(position < alternatives.Length, "NOT_COVERED");
                        var target = alternatives[position];
                        await module.OpenCatalogCardAsync(target);
                        await RenderAsync(window);
                        Require(module.CaptureNavigationLocation() == "catalog-channel/wy/" +
                            Uri.EscapeDataString(target.ActionId) && Cards(module).Any() &&
                            module.Status.Length == 0, "NOT_COVERED");
                        Require(!player.HasMedia && !player.IsPlaying, "PERMISSION_DENIED");
                        return Cards(module).Count();
                    });
                    if (opened)
                        await report.StepAsync("guest.wy.channel.switch-" + (index + 1) + ".artwork",
                            () => WaitForVisibleArtworkAsync(window));
                }
                await report.StepAsync("guest.wy.channel.restore", async () =>
                {
                    await module.RestoreNavigationAsync(firstRoute, CancellationToken.None);
                    await RenderAsync(window);
                    Require(module.CaptureNavigationLocation() == firstRoute && Cards(module).Any() &&
                        module.Status.Length == 0, "NOT_COVERED");
                    return Cards(module).Count();
                });
                await report.StepAsync("guest.wy.channel.restore.artwork", () => WaitForVisibleArtworkAsync(window));
            }
            else report.Record("guest.wy.channel.ui", "NOT_COVERED", "NOT_COVERED");
        }
        Require(!player.HasMedia && !player.IsPlaying &&
            runtime.AuthenticatedPublications("wy") == 0 && runtime.AuthenticatedPublications("kg") == 0, "PERMISSION_DENIED");
        report.Record("guest.catalog.no-account-or-media", "PASS", count: 1);
    }

    private static async Task<int> WaitForVisibleArtworkAsync(Window window)
    {
        await RenderAsync(window);
        var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        var images = Descendants(window).OfType<ArtworkImage>().Where(image =>
            image.IsVisible && image.ActualWidth > 0 && image.ActualHeight > 0 && image.Address.Length > 0 &&
            image.TransformToAncestor(window).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight))
                .IntersectsWith(bounds)).ToArray();
        Require(images.Length > 0, "NOT_COVERED");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var held = new List<IDisposable>();
        try
        {
            foreach (var image in images)
                held.Add(await image.PrepareForPresentationAsync(deadline.Token));
            Require(images.All(image => image.Source is not null), "NOT_COVERED");
            return images.Length;
        }
        finally { foreach (var lease in held) lease.Dispose(); }

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }

    private static async Task CheckSafetyAsync(MusicNativeApplication application)
    {
        Require((await application.Feedback.ReadSettingsAsync(refresh: true)).Enabled.Count == 0, "PERMISSION_DENIED");
        var sync = Reply(await application.ExecuteAsync("music.librarySync.get"));
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            var settings = sync.GetProperty("platforms").GetProperty(platform);
            Require(!settings.GetProperty("favorites").GetBoolean() && !settings.GetProperty("playlists").GetBoolean(),
                "PERMISSION_DENIED");
        }
    }

    private static async Task<Dictionary<string, string>> AccountStatesAsync(MusicNativeApplication application)
    {
        var states = Reply(await application.ExecuteAsync("music.accounts.list")).GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("platform").GetString()!, row => row.GetProperty("state").GetString()!);
        Require(states.Count == 3 && new[] { "wy", "tx", "kg" }.All(states.ContainsKey), "INVALID_RESPONSE");
        return states;
    }

    private static async Task<bool> LogoutAsync(MusicNativeApplication application, MusicCapability module,
        NativeAudioPlayback player, Report report)
    {
        bool stopped = await report.StepAsync("account.logout.stop", async () =>
        {
            await module.CancelRecommendationAsync();
            await player.StopAsync();
            return 1;
        });
        bool cleared = true;
        // The public application command also permits removing a revoked saved credential.
        // Each local mutation is attempted once only, including a lost/unknown receipt.
        foreach (string platform in new[] { "wy", "tx", "kg" })
            cleared &= await report.StepAsync("account.logout." + platform, async () =>
            {
                Reply(await application.ExecuteAsync("music.accounts.logout", new { platform }));
                return 1;
            });
        bool confirmed = await report.StepAsync("account.logout.readback", async () =>
        {
            await module.ReadAccountsAsync();
            Require((await AccountStatesAsync(application)).Values.All(state => state == "none"), "COMMIT_UNKNOWN");
            await CheckSafetyAsync(application);
            return 3;
        });
        return stopped && cleared && confirmed;
    }

    private static async Task ExerciseWyAsync(string phase, MusicNativeApplication application, MusicCapability module, NativeAudioPlayback player,
        ObservedRuntime runtime, Report report, Window window)
    {
        if (!await report.StepAsync(phase + ".wy.home", () => HomeAsync(module, runtime, report, window, "wy", refresh: true)))
        {
            foreach (string item in new[] { "similar", "fm", "playlist", "album", "single" })
                report.Record(phase + ".wy." + item, "NOT_COVERED", "NOT_COVERED");
            return;
        }
        foreach (string kind in new[] { "similar", "fm" })
            await report.StepAsync(phase + ".wy." + kind, async () =>
            {
                await HomeAsync(module, runtime, report, window, "wy", refresh: false);
                var card = Cards(module).FirstOrDefault(card => card.Kind == kind);
                Require(card is not null, "NOT_COVERED");
                Require(card!.CanOpen, card.ErrorCode.Length > 0 ? SafeCode(card.ErrorCode) : "UNSUPPORTED");
                return await PlayAsync(() => module.OpenCatalogCardAsync(card), module, player, report);
            });

        string? fallbackDetail = null;
        foreach (string kind in new[] { "playlist", "album" })
        {
            bool opened = await report.StepAsync(phase + ".wy." + kind + ".detail", async () =>
            {
                await HomeAsync(module, runtime, report, window, "wy", refresh: false);
                int errors = report.Failures;
                var card = Cards(module).FirstOrDefault(card => card.Kind == kind);
                if (kind == "album" && card is null)
                {
                    card = Cards(module).FirstOrDefault(card => card.CanAlbum);
                    if (card is null && fallbackDetail is not null)
                    {
                        // Album links can belong to actual playlist members rather than home.
                        await module.NavigateAsync(fallbackDetail, CancellationToken.None);
                        card = Cards(module).FirstOrDefault(card => card.CanAlbum);
                    }
                    Require(card is not null, report.Failures > errors ? report.LastError : "NOT_COVERED");
                    await module.AlbumCatalogCardAsync(card!);
                }
                else
                {
                    Require(card is not null, "NOT_COVERED");
                    Require(card.CanOpen, card.ErrorCode.Length > 0 ? SafeCode(card.ErrorCode) : "UNSUPPORTED");
                    await module.OpenCatalogCardAsync(card!);
                }
                await RenderAsync(window);
                Require(module.CaptureNavigationLocation().StartsWith("catalog-" + kind + "/", StringComparison.Ordinal),
                    report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
                Require(module.Songs.Count > 0, report.Failures > errors ? report.LastError : "NOT_COVERED");
                Require(module.Status.Length == 0, report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
                fallbackDetail = module.CaptureNavigationLocation();
                return module.Songs.Count;
            });
            if (opened)
                await report.StepAsync(phase + ".wy." + kind + ".play-all", async () =>
                {
                    Require(module.CanPlayCatalogPlaylist, "UNCLASSIFIED_CODE");
                    await PlayAsync(module.PlayCatalogPlaylistAsync, module, player, report);
                    var queue = Reply(await application.ExecuteAsync("music.queue.get", new { offset = 0, limit = 1 }));
                    int total = queue.GetProperty("total").GetInt32();
                    // The current entry is returned independently of the page, not necessarily entries[0].
                    var current = queue.GetProperty("current");
                    string? currentId = queue.GetProperty("currentId").GetString();
                    string? songId = current.GetProperty("song").GetProperty("songId").GetString();
                    Require(total > 0 && queue.GetProperty("entries").GetArrayLength() > 0 &&
                        !string.IsNullOrEmpty(currentId) && current.GetProperty("id").GetString() == currentId &&
                        !string.IsNullOrEmpty(songId) && module.CurrentSongId == songId &&
                        module.CurrentTrackIdentity == currentId + "\0" + songId, "INVALID_RESPONSE");
                    return total;
                });
            else report.Record(phase + ".wy." + kind + ".play-all", "NOT_COVERED", "NOT_COVERED");
        }
        await report.StepAsync(phase + ".wy.single.real-media", async () =>
        {
            await HomeAsync(module, runtime, report, window, "wy", refresh: false);
            var card = Cards(module).FirstOrDefault(card => card.CanPlay && (card.Song is not null || card.Kind == "song"));
            if (card is null && fallbackDetail is not null)
            {
                await module.NavigateAsync(fallbackDetail, CancellationToken.None);
                card = Cards(module).FirstOrDefault(card => card.Song is not null && card.CanPlay);
            }
            Require(card is not null, "NOT_COVERED");
            return await PlayAsync(() => module.OpenCatalogCardAsync(card!), module, player, report);
        });
    }

    private static IEnumerable<MusicCatalogCard> Cards(MusicCapability module) =>
        module.CatalogSections.SelectMany(section => section.Action is { } action
            ? section.Items.Append(action) : section.Items).Where(card => card.Platform == module.CatalogSourcePlatform && card.Display);

    private static async Task<int> HomeAsync(MusicCapability module, ObservedRuntime runtime, Report report, Window window,
        string platform, bool refresh)
    {
        int errors = report.Failures;
        if (module.CatalogSourcePlatform != platform) await module.SetCatalogSourceAsync(platform);
        Require(module.CatalogSourcePlatform == platform, report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
        int before = runtime.Successes(platform, "home");
        await module.NavigateAsync("catalog-home", CancellationToken.None);
        // Existing readiness tasks only; all actions still use public navigation/commands.
        await module.HomeRefresh;
        await module.HomeInitialization;
        if (refresh && runtime.Successes(platform, "home") == before)
        {
            await module.RefreshCatalogAsync();
            await module.HomeRefresh;
            await module.HomeInitialization;
        }
        await RenderAsync(window);
        Require(module.IsCatalogHome, report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
        Require(Cards(module).Any(), report.Failures > errors ? report.LastError : "NOT_COVERED");
        Require(module.Status.Length == 0, report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
        Require(!refresh || runtime.Successes(platform, "home") > before, "NOT_COVERED");
        return Cards(module).Count();
    }

    private static async Task<int> PlayAsync(Func<Task> action, MusicCapability module, NativeAudioPlayback player, Report report)
    {
        await module.CancelRecommendationAsync();
        await player.StopAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(object? sender, EventArgs args) { started.TrySetResult(); }
        void Failed(object? sender, EventArgs args) { failed.TrySetResult(); }
        player.Started += Started;
        player.Failed += Failed;
        int errors = report.Failures;
        try
        {
            await action();
            if (!started.Task.IsCompleted && module.Status.Length > 0)
                throw new LiveFailure(report.Failures > errors ? report.LastError : "UNCLASSIFIED_CODE");
            var terminal = await Task.WhenAny(started.Task, failed.Task, Task.Delay(TimeSpan.FromSeconds(45)));
            if (terminal == failed.Task) throw new LiveFailure(SafeCode(player.LastErrorCode));
            if (terminal != started.Task)
                throw new LiveFailure(report.Failures > errors ? report.LastError :
                    module.Status.Length > 0 ? "UNCLASSIFIED_CODE" : "TIMEOUT");
            Require(player.OwnerId == module.Id && player.HasMedia && player.IsPlaying && module.IsPlaying, "UNCLASSIFIED_CODE");
            Require(module.CurrentSource == "网易云音乐", "INVALID_RESPONSE");
            double position = player.PositionSeconds;
            var watch = Stopwatch.StartNew();
            while (player.LastErrorCode is null && player.IsPlaying &&
                player.PositionSeconds <= position && watch.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(100);
            if (player.LastErrorCode is { } code) throw new LiveFailure(SafeCode(code));
            Require(player.HasMedia && player.IsPlaying && module.IsPlaying && player.PositionSeconds > position, "UNCLASSIFIED_CODE");
            Require(module.CurrentSource == "网易云音乐", "INVALID_RESPONSE");
            return 1; // Real native Playing + progressing system-output input, never a URL/mock event.
        }
        finally
        {
            player.Started -= Started;
            player.Failed -= Failed;
            await module.CancelRecommendationAsync();
            await player.StopAsync();
        }
    }

    private static async Task RenderAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Require(window.IsVisible && window.ActualWidth > 0 && window.ActualHeight > 0, "NOT_COVERED");
    }

    private static JsonElement Reply(JsonElement reply)
    {
        if (reply.GetProperty("ok").ValueKind != JsonValueKind.True)
            throw new LiveFailure(SafeCode(reply.GetProperty("code").GetString()));
        return reply;
    }

    private sealed class LiveFailure(string code) : Exception
    {
        internal string Code { get; } = code;
    }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code)
    {
        if (!condition) throw new LiveFailure(code);
    }
    private static string Platform(string platform) => platform is "wy" or "tx" or "kg" or "kw" or "mg" ? platform : "other";
    private static string ErrorCode(Exception error) => error switch
    {
        LiveFailure known => SafeCode(known.Code),
        BridgeException known => SafeCode(known.Code),
        MusicPlatformException known => SafeCode(known.Code),
        MusicAccountException known => SafeCode(known.Code),
        MusicOperationException known => SafeCode(known.Code),
        PlaybackException known => SafeCode(known.Code),
        DocumentStoreException => "STORAGE_ERROR",
        OperationCanceledException => "CANCELLED",
        TimeoutException => "TIMEOUT",
        UnauthorizedAccessException => "PERMISSION_DENIED",
        IOException => "STORAGE_ERROR",
        JsonException or InvalidOperationException or KeyNotFoundException => "INVALID_RESPONSE",
        _ => "INTERNAL"
    };

    private static readonly HashSet<string> Codes = new(BridgeException.Codes.Concat(new[]
    {
        "UNSUPPORTED", "UPSTREAM_ERROR", "AUTH_REQUIRED", "AUTH_EXPIRED", "INVALID_CREDENTIALS",
        "FORBIDDEN", "RATE_LIMITED", "VERIFICATION_REQUIRED", "SIGNATURE_REQUIRED", "DEVICE_CONTEXT_REQUIRED",
        "STALE_SESSION", "NOT_FOUND", "MEDIA_UNAVAILABLE", "TRIAL_ONLY", "COMMIT_UNKNOWN", "REMOTE_CHANGED",
        "CLOUD_RESPONSE_UNCONFIRMED", "QUALITY_UNAVAILABLE", "CREDENTIALS_UNAVAILABLE", "STORAGE_CONFLICT",
        "REDIRECT_DENIED", "BUSY", "QR_EXPIRED", "SMS_SEND_UNCONFIRMED", "DEVICE_LIMIT", "INVALID_CODE",
        "ACCOUNT_SELECTION_REQUIRED", "INVALID_PHONE", "IDENTITY_MISMATCH", "TRANSFER_FORMAT", "TRANSFER_FILE",
        "SAVE_FAILED", "HANDOFF_UNCONFIRMED", "NOT_COVERED", "UNCLASSIFIED_CODE"
    }).Concat(typeof(PlaybackError).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)), StringComparer.Ordinal);
    private static string SafeCode(string? code) => code is not null && Codes.Contains(code) ? code : "UNCLASSIFIED_CODE";

    // Only observes safe receipts; forwards the same real calls, arguments, deadlines and results.
    // In particular this never supplies an identity, a protocol response, a media URI or an event.
    private sealed class ObservedRuntime(NodeRuntime runtime, Report report) : IRuntimeClient
    {
        private readonly object gate = new();
        private readonly Dictionary<(string, string), int> successes = [];
        private readonly Dictionary<(string, bool), int> publications = [];
        internal int Successes(string platform, string operation) { lock (gate) return successes.GetValueOrDefault((platform, operation)); }
        internal int AuthenticatedPublications(string platform) { lock (gate) return publications.GetValueOrDefault((platform, true)); }
        internal int GuestPublications(string platform) { lock (gate) return publications.GetValueOrDefault((platform, false)); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
            ObserveAsync(method, parameters, () => runtime.CallAsync(method, parameters, cancellationToken));
        public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            ObserveAsync(method, parameters, () => runtime.CallAsync(method, parameters, timeout, cancellationToken));
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
            runtime.UploadSourceAsync(module, installationId, sourceRevision, source, basename, cancellationToken);
        public IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
            CancellationToken cancellationToken = default) => runtime.EventsAsync(module, sessionId, after, cancellationToken);
        private async Task<JsonElement> ObserveAsync(string method, object? parameters, Func<Task<JsonElement>> call)
        {
            string stage = report.Stage;
            JsonElement reply;
            try { reply = await call().ConfigureAwait(false); }
            catch (Exception error) { report.Record(stage + ".runtime.threw", "FAIL", ErrorCode(error)); throw; }
            if (reply.ValueKind != JsonValueKind.Object)
            {
                report.Record(stage + ".runtime.envelope", "FAIL", "INVALID_RESPONSE");
                return reply;
            }
            if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                string boundary = reply.TryGetProperty("contract", out var contract) && contract.ValueKind == JsonValueKind.String
                    ? contract.GetString() switch
                    {
                        "music.platform.v1" => "module",
                        "runtime.http.v1" => "wire",
                        _ => "envelope"
                    } : "envelope";
                report.Record(stage + ".runtime." + boundary, "FAIL", reply.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String ? SafeCode(code.GetString()) : "INVALID_RESPONSE");
            }
            else if (ok.ValueKind == JsonValueKind.True && parameters is not null)
            {
                // Read only the non-sensitive routing labels and nullness of the session.
                // Do not serialize parameters: candidate imports can contain credentials.
                string platform = Platform(parameters.GetType().GetProperty("platform")?.GetValue(parameters) as string ?? "");
                lock (gate)
                {
                    if (method == "music.platform.request")
                    {
                        string operation = parameters.GetType().GetProperty("operation")?.GetValue(parameters) as string ?? "";
                        successes[(platform, operation)] = successes.GetValueOrDefault((platform, operation)) + 1;
                    }
                    if (method == "music.platform.session.update" && reply.TryGetProperty("value", out var value) &&
                        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("published", out var published) &&
                        published.ValueKind == JsonValueKind.True)
                    {
                        bool authenticated = parameters.GetType().GetProperty("session")?.GetValue(parameters) is not null;
                        publications[(platform, authenticated)] = publications.GetValueOrDefault((platform, authenticated)) + 1;
                    }
                }
            }
            return reply;
        }
    }

    private sealed class Report : IDisposable
    {
        private readonly object gate = new();
        private readonly List<string> earlyLines = [];
        private StreamWriter? writer;
        private int failures, notCovered;
        private bool outputFailed, disposed;
        private string stage = "live.startup", lastError = "NOT_COVERED";
        internal string Stage { get => Volatile.Read(ref stage); set => Volatile.Write(ref stage, value); }
        internal string LastError { get { lock (gate) return lastError; } }
        internal int Failures { get { lock (gate) return failures; } }
        internal int NotCovered { get { lock (gate) return notCovered; } }
        internal bool Success { get { lock (gate) return failures == 0 && notCovered == 0 && !outputFailed; } }
        internal void Open(string path)
        {
            lock (gate)
            {
                writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                foreach (string line in earlyLines) writer.WriteLine(line);
                earlyLines.Clear();
            }
        }
        internal void WriteIdentity(int pid, string user, int sessionId, bool interactive) =>
            Write($"LIVE desktop.identity pid={pid} user={user} sessionId={sessionId} userInteractive={interactive}");
        internal void Record(string stage, string status, string? code = null, int count = 0)
        {
            lock (gate)
            {
                if (status == "FAIL") { failures++; lastError = SafeCode(code); }
                if (status == "NOT_COVERED") notCovered++;
                Write($"LIVE {stage} {status}" + (code is null ? "" : " code=" + SafeCode(code)) + $" count={count}");
            }
        }
        internal async Task<bool> StepAsync(string stage, Func<Task<int>> action)
        {
            Stage = stage;
            Write($"LIVE {stage} BEGIN");
            try { Record(stage, "PASS", count: await action()); return true; }
            catch (Exception error)
            {
                string code = ErrorCode(error);
                Record(stage, code == "NOT_COVERED" ? "NOT_COVERED" : "FAIL", code);
                return false;
            }
        }
        private void Write(string line)
        {
            lock (gate)
            {
                try
                {
                    Console.WriteLine(line);
                    if (writer is not null) writer.WriteLine(line);
                    else if (!disposed) earlyLines.Add(line);
                }
                catch (IOException) { if (!outputFailed) failures++; outputFailed = true; }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                var closing = writer;
                writer = null;
                try { closing?.Dispose(); }
                catch (Exception error)
                {
                    outputFailed = true;
                    Record("live.output-close", "FAIL", ErrorCode(error));
                }
            }
        }
    }
}
