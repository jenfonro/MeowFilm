using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Feedback;
using static MeowFilm.Windows.Tests.MusicQqFeedbackIntegrationFixture;

namespace MeowFilm.Windows.Tests;

/// <summary>Default NativeApplication + real account owner + real protocol + DPAPI, synthetic HTTP only.</summary>
public static class MusicQqFeedbackIntegrationTests
{
    private static TestCase Case(string name, Func<Task> run) => new("music-qq-feedback-integration: " + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("default-off-is-passive-and-native", DefaultOff);
        yield return Case("consent-without-account-cannot-create-device", NoAccount);
        yield return Case("default-factory-real-owner-one-settlement", Success);
        yield return Case("no-native-sample-no-listening-credit", NoSample);
        yield return Case("pending-acceptance-and-stall-not-credited", PendingAcceptance);
        yield return Case("warm-context-Finish-live-auth-required", () => LiveFailure("auth"));
        yield return Case("warm-context-Finish-exact-homepage-required", () => LiveFailure("profile"));
        yield return Case("wrong-track-never-falls-back", WrongTrack);
        foreach (bool account in new[] { false, true })
        {
            string action = account ? "account-logout" : "settings-revoke";
            yield return Case(action + "-drains-pending-anonymous-session", () => PendingDevice(account));
            yield return Case(action + "-drains-report-read-and-async-cleanup", () => RevokeResponse(account));
        }
        yield return Case("same-user-replacement-fences-old-track-and-reuses-device", Replacement);
        yield return Case("shutdown-preparation-drains-and-resumes-same-owner", CloseResume);
        yield return Case("cancelled-close-wait-cannot-abandon-physical-work", CancelledCloseWait);
        yield return Case("final-dispose-waits-before-owned-http-not-borrowed-store", FinalDispose);
        yield return Case("DPAPI-cold-reopen-no-replay-live-auth-warm-device", Reopen);
        yield return Case("negative-ACK-is-terminal-without-retry", BadAck);
    }

