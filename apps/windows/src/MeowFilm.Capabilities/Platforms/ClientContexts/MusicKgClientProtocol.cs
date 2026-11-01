// SPDX-License-Identifier: GPL-3.0-only
// KuGouMusicApi 514f870d anonymous register_dev recipe; see LICENSES/NOTICE.md.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal sealed partial class MusicPlatformClientProtocol
{
    internal async Task<string?> RegisterKgAsync(MusicKgClientContext device, CancellationToken token)
    {
        string secret;
        try { secret = registrationEntropy(); }
        catch { throw new MusicPlatformException("INVALID_REQUEST"); }
        PJ.Require(secret is not null && Match(secret, "^[a-z0-9]{6}$"), "INVALID_REQUEST");
        string digest = PlatformProtocols.Md5(secret!);
        byte[] key = Encoding.ASCII.GetBytes(digest[..16]), iv = Encoding.ASCII.GetBytes(digest[16..]);
        byte[] plaintext = Encoding.UTF8.GetBytes(PJ.Encode(KgRegistrationData(device.Seed)));
        byte[] portrait = Encoding.UTF8.GetBytes(PJ.Encode(new JsonObject { ["aes"] = secret, ["uid"] = 0, ["token"] = "" }));
        try
        {
            using var aes = Aes.Create(); aes.Key = key;
            using var rsa = RSA.Create(); rsa.ImportFromPem(PlatformProtocols.KgPublicKey);
            string encrypted = Convert.ToBase64String(aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7));
            string p = Convert.ToHexStringLower(rsa.Encrypt(portrait, RSAEncryptionPadding.Pkcs1));
            var response = await transport.SendAccountStepAsync(MusicAccountHttpStep.KgRegister,
                MusicPlatformRequests.KgRegisterDevice(device.Mid, Clock.GetUtcNow().ToUnixTimeSeconds(), p, encrypted), token, TimeSpan.FromSeconds(25))
                .ConfigureAwait(false);
            byte[]? plain = null;
            try
            {
                PJ.Require(response.Location is null, "REDIRECT_DENIED");
                PJ.Require(response.Bytes.Length is > 0 and <= 1048576 && response.Bytes.Length % 16 == 0,
                    "INVALID_RESPONSE");
                try { plain = aes.DecryptCbc(response.Bytes, iv, PaddingMode.PKCS7); }
                catch { throw new MusicPlatformException("INVALID_RESPONSE"); }
                token.ThrowIfCancellationRequested();
                var data = PJ.Parse(plain, 1048576); PlatformProtocols.KgAccountStatus(data);
                // The fixed adapter explicitly accepts a successful [] with no issued dfid.
                if (data["data"] is JsonArray { Count: 0 }) return null;
                PJ.Require(data["data"] is JsonObject, "INVALID_RESPONSE");
                string dfid = Text(data["data"]!["dfid"]);
                PJ.Require(Match(dfid, "^[A-Za-z0-9_-]{8,128}$"), "INVALID_RESPONSE");
                return dfid;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response.Bytes);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
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
}
