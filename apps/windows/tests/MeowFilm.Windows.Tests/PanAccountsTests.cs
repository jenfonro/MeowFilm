using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using static MeowFilm.Windows.Tests.PanAccountFixture;

namespace MeowFilm.Windows.Tests;

public static class PanAccountsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("projects seven providers and verifies only configured rows with three actions", Projection);
        yield return Case("network format and rate failures preserve state; only explicit auth expires credentials", Failures);
        yield return Case("whole replacement preserves password spaces and accepts optional timestamp and TV fields", Manual);
        yield return Case("manual errors keep the dialog open and never publish raw upstream errors", ManualFailures);
        yield return Case("retired manual writes drain real side effects before logout or replacement", ManualReceipts);
        yield return Case("retired clear drains before reentry read and manual or QR account replacement", ClearLoginReceipts);
        yield return Case("reentry refresh and clear cannot pass an admitted retired clear", ClearOperationReceipts);
        yield return Case("shutdown waits for the real retired clear without publishing or refreshing", ClearShutdownReceipt);
        yield return Case("QR pending scanned confirmed expired and cancelled have distinct lifecycle", QrStates);
        yield return Case("QR failures never claim success and expired challenges can restart", QrFailures);
        yield return Case("URL encoding uses arbitrary provider text and data images stay local", QrImages);
        yield return Case("close and method switch cancel late start before admitting successor", LateStart);
        yield return Case("leaving cancels poll and old confirmation cannot overwrite newer logout revision", LatePoll);
        yield return Case("late refresh cannot replace logout; row operations exclude duplicate mutations", LateRefresh);
        yield return Case("page reads cannot overwrite later account changes and failed reads can retry", Reads);
        yield return Case("shutdown drains late login and manual operations without owning runtime", Shutdown);
    }
    private static TestCase Case(string name, Func<Task> run) =>
        new("pan-accounts: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task Projection()
    {
        var runtime = new PanAccountFixture();
        runtime.Account("baidu", 1, "signedOut", "cookie");
        runtime.Account("quark", 2, "expired", "cookie");
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        Check(model.Rows.Select(row => row.Provider).SequenceEqual(Providers.Select(item => item.Id)), "real provider order projects all seven accounts");
        Check(runtime.Calls.Count(call => call.Method == "pan.refresh") == 2, "configured rows are verified even when their last status was signedOut");
        Check(model.Rows.Take(2).All(row => row is { StatusText: "已登录", CanRefresh: true, CanLogout: true, CanLogin: false }),
            "signed-in rows have refresh and logout only");
        Check(model.Rows.Skip(2).All(row => row is { StatusText: "未登录", CanLogin: true, CanRefresh: false, CanLogout: false }),
            "signed-out rows have login only");
        Check(model.Rows.Count(row => row.SupportsQr) == 3, "only Baidu Quark and UC expose default QR");
        runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure("AUTH_FAILED"));
        await model.RefreshAsync(model.Rows[0]);
        Check(model.Rows[0] is { StatusText: "失效", LoginText: "重新登录", CanLogin: true, CanLogout: true, CanRefresh: false, Configured: true },
            "expired rows retain credentials and expose relogin and logout");
    }
    private static async Task Failures()
    {
        var runtime = new PanAccountFixture();
        runtime.Account("quark", 4, "signedIn", "cookie");
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows.Single(row => row.Provider == "quark");
        foreach (string code in new[] { "NETWORK_ERROR", "TIMEOUT", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "UPSTREAM_ERROR", "CHALLENGE_REQUIRED" })
        {
            runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure(code));
            await model.RefreshAsync(row);
            Check(row is { Status: "signedIn", Revision: 4, Configured: true } && model.Notice.Length > 0,
                "an ordinary failure preserves the last account state and notifies outside the row");
        }
        foreach (string code in new[] { "AUTH_REQUIRED", "AUTH_FAILED" })
        {
            runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure(code));
            await model.RefreshAsync(row);
            Check(row.Status == "expired" && row.Configured, "only explicit account authentication failure expires");
        }
        runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure("NETWORK_ERROR"));
        await model.RefreshAsync(row);
        Check(row.Status == "expired", "network failure also retains a previously expired account");
    }
    private static async Task Manual()
    {
        var runtime = new PanAccountFixture();
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var cases = new (string Provider, Dictionary<string, string> Draft, string[] Fields)[]
        {
            ("tianyi", new() { ["cookie"] = "synthetic-cookie" }, ["cookie"]),
            ("tianyi", new() { ["username"] = "synthetic-user", ["password"] = "  synthetic password  " }, ["username", "password"]),
            ("yidong", new() { ["authorization"] = "synthetic-authorization" }, ["authorization"]),
            ("yidong", new() { ["authorization"] = "synthetic-authorization", ["refreshedAt"] = "1730000000000" }, ["authorization", "refreshedAt"]),
            ("quark_tv", new() { ["refreshToken"] = "synthetic-refresh", ["deviceId"] = "synthetic-device" }, ["refreshToken", "deviceId"]),
            ("uc_tv", new() { ["refreshToken"] = "synthetic-refresh", ["deviceId"] = "synthetic-device", ["expiresAt"] = "1730000000000" }, ["refreshToken", "deviceId", "expiresAt"])
        };
        foreach (var item in cases)
        {
            var row = model.Rows.Single(row => row.Provider == item.Provider);
            if (row.Configured) await model.LogoutAsync(row);
            await model.OpenLoginAsync(row);
            Check(!model.QrMode && model.CanSave, "non-QR providers use manual fields");
            JsonElement? sent = null;
            runtime.Overrides["pan.settings.set"] = (parameters, _) =>
            {
                sent = parameters.Clone();
                var fields = parameters.GetProperty("credentials").EnumerateObject().Select(field => field.Name).ToArray();
                runtime.Account(item.Provider, row.Revision + 1, "signedOut", fields);
                return Task.FromResult(Ok(runtime.Accounts[item.Provider]));
            };
            await model.SaveAsync(item.Draft);
            var credentials = sent!.Value.GetProperty("credentials");
            Check(credentials.EnumerateObject().Select(field => field.Name).SequenceEqual(item.Fields), "manual save sends only the supplied whole replacement");
            if (item.Draft.ContainsKey("password"))
                Check(credentials.GetProperty("password").GetString() == item.Draft["password"], "password whitespace is preserved exactly");
            foreach (string field in item.Fields.Where(field => field is "refreshedAt" or "expiresAt"))
                Check(credentials.GetProperty(field).ValueKind == JsonValueKind.Number, "protocol timestamps are JSON numbers");
            Check(row.Status == "signedIn" && !model.LoginOpen && !row.IsBusy, "save refreshes before closing the dialog");
        }
    }
    private static async Task ManualFailures()
    {
        var runtime = new PanAccountFixture();
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows.Single(row => row.Provider == "yidong");
        await model.OpenLoginAsync(row);
        foreach (string number in new[] { "-1", "1.5", "9007199254740992", "not-a-number" })
        {
            int count = runtime.Calls.Count;
            await model.SaveAsync(new Dictionary<string, string> { ["refreshedAt"] = number });
            Check(runtime.Calls.Count == count && model.LoginOpen && model.CanSave, "invalid protocol numbers do not dispatch or close the editor");
        }
        runtime.Overrides["pan.settings.set"] = (_, _) => Task.FromResult(Failure("STORAGE_ERROR"));
        await model.SaveAsync(new Dictionary<string, string> { ["authorization"] = "synthetic-authorization" });
        Check(model.LoginOpen && !row.Configured && model.CanSave, "failed save retains the editor and does not claim login");
        runtime.Overrides.Remove("pan.settings.set");
        runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure("NETWORK_ERROR"));
        await model.SaveAsync(new Dictionary<string, string> { ["authorization"] = "synthetic-authorization" });
        Check(model.LoginOpen && row.Configured && row.Status == "signedOut" && model.CanSave, "network failure after saving does not fabricate verification or expiration");
        foreach (var state in new (bool Configured, string Status, long RevisionOffset)[]
            { (true, "signedOut", 0), (true, "expired", 0), (false, "signedOut", 0), (true, "signedIn", -1) })
        {
            runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Ok(new
            {
                refreshed = true,
                account = runtime.Accounts["yidong"] with
                {
                    Configured = state.Configured, Status = state.Status,
                    Revision = runtime.Accounts["yidong"].Revision + state.RevisionOffset
                }
            }));
            await model.SaveAsync(new Dictionary<string, string> { ["authorization"] = "synthetic-authorization" });
            Check(model.LoginOpen && row.Configured && row.Status == "signedOut" && model.CanSave &&
                model.Notice != "网盘凭据已保存", "manual verification requires a current configured signed-in account, not only refreshed=true");
        }
        runtime.Overrides["pan.refresh"] = (_, _) => throw new InvalidOperationException("synthetic-private-upstream");
        await model.CloseLoginAsync();
        await model.RefreshAsync(row);
        Check(!model.Notice.Contains("synthetic", StringComparison.Ordinal), "raw upstream failures never become user text");
    }
    private static async Task ManualReceipts()
    {
        foreach (bool logout in new[] { true, false })
        {
            var runtime = new PanAccountFixture();
            runtime.Account("tianyi", 4, "expired", "cookie");
            runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure("AUTH_FAILED"));
            await using var model = new PanAccounts(runtime);
            await model.ActivateAsync();
            runtime.Overrides.Remove("pan.refresh");
            var row = model.Rows.Single(row => row.Provider == "tianyi");
            await model.OpenLoginAsync(row);
            var entered = Gate<bool>(); var release = Gate<bool>();
            Task<JsonElement>? oldReceipt = null;
            int writes = 0, refreshes = runtime.Calls.Count(call => call.Method == "pan.refresh");
            runtime.Overrides["pan.settings.set"] = (parameters, token) =>
            {
                bool first = ++writes == 1;
                var fields = parameters.GetProperty("credentials").EnumerateObject().Select(field => field.Name).ToArray();
                var receipt = Commit();
                if (first) oldReceipt = receipt;
                return receipt.WaitAsync(token);
                async Task<JsonElement> Commit()
                {
                    if (first) { entered.TrySetResult(true); await release.Task; }
                    runtime.Account("tianyi", runtime.Accounts["tianyi"].Revision + 1, "signedOut", fields);
                    return Ok(runtime.Accounts["tianyi"]);
                }
            };
            var save = model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-retired-cookie" });
            await entered.Task; // HTTP is admitted, but its replace has not run yet.
            var close = model.CloseLoginAsync();
            Task successor;
            if (logout) successor = model.LogoutAsync(row);
            else
            {
                await model.OpenLoginAsync(row);
                successor = model.SaveAsync(new Dictionary<string, string>
                { ["username"] = "synthetic-new-user", ["password"] = "synthetic-new-password" });
            }
            bool fenced;
            try
            {
                // Drain queued cancellation continuations, without releasing the runtime write gate.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                fenced = !close.IsCompleted && !successor.IsCompleted && writes == 1 &&
                    runtime.Accounts["tianyi"].Revision == 4 &&
                    runtime.Calls.All(call => call.Method != "pan.settings.clear");
            }
            finally { release.TrySetResult(true); }
            await Task.WhenAll(save, close, successor, oldReceipt!);
            var stored = runtime.Accounts["tianyi"];
            Check(logout ? !stored.Configured && stored.Status == "signedOut" :
                stored.Configured && stored.Status == "signedIn" && stored.Fields.SequenceEqual(["username", "password"]),
                "the last persisted result is logout or the new account, never the delayed old replace");
            Check(stored.Revision == 6 && row.Revision == stored.Revision && row.Configured == stored.Configured &&
                row.Status == stored.Status, "final presentation agrees with the runtime after both side effects");
            Check(fenced, "close and a successor must wait for the real retired write receipt, not HTTP cancellation");
            Check(runtime.Calls.Count(call => call.Method == "pan.refresh") == refreshes + (logout ? 0 : 1),
                "retired manual receipts never refresh or publish success");
        }
    }
    private static async Task ClearLoginReceipts()
    {
        foreach (bool qr in new[] { false, true })
        {
            var runtime = new PanAccountFixture();
            string provider = qr ? "baidu" : "tianyi";
            runtime.Account(provider, 4, "expired", "cookie");
            runtime.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure("AUTH_FAILED"));
            await using var model = new PanAccounts(runtime);
            await model.ActivateAsync();
            runtime.Overrides.Remove("pan.refresh");
            var row = model.Rows.Single(row => row.Provider == provider);
            var delayed = new DelayedClear(runtime);
            var clear = model.LogoutAsync(row);
            await delayed.Entered.Task;
            int calls = runtime.Calls.Count;
            var notices = new List<string>();
            model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(model.Notice)) notices.Add(model.Notice); };
            var leave = model.DeactivateAsync();
            var read = model.ActivateAsync();
            var firstOpen = model.OpenLoginAsync(row);
            var close = model.CloseLoginAsync();
            var nextOpen = model.OpenLoginAsync(row);
            var save = qr ? Task.CompletedTask : model.SaveAsync(new Dictionary<string, string>
            { ["username"] = "synthetic-new-user", ["password"] = "synthetic-new-password" });
            bool fenced;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                fenced = !clear.IsCompleted && !leave.IsCompleted && !read.IsCompleted && !close.IsCompleted &&
                    !(qr ? nextOpen : save).IsCompleted && runtime.Calls.Count == calls &&
                    runtime.Accounts[provider].Revision == 4;
            }
            finally { delayed.Release.TrySetResult(true); }
            await Task.WhenAll(clear, leave, read, firstOpen, close, nextOpen, save, delayed.Receipt);
            if (qr)
            {
                runtime.Overrides["pan.login.poll"] = (_, _) =>
                {
                    runtime.Account(provider, runtime.Accounts[provider].Revision + 1, "signedIn", "cookie");
                    return Task.FromResult(runtime.Poll(provider, "confirmed", runtime.Accounts[provider]));
                };
                await model.PollLoginAsync();
                await model.CloseLoginAsync();
            }
            var stored = runtime.Accounts[provider];
            Check(stored is { Revision: 6, Configured: true, Status: "signedIn" } &&
                stored.Fields.SequenceEqual(qr ? new[] { "cookie" } : new[] { "username", "password" }),
                "after the old clear really commits, the final stored account is the new login, not a delayed deletion");
            Check(row.Revision == stored.Revision && row.Configured == stored.Configured && row.Status == stored.Status &&
                !model.LoginOpen, "new persisted account and UI agree after both actual side effects drain");
            Check(fenced, "reentry read, close cleanup and the new login all wait for the old clear receipt");
            Check(!notices.Contains("已退出网盘账号"), "the retired clear never publishes a logout success");
            Check(runtime.Starts == (qr ? 1 : 0) &&
                runtime.Calls.Count(call => call.Method == "pan.refresh") == (qr ? 1 : 2),
                "only the new login sends start or verification; closed work and the retired clear send no extra refresh");
        }
    }
    private static async Task ClearOperationReceipts()
    {
        foreach (bool refresh in new[] { true, false })
        {
            var runtime = new PanAccountFixture();
            runtime.Account("baidu", 4, "signedIn", "cookie");
            await using var model = new PanAccounts(runtime);
            await model.ActivateAsync();
            var row = model.Rows[0];
            var delayed = new DelayedClear(runtime);
            var clear = model.LogoutAsync(row);
            await delayed.Entered.Task;
            int calls = runtime.Calls.Count;
            var leave = model.DeactivateAsync();
            var read = model.ActivateAsync();
            var successor = refresh ? model.RefreshAsync(row) : model.LogoutAsync(row);
            bool fenced;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                fenced = !leave.IsCompleted && !read.IsCompleted && !successor.IsCompleted &&
                    runtime.Calls.Count == calls && runtime.Accounts["baidu"].Revision == 4;
            }
            finally { delayed.Release.TrySetResult(true); }
            await Task.WhenAll(clear, leave, read, successor, delayed.Receipt);
            var stored = runtime.Accounts["baidu"];
            Check(fenced, "a cross-page refresh or second clear cannot be admitted ahead of the actual retired clear");
            Check(stored.Revision == (refresh ? 5 : 6) && !stored.Configured && stored.Status == "signedOut" &&
                row.Revision == stored.Revision && !row.Configured && row.Status == stored.Status,
                "successor results preserve the actual cleared account and revision");
            Check(runtime.Calls.Count(call => call.Method == "pan.refresh") == (refresh ? 2 : 1) &&
                runtime.Calls.Count(call => call.Method == "pan.settings.clear") == (refresh ? 1 : 2),
                "retired clear itself performs no refresh and successors dispatch exactly once");
        }
    }
    private static async Task ClearShutdownReceipt()
    {
        var runtime = new PanAccountFixture();
        runtime.Account("baidu", 4, "signedIn", "cookie");
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows[0];
        var delayed = new DelayedClear(runtime);
        var clear = model.LogoutAsync(row);
        await delayed.Entered.Task;
        int calls = runtime.Calls.Count;
        var shutdown = model.PrepareShutdownAsync();
        bool fenced;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            fenced = !shutdown.IsCompleted && !clear.IsCompleted && !delayed.Receipt.IsCompleted;
            await model.ActivateAsync();
        }
        finally { delayed.Release.TrySetResult(true); }
        await Task.WhenAll(clear, shutdown, delayed.Receipt);
        Check(fenced && runtime.Accounts["baidu"] is { Revision: 5, Configured: false, Status: "signedOut" },
            "shutdown does not finish until the admitted clear actually changes storage");
        Check(row is { Revision: 4, Configured: true, Status: "signedIn" } &&
            model.Notice != "已退出网盘账号" && runtime.Calls.Count == calls,
            "retired clear does not publish, refresh or admit work while shutdown drains");
    }
    private static async Task QrStates()
    {
        var runtime = new PanAccountFixture();
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows[0];
        await model.OpenLoginAsync(row);
        Check(model.LoginOpen && model.QrMode && model.QrImage is { IsFrozen: true }, "QR defaults on supported providers");
        foreach (string state in new[] { "pending", "scanned" })
        {
            runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(runtime.Poll("baidu", state));
            await model.PollLoginAsync();
            Check(model.LoginOpen && !row.Configured && model.LoginStatus == (state == "pending" ? "等待扫码" : "等待确认"),
                "pending and scanned never count as authenticated");
        }
        Check(runtime.Calls.Where(call => call.Method == "pan.login.poll").All(call => call.Timeout == TimeSpan.FromSeconds(45)),
            "Baidu poll uses a single 45-second deadline");
        runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(runtime.Poll("baidu", "expired"));
        await model.PollLoginAsync();
        Check(model.LoginOpen && model.QrImage is null && model.CanRetryQr, "expired challenge remains retryable");
        await model.RetryQrAsync();
        Check(runtime.Cancelled.SequenceEqual(["session-1"]) && runtime.Starts == 2, "retry retires the prior challenge");
        runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(runtime.Poll("baidu", "cancelled"));
        await model.PollLoginAsync();
        Check(model.LoginOpen && model.QrImage is null && !row.Configured, "cancelled is not success");
        await model.RetryQrAsync();
        runtime.Account("baidu", 1, "signedIn", "cookie");
        runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(runtime.Poll("baidu", "confirmed", runtime.Accounts["baidu"]));
        await model.PollLoginAsync();
        await model.CloseLoginAsync();
        Check(!model.LoginOpen && row is { Configured: true, Status: "signedIn", Revision: 1 } &&
            !runtime.Cancelled.Contains("session-3"), "only persisted confirmed closes; close does not clear a confirmed account");
        await model.OpenLoginAsync(model.Rows[1]);
        runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(runtime.Poll("quark", "pending"));
        await model.PollLoginAsync();
        Check(runtime.Calls.Last(call => call.Method == "pan.login.poll").Timeout is null, "other providers keep the host deadline");
    }
    private static async Task QrFailures()
    {
        var runtime = new PanAccountFixture();
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows[0];
        foreach (string code in new[] { "STORAGE_ERROR", "NETWORK_ERROR", "TIMEOUT", "LOGIN_EXPIRED" })
        {
            await model.OpenLoginAsync(row);
            runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(Failure(code));
            await model.PollLoginAsync();
            Check(model.LoginOpen && !row.Configured && model.CanRetryQr && model.Notice.Length > 0,
                "a failed exchange or commit never closes as a successful login");
            if (code == "LOGIN_EXPIRED") Check(model.QrImage is null, "expired session removes its image");
            await model.CloseLoginAsync();
        }
        await model.OpenLoginAsync(row);
        runtime.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(runtime.Poll("baidu", "confirmed"));
        await model.PollLoginAsync();
        Check(model.LoginOpen && !row.Configured, "confirmed without the saved account receipt is not accepted");
        await model.RetryQrAsync();
        Check(model.QrImage is not null, "failed QR exchange remains explicitly retryable");
    }
    private static Task QrImages()
    {
        var text = PanQrImage.Create("url", "http://arbitrary.fixture.invalid:8000/扫码?token=synthetic#payload");
        Check(text is { IsFrozen: true, Format: var format } && format == PixelFormats.Gray8, "pan URL is encoded without music's domain policy");
        var pixels = new byte[text.PixelWidth * text.PixelHeight];
        text.CopyPixels(pixels, text.PixelWidth, 0);
        Check(pixels.Contains((byte)0) && pixels.Contains((byte)255), "real QR pixels are produced");
        var image = PanQrImage.Create("image", PixelDataUri);
        Check(image is { IsFrozen: true, PixelWidth: 1, PixelHeight: 1 } && image.Format == PixelFormats.Bgra32,
            "data URI is decoded into detached local pixels");
        try { PanQrImage.Create("image", "https://image.fixture.invalid/code.png"); throw new Exception("Expected data URI"); }
        catch (BridgeException) { }
        return Task.CompletedTask;
    }
    internal const string PixelDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=";

    private static async Task LateStart()
    {
        var runtime = new PanAccountFixture();
        var entered = Gate<bool>(); var release = Gate<JsonElement>();
        runtime.Overrides["pan.login.start"] = (_, _) => { entered.TrySetResult(true); return release.Task; };
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var start = model.OpenLoginAsync(model.Rows[0]);
        await entered.Task;
        var close = model.CloseLoginAsync();
        Check(!model.LoginOpen && !close.IsCompleted, "close fences immediately while late start drains");
        var successor = model.OpenLoginAsync(model.Rows[1]);
        runtime.Overrides.Remove("pan.login.start");
        release.SetResult(runtime.Start("baidu", "late-start"));
        await Task.WhenAll(start, close, successor);
        Check(runtime.Cancelled.SequenceEqual(["late-start"]) && model.LoginRow == model.Rows[1] && model.QrImage is not null,
            "late challenge is cancelled before the successor starts");
        await model.SwitchMethodAsync(false);
        Check(!model.QrMode && model.CanSave && model.QrImage is null && runtime.Cancelled.Count == 2,
            "switching to manual cancels and clears the QR challenge");
        await model.SwitchMethodAsync(true);
        Check(model.QrMode && model.QrImage is not null, "switching back creates a fresh challenge");
        await model.CloseLoginAsync();
        entered = Gate<bool>(); release = Gate<JsonElement>();
        runtime.Overrides["pan.login.start"] = (_, _) => { entered.TrySetResult(true); return release.Task; };
        var late = model.OpenLoginAsync(model.Rows[0]);
        await entered.Task;
        await model.SwitchMethodAsync(false);
        var manual = model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-cookie" });
        Check(!manual.IsCompleted && !model.QrMode, "manual input is available while the old start retires; its write waits for cleanup");
        release.SetResult(runtime.Start("baidu", "late-method-start"));
        await Task.WhenAll(late, manual);
        int cancel = runtime.Calls.FindLastIndex(call => call.Method == "pan.login.cancel");
        int set = runtime.Calls.FindLastIndex(call => call.Method == "pan.settings.set");
        Check(runtime.Cancelled.Contains("late-method-start") && cancel < set && model.Rows[0].Status == "signedIn",
            "switching methods cancels a late challenge before whole replacement");
    }
    private static async Task LatePoll()
    {
        var runtime = new PanAccountFixture();
        var entered = Gate<bool>(); var release = Gate<JsonElement>();
        bool cancelled = false;
        runtime.Overrides["pan.login.poll"] = async (_, token) =>
        {
            using var registration = token.Register(() => cancelled = true);
            entered.TrySetResult(true);
            return await release.Task; // Deliberately ignore cancellation to test publication fencing.
        };
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows[0];
        await model.OpenLoginAsync(row);
        var poll = model.PollLoginAsync();
        await entered.Task;
        var leave = model.DeactivateAsync();
        runtime.Account("baidu", 2, "signedOut");
        var activate = model.ActivateAsync();
        Check(cancelled && !model.LoginOpen, "leaving cancels the in-flight poll immediately");
        release.SetResult(runtime.Poll("baidu", "confirmed", new("baidu", 1, true, "signedIn", ["cookie"])));
        await Task.WhenAll(poll, leave, activate);
        Check(row is { Revision: 2, Status: "signedOut", Configured: false } && runtime.Cancelled.Count == 1,
            "late confirmation cannot replace a later cleared revision");
    }
    private static async Task LateRefresh()
    {
        var runtime = new PanAccountFixture();
        runtime.Account("baidu", 1, "signedIn", "cookie");
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var row = model.Rows[0]; var entered = Gate<bool>(); var release = Gate<JsonElement>();
        runtime.Overrides["pan.refresh"] = (_, _) => { entered.TrySetResult(true); return release.Task; };
        var refresh = model.RefreshAsync(row);
        await entered.Task;
        int calls = runtime.Calls.Count;
        await model.RefreshAsync(row); await model.LogoutAsync(row); await model.OpenLoginAsync(row);
        Check(runtime.Calls.Count == calls && row.IsBusy, "busy row rejects duplicate refresh and account mutations");
        await model.DeactivateAsync();
        runtime.Overrides.Remove("pan.refresh");
        await model.ActivateAsync();
        await model.LogoutAsync(row);
        release.SetResult(Ok(new { refreshed = true, account = new PanAccountSnapshot("baidu", 1, true, "signedIn", ["cookie"]) }));
        await refresh;
        Check(row is { Revision: 2, Status: "signedOut", Configured: false }, "late refresh cannot overwrite successful revision-advancing logout");
    }
    private static async Task Reads()
    {
        var runtime = new PanAccountFixture();
        runtime.Overrides["pan.settings.get"] = (_, _) => Task.FromResult(Failure("NETWORK_ERROR"));
        await using var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        Check(model.LoadFailed && model.Rows.All(row => !row.HasSnapshot && !row.CanLogin), "failed initial read has no fabricated editable accounts");
        runtime.Overrides.Remove("pan.settings.get");
        await model.ReloadAsync();
        Check(!model.LoadFailed && model.Rows.All(row => row.HasSnapshot), "read failure is explicitly retryable");
        var entered = Gate<bool>(); var release = Gate<JsonElement>();
        runtime.Overrides["pan.settings.get"] = (_, _) => { entered.TrySetResult(true); return release.Task; };
        var read = model.ReloadAsync();
        await entered.Task;
        var row = model.Rows.Single(row => row.Provider == "tianyi");
        await model.OpenLoginAsync(row);
        await model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-cookie" });
        release.SetResult(Ok(new { accounts = Providers.Select(item => new PanAccountSnapshot(item.Id, 0, false, "signedOut", [])).ToArray() }));
        await read;
        Check(row.Revision == 1 && row.Status == "signedIn", "read captured before a mutation cannot replace its newer revision");
    }
    private static async Task Shutdown()
    {
        var runtime = new PanAccountFixture();
        var entered = Gate<bool>(); var release = Gate<JsonElement>();
        runtime.Overrides["pan.login.start"] = (_, _) => { entered.TrySetResult(true); return release.Task; };
        var model = new PanAccounts(runtime);
        await model.ActivateAsync();
        var start = model.OpenLoginAsync(model.Rows[0]); await entered.Task;
        var shutdown = model.PrepareShutdownAsync();
        Check(!shutdown.IsCompleted && !model.LoginOpen, "shutdown revokes presentation but waits for actual cleanup");
        release.SetResult(runtime.Start("baidu", "shutdown-start"));
        await Task.WhenAll(start, shutdown);
        Check(runtime.Cancelled.SequenceEqual(["shutdown-start"]), "late startup is cancelled during shutdown");
        await model.ActivateAsync();
        Check(!model.Rows[0].CanLogin, "prepared owner admits no work");
        model.Resume(); runtime.Overrides.Clear();
        await model.ActivateAsync();
        await model.OpenLoginAsync(model.Rows.Single(row => row.Provider == "tianyi"));
        var writeEntered = Gate<bool>(); var writeRelease = Gate<JsonElement>();
        runtime.Overrides["pan.settings.set"] = (_, _) => { writeEntered.TrySetResult(true); return writeRelease.Task; };
        var save = model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-cookie" });
        await writeEntered.Task;
        var disposal = model.DisposeAsync().AsTask();
        Check(!disposal.IsCompleted, "manual receipt drains before owner disposal");
        writeRelease.SetResult(Ok(new PanAccountSnapshot("tianyi", 1, true, "signedOut", ["cookie"])));
        await Task.WhenAll(save, disposal);
        Check(!model.Rows.Single(row => row.Provider == "tianyi").Configured, "retired save cannot publish after shutdown");
        Check(runtime.Calls.All(call => call.Method.StartsWith("pan.", StringComparison.Ordinal)), "owner only borrows existing pan calls");
    }
}
