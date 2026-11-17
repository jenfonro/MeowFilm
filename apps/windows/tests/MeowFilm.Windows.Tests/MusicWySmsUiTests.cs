using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicWySmsUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native-app-same-owner-send-login-reopen-state-logout-no-script", Flow);
        yield return Case("real-WPF-explicit-send-submit-password-clear-and-phone-retained", Controls);
        yield return Case("visible-idle-and-countdown-never-send-or-poll", Idle);
        yield return Case("hide-send-unconfirmed-clears-input-no-commit-unknown", () => Hide("sms.send"));
        yield return Case("hide-identity-cancels-no-late-login", () => Hide("sms.login"));
        yield return Case("platform-switch-clears-sensitive-input-and-old-result", Switch);
        yield return Case("data-context-change-retires-old-visible-SMS", Context);
        yield return Case("unloaded-clears-input-and-cancels", Unload);
        yield return Case("minimize-clears-input-and-never-resends", Minimize);
        yield return Case("unknown-CAS-explicit-list-recovery-no-replay", Unknown);
        yield return Case("bad-code-retry-login-not-resend", BadCode);
        yield return Case("duplicate-clicks-single-flight-and-cooldown-after-cancel", SingleFlight);
        yield return Case("UI-consumable-strings-dispatch-without-length-gate", DirectInput);
        yield return Case("WY-QR-replaces-SMS-without-late-publication", Qr);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.accounts.wy.sms.ui." + name, () => Sta(run));
    private static void Check(bool condition,
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    { if (!condition) throw new InvalidOperationException($"WY SMS account assertion at {member}:{line}: {expression}"); }
    private static async Task Until(Func<bool> predicate,
        [CallerArgumentExpression(nameof(predicate))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try { while (!predicate()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException)
        { throw new TimeoutException($"WY SMS account wait at {member}:{line}: {expression}"); }
    }
    private static async Task Sta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(35));
        Check(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private static Task Send(Fixture f) => f.Module.SendAccountSmsAsync(MusicAccountRuntimeFixture.Phone);
    private static Task Login(Fixture f) => f.Module.LoginAccountSmsAsync(MusicAccountRuntimeFixture.Phone, "86", MusicAccountRuntimeFixture.Captcha);
    private static async Task Flow()
    {
        await using var f = await Fixture.Create();
        Check(f.Module.CanSendAccountSms && f.Module.CanLoginAccountSms && !f.Module.HasAccountSms && f.Transport.Calls == 0);
        await Send(f);
        Check(f.Module.AccountStatus == "验证码已发送" && f.Module.HasAccountSms && !f.Module.CanSendAccountSms &&
            f.Module.CanLoginAccountSms && f.Store.Writes == 0);
        await Login(f);
        Check(f.Module.AccountStatus == "会话验证成功" && f.Module.SelectedAccount!.State == "verified" &&
            f.Module.SelectedAccount.UserId == "42" && !f.Module.HasAccountSms && f.Store.Writes == 1);
        await f.Module.ReadAccountsAsync(); Check(f.Module.SelectedAccount!.State == "verified");
        await f.Module.LogoutAccountAsync(f.Module.SelectedAccount!, confirmed: true);
        Check(f.Module.SelectedAccount!.State == "none" && f.Transport.Calls == 2 && f.Runtime.Calls.Count == 0);
    }
    private static Window Offscreen(object content) => new() { Content = content, Width = 620, Height = 1000,
        Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
    private static void OpenSms(MusicAccountsView view)
    {
        ((Popup)view.FindName("LoginPopup")).IsOpen = true;
        ((Button)view.FindName("SmsModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Fill(MusicAccountsView view)
    {
        ((TextBox)view.FindName("SmsPhoneInput")).Text = MusicAccountRuntimeFixture.Phone;
        ((PasswordBox)view.FindName("SmsCodeInput")).Password = MusicAccountRuntimeFixture.Captcha;
    }
    private static void Cleared(MusicAccountsView view)
    {
        Check(((TextBox)view.FindName("SmsPhoneInput")).Text == "" && ((PasswordBox)view.FindName("SmsCodeInput")).Password == "");
    }
    private static void Click(MusicAccountsView view, string name) =>
        ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Controls()
    {
        await using var f = await Fixture.Create();
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view);
            Check(((PasswordBox)view.FindName("SmsCodeInput")).MaxLength == 8);
            Click(view, "SmsSendButton");
            Check(((PasswordBox)view.FindName("SmsCodeInput")).Password == "");
            await Until(() => f.Module.AccountStatus == "验证码已发送" && !f.Module.AccountBusy);
            await f.Calls.CompleteInternalAccountReadAsync(f.Module, "tx");
            Check(f.Module.AccountStatus == "验证码已发送" && f.Transport.Calls == 1);
            Check(((TextBox)view.FindName("SmsPhoneInput")).Text == MusicAccountRuntimeFixture.Phone);
            ((PasswordBox)view.FindName("SmsCodeInput")).Password = MusicAccountRuntimeFixture.Captcha;
            Click(view, "SmsLoginButton");
            Check(((PasswordBox)view.FindName("SmsCodeInput")).Password == "");
            // The account row is published before the command releases its write
            // fence. This success case must settle before closing the test view;
            // closing while busy deliberately exercises cancellation instead.
            await Until(() => f.Module.SelectedAccount!.State == "verified" && !f.Module.AccountBusy);
            Check(f.Module.AccountStatus == "会话验证成功" && f.Store.Writes == 1 && f.Transport.Calls == 2);
            Cleared(view); Check(f.Runtime.Calls.Count == 0);
        }
        finally { window.Close(); }
    }
    private static async Task Idle()
    {
        await using var f = await Fixture.Create();
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenSms(view);
            await Task.Delay(2200); Check(f.Transport.Calls == 0);
            await Send(f); await Task.Delay(2200);
            Check(f.Transport.Calls == 1 && f.Module.AccountSmsRetrySeconds is > 0 and <= 59);
            window.Hide(); await Until(() => !f.Module.HasAccountSms && !f.Module.AccountBusy);
            window.Show(); await Task.Delay(2200);
            Check(f.Transport.Calls == 1 && !f.Module.CanSendAccountSms);
        }
        finally { window.Close(); }
    }
    private static async Task Hide(string step)
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = step;
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view);
            var operation = step == "sms.send" ? Send(f) : Login(f);
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
            window.Hide(); Cleared(view);
            if (step == "sms.send")
                Check(!operation.IsCompleted && f.Transport.Calls == 1 && f.Store.Writes == 0);
            f.Transport.Release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(4));
            await Until(() => !f.Module.AccountBusy && !f.Module.HasAccountSms);
            await f.Module.ReadAccountsAsync();
            Check(f.Store.Writes == 0 && f.Module.SelectedAccount!.State == "none" && f.Transport.Calls == 1);
            if (step == "sms.send") Check(!f.Module.CanSendAccountSms && f.Module.CanLoginAccountSms);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task Switch()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "sms.login";
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view);
            var login = Login(f); await f.Transport.Arrived.Task;
            f.Module.SelectedAccount = f.Module.Accounts[1]; Cleared(view);
            Check(!f.Module.ShowAccountSms && !f.Module.CanLoginAccountSms);
            f.Transport.Release.TrySetResult(); await login; await f.Module.ReadAccountsAsync();
            Check(f.Module.Accounts[0].State == "none" && f.Store.Writes == 0);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task Context()
    {
        await using var f = await Fixture.Create(); await using var other = await Fixture.Create();
        f.Transport.HoldAt = "sms.send";
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view);
            var send = Send(f); await f.Transport.Arrived.Task;
            view.DataContext = other.Module; Cleared(view);
            f.Transport.Release.TrySetResult(); await send;
            await Until(() => !f.Module.AccountBusy && !f.Module.HasAccountSms);
            Check(other.Transport.Calls == 0 && f.Store.Writes == 0);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task Unload()
    {
        await using var f = await Fixture.Create();
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view); await Send(f);
            window.Content = null; await Dispatcher.Yield(DispatcherPriority.Loaded); Cleared(view);
            await Until(() => !f.Module.HasAccountSms && !f.Module.AccountBusy);
            Check(f.Transport.Calls == 1 && !f.Module.CanSendAccountSms);
        }
        finally { window.Close(); }
    }
    private static async Task Minimize()
    {
        await using var f = await Fixture.Create();
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view); await Send(f);
            window.WindowState = WindowState.Minimized; Cleared(view);
            await Until(() => !f.Module.HasAccountSms);
            window.WindowState = WindowState.Normal; await Task.Delay(2200);
            Check(f.Transport.Calls == 1 && !f.Module.CanSendAccountSms);
        }
        finally { window.Close(); }
    }
    private static async Task Unknown()
    {
        await using var f = await Fixture.Create(); f.Store.LoseReceipt = true;
        await Login(f);
        Check(f.Module.AccountStatus.Contains("未确认") && !f.Module.CanSendAccountSms && !f.Module.CanLoginAccountSms &&
            f.Module.SelectedAccount!.State != "verified");
        int calls = f.Transport.Calls;
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.State == "saved" && f.Module.CanLoginAccountSms && f.Transport.Calls == calls);
    }
    private static async Task BadCode()
    {
        await using var f = await Fixture.Create(); f.Transport.FailureCode = "INVALID_CODE";
        await Login(f);
        Check(f.Module.AccountStatus == "验证码无效或已过期" && f.Module.CanLoginAccountSms && f.Store.Writes == 0);
        f.Transport.FailureCode = null; await Login(f);
        Check(f.Module.SelectedAccount!.State == "verified" && !f.Transport.Steps.Contains("sms.send"));
    }
    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "sms.send";
        var first = Send(f); await Send(f);
        await f.Transport.Arrived.Task; Check(f.Transport.Calls == 1);
        var cancel = f.Module.CancelAccountAsync(); f.Transport.Release.TrySetResult();
        await Task.WhenAll(first, cancel); Check(!f.Module.CanSendAccountSms && f.Module.CanLoginAccountSms && f.Store.Writes == 0);
        await Send(f); Check(f.Transport.Calls == 1);
    }
    private static async Task DirectInput()
    {
        await using var f = await Fixture.Create();
        await f.Module.SendAccountSmsAsync("123");
        Check(f.Transport.Calls == 1 && f.Module.HasAccountSms && !f.Module.CanSendAccountSms &&
            f.Runtime.Platform.Requests.Single().Parameters.GetProperty("phone").GetString() == "123");
        await f.Module.LoginAccountSmsAsync(MusicAccountRuntimeFixture.Phone, "86", "123");
        var login = f.Runtime.Platform.Requests.Last();
        Check(f.Transport.Calls == 2 && login.Operation == "sms.login" &&
            login.Parameters.GetProperty("phone").GetString() == MusicAccountRuntimeFixture.Phone &&
            login.Parameters.GetProperty("captcha").GetString() == "123" &&
            f.Module.SelectedAccount!.State == "verified" && f.Store.Writes == 1 && !f.Module.HasAccountSms);
        f.Module.SelectedAccount = f.Module.Accounts[2]; await Send(f);
        Check(f.Transport.Calls == 2 && !f.Module.ShowAccountSms);
    }
    private static async Task Qr()
    {
        await using var f = await Fixture.Create(); await Send(f);
        await f.Module.BeginAccountQrAsync();
        Check(!f.Module.HasAccountSms && f.Module.HasAccountQr && f.Module.AccountQrImage is { IsFrozen: true });
        await f.Module.CancelAccountAsync();
        Check(!f.Module.HasAccountQr && !f.Module.CanSendAccountSms && f.Transport.Calls == 2);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly NativeMusicScriptTestRuntime Runtime = new();
        internal readonly SmsScenario Transport = new();
        internal readonly MusicAccountRuntimeFixture.StoreProbe Store;
        internal readonly MusicAccountFixtureApplication Calls;
        internal readonly MusicCapability Module;
        private Fixture()
        {
            string root = TestWorkspace.DataPath("wy-sms-ui", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Store = new(new LocalDocumentStore(root, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            Runtime.Platform.OnRequest = Transport.Read;
            Calls = new(new MusicNativeApplication(Store, MusicTestStores.For(Store), Runtime));
            Module = new(Calls, Store, new Output(), new Dialogs());
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try
            {
                await f.Module.InitializeAsync(default);
                // Account fixtures stay on a local page instead of refreshing an unrelated home.
                await f.Module.NavigateAsync("favorites", default);
                f.Module.SelectedAccount = f.Module.Accounts[0]; return f;
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
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class SmsScenario
    {
        internal int Calls;
        internal string? HoldAt, FailureCode;
        internal readonly List<string> Steps = [];
        internal readonly TaskCompletionSource Arrived = MusicAccountRuntimeFixture.Signal(), Release = MusicAccountRuntimeFixture.Signal();
        internal async Task<JsonElement> Read(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            Calls++; Steps.Add(call.Operation);
            if (HoldAt == call.Operation) { Arrived.TrySetResult(); await Release.Task; }
            if (FailureCode is not null) throw new MusicPlatformException(FailureCode);
            if (call.Operation == "sms.send") return MusicPlatformRuntimeFixture.Json(new { confirmed = true, retryAfterSeconds = 60 });
            if (call.Operation == "qr.create") return MusicPlatformRuntimeFixture.Json(new {
                challengeId = call.Parameters.GetProperty("challengeId").GetString(),
                url = "https://music.163.com/login?codekey=SYNTHETIC_QR", imageBase64 = (string?)null,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() });
            if (call.Operation == "sms.login")
            {
                var account = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("wy").GetRawText())!.AsObject();
                account["cookie"] = "MUSIC_U=SYNTHETIC_WY_SESSION; __csrf=SYNTHETIC_CSRF";
                return MusicPlatformRuntimeFixture.Json(account);
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
