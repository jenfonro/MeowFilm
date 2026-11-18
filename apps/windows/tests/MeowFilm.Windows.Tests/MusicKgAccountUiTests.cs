using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Desktop;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicKgAccountUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native-account-owner-raster-login-logout-no-script", Login);
        yield return Case("native-local-raster-encodes-protocol-text-without-URI-admission", Raster);
        yield return Case("visible-challenge-only-poll-hide-cancel-no-restart", Visibility);
        yield return Case("pending-create-hidden-no-QR-or-session-physical-drain", PendingRegistration);
        yield return Case("manual-and-visible-poll-single-flight-old-result-rejected", SingleFlight);
        yield return Case("platform-switch-no-old-login-publication", Switch);
        yield return Case("expired-terminal-removes-QR-no-session", Expired);
        yield return Case("display-expiry-waits-for-canonical-terminal-poll", DisplayExpiry);
        yield return Case("throttled-poll-keeps-same-challenge-for-explicit-retry", Throttled);
        yield return Case("unknown-session-save-explicit-read-no-replay", Unknown);
        yield return Case("loaded-unloaded-and-context-change-stop-polling", Unload);
        yield return Case("minimized-cancels-no-background-recreation", Minimized);
        yield return Case("explicit-confirm-selected-PNG-roundtrip-no-URL-metadata", Save);
        yield return Case("saved-QR-notice-survives-internal-account-read", SavedNotice);
        yield return Case("save-denied-or-picker-cancel-no-files", SaveCancelled);
        yield return Case("save-modal-epoch-change-rejects-retired-image", SaveStale);
        yield return Case("save-existing-file-no-overwrite-and-invalid-destination-denied", SaveBoundary);
        yield return Case("save-modal-query-admission-paused", SaveSingleFlight);
    }
    private static TestCase Case(string name, Func<Task> action) => new("music.accounts.kg.ui." + name, () => Sta(action));
    private static void Check(bool condition,
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    { if (!condition) throw new InvalidOperationException($"KG account assertion at {member}:{line}: {expression}"); }
    private static async Task Until(Func<bool> predicate,
        [CallerArgumentExpression(nameof(predicate))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { while (!predicate()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException)
        { throw new TimeoutException($"KG account wait at {member}:{line}: {expression}"); }
    }
    private static async Task Sta(Func<Task> action)
    { await TestUiHost.InvokeAsync(action); }
    private static async Task Login()
    {
        await using var f = await Fixture.Create();
        Check(f.Module.CanBeginAccountQr && !f.Module.CanSaveAccountQr);
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.HasAccountQr && f.Module.AccountQrImage is { PixelWidth: > 0, PixelHeight: > 0 } &&
            f.Module.AccountQrAccessibleName == "酷狗音乐登录二维码" && f.Module.CanSaveAccountQr);
        await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.SelectedAccount!.State == "verified" &&
            f.Module.SelectedAccount.UserId == "42" && f.Module.AccountStatus == "会话验证成功");
        await f.Module.LogoutAccountAsync(f.Module.SelectedAccount!, confirmed: true);
        Check(f.Module.SelectedAccount!.State == "none" && f.Runtime.Calls.Count == 0);
    }
    private static Task Raster()
    {
        string url = "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=SYNTHETIC_QR";
        var image = MusicAccountQrImage.Create(url);
        Check(image.PixelWidth is > 100 and < 1024);
        byte[] pixels = MusicTestUi.Pixels(image); Check(pixels.Contains((byte)0) && pixels.Contains((byte)255));
        foreach (var address in new[] { url.Replace("h5.kugou.com", "attacker.invalid"), url + "&token=x", url + "#x",
            url.Replace("https:", "http:"), url.Replace(".com/", ".com:8443/"), url.Replace("qrcode=", "codekey="),
            url.Replace("h5.", "user@h5."), url + "%20", url + new string('a', 257), "file:///fixture", "relative-text" })
        {
            var encoded = MusicAccountQrImage.Create(address);
            Check(encoded.PixelWidth == encoded.PixelHeight && encoded.PixelWidth <= 1024);
        }
        Check(MusicAccountQrImage.Create("https://music.163.com/login?codekey=synthetic").PixelWidth > 0);
        return Task.CompletedTask;
    }
    private static DesktopWindow Offscreen(UIElement content) => MusicTestUi.Window(content, 500, 700);
    private static void OpenLogin(MusicAccountsView view) =>
        MusicTestUi.OpenAccountLogin(view);
    private static async Task Visibility()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 1;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await MusicTestUi.Drain();
            await Task.Delay(2200); Check(f.Transport.Calls == 0);
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            await Until(() => f.Transport.Polls == 1);
            window.Hide();
            await Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            int calls = f.Transport.Calls;
            await Task.Delay(2200); window.Show(); await Task.Delay(2200);
            Check(f.Transport.Calls == calls && !f.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static async Task PendingRegistration()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "qr.create";
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await MusicTestUi.Drain();
            OpenLogin(view);
            var begin = f.Module.BeginAccountQrAsync(); await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(f.Store.Writes == 0);
            window.Hide(); await begin.WaitAsync(TimeSpan.FromSeconds(4));
            Check(f.Module.AccountBusy);
            var prepare = f.Module.PrepareShutdownAsync();
            Check(!prepare.IsCompleted);
            f.Transport.Release.TrySetResult(); await prepare;
            Check(!f.Module.AccountBusy);
            Check(!f.Module.HasAccountQr && f.Module.Accounts[2].State != "verified" && f.Transport.Calls == 1);
            Check(f.Store.Writes == 0);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "qr.poll";
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
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "qr.poll";
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
    private static async Task DisplayExpiry()
    {
        await using var f = await Fixture.Create();
        f.Transport.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        f.Transport.Status = 1;
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.HasAccountQr && !f.Module.CanSaveAccountQr && f.Module.CanCheckAccountQr);
        await f.Module.CheckAccountQrAsync();
        Check(f.Transport.Polls == 1 && f.Module.HasAccountQr && f.Module.AccountStatus == "等待扫码");
        f.Transport.Status = 0;
        await f.Module.CheckAccountQrAsync();
        Check(f.Transport.Polls == 2 && !f.Module.HasAccountQr && !f.Module.CanSaveAccountQr &&
            f.Module.AccountStatus == "二维码已过期" && f.Store.Writes == 0);
    }
    private static async Task Throttled()
    {
        await using var f = await Fixture.Create();
        await f.Module.BeginAccountQrAsync();
        var image = f.Module.AccountQrImage;
        f.Transport.PollFailure = "RATE_LIMITED";
        await f.Module.CheckAccountQrAsync();
        Check(f.Module.HasAccountQr && ReferenceEquals(image, f.Module.AccountQrImage) && f.Module.CanCheckAccountQr &&
            f.Module.AccountStatus == "请求过于频繁，请稍后重试" && f.Store.Writes == 0 && f.Transport.Polls == 1);
        f.Transport.PollFailure = null;
        await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.SelectedAccount!.State == "verified" &&
            f.Transport.Polls == 2 && f.Store.Writes == 1);
    }
    private static async Task Unknown()
    {
        await using var f = await Fixture.Create(); f.Store.LoseReceipt = true;
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
            window.Show(); await MusicTestUi.Drain(); OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            view.DataContext = other.Module;
            await Until(() => !f.Module.HasAccountQr);
            await Task.Delay(2200); Check(other.Transport.Calls == 0);
            OpenLogin(view);
            await other.Module.BeginAccountQrAsync();
            window.Content = new Border();
            await Until(() => !other.Module.HasAccountQr);
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
            window.Show(); await MusicTestUi.Drain(); OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            window.WindowState = DesktopWindowState.Minimized;
            await Until(() => !f.Module.HasAccountQr);
            int calls = f.Transport.Calls; window.WindowState = DesktopWindowState.Normal; await Task.Delay(2200);
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
            window.Show(); await MusicTestUi.Drain();
            OpenLogin(view);
            var save = (Button)view.FindName("SaveQrButton");
            Check(save.IsEnabled); DesktopTestUi.Invoke(save);
            Check(File.Exists(f.Dialogs.Path) && f.Module.AccountStatus == "二维码已保存" &&
                f.Dialogs.Confirmations == 1 && f.Dialogs.Selections == 1 && f.Transport.Calls == 1);
            using var stream = File.OpenRead(f.Dialogs.Path!);
            var png = new System.Windows.Media.Imaging.PngBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Check(png.Frames.Count == 1 && png.Frames[0].PixelWidth == f.Module.AccountQrImage!.PixelWidth);
            Check(png.Frames[0].Metadata is null || !((System.Windows.Media.Imaging.BitmapMetadata)png.Frames[0].Metadata).ContainsQuery("/tEXt"));
        }
        finally { window.Close(); }
    }
    private static async Task SavedNotice()
    {
        await using var f = await Fixture.Create();
        await f.Module.BeginAccountQrAsync();
        await f.Module.SaveAccountQrAsync();
        Check(f.Module.AccountStatus == "二维码已保存");
        await f.Calls.CompleteInternalAccountReadAsync(f.Module, "tx");
        Check(f.Module.AccountStatus == "二维码已保存" && f.Module.HasAccountQr && f.Transport.Calls == 1);
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
        internal readonly string Root = TestWorkspace.DataPath("kg-account-ui", Guid.NewGuid().ToString("N"));
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly QrScenario Transport = new();
        internal readonly MusicAccountRuntimeFixture.StoreProbe Store;
        internal readonly Dialogs Dialogs;
        internal readonly MusicAccountFixtureApplication Calls;
        internal readonly MusicCapability Module;
        private Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new(new LocalDocumentStore(Root, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            Dialogs = new() { Path = System.IO.Path.Combine(Root, "explicit-qr.png") };
            Runtime.Platform.OnRequest = Transport.Read;
            Calls = new(new MusicNativeApplication(Store, MusicTestStores.For(Store), Runtime));
            Module = new(Calls, Store, new Output(), Dialogs);
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try
            {
                await f.Module.InitializeAsync(default);
                // Account fixtures stay on a local page instead of refreshing an unrelated home.
                await f.Module.NavigateAsync("favorites", default);
                f.Module.SelectedAccount = f.Module.Accounts[2]; return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult(); Store.Release.TrySetResult();
            try { await Module.DisposeAsync(); Check(Runtime.Calls.Count == 0); }
            finally { await Runtime.DisposeAsync(); await Store.DisposeAsync(); }
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
    private sealed class QrScenario
    {
        internal int Calls, Polls, Status = 4;
        internal long ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds();
        internal string? HoldAt, PollFailure;
        internal readonly TaskCompletionSource Arrived = MusicAccountRuntimeFixture.Signal(), Release = MusicAccountRuntimeFixture.Signal();
        internal async Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            Calls++;
            if (HoldAt == call.Operation) { Arrived.TrySetResult(); await Release.Task; }
            if (call.Operation == "qr.create") return MusicPlatformRuntimeFixture.Json(new {
                challengeId = call.Parameters.GetProperty("challengeId").GetString(),
                url = "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=SYNTHETIC_QR",
                imageBase64 = (string?)null,
                expiresAt = ExpiresAt });
            if (call.Operation == "qr.poll")
            {
                Polls++;
                if (PollFailure is not null) throw new MeowFilm.Capabilities.Platforms.MusicPlatformException(PollFailure);
                var account = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("kg").GetRawText())!.AsObject();
                account["cookie"] = "userid=42; token=SYNTHETIC_KG_TOKEN; KUGOU_API_MID=123456789; t1=SYNTHETIC_T1";
                return MusicPlatformRuntimeFixture.Json(new { status = Status switch { 1 => "waiting", 0 => "expired", _ => "authenticated" },
                    account = Status == 4 ? account : null });
            }
            throw new InvalidOperationException(call.Operation);
        }
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
