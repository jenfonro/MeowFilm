using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Windows.Threading;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Library;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Desktop;
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
                Check(f.Module.HasAccountQr && f.Module.AccountQrImage is { PixelWidth: > 0, PixelHeight: > 0 });
                Check(f.Module.AccountStatus == "等待扫码");
                await f.Calls.CompleteInternalAccountReadAsync(f.Module, "tx");
                Check(f.Module.AccountStatus == "等待扫码" && f.Module.HasAccountQr);
                await f.Module.CheckAccountQrAsync();
                Check(f.Module.AccountStatus == "等待扫码");
                await f.Configure(new { qrStatus = "confirming" });
                await f.Module.CheckAccountQrAsync();
                Check(f.Module.AccountStatus == "等待确认");
                await f.Configure(new { qrStatus = "authenticated" });
                await f.Module.CheckAccountQrAsync();
                Check(!f.Module.HasAccountQr && f.Module.AccountQrImage is null);
                Check(f.Module.SelectedAccount!.State == "verified");
                Check(f.Module.SelectedAccount.UserId == "9007199254740997");
                Check(f.Module.AccountStatus == "会话验证成功");
                Check(f.Transport.Verifications == 1);
                var document = await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace);
                Check(document.Fingerprint.Exists);
                var accounts = AccountDocument.Decode(document);
                var details = await f.Application.ReadDetailsAsync("wy");
                Check(details.Generation == accounts.Get("wy").Generation && details.Profile?.UserId == accounts.Get("wy").Profile?.UserId &&
                    details.Membership.Vip == false);
                string lockFile = Path.Combine(data, ".documents.lock");
                foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories)
                    .Where(file => !string.Equals(file, lockFile, StringComparison.OrdinalIgnoreCase)))
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
            Check(reopened.Transport.Verifications == 1);
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
            await f.Configure(new { qrStatus = "authenticated" });
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
            Check(f.Transport.Verifications == 1);
        });
        yield return Case("QR account failure is finite and does not save an anonymous profile", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            await f.Configure(new { qrStatus = "authenticated", expired = true });
            await f.Module.BeginAccountQrAsync();
            await f.Module.CheckAccountQrAsync();
            Check(!f.Module.HasAccountQr);
            Check(f.Module.AccountStatus == "会话无效或已过期");
            await f.Calls.CompleteInternalAccountReadAsync(f.Module, "tx");
            Check(f.Module.AccountStatus == "会话无效或已过期" && !f.Module.HasAccountQr);
            await f.Module.ReadAccountsAsync();
            Check(f.Module.SelectedAccount!.State == "none");
            Check(f.Module.AccountStatus == "");
        });
        yield return Case("local QR raster is bounded and includes an opaque white quiet zone", () =>
        {
            var image = MusicAccountQrImage.Create("https://music.163.com/login?codekey=SYNTHETIC_QR");
            Check(image.PixelWidth == image.PixelHeight && image.PixelWidth is > 100 and <= 1024);
            var pixels = MusicTestUi.Pixels(image);
            Check(pixels.Take(image.PixelWidth * 16 * 4).All(pixel => pixel == 255));
            Check(pixels.Any(pixel => pixel == 0) && pixels.All(pixel => pixel is 0 or 255));
            Check(pixels.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255));
            // Encoding protocol text never opens it. URI-use restrictions belong to actual loaders.
            foreach (var address in new[] { "https://attacker.invalid/login?codekey=x", "http://music.163.com/login?codekey=x",
                "https://music.163.com/login?codekey=x&redirect=x", "https://music.163.com:444/login?codekey=x",
                "file:///fixture", "https://music.163.com/login?codekey=", "relative-text", "https://user@qr.invalid/" })
            {
                var encoded = MusicAccountQrImage.Create(address);
                Check(encoded.PixelWidth == encoded.PixelHeight && encoded.PixelWidth <= 1024);
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
            await MusicTestUi.Drain();
            view.UpdateLayout();
            Check(qr.Source is not null && ReferenceEquals(qr.Source, f.Module.AccountQrImage));
            await f.Module.LeaveAccountQrAsync();
            Check(!f.Module.HasAccountQr);
            await MusicTestUi.Drain();
            Check(qr.Source is null);
        });
        yield return Case("switching SMS and QR modes suspends and resumes only visible QR polling", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = MusicTestUi.Window(view, 620, 1000);
            try
            {
                window.Show(); await MusicTestUi.Drain();
                MusicTestUi.OpenAccountLogin(view);
                await f.Module.BeginAccountQrAsync();
                int before = f.Transport.Requests;
                DesktopTestUi.Invoke(((Button)view.FindName("SmsModeButton")));
                await Task.Delay(2300);
                Check(f.Transport.Requests == before && f.Module.HasAccountQr);
                DesktopTestUi.Invoke(((Button)view.FindName("QrModeButton")));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (f.Transport.Requests == before || f.Module.AccountBusy) await Task.Delay(20, deadline.Token);
                DesktopTestUi.Invoke(((Button)view.FindName("SmsModeButton")));
                int polled = f.Transport.Requests;
                await Task.Delay(2300);
                Check(polled > before && f.Transport.Requests == polled && !f.Module.SelectedAccount!.HasSession);
            }
            finally { window.Close(); }
        });
        yield return Case("visible WY QR polling respects owner cooldown and stops when login closes", VisibleQrPolling);
        yield return Case("QR waiting states retain popup text without notices and terminal results notify once", QrNotices);
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
            Check(!(await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint.Exists);
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
            Check((await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint.Exists);
        });
        yield return Case("lost QR CAS acknowledgement requires explicit reconciliation without replay and survives restart", async () =>
        {
            string data;
            await using (var f = await Fixture.Create())
            {
                data = f.Data;
                f.Module.SelectedAccount = f.Module.Accounts[0];
                f.Transport.QrStatus = "authenticated"; f.Store.LoseReceipt = MusicAccountOwner.DocumentNamespace;
                await f.Module.BeginAccountQrAsync(); await f.Module.CheckAccountQrAsync();
                Check(f.Module.AccountStatus.Contains("未确认", StringComparison.Ordinal));
                var count = f.Transport.Requests;
                await f.Module.CheckAccountQrAsync(); Check(f.Transport.Requests == count);
                await f.Module.ReadAccountsAsync();
                Check(f.Module.SelectedAccount!.State == "saved" && f.Store.AccountWrites == 1);
                await f.Module.PrepareShutdownAsync();
                Check(f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0 && !f.Runtime.Calls.Any(call => !call.Method.StartsWith("music.platform.", StringComparison.Ordinal)));
            }
            await using var restarted = await Fixture.Create(data);
            Check(AccountState(await restarted.Application.ExecuteAsync("music.accounts.list")) == "saved");
            restarted.Store.LoseReceipt = MusicAccountOwner.DocumentNamespace;
            var logout = await restarted.Application.ExecuteAsync("music.accounts.logout", new { platform = "wy" });
            Check(logout.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
            Check(AccountState(await restarted.Application.ExecuteAsync("music.accounts.list")) == "none");
        });
        yield return Case("actual catalog authentication failure revokes original lease and keeps guest access isolated", async () =>
        {
            await using var f = await Fixture.Create(nativeOnly: true);
            var args = new { platform = "wy" };
            Check((await f.Application.ExecuteAsync("music.catalog.home", args)).GetProperty("ok").GetBoolean(),
                "initial anonymous catalog request must succeed");
            Check((await f.Application.ExecuteAsync("music.catalog.home", args)).GetProperty("ok").GetBoolean(),
                "repeated anonymous catalog request must succeed");
            Check(f.Transport.CatalogRequests == 2, $"canonical anonymous reads: {f.Transport.CatalogRequests} requests");
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_SESSION" })).GetProperty("ok").GetBoolean(),
                "account import must establish the lease under test");
            f.Transport.CatalogFailure = "AUTH_REQUIRED";
            var failed = await f.Application.ExecuteAsync("music.catalog.home", args);
            string? failureCode = failed.GetProperty("code").GetString();
            Check(!failed.GetProperty("ok").GetBoolean() && failureCode == "AUTH_REQUIRED",
                $"catalog authentication failure must remain AUTH_REQUIRED: {failureCode}");
            Check(AccountState(await f.Application.ExecuteAsync("music.accounts.list")) == "revoked",
                "catalog authentication failure must revoke the original lease");
            Check(f.Transport.CatalogRequests == 3, $"authenticated catalog rejection: {f.Transport.CatalogRequests} requests");
            f.Transport.CatalogFailure = null;
            Check((await f.Application.ExecuteAsync("music.catalog.home", args)).GetProperty("ok").GetBoolean(),
                "anonymous catalog remains available after account revocation");
            Check(f.Transport.CatalogRequests == 4,
                $"revoked lookup must not resurrect pre-login guest cache: {f.Transport.CatalogRequests} requests");
        });
        yield return Case("late catalog authentication error never revokes replacement account generation", async () =>
        {
            await using var f = await Fixture.Create(nativeOnly: true);
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_OLD" })).GetProperty("ok").GetBoolean());
            f.Transport.CatalogFailure = "AUTH_REQUIRED"; f.Transport.HoldCatalog = true;
            f.Transport.Arrived = Signal(); f.Transport.Release = Signal();
            var old = f.Application.ExecuteAsync("music.catalog.home", new { platform = "wy" });
            await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_NEW" })).GetProperty("ok").GetBoolean());
            f.Transport.Release.TrySetResult();
            Check(!(await old).GetProperty("ok").GetBoolean());
            Check(AccountState(await f.Application.ExecuteAsync("music.accounts.list")) == "verified");
        });
        yield return Case("details presentation keeps current profile on ordinary refusal verification and upstream unknown", DetailsDisplayFailure);
        yield return Case("VIP sign and banner ordinary errors retain current account without republishing", VipReadFailures);
        yield return Case("VIP rejected notSent and unknown receipts never invent an account logout or retry", VipWriteFailures);
        yield return Case("confirmed VIP sign survives failed optional readback with its real authentication meaning", VipReadbackFailure);
        yield return Case("VIP sign and banner explicit authentication rejects only current account", VipReadAuthentication);
        yield return Case("VIP rejected and notSent authentication receipts revoke without durable logout", VipWriteAuthentication);
        yield return Case("late VIP authentication rejection cannot revoke replacement account", VipLateAuthentication);
        yield return Case("VIP shared read keeps independent subscribers without cancelling or signing", VipSharedRead);
        yield return Case("VIP admitted sign settles after caller cancellation and is never replayed", VipAdmittedCancellation);
        yield return Case("VIP unknown receipt survives temporary account document failure and recovery", VipUnknownAccountRecovery);
        yield return Case("VIP application preparation waits for physical sign and resume retains its confirmed receipt", VipPreparedSign);
        yield return Case("VIP typed reads obey application close admission and physical preparation and disposal", VipTypedDrain);
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
            Check(f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
            Check((await f.Application.ExecuteAsync("music.queue.get")).GetProperty("code").GetString() == "CLOSED");
            await f.Application.CancelShutdownPreparationAsync();
            var read = f.Application.ExecuteAsync("music.accounts.list");
            Check(!read.IsCompleted); // Cancel preparation does not reconcile or replay.
            f.Store.Release.TrySetResult(); await import;
            Check(AccountState(await read) == "saved");
            await f.Application.PrepareShutdownAsync();
            Check((await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint.Exists);
            Check(f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0 && !f.Runtime.Calls.Any(call => !call.Method.StartsWith("music.platform.", StringComparison.Ordinal)));
        });
        yield return Case("recovering preparation keeps real catalog and account owners usable without reimport", async () =>
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0]; await f.Module.ImportAccountAsync("MUSIC_U=SYNTHETIC_SESSION");
            await f.Module.PrepareShutdownAsync(); await f.Module.CancelShutdownPreparationAsync();
            await WaitForEditable(f.Module);
            Check(f.Module.CanEdit && f.Runtime.Disposals == 0 && f.Store.DisposeCalls == 0);
            await f.Module.ValidateAccountAsync(); Check(f.Module.SelectedAccount!.State == "verified");
            Check((await f.Application.ExecuteAsync("music.catalog.home", new { platform = "wy" })).GetProperty("ok").GetBoolean());
            Check(!f.Runtime.Calls.Any(call => !call.Method.StartsWith("music.platform.", StringComparison.Ordinal)));
        });
        yield return Case("explicit abort waits physical unknown CAS and releases native client only after settlement", async () =>
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
            Check(!abort.IsCompleted && f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
            f.Store.Release.TrySetResult(); await abort;
            Check(f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
            Check((await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint.Exists);
        });
        yield return Case("production unknown library receipt fails prepare and final disposal fences without disposing shared store or runtime", async () =>
        {
            var f = await Fixture.Create();
            MusicTestStores.For(f.Store).Failure = 2;
            try
            {
                var result = await f.Application.ExecuteAsync("music.queue.mode", new { mode = 2 });
                Check(result.GetProperty("code").GetString() == "COMMIT_UNKNOWN");
                var prepare = f.Application.PrepareShutdownAsync();
                await Unknown(prepare);
                Check(ReferenceEquals(prepare, f.Application.PrepareShutdownAsync()));
                Check(f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
                await Unknown(f.Application.DisposeAsync().AsTask());
                Check(f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
                Check((await MusicTestStores.For(f.Store).ReadAsync("local")).Revision.Generation > 0);
            }
            finally { await Unknown(f.DisposeAsync().AsTask()); }
        });
    }
    private static async Task VisibleQrPolling()
    {
        await using var f = await Fixture.Create();
        f.Module.SelectedAccount = f.Module.Accounts[0];
        f.Transport.LimitQrPolling = true;
        var view = new MusicAccountsView { DataContext = f.Module };
        var window = MusicTestUi.Window(view, 620, 1000);
        var popup = (ContentDialog)view.FindName("LoginPopup");
        try
        {
            window.Show(); await MusicTestUi.Drain();
            MusicTestUi.OpenAccountLogin(view);
            await f.Module.BeginAccountQrAsync();
            await MusicTestUi.Drain();
            Check(MusicTestUi.IsDialogOpen(popup) && DesktopVisual.IsVisible((Image)view.FindName("AccountQrImage")));
            await Until(() => f.Transport.QrPollTimes.Count >= 2 && !f.Module.AccountBusy);
            var polls = f.Transport.QrPollTimes.ToArray();
            Check(polls.Length >= 2 && Volatile.Read(ref f.Transport.QrRateLimits) == 0 &&
                f.Module.HasAccountQr && f.Module.AccountStatus == "等待扫码");
            Check(polls.Zip(polls.Skip(1), (before, after) => Stopwatch.GetElapsedTime(before, after))
                .All(interval => interval >= TimeSpan.FromSeconds(3)));
            int stopped = f.Transport.QrPollTimes.Count;
            popup.Hide();
            await Until(() => !f.Module.HasAccountQr && !f.Module.AccountBusy);
            await MusicTestUi.Drain();
            await Task.Delay(TimeSpan.FromMilliseconds(3300));
            Check(f.Transport.QrPollTimes.Count == stopped && Volatile.Read(ref f.Transport.QrRateLimits) == 0);
        }
        finally { window.Close(); await f.Module.LeaveAccountQrAsync(); }
    }
    private static async Task QrNotices()
    {
        foreach (bool expired in new[] { false, true })
        {
            await using var f = await Fixture.Create();
            f.Module.SelectedAccount = f.Module.Accounts[0];
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = MusicTestUi.Window(view, 620, 1000);
            var popup = (ContentDialog)view.FindName("LoginPopup");
            var qrMode = (Button)view.FindName("QrModeButton");
            var smsMode = (Button)view.FindName("SmsModeButton");
            var qrText = ((StackPanel)view.FindName("QrPanel")).Children.OfType<TextBlock>().Single();
            var notices = new List<DesktopNoticeEventArgs>();
            NoticeOutlet.SetReceiver(DesktopTestUi.WindowRoot(window), notice =>
            { if (notice.Message.Length > 0) notices.Add(notice); });
            try
            {
                window.Show(); await MusicTestUi.Drain();
                MusicTestUi.OpenAccountLogin(view);
                DesktopTestUi.Invoke(smsMode);
                await MusicTestUi.Drain();
                await f.Module.BeginAccountQrAsync();
                await CheckVisibleStatus("等待扫码");
                foreach (string state in new[] { "waiting", "waiting", "confirming", "confirming", "waiting", "confirming" })
                {
                    // SMS mode has stopped the view's automatic loop before manual admission.
                    await Until(() => !f.Module.AccountBusy);
                    int before = f.Transport.QrPollTimes.Count;
                    string previous = f.Module.AccountStatus;
                    await f.Configure(new { qrStatus = state });
                    f.Transport.HoldQrPoll = true; f.Transport.Arrived = Signal(); f.Transport.Release = Signal();
                    var poll = f.Module.CheckAccountQrAsync();
                    try
                    {
                        await f.Transport.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await CheckVisibleStatus(previous);
                    }
                    finally
                    {
                        f.Transport.Release.TrySetResult();
                        await poll.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    await CheckVisibleStatus(state == "waiting" ? "等待扫码" : "等待确认");
                    Check(f.Transport.QrPollTimes.Count == before + 1 && f.Module.HasAccountQr);
                }
                await Until(() => !f.Module.AccountBusy);
                await f.Configure(new { qrStatus = "authenticated", expired });
                int terminalPolls = f.Transport.QrPollTimes.Count;
                await f.Module.CheckAccountQrAsync();
                await MusicTestUi.Drain();
                string expected = expired ? "会话无效或已过期" : "会话验证成功";
                Check(!f.Module.HasAccountQr && f.Module.AccountStatus == expected &&
                    f.Module.SelectedAccount!.State == (expired ? "none" : "verified"));
                Check(notices.Count == 1 && notices[0].Message == expected && DesktopVisual.Contains(view, notices[0].Outlet));
                var accountOutlet = notices[0].Outlet;
                await f.Calls.CompleteInternalAccountReadAsync(f.Module, "tx");
                await f.Module.CheckAccountQrAsync();
                await MusicTestUi.Drain();
                Check(f.Module.AccountStatus == expected);
                Check(f.Transport.QrPollTimes.Count == terminalPolls + 1);
                Check(notices.Count(notice => ReferenceEquals(notice.Outlet, accountOutlet)) == 1);
            }
            finally
            {
                f.Transport.Release?.TrySetResult();
                window.Close(); await f.Module.LeaveAccountQrAsync();
            }

            async Task CheckVisibleStatus(string expected)
            {
                DesktopTestUi.Invoke(qrMode);
                try
                {
                    await MusicTestUi.Drain();
                    Check(MusicTestUi.IsDialogOpen(popup) && DesktopVisual.IsVisible(qrText) && qrText.Text == expected &&
                        f.Module.AccountStatus == expected && notices.Count == 0);
                }
                finally
                {
                    DesktopTestUi.Invoke(smsMode);
                    await MusicTestUi.Drain();
                }
            }
        }
    }
    private static async Task DetailsDisplayFailure()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_DISPLAY" })).GetProperty("ok").GetBoolean());
        var known = await f.Application.ReadDetailsAsync("wy");
        await using var model = new MusicAccountDetailsViewModel(f.Application);
        model.SelectAccount(new("wy", "网易云音乐", known.State, known.Profile!.DisplayName, known.Profile.UserId, known.Generation));
        await model.ActivateAsync();
        Check(model.HasProfile && model.HasSession && model.DisplayName == known.Profile.DisplayName && model.CanRefresh);
        int writes = f.Store.AccountWrites;
        var fingerprint = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
        int observations = 0; model.Observed += _ => observations++;
        foreach (string code in new[] { "FORBIDDEN", "VERIFICATION_REQUIRED", "UPSTREAM_ERROR" })
        {
            f.Runtime.OnRequest = (call, token) => call.Operation == "account.profile"
                ? Task.FromException<JsonElement>(new MusicPlatformException(code)) : f.Transport.Respond(call, token);
            await model.RefreshAsync();
            Check(model.HasProfile && model.HasSession && model.CanRefresh && model.DisplayName == known.Profile.DisplayName &&
                model.UserId == known.Profile.UserId && observations == 0);
            Check(model.Status == (code == "VERIFICATION_REQUIRED" ? "等待平台验证" : "资料或权益读取失败"));
            Check((await f.Application.ReadDetailsAsync("wy")) is { State: "verified", Profile: not null });
            Check(f.Store.AccountWrites == writes && (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == fingerprint);
        }
        f.Runtime.OnRequest = (call, token) => call.Operation == "account.profile"
            ? Task.FromException<JsonElement>(new MusicPlatformException("INVALID_CREDENTIALS")) : f.Transport.Respond(call, token);
        await model.RefreshAsync();
        Check(!model.HasProfile && model.DisplayName == "" && model.Status == "账号会话无效或已过期");
        Check(AccountState(await f.Application.ExecuteAsync("music.accounts.list")) == "revoked" && f.Store.AccountWrites == writes);
    }
    private static async Task VipReadFailures()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
        var known = await f.Application.ReadVipPageAsync();
        Check(known.Account is not null && known.Sign is not null && known.Banner is not null);
        int writes = f.Store.AccountWrites;
        int publications = f.Runtime.Calls.Count(call => call.Method == "music.platform.session.update");
        int invalidations = 0; f.Application.VipPageInvalidated += () => invalidations++;
        var fingerprint = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
        foreach (string code in new[] { "FORBIDDEN", "VERIFICATION_REQUIRED", "UPSTREAM_ERROR" })
        {
            f.Runtime.OnRequest = (call, token) => call.Operation is "vip.signCard" or "vip.banner"
                ? Task.FromException<JsonElement>(new MusicPlatformException(code)) : f.Transport.Respond(call, token);
            var failed = await f.Application.ReadVipPageAsync();
            Check(failed.Generation == known.Generation && failed.Account == known.Account &&
                failed.Sign is null && failed.Banner is null && failed.SignError == code && failed.BannerError == code);
            Check((await f.Application.ReadDetailsAsync("wy")) is { State: "verified", Profile: not null });
            Check(invalidations == 0 && f.Store.AccountWrites == writes &&
                (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == fingerprint);
            Check(f.Runtime.Calls.Count(call => call.Method == "music.platform.session.update") == publications);
        }
    }
    private static async Task VipWriteFailures()
    {
        foreach (var (status, code, expected) in new[] {
            ("rejected", "FORBIDDEN", "FORBIDDEN"),
            ("notSent", "VERIFICATION_REQUIRED", "VERIFICATION_REQUIRED"),
            ("unknown", "UPSTREAM_ERROR", "COMMIT_UNKNOWN") })
        {
            await using var f = await Fixture.Create(nativeOnly: true);
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
            var known = await f.Application.ReadVipPageAsync();
            int writes = f.Store.AccountWrites;
            int publications = f.Runtime.Calls.Count(call => call.Method == "music.platform.session.update");
            var fingerprint = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
            f.Runtime.OnRequest = (call, token) => call.Operation == "vip.checkIn"
                ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status, errorCode = code })) : f.Transport.Respond(call, token);
            await OperationError(() => f.Application.SignVipPageAsync(known.Generation), expected);
            var retained = await f.Application.ReadVipPageAsync();
            Check(retained.Account == known.Account && retained.SignUnconfirmed == (status == "unknown") && !retained.SignConfirmed);
            Check((await f.Application.ReadDetailsAsync("wy")) is { State: "verified", Profile: not null });
            Check(f.Store.AccountWrites == writes && (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == fingerprint);
            Check(f.Runtime.Calls.Count(call => call.Method == "music.platform.session.update") == publications);
            if (status == "unknown") await OperationError(() => f.Application.SignVipPageAsync(known.Generation), "COMMIT_UNKNOWN");
            Check(f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1);
        }
    }
    private static async Task VipReadbackFailure()
    {
        foreach (string code in new[] { "FORBIDDEN", "VERIFICATION_REQUIRED", "UPSTREAM_ERROR", "AUTH_EXPIRED" })
        {
            await using var f = await Fixture.Create(nativeOnly: true);
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
            var known = await f.Application.ReadVipPageAsync();
            int writes = f.Store.AccountWrites;
            var fingerprint = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
            f.Runtime.OnRequest = (call, token) => call.Operation == "vip.signCard"
                ? Task.FromException<JsonElement>(new MusicPlatformException(code)) : f.Transport.Respond(call, token);
            var receipt = await f.Application.SignVipPageAsync(known.Generation);
            Check(receipt.Generation == known.Generation && receipt.RefreshedSign is null);
            Check(AccountState(await f.Application.ExecuteAsync("music.accounts.list")) == (code == "AUTH_EXPIRED" ? "revoked" : "verified"));
            Check(f.Store.AccountWrites == writes && (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == fingerprint);
            if (code != "AUTH_EXPIRED")
            {
                var retained = await f.Application.ReadVipPageAsync();
                Check(retained.SignConfirmed && retained.SignedToday && !retained.SignUnconfirmed && retained.SignError == code);
                await OperationError(() => f.Application.SignVipPageAsync(known.Generation), "ALREADY_SIGNED");
            }
            Check(f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1);
        }
    }
    private static async Task VipReadAuthentication()
    {
        foreach (var (operation, code) in new[] { ("vip.signCard", "AUTH_REQUIRED"), ("vip.banner", "IDENTITY_MISMATCH") })
        {
            await using var f = await Fixture.Create(nativeOnly: true);
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
            await f.Application.ReadVipPageAsync();
            int writes = f.Store.AccountWrites;
            var fingerprint = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
            f.Runtime.OnRequest = (call, token) => call.Operation == operation
                ? Task.FromException<JsonElement>(new MusicPlatformException(code)) : f.Transport.Respond(call, token);
            await OperationError(() => f.Application.ReadVipPageAsync(), "STALE_SESSION");
            Check((await f.Application.ReadDetailsAsync("wy")) is { State: "revoked", Profile: null });
            Check(f.Store.AccountWrites == writes && (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == fingerprint);
        }
    }
    private static async Task VipWriteAuthentication()
    {
        foreach (var (status, code) in new[] { ("rejected", "AUTH_EXPIRED"), ("notSent", "INVALID_CREDENTIALS") })
        {
            await using var f = await Fixture.Create(nativeOnly: true);
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
            var known = await f.Application.ReadVipPageAsync();
            int writes = f.Store.AccountWrites;
            var fingerprint = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
            f.Runtime.OnRequest = (call, token) => call.Operation == "vip.checkIn"
                ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status, errorCode = code })) : f.Transport.Respond(call, token);
            await OperationError(() => f.Application.SignVipPageAsync(known.Generation), code);
            Check((await f.Application.ReadDetailsAsync("wy")) is { State: "revoked", Profile: null });
            Check(f.Store.AccountWrites == writes && (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == fingerprint);
            Check(f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1);
        }
    }
    private static async Task VipLateAuthentication()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_OLD" })).GetProperty("ok").GetBoolean());
        var known = await f.Application.ReadVipPageAsync();
        var entered = Signal(); var release = Signal();
        f.Runtime.OnRequest = async (call, token) =>
        {
            if (call.Operation != "vip.checkIn") return await f.Transport.Respond(call, token);
            entered.TrySetResult(); await release.Task;
            return MusicPlatformRuntimeFixture.Json(new { status = "rejected", errorCode = "INVALID_CREDENTIALS" });
        };
        var old = f.Application.SignVipPageAsync(known.Generation);
        MusicAccountDetails replacement;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.Application.ExecuteAsync("music.accounts.import",
                new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_REPLACEMENT" })).GetProperty("ok").GetBoolean());
            replacement = await f.Application.ReadDetailsAsync("wy");
            Check(replacement.Generation > known.Generation && replacement.State == "verified");
        }
        finally { release.TrySetResult(); }
        await OperationError(() => old, "INVALID_CREDENTIALS");
        var current = await f.Application.ReadDetailsAsync("wy");
        Check(current.Generation == replacement.Generation && current.State == "verified" && current.Profile == replacement.Profile);
        Check(f.Store.AccountWrites == 2 && f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1);
        f.Runtime.OnRequest = f.Transport.Respond;
        Check((await f.Application.ReadVipPageAsync()).Account?.Generation == replacement.Generation);
    }
    private static async Task VipSharedRead()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        f.Runtime.OnRequest = async (call, token) =>
        {
            if (call.Operation == "vip.signCard")
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task;
            }
            return await f.Transport.Respond(call, token);
        };
        using var cancellation = new CancellationTokenSource();
        var reading = f.Application.ReadVipPageAsync(cancellation.Token);
        Task<MusicVipPageSnapshot>? joined = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected unsubscribe"); }
            catch (OperationCanceledException) { Check(cancellation.IsCancellationRequested); }
            Check(!cancelled.Task.IsCompleted && !release.Task.IsCompleted);
            joined = f.Application.ReadVipPageAsync();
            Check(!joined.IsCompleted && f.Runtime.Requests.Count(call => call.Operation == "vip.signCard") == 1);
        }
        finally { release.TrySetResult(); if (joined is not null) await joined; }
        var current = await joined!.WaitAsync(TimeSpan.FromSeconds(5));
        Check(current.Generation > 0 && current.Account is not null && current.Sign is not null);
        Check(!cancelled.Task.IsCompleted && f.Store.AccountWrites == 1 &&
            f.Runtime.Requests.Count(call => call.Operation == "vip.signCard") == 1 &&
            f.Runtime.Requests.All(call => call.Operation != "vip.checkIn"));
    }
    private static async Task VipAdmittedCancellation()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
        var known = await f.Application.ReadVipPageAsync();
        var before = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        f.Runtime.OnRequest = async (call, token) =>
        {
            if (call.Operation == "vip.checkIn")
            {
                Check(!token.CanBeCanceled, "an admitted mutation owns its physical receipt");
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task;
            }
            return await f.Transport.Respond(call, token);
        };
        using var cancellation = new CancellationTokenSource();
        var signing = f.Application.SignVipPageAsync(known.Generation, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            Check(!signing.IsCompleted && !cancelled.Task.IsCompleted);
            release.TrySetResult();
            var receipt = await signing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(receipt.Generation == known.Generation && receipt.RefreshedSign is null);
            await OperationError(() => f.Application.SignVipPageAsync(known.Generation), "ALREADY_SIGNED");
            var reread = await f.Application.ReadVipPageAsync();
            Check(reread.Generation == known.Generation && reread.Sign is { SignedToday: false } &&
                reread.SignConfirmed && reread.SignedToday && !reread.SignUnconfirmed);
            Check(f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1 &&
                f.Store.AccountWrites == 1 && (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == before);
        }
        finally { release.TrySetResult(); await signing; }
    }
    private static async Task VipUnknownAccountRecovery()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
        var known = await f.Application.ReadVipPageAsync();
        var before = (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint;
        f.Runtime.OnRequest = (call, token) => call.Operation == "vip.checkIn"
            ? Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = "NETWORK_ERROR" }))
            : f.Transport.Respond(call, token);
        await OperationError(() => f.Application.SignVipPageAsync(known.Generation), "COMMIT_UNKNOWN");
        f.Store.FailAccountReads = true;
        try
        {
            var unavailable = await f.Application.ReadVipPageAsync();
            Check(unavailable.Generation == 0 && unavailable.Account is null && unavailable.Sign is null &&
                unavailable.Banner is null && !unavailable.SignedToday && !unavailable.SignConfirmed &&
                !unavailable.SignUnconfirmed && unavailable.AccountError == "STORAGE_UNAVAILABLE");
        }
        finally { f.Store.FailAccountReads = false; }
        var recovered = await f.Application.ReadVipPageAsync();
        Check(recovered.Generation == known.Generation && recovered.Account == known.Account &&
            recovered.Sign is { SignedToday: false } && recovered.SignUnconfirmed &&
            !recovered.SignConfirmed && !recovered.SignedToday);
        await OperationError(() => f.Application.SignVipPageAsync(known.Generation), "COMMIT_UNKNOWN");
        Check(f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1 && f.Store.AccountWrites == 1 &&
            (await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace)).Fingerprint == before);
    }
    private static async Task VipPreparedSign()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
        var known = await f.Application.ReadVipPageAsync();
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        f.Runtime.OnRequest = async (call, token) =>
        {
            if (call.Operation == "vip.checkIn")
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task;
            }
            return await f.Transport.Respond(call, token);
        };
        var signing = f.Application.SignVipPageAsync(known.Generation);
        Task preparing = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preparing = f.Application.PrepareShutdownAsync();
            await OperationError(() => f.Application.ReadVipPageAsync(), "CLOSED");
            await Task.Delay(30);
            Check(!signing.IsCompleted && !preparing.IsCompleted && !cancelled.Task.IsCompleted &&
                f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
            release.TrySetResult();
            Check((await signing.WaitAsync(TimeSpan.FromSeconds(5))).Generation == known.Generation);
            await preparing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            try { await signing; } finally { await preparing; }
        }
        await f.Application.CancelShutdownPreparationAsync();
        var resumed = await f.Application.ReadVipPageAsync();
        Check(resumed.Generation == known.Generation && resumed.Sign is { SignedToday: false } &&
            resumed.SignConfirmed && resumed.SignedToday && !resumed.SignUnconfirmed,
            "retiring a page cannot turn the real confirmed sign receipt into an unknown mutation");
        await OperationError(() => f.Application.SignVipPageAsync(known.Generation), "ALREADY_SIGNED");
        Check(f.Runtime.Requests.Count(call => call.Operation == "vip.checkIn") == 1 && f.Store.AccountWrites == 1);
    }
    private static async Task VipTypedDrain()
    {
        await using var f = await Fixture.Create(nativeOnly: true);
        Check((await f.Application.ExecuteAsync("music.accounts.import",
            new { platform = "wy", cookie = "MUSIC_U=SYNTHETIC_VIP" })).GetProperty("ok").GetBoolean());
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        f.Runtime.OnRequest = async (call, token) =>
        {
            if (call.Operation == "vip.signCard")
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                entered.TrySetResult(); await release.Task;
            }
            return await f.Transport.Respond(call, token);
        };
        var reading = f.Application.ReadVipPageAsync();
        Task preparing = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            preparing = f.Application.PrepareShutdownAsync();
            await OperationError(() => f.Application.ReadVipPageAsync(), "CLOSED");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!preparing.IsCompleted && f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
        }
        finally
        {
            release.TrySetResult();
            try { await VipClosedRead(reading); } finally { await preparing; }
        }
        await f.Application.CancelShutdownPreparationAsync();
        f.Runtime.OnRequest = f.Transport.Respond;
        Check((await f.Application.ReadVipPageAsync()).Generation > 0);

        var closeEntered = Signal(); var closeRelease = Signal(); var closeCancelled = Signal();
        f.Runtime.OnRequest = async (call, token) =>
        {
            if (call.Operation == "vip.signCard")
            {
                using var registration = token.Register(() => closeCancelled.TrySetResult());
                closeEntered.TrySetResult(); await closeRelease.Task;
            }
            return await f.Transport.Respond(call, token);
        };
        var lastRead = f.Application.ReadVipPageAsync();
        Task disposal = Task.CompletedTask;
        try
        {
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = f.Application.DisposeAsync().AsTask();
            await OperationError(() => f.Application.ReadVipPageAsync(), "CLOSED");
            await closeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!disposal.IsCompleted && f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
        }
        finally
        {
            closeRelease.TrySetResult();
            try { await VipClosedRead(lastRead); } finally { await disposal; }
        }
        Check(f.Runtime.Requests.Count(call => call.Operation == "vip.signCard") == 3 &&
            f.Runtime.Requests.All(call => call.Operation != "vip.checkIn") && f.Store.AccountWrites == 1 &&
            f.Store.DisposeCalls == 0 && f.Runtime.Disposals == 0);
    }
    private static async Task VipClosedRead(Task reading)
    {
        try { await reading; }
        catch (OperationCanceledException) { return; }
        catch (MusicOperationException error) { Check(error.Code == "CLOSED"); return; }
        throw new InvalidOperationException("Expected retired VIP read");
    }
    private static async Task OperationError(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (MusicOperationException error) { Check(error.Code == expected && error.InnerException is null); return; }
        throw new InvalidOperationException("Expected " + expected);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
    private static string? AccountState(JsonElement result) => result.GetProperty("items")[0].GetProperty("state").GetString();
    private static async Task Unknown(Task task)
    {
        try { await task; throw new InvalidOperationException("Expected unknown receipt"); }
        catch (Exception error) when (error.Message == "COMMIT_UNKNOWN") { }
    }
    private static TestCase Case(string name, Func<Task> body) => new("music-account-chain: " + name, () => Sta(body));
    private static void Check(bool condition,
        [CallerArgumentExpression(nameof(condition))] string expression = "",
        [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    { if (!condition) throw new InvalidOperationException($"Account integration assertion at {member}:{line}: {expression}"); }
    private static async Task WaitForEditable(MusicCapability module)
    {
        // Admit the posted resume refresh before observing actual idle state.
        await MusicTestUi.Drain();
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
    { await TestUiHost.InvokeAsync(action); }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly MusicPlatformRuntimeFixture Runtime = new();
        public readonly AccountPeer Transport = new();
        public ControlledStore Store { get; }
        public string Data { get; }
        public MusicNativeApplication Application { get; }
        public MusicAccountFixtureApplication Calls { get; }
        public MusicCapability Module { get; }
        private Fixture(string data, ControlledStore store)
        {
            Data = data; Store = store;
            Runtime.OnRequest = Transport.Respond;
            Application = new(store, MusicTestStores.For(store), Runtime);
            Calls = new(Application);
            Module = new(Calls, store, new Output(), new Dialogs());
        }
        public static async Task<Fixture> Create(string? data = null, bool nativeOnly = false)
        {
            data ??= Path.Combine(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
                ?? throw new InvalidOperationException("Isolated data required"), "account-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            var store = new ControlledStore(new LocalDocumentStore(data, AppIdentity.NativeNamespaces, protectedNamespaces: AppIdentity.ProtectedNamespaces));
            var result = new Fixture(data, store);
            try
            {
                if (nativeOnly)
                    // Catalog-owner cases do not mount a home presentation or its recovery requests.
                    await result.Application.InitializeAsync();
                else
                {
                    await result.Module.InitializeAsync(default);
                    await result.Module.NavigateAsync("favorites", default);
                }
                Check(!result.Runtime.Calls.Any(call => !call.Method.StartsWith("music.platform.", StringComparison.Ordinal)) && result.Transport.Requests == 0);
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }
        public Task Configure(object parameters)
        {
            var p = JsonSerializer.SerializeToElement(parameters);
            if (p.TryGetProperty("qrStatus", out var status)) Transport.QrStatus = status.GetString()!;
            if (p.TryGetProperty("expired", out var expired)) Transport.Expired = expired.GetBoolean();
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            Store.Release?.TrySetResult(); Transport.Release?.TrySetResult();
            try { await Module.DisposeAsync(); Check(!Runtime.Calls.Any(call => !call.Method.StartsWith("music.platform.", StringComparison.Ordinal))); }
            finally
            {
                await Runtime.DisposeAsync(); await Store.DisposeAsync();
            }
        }
    }
    private sealed class ControlledStore(LocalDocumentStore inner) : IDocumentStore
    {
        public string? LoseReceipt;
        public bool FailAccountReads;
        public int Writes, AccountWrites, DisposeCalls;
        public TaskCompletionSource? Arrived, Release;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken token = default) =>
            FailAccountReads && ns == MusicAccountOwner.DocumentNamespace
                ? throw new DocumentStoreException(DocumentStoreError.Unavailable) : inner.ReadAsync(ns, token);
        public async Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Writes);
            if (ns == MusicAccountOwner.DocumentNamespace) Interlocked.Increment(ref AccountWrites);
            if (ns == MusicAccountOwner.DocumentNamespace && Release is { } hold)
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
    private sealed class AccountPeer
    {
        public string QrStatus = "waiting";
        public string? CatalogFailure;
        public int Requests, CatalogRequests, Verifications;
        public readonly ConcurrentQueue<long> QrPollTimes = new();
        public int QrRateLimits;
        private long? lastQrPoll;
        public bool Expired;
        public TaskCompletionSource? Arrived, Release;
        public bool HoldValidation, HoldCatalog, HoldQrPoll, LimitQrPolling;
        internal async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken cancellationToken)
        {
            if (call.Operation == "qr.cancel") return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
            Interlocked.Increment(ref Requests);
            if (call.Operation == "qr.create") {
                lastQrPoll = null;
                return MusicPlatformRuntimeFixture.Json(new { challengeId = call.Parameters.GetProperty("challengeId").GetString(),
                    url = "https://music.163.com/login?codekey=SYNTHETIC_QR", expiresAt = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeMilliseconds(), imageBase64 = (string?)null });
            }
            if (call.Operation == "qr.poll") {
                long now = Stopwatch.GetTimestamp();
                QrPollTimes.Enqueue(now);
                if (LimitQrPolling && lastQrPoll is { } previous &&
                    Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(3))
                {
                    Interlocked.Increment(ref QrRateLimits);
                    throw new MusicPlatformException("RATE_LIMITED");
                }
                lastQrPoll = now;
                if (HoldQrPoll) { HoldQrPoll = false; Arrived!.TrySetResult(); await Release!.Task.WaitAsync(cancellationToken); }
                if (QrStatus != "authenticated") return MusicPlatformRuntimeFixture.Json(new { status = QrStatus });
                if (Expired) throw new MusicPlatformException("AUTH_EXPIRED");
                Interlocked.Increment(ref Verifications);
                var account = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation("wy", "9007199254740997", vip: false).GetRawText())!.AsObject();
                account["cookie"] = "MUSIC_U=SYNTHETIC_SESSION";
                return MusicPlatformRuntimeFixture.Json(new { status = "authenticated", account });
            }
            if (call.Operation is "account.verify" or "account.profile") {
                var expired = Expired;
                if (HoldValidation) { HoldValidation = false; Arrived!.TrySetResult(); await Release!.Task.WaitAsync(cancellationToken); }
                if (expired) throw new MusicPlatformException("AUTH_EXPIRED");
                Interlocked.Increment(ref Verifications);
                var observation = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation(call.Platform,
                    call.Platform == "tx" ? "42" : "9007199254740997", vip: false, displayName: "合成账号").GetRawText())!.AsObject();
                if (call.Operation == "account.verify")
                    observation["cookie"] = call.Payload.GetProperty("candidate").GetProperty("cookie").GetString();
                return MusicPlatformRuntimeFixture.Json(observation);
            }
            if (call.Operation == "vip.signCard") return MusicPlatformRuntimeFixture.Json(new {
                text = "Fixture sign", subText = "", redPrize = false, redDay = false, buttonText = "签到",
                background = "", days = new[] { new { text = "今天", signed = false, cover = "", signedAt = 0L, today = true } } });
            if (call.Operation == "vip.banner") return MusicPlatformRuntimeFixture.Json(new {
                image = "https://images.invalid/vip", resourceType = "playlist", resourceId = "fixture-vip" });
            if (call.Operation == "vip.checkIn") return MusicPlatformRuntimeFixture.Json(new { status = "confirmed", errorCode = "" });
            Check(call.Operation == "home");
            Interlocked.Increment(ref CatalogRequests);
            var catalogFailure = CatalogFailure;
            if (HoldCatalog) { HoldCatalog = false; Arrived!.TrySetResult(); await Release!.Task; }
            if (catalogFailure is not null) throw new MusicPlatformException(catalogFailure);
            return MusicPlatformRuntimeFixture.Json(new { platform = call.Platform, sections = Array.Empty<object>(), more = false, continuation = (object?)null });
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

internal sealed class MusicAccountFixtureApplication(IMusicApplication inner) : MusicApplicationForwarder(inner)
{
    internal readonly List<string> AccountCalls = [];
    private AccountReadHold? nextRead;

    internal async Task CompleteInternalAccountReadAsync(MusicCapability module, string invalidatedPlatform)
    {
        await module.ActivatePlatformSettingsAsync();
        using var read = new AccountReadHold();
        nextRead = read;
        try
        {
            // A different platform's real account invalidation refreshes the shared
            // snapshot without cancelling the selected login or publishing a UI notice.
            var cancelled = await ExecuteAsync("music.accounts.cancel", new { platform = invalidatedPlatform });
            if (!cancelled.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException("Fixture account invalidation failed");
            var activation = module.ActivatePlatformSettingsAsync();
            await read.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (activation.IsCompleted)
                throw new InvalidOperationException("Fixture activation did not join the held account read");
            read.Release.TrySetResult();
            await activation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (ReferenceEquals(nextRead, read)) nextRead = null; }
    }

    public override Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (method.StartsWith("music.accounts.", StringComparison.Ordinal)) AccountCalls.Add(method);
        var read = method == "music.accounts.list" ? nextRead : null;
        if (read is not null) nextRead = null;
        var result = base.ExecuteAsync(method, parameters, cancellationToken);
        return read is null ? result : Hold(result, read, cancellationToken);

        static async Task<JsonElement> Hold(Task<JsonElement> result, AccountReadHold read, CancellationToken token)
        {
            var reply = await result;
            read.Arrived.TrySetResult();
            await read.Release.Task.WaitAsync(token);
            return reply;
        }
    }

    private sealed class AccountReadHold : IDisposable
    {
        internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Release.TrySetResult();
    }
}
