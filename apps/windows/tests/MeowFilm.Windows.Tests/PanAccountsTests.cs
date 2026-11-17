using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.PanAccountFixture;

namespace MeowFilm.Windows.Tests;

public static class PanAccountsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("offline reads manual saves and clears never call the runtime", Offline);
        yield return Case("failed offline QR leaves local credentials editable", OfflineQr);
        yield return Case("native credentials and stored documents enforce provider types", Validation);
        yield return Case("error snapshots have only provider and fixed code and cannot masquerade as account receipts", ErrorSnapshots);
        yield return Case("corrupt account rows disable operations while healthy siblings commit and stale errors stay retired", IsolatedErrors);
        yield return Case("one provider edit preserves unknown material and does not validate other accounts", RawInputs);
        yield return Case("save errors and CAS conflicts preserve the dialog and newer accounts", SaveFailures);
        yield return Case("retired saves and clears drain durable receipts before successors or shutdown", Receipts);
        yield return Case("cancelled reads do not admit clears and old reads cannot overwrite saves", Reads);
        yield return Case("online success rereads protected storage and rejects stale HTTP snapshots", Online);
        yield return Case("only explicit current authentication failures expire credentials", Failures);
        yield return Case("QR states failures and confirmed storage receipts retain their lifecycle", QrStates);
        yield return Case("late QR cleanup cannot block local settings but is drained on shutdown", LateStart);
        yield return Case("late polls and refreshes cannot publish into a reentered page", LateResults);
        yield return Case("URL encoding uses provider text and data images stay local", QrImages);
    }
    private static TestCase Case(string name, Func<Task> run) =>
        new("pan-accounts: " + name, () => MusicQualityConsumerTests.Sta(run));

    private static async Task Offline()
    {
        var fixture = new PanAccountFixture { Offline = true };
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        Check(model.Rows.Select(row => row.Provider).SequenceEqual(["baidu", "quark", "uc", "tianyi", "yidong"]) &&
            model.Rows.All(row => row.HasSnapshot && !row.Configured && row.Revision == 0) && fixture.Calls.Count == 0,
            "empty native settings expose exactly five unconfigured providers without runtime calls");
        Check(model.Rows.All(row => row.CredentialFields.Select(field => field.Id)
                .SequenceEqual(Providers.Single(provider => provider.Id == row.Provider).Fields)) &&
            model.Rows.Where(row => row.SupportsQr).Select(row => row.Provider).SequenceEqual(["baidu", "quark", "uc"]) &&
            model.Rows.All(row => row.SupportsRefresh), "current providers retain exact manual fields, QR and refresh capabilities");
        var cases = new (string Provider, Dictionary<string, string> Draft)[]
        {
            ("baidu", new() { ["cookie"] = "synthetic-baidu-cookie" }),
            ("quark", new() { ["cookie"] = "synthetic-quark-cookie" }),
            ("uc", new() { ["cookie"] = "synthetic-uc-cookie" }),
            ("tianyi", new() { ["username"] = "synthetic-user", ["password"] = "  synthetic password  " }),
            ("yidong", new() { ["authorization"] = "synthetic-auth", ["refreshedAt"] = "1730000000000" })
        };
        foreach (var (provider, draft) in cases)
        {
            var row = model.Rows.Single(row => row.Provider == provider);
            await model.OpenCredentialsAsync(row);
            await model.SaveAsync(draft);
            var credentials = fixture.Saved.GetProperty("accounts").GetProperty(provider).GetProperty("credentials");
            Check(credentials.EnumerateObject().Select(field => field.Name).SequenceEqual(draft.Keys),
                "only the supplied whole replacement is saved");
            if (draft.ContainsKey("password")) Check(credentials.GetProperty("password").GetString() == draft["password"], "password spaces remain exact");
            foreach (var field in draft.Keys.Where(field => field is "refreshedAt"))
                Check(credentials.GetProperty(field).ValueKind == JsonValueKind.Number, "timestamps are numbers");
            Check(!model.LoginOpen && row.Configured && row.Revision == 1, "native commit closes the editor without online verification");
            await model.LogoutAsync(row);
            Check(!row.Configured && row.Revision == 2 && row.Status == "signedOut", "offline clear advances its real revision");
        }
        Check(fixture.Calls.Count == 0 && fixture.Notifications.Count == 0, "offline edits do not start or queue runtime work");
        fixture.Offline = false; fixture.NotificationFails = true;
        var baidu = model.Rows[0];
        await model.OpenCredentialsAsync(baidu);
        await model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-new-cookie" });
        Check(!model.LoginOpen && baidu.Revision == 3 && model.Notice == "网盘凭据已保存",
            "notification failure cannot turn a durable commit into a failed save");
    }

    private static async Task OfflineQr()
    {
        var fixture = new PanAccountFixture { Offline = true };
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        await model.OpenLoginAsync(model.Rows[0]);
        Check(model.LoginOpen && model.QrImage is null && !model.LoginBusy && model.Notice.Length > 0,
            "offline QR fails rather than creating a client-side upstream request");
        await model.SwitchMethodAsync(false);
        await model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-cookie" });
        Check(model.Rows[0].Configured && !model.LoginOpen && fixture.Calls.Count == 1, "manual save remains local after failed QR");
    }

    private static async Task Validation()
    {
        var fixture = new PanAccountFixture();
        var store = new PanSettingsStore(fixture, fixture);
        foreach (var (provider, credentials) in new (string, Dictionary<string, object>)[]
        {
            ("unknown", new()), ("baidu", new() { ["cookie"] = 1L }),
            ("yidong", new() { ["refreshedAt"] = "1" }), ("yidong", new() { ["refreshedAt"] = -1L }),
            ("yidong", new() { ["refreshedAt"] = 9007199254740992L }),
            ("tianyi", new() { ["cookie"] = "synthetic\r\nheader" }),
            ("tianyi", new() { ["password"] = "synthetic\0password" }),
            ("baidu", new() { ["cookie"] = true })
        }) await Expect("INVALID_REQUEST", () => store.SaveAsync(provider, credentials));
        Check(fixture.Writes.Count == 0 && fixture.Calls.Count == 0, "invalid types never touch storage or network");
        foreach (string json in new[]
        {
            "{\"version\":\"1\",\"accounts\":{}}", "{\"version\":2,\"accounts\":{}}",
            "{\"version\":1,\"accounts\":[]}",
            "{\"version\":1,\"accounts\":{\"baidu\":{\"revision\":0,\"credentials\":{}}}}",
            "{\"version\":1,\"accounts\":{\"baidu\":{\"revision\":1,\"credentials\":{\"cookie\":2}}}}",
            "{\"version\":1,\"accounts\":{\"baidu\":{\"revision\":1,\"credentials\":{\"cookie\":\"a\",\"cookie\":\"b\"}}}}",
            "{\"version\":1,\"accounts\":{\"baidu\":{\"revision\":9007199254740992,\"credentials\":{}}}}"
        })
        {
            fixture.SetDocument(json);
            if (!json.Contains("baidu")) await Expect("CORRUPT_STORAGE", () => store.ReadAsync(default));
            else
            {
                var result = (await store.ReadAsync(default)).Single(account => account.Provider == "baidu");
                Check(result is { ErrorCode: "CORRUPT_STORAGE", Revision: null, Configured: null, Status: null, Fields: null },
                    "bad account material is an explicit error, not a successful signed-out account");
            }
            if (!json.Contains("cookie")) await Expect("CORRUPT_STORAGE", () => store.ClearAsync("baidu"));
            else Check((await store.ClearAsync("baidu")).Configured == false, "explicit clear can remove malformed consumed credentials");
        }
        fixture.SetDocument("{\"version\":1,\"accounts\":{\"baidu\":{\"revision\":9007199254740991,\"credentials\":{}}}}");
        await Expect("STORAGE_ERROR", () => store.ClearAsync("baidu"));
    }

    private static async Task RawInputs()
    {
        var fixture = new PanAccountFixture { Offline = true };
        fixture.SetDocument("""
            {"version":1,"future":1e400,"future":9007199254740993,"accounts":{
             "baidu":{"revision":2,"credentials":{"cookie":"synthetic","future":9007199254740993,"future":1e400},"extra":1e400},
             "quark":{"revision":"broken","credentials":false},"future":{"anything":1e400}}}
            """);
        var before = fixture.Saved;
        var store = new PanSettingsStore(fixture, fixture);
        Check((await store.ReadAsync("baidu", default)).Revision == 2, "single account read does not consume unrelated accounts");
        var saved = await store.SaveAsync("baidu", new Dictionary<string, object>
            { ["cookie"] = "synthetic-new", ["unused"] = new object() });
        var after = fixture.Saved;
        Check(saved.Revision == 3 && saved.Configured == true && after.EnumerateObject().Count(item => item.NameEquals("future")) == 2,
            "extra input is not a credential and root extensions remain exact");
        foreach (string name in new[] { "quark", "future" })
            Check(before.GetProperty("accounts").GetProperty(name).GetRawText() == after.GetProperty("accounts").GetProperty(name).GetRawText(),
                "unedited accounts are never rewritten or validated");
        var credentials = after.GetProperty("accounts").GetProperty("baidu").GetProperty("credentials");
        Check(credentials.EnumerateObject().Count(item => item.NameEquals("future")) == 2 && !credentials.TryGetProperty("unused", out _) &&
            credentials.GetProperty("future").GetRawText() == "1e400", "known replacement keeps stored opaque extensions without sending new unknown input");
        await store.ClearAsync("baidu");
        Check((await store.ReadAsync("baidu", default)).Configured == false &&
            fixture.Saved.GetProperty("accounts").GetProperty("baidu").GetProperty("credentials").EnumerateObject().Count() == 2,
            "clear retires actual credentials without erasing extensions");
    }

    private static Task ErrorSnapshots()
    {
        var error = PanAccountSnapshot.Read(BridgeCodec.Value(new { provider = "baidu", errorCode = "CORRUPT_STORAGE", future = true }));
        var wire = JsonSerializer.SerializeToElement(error, Json);
        Check(wire.EnumerateObject().Select(item => item.Name).SequenceEqual(["provider", "errorCode"]) &&
            error is { Revision: null, Configured: null, Status: null, Fields: null },
            "error snapshots never fabricate or serialize success fields");
        foreach (string invalid in new[]
        {
            """{"provider":"unknown","errorCode":"CORRUPT_STORAGE"}""",
            """{"provider":"baidu","provider":"quark","errorCode":"CORRUPT_STORAGE"}""",
            """{"provider":"baidu","errorCode":"CORRUPT_STORAGE","errorCode":"CORRUPT_STORAGE"}""",
            """{"provider":"baidu","errorCode":"private-message"}""",
            """{"provider":"baidu","errorCode":"CORRUPT_STORAGE","revision":0}""",
            """{"provider":"baidu","errorCode":"CORRUPT_STORAGE","configured":false}""",
            """{"provider":"baidu","errorCode":"CORRUPT_STORAGE","status":"signedOut"}""",
            """{"provider":"baidu","errorCode":"CORRUPT_STORAGE","fields":[]}""",
            """{"provider":"baidu","revision":1,"revision":2,"configured":true,"status":"signedIn","fields":["cookie"]}"""
        })
            BridgeTests.Throws("INVALID_RESPONSE", () => PanAccountSnapshot.Read(BridgeCodec.DecodeJSONValue(System.Text.Encoding.UTF8.GetBytes(invalid))));
        var row = new PanAccountRow(PanSettingsStore.Providers[0]) { Active = true };
        row.Publish(new("baidu", 7, true, "signedIn", ["cookie"]));
        row.Publish(error);
        Check(row.Revision is null && row.Status is null && row.ErrorCode == "CORRUPT_STORAGE" &&
            row.StatusText == "凭据损坏" && !row.CanEdit && !row.CanLogin && !row.CanRefresh && !row.CanLogout,
            "an unversioned error replaces the old presentation without inventing a newer or empty account");
        BridgeTests.Throws("INVALID_RESPONSE", () => row.Publish(PanAccountSnapshot.Corrupt("quark")));
        row.Publish(new("baidu", 8, true, "signedIn", ["cookie"]));
        row.Publish(new("baidu", 7, false, "signedOut", []));
        Check(row.Revision == 8 && row.ErrorCode is null && row.CanEdit && row.Status == "signedIn",
            "a real reread recovers the row and older healthy revisions still cannot replace it");
        return Task.CompletedTask;
    }

    private static async Task IsolatedErrors()
    {
        var fixture = new PanAccountFixture { Offline = true };
        fixture.SetDocument("""
            {"version":1,"accounts":{"baidu":{"revision":2,"credentials":{"cookie":"synthetic"}},
             "quark":{"revision":"broken","credentials":false,"future":9007199254740993}}}
            """);
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        var healthy = model.Rows[0]; var broken = model.Rows[1];
        string raw = fixture.Saved.GetProperty("accounts").GetProperty("quark").GetRawText();
        Check(!model.LoadFailed && model.Notice.Length == 0 && healthy.CanEdit && broken.HasSnapshot &&
            broken.ErrorCode == "CORRUPT_STORAGE" && broken.Revision is null && broken.Status is null,
            "one bad native account is visible without failing the healthy list or falling back to signed-out");
        await model.OpenCredentialsAsync(broken); await model.OpenLoginAsync(broken);
        await model.RefreshAsync(broken); await model.LogoutAsync(broken);
        Check(!model.LoginOpen && fixture.Calls.Count == 0 && fixture.Writes.Count == 0, "all error-row entry points reject account operations");
        await Expect("CORRUPT_STORAGE", () => new PanSettingsStore(fixture, fixture).ClearAsync("quark"));
        await Expect("CORRUPT_STORAGE", () => new PanSettingsStore(fixture, fixture).SaveAsync("quark",
            new Dictionary<string, object> { ["cookie"] = "synthetic-replacement" }));
        await model.OpenCredentialsAsync(healthy);
        await model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-new" });
        Check(healthy.Revision == 3 && fixture.Writes.Count == 1 &&
            fixture.Saved.GetProperty("accounts").GetProperty("quark").GetRawText() == raw,
            "a healthy native CAS receipt cannot rewrite or repair the bad sibling");

        fixture.SetDocument("""{"version":1,"accounts":{"baidu":{"revision":0,"credentials":{}}}}""");
        var stale = fixture.Snapshot();
        fixture.Account("baidu", 3, "signedIn", "cookie");
        var held = Gate<DocumentSnapshot>();
        fixture.ReadOverride = _ => { fixture.ReadOverride = null; return held.Task; };
        var read = model.ReloadAsync();
        await model.OpenCredentialsAsync(healthy);
        await model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-current" });
        held.SetResult(stale); await read;
        Check(healthy.Revision == 4 && healthy.ErrorCode is null && healthy.Status == "signedIn",
            "a delayed unversioned error is fenced by the row operation generation");

        fixture.SetDocument("""{"version":1,"accounts":{"baidu":{"revision":0,"credentials":{}}}}""");
        await model.ReloadAsync();
        Check(healthy.ErrorCode == "CORRUPT_STORAGE" && healthy.Revision is null && !healthy.CanEdit,
            "a current reread exposes corruption instead of retaining an earlier signed-in snapshot");
        fixture.Account("baidu", 5, "signedIn", "cookie");
        await model.ReloadAsync();
        Check(healthy.Revision == 5 && healthy.ErrorCode is null && healthy.CanEdit && fixture.Calls.Count == 0,
            "recovery requires real stored account material without an automatic network request");
    }

    private static async Task SaveFailures()
    {
        var fixture = new PanAccountFixture();
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        var row = model.Rows.Single(row => row.Provider == "yidong");
        await model.OpenCredentialsAsync(row);
        await model.SaveAsync(new Dictionary<string, string> { ["authorization"] = "synthetic-auth", ["unknown"] = "synthetic" });
        Check(fixture.Writes.Count == 0 && model.CanSave && !row.Configured, "unknown draft fields are rejected rather than silently discarded");
        foreach (string number in new[] { "-1", "1.5", "9007199254740992", "not-a-number" })
            await model.SaveAsync(new Dictionary<string, string> { ["refreshedAt"] = number });
        Check(fixture.Writes.Count == 0 && model.CanSave, "invalid manual numbers leave the editor usable");
        fixture.WriteOverride = (_, _, _) => throw new DocumentStoreException(DocumentStoreError.Protection);
        await model.SaveAsync(new Dictionary<string, string> { ["authorization"] = "synthetic-auth" });
        Check(model.LoginOpen && model.CanSave && !row.Configured && model.Notice == "网盘凭据保存失败", "failed commit has no success side effects");
        int attempts = 0;
        fixture.WriteOverride = (bytes, expected, _) =>
        {
            attempts++;
            fixture.Account("baidu", 1, "signedIn", "cookie");
            fixture.Account("yidong", 2, "signedIn", "authorization");
            return Task.FromResult(fixture.Commit(bytes, expected));
        };
        await model.SaveAsync(new Dictionary<string, string> { ["authorization"] = "synthetic-old" });
        Check(model.LoginOpen && model.CanSave && row.Revision == 2 && fixture.Writes.Count == 0 &&
            fixture.Saved.GetProperty("accounts").GetProperty("baidu").GetProperty("revision").GetInt64() == 1,
            "CAS conflict preserves the other account and newer credentials instead of blindly retrying");
        Check(model.Notice == "网盘账号已变更", "conflicts have a fixed non-secret error");
        var accounts = fixture.Saved.GetProperty("accounts");
        Check(attempts == 1 && accounts.GetProperty("baidu").GetProperty("credentials").GetProperty("cookie").GetString() == "synthetic-cookie" &&
            accounts.GetProperty("yidong").GetProperty("credentials").GetProperty("authorization").GetString() == "synthetic-authorization" &&
            fixture.Notifications.Count == 0 && fixture.Calls.Count == 0,
            "conflicts never retry the write, replace newer credentials or notify the runtime");
    }

    private static async Task Receipts()
    {
        foreach (bool clear in new[] { false, true })
        foreach (bool shutdown in new[] { false, true })
        {
            var fixture = new PanAccountFixture();
            fixture.Account("tianyi", 4, "signedIn", "cookie");
            await using var model = new PanAccounts(fixture, fixture);
            await model.ActivateAsync();
            var row = model.Rows.Single(row => row.Provider == "tianyi");
            var entered = Gate<bool>(); var release = Gate<bool>();
            fixture.WriteOverride = async (bytes, expected, token) =>
            {
                fixture.WriteOverride = null;
                Check(!token.CanBeCanceled, "accepted native writes own their receipt after UI cancellation");
                entered.SetResult(true); await release.Task;
                return fixture.Commit(bytes, expected);
            };
            if (!clear) await model.OpenCredentialsAsync(row);
            var mutation = clear ? model.LogoutAsync(row) :
                model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-retired" });
            await entered.Task;
            var retire = shutdown ? model.PrepareShutdownAsync() : model.DeactivateAsync();
            Task successor = Task.CompletedTask;
            if (!shutdown)
            {
                successor = Replace();
                async Task Replace()
                {
                    await model.ActivateAsync();
                    await model.OpenCredentialsAsync(row);
                    await model.SaveAsync(new Dictionary<string, string> { ["username"] = "synthetic-new", ["password"] = "synthetic-password" });
                }
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            bool fenced = !retire.IsCompleted && (shutdown || !successor.IsCompleted) && fixture.Writes.Count == 0;
            release.SetResult(true);
            await Task.WhenAll(mutation, retire, successor);
            Check(fenced && fixture.Calls.Count == 0, "retirement and successors wait for the real commit, without runtime calls");
            var saved = fixture.Saved.GetProperty("accounts").GetProperty("tianyi");
            Check(saved.GetProperty("revision").GetInt64() == (shutdown ? 5 : 6), "accepted old mutation settles before the final replacement");
            if (shutdown) Check(row.Revision == 4 && !model.LoginOpen, "shutdown does not publish retired success");
            else Check(row.Revision == 6 && row.Configured && !model.LoginOpen &&
                saved.GetProperty("credentials").EnumerateObject().Select(field => field.Name).SequenceEqual(["username", "password"]),
                "last native replacement and presentation agree");
        }
    }

    private static async Task Reads()
    {
        var fixture = new PanAccountFixture();
        fixture.ReadOverride = _ => throw new DocumentStoreException(DocumentStoreError.Corrupt);
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        Check(model.LoadFailed && model.Rows.All(row => !row.HasSnapshot && !row.CanEdit), "failed local reads do not fabricate editable state");
        fixture.ReadOverride = null;
        await model.ReloadAsync();
        var row = model.Rows.Single(row => row.Provider == "tianyi");
        var entered = Gate<bool>(); var release = Gate<DocumentSnapshot>();
        fixture.ReadOverride = _ => { fixture.ReadOverride = null; entered.SetResult(true); return release.Task; };
        var read = model.ReloadAsync();
        await entered.Task;
        var old = fixture.Snapshot();
        await model.OpenCredentialsAsync(row);
        await model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-new" });
        release.SetResult(old);
        await read;
        Check(row.Revision == 1 && row.Configured && !model.LoadFailed, "older page reads cannot overwrite a later save");
        var cancelled = Gate<bool>();
        fixture.ReadOverride = async token =>
        {
            cancelled.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return fixture.Snapshot();
        };
        var clear = model.LogoutAsync(row);
        await cancelled.Task;
        await model.DeactivateAsync();
        await clear;
        Check(fixture.Writes.Count == 1 && fixture.Accounts["tianyi"].Revision == 1, "cancel before write admission has no durable clear");
    }

    private static async Task Online()
    {
        var fixture = new PanAccountFixture();
        fixture.Account("baidu", 1, "signedIn", "cookie");
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        var row = model.Rows[0];
        fixture.Overrides["pan.refresh"] = (_, _) =>
        {
            fixture.Account("baidu", 2, "signedIn", "cookie");
            return Task.FromResult(Ok(new { refreshed = true, account = fixture.Accounts["baidu"] }));
        };
        await model.RefreshAsync(row);
        Check(row.Revision == 2 && fixture.Writes.Count == 0, "online success rereads the committed protected document, never HTTP-backfills");
        fixture.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Ok(new
        { refreshed = true, account = PanAccountSnapshot.Corrupt("baidu") }));
        await model.RefreshAsync(row);
        Check(row.Revision == 2 && row.Status == "signedIn" && fixture.Writes.Count == 0 && model.Notice == "网盘凭据保存失败",
            "an error-only HTTP item is not a refreshed receipt or a replacement for healthy native authority");
        fixture.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Ok(new
        { refreshed = true, account = new PanAccountSnapshot("baidu", 1, true, "signedIn", ["cookie"]) }));
        await model.RefreshAsync(row);
        Check(row.Revision == 2 && model.Notice == "网盘账号已变更", "stale HTTP result cannot replace the native account");
        fixture.Overrides["pan.refresh"] = (_, _) =>
        {
            fixture.Account("baidu", 3, "signedIn", "cookie");
            return Task.FromResult(Ok(new { refreshed = true, account = fixture.Accounts["baidu"] }));
        };
        fixture.ReadOverride = _ => throw new DocumentStoreException(DocumentStoreError.Protection);
        await model.RefreshAsync(row);
        Check(row.Revision == 2 && fixture.Writes.Count == 0 && model.Notice == "网盘凭据保存失败",
            "a failed protected reread cannot be bypassed with the HTTP snapshot");
        fixture.ReadOverride = null;
        await model.ReloadAsync();
        Check(row.Revision == 3, "a subsequent local read recovers the real committed account");
    }

    private static async Task Failures()
    {
        var fixture = new PanAccountFixture();
        fixture.Account("baidu", 1, "signedIn", "cookie");
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        var row = model.Rows[0];
        foreach (string code in new[] { "NETWORK_ERROR", "TIMEOUT", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "UPSTREAM_ERROR", "CHALLENGE_REQUIRED" })
        {
            fixture.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure(code));
            await model.RefreshAsync(row);
            Check(row is { Status: "signedIn", Revision: 1, Configured: true }, "ordinary failures do not expire or clear credentials");
        }
        foreach (string code in new[] { "AUTH_REQUIRED", "AUTH_FAILED" })
        {
            fixture.Overrides["pan.refresh"] = (_, _) => Task.FromResult(Failure(code));
            await model.RefreshAsync(row);
            Check(row.Status == "expired" && row.Configured, "explicit current authentication failure expires only presentation");
        }
        fixture.Overrides["pan.refresh"] = (_, _) =>
        {
            fixture.Account("baidu", 2, "signedIn", "cookie");
            return Task.FromResult(Failure("AUTH_FAILED"));
        };
        await model.RefreshAsync(row);
        Check(row.Revision == 2 && row.Status == "signedIn", "failure from old credentials cannot expire a newer native account");
        fixture.Overrides["pan.refresh"] = (_, _) => throw new Exception("synthetic-private-upstream");
        await model.RefreshAsync(row);
        Check(!model.Notice.Contains("synthetic"), "errors never publish raw upstream details");
    }

    private static async Task QrStates()
    {
        var fixture = new PanAccountFixture();
        await using var model = new PanAccounts(fixture, fixture);
        await model.ActivateAsync();
        var row = model.Rows[0];
        await model.OpenLoginAsync(row);
        foreach (string state in new[] { "pending", "scanned", "expired", "cancelled" })
        {
            if (model.QrImage is null) await model.RetryQrAsync();
            fixture.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(fixture.Poll("baidu", state));
            await model.PollLoginAsync();
            Check(model.LoginOpen && !row.Configured, "pending scanned expired and cancelled never count as saved login");
        }
        foreach (string code in new[] { "STORAGE_ERROR", "NETWORK_ERROR", "TIMEOUT", "LOGIN_EXPIRED" })
        {
            await model.RetryQrAsync();
            fixture.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(Failure(code));
            await model.PollLoginAsync();
            Check(model.LoginOpen && !row.Configured && model.CanRetryQr && model.Notice.Length > 0, "failed QR remains retryable without claiming success");
        }
        await model.RetryQrAsync();
        fixture.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(fixture.Poll("baidu", "confirmed",
            new("baidu", 1, true, "signedIn", ["cookie"])));
        await model.PollLoginAsync();
        Check(!row.Configured && model.LoginOpen, "HTTP confirmation without protected storage commit is not authoritative");
        await model.RetryQrAsync();
        fixture.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(fixture.Poll("baidu", "confirmed",
            PanAccountSnapshot.Corrupt("baidu")));
        await model.PollLoginAsync();
        Check(!row.Configured && model.LoginOpen && model.CanRetryQr && fixture.Writes.Count == 0 &&
            model.Notice == "网盘凭据保存失败", "an error item cannot confirm a QR session or fabricate a persisted identity");
        await model.RetryQrAsync();
        fixture.Overrides["pan.login.poll"] = (_, _) =>
        {
            fixture.Account("baidu", 1, "signedIn", "cookie");
            return Task.FromResult(fixture.Poll("baidu", "confirmed", fixture.Accounts["baidu"]));
        };
        string confirmed = fixture.LastSession;
        await model.PollLoginAsync(); await model.CloseLoginAsync();
        Check(!model.LoginOpen && row is { Revision: 1, Configured: true } && !fixture.Cancelled.Contains(confirmed) &&
            fixture.Writes.Count == 0, "only native committed QR succeeds; it is never written back from HTTP");
        Check(fixture.Calls.Where(call => call.Method == "pan.login.poll").All(call => call.Timeout == TimeSpan.FromSeconds(45)),
            "Baidu polls retain their single 45-second deadline");
        await model.OpenLoginAsync(model.Rows[1]);
        fixture.Overrides["pan.login.poll"] = (_, _) => Task.FromResult(fixture.Poll("quark", "pending"));
        await model.PollLoginAsync();
        Check(fixture.Calls.Last(call => call.Method == "pan.login.poll").Timeout is null, "other providers retain the default timeout");
    }

    private static async Task LateStart()
    {
        foreach (bool shutdown in new[] { false, true })
        {
            var fixture = new PanAccountFixture();
            var entered = Gate<bool>(); var release = Gate<JsonElement>();
            fixture.Overrides["pan.login.start"] = (_, _) => { entered.SetResult(true); return release.Task; };
            await using var model = new PanAccounts(fixture, fixture);
            await model.ActivateAsync();
            var start = model.OpenLoginAsync(model.Rows[0]);
            await entered.Task;
            Task retire = shutdown ? model.PrepareShutdownAsync() : model.DeactivateAsync();
            Task save = Task.CompletedTask;
            if (!shutdown)
            {
                await model.ActivateAsync();
                await model.OpenCredentialsAsync(model.Rows[0]);
                save = model.SaveAsync(new Dictionary<string, string> { ["cookie"] = "synthetic-new" });
                await save;
                Check(model.Rows[0].Configured && fixture.Writes.Count == 1, "local save does not wait for a hung QR request");
                await model.LogoutAsync(model.Rows[0]);
            }
            Check(!retire.IsCompleted && (shutdown || fixture.Writes.Count == 2 && !model.Rows[0].Configured),
                "local settings commit independently of the late QR startup; retirement still drains its cleanup");
            release.SetResult(fixture.Start("baidu", "late-session"));
            await Task.WhenAll(start, retire, save);
            Check(fixture.Cancelled.SequenceEqual(["late-session"]) && (shutdown || !model.Rows[0].Configured),
                "the late challenge is cancelled without holding local settings hostage");
        }
    }

    private static async Task LateResults()
    {
        foreach (bool qr in new[] { true, false })
        {
            var fixture = new PanAccountFixture();
            if (!qr) fixture.Account("baidu", 1, "signedIn", "cookie");
            await using var model = new PanAccounts(fixture, fixture);
            await model.ActivateAsync();
            var row = model.Rows[0];
            if (qr) await model.OpenLoginAsync(row);
            var entered = Gate<bool>(); var release = Gate<JsonElement>();
            bool cancelled = false;
            fixture.Overrides[qr ? "pan.login.poll" : "pan.refresh"] = async (_, token) =>
            {
                using var registration = token.Register(() => cancelled = true);
                entered.SetResult(true);
                return await release.Task;
            };
            var operation = qr ? model.PollLoginAsync() : model.RefreshAsync(row);
            await entered.Task;
            int calls = fixture.Calls.Count;
            if (!qr) { await model.RefreshAsync(row); await model.LogoutAsync(row); Check(fixture.Calls.Count == calls, "busy rows exclude duplicate mutations"); }
            var leave = model.DeactivateAsync();
            fixture.Account("baidu", 2, "signedOut");
            var read = model.ActivateAsync();
            release.SetResult(qr ? fixture.Poll("baidu", "confirmed", new("baidu", 1, true, "signedIn", ["cookie"])) :
                Ok(new { refreshed = true, account = new PanAccountSnapshot("baidu", 1, true, "signedIn", ["cookie"]) }));
            await Task.WhenAll(operation, leave, read);
            Check(cancelled && row is { Revision: 2, Configured: false, Status: "signedOut" }, "late results do not replace newer native clears");
        }
    }

    private static Task QrImages()
    {
        var text = PanQrImage.Create("url", "http://arbitrary.fixture.invalid:8000/扫码?token=synthetic#payload");
        Check(text is { IsFrozen: true, Format: var format } && format == PixelFormats.Gray8, "provider URL is encoded without requesting it");
        var pixels = new byte[text.PixelWidth * text.PixelHeight];
        text.CopyPixels(pixels, text.PixelWidth, 0);
        Check(pixels.Contains((byte)0) && pixels.Contains((byte)255), "real QR pixels are produced");
        var image = PanQrImage.Create("image", PixelDataUri);
        Check(image is { IsFrozen: true, PixelWidth: 1, PixelHeight: 1 } && image.Format == PixelFormats.Bgra32, "data URI decodes into detached local pixels");
        try { PanQrImage.Create("image", "https://image.fixture.invalid/code.png"); throw new Exception("Expected data URI"); }
        catch (BridgeException) { }
        return Task.CompletedTask;
    }
    internal const string PixelDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=";
    private static async Task Expect(string code, Func<Task> run)
    {
        try { await run(); throw new InvalidOperationException("Expected " + code); }
        catch (BridgeException error) { Check(error.Code == code, "fixed expected error code"); }
    }
}
