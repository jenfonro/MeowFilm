// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus SMS admission and api-enhanced WEAPI recipes. See LICENSES/NOTICE.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    // No phone numbers or codes in the ledger; per-owner random-key hashes cannot
    // be used as stable phone identifiers. Cancel/logout never reset this ledger.
    private readonly byte[] smsLedgerKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, SmsLimit> smsLimits = new(StringComparer.Ordinal);
    private long? smsWindow;
    private int smsSends;
    private sealed class SmsLimit(long started)
    {
        internal readonly long Started = started;
        internal long? SentAt;
        internal int Attempts;
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
            int Left(long start, int seconds) =>
                Math.Clamp((int)Math.Ceiling(seconds - clock.GetElapsedTime(start).TotalSeconds), 1, 600);
            foreach (var item in smsLimits.Where(p => !p.Value.Busy &&
                clock.GetElapsedTime(p.Value.Started) >= TimeSpan.FromMinutes(10) &&
                (p.Value.SentAt is not { } sentAt || clock.GetElapsedTime(sentAt) >= TimeSpan.FromSeconds(60)))
                .Select(p => p.Key).ToArray())
                smsLimits.Remove(item);
            if (smsWindow is null || clock.GetElapsedTime(smsWindow.Value) >= TimeSpan.FromMinutes(10))
            { smsWindow = clock.GetTimestamp(); smsSends = 0; }
            if (!smsLimits.TryGetValue(key, out var limit))
            {
                if (smsLimits.Count >= 128) throw new MusicAccountException("RATE_LIMITED", 60);
                smsLimits.Add(key, limit = new(clock.GetTimestamp()));
            }
            if (limit.Busy) throw new MusicAccountException("BUSY", 1);
            if (send && smsSends >= 20) throw new MusicAccountException("RATE_LIMITED", Left(smsWindow.Value, 600));
            if (send && limit.SentAt is { } sent && clock.GetElapsedTime(sent) < TimeSpan.FromSeconds(60))
                throw new MusicAccountException("RATE_LIMITED", Left(sent, 60));
            if (!send && limit.Attempts >= 8) throw new MusicAccountException("RATE_LIMITED", Left(limit.Started, 600));
            limit.Busy = true;
            if (send) { limit.SentAt = clock.GetTimestamp(); smsSends++; } else limit.Attempts++;
            return limit;
        }
    }
    private void ReleaseSms(SmsLimit limit) { lock (gate) limit.Busy = false; }

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
            }, requestLimit: 1).ConfigureAwait(false);
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
        }, requestLimit: 3).ConfigureAwait(false);
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
            var data = PJ.Parse(response.Bytes, 65536); Inspect(data);
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
