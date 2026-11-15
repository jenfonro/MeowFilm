// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
namespace MeowFilm.Windows.Tests;

public static class MusicNativeHttpSeamTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("music.native.runtime.malformed-read-envelope-is-invalid-response", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(MusicSongFixture.Online("wy", "canonical-id")));
            f.OnResponse = (call, reply) => call.Method == "music.platform.request"
                ? MusicPlatformRuntimeFixture.Json(new { ok = "invalid", contract = "music.platform.v1", value = new { } }) : reply;
            await Failure("INVALID_RESPONSE", () => f.Catalog.SongAsync(new("wy", "requested-id")));
            Check(f.Requests.Count == 1);
        });
        yield return new("music.native.runtime.malformed-dispatched-write-envelope-is-unknown-without-replay", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            f.OnRequest = (_, token) =>
            {
                Check(!token.CanBeCanceled);
                return Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status = "confirmed" }));
            };
            f.OnResponse = (call, reply) => call.Method == "music.platform.request"
                ? MusicPlatformRuntimeFixture.Json(new { ok = true, contract = "music.platform.v1" }) : reply;
            var result = await f.Client.ReadAsync("wy", "sync.write", new { }, default);
            Check(result.GetProperty("status").GetString() == "unknown" && result.GetProperty("errorCode").GetString() == "INVALID_RESPONSE");
            Check(f.Requests.Count == 1);
            f.Client.Invalidate("wy"); await f.Client.DrainAsync();
            Check(f.Requests.Count == 1);
        });
        foreach (object? code in new object?[] { null, "", 7 })
        {
            object? malformed = code;
            yield return new("music.native.runtime.malformed-rejection-code-" + (malformed is null ? "null" : malformed is string ? "empty" : "type") + "-is-unknown", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture();
                f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { status = "confirmed" }));
                f.OnResponse = (call, reply) => call.Method == "music.platform.request"
                    ? MusicPlatformRuntimeFixture.Json(new { ok = false, contract = "music.platform.v1", code = malformed }) : reply;
                var result = await f.Client.ReadAsync("wy", "sync.write", new { }, default);
                Check(result.GetProperty("status").GetString() == "unknown" && result.GetProperty("errorCode").GetString() == "INVALID_RESPONSE");
                f.Client.Invalidate("wy"); await f.Client.DrainAsync();
                Check(f.Requests.Count == 1);
            });
        }
        yield return new("music.native.runtime.only-wy-sync-prepare-borrows-60-second-deadline", async () =>
        {
            await using var f = new MusicPlatformRuntimeFixture();
            f.OnRequest = (_, _) => Task.FromResult(MusicPlatformRuntimeFixture.Json(new { preparationId = "synthetic-preparation" }));
            foreach (string platform in new[] { "wy", "tx", "kg" })
                foreach (string operation in new[] { "sync.prepare", "sync.read", "song" })
                {
                    await f.Client.ReadAsync(platform, operation, new { }, default);
                    TimeSpan? expected = platform == "wy" && operation == "sync.prepare" ? TimeSpan.FromSeconds(60) : null;
                    Check(f.Requests.Last().Timeout == expected);
                }
            Check(f.Requests.Count == 9 && f.Requests.Count(call => call.Timeout is not null) == 1);
            Check(f.Calls.Where(call => call.Method != "music.platform.request").All(call => call.Timeout is null));
        });
        foreach (string operation in new[] { "sync.write", "dailyStyle.save", "feedback.send", "feedback.upload", "vip.checkIn" })
        {
            string op = operation;
            yield return new("music.native.runtime." + op + ".pre-cancel-is-not-sent", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
                var result = await f.Client.RequestAsync("wy", op, new { }, null, cancel.Token);
                Check(result.GetProperty("status").GetString() == "notSent" && result.GetProperty("errorCode").GetString() == "CANCELLED");
                Check(f.Requests.Count == 0);
            });
            foreach (bool lostExchange in new[] { false, true })
            {
                bool lost = lostExchange;
                yield return new("music.native.runtime." + op + (lost ? ".lost-exchange-is-unknown" : ".envelope-rejection-is-not-sent"), async () =>
                {
                    await using var f = new MusicPlatformRuntimeFixture();
                    f.OnRequest = (_, _) => lost ? throw new BridgeException("NETWORK_ERROR") : throw new MusicPlatformException("FORBIDDEN");
                    var result = await f.Client.ReadAsync("wy", op, new { }, default);
                    Check(result.GetProperty("status").GetString() == (lost ? "unknown" : "notSent")); Check(f.Requests.Count == 1);
                });
            }
            yield return new("music.native.runtime." + op + ".dispatched-cancel-and-revoke-do-not-erase-receipt", async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture(); var entered = Signal(); var release = Signal();
                f.OnRequest = async (_, token) => { Check(!token.CanBeCanceled); entered.TrySetResult(); await release.Task; return MusicPlatformRuntimeFixture.Json(new { status = "confirmed" }); };
                using var cancel = new CancellationTokenSource(); var write = f.Client.ReadAsync("wy", op, new { }, cancel.Token);
                try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); f.Client.Invalidate("wy"); Check(!write.IsCompleted); }
                finally { release.TrySetResult(); }
                Check((await write).GetProperty("status").GetString() == "confirmed"); await f.Client.DrainAsync(); Check(f.Requests.Count == 1);
            });
        }
        foreach (string code in new[] { "NETWORK_ERROR", "TIMEOUT", "LIMIT", "INVALID_RESPONSE", "AUTH_REQUIRED", "FORBIDDEN" })
        {
            string c = code;
            yield return new("music.native.runtime.read-keeps-" + c, async () =>
            {
                await using var f = new MusicPlatformRuntimeFixture(); f.OnRequest = (_, _) => throw new MusicPlatformException(c);
                await Failure(c, () => f.Catalog.SongAsync(new("wy", "id"))); Check(f.Requests.Count == 1);
            });
        }
    }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void Check(bool value) { if (!value) throw new InvalidOperationException("Runtime boundary assertion"); }
    internal static async Task Failure(string code, Func<Task> run)
    {
        try { await run(); }
        catch (MusicPlatformException error) { Check(error.Code == code && error.InnerException is null); return; }
        catch (OperationCanceledException) when (code == "CANCELLED") { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
