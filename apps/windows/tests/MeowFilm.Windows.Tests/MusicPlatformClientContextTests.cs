// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Windows.Tests.MusicNativeHttpSeamTests;
namespace MeowFilm.Windows.Tests;
public static class MusicPlatformClientContextTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music platform publishes guest then current native account then guest without native device state", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { entries = new object[] { } }));
            await f.Catalog.HomeAsync(new("wy")); await f.Catalog.HomeAsync(new("wy"));
            Check(f.Calls.Count(c => c.Method == "music.platform.session.update") == 1);
            f.Sessions.Current = new("wy", "account-scope", 3, "9007199254740997", "MUSIC_U=SYNTHETIC_CURRENT");
            await f.Catalog.HomeAsync(new("wy"));
            var request = f.Requests.Last().Payload;
            Check(request.GetProperty("expectedSession").GetProperty("generation").GetInt64() == 3 && !request.TryGetProperty("candidate", out _));
            f.Sessions.Clear("wy"); f.Client.Invalidate("wy"); await f.Client.DrainAsync(); await f.Catalog.HomeAsync(new("wy"));
            Check(f.Requests.Last().Payload.GetProperty("expectedSession").ValueKind == System.Text.Json.JsonValueKind.Null);
            var publications = f.Calls.Where(c => c.Method == "music.platform.session.update").ToArray();
            Check(publications.Length == 3 && publications[0].Payload.GetProperty("session").ValueKind == System.Text.Json.JsonValueKind.Null &&
                publications[1].Payload.GetProperty("session").GetProperty("scope").GetString() == "account-scope");
        });
        yield return new("Music platform candidate authentication never publishes credentials as the active account", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (call, _) =>
            {
                Check(call.Payload.GetProperty("candidate").GetProperty("cookie").GetString() == "MUSIC_U=SYNTHETIC_CANDIDATE");
                return Task.FromResult(Verified("MUSIC_U=SYNTHETIC_AUTHENTICATED"));
            };
            await using var protocol = f.CreateAccountProtocol();
            var verified = await protocol.ValidateAsync("wy", "MUSIC_U=SYNTHETIC_CANDIDATE");
            Check(verified.Cookie == "MUSIC_U=SYNTHETIC_AUTHENTICATED" && verified.Details.Profile.UserId == "42");
            Check(f.Calls.All(call => call.Method == "music.platform.request") && f.Requests.Count == 1);
        });
        yield return new("Music account pre-cancelled candidates and QR creation do not call runtime", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            await using var protocol = f.CreateAccountProtocol();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Failure("CANCELLED", () => protocol.ValidateAsync("wy", "MUSIC_U=SYNTHETIC_CANDIDATE", cancel.Token));
            await Failure("CANCELLED", () => protocol.BeginAsync("wy", cancel.Token));
            Check(f.Calls.Count == 0);
            await protocol.DisposeAsync();
            await f.DisposeAsync();
            Check(f.Calls.Count == 0);
        });
        yield return new("Music untouched account owner closes without creating platform work", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            await using var store = new MusicPlatformRuntimeFixture.MemoryStore();
            await using var owner = f.CreateAccountOwner(store);
            await owner.DisposeAsync();
            await f.DisposeAsync();
            Check(f.Calls.Count == 0);
        });
        yield return new("Music pre-cancelled reads and writes leave no platform scope to reset on close", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Failure("CANCELLED", () => f.Client.RequestAsync("wy", "home", new { }, null, cancel.Token));
            var receipt = await f.Client.RequestAsync("wy", "dailyStyle.save", new { }, null, cancel.Token);
            Check(receipt.GetProperty("status").GetString() == "notSent" &&
                receipt.GetProperty("errorCode").GetString() == "CANCELLED");
            await f.DisposeAsync();
            Check(f.Calls.Count == 0);
        });
        foreach (bool rejectCancel in new[] { false, true })
        {
            bool rejected = rejectCancel;
            yield return new("Music QR cancel physically precedes new begin without replaying " + (rejected ? "old failure" : "old success"), async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture();
                var entered = Signal(); var release = Signal();
                f.OnRequest = async (call, token) =>
                {
                    if (call.Operation == "qr.create") return Challenge(call);
                    Check(call.Operation == "qr.cancel");
                    entered.TrySetResult(); await release.Task;
                    Check(!token.IsCancellationRequested);
                    if (rejected) throw new MusicPlatformException("NETWORK_ERROR");
                    return MusicPlatformRuntimeFixture.Json(new { cancelled = true });
                };
                await using var protocol = f.CreateAccountProtocol();
                var first = await protocol.BeginAsync("wy");
                var cancel = protocol.CancelAsync("wy");
                Task<MusicAccountChallenge> next;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    f.Client.Invalidate("wy");
                    next = protocol.BeginAsync("wy");
                    Check(!cancel.IsCompleted && !next.IsCompleted &&
                        f.Requests.Select(call => call.Operation).SequenceEqual(["qr.create", "qr.cancel"]));
                }
                finally { release.TrySetResult(); }
                if (rejected) await AccountFailure("NETWORK_ERROR", () => cancel);
                else await cancel;
                Check((await next).ChallengeId != first.ChallengeId);
                await f.Client.DrainAsync();
                Check(f.Requests.Select(call => call.Operation).SequenceEqual(["qr.create", "qr.cancel", "qr.create"]));
            });
        }
        foreach (bool resetFailure in new[] { false, true })
        {
            bool failed = resetFailure;
            yield return new("Music delayed reset gates new candidates and QR creation " + (failed ? "without dispatch after failure" : "until its receipt"), async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture();
                f.OnRequest = (call, _) => Task.FromResult(call.Operation switch
                {
                    "qr.create" => Challenge(call),
                    "account.verify" => Verified("MUSIC_U=SYNTHETIC_AUTHENTICATED"),
                    _ => throw new InvalidOperationException(call.Operation)
                });
                await using var protocol = f.CreateAccountProtocol();
                await protocol.BeginAsync("wy");
                var entered = Signal(); var release = Signal();
                int resets = 0;
                f.OnSessionUpdate = async (_, _) =>
                {
                    if (Interlocked.Increment(ref resets) == 1)
                    {
                        entered.TrySetResult(); await release.Task;
                        if (failed) throw new MusicPlatformException("NETWORK_ERROR");
                    }
                    return MusicPlatformRuntimeFixture.Json(new { published = true });
                };
                f.Client.Invalidate("wy");
                Task<MusicAccountVerifiedCredential> verify;
                Task<MusicAccountChallenge> begin;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    verify = protocol.ValidateAsync("wy", "MUSIC_U=SYNTHETIC_CANDIDATE");
                    begin = protocol.BeginAsync("wy");
                    Check(!verify.IsCompleted && !begin.IsCompleted && f.Requests.Count == 1);
                }
                finally { release.TrySetResult(); }
                if (failed)
                {
                    await AccountFailure("NETWORK_ERROR", () => verify);
                    await AccountFailure("NETWORK_ERROR", () => begin);
                    await Failure("NETWORK_ERROR", f.Client.DrainAsync);
                    Check(f.Requests.Count == 1);
                }
                else
                {
                    Check((await verify).Cookie == "MUSIC_U=SYNTHETIC_AUTHENTICATED");
                    await begin; await f.Client.DrainAsync();
                    Check(f.Requests.Count(call => call.Operation == "account.verify") == 1 &&
                        f.Requests.Count(call => call.Operation == "qr.create") == 2);
                }
            });
        }
        yield return new("Music existing client drain also awaits concurrently started final physical reset", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            var entered = Signal(); var release = Signal(); var closingEntered = Signal(); var closingRelease = Signal();
            f.OnRequest = async (_, _) =>
            {
                entered.TrySetResult(); await release.Task;
                return MusicPlatformRuntimeFixture.Json(new { entries = Array.Empty<object>() });
            };
            var read = f.Catalog.HomeAsync(new("wy"));
            Task drain, close;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                drain = f.Client.DrainAsync();
                f.OnSessionUpdate = async (_, _) =>
                {
                    closingEntered.TrySetResult(); await closingRelease.Task;
                    return MusicPlatformRuntimeFixture.Json(new { published = true });
                };
                close = f.Client.DisposeAsync().AsTask();
                await closingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                release.TrySetResult();
                await Failure("CLOSED", () => read);
                await Task.Delay(30);
                Check(!drain.IsCompleted && !close.IsCompleted);
            }
            finally { release.TrySetResult(); closingRelease.TrySetResult(); }
            await Task.WhenAll(drain, close);
            Check(f.Requests.Count == 1);
        });
        yield return new("Music platform replacement fences a late old result and leaves another platform current", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture(); var entered = Signal(); var release = Signal();
            f.Sessions.Set(new("wy", "old", 1, "42", "MUSIC_U=SYNTHETIC_OLD"));
            f.Sessions.Set(new("tx", "qq", 1, "43", "musickey=SYNTHETIC_QQ"));
            f.OnRequest = async (call, _) => { if (call.Platform == "wy") { entered.TrySetResult(); await release.Task; } return MusicPlatformRuntimeFixture.Json(new { }); };
            var old = f.Catalog.HomeAsync(new("wy"));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Sessions.Set(new("wy", "new", 2, "44", "MUSIC_U=SYNTHETIC_NEW"));
                f.Client.Invalidate("wy"); await f.Catalog.SongAsync(new("tx", "mid"));
            }
            finally { release.TrySetResult(); }
            await Failure("STALE_SESSION", () => old); await f.Client.DrainAsync();
            Check(f.Requests.Count == 2);
        });
    }
    private static JsonElement Verified(string cookie)
    {
        var result = JsonNode.Parse(MusicPlatformRuntimeFixture.Observation().GetRawText())!.AsObject();
        result["cookie"] = cookie;
        return MusicPlatformRuntimeFixture.Json(result);
    }
    private static JsonElement Challenge(MusicPlatformRuntimeFixture.Call call) => MusicPlatformRuntimeFixture.Json(new
    {
        challengeId = call.Parameters.GetProperty("challengeId").GetString(), url = "https://qr.invalid/synthetic",
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeMilliseconds(), imageBase64 = (string?)null
    });
    private static async Task AccountFailure(string code, Func<Task> action)
    {
        try { await action(); }
        catch (MusicAccountException error) { Check(error.Code == code && error.InnerException is null); return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
