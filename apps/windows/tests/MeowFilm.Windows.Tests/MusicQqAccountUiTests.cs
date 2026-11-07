using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicQqAccountUiTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("native-owner-png-profile-save-readback-logout", Login);
        yield return Case("png-native-decode-bounds-pixels-only", Image);
        yield return Case("visible-user-challenge-only-poll-hide-cancel-no-background", Visibility);
        yield return Case("pending-generation-hidden-before-png-never-publishes", PendingHidden);
        yield return Case("manual-and-visible-query-single-flight", SingleFlight);
        yield return Case("platform-switch-fences-old-authorization-and-pixels", Switch);
        yield return Case("lost-save-receipt-explicit-reconciliation-not-false-login", Unknown);
        yield return Case("refused-terminal-clears-image", Refused);
        yield return Case("unloaded-cancels-and-context-replacement-cannot-reuse-challenge", Unload);
        yield return Case("minimized-cancels-and-restore-does-not-recreate-challenge", Minimized);
        yield return Case("datacontext-replacement-cancels-old-owner-without-polling-new-owner", ContextReplacement);
    }
    private static TestCase Case(string name, Func<Task> action) => new("music.accounts.qq.ui." + name, () => Sta(action));
    private static void Check(bool value) => MusicNativeQqAccountTests.Check(value);
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
        Check(f.Module.CanBeginAccountQr);
        await f.Module.BeginAccountQrAsync();
        Check(f.Module.HasAccountQr && f.Module.AccountQrImage is { IsFrozen: true, PixelWidth: 16 } &&
            f.Module.AccountQrAccessibleName == "QQ 音乐登录二维码");
        await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.SelectedAccount!.State == "verified" &&
            f.Module.SelectedAccount.UserId == "42" && f.Module.AccountStatus == "会话验证成功");
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.State == "verified");
        await f.Module.LogoutAccountAsync(f.Module.SelectedAccount!, confirmed: true);
        Check(f.Module.SelectedAccount.State == "none" && f.Runtime.Calls == 0);
    }
    private static Task Image()
    {
        string png = Convert.ToBase64String(QqAccountFixtureHandler.Image);
        var image = MusicAccountQrImage.DecodePng(png);
        Check(image.IsFrozen && image.Format == PixelFormats.Bgra32 && image.PixelWidth == 16);
        byte[] pixels = new byte[16 * 16 * 4]; image.CopyPixels(pixels, 64, 0);
        Check(pixels.Contains((byte)0) && pixels.Contains((byte)255));
        foreach (var text in new[] { "https://attacker.invalid/secret", "data:image/svg+xml,<svg/>", "not-base64",
            Convert.ToBase64String(QqAccountFixtureHandler.Png(width: 2048)),
            Convert.ToBase64String(QqAccountFixtureHandler.Png(inflated: 1024 * 1024)) })
        {
            try { MusicAccountQrImage.DecodePng(text); throw new InvalidOperationException("Expected image rejection"); }
            catch (Exception e) when (e.Message is "INVALID_RESPONSE" or "LIMIT") { }
        }
        return Task.CompletedTask;
    }
    private static async Task Visibility()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            await Task.Delay(2200); Check(f.Transport.Calls == 0); // No idle/background timer requests.
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            await Until(() => f.Transport.Polls == 1);
            window.Hide();
            await Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            int calls = f.Transport.Calls;
            await Task.Delay(2300); Check(f.Transport.Calls == calls);
            window.Show(); await Task.Delay(2200);
            Check(!f.Module.HasAccountQr && f.Transport.Calls == calls && f.Runtime.Calls == 0);
        }
        finally { window.Close(); }
    }
    private static async Task PendingHidden()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "ptqrshow";
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            var begin = f.Module.BeginAccountQrAsync();
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.Hide(); await begin.WaitAsync(TimeSpan.FromSeconds(3));
            f.Transport.Release.TrySetResult();
            await Until(() => !f.Module.AccountBusy);
            Check(!f.Module.HasAccountQr && f.Store.Writes == 0);
        }
        finally { f.Transport.Release.TrySetResult(); window.Close(); }
    }
    private static async Task SingleFlight()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "ptqrlogin";
        await f.Module.BeginAccountQrAsync();
        var first = f.Module.CheckAccountQrAsync();
        await f.Module.CheckAccountQrAsync(); // Admission is synchronous before command dispatch.
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(f.Transport.Polls == 0 && f.Transport.Calls == 2);
        var cancel = f.Module.CancelAccountAsync();
        f.Transport.Release.TrySetResult(); await Task.WhenAll(first, cancel);
        Check(!f.Module.HasAccountQr && f.Store.Writes == 0);
    }
    private static async Task Switch()
    {
        await using var f = await Fixture.Create(); f.Transport.HoldAt = "QQLogin";
        await f.Module.BeginAccountQrAsync();
        var first = f.Module.CheckAccountQrAsync();
        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Module.SelectedAccount = f.Module.Accounts[0];
        Check(!f.Module.HasAccountQr);
        f.Transport.Release.TrySetResult(); await first;
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.Platform == "wy" && f.Module.Accounts[1].State == "none" && f.Store.Writes == 0);
    }
    private static async Task Unknown()
    {
        await using var f = await Fixture.Create(); f.Store.LoseReceipt = true;
        await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.AccountStatus.Contains("未确认") && f.Module.SelectedAccount!.State != "verified");
        int calls = f.Transport.Calls;
        await f.Module.CheckAccountQrAsync(); Check(f.Transport.Calls == calls);
        await f.Module.ReadAccountsAsync();
        Check(f.Module.SelectedAccount!.State == "saved" && f.Store.Writes == 1);
    }
    private static async Task Refused()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 68;
        await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
        Check(!f.Module.HasAccountQr && f.Module.AccountStatus == "已拒绝登录" && f.Store.Writes == 0);
    }
    private static async Task Unload()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = f.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            window.Content = new Border();
            await Until(() => !f.Module.HasAccountQr);
            int calls = f.Transport.Calls;
            window.Content = view; await Task.Delay(2300);
            Check(f.Transport.Calls == calls && !f.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static async Task Minimized()
    {
        await using var f = await Fixture.Create(); f.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            await f.Module.BeginAccountQrAsync();
            window.WindowState = WindowState.Minimized;
            await Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            int calls = f.Transport.Calls;
            await Task.Delay(2200); window.WindowState = WindowState.Normal; await Task.Delay(2200);
            Check(f.Transport.Calls == calls && !f.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static async Task ContextReplacement()
    {
        await using var first = await Fixture.Create();
        await using var next = await Fixture.Create();
        first.Transport.Status = 66;
        var view = new MusicAccountsView { DataContext = first.Module }; var window = Offscreen(view);
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            OpenLogin(view);
            await first.Module.BeginAccountQrAsync();
            await next.Module.BeginAccountQrAsync();
            Check(next.Module.HasAccountQr);
            int nextCalls = next.Transport.Calls;
            view.DataContext = next.Module;
            await Until(() => !first.Module.HasAccountQr && !first.Module.AccountBusy);
            int calls = first.Transport.Calls;
            await Task.Delay(2300);
            Check(first.Transport.Calls == calls && next.Transport.Calls == nextCalls && next.Module.HasAccountQr);
        }
        finally { window.Close(); }
    }
    private static Window Offscreen(UIElement view) => new()
    {
        Content = view, Left = -32000, Top = -32000, Width = 500, Height = 700,
        ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
    };
    private static void OpenLogin(MusicAccountsView view) =>
        ((Popup)view.FindName("LoginPopup")).IsOpen = true;
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private sealed class NoRuntime : IRuntimeClient
    {
        internal int Calls;
        public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
            ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Native business must not upload script sources"); }
        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Ordinary account must not start Node"); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal NoRuntime Runtime = new();
        internal QqAccountFixtureHandler Transport = new();
        internal QqAccountStore Store;
        internal MusicNativeApplication Application;
        internal MusicCapability Module;
        private Fixture()
        {
            Store = new(new LocalDocumentStore(MusicNativeQqAccountTests.DataPath(), AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            Application = new(Store, Runtime, Transport);
            Module = new(Application, Store, new Output(), new Dialogs());
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try { await f.Module.InitializeAsync(default); f.Module.SelectedAccount = f.Module.Accounts[1]; return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult();
            try { await Module.DisposeAsync(); Check(Runtime.Calls == 0); }
            finally { await Store.DisposeAsync(); }
        }
    }
    private sealed class Dialogs : IMusicDialogs
    {
        public bool Confirm(string message, string title) => true;
        public string[]? SelectAudioFiles() => null;
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
