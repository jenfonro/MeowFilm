using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed record AppDefinition(string Id, IReadOnlyList<RuntimeModule> RuntimeModules);

public static class AppLauncher
{
    public static void Run(AppDefinition definition,
        Func<IRuntimeClient, IStorageDatabase, IReadOnlyList<IDesktopModule>> createModules,
        Func<Action<Application>, Application> createApplication,
        Func<ValueTask>? disposeResources = null,
        Func<DesktopWindow, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null,
        Func<IStorageDatabase, IRuntimeSettingsStore>? runtimeSettings = null,
        Func<Task>? stopPlayback = null)
        => Run(definition, (runtime, store, modules) =>
        {
            foreach (var module in createModules(runtime, store)) modules.Add(module);
        }, createApplication, disposeResources, attachWindow, createPages, runtimeSettings, stopPlayback);

    /// <summary>
    /// Incremental registration owns each completed constructor even when a
    /// later capability constructor fails. It is not a global service locator.
    /// </summary>
    public static void Run(AppDefinition definition,
        Action<IRuntimeClient, IStorageDatabase, ICollection<IDesktopModule>> createModules,
        Func<Action<Application>, Application> createApplication,
        Func<ValueTask>? disposeResources = null,
        Func<DesktopWindow, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null,
        Func<IStorageDatabase, IRuntimeSettingsStore>? runtimeSettings = null,
        Func<Task>? stopPlayback = null)
        => Run(AppDataPaths.Current, definition, createModules, createApplication, disposeResources, attachWindow, createPages, runtimeSettings, stopPlayback);

