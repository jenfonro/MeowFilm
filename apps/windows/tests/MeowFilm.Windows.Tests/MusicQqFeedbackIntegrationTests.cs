using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using static MeowFilm.Windows.Tests.MusicQqFeedbackIntegrationFixture;
namespace MeowFilm.Windows.Tests;

public static class MusicQqFeedbackIntegrationTests
{
    private static TestCase Case(string name, Func<Task> run) => new("music-qq-feedback-integration: " + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("default off and consent without account remain passive", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.App.InitializeAsync();
            f.Play(); await f.Drain(); await f.Finish(); Check(f.Runtime.Requests.Count == 0);
            await f.Enable("tx"); f.Play(); await f.Drain(); await f.Finish(); Check(f.Runtime.Requests.Count == 0 && f.Store.WritesFor(AccountNamespace) == 0);
        });
        yield return Case("default factory uses real owner and one semantic terminal without generation churn", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted();
            var before = await f.Store.RevisionAsync(AccountNamespace);
            for (int i = 0; i < 4; i++) f.Feedback.Started("entry-1", 0, 240);
            await f.Drain(); await f.Listen(); f.Feedback.Finish(); f.Feedback.Finish(); await f.Drain(); await f.App.PrepareShutdownAsync();
            Check(f.Handler.Steps.SequenceEqual(["feedback.prepare", "feedback.send", "feedback.release"]) && f.Handler.Reports.Count == 1 && f.Feedback.LastError is null);
            Check(before == await f.Store.RevisionAsync(AccountNamespace) && f.Store.WritesFor(AccountNamespace) == 1);
            var end = f.Handler.Reports.Single(); Check(end.GetProperty("listened").GetDouble() >= 1 && end.GetProperty("mediaListened").GetDouble() >= 1);
            Check(f.Store.WritesFor(MusicFeedbackSettingsOwner.DocumentNamespace) == 1 && MusicTestStores.For(f.Store).Writes == 0);
        });
        yield return Case("wall time without native samples never fabricates listening credit", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await Task.Delay(1120); await f.Finish();
            var facts = f.Handler.Reports.Single(); Check(facts.GetProperty("listened").GetDouble() == 0 && facts.GetProperty("mediaListened").GetDouble() == 0);
        });
        yield return Case("pending acceptance and stalled samples have zero listening credit", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture();
            await f.App.InitializeAsync(); await f.Enable("tx");
            var hold = f.Handler.Hold("account.verify");
            var accepting = f.Login();
            try {
                await hold.Wait(); f.Play();
                await Task.Delay(1120); f.Feedback.Observe("entry-1", 1.05, 240, true); await f.Drain();
                Check(!accepting.IsCompleted && f.Handler.Count("feedback.prepare") == 0 && f.Handler.Reports.IsEmpty &&
                    f.Store.WritesFor(AccountNamespace) == 0 && f.Feedback.LastError is null,
                    "a candidate account has not granted playback feedback eligibility");
                hold.Release.TrySetResult(); await accepting;
                f.Feedback.Observe("entry-1", 1.05, 240, true); await f.Drain();
                Check(f.Handler.Count("feedback.prepare") == 1 && await f.AccountState() == "verified" &&
                    f.Store.WritesFor(AccountNamespace) == 1 && f.Feedback.LastError is null);
                f.Feedback.Observe("entry-1", 1.05, 240, true); await f.Finish();
                var facts = f.Handler.Reports.Single();
                Check(facts.GetProperty("listened").GetDouble() == 0 && facts.GetProperty("mediaListened").GetDouble() == 0);
            }
            finally
            {
                hold.Release.TrySetResult();
                try { await accepting; } finally { await f.Drain(); }
            }
        });
        foreach (string code in new[] { "AUTH_REQUIRED", "AUTH_EXPIRED", "INVALID_CREDENTIALS", "IDENTITY_MISMATCH", "INVALID_RESPONSE" })
            yield return Case("canonical prepare failure never falls back " + code, async () => {
                await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); f.Handler.FailureOperation = "feedback.prepare"; f.Handler.FailureCode = code;
                var before = await f.Store.RevisionAsync(AccountNamespace);
                f.Play(); await f.Drain(); await f.Finish(); Check(f.Handler.Count("feedback.prepare") == 1 && f.Handler.Reports.IsEmpty && f.Feedback.LastError == "FEEDBACK_FAILED");
                Check(await f.AccountState() == (code is "AUTH_REQUIRED" or "AUTH_EXPIRED" or "INVALID_CREDENTIALS" ? "revoked" : "verified"));
                Check(before == await f.Store.RevisionAsync(AccountNamespace));
                AssertRedacted(f.Feedback.LastError!);
            });
        foreach (string code in new[] { "AUTH_REQUIRED", "AUTH_EXPIRED", "INVALID_CREDENTIALS" })
            yield return Case("terminal " + code + " revokes the exact live account without durable logout", async () => {
                await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen();
                var before = await f.Store.RevisionAsync(AccountNamespace);
                f.Handler.SendStatus = "rejected"; f.Handler.SendErrorCode = code;
                await f.Finish();
                Check(f.Handler.Count("feedback.send") == 1 && f.Feedback.LastError == "FEEDBACK_FAILED");
                Check(await f.AccountState() == "revoked" && before == await f.Store.RevisionAsync(AccountNamespace));
                int requests = f.Handler.Steps.Length;
                f.Play("after-revocation"); await f.Drain(); await f.Finish();
                Check(f.Handler.Steps.Length == requests, "No automatic reauthentication or feedback replay");
                AssertRedacted(f.Feedback.LastError!);
            });
        foreach (string code in new[] { "IDENTITY_MISMATCH", "INVALID_RESPONSE" })
            yield return Case("terminal recording or field failure does not revoke authentication " + code, async () => {
                await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen();
                var before = await f.Store.RevisionAsync(AccountNamespace);
                f.Handler.SendStatus = "rejected"; f.Handler.SendErrorCode = code;
                await f.Finish(); await f.Finish();
                Check(f.Handler.Count("feedback.send") == 1 && f.Feedback.LastError == "FEEDBACK_FAILED");
                Check(await f.AccountState() == "verified" && before == await f.Store.RevisionAsync(AccountNamespace));
            });
        yield return Case("notSent authentication refusal revokes without claiming a confirmed report", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen();
            f.Handler.SendStatus = "notSent"; f.Handler.SendErrorCode = "AUTH_REQUIRED";
            var before = await f.Store.RevisionAsync(AccountNamespace);
            await f.Finish(); await f.Finish();
            Check(f.Handler.Count("feedback.send") == 1 && f.Feedback.LastError == "FEEDBACK_FAILED");
            Check(await f.AccountState() == "revoked" && before == await f.Store.RevisionAsync(AccountNamespace));
        });
        foreach (bool account in new[] { false, true })
            yield return Case((account ? "logout" : "consent revoke") + " cancels old prepare and drains physical work", async () => {
                await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); var hold = f.Handler.Hold("feedback.prepare"); f.Play();
                try {
                    await hold.Wait(); if (account) await f.Call("music.accounts.logout", new { platform = "tx" }); else await f.Enable();
                    await hold.Cancelled(); var drain = f.Drain(); await Pending(drain); Check(f.Store.Disposals == 0);
                    hold.Release.TrySetResult(); await drain; await f.Finish(); Check(f.Handler.Reports.IsEmpty);
                    if (account) Check(await f.AccountState() == "none"); else Check(!(await f.Feedback.ReadSettingsAsync()).Enabled.Contains("tx"));
                } finally { hold.Release.TrySetResult(); }
            });
        foreach (bool account in new[] { false, true })
            yield return Case((account ? "logout" : "consent revoke") + " cannot abandon an already dispatched terminal", async () => {
                await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen();
                var hold = f.Handler.Hold("feedback.send"); f.Feedback.Finish();
                try {
                    await hold.Wait(); if (account) await f.Call("music.accounts.logout", new { platform = "tx" }); else await f.Enable();
                    Check(!hold.CancelObserved.Task.IsCompleted); var close = f.App.PrepareShutdownAsync(); await Pending(close);
                    hold.Release.TrySetResult(); await close; Check(f.Handler.Reports.Count == 1 && f.Store.Disposals == 0);
                } finally { hold.Release.TrySetResult(); }
            });
        yield return Case("same-user replacement fences late prepare and grants only a fresh attempt", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); var hold = f.Handler.Hold("feedback.prepare"); f.Play();
            try {
                await hold.Wait(); await f.Login(OtherKey); await hold.Cancelled(); hold.Release.TrySetResult(); await f.Drain(); Check(f.Handler.Reports.IsEmpty);
                string? previousError = f.Feedback.LastError;
                Check(previousError is null or "FEEDBACK_FAILED", "Only a redacted stale-instance diagnostic");
                await f.Accepted("replacement", previousError); await f.Listen(); await f.Finish();
                Check(f.Handler.Reports.Count == 1 && f.Feedback.LastError == previousError);
            }
            finally { hold.Release.TrySetResult(); }
        });
        yield return Case("late authentication rejection cannot revoke a same-user replacement", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen();
            f.Handler.SendStatus = "rejected"; f.Handler.SendErrorCode = "INVALID_CREDENTIALS";
            var hold = f.Handler.Hold("feedback.send"); f.Feedback.Finish();
            try {
                await hold.Wait(); await f.Login(OtherKey);
                var replacement = await f.Store.RevisionAsync(AccountNamespace);
                Check(await f.AccountState() == "verified" && !hold.CancelObserved.Task.IsCompleted);
                hold.Release.TrySetResult(); await f.Drain();
                Check(f.Handler.Reports.Count == 1 && f.Feedback.LastError == "FEEDBACK_FAILED");
                Check(await f.AccountState() == "verified" && replacement == await f.Store.RevisionAsync(AccountNamespace));
                f.Handler.SendStatus = "confirmed";
                await f.Accepted("replacement", "FEEDBACK_FAILED"); await f.Listen(); await f.Finish();
                Check(f.Handler.Reports.Count == 2 && await f.AccountState() == "verified");
            }
            finally { hold.Release.TrySetResult(); }
        });
        yield return Case("shutdown drains terminal and cancelled preparation resumes the same owner without replay", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen();
            await f.App.PrepareShutdownAsync(); int reports = f.Handler.Reports.Count;
            await f.App.CancelShutdownPreparationAsync(); await f.Drain(); Check(f.Handler.Reports.Count == reports && await f.AccountState() == "verified");
            await f.Accepted("resumed"); await f.Listen(); await f.Finish(); Check(f.Handler.Reports.Count == reports + 1);
        });
        yield return Case("cancelled shutdown waiter cannot abandon shared physical release", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); var hold = f.Handler.Hold("feedback.release");
            using var stop = new CancellationTokenSource(); var waiting = f.App.PrepareShutdownAsync(stop.Token);
            try { await hold.Wait(); stop.Cancel(); await Cancelled(waiting); var close = f.App.PrepareShutdownAsync(); await Pending(close); Check(f.Store.Disposals == 0); hold.Release.TrySetResult(); await close; }
            finally { hold.Release.TrySetResult(); }
        });
        yield return Case("final disposal waits for physical prepare and preserves borrowed store", async () => {
            await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); var hold = f.Handler.Hold("feedback.prepare"); f.Play();
            try { await hold.Wait(); var close = f.App.DisposeAsync().AsTask(); await hold.Cancelled(); await Pending(close); Check(f.Store.Disposals == 0); hold.Release.TrySetResult(); await close; }
            finally { hold.Release.TrySetResult(); }
        });
        yield return Case("current DPAPI cold reopen never replays QQ feedback", async () => {
            string root; await using (var f = new MusicQqFeedbackIntegrationFixture()) { root = f.Root; await f.Ready(); await f.Accepted(); await f.Listen(); await f.Finish(); Check(f.Handler.Reports.Count == 1); }
            await using var cold = new MusicQqFeedbackIntegrationFixture(root); await cold.App.InitializeAsync(); Check(cold.Runtime.Requests.Count == 0 && await cold.AccountState() == "saved");
            await cold.Call("music.accounts.validate", new { platform = "tx" }); await cold.Accepted(); await cold.Listen(); await cold.Finish(); Check(cold.Handler.Reports.Count == 1);
        });
        foreach (string status in new[] { "unknown", "rejected", "notSent" })
            yield return Case("canonical " + status + " terminal has no automatic replay", async () => {
                await using var f = new MusicQqFeedbackIntegrationFixture(); await f.Ready(); await f.Accepted(); await f.Listen(); f.Handler.SendStatus = status;
                if (status == "unknown") f.Handler.SendErrorCode = "AUTH_EXPIRED";
                await f.Finish(); await f.Finish();
                Check(await f.AccountState() == "verified");
                await f.App.PrepareShutdownAsync(); Check(f.Handler.Reports.Count == 1 && f.Feedback.LastError == "FEEDBACK_FAILED");
            });
    }
}
