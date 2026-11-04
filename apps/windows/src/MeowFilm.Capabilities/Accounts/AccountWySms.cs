// SPDX-License-Identifier: GPL-3.0-only
// Fixed api-enhanced WEAPI recipes. See LICENSES/NOTICE.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    // Only active requests and unexpired cooldowns; cancel/logout cannot bypass a send.
    private readonly byte[] smsLedgerKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, SmsLimit> smsLimits = new(StringComparer.Ordinal);
    private ITimer? smsExpiry;
    private sealed class SmsLimit
    {
        internal long? SentAt;
        internal bool Busy;
        public override string ToString() => "MusicSmsLimit (private)";
    }
    private SmsLimit ReserveSms(MusicAccountSmsInput input, bool send)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(input.CountryCode + ":" + input.Phone);
        string key;
        try { key = Convert.ToHexString(HMACSHA256.HashData(smsLedgerKey, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            PruneSms();
            if (!smsLimits.TryGetValue(key, out var limit))
                smsLimits.Add(key, limit = new());
            if (limit.Busy) throw new MusicAccountException("BUSY", 1);
            if (send && limit.SentAt is { } sent && clock.GetElapsedTime(sent) < TimeSpan.FromSeconds(60))
                throw new MusicAccountException("RATE_LIMITED",
                    Math.Max(1, (int)Math.Ceiling(60 - clock.GetElapsedTime(sent).TotalSeconds)));
            limit.Busy = true;
            if (send) limit.SentAt = clock.GetTimestamp();
            return limit;
        }
    }
    private void ReleaseSms(SmsLimit limit)
    {
        lock (gate) { limit.Busy = false; if (!closed) PruneSms(); }
    }
    private void PruneSms()
    {
        TimeSpan? next = null;
        foreach (var (key, limit) in smsLimits.ToArray())
        {
            if (limit.Busy) continue;
            var left = limit.SentAt is { } sent ? TimeSpan.FromSeconds(60) - clock.GetElapsedTime(sent) : TimeSpan.Zero;
            if (left <= TimeSpan.Zero) smsLimits.Remove(key);
            else if (next is null || left < next) next = left;
        }
        if (next is not null)
            smsExpiry ??= clock.CreateTimer(_ => { lock (gate) { if (!closed) PruneSms(); } },
                null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        smsExpiry?.Change(next ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task<MusicAccountSmsSent> SendSmsAsync(MusicAccountSmsInput input, CancellationToken cancellationToken = default)
    {
        AccountRules.Require(input is not null);
        input = input!.Normalize(login: false);
        bool dispatched = false;
        try
        {
            return await Run("wy", cancellationToken, async ctx =>
            {
                var limit = ReserveSms(input, send: true);
                try
                {
                    ctx.Guard();
                    var request = MusicPlatformRequests.WySmsSend(input.Phone, input.CountryCode, ctx.WyClient);
                    dispatched = true; // An admitted HTTP attempt cannot be recalled, even on cancellation.
                    var response = await ctx.SendAccount(MusicAccountHttpStep.WySmsSend, request).ConfigureAwait(false);
                    _ = WySmsJson(response);
                    await ctx.AcceptWyCookies(response.Cookies).ConfigureAwait(false);
                    ctx.Guard();
                    return new MusicAccountSmsSent("wy", true, 60);
                }
                finally { ReleaseSms(limit); }
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (dispatched && AccountRules.Error(error) is
            "CANCELLED" or "TIMEOUT" or "NETWORK_ERROR" or "SUPERSEDED" or "CLOSED")
        { throw new MusicAccountException("SMS_SEND_UNCONFIRMED", 60); }
    }

    public async Task LoginSmsAsync(MusicAccountSmsInput input, string attemptId,
        MusicAccountSessionHandoff acceptSession, CancellationToken cancellationToken = default)
    {
        AccountRules.Require(input is not null && acceptSession is not null && Guid.TryParseExact(attemptId, "D", out _));
        input = input!.Normalize(login: true);
        await Run("wy", cancellationToken, async ctx =>
        {
            var limit = ReserveSms(input, send: false);
            try
            {
                var response = await ctx.SendAccount(MusicAccountHttpStep.WySmsLogin,
                    MusicPlatformRequests.WySmsLogin(input.Phone, input.CountryCode, input.Captcha!, ctx.WyClient)).ConfigureAwait(false);
                var data = WySmsJson(response);
                string cookie = QrCookie(response.Cookies, clock.GetUtcNow());
                var login = WySmsProfile(data, cookie);
                GuardSmsProfile(login, input);
                await ctx.AcceptWyCookies(response.Cookies).ConfigureAwait(false);
                // The login body is not authority to publish a session. Confirm it
                // using the returned cookie and compare two explicit identities.
                var status = await ctx.Send(ctx.Wy(
                    "/api/w/nuser/account/get", new(), cookie, weapi: true)).ConfigureAwait(false);
                WyStatus(status.Body);
                var verified = WySmsProfile(status.Body, cookie);
                AccountRules.Require(login.UserId == verified.UserId, "IDENTITY_MISMATCH");
                GuardSmsProfile(verified, input);
                await ctx.AcceptWyCookies(status.SetCookieHeaders, cookie).ConfigureAwait(false);
                ctx.Guard();
                var details = await ObserveAccountDetails(ctx, verified, cookie).ConfigureAwait(false);
                ctx.Guard();
                await ctx.Accept(acceptSession!, new("wy", attemptId, cookie, details)).ConfigureAwait(false);
                return true;
            }
            finally { ReleaseSms(limit); }
        }).ConfigureAwait(false);
    }
    private static void GuardSmsProfile(MusicAccountProfile profile, MusicAccountSmsInput input)
    {
        foreach (string secret in new[] { input.Phone, input.Captcha! })
            AccountRules.Require(!new[] { profile.UserId, profile.DisplayName, profile.Avatar ?? "" }
                .Any(s => s.Contains(secret, StringComparison.Ordinal) ||
                    s.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal)), "INVALID_RESPONSE");
    }
    private static MusicAccountProfile WySmsProfile(JsonObject data, string cookie)
    {
        if (data.ContainsKey("profile") && data["profile"] is null) throw new MusicAccountException("AUTH_EXPIRED");
        AccountRules.Require(data["profile"] is JsonObject && data["account"] is JsonObject, "INVALID_RESPONSE");
        string userId = Id(data["profile"]!["userId"]);
        AccountRules.Require(Id(data["account"]!["id"]) == userId, "IDENTITY_MISMATCH");
        return Summary("wy", userId, data["profile"]!["nickname"], data["profile"]!["avatarUrl"], cookie);
    }
    private static JsonObject WySmsJson(MusicAccountHttpResult response)
    {
        try
        {
            AccountRules.Require(response.Location is null, "REDIRECT_DENIED");
            var data = PJ.Parse(response.Bytes); Inspect(data);
            int code = StrictCode(data["code"]);
            if (response.Status == 429 || code is 405 or 429 or 509) throw new MusicAccountException("RATE_LIMITED", 60);
            if (code is 250 or 460 or 461) throw new MusicAccountException("VERIFICATION_REQUIRED");
            if (code is 400 or 502 or 503) throw new MusicAccountException("INVALID_CODE");
            if (code is 301 or 401) throw new MusicAccountException("AUTH_EXPIRED");
            if (response.Status == 403 || code == 403) throw new MusicAccountException("FORBIDDEN");
            AccountRules.Require(response.Status is >= 200 and < 300 && code == 200, "UPSTREAM_ERROR");
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(response.Bytes); }
    }
}
