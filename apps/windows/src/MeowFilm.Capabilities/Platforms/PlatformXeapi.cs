// SPDX-License-Identifier: GPL-3.0-only
// Native translation of api-enhanced MIT pin: register_xeapikey.js, util/crypto.js/request.js.
// Public protocol constants only. No device registration, anti-cheat token or challenge bypass.
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal static class PlatformXeapi
{
    private static readonly byte[] StaticKey = Convert.FromHexString("ab1d5a430f6bb04a3f01e81ddd72bd916d5ce591248ac128714806d7f8fb1b84");
    private const string SignKey = "mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g==";
    private const string Agent = "NeteaseMusic/9.5.61.260802021928(9005061);Dalvik/2.1.0 (Linux; U; Android 12; HBN-AL00 Build/cd737a2.0)";
    internal sealed class KeyState(byte[] publicKey, string version, string sk)
    {
        internal byte[] PublicKey { get; } = publicKey;
        internal string Version { get; } = version;
        internal string Sk { get; } = sk;
        public override string ToString() => "XeapiKeyState (redacted)";
    }
    private static byte[] Sign(string timestamp, string nonce) =>
        HMACSHA256.HashData(Encoding.ASCII.GetBytes(SignKey), Encoding.UTF8.GetBytes(timestamp + nonce));
    private static byte[] Encrypt(byte[] key, byte[] text)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return aes.EncryptEcb(text, PaddingMode.PKCS7);
    }
    private static byte[] Decrypt(byte[] key, byte[] encrypted)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return aes.DecryptEcb(encrypted, PaddingMode.PKCS7);
    }
    private static string Device(Dictionary<string, string> cookies)
    {
        var value = cookies["deviceId"];
        PJ.Require(value.Length is > 0 and <= 256, "DEVICE_CONTEXT_REQUIRED");
        return value;
    }
    internal static (HttpRequestMessage Request, string Nonce) KeyRequest(MusicWyClientContext client, MusicPlatformSession? session)
    {
        var device = Device(MusicWyRequestContext.Cookies(client, session?.Cookie));
        var nonce = string.Concat(Enumerable.Range(0, 16).Select(_ => RandomNumberGenerator.GetInt32(10).ToString(CultureInfo.InvariantCulture)));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var data = new Dictionary<string, string> {
            ["appVersion"] = "9.5.61", ["currentKeyVersion"] = "", ["deviceId"] = device, ["nonce"] = nonce,
            ["os"] = "android", ["requestType"] = "active", ["signature"] = Convert.ToBase64String(Sign(timestamp, nonce)),
            ["t1"] = "", ["t2"] = "", ["timestamp"] = timestamp, ["uid"] = ""
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://interface.music.163.com/api/bsr/sk/get") {
            Content = new StringContent(PlatformProtocols.Form(data), Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        request.Headers.TryAddWithoutValidation("Cookie", "deviceId=" + device);
        return (request, nonce);
    }
    internal static KeyState ReadKey(JsonObject response, string nonce)
    {
        var data = response["data"];
        PJ.Require(data is JsonObject, "INVALID_RESPONSE");
        var timestamp = PJ.Id(PJ.At(data, "timestamp"));
        byte[] encrypted, signature;
        try
        {
            encrypted = Convert.FromBase64String(PJ.S(data, "encryptedData"));
            signature = Convert.FromBase64String(PJ.S(data, "signature"));
        }
        catch (FormatException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
        PJ.Require(CryptographicOperations.FixedTimeEquals(Sign(timestamp, nonce), signature), "INVALID_RESPONSE");
        byte[] plain;
        try { plain = Decrypt(StaticKey, encrypted); }
        catch (CryptographicException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
        try
        {
            var state = PJ.Parse(plain, 16384);
            var version = PJ.Scalar(state["version"]); var sk = PJ.S(state, "sk");
            PJ.Require(version.Length is > 0 and <= 128 && sk.Length is > 0 and <= 4096 &&
                !version.Contains('|') && !sk.Contains('|') && !version.Any(char.IsControl) && !sk.Any(char.IsControl), "INVALID_RESPONSE");
            byte[] key;
            try { key = Convert.FromBase64String(PJ.S(state, "publicKey")); }
            catch (FormatException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
            PJ.Require(key.Length == 32 && key.Any(b => b != 0), "INVALID_RESPONSE");
            return new(key, version, sk);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] EncryptKey(byte[] dynamicKey, KeyState state)
    {
        PJ.Require(OperatingSystem.IsWindows(), "CRYPTO_UNAVAILABLE");
        using var local = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("curve25519"));
        using var peer = ECDiffieHellman.Create(new ECParameters {
            Curve = ECCurve.CreateFromFriendlyName("curve25519"), Q = new ECPoint { X = state.PublicKey, Y = new byte[32] }
        });
        var ephemeral = local.ExportParameters(false).Q.X!;
        PJ.Require(ephemeral.Length == 32, "CRYPTO_UNAVAILABLE");
        // Windows CNG curve25519 exports and derives RFC7748 little-endian values (tested with its fixed vector).
        var shared = local.DeriveRawSecretAgreement(peer.PublicKey);
        var prk = HMACSHA256.HashData(new byte[32], shared);
        byte[] info = [.. ephemeral, 1];
        var expanded = HMACSHA256.HashData(prk, info);
        var key = expanded[..16]; var iv = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(Convert.ToBase64String(dynamicKey) + "|android|" + state.Sk);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        try
        {
            PJ.Require(shared.Length == 32 && shared.Any(b => b != 0), "INVALID_RESPONSE");
            using var aes = new AesGcm(key, 16); aes.Encrypt(iv, plain, cipher, tag);
            return [.. ephemeral, .. iv, .. cipher, .. tag];
        }
        finally
        {
            foreach (var bytes in new[] { shared, prk, expanded, key, plain }) CryptographicOperations.ZeroMemory(bytes);
        }
    }
    internal static HttpRequestMessage Request(string path, JsonObject parameters, MusicPlatformSession session,
        MusicWyClientContext client, KeyState state)
    {
        PJ.Require(path is "/api/v3/discovery/recommend/songs" or "/api/homepage/category/daily/song/list" or "/api/song/enhance/player/url/v1");
        var cookies = MusicWyRequestContext.Cookies(client, session.Cookie); var device = Device(cookies);
        var version = PJ.Cookie(cookies, "os") == "android" ? PJ.Cookie(cookies, "appver", "9.1.65") : "9.1.65";
        var osver = PJ.Cookie(cookies, "os") == "android" ? PJ.Cookie(cookies, "osver", "16") : "16";
        var build = PJ.Cookie(cookies, "buildver", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var fields = new JsonObject();
        var body = PlatformProtocols.Form(parameters.Where(p => p.Key != "e_r").ToDictionary(p => p.Key, p => PJ.Scalar(p.Value)));
        if (body.Length > 0) fields["content"] = body;
        fields["queryString"] = "e_r=true";
        var plaintext = Encoding.UTF8.GetBytes(PJ.Encode(fields)); var dynamicKey = RandomNumberGenerator.GetBytes(16);
        try
        {
            var inner = Encrypt(StaticKey, plaintext); var mask = RandomNumberGenerator.GetBytes(16);
            for (var i = 0; i < inner.Length; i++) inner[i] ^= mask[i & 15];
            var rotate = (mask[0] & 15) % inner.Length;
            var content = Encrypt(dynamicKey, [.. mask, .. inner[rotate..], .. inner[..rotate]]);
            var sealedKey = EncryptKey(dynamicKey, state);
            var revision = Encrypt(StaticKey, Encoding.UTF8.GetBytes(state.Version + "|"));
            var encoded = PlatformProtocols.Form(new Dictionary<string, string> {
                ["C"] = Convert.ToBase64String(content).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                ["S"] = Convert.ToBase64String(sealedKey), ["R"] = Convert.ToBase64String(revision)
            });
            // A fresh per-operation key avoids the upstream's process-global cross-account session optimization.
            var request = new HttpRequestMessage(HttpMethod.Post, "https://interface3.music.163.com/xeapi/" + path[5..]) {
                Content = new StringContent(encoded, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            cookies["os"] = "android"; cookies["osver"] = osver; cookies["appver"] = version; cookies["buildver"] = build;
            cookies["sDeviceId"] = PJ.Cookie(cookies, "sDeviceId", device);
            foreach (var (name, value) in new[] { ("User-Agent", Agent), ("X-Client-Enc-State", "ENCRYPTED"), ("x-aeapi", "true"),
                ("x-deviceid", device), ("x-os", "android"), ("x-osver", osver), ("x-appver", version),
                ("x-sdeviceid", cookies["sDeviceId"]), ("x-buildver", build), ("x-music-u", PJ.Cookie(cookies, "MUSIC_U")),
                ("Cookie", string.Join("; ", cookies.Select(p => p.Key + "=" + p.Value))) })
                request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }
        catch (PlatformNotSupportedException) { throw new MusicPlatformException("CRYPTO_UNAVAILABLE"); }
        catch (CryptographicException) { throw new MusicPlatformException("CRYPTO_UNAVAILABLE"); }
        finally { CryptographicOperations.ZeroMemory(dynamicKey); CryptographicOperations.ZeroMemory(plaintext); }
    }
    internal static JsonObject DecodeResponse(byte[] bytes, int maximum, CancellationToken token)
    {
        byte[] plain;
        try { plain = Decrypt("e82ckenh8dichen8"u8.ToArray(), bytes); }
        catch (CryptographicException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (plain.Length > 1 && plain[0] == 0x1f && plain[1] == 0x8b)
            {
                using var input = new MemoryStream(plain, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = gzip.Read(buffer);
                    if (read == 0) break;
                    PJ.Require(output.Length + read <= maximum, "LIMIT");
                    output.Write(buffer, 0, read);
                }
                var decoded = output.ToArray();
                try { return PJ.Parse(decoded, maximum); }
                finally { CryptographicOperations.ZeroMemory(decoded); }
            }
            return PJ.Parse(plain, maximum);
        }
        catch (InvalidDataException) { throw new MusicPlatformException("INVALID_RESPONSE"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
