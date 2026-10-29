using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicKgAccountUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native-account-owner-raster-login-logout-no-node", Login);
        yield return Case("native-local-raster-fixed-platform-address-only", Raster);
        yield return Case("visible-challenge-only-poll-hide-cancel-no-restart", Visibility);
        yield return Case("new-device-registration-hidden-seed-persists-no-QR-or-session", PendingRegistration);
        yield return Case("manual-and-visible-poll-single-flight-old-result-rejected", SingleFlight);
        yield return Case("platform-switch-no-old-login-publication", Switch);
        yield return Case("expired-terminal-removes-QR-no-session", Expired);
        yield return Case("unknown-session-save-explicit-read-no-replay", Unknown);
        yield return Case("loaded-unloaded-and-context-change-stop-polling", Unload);
        yield return Case("minimized-cancels-no-background-recreation", Minimized);
        yield return Case("explicit-confirm-selected-PNG-roundtrip-no-URL-metadata", Save);
        yield return Case("save-denied-or-picker-cancel-no-files", SaveCancelled);
        yield return Case("save-modal-epoch-change-rejects-retired-image", SaveStale);
        yield return Case("save-existing-file-no-overwrite-and-invalid-destination-denied", SaveBoundary);
        yield return Case("save-modal-query-admission-paused", SaveSingleFlight);
    }
    private static TestCase Case(string name, Func<Task> action) => new("music.accounts.kg.ui." + name, () => Sta(action));
    private static void Check(bool test) => MusicNativeKgAccountTests.Check(test);
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception e) { done.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Check(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private static async Task Login()
    {
        await using var f = await Fixture.Create();
        Check(f.Module.CanBeginAccountQr && !f.Module.CanSaveAccountQr);
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.HasAccountQr && f.Module.AccountQrImage is { IsFrozen: true } &&
            f.Module.AccountQrImage.Format == PixelFormats.Gray8 &&
            f.Module.AccountQrAccessibleName == "酷狗音乐登录二维码" && f.Module.CanSaveAccountQr);
        await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.SelectedAccount!.State == "verified" &&
            f.Module.SelectedAccount.UserId == "42" && f.Module.AccountStatus == "会话验证成功");
        await f.Module.LogoutAccountAsync(f.Module.SelectedAccount!, confirmed: true);
        Check(f.Module.SelectedAccount!.State == "none" && f.Transport.Registrations == 0 && f.Runtime.Calls == 0);
        Check((await KgAccountFixture.ReadDevice(f.Store))["seed"]!.GetValue<string>() == KgAccountFixture.Seed);
    }
    private static Task Raster()
    {
        string url = "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=" + KgAccountHandler.QrKey;
        var image = MusicAccountQrImage.Create(url, "kg");
        Check(image.IsFrozen && image.PixelWidth is > 100 and < 1024);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight];
        image.CopyPixels(pixels, image.PixelWidth, 0); Check(pixels.Contains((byte)0) && pixels.Contains((byte)255));
        foreach (var bad in new[] { url.Replace("h5.kugou.com", "attacker.invalid"), url + "&token=x", url + "#x",
            url.Replace("https:", "http:"), url.Replace(".com/", ".com:8443/"), url.Replace("qrcode=", "codekey="),
            url.Replace("h5.", "user@h5."), url + "%20", url + new string('a', 257) })
        {
            try { MusicAccountQrImage.Create(bad, "kg"); throw new InvalidOperationException("Expected QR rejection"); }
            catch (Exception e) when (e.Message == "INVALID_RESPONSE") { }
        }
        Check(MusicAccountQrImage.Create("https://music.163.com/login?codekey=synthetic").IsFrozen);
        try { MusicAccountQrImage.Create(url); throw new InvalidOperationException(); }
        catch (Exception e) when (e.Message == "INVALID_RESPONSE") { }
        return Task.CompletedTask;
    }
    private static Window Offscreen(object content) => new() { Content = content, Width = 500, Height = 700,
        Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
    private static void OpenLogin(MusicAccountsView view) =>
        ((Popup)view.FindName("LoginPopup")).IsOpen = true;
    private static async Task Visibility()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 1;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            await Task.Delay(2200); Check(f.Transport.Calls == 0);
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            await MusicNativeKgAccountTests.Until(() => f.Transport.Polls == 1);
            window.Hide();
            await MusicNativeKgAccountTests.Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            int calls = f.Transport.Calls;
            await Task.Delay(2200); window.Show(); await Task.Delay(2200);
            Check(f.Transport.Calls == calls && !f.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static async Task PendingRegistration()
    {
        await using var f = await Fixture.Create(seedDevice: false); f.Transport.HoldAt = "register";
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            var begin = f.Module.BeginAccountQrAsync(); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(f.Store.Writes == 1);
            window.Hide(); await begin.WaitAsync(TimeSpan.FromSeconds(4));
            Check(f.Module.AccountBusy);
            var prepare = f.Module.PrepareShutdownAsync();
            Check(!prepare.IsCompleted);
            f.Transport.Release.TrySetResult(); await prepare;
            Check(!f.Module.AccountBusy);
            Check(!f.Module.HasAccountQr && f.Module.Accounts[2].State != "verified" && f.Transport.Calls == 1);
            Check((await KgAccountFixture.ReadDevice(f.Store))["seed"]!.GetValue<string>().Length == 32);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "poll";
        await f.Module.BeginAccountQrAsync();
        var poll = f.Module.CheckAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(f.Transport.Calls == 2);
        var cancel = f.Module.CancelAccountAsync(); f.Transport.Release.TrySetResult();
        await Task.WhenAll(poll, cancel);
        Check(!f.Module.HasAccountQr && f.Store.Writes == 0);
    }
    private static async Task Switch()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "profile";
        await f.Module.BeginAccountQrAsync(); var poll = f.Module.CheckAccountQrAsync();
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
        f.Module.SelectedAccount = f.Module.Accounts[0];
        Check(!f.Module.HasAccountQr);
        f.Transport.Release.TrySetResult(); await poll;
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.Platform == "wy" && f.Module.Accounts[2].State == "none" && f.Store.Writes == 0);
    }
    private static async Task Expired()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 0;
        await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && !f.Module.CanSaveAccountQr && f.Module.AccountStatus == "二维码已过期");
    }
    private static async Task Unknown()
    {
        await using var f = await Fixture.Create(); f.Store.LoseReceiptAt = 1;
        await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.AccountStatus.Contains("未确认") &&
            f.Module.SelectedAccount!.State != "verified" && !f.Module.CanBeginAccountQr);
        int calls = f.Transport.Calls;
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.State == "saved" && f.Transport.Calls == calls);
    }
    private static async Task Unload()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 1;
        await using var other = await Fixture.Create();
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            view.DataContext = other.Module;
            await MusicNativeKgAccountTests.Until(() => !f.Module.HasAccountQr);
            await Task.Delay(2200); Check(other.Transport.Calls == 0);
            OpenLogin(view);
            await other.Module.BeginAccountQrAsync();
            window.Content = new Border();
            await MusicNativeKgAccountTests.Until(() => !other.Module.HasAccountQr);
            int calls = other.Transport.Calls; window.Content = view; await Task.Delay(2200);
            Check(!other.Module.HasAccountQr && other.Transport.Calls == calls);
        }
        finally { window.Close(); }
    }
    private static async Task Minimized()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 1;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            window.WindowState = WindowState.Minimized;
            await MusicNativeKgAccountTests.Until(() => !f.Module.HasAccountQr);
            int calls = f.Transport.Calls; window.WindowState = WindowState.Normal; await Task.Delay(2200);
            Check(!f.Module.HasAccountQr && f.Transport.Calls == calls);
        }
        finally { window.Close(); }
    }
    private static async Task Save()
    {
        await using var f = await Fixture.Create(); await f.Module.BeginAccountQrAsync();
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            var save = (Button)view.FindName("SaveQrButton");
            Check(save.IsEnabled); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(File.Exists(f.Dialogs.Path) && f.Module.AccountStatus == "二维码已保存" &&
                f.Dialogs.Confirmations == 1 && f.Dialogs.Selections == 1 && f.Transport.Calls == 1);
            using var stream = File.OpenRead(f.Dialogs.Path!);
            var png = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Check(png.Frames.Count == 1 && png.Frames[0].PixelWidth == f.Module.AccountQrImage!.PixelWidth);
            Check(png.Frames[0].Metadata is null || !((BitmapMetadata)png.Frames[0].Metadata).ContainsQuery("/tEXt"));
        }
        finally { window.Close(); }
    }
    private static async Task SaveCancelled()
    {
        await using var f = await Fixture.Create(); await f.Module.BeginAccountQrAsync();
        f.Dialogs.Consent = false; await f.Module.SaveAccountQrAsync();
        Check(!File.Exists(f.Dialogs.Path) && f.Dialogs.Selections == 0);
        f.Dialogs.Consent = true; string path = f.Dialogs.Path!; f.Dialogs.Path = null;
        await f.Module.SaveAccountQrAsync();
        Check(!File.Exists(path) && f.Dialogs.Selections == 1 && f.Module.HasAccountQr);
    }
    private static async Task SaveStale()
    {
        await using var f = await Fixture.Create(); await f.Module.BeginAccountQrAsync();
        f.Dialogs.OnSelect = () => f.Module.SelectedAccount = f.Module.Accounts[1];
        await f.Module.SaveAccountQrAsync();
        Check(!File.Exists(f.Dialogs.Path) && !f.Module.HasAccountQr);
    }
    private static async Task SaveBoundary()
    {
        await using var f = await Fixture.Create(); await f.Module.BeginAccountQrAsync();
        File.WriteAllText(f.Dialogs.Path!, "keep-existing");
        await f.Module.SaveAccountQrAsync();
        Check(File.ReadAllText(f.Dialogs.Path!) == "keep-existing" && f.Module.AccountStatus == "文件已存在");
        foreach (string bad in new[] { "relative.png", @"\\server.invalid\share\qr.png",
            f.Dialogs.Path + ":secret.png", Path.Combine(f.Root, "qr.txt") })
        {
            f.Dialogs.Path = bad; await f.Module.SaveAccountQrAsync();
            Check(f.Module.AccountStatus == "二维码保存失败");
        }
    }
    private static async Task SaveSingleFlight()
    {
        await using var f = await Fixture.Create(); await f.Module.BeginAccountQrAsync();
        f.Dialogs.OnSelect = () =>
        {
            Check(!f.Module.CanCheckAccountQr && !f.Module.CanBeginAccountQr && !f.Module.CanSaveAccountQr);
            Check(f.Module.CheckAccountQrAsync().IsCompletedSuccessfully);
        };
        await f.Module.SaveAccountQrAsync();
        Check(File.Exists(f.Dialogs.Path) && f.Transport.Calls == 1 && f.Module.CanCheckAccountQr);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = MusicNativeKgAccountTests.DataPath();
        internal readonly NoRuntime Runtime = new();
        internal readonly KgAccountHandler Transport = new();
        internal readonly KgAccountStore Store;
        internal readonly Dialogs Dialogs;
        internal readonly MusicCapability Module;
        private Fixture()
        {
            Store = new(new DpapiDocumentStore(Root, ["music.library.v1", "music.accounts.v1", "music.files.v1"]));
            Dialogs = new() { Path = System.IO.Path.Combine(Root, "explicit-qr.png") };
            Module = new(new MusicNativeApplication(Store, Runtime, Transport), Store, new Output(), Dialogs);
        }
        internal static async Task<Fixture> Create(bool seedDevice = true)
        {
            var f = new Fixture();
            try
            {
                if (seedDevice) await KgAccountFixture.SeedDevice(f.Store.Inner);
                await f.Module.InitializeAsync(default);
                f.Module.SelectedAccount = f.Module.Accounts[2]; return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult(); Store.Release.TrySetResult();
            try { await Module.DisposeAsync(); Check(Runtime.Calls == 0); }
            finally { await Store.DisposeAsync(); }
        }
    }
    private sealed class Dialogs : IMusicDialogs, IMusicAccountQrSaveDialog
    {
        internal bool Consent = true;
        internal string? Path;
        internal int Confirmations, Selections;
        internal Action? OnSelect;
        public bool Confirm(string message, string title) { Confirmations++; return Consent; }
        public string[]? SelectAudioFiles() => null;
        public string? SelectQrPngPath() { Selections++; OnSelect?.Invoke(); return Path; }
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Ordinary KG must not start Node"); }
    }
    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId => null;
        public string? Identity => null;
        public bool HasMedia => false;
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public int Volume { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token) => throw new InvalidOperationException();
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(double value) => Task.CompletedTask;
    }
}