    private static async Task DefaultOff()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.App.InitializeAsync(); f.Play(); await f.Drain(); await f.Finish();
        Check(f.Handler.Steps.Length == 0 && f.Store.Reads(AccountNamespace) == 0 &&
            f.Store.Writes("music.feedback.v1") == 0 && f.Runtime.Calls == 0);
        Check(!(await f.Store.Fingerprint(AccountNamespace)).Exists);
    }
    private static async Task NoAccount()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Enable("tx"); f.Play(); await f.Drain(); await f.Finish();
        Check(f.Handler.Steps.Length == 0 && f.Store.Writes(AccountNamespace) == 0);
    }
    private static async Task Success()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted();
        Check(f.Handler.Steps.SequenceEqual(["auth", "profile", "qimei", "session", "track"]));
        Check(f.Store.Writes(AccountNamespace) == 4, "Account plus three acknowledged device CAS writes");
        var before = await f.Store.Fingerprint(AccountNamespace);
        for (int i = 0; i < 4; i++) f.Feedback.Started("entry-1", 0, 240);
        await f.Drain(); await f.Listen();
        f.Feedback.Finish(); f.Feedback.Finish(); await f.Drain();
        await f.App.PrepareShutdownAsync(); await f.App.PrepareShutdownAsync();
        Check(f.Handler.Count("auth") == 2 && f.Handler.Count("profile") == 2 && f.Handler.Count("track") == 2 &&
            f.Handler.Count("qimei") == 1 && f.Handler.Count("session") == 1 && f.Handler.Count("vkey") == 1 &&
            f.Handler.Count("report") == 1 && f.Handler.Reports.Count == 1 && f.Feedback.LastError is null);
        Check(before == await f.Store.Fingerprint(AccountNamespace), "Warm Finish cannot churn device/account generations");
        Check(f.Store.Writes("music.feedback.v1") == 1 && f.Store.Writes("music.library.v1") == 0,
            "No durable report/outbox/library side effect");
    }
    private static async Task NoSample()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await Task.Delay(1120);
        await f.Finish();
        Check(f.Handler.Count("report") == 0 && f.Handler.Count("auth") == 1 &&
            f.Feedback.LastError == "FEEDBACK_FAILED", "Wall time without media advancement is not listening");
    }
    private static async Task PendingAcceptance()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); var hold = f.Handler.Hold("track"); f.Play();
        await hold.Wait(); await Task.Delay(1120);
        f.Feedback.Observe("entry-1", 1.05, 240, true);
        hold.Release.TrySetResult(); await f.Drain();
        f.Feedback.Observe("entry-1", 1.05, 240, true);
        await f.Finish();
        Check(f.Handler.Count("report") == 0 && f.Handler.Count("vkey") == 0,
            "Protocol acceptance delay and a stalled sample must not be credited");
    }
    private static async Task LiveFailure(string fault)
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await f.Listen();
        int writes = f.Store.Writes(AccountNamespace);
        f.Handler.Fault = fault; await f.Finish();
        Check(f.Handler.Count("auth") == 2 && f.Handler.Count("vkey") == 0 && f.Handler.Count("report") == 0 &&
            f.Store.Writes(AccountNamespace) == writes, "Cached device cannot authenticate Finish");
        Check(await f.AccountState() == "revoked", "Real owner invalidation reaches the application");
        int requests = f.Handler.Steps.Length;
        f.Play("after-revocation"); await f.Drain(); await f.Finish();
        Check(f.Handler.Steps.Length == requests, "No automatic reauthentication/retry");
        AssertRedacted(f.Feedback.LastError ?? "");
    }
    private static async Task WrongTrack()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); f.Handler.Fault = "track";
        f.Play(); await f.Drain(); await f.Finish();
        Check(f.Handler.Count("track") == 1 && f.Handler.Count("vkey") == 0 &&
            f.Handler.Count("report") == 0 && f.Feedback.LastError == "FEEDBACK_FAILED");
    }
    private static async Task Revoke(MusicQqFeedbackIntegrationFixture f, bool account)
    {
        if (account) await f.Call("music.accounts.logout", new { platform = "tx" });
        else await f.Enable();
    }
    private static async Task PendingDevice(bool account)
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); var hold = f.Handler.Hold("session"); f.Play(); await hold.Wait();
        await Revoke(f, account); await hold.Cancelled();
        var drain = f.Feedback.DrainAsync(); await Pending(drain);
        Check(f.Handler.Count("track") == 0 && f.Handler.Disposals == 0 && f.Store.Disposals == 0);
        hold.Release.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(8)); await f.Finish();
        var device = await f.Device();
        Check(device["generation"]!.GetValue<long>() == 2 && device["session"] is null &&
            f.Handler.Count("track") == 0 && f.Handler.Count("report") == 0, "Late anonymous session must not be installed");
        if (account) Check(await f.AccountState() == "none");
        else Check(!(await f.Feedback.ReadSettingsAsync()).Enabled.Contains("tx"));
    }
    private static async Task RevokeResponse(bool account)
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await f.Listen();
        var response = f.Handler.HoldReportResponse();
        f.Feedback.Finish(); await response.Read.Wait();
        await Revoke(f, account); await response.Read.Cancelled();
        var drain = f.Feedback.DrainAsync(); await Pending(drain);
        response.Read.Release.TrySetResult(); await response.Cleanup.Wait(); await Pending(drain);
        Check(!response.Disposed && f.Handler.Disposals == 0 && f.Store.Disposals == 0,
            "Revocation must retain resources through physical cleanup");
        response.Cleanup.Release.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(8));
        f.Feedback.Finish(); await f.Drain();
        Check(response.Disposed && f.Handler.Count("report") == 1, "Already-sent report is never retried after revocation");
        if (account) Check(await f.AccountState() == "none");
        else Check(!(await f.Feedback.ReadSettingsAsync()).Enabled.Contains("tx"));
    }
    private static async Task Replacement()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); var before = await f.Device(); await f.Listen();
        var hold = f.Handler.Hold("track"); f.Feedback.Finish(); await hold.Wait();
        var replacement = await f.Login(OtherKey);
        Check(replacement.GetProperty("account").GetProperty("generation").GetInt64() == 2);
        await hold.Cancelled(); var drain = f.Feedback.DrainAsync(); await Pending(drain);
        hold.Release.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(8));
        Check(f.Handler.Count("report") == 0 && f.Handler.Count("vkey") == 0);
        // LastError is a sticky diagnostic, not the new session's receipt. A stale
        // account guard may set FEEDBACK_FAILED before observing cancellation.
        string? previousError = f.Feedback.LastError;
        Check(previousError is null or "FEEDBACK_FAILED", "Only a redacted stale-instance diagnostic");
        await f.Accepted("replacement", previousError); await f.Listen(); await f.Finish();
        Check(f.Handler.Count("report") == 1 && f.Handler.Count("qimei") == 1 && f.Handler.Count("session") == 1 &&
            JsonNode.DeepEquals(before, await f.Device()) && f.Feedback.LastError == previousError,
            "Same anonymous device, new verified account authority");
    }
    private static async Task CloseResume()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await f.Listen();
        var hold = f.Handler.Hold("report");
        var closing = f.App.PrepareShutdownAsync(); await hold.Wait();
        var resuming = f.App.CancelShutdownPreparationAsync();
        await Pending(closing); await Pending(resuming);
        Check((await f.App.ExecuteAsync("music.accounts.list")).GetProperty("code").GetString() == "CLOSED");
        hold.Release.TrySetResult(); await Task.WhenAll(closing, resuming).WaitAsync(TimeSpan.FromSeconds(8));
        Check(f.Handler.Count("report") == 1 && f.Handler.Disposals == 0 && f.Store.Disposals == 0);
        f.Feedback.Observe("entry-1", 10, 240, true); f.Feedback.Finish(); await f.Drain();
        Check(f.Handler.Count("report") == 1, "Retired selection cannot settle again after resume");
        await f.Call("music.librarySync.get");
        await f.Accepted("resumed"); await f.Listen(); await f.Finish();
        Check(f.Handler.Count("report") == 2 && f.Handler.Count("qimei") == 1 && f.Handler.Count("session") == 1 &&
            f.Feedback.LastError is null, "Same application/owner resumes successfully");
    }
    private static async Task CancelledCloseWait()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await f.Listen();
        var response = f.Handler.HoldReportResponse();
        using var caller = new CancellationTokenSource();
        var closing = f.App.PrepareShutdownAsync(caller.Token);
        await response.Read.Wait(); caller.Cancel(); await Cancelled(closing);
        var resuming = f.App.CancelShutdownPreparationAsync(); await Pending(resuming);
        response.Read.Release.TrySetResult(); await response.Cleanup.Wait(); await Pending(resuming);
        Check(f.Handler.Disposals == 0 && f.Store.Disposals == 0);
        response.Cleanup.Release.TrySetResult(); await resuming.WaitAsync(TimeSpan.FromSeconds(8));
        await f.Call("music.accounts.list");
        Check(f.Handler.Count("report") == 1 && response.Disposed && f.Feedback.LastError is null);
    }
    private static async Task FinalDispose()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await f.Listen();
        var response = f.Handler.HoldReportResponse();
        var disposing = f.App.DisposeAsync().AsTask();
        await response.Read.Wait(); response.Read.Release.TrySetResult();
        await response.Cleanup.Wait(); await Pending(disposing);
        Check(f.Handler.Disposals == 0 && f.Store.Disposals == 0, "No early dependency release");
        response.Cleanup.Release.TrySetResult(); await disposing.WaitAsync(TimeSpan.FromSeconds(8));
        Check(f.Handler.Disposals == 1 && f.Store.Disposals == 0 && response.Disposed);
        Check((await f.App.ExecuteAsync("music.accounts.list")).GetProperty("code").GetString() == "CLOSED");
        Check((await f.Store.Fingerprint(AccountNamespace)).Exists, "Borrowed DPAPI remains available to product");
    }
    private static async Task Reopen()
    {
        string root; JsonObject device;
        await using (var f = new MusicQqFeedbackIntegrationFixture())
        {
            root = f.Root; await f.Ready(); await f.Accepted(); await f.Listen(); await f.Finish();
            device = await f.Device();
            Check(f.Handler.Count("report") == 1);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
                Check(!raw.Contains(Key, StringComparison.Ordinal) && !raw.Contains(Qimei, StringComparison.Ordinal) &&
                    !raw.Contains(device["openUdid"]!.GetValue<string>(), StringComparison.Ordinal), "DPAPI ciphertext at rest");
            }
            Check(!(await f.Store.AccountRoot()).ToJsonString().Contains(Mid, StringComparison.Ordinal), "No persisted report");
        }
        await using var reopened = new MusicQqFeedbackIntegrationFixture(root);
        await reopened.App.InitializeAsync();
        Check((await reopened.Feedback.ReadSettingsAsync()).Enabled.SequenceEqual(["tx"]) &&
            await reopened.AccountState() == "saved" && reopened.Handler.Steps.Length == 0);
        reopened.Feedback.Observe("entry-1", 10, 240, true); reopened.Feedback.Finish(); await reopened.Drain();
        Check(reopened.Handler.Steps.Length == 0, "Cold reopen cannot replay a prior listening instance");
        await reopened.Accepted("cold-new"); await reopened.Listen(); await reopened.Finish();
        Check(reopened.Handler.Steps.SequenceEqual(["auth", "profile", "track", "auth", "profile", "track", "vkey", "report"]) &&
            reopened.Store.Writes(AccountNamespace) == 0 && JsonNode.DeepEquals(device, await reopened.Device()) &&
            reopened.Feedback.LastError is null, "Reopened warm device still requires live account proof");
    }
    private static async Task BadAck()
    {
        await using var f = new MusicQqFeedbackIntegrationFixture();
        await f.Ready(); await f.Accepted(); await f.Listen(); f.Handler.Fault = "ack";
        await f.Finish(); f.Feedback.Finish(); await f.Drain(); await f.App.PrepareShutdownAsync();
        Check(f.Handler.Count("report") == 1 && f.Feedback.LastError == "FEEDBACK_FAILED",
            "An unconfirmed report is terminal, not replayable");
    }
}
