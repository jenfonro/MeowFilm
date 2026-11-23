using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Core.Runtime;
using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Debug;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed record AppDefinition(string Id, IReadOnlyList<RuntimeModule> RuntimeModules);

public static class AppLauncher
{
    /// <summary>
    /// Incremental registration owns each completed constructor even when a
    /// later capability constructor fails. It is not a global service locator.
    /// </summary>
    public static void Run(AppDefinition definition,
        Action<IRuntimeClient, IStorageDatabase, IStorageDatabase, ICollection<IDesktopModule>> createModules,
        Func<Action<Application>, Application> createApplication,
        Func<ValueTask>? disposeResources = null,
        Func<DesktopWindow, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null,
        Func<IStorageDatabase, IRuntimeSettingsStore>? runtimeSettings = null,
        Func<Task>? stopPlayback = null,
        DebugLaunchOptions? debugOptions = null)
    {
        var paths = AppDataPaths.Resolve(debugOptions);
        var startup = new AppStartup();
        UserDataDirectory? userData = null;
        AppShutdown? ownedLifecycle = null;
        DebugSession? debugSession = null;
        DebugDiagnostics? debugDiagnostics = null;
        DebugControlHttpServer? debugServer = null;
        ShellWindow? activeShell = null;
        AppRuntimeConnectionHost? activeRuntime = null;
        NativeDatabase? activeDataStore = null;
        NativeDatabase? activeCacheStore = null;
        bool cleanDrain = true;
        string? lastFatalError = null;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            if (debugOptions is { Enabled: true })
            {
                try
                {
                    var debugRoot = debugOptions.DebugDir ?? AppDataPaths.DefaultDebugRoot(AppIdentity.StorageId);
                    debugSession = new DebugSession(debugOptions, debugRoot, paths.Documents);
                    DebugLog.Current = debugSession;
                    debugDiagnostics = new DebugDiagnostics(debugSession);
                    debugServer = new DebugControlHttpServer(
                        debugSession,
                        debugDiagnostics,
                        () => Volatile.Read(ref activeShell)?.DebugHost,
                        () => Volatile.Read(ref activeRuntime),
                        dbName => string.Equals(dbName, "cache", StringComparison.Ordinal)
                            ? Volatile.Read(ref activeCacheStore)
                            : Volatile.Read(ref activeDataStore));
                    DebugLog.Info(DebugCategories.Lifecycle, "app.launch", "debug.started", new
                    {
                        sessionId = debugSession.SessionId,
                        sessionDir = debugSession.SessionDir,
                        controlUrl = debugServer.ControlUrl,
                        dataRoot = paths.Documents,
                        options = debugSession.GetCurrentConfigSnapshot()
                    });
                }
                catch (Exception ex)
                {
                    cleanDrain = false;
                    lastFatalError = ex.ToString();
                    Console.Error.WriteLine($"Failed to initialize debug mode: {ex.Message}");
                    Environment.ExitCode = 2;
                    return;
                }
            }
            Application.Start(args =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                // The executable's compiled XAML root owns metadata for all
                // referenced view libraries; the launcher owns startup.
                var app = createApplication(Launch);
                if (debugDiagnostics is not null)
                {
                    app.UnhandledException += (_, e) =>
                    {
                        lastFatalError = e.Exception?.ToString() ?? e.Message;
                        if (e.Exception is not null)
                            debugDiagnostics.RecordException("unhandled", e.Exception, "WinUI.Application.UnhandledException");
                    };
                }
            });
        }
        catch (Exception ex)
        {
            cleanDrain = false;
            lastFatalError = ex.ToString();
            debugDiagnostics?.RecordException("unhandled", ex, "AppLauncher.Run");
            throw;
        }
        finally
        {
            startup.Dispose();
            if (ownedLifecycle is null || ownedLifecycle.IsReleased) userData?.Dispose();
            if (debugServer is not null)
            {
                try { debugServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            }
            if (debugSession is not null)
            {
                DebugLog.Info(DebugCategories.Lifecycle, "app.exit", "debug.stopped",
                    new { exitCode = Environment.ExitCode, cleanDrain, lastError = lastFatalError });
                debugSession.MarkExiting(Environment.ExitCode, cleanDrain, lastFatalError);
                DebugLog.Current = null;
                debugDiagnostics?.Dispose();
                debugSession.Dispose();
            }
        }

        void Launch(Application application)
        {
        bool quietDebug = debugOptions is { Enabled: true };
        DesktopTypography.Install(application);
        var launchWindow = new DesktopWindow { Title = AppIdentity.Name };
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
        launchWindow.Closing += (_, args) =>
        {
            if (switched || allowLaunchClose) return;
            args.Cancel = true;
            exitRequested = true;
            ExitStartup();
        };
        retry.Click += (_, _) => { if (exitRequested) ExitStartup(); else _ = StartAsync(); };
        if (!quietDebug) launchWindow.Activate();
        _ = StartAsync();

        async Task StartAsync()
        {
            try
            {
                retry.Visibility = Visibility.Collapsed; label.Text = "正在启动";
                var outcome = await startup.StartAsync(StartCoreAsync);
                if (exitRequested) return;
                if (outcome == StartupOutcome.RetryableFailure)
                {
                    label.Text = "启动失败";
                    retry.Visibility = Visibility.Visible;
                    if (quietDebug) launchWindow.ShowBackgroundInactive();
                }
                else if (outcome == StartupOutcome.CleanupFailed)
                {
                    label.Text = "启动失败，请重新打开应用";
                    exitRequested = true;
                    retry.Content = "退出";
                    retry.Visibility = Visibility.Visible;
                    if (quietDebug) launchWindow.ShowBackgroundInactive();
                }
            }
            catch (Exception ex)
            {
                DebugLog.Error(DebugCategories.Lifecycle, "app.launch", "startup.ui_error", error: ex);
                System.Diagnostics.Trace.TraceError("APP_LAUNCH_UI_FAILED");
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
            IStorageDatabase? dataStore = null;
            IStorageDatabase? cacheStore = null;
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
                    new(() => cacheStore?.DisposeAsync() ?? ValueTask.CompletedTask),
                    new(() => dataStore?.DisposeAsync() ?? ValueTask.CompletedTask)
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
                var dataDb = NativeDatabase.OpenData(paths.Documents);
                dataStore = dataDb;
                Volatile.Write(ref activeDataStore, dataDb);
                var cacheDb = NativeDatabase.OpenCache(paths.Documents);
                cacheStore = cacheDb;
                Volatile.Write(ref activeCacheStore, cacheDb);
                await Task.WhenAll(dataStore.Ready, cacheStore.Ready);
                DebugLog.Info(DebugCategories.Lifecycle, "storage", "database.ready",
                    new
                    {
                        dataDatabasePath = dataDb.DatabasePath,
                        cacheDatabasePath = cacheDb.DatabasePath,
                        sqliteVersion = dataDb.SQLiteVersion
                    });
                cancellationToken.ThrowIfCancellationRequested();
                var nativeSettings = runtimeSettings?.Invoke(dataStore);
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
                }, onRuntimeEndpointChanged: (url, childPid) => debugSession?.UpdateRuntimeEndpoint(url, childPid));
                Volatile.Write(ref activeRuntime, runtime);
                cancellationToken.ThrowIfCancellationRequested();
                createModules(runtime, dataStore, cacheStore, modules);
                appearance = new AppAppearance(dataStore);
                closePreferences = new AppClosePreferences(dataStore);
                await appearance.LoadAsync();
                await closePreferences.LoadAsync();
                mediaControls = new AppMediaControlSettings(dataStore);
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
                    resetUserData: reset.ResetAsync) { Title = AppIdentity.Name };
                Volatile.Write(ref activeShell, shell);
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
                }, cacheDirectory: Path.Combine(paths.Caches, "artwork-v1"));
                ArtworkScope.SetLoader(shell.Root, artwork);
                windowAttachment = attachWindow?.Invoke(shell, modules);
                cancellationToken.ThrowIfCancellationRequested();
                shell.Closed += (_, _) =>
                {
                    shellClosed = true;
                    if (!switched || replacing) return;
                    _ = ExitAfterShellClosedAsync(shell);
                };
                async Task ExitAfterShellClosedAsync(ShellWindow closedShell)
                {
                    try
                    {
                        await closedShell.ShutdownCompletion;
                        application.Exit();
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Error(DebugCategories.Lifecycle, "app.exit", "shell.exit_error", error: ex);
                        System.Diagnostics.Trace.TraceError("APP_EXIT_FAILED");
                    }
                }
                if (quietDebug) shell.ShowBackgroundInactive();
                else shell.Activate();
                switched = true;
                DebugLog.Info(DebugCategories.Lifecycle, "shell", "shell.activated",
                    new { modules = modules.Select(m => m.Id).ToArray(), quietBackground = quietDebug });
                // Keep the startup/retry surface for an in-process data reset.
                launchWindow.Hide();
                return StartupOutcome.Started;

                Task ReleaseForResetAsync() => lifecycle.CloseAsync();

                async Task CloseProcessAsync()
                {
                    // A process exit has no replacement generation. Stop external
                    // execution/output, but do not drain every in-memory owner.
                    // The databases seal admission and commit their accepted queues.
                    // Already acknowledged saves never depend on this callback.
                    Observe(() => stopPlayback?.Invoke() ?? Task.CompletedTask);
                    Observe(() => runtime.DisposeAsync().AsTask());
                    try { await cacheStore.DisposeAsync(); }
                    catch { System.Diagnostics.Trace.TraceError("CACHE_DATABASE_RELEASE_FAILED"); }
                    try { await dataStore.DisposeAsync(); }
                    catch { System.Diagnostics.Trace.TraceError("DATA_DATABASE_RELEASE_FAILED"); }
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
                    Volatile.Write(ref activeShell, null);
                    Volatile.Write(ref activeRuntime, null);
                    Volatile.Write(ref activeDataStore, null);
                    Volatile.Write(ref activeCacheStore, null);
                    DebugLog.Info(DebugCategories.Lifecycle, "reset", "app.reopen.start");
                    try
                    {
                        await shell!.CloseAfterReleaseAsync();
                        if (!shellClosed) throw new InvalidOperationException("SHUTDOWN_FAILED");
                    }
                    finally { if (!shellClosed) replacing = false; }
                    if (exitRequested) return;
                    switched = false;
                    if (!quietDebug) launchWindow.Activate();
                    startup.Dispose();
                    startup = new AppStartup();
                    ownedLifecycle = null;
                    retry.Content = "重试";
                    await StartAsync();
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref activeShell, null);
                Volatile.Write(ref activeRuntime, null);
                Volatile.Write(ref activeDataStore, null);
                Volatile.Write(ref activeCacheStore, null);
                DebugLog.Error(DebugCategories.Lifecycle, "app.launch", "startup.failed",
                    new { cancelled = cancellationToken.IsCancellationRequested }, error: ex);
                try
                {
                    await Lifecycle().CloseAsync();
                    if (shell is not null)
                    {
                        replacing = true;
                        await shell.CloseAfterReleaseAsync();
                    }
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
