using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Core.Runtime;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountIntegrationTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("production Native QR protocol WPF DPAPI verified save and restart remain one account scope", async () =>
        {
            string data;
            await using (var f = await Fixture.Create())
            {
                data = f.Data;
                f.Module.SelectedAccount = f.Module.Accounts[0];
                await f.Module.BeginAccountQrAsync();
                Check(f.Module.HasAccountQr && f.Module.AccountQrImage is { IsFrozen: true });
                Check(f.Module.AccountStatus == "等待扫码");
                await f.Module.CheckAccountQrAsync();
                Check(f.Module.AccountStatus == "等待扫码");
                await f.Configure(new { qrStatus = 802 });
                await f.Module.CheckAccountQrAsync();
                Check(f.Module.AccountStatus == "等待确认");
                await f.Configure(new { qrStatus = 803 });
                await f.Module.CheckAccountQrAsync();
                Check(!f.Module.HasAccountQr && f.Module.AccountQrImage is null);
                Check(f.Module.SelectedAccount!.State == "verified");
                Check(f.Module.SelectedAccount.UserId == "9007199254740997");
                Check(f.Module.AccountStatus == "会话验证成功");
                Check(f.Transport.RightsRequests == 1);
                var document = await f.Store.ReadAsync("music.accounts.v1");
                Check(document.Fingerprint.Exists);
                var accounts = AccountDocument.Decode(document);
                var details = accounts.GetDetails("wy");
                Check(details is not null && details.BelongsTo(accounts.Get("wy")) &&
                    details.Rights.Membership.Vip == false);
                foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
                    Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("SYNTHETIC_SESSION", StringComparison.Ordinal));
            }
            await using var reopened = await Fixture.Create(data);
            reopened.Module.SelectedAccount = reopened.Module.Accounts[0];
            await reopened.Module.ReadAccountsAsync();
            Check(reopened.Module.SelectedAccount!.State == "saved");
            Check(!reopened.Module.HasAccountQr);
            Check(reopened.Transport.Requests == 0);
            await reopened.Module.ValidateAccountAsync();
            Check(reopened.Module.SelectedAccount.State == "verified");
            Check(reopened.Transport.RightsRequests == 1);
            await reopened.Module.LogoutAccountAsync(reopened.Module.SelectedAccount!, confirmed: true);
            Check(reopened.Module.SelectedAccount.State == "none");
        });
        yield return Case("native QR cancel retires pixels and cannot later publish a session", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            await f.Module.BeginAccountQrAsync();
            Check(f.Module.CanCheckAccountQr && f.Module.CanCancelAccount);
            await f.Module.CancelAccountAsync();
            Check(!f.Module.HasAccountQr && !f.Module.CanCheckAccountQr);
            Check(f.Module.SelectedAccount!.State == "none");
            Check(f.Module.AccountStatus == "已取消");
            await f.Configure(new { qrStatus = 803 });
            await f.Module.CheckAccountQrAsync();
            Check(f.Module.SelectedAccount.State == "none");
        });
        yield return Case("platform switch cannot display or use another challenge", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            await f.Module.BeginAccountQrAsync();
            f.Module.SelectedAccount = f.Module.Accounts[2];
            Check(!f.Module.HasAccountQr && f.Module.AccountBusy &&
                !f.Module.CanBeginAccountQr && !f.Module.CanCheckAccountQr);
            var requests = f.Transport.Requests;
            await f.Module.CheckAccountQrAsync();
            Check(f.Transport.Requests == requests && !f.Module.HasAccountQr);
            await f.Module.CancelAccountAsync();
            Check(!f.Module.AccountBusy && f.Module.CanBeginAccountQr &&
                f.Module.SelectedAccount!.State == "none");
            f.Module.SelectedAccount = f.Module.Accounts[1];
            await f.Module.ImportAccountAsync("musicid=42; musickey=SYNTHETIC_QQ; encrypt_uin=SYNTHETIC_EUIN");
            Check(f.Module.SelectedAccount.State == "verified" && f.Module.SelectedAccount.Platform == "tx");
            Check(!f.Module.HasAccountQr);
            Check(f.Transport.RightsRequests == 1);
        });
        yield return Case("QR account failure is finite and does not save an anonymous profile", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            await f.Configure(new { qrStatus = 803, expired = true });
            await f.Module.BeginAccountQrAsync();
            await f.Module.CheckAccountQrAsync();
            Check(!f.Module.HasAccountQr);
            Check(f.Module.AccountStatus == "会话无效或已过期");
            await f.Module.ReadAccountsAsync();
            Check(f.Module.SelectedAccount!.State == "none");
        });
        yield return Case("local QR raster is bounded, frozen and includes white quiet zone", () =>
        {
            var image = MusicAccountQrImage.Create("https://music.163.com/login?codekey=SYNTHETIC_QR");
            Check(image.IsFrozen && image.PixelWidth == image.PixelHeight && image.PixelWidth is > 100 and <= 1024);
            var pixels = new byte[image.PixelWidth * image.PixelHeight];
            image.CopyPixels(pixels, image.PixelWidth, 0);
            Check(pixels.Take(image.PixelWidth * 16).All(pixel => pixel == 255));
            Check(pixels.Any(pixel => pixel == 0) && pixels.All(pixel => pixel is 0 or 255));
            foreach (var invalid in new[] { "https://attacker.invalid/login?codekey=x", "http://music.163.com/login?codekey=x",
                "https://music.163.com/login?codekey=x&redirect=x", "https://music.163.com:444/login?codekey=x",
                "file:///fixture", "https://music.163.com/login?codekey=" })
            {
                try { MusicAccountQrImage.Create(invalid); throw new InvalidOperationException("Expected unsafe QR rejection"); }
                catch (Exception error) when (error.Message == "INVALID_RESPONSE") { }
            }
            return Task.CompletedTask;
        });
        yield return Case("standalone account view resolves QR resources without starting network", async () =>
        {
            await using var f = await Fixture.Create();
            var view = new MusicAccountsView { DataContext = f.Module };
            view.Measure(new Size(600, 760)); view.Arrange(new Rect(0, 0, 600, 760)); view.UpdateLayout();
            var qr = (Image)view.FindName("AccountQrImage");
            Check(qr.Source is null);
            f.Module.SelectedAccount = f.Module.Accounts[0];
            await f.Module.BeginAccountQrAsync();
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            view.UpdateLayout();
            Check(qr.Source is not null && ReferenceEquals(qr.Source, f.Module.AccountQrImage));
            await f.Module.LeaveAccountQrAsync();
            Check(!f.Module.HasAccountQr);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Check(qr.Source is null);
        });
        yield return Case("switching SMS and QR modes suspends and resumes only visible QR polling", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = new Window { Content = view, Width = 620, Height = 1000,
                Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                ((Popup)view.FindName("LoginPopup")).IsOpen = true;
                await f.Module.BeginAccountQrAsync();
                int before = f.Transport.Requests;
                ((Button)view.FindName("SmsModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(2300);
                Check(f.Transport.Requests == before && f.Module.HasAccountQr,
                    $"SMS mode: requests {before}->{f.Transport.Requests}, QR {f.Module.HasAccountQr}, popup {((Popup)view.FindName("LoginPopup")).IsOpen}, platform {f.Module.SelectedAccount?.Platform}");
                ((Button)view.FindName("QrModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (f.Transport.Requests == before || f.Module.AccountBusy) await Task.Delay(20, deadline.Token);
                ((Button)view.FindName("SmsModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                int polled = f.Transport.Requests;
                await Task.Delay(2300);
                Check(polled > before && f.Transport.Requests == polled && !f.Module.SelectedAccount!.HasSession,
                    $"After QR resume: requests {before}->{polled}->{f.Transport.Requests}, state {f.Module.SelectedAccount?.State}, QR {f.Module.HasAccountQr}");
            }
            finally { window.Close(); }
        });
        yield return Case("native import cancellation before validation cannot persist late credentials", async () =>
        {
            await using var f = await Fixture.Create();
            f.Transport.HoldValidation = true; f.Transport.Arrived = Signal(); f.Transport.Release = Signal();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            var import = f.Module.ImportAccountAsync("MUSIC_U=SYNTHETIC_SESSION");
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Module.CancelAccountAsync(); await import;
            f.Transport.Release.TrySetResult();
            await f.Module.ReadAccountsAsync();
            Check(f.Module.SelectedAccount!.State == "none");
            Check(!(await f.Store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
        });
        yield return Case("admitted cancelled CAS returns unknown and explicit read waits for physical DPAPI receipt", async () =>
        {
            await using var f = await Fixture.Create();
            f.Store.Arrived = Signal(); f.Store.Release = Signal();
            using var cancel = new CancellationTokenSource();
            var import = f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_SESSION" }, cancel.Token);
            await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            var unknown = await import;
            Check(!unknown.GetProperty("ok").GetBoolean() && unknown.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
            var read = f.Application.ExecuteAsync("music.accounts.list");
            Check(!read.IsCompleted);
            f.Store.Release.TrySetResult();
            Check(AccountState(await read) == "saved" && f.Store.AccountWrites == 1);
            Check((await f.Store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
        });
        yield return Case("lost QR CAS acknowledgement requires explicit reconciliation without replay and survives restart", async () =>
        {
            string data;
            await using (var f = await Fixture.Create())
            {
                data = f.Data;
                f.Module.SelectedAccount = f.Module.Accounts[0];
                f.Transport.QrStatus = 803; f.Store.LoseReceipt = "music.accounts.v1";
                await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
                Check(f.Module.AccountStatus.Contains("未确认", StringComparison.Ordinal));
                var count = f.Transport.Requests;
                await f.Module.CheckAccountQrAsync(); Check(f.Transport.Requests == count);
                await f.Module.ReadAccountsAsync();
                Check(f.Module.SelectedAccount!.State == "saved" && f.Store.AccountWrites == 1);
                await f.Module.PrepareShutdownAsync();
                Check(f.Store.DisposeCalls == 0 && f.Transport.DisposeCalls == 0 && !f.Runtime.HasStarted);
            }
            await using var restarted = await Fixture.Create(data);
            Check(AccountState(await restarted.Application.ExecuteAsync("music.accounts.list")) == "saved");
            restarted.Store.LoseReceipt = "music.accounts.v1";
            var logout = await restarted.Application.ExecuteAsync("music.accounts.logout", new { platform = "wy" });
            Check(logout.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
            Check(AccountState(await restarted.Application.ExecuteAsync("music.accounts.list")) == "none");
        });
        yield return Case("actual catalog authentication failure revokes original lease and clears anonymous cache", async () =>
        {
            await using var f = await Fixture.Create();
            var args = new { platform = "wy" };
            Check((await f.Application.ExecuteAsync("music.catalog.home", args)).GetProperty("ok").GetBoolean());
            Check((await f.Application.ExecuteAsync("music.catalog.home", args)).GetProperty("ok").GetBoolean());
            Check(f.Transport.CatalogRequests == 1);
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_SESSION" })).GetProperty("ok").GetBoolean());
            f.Transport.CatalogStatus = 401;
            var failed = await f.Application.ExecuteAsync("music.catalog.home", args);
            Check(failed.GetProperty("code").GetString() == "AUTH_REQUIRED");
            Check(AccountState(await f.Application.ExecuteAsync("music.accounts.list")) == "revoked");
            f.Transport.CatalogStatus = 200;
            Check((await f.Application.ExecuteAsync("music.catalog.home", args)).GetProperty("ok").GetBoolean());
            Check(f.Transport.CatalogRequests == 3); // No resurrected pre-login guest cache.
        });
        yield return Case("late catalog authentication error never revokes replacement account generation", async () =>
        {
            await using var f = await Fixture.Create();
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_OLD" })).GetProperty("ok").GetBoolean());
            f.Transport.CatalogStatus = 401; f.Transport.HoldCatalog = true;
            f.Transport.Arrived = Signal(); f.Transport.Release = Signal();
            var old = f.Application.ExecuteAsync("music.catalog.home", new { platform = "wy" });
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_NEW" })).GetProperty("ok").GetBoolean());
            f.Transport.Release.TrySetResult();
            Check(!(await old).GetProperty("ok").GetBoolean());
            Check(AccountState(await f.Application.ExecuteAsync("music.accounts.list")) == "verified");
        });
        yield return Case("unknown account receipt fails reversible prepare until explicit physical DPAPI reconciliation", async () =>
        {
            await using var f = await Fixture.Create();
            f.Store.Arrived = Signal(); f.Store.Release = Signal();
            var import = f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_SESSION" });
            await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelledWait = new CancellationTokenSource();
            var prepare = f.Application.PrepareShutdownAsync();
            cancelledWait.Cancel();
            try { await f.Application.PrepareShutdownAsync(cancelledWait.Token); throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await Unknown(prepare);
            Check(f.Store.DisposeCalls == 0 && f.Transport.DisposeCalls == 0);
            Check((await f.Application.ExecuteAsync("music.queue.get")).GetProperty("code").GetString() == "CLOSED");
            await f.Application.CancelShutdownPreparationAsync();
            var read = f.Application.ExecuteAsync("music.accounts.list");
            Check(!read.IsCompleted); // Cancel preparation does not reconcile or replay.
            f.Store.Release.TrySetResult(); await import;
            Check(AccountState(await read) == "saved");
            await f.Application.PrepareShutdownAsync();
            Check((await f.Store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
            Check(f.Store.DisposeCalls == 0 && f.Runtime.DisposeCalls == 0 && !f.Runtime.HasStarted);
        });
        yield return Case("recovering preparation keeps real catalog and account owners usable without reimport", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0]; await f.Module.ImportAccountAsync("MUSIC_U=SYNTHETIC_SESSION");
            await f.Module.PrepareShutdownAsync(); await f.Module.CancelShutdownPreparationAsync();
            await WaitForEditable(f.Module);
            Check(f.Module.CanEdit && f.Transport.DisposeCalls == 0 && f.Store.DisposeCalls == 0);
            await f.Module.ValidateAccountAsync(); Check(f.Module.SelectedAccount!.State == "verified");
            Check((await f.Application.ExecuteAsync("music.catalog.home", new { platform = "wy" })).GetProperty("ok").GetBoolean());
            Check(!f.Runtime.HasStarted);
        });
        yield return Case("explicit abort waits physical unknown CAS and releases owned HTTP only after settlement", async () =>
        {
            await using var f = await Fixture.Create();
            f.Store.Arrived = Signal(); f.Store.Release = Signal();
            using var cancel = new CancellationTokenSource();
            var import = f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_SESSION" }, cancel.Token);
            await f.Store.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            Check((await import).GetProperty("code").GetString() == "COMMIT_UNKNOWN");
            var abort = f.Module.AbortAsync();
            await Task.Delay(30);
            Check(!abort.IsCompleted && f.Store.DisposeCalls == 0 && f.Transport.DisposeCalls == 0);
            f.Store.Release.TrySetResult(); await abort;
            Check(f.Store.DisposeCalls == 0 && f.Runtime.DisposeCalls == 0 && f.Transport.DisposeCalls == 1);
            Check((await f.Store.ReadAsync("music.accounts.v1")).Fingerprint.Exists);
        });
        yield return Case("production unknown library receipt fails prepare and final disposal fences without disposing shared store or runtime", async () =>
        {
            var f = await Fixture.Create();
            f.Store.LoseReceipt = "music.library.v1";
            try
            {
                var result = await f.Application.ExecuteAsync("music.queue.mode", new { mode = 2 });
                Check(result.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
                var prepare = f.Application.PrepareShutdownAsync();
                await Unknown(prepare);
                Check(ReferenceEquals(prepare, f.Application.PrepareShutdownAsync()));
                Check(f.Store.DisposeCalls == 0 && f.Runtime.DisposeCalls == 0 && f.Transport.DisposeCalls == 0);
                await Unknown(f.Application.DisposeAsync().AsTask());
                Check(f.Store.DisposeCalls == 0 && f.Runtime.DisposeCalls == 0 && f.Transport.DisposeCalls == 1);
                Check((await f.Store.ReadAsync("music.library.v1")).Fingerprint.Exists);
            }
            finally { await Unknown(f.DisposeAsync().AsTask()); }
        });
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string? AccountState(JsonElement result) => result.GetProperty("items")[0].GetProperty("state").GetString();
    private static async Task Unknown(Task task)
    {
        try { await task; throw new InvalidOperationException("Expected unknown receipt"); }
        catch (Exception error) when (error.Message == "COMMIT_UNKNOWN") { }
    }
    private static TestCase Case(string name, Func<Task> body) => new("music-account-chain: " + name, () => Sta(body));
    private static void Check(bool condition, string message = "Account integration assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitForEditable(MusicCapability module)
    {
        // Admit the posted resume refresh before observing actual idle state.
        await Dispatcher.Yield(DispatcherPriority.Background);
        var idle = Signal();
        PropertyChangedEventHandler changed = (_, _) => { if (module.CanEdit) idle.TrySetResult(); };
        module.PropertyChanged += changed;
        try
        {
            if (module.CanEdit) idle.TrySetResult();
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { module.PropertyChanged -= changed; }
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
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Check(thread.Join(TimeSpan.FromSeconds(5)));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly NativeMusicScriptTestRuntime Runtime = new();
        public readonly AccountTransport Transport = new();
        public ControlledStore Store { get; }
        public string Data { get; }
        public MusicNativeApplication Application { get; }
        public MusicCapability Module { get; }
        private Fixture(string data, ControlledStore store)
        {
            Data = data; Store = store;
            Application = new(store, Runtime, Transport);
            Module = new(Application, store, new Output(), new Dialogs());
        }
        public static async Task<Fixture> Create(string? data = null)
        {
            data ??= Path.Combine(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
                ?? throw new InvalidOperationException("Isolated data required"), "account-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            var store = new ControlledStore(new LocalDocumentStore(data, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            var result = new Fixture(data, store);
            try
            {
                await result.Module.InitializeAsync(default);
                Check(!result.Runtime.HasStarted && result.Transport.Requests == 0);
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }
        public Task Configure(object parameters)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            if (p.TryGetProperty("qrStatus", out var status)) Transport.QrStatus = status.GetInt32();
            if (p.TryGetProperty("expired", out var expired)) Transport.Expired = expired.GetBoolean();
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            Store.Release?.TrySetResult(); Transport.Release?.TrySetResult();
            try { await Module.DisposeAsync(); Check(!Runtime.HasStarted); }
            finally
            {
                await Runtime.DisposeAsync(); await Store.DisposeAsync();
            }
        }
    }
    private sealed class ControlledStore(LocalDocumentStore inner) : IDocumentStore
    {
        public string? LoseReceipt;
        public int Writes, AccountWrites, DisposeCalls;
        public TaskCompletionSource? Arrived, Release;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) => inner.ReadAsync(ns, token);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Writes);
            if (ns == "music.accounts.v1") Interlocked.Increment(ref AccountWrites);
            if (ns == "music.accounts.v1" && Release is { } hold)
            {
                Arrived!.TrySetResult();
                await hold.Task; // Simulate a physical CAS already admitted before caller cancellation.
                Release = null;
                token = CancellationToken.None; // The held physical write still settles in real DPAPI.
            }
            var saved = await inner.WriteAsync(ns, data, expected, token);
            if (LoseReceipt == ns) { LoseReceipt = null; throw new DocumentStoreException(DocumentStoreError.Storage); }
            return saved;
        }
        public async ValueTask DisposeAsync() { DisposeCalls++; await inner.DisposeAsync(); }
    }
    private sealed class AccountTransport : HttpMessageHandler
    {
        public int QrStatus = 801, CatalogStatus = 200, Requests, DisposeCalls, CatalogRequests, RightsRequests;
        public bool Expired;
        public TaskCompletionSource? Arrived, Release;
        public bool HoldValidation, HoldCatalog;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            HttpResponseMessage Json(object body, int status = 200) => new((HttpStatusCode)status)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (uri.Host == "interfacepc.music.163.com" &&
                uri.AbsolutePath.StartsWith("/eapi/login/qrcode/", StringComparison.Ordinal))
            {
                if (uri.AbsolutePath.EndsWith("/unikey", StringComparison.Ordinal)) return Json(new { code = 200, unikey = "SYNTHETIC_QR" });
                var result = Json(new { code = QrStatus });
                if (QrStatus == 803) result.Headers.TryAddWithoutValidation("Set-Cookie", "MUSIC_U=SYNTHETIC_SESSION; Domain=.music.163.com; Path=/; HttpOnly");
                return result;
            }
            if (uri.Host == "u.y.qq.com")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var operation = payload.RootElement.GetProperty("req_0");
                var method = operation.GetProperty("method").GetString();
                if (method == "GetLoginUserInfo")
                    return Json(new { code = 0, req_0 = new { code = 0, data = new { code = 0 } } });
                if (method == "vip_login_base")
                {
                    Check(operation.GetProperty("module").GetString() == "VipLogin.VipLoginInter" &&
                        payload.RootElement.GetProperty("comm").GetProperty("qq").GetString() == "42" &&
                        payload.RootElement.GetProperty("comm").GetProperty("authst").GetString() == "SYNTHETIC_QQ");
                    Interlocked.Increment(ref RightsRequests);
                    return Json(new { code = 0, req_0 = new { code = 0, data = new { svip = 0, identity = new { vip = 0 } } } });
                }
                Check(method == "GetHomepageHeader" &&
                    operation.GetProperty("module").GetString() == "music.UnifiedHomepage.UnifiedHomepageSrv");
                return Json(new { code = 0, req_0 = new { code = 0, data = new { Info = new { BaseInfo = new { EncryptedUin = "SYNTHETIC_EUIN", Name = "合成账号" } } } } });
            }
            Check(uri.Host is "music.163.com" or "interfacepc.music.163.com");
            if (uri.AbsolutePath.Contains("/nuser/account/get", StringComparison.Ordinal))
            {
                var expired = Expired;
                if (HoldValidation) { HoldValidation = false; Arrived!.TrySetResult(); await Release!.Task; }
                return Json(new { code = 200, profile = expired ? null : new { userId = "9007199254740997", nickname = "合成账号" } });
            }
            if (uri.AbsolutePath == "/weapi/music-vip-membership/client/vip/info")
            {
                Check(uri.Host == "music.163.com" && uri.Scheme == "https" &&
                    request.Headers.TryGetValues("Cookie", out var cookies) &&
                    cookies.Single().Contains("MUSIC_U=SYNTHETIC_", StringComparison.Ordinal));
                Interlocked.Increment(ref RightsRequests);
                return Json(new { code = 200, data = new { userId = "9007199254740997",
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), associator = new { expireTime = 0 },
                    musicPackage = new { expireTime = 0 }, redplus = new { expireTime = 0 } } });
            }
            Check(uri.Host == "interfacepc.music.163.com" && uri.AbsolutePath == "/eapi/link/page/rcmd/resource/show");
            Interlocked.Increment(ref CatalogRequests);
            var catalogStatus = CatalogStatus;
            if (HoldCatalog) { HoldCatalog = false; Arrived!.TrySetResult(); await Release!.Task; }
            return Json(new { code = 200, data = new { cursor = "0", hasMore = false, blocks = Array.Empty<object>() } }, catalogStatus);
        }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
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
