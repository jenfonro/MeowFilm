// SPDX-License-Identifier: GPL-3.0-only
// KuGouMusicApi 514f870db7e60ca37bc5535be5da36d32b571c6f:
// register_dev/login_qr_{key,check,create}, util/{crypto,request,helper}. See LICENSES/NOTICE.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountProtocol
{
    /// <summary>
    /// Trusted native composition seam for deterministic encrypted HTTP fixtures.
    /// Existing constructors always use CSPRNG. No RPC or device/storage authority
    /// is granted here; the factory borrows HTTP and retains all protocol budgets.
    /// </summary>
    public static MusicAccountProtocol CreateWithRegistrationEntropy(MusicPlatformHttp transport,
        Func<string> registrationEntropy, TimeSpan? timeout = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(registrationEntropy);
        return new(transport, true, timeout, clock, registrationEntropy);
    }
    private static string KgRandomSecret() => string.Concat(Enumerable.Range(0, 6)
        .Select(_ => "0123456789abcdefghijklmnopqrstuvwxyz"[RandomNumberGenerator.GetInt32(36)]));

    Task<MusicAccountChallenge> IMusicKgDeviceProtocol.BeginKgAsync(
        Func<CancellationToken, Task<StoredKgDevice>> acquire,
        Func<StoredKgDevice, string, CancellationToken, Task<StoredKgDevice>> save, CancellationToken token) =>
        Run("kg", token, async ctx =>
        {
            lock (gate)
            {
                var state = states["kg"];
                AccountRules.Require(state.LastQrCreation is not { } last ||
                    clock.GetElapsedTime(last) >= TimeSpan.FromSeconds(5), "RATE_LIMITED");
                state.LastQrCreation = clock.GetTimestamp();
            }
            var device = await acquire(ctx.Token).ConfigureAwait(false);
            ctx.Guard();
            if (device.Dfid is null)
            {
                string? dfid = await RegisterKg(ctx, device).ConfigureAwait(false);
                if (dfid is not null)
                    device = await save(device, dfid, ctx.Token).ConfigureAwait(false);
                ctx.Guard();
            }
            var response = await ctx.SendAccount(MusicAccountHttpStep.KgQrKey,
                MusicPlatformRequests.KgQrKey(device.Mid, device.Dfid, clock.GetUtcNow().ToUnixTimeSeconds())).ConfigureAwait(false);
            var data = KgAccountJson(response);
            AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
            string key = Text(data["data"]!["qrcode"]);
            AccountRules.Require(AccountRules.Match(key, "^[A-Za-z0-9_-]{1,256}$"), "INVALID_RESPONSE");
            var challenge = new MusicAccountChallenge("kg", Guid.NewGuid().ToString("D"),
                "https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=" + key,
                clock.GetUtcNow().ToUnixTimeMilliseconds() + 180000);
            lock (gate)
            {
                ctx.Guard();
                states["kg"].Qr = new(challenge.ChallengeId, key, ctx.Generation, clock.GetTimestamp()) { Device = device };
            }
            return challenge;
        }, requestLimit: 2);

    private async Task<string?> RegisterKg(Context ctx, StoredKgDevice device)
    {
        string secret;
        try { secret = registrationEntropy(); }
        catch { throw new MusicAccountException("INVALID_REQUEST"); }
        AccountRules.Require(secret is not null && AccountRules.Match(secret, "^[a-z0-9]{6}$"), "INVALID_REQUEST");
        string digest = PlatformProtocols.Md5(secret!);
        byte[] key = Encoding.ASCII.GetBytes(digest[..16]), iv = Encoding.ASCII.GetBytes(digest[16..]);
        byte[] plaintext = Encoding.UTF8.GetBytes(PJ.Encode(KgRegistrationData(device.Seed)));
        byte[] portrait = Encoding.UTF8.GetBytes(PJ.Encode(new JsonObject { ["aes"] = secret, ["uid"] = 0, ["token"] = "" }));
        try
        {
            using var aes = Aes.Create(); aes.Key = key;
            using var rsa = RSA.Create(); rsa.ImportFromPem(KgPublicKey);
            string encrypted = Convert.ToBase64String(aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7));
            string p = Convert.ToHexStringLower(rsa.Encrypt(portrait, RSAEncryptionPadding.Pkcs1));
            var response = await ctx.SendAccount(MusicAccountHttpStep.KgRegister,
                MusicPlatformRequests.KgRegisterDevice(device.Mid, clock.GetUtcNow().ToUnixTimeSeconds(), p, encrypted))
                .ConfigureAwait(false);
            AccountRules.Require(response.Location is null, "REDIRECT_DENIED");
            AccountRules.Require(response.Bytes.Length is > 0 and <= 1048576 && response.Bytes.Length % 16 == 0,
                "INVALID_RESPONSE");
            byte[] plain;
            try { plain = aes.DecryptCbc(response.Bytes, iv, PaddingMode.PKCS7); }
            catch { throw new MusicAccountException("INVALID_RESPONSE"); }
            finally { CryptographicOperations.ZeroMemory(response.Bytes); }
            try
            {
                ctx.Guard();
                var data = PJ.Parse(plain, 1048576); Inspect(data); KgAccountStatus(data);
                // The fixed adapter explicitly accepts a successful [] with no issued dfid.
                if (data["data"] is JsonArray { Count: 0 }) return null;
                AccountRules.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
                string dfid = Text(data["data"]!["dfid"]);
                AccountRules.Require(AccountRules.Match(dfid, "^[A-Za-z0-9_-]{8,128}$"), "INVALID_RESPONSE");
                return dfid;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(portrait);
        }
    }
    private static JsonObject KgRegistrationData(string seed)
    {
        // Source's fixed anonymous client descriptor, NOT readings of this PC/hardware.
        var data = new JsonObject
        {
            ["availableRamSize"] = 4983533568L, ["availableRomSize"] = 48114719, ["availableSDSize"] = 48114717,
            ["basebandVer"] = "", ["batteryLevel"] = 100, ["batteryStatus"] = 3, ["brand"] = "Redmi",
            ["buildSerial"] = "unknown", ["device"] = "marble", ["imei"] = seed, ["imsi"] = "",
            ["manufacturer"] = "Xiaomi", ["uuid"] = seed
        };
        foreach (string sensor in new[] { "accelerometer", "gravity", "gyroscope", "light", "magnetic",
            "orientation", "pressure", "step_counter", "temperature" })
        { data[sensor] = false; data[sensor + "Value"] = ""; }
        return data;
    }
    private static JsonObject KgAccountJson(MusicAccountHttpResult response)
    {
        AccountRules.Require(response.Location is null, "REDIRECT_DENIED");
        try
        {
            var data = PJ.Parse(response.Bytes, 65536); Inspect(data); KgAccountStatus(data); return data;
        }
        finally { CryptographicOperations.ZeroMemory(response.Bytes); }
    }
    private static void KgAccountStatus(JsonObject data)
    {
        var codes = new[] { "error_code", "errcode", "err_code" }.Where(k => data[k] is not null)
            .Select(k => Scalar(data[k])).ToArray();
        if (codes.Contains("20018")) throw new MusicAccountException("AUTH_EXPIRED");
        if (codes.Contains("20028") || Truth(data["ssaCode"]) || Truth(data["ssa-code"]))
            throw new MusicAccountException("VERIFICATION_REQUIRED");
        AccountRules.Require(StrictCode(data["status"]) == 1 && !Truth(data["error"]) && codes.All(c => c == "0"),
            "UPSTREAM_ERROR");
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
            if (current.Polls >= 100) { Fence(state, "CANCELLED"); throw new MusicAccountException("RATE_LIMITED"); }
            current.Polls++; qr = current;
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
        }, replace: false, requestLimit: 3);
    }
}
