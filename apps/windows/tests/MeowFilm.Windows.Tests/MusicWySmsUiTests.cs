using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicWySmsUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native-app-same-owner-send-login-reopen-state-logout-no-node", Flow);
        yield return Case("real-WPF-explicit-send-submit-password-clear-and-phone-retained", Controls);
        yield return Case("visible-idle-and-countdown-never-send-or-poll", Idle);
        yield return Case("hide-send-unconfirmed-clears-input-no-commit-unknown", () => Hide("send"));
        yield return Case("hide-identity-cancels-no-late-login", () => Hide("status"));
        yield return Case("platform-switch-clears-sensitive-input-and-old-result", Switch);
        yield return Case("data-context-change-retires-old-visible-SMS", Context);
        yield return Case("unloaded-clears-input-and-cancels", Unload);
        yield return Case("minimize-clears-input-and-never-resends", Minimize);
        yield return Case("unknown-CAS-explicit-list-recovery-no-replay", Unknown);
        yield return Case("bad-code-retry-login-not-resend", BadCode);
        yield return Case("duplicate-clicks-single-flight-and-cooldown-after-cancel", SingleFlight);
        yield return Case("UI-input-validation-before-any-native-request", InvalidInput);
        yield return Case("WY-QR-replaces-SMS-without-late-publication", Qr);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.accounts.wy.sms.ui." + name, () => Sta(run));
    private static void Check(bool condition) => MusicNativeWySmsTests.Check(condition);
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
    private static Task Send(Fixture f) => f.Module.SendAccountSmsAsync(WySmsHandler.Phone);
    private static Task Login(Fixture f) => f.Module.LoginAccountSmsAsync(WySmsHandler.Phone, "86", WySmsHandler.Captcha);
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
        await f.Module.LogoutAccountAsync();
        Check(f.Module.SelectedAccount!.State == "none" && f.Transport.Calls == 3 && f.Runtime.Calls == 0);
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
        ((TextBox)view.FindName("SmsPhoneInput")).Text = WySmsHandler.Phone;
        ((PasswordBox)view.FindName("SmsCodeInput")).Password = WySmsHandler.Captcha;
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
            await MusicNativeWySmsTests.Until(() => f.Module.AccountStatus == "验证码已发送" && !f.Module.AccountBusy);
            Check(((TextBox)view.FindName("SmsPhoneInput")).Text == WySmsHandler.Phone);
            ((PasswordBox)view.FindName("SmsCodeInput")).Password = WySmsHandler.Captcha;
            Click(view, "SmsLoginButton");
            Check(((PasswordBox)view.FindName("SmsCodeInput")).Password == "");
            // The account row is published before the command releases its write
            // fence. This success case must settle before closing the test view;
            // closing while busy deliberately exercises cancellation instead.
            await MusicNativeWySmsTests.Until(() => f.Module.SelectedAccount!.State == "verified" && !f.Module.AccountBusy);
            Check(f.Module.AccountStatus == "会话验证成功" && f.Store.Writes == 1 && f.Transport.Calls == 3);
            Cleared(view); Check(f.Runtime.Calls == 0);
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
            window.Hide(); await MusicNativeWySmsTests.Until(() => !f.Module.HasAccountSms && !f.Module.AccountBusy);
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
            var operation = step == "send" ? Send(f) : Login(f);
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(4));
            window.Hide(); Cleared(view);
            await operation.WaitAsync(TimeSpan.FromSeconds(4)); f.Transport.Release.TrySetResult();
            await MusicNativeWySmsTests.Until(() => !f.Module.AccountBusy && !f.Module.HasAccountSms);
            await f.Module.ReadAccountsAsync();
            Check(f.Store.Writes == 0 && f.Module.SelectedAccount!.State == "none");
            if (step == "send") Check(!f.Module.CanSendAccountSms && f.Module.CanLoginAccountSms);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task Switch()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "status";
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
        f.Transport.HoldAt = "send";
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded); OpenSms(view); Fill(view);
            var send = Send(f); await f.Transport.Arrived.Task;
            view.DataContext = other.Module; Cleared(view);
            f.Transport.Release.TrySetResult(); await send;
            await MusicNativeWySmsTests.Until(() => !f.Module.AccountBusy && !f.Module.HasAccountSms);
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
            await MusicNativeWySmsTests.Until(() => !f.Module.HasAccountSms);
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
            await MusicNativeWySmsTests.Until(() => !f.Module.HasAccountSms);
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
        await using var f = await Fixture.Create(); f.Transport.BusinessCode = 502;
        await Login(f);
        Check(f.Module.AccountStatus == "验证码无效或已过期" && f.Module.CanLoginAccountSms && f.Store.Writes == 0);
        f.Transport.BusinessCode = 200; await Login(f);
        Check(f.Module.SelectedAccount!.State == "verified" && !f.Transport.Steps.Contains("send"));
    }
    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "send";
        var first = Send(f); await Send(f);
        await f.Transport.Arrived.Task; Check(f.Transport.Calls == 1);
        var cancel = f.Module.CancelAccountAsync(); f.Transport.Release.TrySetResult();
        await Task.WhenAll(first, cancel); Check(!f.Module.CanSendAccountSms && f.Module.CanLoginAccountSms && f.Store.Writes == 0);
        await Send(f); Check(f.Transport.Calls == 1);
    }
    private static async Task InvalidInput()
    {
        await using var f = await Fixture.Create();
        await f.Module.SendAccountSmsAsync("123");
        await f.Module.LoginAccountSmsAsync(WySmsHandler.Phone, "86", "123");
        Check(f.Transport.Calls == 0 && f.Module.CanSendAccountSms && !f.Module.HasAccountSms);
        f.Module.SelectedAccount = f.Module.Accounts[2]; await Send(f);
        Check(f.Transport.Calls == 0 && !f.Module.ShowAccountSms);
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
        internal readonly NoRuntime Runtime = new();
        internal readonly WySmsHandler Transport = new();
        internal readonly WySmsStore Store;
        internal readonly MusicCapability Module;
        private Fixture()
        {
            Store = new(new DpapiDocumentStore(MusicNativeWySmsTests.Root(), ["music.library.v1", "music.accounts.v1", "music.files.v1"]));
            Module = new(new MusicNativeApplication(Store, Runtime, Transport), Store, new Output(), new Dialogs());
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try { await f.Module.InitializeAsync(default); f.Module.SelectedAccount = f.Module.Accounts[0]; return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult(); Store.Release.TrySetResult();
            try { await Module.DisposeAsync(); Check(Runtime.Calls == 0); }
            finally { await Store.DisposeAsync(); }
        }
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("WY SMS must not start Node"); }
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
