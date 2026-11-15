using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>Account protocol adapter. The native owner alone admits, saves and publishes credentials.</summary>
internal sealed class MusicAccountProtocol(MusicPlatformClient platform) : IMusicAccountProtocol, IMusicAccountTransferProtocol
{
    private readonly object gate = new();
    private readonly HashSet<Task> pending = [];
    private readonly HashSet<string> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> qrCancellations = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    private Task? disposal;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private async Task<JsonElement> Call(string source, string operation, object parameters,
        string? cookie, string userId, CancellationToken token)
    {
        CancellationTokenSource linked;
        Task<JsonElement> work;
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try
            {
                work = platform.CandidateAsync(source, operation, parameters, cookie, userId, linked.Token);
                sources.Add(source); pending.Add(work);
            }
            catch { linked.Dispose(); throw; }
        }
        try { return await work.ConfigureAwait(false); }
        catch (MusicPlatformException error)
        {
            string code = error.Code == "AUTH_REQUIRED" ? "AUTH_EXPIRED" : error.Code;
            throw new MusicAccountException(code,
                operation.StartsWith("sms.", StringComparison.Ordinal) && code is ("RATE_LIMITED" or "SMS_SEND_UNCONFIRMED") ? 60 : 0);
        }
        finally { lock (gate) pending.Remove(work); linked.Dispose(); }
    }
    private static T Decode<T>(Func<T> consume)
    {
        try { return consume(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new MusicAccountException("INVALID_RESPONSE"); }
    }
    private static MusicAccountDetailsObservation Observation(JsonElement result) => Decode(() => new MusicAccountDetailsObservation(
        result.GetProperty("profile").Deserialize<MusicAccountProfile>(Json) ?? throw new MusicAccountException("INVALID_RESPONSE"),
        result.GetProperty("profileFetchedAt").GetInt64(),
        result.GetProperty("entitlements").Deserialize<MusicAccountEntitlements>(Json) ?? throw new MusicAccountException("INVALID_RESPONSE")));
    public async Task<MusicAccountVerifiedCredential> ValidateAsync(string source, string cookie,
        CancellationToken cancellationToken = default)
    {
        var result = await Call(source, "account.verify", new { }, cookie, "", cancellationToken).ConfigureAwait(false);
        return Decode(() => new MusicAccountVerifiedCredential(result.GetProperty("cookie").GetString()!, Observation(result)));
    }
    public Task<MusicAccountVerifiedCredential> ValidateTransferAsync(string source, string cookie, CancellationToken token) =>
        ValidateAsync(source, cookie, token);
    public async Task<MusicAccountDetailsObservation> ReadAccountDetailsAsync(MusicPlatformSession account,
        CancellationToken token = default) => Observation(await Call(account.Platform, "account.profile", new { },
            account.Cookie, account.UserId, token).ConfigureAwait(false));
    public async Task<MusicAccountChallenge> BeginAsync(string source, CancellationToken cancellationToken = default)
    {
        await DrainQrCancellation(source).WaitAsync(cancellationToken).ConfigureAwait(false);
        string challengeId = Guid.NewGuid().ToString("D");
        var result = await Call(source, "qr.create", new { challengeId }, null, "", cancellationToken).ConfigureAwait(false);
        return Decode(() =>
        {
            AccountRules.Require(result.GetProperty("challengeId").GetString() == challengeId, "INVALID_RESPONSE");
            return new MusicAccountChallenge(source, challengeId, result.GetProperty("url").GetString()!,
                result.GetProperty("expiresAt").GetInt64(), result.GetProperty("imageBase64").ValueKind == JsonValueKind.Null ? null : result.GetProperty("imageBase64").GetString());
        });
    }
    public async Task<MusicAccountPoll> PollAsync(string source, string challengeId, MusicAccountSessionHandoff acceptSession,
        CancellationToken cancellationToken = default)
    {
        await DrainQrCancellation(source).WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await Call(source, "qr.poll", new { challengeId }, null, "", cancellationToken).ConfigureAwait(false);
        string status = Decode(() => result.GetProperty("status").GetString()!);
        if (status == "authenticated")
        {
            var account = Decode(() => result.GetProperty("account"));
            var handoff = Decode(() => new MusicAccountHandoff(source, challengeId, account.GetProperty("cookie").GetString()!,
                Observation(account)));
            bool accepted = await acceptSession(handoff, cancellationToken).ConfigureAwait(false);
            AccountRules.Require(accepted, "HANDOFF_REJECTED");
        }
        return new(source, challengeId, status);
    }
    public async Task<MusicAccountSmsSent> SendSmsAsync(MusicAccountSmsInput input, CancellationToken cancellationToken = default)
    {
        var result = await Call(input.Platform, "sms.send", new { phone = input.Phone, countryCode = input.CountryCode }, null, "", cancellationToken).ConfigureAwait(false);
        try
        {
            var sent = Decode(() => new MusicAccountSmsSent(input.Platform,
                result.GetProperty("confirmed").GetBoolean(), result.GetProperty("retryAfterSeconds").GetInt32()));
            AccountRules.Require(sent.Sent, "INVALID_RESPONSE");
            return sent;
        }
        catch (MusicAccountException error) when (error.Code == "INVALID_RESPONSE")
        { throw new MusicAccountException("SMS_SEND_UNCONFIRMED", 60); }
    }
    public async Task LoginSmsAsync(MusicAccountSmsInput input, string attemptId, MusicAccountSessionHandoff acceptSession,
        CancellationToken cancellationToken = default)
    {
        var result = await Call(input.Platform, "sms.login", new { phone = input.Phone, countryCode = input.CountryCode,
            captcha = input.Captcha }, null, "", cancellationToken).ConfigureAwait(false);
        var handoff = Decode(() => new MusicAccountHandoff(input.Platform, attemptId,
            result.GetProperty("cookie").GetString()!, Observation(result)));
        AccountRules.Require(await acceptSession(handoff, cancellationToken).ConfigureAwait(false), "HANDOFF_REJECTED");
    }
    public Task CancelAsync(string source)
    {
        lock (gate)
        {
            if (closed) return Task.FromException(new MusicAccountException("CLOSED"));
            if (!sources.Contains(source)) return Task.CompletedTask;
            var before = qrCancellations.GetValueOrDefault(source) ?? Task.CompletedTask;
            var work = CancelRemote(before);
            qrCancellations[source] = work;
            pending.Add(work);
            _ = work.ContinueWith(done =>
            { _ = done.Exception; lock (gate) pending.Remove(work); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return work;
            async Task CancelRemote(Task previous)
            {
                await Task.Yield();
                try { await previous.ConfigureAwait(false); } catch { /* Order, not replay, the prior cancellation. */ }
                _ = await Call(source, "qr.cancel", new { }, null, "", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
    private async Task DrainQrCancellation(string source)
    {
        Task work; lock (gate) work = qrCancellations.GetValueOrDefault(source) ?? Task.CompletedTask;
        try { await work.ConfigureAwait(false); }
        catch { /* A new create supersedes the old challenge itself; do not replay an earlier error. */ }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            disposal = Drain();
            return new(disposal);
        }
        async Task Drain()
        {
            await Task.Yield();
            lifetime.Cancel();
            Task[] work; string[] targets;
            lock (gate) { work = pending.ToArray(); targets = sources.ToArray(); }
            try
            {
                // A cancelled RPC waiter is not Node's physical completion. The
                // existing session reset cancels candidates/QR and acknowledges its drain.
                foreach (string source in targets) platform.Invalidate(source);
                try { await Task.WhenAll(work).ConfigureAwait(false); } catch { /* Receipts stay on the original calls. */ }
                await platform.DrainAsync().ConfigureAwait(false);
            }
            finally { lifetime.Dispose(); }
        }
    }
}
