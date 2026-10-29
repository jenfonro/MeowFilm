// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    public Task<MusicAccountDetailsObservation> ReadAccountDetailsAsync(MusicPlatformSession account,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        string platform = AccountRules.Platform(account.Platform);
        var parts = AccountRules.Cookies(account.Cookie);
        return Run(platform, token, async ctx =>
        {
            // Always authenticate the exact credential before interpreting rights.
            // Public profile/device/cache values are never login proof.
            var profile = platform switch {
                "wy" => await ValidateWy(ctx, account.Cookie, parts).ConfigureAwait(false),
                "tx" => await ValidateTx(ctx, account.Cookie, parts).ConfigureAwait(false),
                "kg" => await ValidateKg(ctx, account.Cookie, parts).ConfigureAwait(false),
                _ => throw new MusicAccountException("INVALID_REQUEST")
            };
            AccountRules.Require(profile.UserId == account.UserId, "IDENTITY_MISMATCH");
            long fetched = clock.GetUtcNow().ToUnixTimeMilliseconds();
            MusicAccountEntitlements rights;
            try
            {
                if (platform == "wy")
                {
                    var data = (await ctx.Send(MusicPlatformRequests.WyWithCookie(
                        "/api/music-vip-membership/client/vip/info", new() { ["userId"] = account.UserId },
                        account.Cookie, weapi: true)).ConfigureAwait(false)).Body;
                    WyStatus(data);
                    AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
                    rights = AccountDetailsSnapshot.Wy(data["data"]!.AsObject(), account.UserId, clock.GetUtcNow().ToUnixTimeMilliseconds());
                }
                else if (platform == "tx")
                {
                    var data = await QqRead(ctx, account.Cookie, "VipLogin.VipLoginInter", "vip_login_base", new()).ConfigureAwait(false);
                    rights = AccountDetailsSnapshot.Qq(data, clock.GetUtcNow().ToUnixTimeMilliseconds());
                }
                else
                {
                    var request = MusicPlatformRequests.KgWithCookie("/v1/get_union_vip",
                        new() { ["busi_type"] = "concept" }, account.Cookie, parts["KUGOU_API_MID"]);
                    // The pinned module explicitly supplies this HTTPS baseURL.
                    // The Android signature covers query/body, not an invented proxy.
                    request.RequestUri = new UriBuilder(request.RequestUri!) { Host = "kugouvip.kugou.com" }.Uri;
                    var data = (await ctx.Send(request).ConfigureAwait(false)).Body;
                    try { PlatformProtocols.KgSuccess(data, "/v1/get_union_vip"); }
                    catch (MusicPlatformException e) { throw new MusicAccountException(e.Code == "AUTH_REQUIRED" ? "AUTH_EXPIRED" : e.Code); }
                    AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
                    rights = AccountDetailsSnapshot.Kg(data["data"]!.AsObject(), account.UserId, clock.GetUtcNow().ToUnixTimeMilliseconds());
                }
            }
            catch (MusicAccountException error) when (error.Code is "NETWORK_ERROR" or "UPSTREAM_ERROR" or
                "INVALID_RESPONSE" or "RATE_LIMITED")
            {
                ctx.Guard();
                rights = AccountDetailsSnapshot.Unknown(clock.GetUtcNow().ToUnixTimeMilliseconds(), error.Code, platform == "kg");
            }
            ctx.Guard();
            return new MusicAccountDetailsObservation(profile, fetched, AccountDetailsSnapshot.Validate(rights, platform));
        }, detailsRead: true);
    }
}
