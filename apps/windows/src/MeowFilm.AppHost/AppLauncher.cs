using System.IO;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed record AppDefinition(string Id, IReadOnlyList<RuntimeModule> RuntimeModules,
    IReadOnlyList<string> NativeNamespaces,
    IReadOnlyList<string>? NativeCollections = null, IReadOnlyList<string>? ProtectedNamespaces = null);

public static class AppLauncher
{
    public static void Run(AppDefinition definition,
        Func<IRuntimeClient, IDocumentStore, IReadOnlyList<IDesktopModule>> createModules,
        Func<ValueTask>? disposeResources = null,
        Func<Window, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null)
        => Run(definition, (runtime, store, modules) =>
        {
            foreach (var module in createModules(runtime, store)) modules.Add(module);
        }, disposeResources, attachWindow, createPages);

    /// <summary>
    /// Incremental registration owns each completed constructor even when a
    /// later capability constructor fails. It is not a global service locator.
    /// </summary>
    public static void Run(AppDefinition definition,
        Action<IRuntimeClient, IDocumentStore, ICollection<IDesktopModule>> createModules,
        Func<ValueTask>? disposeResources = null,
        Func<Window, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null)
        => Run(AppDataPaths.Current, definition, createModules, disposeResources, attachWindow, createPages);

