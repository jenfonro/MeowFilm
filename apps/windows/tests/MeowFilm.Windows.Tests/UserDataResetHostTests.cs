using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using System.Text.Json.Nodes;
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
        // A process owns one WinUI Application. The child runs the real launcher
        // without first creating the component test application.
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
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(parent);
            string external = Path.Combine(parent, "original.mp3");
            File.WriteAllText(external, "external original");
            var modules = new List<ProbeModule>();
            var shells = new List<ShellWindow>();
            var attachments = new List<Attachment>();
            NativeTestDatabase? cacheStore = null;
            int disposals = 0, pid = Environment.ProcessId;
            Exception? failure = null;
            var guard = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
            guard.Tick += (_, _) =>
            {
                failure ??= new InvalidOperationException("launcher reset did not settle");
                Application.Current.Exit();
            };
            guard.Start();
            try
            {
                AppLauncher.Run(paths,
                    new(AppIdentity.RuntimeId, AppIdentity.ScriptModules),
                    (runtime, store, owners) =>
                    {
                        Check(modules.All(module => module.Released), "old owners release before replacement construction");
                        Check(cacheStore is null, "old App-owned cache must release before replacement construction");
                        cacheStore = new NativeTestDatabase(store);
                        var module = new ProbeModule(store, runtime, cacheStore, paths.Documents);
                        modules.Add(module); owners.Add(module);
                    },
                    launch => new TestApplication(launch),
                    async () =>
                    {
                        if (cacheStore is not null) { await cacheStore.DisposeAsync(); cacheStore = null; }
                        disposals++;
                    },
                    (window, owners) =>
                    {
                        var shell = (ShellWindow)window;
                        var module = (ProbeModule)owners.Single();
                        shells.Add(shell);
                        int generation = shells.Count;
                        shell.Root.Loaded += async (_, _) =>
                        {
                            try
                            {
                                Check(Environment.ProcessId == pid && modules.Count == generation, "same process, new owners");
                                Check(shells.Take(generation - 1).All(previous => previous.CleanupCompleted && !previous.IsVisible),
                                    "old windows are closed before replacement becomes usable");
                                Check(disposals == generation - 1 &&
                                    attachments.Take(generation - 1).All(item => item.Disposals == 1), "one release per generation");
                                Check(!Directory.Exists(paths.Caches), "all old native caches were removed before replacement");
                                Check(await module.Store.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_home")) == 0,
                                    "New cache owner cannot reuse previous rows.");
                                Check(await module.Store.ReadAsync(db => db.Revision("reset-probe")) == StorageRevision.Empty,
                                    "Late writes cannot survive reset.");
                                Check(await module.Store.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_music_accounts")) == 0,
                                    "Previous account rows were removed.");
                                var library = await module.Native.ExecuteAsync("music.library.list", new { });
                                Check(library.GetProperty("ok").GetBoolean() && library.GetProperty("total").GetInt32() == 1,
                                    "new native library contains only the default favorites");
                                var accounts = await module.Native.ExecuteAsync("music.accounts.list", new { });
                                Check(accounts.GetProperty("ok").GetBoolean() &&
                                    accounts.GetProperty("items").EnumerateArray().All(item => item.GetProperty("state").GetString() == "none"),
                                    "new native account owner is signed out");
                                Check(!Directory.Exists(Path.Combine(root, "script-runtime")) &&
                                    !Directory.Exists(Path.Combine(root, "music-playback-cache")), "old runtime and playback files removed");
                                Click((Button)shell.Root.FindName("SettingsButton"));
                                await DesktopTestUi.DrainAsync();
                                var settings = Descendants(shell.Root).OfType<GeneralSettingsView>().Single();
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
                                await module.Store.WriteAsync(db =>
                                {
                                    AccountStorageRecords.Account.Save(db, new JsonObject { ["version"] = 2, ["generation"] = 1 },
                                        AccountStorageRecords.Account.Read(db, ["wy"]), ["wy"]);
                                    return true;
                                });
                                foreach (string directory in new[] { "script-runtime/volumes/fixture/tmp", "resources-v1/audio" })
                                {
                                    Directory.CreateDirectory(Path.Combine(root, directory));
                                    File.WriteAllText(Path.Combine(root, directory, "unknown.bin"), "synthetic private data");
                                }
                                Directory.CreateDirectory(Path.Combine(paths.Caches, "audio"));
                                File.WriteAllText(Path.Combine(paths.Caches, "audio", "unknown.bin"), "synthetic native audio");
                                ((StackPanel)settings.FindName("Areas")).Children.OfType<RadioButton>()
                                    .Single(tab => Equals(tab.Tag, "cache")).IsChecked = true;
                                Click((Button)settings.FindName("ClearUserDataButton"));
                                var dialog = (UserControl)shell.Root.FindName("ResetDialog");
                                Check(dialog.Visibility == Visibility.Visible);
                                Click((Button)dialog.FindName("ConfirmChoice"));
                            }
                            catch (Exception error)
                            {
                                failure = error;
                                try { await shell.AbortAsync(); }
                                catch { Application.Current.Exit(); }
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
                        module.Store.ReadAsync(db => db.Revision("reset-probe")).GetAwaiter().GetResult();
                        throw new InvalidOperationException("retired store admitted a read");
                    }
                    catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Closed) { }
                    try
                    {
                        module.Cache.ReadAsync(db => db.Revision(MusicHomeSnapshotStore.Namespace)).GetAwaiter().GetResult();
                        throw new InvalidOperationException("retired cache admitted a read");
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
        public IStorageDatabase Store { get; }
        public NativeTestDatabase Cache { get; }
        public MusicNativeApplication Native { get; }
        public MusicRecordTestStore Records { get; }
        public bool Released { get; private set; }
        public ProbeModule(IStorageDatabase store, IRuntimeClient runtime, NativeTestDatabase cache, string databaseRoot)
        {
            Store = store; Cache = cache; Records = MusicTestStores.Use(store, databaseRoot);
            Native = new MusicNativeApplication(store, Records, runtime);
        }
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
            await Store.WriteAsync(db =>
            {
                HomeStorageRecords.Home.Save(db, new JsonObject { ["platform"] = "wy", ["pages"] = new JsonArray() },
                    HomeStorageRecords.Home.Read(db, ["device"]), ["device"]);
                return db.CommitRevision("reset-probe", db.Revision("reset-probe"), Guid.NewGuid().ToString("D"));
            });
            await Records.DisposeAsync();
            Released = true;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Click(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Check(bool value, string message = "reset host assertion")
    { if (!value) throw new InvalidOperationException(message); }
}