    // Isolated App-host tests use the same launcher without touching the user's data.
    internal static void Run(AppDataPaths paths, AppDefinition definition,
        Action<IRuntimeClient, IStorageDatabase, ICollection<IDesktopModule>> createModules,
        Func<Action<Application>, Application> createApplication,
        Func<ValueTask>? disposeResources = null,
        Func<DesktopWindow, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null,
        Func<IStorageDatabase, IRuntimeSettingsStore>? runtimeSettings = null,
        Func<Task>? stopPlayback = null)
    {
        var startup = new AppStartup();
        UserDataDirectory? userData = null;
        AppShutdown? ownedLifecycle = null;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            Application.Start(args =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                // The executable's compiled XAML root owns metadata for all
                // referenced view libraries; the launcher owns startup.
                _ = createApplication(Launch);
            });
        }
        finally
        {
            startup.Dispose();
            if (ownedLifecycle is null || ownedLifecycle.IsReleased) userData?.Dispose();
        }

        void Launch(Application application)
        {
        DesktopTypography.Install(application);
        var launchWindow = new DesktopWindow { Title = "meowfilm" };
        launchWindow.Resize(420, 220);
        if (launchWindow.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        var display = DisplayArea.GetFromWindowId(launchWindow.AppWindow.Id, DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        var size = launchWindow.AppWindow.Size;
        launchWindow.AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2));
        var label = new TextBlock
        {
            Text = "正在启动", Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 20
        };
        var retry = new Button { Content = "重试", Margin = new Thickness(12), Padding = new Thickness(24, 8, 24, 8), Visibility = Visibility.Collapsed };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label); stack.Children.Add(retry);
        launchWindow.Content = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 247, 249, 252)), Children = { stack } };
        var switched = false;
        var exitRequested = false;
        var allowLaunchClose = false;
        Task<StartupOutcome>? starting = null;
        launchWindow.Closing += (_, args) =>
        {
            if (switched || allowLaunchClose) return;
            args.Cancel = true;
            exitRequested = true;
            ExitStartup();
        };
        retry.Click += async (_, _) => { if (exitRequested) ExitStartup(); else await StartAsync(); };
        launchWindow.Activate();
        _ = StartAsync();

        async Task StartAsync()
        {
            retry.Visibility = Visibility.Collapsed; label.Text = "正在启动";
            starting = startup.StartAsync(StartCoreAsync);
            var outcome = await starting;
            if (exitRequested) return;
            if (outcome == StartupOutcome.RetryableFailure)
            {
                label.Text = "启动失败";
                retry.Visibility = Visibility.Visible;
            }
            else if (outcome == StartupOutcome.CleanupFailed)
            {
                label.Text = "启动失败，请重新打开应用";
                exitRequested = true;
                retry.Content = "退出";
                retry.Visibility = Visibility.Visible;
            }
        }

        void ExitStartup()
        {
            startup.Dispose();
            allowLaunchClose = true;
            launchWindow.Close();
            application.Exit();
        }

        async Task<StartupOutcome> StartCoreAsync(CancellationToken cancellationToken)
        {
            IStorageDatabase? store = null;
            AppRuntimeConnectionHost? runtime = null;
            ArtworkService? artwork = null;
            AppAppearance? appearance = null;
            AppMediaControlSettings? mediaControls = null;
            AppClosePreferences? closePreferences = null;
            var modules = new List<IDesktopModule>();
            IAsyncDisposable? windowAttachment = null;
            ShellWindow? shell = null;
            AppShutdown? shutdown = null;
            var shellClosed = false;
            var replacing = false;
            Task? exiting = null;
            AppShutdown Lifecycle()
            {
                // Cleanup must not be prevented by malformed assembly output.
                // Each successfully constructed object is retired exactly once.
                return ownedLifecycle = shutdown ??= new AppShutdown(
                    Enumerable.Distinct<IAsyncDisposable>(
                        modules.Cast<IAsyncDisposable>(), ReferenceEqualityComparer.Instance),
                [
                    new(() => windowAttachment?.DisposeAsync() ?? ValueTask.CompletedTask),
                    new(() => artwork?.DisposeAsync() ?? ValueTask.CompletedTask),
                    new(() => disposeResources?.Invoke() ?? ValueTask.CompletedTask),
                    new(() => runtime?.DisposeAsync() ?? ValueTask.CompletedTask, InterruptsPendingOperations: true),
                    new(() => store?.DisposeAsync() ?? ValueTask.CompletedTask)
                ], sharedParticipants: new IAsyncDisposable?[] { mediaControls, appearance, closePreferences }
                    .OfType<IAsyncDisposable>());
            }
            try
            {
                userData ??= new UserDataDirectory(paths.Documents);
                // A durable reset request is completed before any store, runtime
                // or cache owner can read the partially-cleared current root.
                await Task.Run(userData.Open, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(paths.Documents);
                store = new NativeDatabase(paths.Documents);
                await store.Ready;
                cancellationToken.ThrowIfCancellationRequested();
                var nativeSettings = runtimeSettings?.Invoke(store);
                runtime = new AppRuntimeConnectionHost(async token =>
                {
                    // Native pages and business owners never wait for this start:
                    // a failed runtime only makes script sources unavailable.
                    var root = Path.Combine(AppContext.BaseDirectory, "runtime");
                    var bundle = await Task.Run(() => RuntimeBundle.Verify(root, definition.Id), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return await NodeRuntime.StartAsync(Path.Combine(root, "node.exe"), bundle,
                        definition.RuntimeModules, paths.ScriptRuntime, token, ownerRevoked: token,
                        settingsStore: nativeSettings).ConfigureAwait(false);
                });
                cancellationToken.ThrowIfCancellationRequested();
                createModules(runtime, store, modules);
                appearance = new AppAppearance(store);
                closePreferences = new AppClosePreferences(store);
                await appearance.LoadAsync();
                await closePreferences.LoadAsync();
                mediaControls = new AppMediaControlSettings(store);
                await mediaControls.LoadAsync();
                ValidateModuleComposition(modules);
                await PrepareModulesAsync(modules, cancellationToken);
                // The shared runtime starts with the App; starting it executes no script.
                runtime.Start();
                var lifecycle = Lifecycle();
                var reset = new AppUserDataReset(userData.RequestReset, ReleaseForResetAsync,
                    () => Task.Run(userData.Clear), ReopenAsync);
                shell = new ShellWindow(modules, createPages?.Invoke(modules) ?? [],
                    () => replacing ? lifecycle.CloseAsync() : exiting ??= CloseProcessAsync(),
                    appearance, closePreferences, mediaControls: mediaControls,
                    resetUserData: reset.ResetAsync) { Title = "meowfilm" };
                artwork = new ArtworkService(readTrusted: async (reference, token) =>
                {
                    try { return await runtime.ReadImageAsync(reference, token).ConfigureAwait(false); }
                    catch (BridgeException error)
                    {
                        throw new ArtworkException(error.Code switch
                        {
                            "INVALID_REQUEST" => "ARTWORK_ADDRESS",
                            "RESOURCE_LIMIT" => "ARTWORK_LIMIT",
                            "INVALID_RESPONSE" or "UPSTREAM_FORMAT" => "ARTWORK_FORMAT",
                            "CLOSED" => "ARTWORK_CLOSED",
                            "TIMEOUT" => "ARTWORK_TIMEOUT",
                            "PERMISSION_DENIED" => "ARTWORK_DENIED",
                            _ => "ARTWORK_NETWORK"
                        });
                    }
                }, cacheDirectory: Path.Combine(paths.Caches, "artwork-v2"));
                ArtworkScope.SetLoader(shell.Root, artwork);
                windowAttachment = attachWindow?.Invoke(shell, modules);
                cancellationToken.ThrowIfCancellationRequested();
                shell.Closed += async (_, _) =>
                {
                    shellClosed = true;
                    if (!switched || replacing) return;
                    await shell.ShutdownCompletion;
                    application.Exit();
                };
                shell.Activate();
                switched = true;
                // Keep the startup/retry surface for an in-process data reset.
                launchWindow.Hide();
                return StartupOutcome.Started;

                Task ReleaseForResetAsync() => lifecycle.CloseAsync();

                async Task CloseProcessAsync()
                {
                    // A process exit has no replacement generation. Stop external
                    // execution/output, but do not drain every in-memory owner.
                    // The database seals admission and commits its accepted queue.
                    // Already acknowledged saves never depend on this callback.
                    Observe(() => stopPlayback?.Invoke() ?? Task.CompletedTask);
                    Observe(() => runtime.DisposeAsync().AsTask());
                    try { await store.DisposeAsync(); }
                    catch { System.Diagnostics.Trace.TraceError("DATABASE_RELEASE_FAILED"); }
                }

                static void Observe(Func<Task> stop)
                {
                    _ = ObserveAsync();
                    async Task ObserveAsync()
                    {
                        try { await stop(); }
                        catch { System.Diagnostics.Trace.TraceError("APP_STOP_FAILED"); }
                    }
                }

                async Task ReopenAsync()
                {
                    // Closing this retired shell must not shut down the dispatcher.
                    replacing = true;
                    try
                    {
                        await shell!.CloseAfterReleaseAsync();
                        if (!shellClosed) throw new InvalidOperationException("SHUTDOWN_FAILED");
                    }
                    finally { if (!shellClosed) replacing = false; }
                    if (exitRequested) return;
                    switched = false;
                    launchWindow.Activate();
                    startup.Dispose();
                    startup = new AppStartup();
                    starting = null;
                    ownedLifecycle = null;
                    retry.Content = "重试";
                    await StartAsync();
                }
            }
            catch
            {
                try
                {
                    await Lifecycle().CloseAsync();
                    if (shell is not null) await shell.CloseAfterReleaseAsync();
                }
                catch { return StartupOutcome.CleanupFailed; }
                return cancellationToken.IsCancellationRequested ? StartupOutcome.Cancelled : StartupOutcome.RetryableFailure;
            }
        }
    }

    }

    internal static async Task PrepareModulesAsync(IReadOnlyList<IDesktopModule> modules,
        CancellationToken cancellationToken = default)
    {
        foreach (var participant in modules.OfType<IDesktopStartupParticipant>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await participant.PrepareStartupAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void ValidateModuleComposition(IReadOnlyList<IDesktopModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count == 0 || modules.Any(module => module is null || string.IsNullOrWhiteSpace(module.Id)) ||
            modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != modules.Count)
            throw new InvalidOperationException("APP_COMPOSITION_INVALID");
    }

}
