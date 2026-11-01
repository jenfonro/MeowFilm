// SPDX-License-Identifier: GPL-3.0-only
// KuGouMusicApi 514f870db7e60ca37bc5535be5da36d32b571c6f:
// register_dev/login_qr_{key,check,create}, util/{crypto,request,helper}. See LICENSES/NOTICE.md.
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    private Task<MusicAccountChallenge> BeginKg(CancellationToken token) =>
        Run("kg", token, async ctx =>
        {
            lock (gate)
            {
                var state = states["kg"];
                AccountRules.Require(state.LastQrCreation is not { } last ||
                    clock.GetElapsedTime(last) >= TimeSpan.FromSeconds(5), "RATE_LIMITED");
                state.LastQrCreation = clock.GetTimestamp();
            }
            var device = await clients.EnsureKgAsync(ClientProtocol, ctx.Token).ConfigureAwait(false);
            ctx.Client = device;
            ctx.Guard();
            var response = await ctx.SendAccount(MusicAccountHttpStep.KgQrKey,
                MusicPlatformRequests.KgQrKey(device.Mid, device.Dfid, clock.GetUtcNow().ToUnixTimeSeconds())).ConfigureAwait(false);
            var data = KgAccountJson(response);
            AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
            string key = Text(data["data"]!["qrcode"]);
            AccountRules.Require(AccountRules.Match(key, "^[A-Za-z0-9_-]{1,256}$"), "INVALID_RESPONSE");
            var challenge = new MusicAccountChallenge("kg", Guid.NewGuid().ToString("D"),
                "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=" + key,
                clock.GetUtcNow().ToUnixTimeMilliseconds() + 180000) { KgClient = device };
            lock (gate)
            {
                ctx.Guard();
                states["kg"].Qr = new(challenge.ChallengeId, key, ctx.Generation, clock.GetTimestamp()) { Device = device };
            }
            return challenge;
        });

    private static JsonObject KgAccountJson(MusicAccountHttpResult response)
    {
        try
        {
            AccountRules.Require(response.Location is null, "REDIRECT_DENIED");
            var data = PJ.Parse(response.Bytes, 65536); Inspect(data); PlatformProtocols.KgAccountStatus(data); return data;
        }
        finally { CryptographicOperations.ZeroMemory(response.Bytes); }
    }
    private Task<MusicAccountPoll> PollKg(string id, MusicAccountSessionHandoff accept, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Qr qr;
        lock (gate)
        {
            AccountRules.Require(!closed, "CLOSED");
            var state = states["kg"];
            var current = state.Qr;
            if (current is null || current.Id != id || current.Consumed)
                return Task.FromResult(new MusicAccountPoll("kg", id, "expired"));
            if (clock.GetElapsedTime(current.Started) >= TimeSpan.FromSeconds(180))
            { Fence(state, "CANCELLED"); return Task.FromResult(new MusicAccountPoll("kg", id, "expired")); }
            AccountRules.Require(state.Active is null, "BUSY");
            qr = current;
        }
        return Run<MusicAccountPoll>("kg", token, async ctx =>
        {
            void GuardQr()
            {
                ctx.Guard();
                lock (gate) AccountRules.Require(ReferenceEquals(states["kg"].Qr, qr) && qr.Generation == ctx.Generation,
                    "SUPERSEDED");
                AccountRules.Require(clock.GetElapsedTime(qr.Started) < TimeSpan.FromSeconds(180), "QR_EXPIRED");
            }
            GuardQr();
            var device = qr.Device ?? throw new MusicAccountException("DEVICE_CONTEXT_REQUIRED");
            AccountRules.Require(clients.IsCurrent(device) && ReferenceEquals(ctx.KgClient, device), "STORAGE_CONFLICT");
            var response = await ctx.SendAccount(MusicAccountHttpStep.KgQrCheck,
                MusicPlatformRequests.KgQrCheck(device.Mid, device.Dfid, clock.GetUtcNow().ToUnixTimeSeconds(), qr.Key, device.Dev))
                .ConfigureAwait(false);
            GuardQr();
            var data = KgAccountJson(response);
            AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
            var result = data["data"]!.AsObject();
            string status = result["status"] is JsonValue statusValue && statusValue.TryGetValue<string>(out var statusText)
                ? statusText : StrictCode(result["status"]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            AccountRules.Require(status is "0" or "1" or "2" or "4", "INVALID_RESPONSE");
            if (status != "4")
            {
                if (status == "0") lock (gate) { GuardQr(); states["kg"].Qr = null; }
                return new("kg", id, status == "0" ? "expired" : status == "1" ? "waiting" : "confirming");
            }
            lock (gate) { GuardQr(); AccountRules.Require(!qr.Consumed, "QR_EXPIRED"); qr.Consumed = true; }
            string userId = Id(result["userid"]);
            string tokenValue = AccountRules.Text(Text(result["token"]), 4096);
            var parts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["userid"] = userId, ["token"] = tokenValue, ["KUGOU_API_MID"] = device.Mid,
                ["KUGOU_API_GUID"] = device.Seed, ["KUGOU_API_DEV"] = device.Dev,
                ["KUGOU_API_MAC"] = "02:00:00:00:00:00", ["KUGOU_API_WEBGL"] = device.Seed
            };
            if (device.Dfid is not null) parts["dfid"] = device.Dfid;
            if (result["t1"] is not null) parts["t1"] = AccountRules.Text(Text(result["t1"]), 4096);
            // No Set-Cookie merge or login VIP/rights assertion; only validated exact identity.
            string cookie = string.Join("; ", parts.Select(p => p.Key + "=" + p.Value));
            try { _ = AccountRules.Cookies(cookie); }
            catch { throw new MusicAccountException("INVALID_RESPONSE"); }
            var profile = await ValidateKg(ctx, cookie, parts, requireIdentity: true).ConfigureAwait(false);
            GuardQr();
            var details = await ObserveAccountDetails(ctx, profile, cookie).ConfigureAwait(false);
            GuardQr();
            await ctx.Accept(accept, new("kg", id, cookie, details)).ConfigureAwait(false);
            return new("kg", id, "authenticated");
        }, replace: false);
    }
}
