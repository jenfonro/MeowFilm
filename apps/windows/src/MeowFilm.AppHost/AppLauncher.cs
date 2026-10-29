using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.AppHost;

public sealed record AppDefinition(string Id, IReadOnlyList<RuntimeModule> RuntimeModules,
    IReadOnlyList<string> NativeNamespaces, IReadOnlyList<string> BridgeNamespaces);

public static class AppLauncher
{
    public static void Run(AppDefinition definition,
        Func<IRuntimeClient, IProtectedDocumentStore, IReadOnlyList<IDesktopModule>> createModules,
        Func<ValueTask>? disposeResources = null,
        Func<Window, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null)
        => Run(definition, (runtime, store, modules) =>
        {
            foreach (var module in createModules(runtime, store)) modules.Add(module);
        }, disposeResources, attachWindow);

    /// <summary>
    /// Incremental registration owns each completed constructor even when a
    /// later capability constructor fails. It is not a global service locator.
    /// </summary>
    public static void Run(AppDefinition definition,
        Action<IRuntimeClient, IProtectedDocumentStore, ICollection<IDesktopModule>> createModules,
        Func<ValueTask>? disposeResources = null,
        Func<Window, IReadOnlyList<IDesktopModule>, IAsyncDisposable?>? attachWindow = null)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var launchWindow = new Window
        {
            Title = "meowfilm", Width = 420, Height = 220, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 249, 252))
        };
        var label = new TextBlock { Text = "正在启动", FontSize = 18, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
        var retry = new Button { Content = "重试", Margin = new Thickness(12), Padding = new Thickness(24, 8, 24, 8), Visibility = Visibility.Collapsed };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label); stack.Children.Add(retry); launchWindow.Content = stack;
        var switched = false;
        var exitRequested = false;
        var allowLaunchClose = false;
        var exitWait = false;
        AppShutdown? ownedLifecycle = null;
        Task<StartupOutcome>? starting = null;
        using var startup = new AppStartup();
        launchWindow.Closing += (_, args) =>
        {
            if (switched || allowLaunchClose) return;
            args.Cancel = true;
            exitRequested = true;
            _ = ExitStartupAsync();
        };
        retry.Click += async (_, _) => { if (exitRequested) await ExitStartupAsync(); else await StartAsync(); };
        application.Startup += async (_, _) => { launchWindow.Show(); await StartAsync(); };
        application.Run();

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
            DpapiDocumentStore? store = null;
            ProtectedDocumentBridge? documents = null;
            OnDemandRuntimeClient? runtime = null;
            ArtworkService? artwork = null;
            var modules = new List<IDesktopModule>();
            IAsyncDisposable? windowAttachment = null;
            ShellWindow? shell = null;
            AppShutdown? shutdown = null;
            IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>? platformMethods = null;
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
                    new(() => documents?.DisposeAsync() ?? ValueTask.CompletedTask),
                    new(() => store?.DisposeAsync() ?? ValueTask.CompletedTask)
                ]);
            }
            try
            {
                var paths = AppDataPaths.Current;
                Directory.CreateDirectory(paths.Documents);
                store = new DpapiDocumentStore(paths.Documents, definition.NativeNamespaces);
                documents = new ProtectedDocumentBridge(store, definition.BridgeNamespaces);
                runtime = new OnDemandRuntimeClient(async token =>
                {
                    // Only a real script operation needs a runtime payload.
                    // Native pages and business owners do not depend on Node
                    // installation, integrity checks or process startup.
                    var root = Path.Combine(AppContext.BaseDirectory, "runtime");
                    var bundle = await Task.Run(() => RuntimeBundle.Verify(root, definition.Id,
                        definition.RuntimeModules), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return await NodeRuntime.StartAsync(Path.Combine(root, "node.exe"), bundle,
                        definition.RuntimeModules, platformMethods ?? throw new BridgeException("INVALID_MESSAGE"), token).ConfigureAwait(false);
                });
                cancellationToken.ThrowIfCancellationRequested();
                createModules(runtime, store, modules);
                ValidateModuleComposition(modules);
                platformMethods = ComposeScriptCapabilities(documents.Methods, modules.OfType<IScriptPlatformCapabilities>());
                var lifecycle = Lifecycle();
                shell = new ShellWindow(modules, [],
                    lifecycle.CloseAsync, lifecycle.ForceReleaseAsync, lifecycle.CancelShutdownPreparationAsync) { Title = "meowfilm" };
                artwork = new ArtworkService();
                ArtworkScope.SetLoader(shell, artwork);
                windowAttachment = attachWindow?.Invoke(shell, modules);
                cancellationToken.ThrowIfCancellationRequested();
                application.MainWindow = shell;
                shell.Closed += (_, _) =>
                {
                    // Normal/forced shell closure already awaited actual cleanup.
                    // Never shut down the dispatcher on a failed drain receipt.
                    if (switched && lifecycle.IsReleased) application.Shutdown();
                };
                shell.Show();
                switched = true;
                launchWindow.Close();
                return StartupOutcome.Started;
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

    internal static void ValidateModuleComposition(IReadOnlyList<IDesktopModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        if (modules.Count == 0 || modules.Any(module => module is null || string.IsNullOrWhiteSpace(module.Id)) ||
            modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != modules.Count)
            throw new InvalidOperationException("APP_COMPOSITION_INVALID");
    }

    /// <summary>One immutable, explicitly assembled reverse-call table per App.</summary>
    public static IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> ComposeScriptCapabilities(
        IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> platform,
        IEnumerable<IScriptPlatformCapabilities> modules)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(modules);
        var methods = new Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(StringComparer.Ordinal);
        Add(platform);
        foreach (var module in modules)
        {
            if (module is null) throw new InvalidOperationException("SCRIPT_CAPABILITY_COLLISION");
            Add(module.ScriptCapabilities ?? throw new InvalidOperationException("SCRIPT_CAPABILITY_COLLISION"));
        }
        return new ReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>>(methods);

        void Add(IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> source)
        {
            foreach (var pair in source)
            {
                if (!BridgeCodec.ValidMethod(pair.Key) || !pair.Key.StartsWith("platform.", StringComparison.Ordinal) ||
                    pair.Value is null || methods.Count >= 64 || !methods.TryAdd(pair.Key, pair.Value))
                    throw new InvalidOperationException("SCRIPT_CAPABILITY_COLLISION");
            }
        }
    }
}
