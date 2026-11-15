// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

// Anonymous device/QIMEI/session persistence and all wire recipes belong to Node.
// These tests exercise the remaining native authority and borrowed-runtime boundary.
public static class MusicQqFeedbackContextTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("current DPAPI account reopens without publishing a native device document", async () => {
            string root;
            await using (var f = new MusicAccountRuntimeFixture("tx")) {
                root = f.Root; Ok(await f.Import()); var account = (await f.Owner.AcquireAsync("tx"))!;
                f.Runtime.OnRequest = (call, _) => Task.FromResult(call.Operation switch {
                    "feedback.prepare" => MusicPlatformRuntimeFixture.Json(new { sessionId = "synthetic-node-context" }),
                    "feedback.release" => MusicPlatformRuntimeFixture.Json(new { released = true }), _ => throw new InvalidOperationException(call.Operation) });
                await using var protocol = new NativeMusicFeedbackProtocol(f.Owner, f.Runtime.Client);
                await using (var session = await protocol.StartAsync(account, Start())) { }
                Check(f.Store.Writes == 1);
                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("SYNTHETIC", StringComparison.Ordinal));
            }
            await using var cold = new MusicAccountRuntimeFixture("tx", root);
            var current = await cold.Owner.AcquireAsync("tx"); Check(current is not null && cold.Runtime.Requests.Count == 0 && cold.Store.Writes == 0);
            string dto = (await cold.Owner.CallAsync("music.accounts.list")).GetRawText(); Check(!dto.Contains("SYNTHETIC", StringComparison.Ordinal));
        });
        yield return Case("only exact issued account can acquire a Node feedback context", async () => {
            await using var f = new MusicAccountRuntimeFixture("tx"); Ok(await f.Import()); var account = (await f.Owner.AcquireAsync("tx"))!;
            await using var protocol = new NativeMusicFeedbackProtocol(f.Owner, f.Runtime.Client); int before = f.Runtime.Requests.Count;
            await Error(() => protocol.StartAsync(new("tx", account.Scope, account.Generation, account.UserId, account.Cookie), Start()), "SESSION_CHANGED");
            Check(before == f.Runtime.Requests.Count && f.Store.Writes == 1);
        });
        yield return Case("logout retires old context admission without rebuilding a native device", async () => {
            await using var f = new MusicAccountRuntimeFixture("tx"); Ok(await f.Import()); var account = (await f.Owner.AcquireAsync("tx"))!;
            await using var protocol = new NativeMusicFeedbackProtocol(f.Owner, f.Runtime.Client); Ok(await f.Owner.CallAsync("music.accounts.logout", new { platform = "tx" }));
            int before = f.Runtime.Requests.Count; await Error(() => protocol.StartAsync(account, Start()), "SESSION_CHANGED");
            Check(f.Runtime.Requests.Count == before && await f.Owner.AcquireAsync("tx") is null);
        });
        foreach (string code in new[] { "AUTH_EXPIRED", "IDENTITY_MISMATCH", "VERIFICATION_REQUIRED", "INVALID_RESPONSE", "CONFLICT", "COMMIT_UNKNOWN", "NETWORK_ERROR", "TIMEOUT", "LIMIT" })
            yield return Case("finite canonical context error never retries " + code, async () => {
                await using var runtime = new MusicPlatformRuntimeFixture(); var account = new MusicPlatformSession("tx", "synthetic-scope", 1, "42", "musicid=42; musickey=SYNTHETIC");
                runtime.Sessions.Current = account; runtime.OnRequest = (_, _) => throw new MusicPlatformException(code);
                await using var protocol = new NativeMusicFeedbackProtocol(runtime.Sessions, runtime.Client);
                await Error(() => protocol.StartAsync(account, Start()), code); Check(runtime.Requests.Count == 1);
            });
        yield return Case("context cancellation retains physical prepare at the owning protocol", async () => {
            await using var runtime = new MusicPlatformRuntimeFixture(); var account = new MusicPlatformSession("tx", "synthetic-scope", 1, "42", "musicid=42; musickey=SYNTHETIC"); runtime.Sessions.Current = account;
            var entered = Signal(); var cancelled = Signal(); var release = Signal();
            runtime.OnRequest = async (call, token) => {
                if (call.Operation == "feedback.release") return MusicPlatformRuntimeFixture.Json(new { released = true });
                using var registration = token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await release.Task;
                return MusicPlatformRuntimeFixture.Json(new { sessionId = "late-node-context" });
            };
            await using var protocol = new NativeMusicFeedbackProtocol(runtime.Sessions, runtime.Client); using var stop = new CancellationTokenSource();
            var acquire = protocol.StartAsync(account, Start(), stop.Token);
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var close = protocol.DisposeAsync().AsTask(); Check(!close.IsCompleted && !acquire.IsCompleted); release.TrySetResult();
                try { await acquire; throw new InvalidOperationException("Expected cancellation or close"); }
                catch (OperationCanceledException) { }
                catch (MusicFeedbackException error) when (error.Code == "CLOSED") { }
                await close;
            } finally { release.TrySetResult(); }
        });
    }
    private static MusicFeedbackStart Start() => new(new(), new("tx", "synthetic-mid"), 240, 0, 1, false);
    private static TestCase Case(string name, Func<Task> run) => new("music.qq.context." + name, run);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("QQ native authority invariant"); }
    private static void Ok(System.Text.Json.JsonElement result) => Check(result.GetProperty("ok").GetBoolean());
    private static async Task Error(Func<Task> action, string code) {
        try { await action(); } catch (MusicFeedbackException error) when (error.Code == code) { Check(error.InnerException is null && error.Message == code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