    // Isolated App-host tests use the same launcher without touching the user's data.
    internal static void Run(AppDataPaths paths, AppDefinition definition,
        Action<IRuntimeClient, IDocumentStore, ICollection<IDesktopModule>> createModules,
        Func<ValueTask>? disposeResources = null,
        Func<Window, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null,
        Func<IReadOnlyList<IDesktopModule>, IReadOnlyList<IDesktopPage>>? createPages = null)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        DesktopTypography.Install(application);
        var launchWindow = new DesktopWindow
        {
            Title = "meowfilm", Width = 420, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 249, 252))
        };
        var label = new TextBlock
        {
            Text = "正在启动", Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MeowFilm.Font.Size.SectionHeading");
        label.SetResourceReference(TextBlock.FontWeightProperty, "MeowFilm.Font.Weight.Strong");
        var retry = new Button { Content = "重试", Margin = new Thickness(12), Padding = new Thickness(24, 8, 24, 8), Visibility = Visibility.Collapsed };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label); stack.Children.Add(retry); launchWindow.Content = stack;
        var switched = false;
        var exitRequested = false;
        var allowLaunchClose = false;
        var exitWait = false;
        AppShutdown? ownedLifecycle = null;
        Task<StartupOutcome>? starting = null;
        var startup = new AppStartup();
        UserDataDirectory? userData = null;
        launchWindow.Closing += (_, args) =>
        {
            if (switched || allowLaunchClose) return;
            args.Cancel = true;
            exitRequested = true;
            _ = ExitStartupAsync();
        };
        retry.Click += async (_, _) => { if (exitRequested) await ExitStartupAsync(); else await StartAsync(); };
        application.SessionEnding += (_, _) => exitRequested = true;
        application.Startup += async (_, _) => { launchWindow.Show(); await StartAsync(); };
        try { application.Run(); }
        finally
        {
            startup.Dispose();
            // The process owns this lease throughout all generations. OS session
            // ending may still be draining owners; never release it ahead of them.
            if (ownedLifecycle is null || ownedLifecycle.IsReleased) userData?.Dispose();
        }

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
                label.Text = "启动清理失败";
                exitRequested = true;
                retry.Content = "重试清理";
                retry.Visibility = Visibility.Visible;
            }
        }

        async Task ExitStartupAsync()
        {
            if (exitWait) return;
            exitWait = true;
            retry.Visibility = Visibility.Collapsed;
            label.Text = "正在清理";
            try
            {
                if (!await startup.StopAsync() && starting is { IsCompleted: false })
                    throw new InvalidOperationException("SHUTDOWN_FAILED");
                // A cancelled startup is not proof of cleanup. The retained owner
                // remains reachable after a failed attempt and across explicit retries.
                if (starting is not null) await starting;
                if (ownedLifecycle is not null) await ownedLifecycle.ForceReleaseAsync();
                allowLaunchClose = true;
                launchWindow.Close();
                application.Shutdown();
            }
            catch
            {
                label.Text = "清理尚未完成";
                retry.Content = "重试清理";
                retry.Visibility = Visibility.Visible;
            }
            finally { exitWait = false; }
        }

        async Task<StartupOutcome> StartCoreAsync(CancellationToken cancellationToken)
        {
            LocalDocumentStore? store = null;
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
            AppShutdown Lifecycle()
            {
                // Cleanup must not be prevented by malformed assembly output.
                // Each successfully constructed object is retired exactly once.
                return ownedLifecycle = shutdown ??= new AppShutdown(
                    Enumerable.Distinct<IDesktopShutdownParticipant>(
                        modules.Cast<IDesktopShutdownParticipant>(), ReferenceEqualityComparer.Instance),
                [
                    new(() => windowAttachment?.DisposeAsync() ?? ValueTask.CompletedTask),
                    new(() => artwork?.DisposeAsync() ?? ValueTask.CompletedTask),
                    new(() => disposeResources?.Invoke() ?? ValueTask.CompletedTask),
                    new(() => runtime?.DisposeAsync() ?? ValueTask.CompletedTask, InterruptsPendingOperations: true),
                    new(() => store?.DisposeAsync() ?? ValueTask.CompletedTask)
                ], sharedParticipants: new IDesktopShutdownParticipant?[] { mediaControls, appearance, closePreferences }
                    .OfType<IDesktopShutdownParticipant>());
            }
            try
            {
                userData ??= new UserDataDirectory(paths.Documents, paths.Caches);
                // A durable reset request is completed before any store, runtime
                // or cache owner can read either partially-cleared root. Normal
                // admission leaves the optional cache directory unopened.
                await Task.Run(userData.Open, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(paths.Documents);
                store = new LocalDocumentStore(paths.Documents, definition.NativeNamespaces,
                    definition.NativeCollections, definition.ProtectedNamespaces);
                runtime = new AppRuntimeConnectionHost(async token =>
                {
                    // Native pages and business owners never wait for this start:
                    // a failed runtime only makes script sources unavailable.
                    var root = Path.Combine(AppContext.BaseDirectory, "runtime");
                    var bundle = await Task.Run(() => RuntimeBundle.Verify(root, definition.Id,
                        definition.RuntimeModules), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return await NodeRuntime.StartAsync(Path.Combine(root, "node.exe"), bundle,
                        definition.RuntimeModules, paths.ScriptRuntime, token, ownerRevoked: token).ConfigureAwait(false);
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
                    lifecycle.CloseAsync, lifecycle.ForceReleaseAsync, lifecycle.CancelShutdownPreparationAsync,
                    appearance, closePreferences, mediaControls: mediaControls,
                    resetUserData: reset.ResetAsync) { Title = "meowfilm" };
                artwork = new ArtworkService(readTrusted: async (reference, maximumBytes, token) =>
                {
                    try { return await runtime.ReadImageAsync(reference, maximumBytes, token).ConfigureAwait(false); }
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
                });
                ArtworkScope.SetLoader(shell, artwork);
                windowAttachment = attachWindow?.Invoke(shell, modules);
                cancellationToken.ThrowIfCancellationRequested();
                application.MainWindow = shell;
                SessionEndingCancelEventHandler sessionEnding = (_, _) => shell.PrepareForSessionEnding();
                application.SessionEnding += sessionEnding;
                shell.Closed += (_, _) =>
                {
                    shellClosed = true;
                    application.SessionEnding -= sessionEnding;
                    // User-requested closure awaited cleanup; session ending keeps the OS path.
                    // Never initiate dispatcher shutdown on a failed drain receipt.
                    if (switched && !replacing && lifecycle.IsReleased) application.Shutdown();
                };
                shell.Show();
                switched = true;
                // Keep the startup/retry surface for an in-process data reset.
                launchWindow.Hide();
                return StartupOutcome.Started;

                async Task ReleaseForResetAsync()
                {
                    // Revoke system media/hotkey input before any asynchronous
                    // runtime drain. The lifecycle also owns this participant.
                    var inputDrain = mediaControls.PrepareShutdownAsync();
                    _ = inputDrain.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    await lifecycle.ForceReleaseAsync();
                }

                async Task ReopenAsync()
                {
                    // Closing this retired shell must not shut down the dispatcher.
                    replacing = true;
                    try
                    {
                        await shell!.AbortAsync();
                        if (!shellClosed) throw new InvalidOperationException("SHUTDOWN_FAILED");
                    }
                    finally { if (!shellClosed) replacing = false; }
                    if (exitRequested) return;
                    switched = false;
                    application.MainWindow = launchWindow;
                    launchWindow.Show();
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
                    await Lifecycle().ForceReleaseAsync();
                    if (shell is not null) await shell.AbortAsync();
                }
                catch { return StartupOutcome.CleanupFailed; }
                return cancellationToken.IsCancellationRequested ? StartupOutcome.Cancelled : StartupOutcome.RetryableFailure;
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
