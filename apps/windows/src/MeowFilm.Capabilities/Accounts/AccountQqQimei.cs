// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 2698673d utils/qimei.py and the explicit vivo profile in utils/device.py.
// Public client protocol constants only. Returned QIMEI is NEVER generated locally.
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

internal static class QqQimeiRecipe
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDEIxgwoutfwoJxcGQeedgP7FG9qaIuS0qzfR8gWkrkTZKM2iWHn2ajQpBRZjMSoSf6+KJGvar2ORhBfpDXyVtZCKpqLQ+FLkpncClKVIrBwv6PHyUvuCb0rIarmgDnzkfQAqVufEtR64iazGDKatvJ9y6B9NMbHddGSAUmRTCrHQIDAQAB
        -----END PUBLIC KEY-----
        """;
    private const string Extra = """{"appKey":"0AND0HD6FE4HY80F"}""";
    private const string Version = "20.9.0.8";
    private static string Hex(int length) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes((length + 1) / 2))[..length];
    private static byte[] Encrypt(byte[] key, byte[] content, byte[]? iv = null)
    {
        using var aes = Aes.Create(); aes.Key = key;
        return aes.EncryptCbc(content, iv ?? key, PaddingMode.PKCS7);
    }
    private static string DeviceToken(string value)
    {
        byte[] plain = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(Encrypt("lvcwmSYVr2Axv1gn"u8.ToArray(), plain, "Zs0ntDqG2jyhKN0c"u8.ToArray())); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static string Beacon(DateTimeOffset now)
    {
        string month = now.UtcDateTime.ToString("yyyy-MM-", CultureInfo.InvariantCulture) + "01";
        string common = month + RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture) + "." +
            RandomNumberGenerator.GetInt32(100000000, 1000000000).ToString(CultureInfo.InvariantCulture);
        var output = new StringBuilder();
        for (int i = 1; i <= 40; i++)
        {
            string value = i switch {
                1 or 2 or 13 or 14 or 17 or 18 or 21 or 22 or 25 or 26 or 29 or 30 or 33 or 34 or 37 or 38 => common,
                3 => "0000000000000000",
                4 => string.Concat(Enumerable.Range(0, 16).Select(_ => "123456789abcdef"[RandomNumberGenerator.GetInt32(15)])),
                _ => RandomNumberGenerator.GetInt32(10000).ToString(CultureInfo.InvariantCulture) };
            output.Append('k').Append(i.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }
        return output.ToString();
    }
    internal static HttpRequestMessage Request(StoredQqFeedbackDevice device, DateTimeOffset now) =>
        RequestCore(device, now, Hex(16), Hex(16), Beacon(now), RandomNumberGenerator.GetInt32(14401));
    // Separates ephemeral entropy from the audited wire recipe; no key or comm escapes in a DTO.
    private static HttpRequestMessage RequestCore(StoredQqFeedbackDevice device, DateTimeOffset now,
        string cryptKey, string nonce, string beacon, int uptimeOffset)
    {
        byte[] digest = MD5.HashData(Encoding.ASCII.GetBytes(device.AndroidId));
        var reserved = new JsonObject {
            ["harmony"] = "0", ["clone"] = "0", ["containe"] = "", ["oz"] = DeviceToken(device.AndroidId),
            ["oo"] = DeviceToken("V2408A"), ["kelong"] = "0", ["ip"] = $"192.168.{digest[0]}.{digest[1] % 253 + 2}",
            ["uptimes"] = now.UtcDateTime.AddSeconds(-uptimeOffset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["multiUser"] = "0", ["bod"] = "sun", ["brd"] = "vivo", ["dv"] = "PD2408", ["firstLevel"] = "35",
            ["manufact"] = "vivo", ["name"] = "PD2408", ["host"] = "comdg01150014",
            ["kernel"] = "Linux localhost 6.6.89-android15-8-g1f71897ac249-abogki467805059-4k #1 SMP PREEMPT Thu Dec 11 01:56:00 UTC 2025 aarch64",
            ["pre"] = "0", ["av"] = Version, ["ch"] = "" };
        var payload = new JsonObject {
            ["androidId"] = device.AndroidId, ["platformId"] = 1, ["appKey"] = "0AND0HD6FE4HY80F",
            ["appVersion"] = Version, ["beaconIdSrc"] = beacon, ["brand"] = "vivo", ["channelId"] = "10003505",
            ["cid"] = "", ["imei"] = device.Imei, ["imsi"] = "", ["mac"] = "", ["model"] = "V2408A",
            ["networkType"] = "wifi", ["oaid"] = "", ["osVersion"] = "Android 15,level 35", ["qimei"] = "",
            ["qimei36"] = "", ["sdkVersion"] = "1.2.13.6", ["targetSdkVersion"] = "30", ["audit"] = "",
            ["userId"] = "{}", ["packageId"] = "com.tencent.qqmusic", ["deviceType"] = "Phone", ["sdkName"] = "",
            ["reserved"] = PJ.Encode(reserved) };
        byte[] keyBytes = Encoding.ASCII.GetBytes(cryptKey), plain = Encoding.UTF8.GetBytes(PJ.Encode(payload));
        try
        {
            using var rsa = RSA.Create(); rsa.ImportFromPem(PublicKey);
            string key = Convert.ToBase64String(rsa.Encrypt(keyBytes, RSAEncryptionPadding.Pkcs1));
            string parameters = Convert.ToBase64String(Encrypt(keyBytes, plain));
            long seconds = now.ToUnixTimeSeconds();
            string timestamp = seconds.ToString(CultureInfo.InvariantCulture);
            var json = new JsonObject { ["app"] = 0, ["os"] = 1, ["qimeiParams"] = new JsonObject {
                ["key"] = key, ["params"] = parameters, ["time"] = timestamp, ["nonce"] = nonce,
                ["sign"] = PlatformProtocols.Md5(key + parameters + (seconds * 1000).ToString(CultureInfo.InvariantCulture) +
                    nonce + "ZdJqM15EeO2zWc08" + Extra), ["extra"] = Extra } };
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tencentmusic.com/tme/trpc/proxy") {
                Content = new StringContent(PJ.Encode(json), Encoding.UTF8, "application/json") };
            foreach (var (name, value) in new[] { ("Host", "api.tencentmusic.com"), ("method", "GetQimei"),
                ("service", "trpc.tme_datasvr.qimeiproxy.QimeiProxy"), ("appid", "qimei_qq_android"),
                ("sign", PlatformProtocols.Md5("qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH" + timestamp)),
                ("User-Agent", "QQMusic"), ("timestamp", timestamp) })
                request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); CryptographicOperations.ZeroMemory(plain); }
    }
}
