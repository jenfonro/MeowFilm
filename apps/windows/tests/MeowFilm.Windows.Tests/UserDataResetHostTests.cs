using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class UserDataResetHostTests
{
    private const string CaseName = "User data reset host: launcher replaces two generations in the same process";
    public static IEnumerable<TestCase> Cases()
    {
        yield return new(CaseName, Run);
    }

    private static async Task Run()
    {
        // WPF allows only one Application per AppDomain. Keep this real launcher
        // test isolated from the runner's other independent STA component tests.
        if (Environment.GetEnvironmentVariable("MEOWFILM_RESET_LAUNCHER_CHILD") == "1")
        {
            await Launch();
            return;
        }
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = TestWorkspace.SourceRoot
        };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--case"); info.ArgumentList.Add(CaseName);
        info.Environment["MEOWFILM_RESET_LAUNCHER_CHILD"] = "1";
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(75)); }
        catch { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
        var text = await output;
        Check(child.ExitCode == 0 && text.Contains("TOTAL 1; PASSED 1; FAILED 0; SKIPPED 0", StringComparison.Ordinal),
            text + await errors);
    }

    private static async Task Launch()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string parent = TestWorkspace.DataPath("reset-host-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(parent, "aggregate");
            Directory.CreateDirectory(parent);
            string external = Path.Combine(parent, "original.mp3");
            File.WriteAllText(external, "external original");
            var modules = new List<ProbeModule>();
            var shells = new List<ShellWindow>();
            var attachments = new List<Attachment>();
            int disposals = 0, pid = Environment.ProcessId;
            Exception? failure = null;
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
            guard.Tick += (_, _) =>
            {
                failure ??= new InvalidOperationException("launcher reset did not settle");
                System.Windows.Application.Current.Shutdown();
            };
            guard.Start();
            try
            {
                AppLauncher.Run(new AppDataPaths(root),
                    new(AppIdentity.RuntimeId, AppIdentity.ScriptModules, AppIdentity.NativeNamespaces,
                        AppIdentity.NativeCollections, AppIdentity.ProtectedNamespaces),
                    (runtime, store, owners) =>
                    {
                        Check(modules.All(module => module.Released), "old owners release before replacement construction");
                        var module = new ProbeModule(store, runtime);
                        modules.Add(module); owners.Add(module);
                    },
                    () => { disposals++; return ValueTask.CompletedTask; },
                    (window, owners) =>
                    {
                        var shell = (ShellWindow)window;
                        var module = (ProbeModule)owners.Single();
                        shell.ShowActivated = false;
                        shells.Add(shell);
                        int generation = shells.Count;
                        shell.Loaded += async (_, _) =>
                        {
                            try
                            {
                                Check(Environment.ProcessId == pid && modules.Count == generation, "same process, new owners");
                                Check(shells.Take(generation - 1).All(previous => previous.CleanupCompleted && !previous.IsVisible),
                                    "old windows are closed before replacement becomes usable");
                                Check(disposals == generation - 1 &&
                                    attachments.Take(generation - 1).All(item => item.Disposals == 1), "one release per generation");
                                Check((await module.Store.ReadAsync("film.library.v1")).Data is null, "late old writes cannot survive reset");
                                Check((await module.Store.ReadAsync("music.accounts.v1")).Data is null, "old account bytes were removed");
                                var library = await module.Native.ExecuteAsync("music.library.list", new { });
                                Check(library.GetProperty("ok").GetBoolean() && library.GetProperty("total").GetInt32() == 1,
                                    "new native library contains only the default favorites");
                                var accounts = await module.Native.ExecuteAsync("music.accounts.list", new { });
                                Check(accounts.GetProperty("ok").GetBoolean() &&
                                    accounts.GetProperty("items").EnumerateArray().All(item => item.GetProperty("state").GetString() == "none"),
                                    "new native account owner is signed out");
                                Check(!Directory.Exists(Path.Combine(root, "script-runtime")) &&
                                    !Directory.Exists(Path.Combine(root, "music-playback-cache")), "old runtime and playback files removed");
                                Click((Button)shell.FindName("SettingsButton"));
                                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                                var settings = Descendants(shell).OfType<GeneralSettingsView>().Single();
                                var appearance = (IDesktopAppearance)settings.DataContext;
                                Check(appearance.ThemeColor == AppearanceColors.DefaultColor &&
                                    shell.MediaControls is { IsAvailable: true }, "fresh default settings are usable");
                                if (generation == 3)
                                {
                                    Check(File.ReadAllText(external) == "external original");
                                    await shell.AbortAsync();
                                    return;
                                }
                                var created = await module.Native.ExecuteAsync("music.library.create", new { name = "synthetic playlist" });
                                Check(created.GetProperty("ok").GetBoolean());
                                await appearance.SaveAsync("#ABCDEF");
                                Check(appearance.ThemeColor == "#ABCDEF");
                                await module.Store.WriteAsync("music.accounts.v1", "broken old credentials"u8.ToArray(),
                                    DocumentFingerprint.Missing);
                                foreach (string directory in new[] { "script-runtime", "music-playback-cache" })
                                {
                                    Directory.CreateDirectory(Path.Combine(root, directory));
                                    File.WriteAllText(Path.Combine(root, directory, "unknown.bin"), "synthetic private data");
                                }
                                ((StackPanel)settings.FindName("Areas")).Children.OfType<RadioButton>()
                                    .Single(tab => Equals(tab.Tag, "cache")).IsChecked = true;
                                Click((Button)settings.FindName("ClearUserDataButton"));
                                var dialog = (UserControl)shell.FindName("ResetDialog");
                                Check(dialog.Visibility == Visibility.Visible);
                                Click((Button)dialog.FindName("ConfirmChoice"));
                            }
                            catch (Exception error)
                            {
                                failure = error;
                                try { await shell.AbortAsync(); }
                                catch { System.Windows.Application.Current.Shutdown(); }
                            }
                        };
                        var attachment = new Attachment();
                        attachments.Add(attachment);
                        return attachment;
                    });
                guard.Stop();
                if (failure is not null) throw failure;
                Check(shells.Count == 3 && modules.All(module => module.Released) && disposals == 3 &&
                    attachments.All(item => item.Disposals == 1), "two complete resets followed by actual final exit");
                foreach (var module in modules)
                {
                    try
                    {
                        module.Store.ReadAsync("film.library.v1").GetAwaiter().GetResult();
                        throw new InvalidOperationException("retired store admitted a read");
                    }
                    catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Closed) { }
                }
                Directory.Delete(parent, recursive: true);
                done.SetResult();
            }
            catch (Exception error) { guard.Stop(); done.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Check(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private sealed class Attachment : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class ProbeModule : IDesktopModule
    {
        public IDocumentStore Store { get; }
        public MusicNativeApplication Native { get; }
        public bool Released { get; private set; }
        public ProbeModule(IDocumentStore store, IRuntimeClient runtime)
        { Store = store; Native = new MusicNativeApplication(store, runtime); }
        public string Id => "probe";
        public string Title => "测试";
        public string Version => "1";
        public ObservableCollection<NavigationItem> Navigation { get; } = [new("home", "首页", "")];
        public FrameworkElement View { get; } = new Border();
        public FrameworkElement SettingsView { get; } = new Border();
        public FrameworkElement? PlayerView => null;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task NavigateAsync(string route, CancellationToken token) => Task.CompletedTask;
        public Task SearchAsync(string query, CancellationToken token) => Task.CompletedTask;
        public string CaptureNavigationLocation() => "home";
        public Task RestoreNavigationAsync(string location, CancellationToken token) => Task.CompletedTask;
        public Task PrepareShutdownAsync() => Native.PrepareShutdownAsync();
        public Task CancelShutdownPreparationAsync() => Native.CancelShutdownPreparationAsync();
        public ValueTask DisposeAsync() => new(AbortAsync());
        public async Task AbortAsync()
        {
            if (Released) return;
            await Native.AbortAsync();
            var snapshot = await Store.ReadAsync("film.library.v1");
            await Store.WriteAsync("film.library.v1", "late old generation write"u8.ToArray(), snapshot.Fingerprint);
            Released = true;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Check(bool value, string message = "reset host assertion")
    { if (!value) throw new InvalidOperationException(message); }
}
