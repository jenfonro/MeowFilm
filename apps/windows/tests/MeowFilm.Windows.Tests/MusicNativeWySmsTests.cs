// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Core.Runtime;
using static MeowFilm.Windows.Tests.MusicAccountRuntimeFixture;
namespace MeowFilm.Windows.Tests;
public static class MusicNativeWySmsTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string platform in new[] { "wy", "kg" })
        {
            string p = platform;
            yield return Case(p + "-explicit-send-then-login-current-CAS", async () => {
                await using var f = new MusicAccountRuntimeFixture(p);
                var sent = Ok(await f.Send()); Check(sent.GetProperty("sent").GetBoolean() && sent.GetProperty("retryAfter").GetInt32() == 60 && f.Store.Writes == 0);
                Ok(await f.SmsLogin()); Check(f.Store.Writes == 1 && (await f.Owner.ReadDetailsAsync(p)).Membership.Vip == true);
                Check(f.Runtime.Requests.Where(call => call.Operation.StartsWith("sms.", StringComparison.Ordinal)).Select(call => call.Operation).SequenceEqual(["sms.send", "sms.login"]));
                var input = f.Runtime.Requests.Single(call => call.Operation == "sms.login").Parameters;
                Check(input.GetProperty("phone").GetString() == Phone && input.GetProperty("captcha").GetString() == Captcha);
            });
            yield return Case(p + "-existing-code-does-not-send-or-inherit-old-grant", async () => {
                await using var f = new MusicAccountRuntimeFixture(p); Ok(await f.Import());
                var old = await f.Owner.AcquireAsync(p); Ok(await f.SmsLogin());
                Check(!f.Owner.IsCurrent(old!) && f.Store.Writes == 2 && !f.Runtime.Requests.Any(call => call.Operation == "sms.send"));
            });
            yield return Case(p + "-send-never-deletes-existing-saved-credentials", async () => {
                await using var f = new MusicAccountRuntimeFixture(p); Ok(await f.Import());
                Ok(await f.Send()); Check(f.Store.Writes == 1 && await f.Owner.AcquireAsync(p) is not null);
            });
            yield return Case(p + "-pre-cancel-send-and-login-zero-runtime-zero-write", async () => {
                await using var f = new MusicAccountRuntimeFixture(p);
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                Check(Code(await f.Send(cancel.Token)) == "CANCELLED" && Code(await f.SmsLogin(cancel.Token)) == "CANCELLED");
                Check(f.Runtime.Calls.Count == 0 && f.Store.Writes == 0);
            });
            foreach (string code in new[] { "SMS_SEND_UNCONFIRMED", "RATE_LIMITED", "INVALID_RESPONSE", "AUTH_EXPIRED", "VERIFICATION_REQUIRED", "TIMEOUT", "NETWORK_ERROR" })
            {
                string c = code;
                yield return Case(p + "-canonical-error-" + c + "-no-auto-retry", async () => {
                    await using var f = new MusicAccountRuntimeFixture(p) { FailureCode = c };
                    Check(Code(await f.Send()) == c &&
                        f.Runtime.Requests.Where(call => call.Operation != "qr.cancel").Select(call => call.Operation).SequenceEqual(["sms.send"]) &&
                        f.Store.Writes == 0);
                });
            }
            yield return Case(p + "-cancel-login-no-late-handoff-physical-close", async () => {
                await using var f = new MusicAccountRuntimeFixture(p) { HoldOperation = "sms.login" };
                var login = f.SmsLogin(); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Ok(await f.Owner.CallAsync("music.accounts.cancel", new { platform = p }));
                await f.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); var close = f.Owner.DisposeAsync().AsTask();
                Check(!close.IsCompleted); f.Release.TrySetResult(); Check(!((await login).GetProperty("ok").GetBoolean())); await close;
                Check(f.Store.Writes == 0);
            });
            yield return Case(p + "-dispatched-send-settles-while-cancel-and-close-drain-without-resend", async () => {
                await using var f = new MusicAccountRuntimeFixture(p) { HoldOperation = "sms.send" };
                using var cancel = new CancellationTokenSource();
                var send = f.Send(cancel.Token); await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task close;
                try
                {
                    cancel.Cancel(); close = f.Owner.DisposeAsync().AsTask();
                    Check(!send.IsCompleted && !close.IsCompleted && !f.Cancelled.Task.IsCompleted);
                }
                finally { f.Release.TrySetResult(); }
                Check(Ok(await send).GetProperty("sent").GetBoolean()); await close;
                Check(f.Runtime.Requests.Count(call => call.Operation == "sms.send") == 1 && f.Store.Writes == 0 && f.Store.Disposals == 0);
            });
            foreach (string kind in new[] { "lost-exchange", "malformed-envelope", "empty-error-code", "missing-confirmation" })
            {
                string resultKind = kind;
                yield return Case(p + "-dispatched-" + resultKind + "-cancel-close-remains-unconfirmed-no-resend", async () => {
                    await using var f = new MusicAccountRuntimeFixture(p) { HoldOperation = "sms.send" };
                    f.Runtime.OnRequest = async (call, token) =>
                    {
                        var value = await f.Respond(call, token);
                        if (call.Operation != "sms.send") return value;
                        if (resultKind == "lost-exchange") throw new BridgeException("NETWORK_ERROR");
                        return resultKind == "missing-confirmation" ? MusicPlatformRuntimeFixture.Json(new { retryAfterSeconds = 60 }) : value;
                    };
                    f.Runtime.OnResponse = (call, reply) => call.Method != "music.platform.request" || call.Operation != "sms.send" ? reply :
                        resultKind == "malformed-envelope" ? MusicPlatformRuntimeFixture.Json(new { ok = true, contract = "music.platform.v1" }) :
                        resultKind == "empty-error-code" ? MusicPlatformRuntimeFixture.Json(new { ok = false, contract = "music.platform.v1", code = "" }) : reply;
                    using var cancel = new CancellationTokenSource();
                    var send = f.Send(cancel.Token);
                    Task close;
                    try
                    {
                        await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        cancel.Cancel(); close = f.Owner.DisposeAsync().AsTask();
                        Check(!send.IsCompleted && !close.IsCompleted && !f.Cancelled.Task.IsCompleted);
                    }
                    finally { f.Release.TrySetResult(); }
                    var result = await send;
                    Check(Code(result) == "SMS_SEND_UNCONFIRMED" && result.GetProperty("retryAfter").GetInt32() == 60);
                    await close;
                    Check(f.Runtime.Requests.Count(call => call.Operation == "sms.send") == 1 && f.Store.Writes == 0 && f.Store.Disposals == 0);
                });
            }
            yield return Case(p + "-SMS-current-DPAPI-cold-reopen-does-not-replay-login", async () => {
                string root;
                await using (var f = new MusicAccountRuntimeFixture(p)) { root = f.Root; Ok(await f.SmsLogin()); Check(f.Store.Writes == 1); }
                await using var cold = new MusicAccountRuntimeFixture(p, root);
                Check((await cold.Owner.ReadDetailsAsync(p)).State == "saved" && cold.Runtime.Requests.Count == 0);
                Ok(await cold.Owner.CallAsync("music.accounts.validate", new { platform = p }));
                Check((await cold.Owner.ReadDetailsAsync(p)).State == "verified" && !cold.Runtime.Requests.Any(call => call.Operation.StartsWith("sms.", StringComparison.Ordinal)));
            });
            yield return Case(p + "-other-owner-logout-fences-SMS-CAS", async () => {
                await using var f = new MusicAccountRuntimeFixture(p); Ok(await f.Import());
                await using var other = new MusicAccountRuntimeFixture(p, f.Root);
                f.BeforeResponse = async () => Ok(await other.Owner.CallAsync("music.accounts.logout", new { platform = p }));
                Check(Code(await f.SmsLogin()) == "STORAGE_CONFLICT" && f.Store.Writes == 1 && await f.Owner.AcquireAsync(p) is null);
            });
            yield return Case(p + "-lost-durable-receipt-no-login-replay", async () => {
                await using var f = new MusicAccountRuntimeFixture(p); f.Store.LoseReceipt = true;
                Check(Code(await f.SmsLogin()) == "HANDOFF_UNCONFIRMED" && f.Store.Writes == 1 && await f.Owner.AcquireAsync(p) is null);
                Check(Code(await f.SmsLogin()) == "COMMIT_UNKNOWN" && f.Runtime.Requests.Count(call => call.Operation == "sms.login") == 1);
                Ok(await f.Owner.CallAsync("music.accounts.list")); Check(await f.Owner.AcquireAsync(p) is not null);
            });
            yield return Case(p + "-CAS-conflict-not-unknown", async () => {
                await using var f = new MusicAccountRuntimeFixture(p); f.Store.Conflict = true;
                Check(Code(await f.SmsLogin()) == "STORAGE_CONFLICT" && f.Store.Writes == 1 && await f.Owner.AcquireAsync(p) is null);
            });
            yield return Case(p + "-cancel-admitted-CAS-drains-before-explicit-read", async () => {
                await using var f = new MusicAccountRuntimeFixture(p); f.Store.HoldWrite = true;
                var login = f.SmsLogin(); await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Ok(await f.Owner.CallAsync("music.accounts.cancel", new { platform = p }));
                Check(Code(await login) == "HANDOFF_UNCONFIRMED"); var read = f.Owner.CallAsync("music.accounts.list"); Check(!read.IsCompleted);
                f.Store.Release.TrySetResult(); Ok(await read); Check(f.Store.Writes == 1 && await f.Owner.AcquireAsync(p) is not null);
            });
        }
        yield return Case("storage-read-failure-prevents-send", async () => {
            await using var f = new MusicAccountRuntimeFixture("wy"); f.Store.FailRead = true;
            Check(Code(await f.Send()) == "STORAGE_UNAVAILABLE" && f.Runtime.Requests.All(call => call.Operation == "qr.cancel"));
        });
        yield return Case("missing-or-unconvertible-native-input-and-secret-DTOs", async () => {
            await using var f = new MusicAccountRuntimeFixture("wy");
            foreach (object input in new object[] { new { platform = "kw", phone = Phone }, new { platform = "wy" },
                new { platform = "wy", phone = Phone, countryCode = 86 },
                new { platform = "wy", phone = 10000000000L } })
                Check(Code(await f.Owner.CallAsync("music.accounts.sms.send", input)) == "INVALID_REQUEST");
            Check(Code(await f.Owner.CallAsync("music.accounts.sms.login", new { platform = "wy", phone = Phone })) == "INVALID_REQUEST");
            Check(f.Runtime.Requests.Count == 0 && f.Store.Writes == 0);
            var secret = new MusicAccountSmsInput("wy", Phone, "86", Captcha);
            Check(JsonSerializer.Serialize(secret) == "{}" && !secret.ToString().Contains(Phone));
        });
        yield return Case("SMS-uses-required-strings-and-JSON-last-field-without-extra-parameter-gates", async () => {
            await using var f = new MusicAccountRuntimeFixture("wy");
            Ok(await f.Owner.CallAsync("music.accounts.sms.send", new { platform = "wy", phone = "12345", countryCode = "++86",
                url = "https://unused.invalid", headers = new { cookie = "unused" } }));
            var first = f.Runtime.Requests.Single(call => call.Operation == "sms.send").Parameters;
            Check(first.GetProperty("phone").GetString() == "12345" && first.GetProperty("countryCode").GetString() == "++86" &&
                !first.TryGetProperty("url", out _) && !first.TryGetProperty("headers", out _));
            using var duplicate = JsonDocument.Parse("{\"platform\":\"wy\",\"phone\":\"10000000000\",\"phone\":\"10000000001\"}");
            Ok(await f.Owner.ExecuteAsync("music.accounts.sms.send", duplicate.RootElement));
            Check(f.Runtime.Requests.Last(call => call.Operation == "sms.send").Parameters.GetProperty("phone").GetString() == "10000000001" && f.Store.Writes == 0);
        });
        yield return Case("QR-and-SMS-share-native-epoch-not-challenge", async () => {
            await using var f = new MusicAccountRuntimeFixture("wy"); string id = Id(Ok(await f.Begin()));
            Ok(await f.Send()); Check(Ok(await f.Poll(id)).GetProperty("status").GetString() == "expired");
            Check(f.Store.Writes == 0 && !f.Runtime.Requests.Any(call => call.Operation == "qr.poll"));
        });
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.native.accounts.sms." + name, run);
    internal static string Root() { string root = TestWorkspace.DataPath("wy-sms", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    internal static async Task Until(Func<bool> ready)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(10, deadline.Token); }
}
