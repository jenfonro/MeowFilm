// SPDX-License-Identifier: GPL-3.0-only
// KuGouMusicApi 514f870d playlist_del / playlist_tracks_sort / util/crypto / util/request (MIT).
// Protocol constants are public client parameters, not credentials. No SSA fabrication, proxy or plaintext retry.
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
namespace MeowFilm.Capabilities.Sync;

internal static class MusicSyncKgCloud
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB
        -----END PUBLIC KEY-----
        """;
    internal static Task SendAsync(MusicPlatformSession session, string path, JsonObject body,
        Func<HttpRequestMessage, CancellationToken, Task<byte[]>> transport, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(path is "/v2/delete_list" or "/v1/modify_song_sort");
        string alphabet = path == "/v2/delete_list" ? "0123456789abcdefghijklmnopqrstuvwxyz" : "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string secret = string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
        return SendCoreAsync(session, path, body, transport, token, secret);
    }
    // Protocol core is kept separate from entropy selection; no secret/key is exposed through HTTP or the public service.
    private static async Task SendCoreAsync(MusicPlatformSession session, string path, JsonObject body,
        Func<HttpRequestMessage, CancellationToken, Task<byte[]>> transport, CancellationToken token, string secret)
    {
        token.ThrowIfCancellationRequested();
        LJ.Require(path is "/v2/delete_list" or "/v1/modify_song_sort");
        LJ.Require(session.Platform == "kg", "INVALID_PLATFORM");
        var cookies = PJ.CookieHeader(session.Cookie); string mid = PJ.Cookie(cookies, "KUGOU_API_MID"), dfid = PJ.Cookie(cookies, "dfid", "-");
        LJ.Require(mid.Length is > 0 and <= 39 && mid.All(char.IsAsciiDigit) &&
            PJ.Cookie(cookies, "userid") == session.UserId, "DEVICE_CONTEXT_REQUIRED");
        LJ.Require(PJ.Cookie(cookies, "token").Length > 0, "AUTH_REQUIRED");
        string digest = PlatformProtocols.Md5(secret); byte[] key = Encoding.ASCII.GetBytes(digest[..16]), iv = Encoding.ASCII.GetBytes(digest[16..]);
        byte[] plaintext = Encoding.UTF8.GetBytes(PJ.Encode(body)), credential = Encoding.UTF8.GetBytes(PJ.Encode(LJ.Obj(("aes", secret), ("uid", session.UserId), ("token", PJ.Cookie(cookies, "token")))));
        byte[]? raw = null, decoded = null;
        try
        {
            using var rsa = RSA.Create(); rsa.ImportFromPem(PublicKey);
            LJ.Require(credential.Length <= rsa.KeySize / 8 - 11, "INVALID_CREDENTIALS");
            string portrait = Convert.ToHexString(rsa.Encrypt(credential, RSAEncryptionPadding.Pkcs1));
            using var aes = Aes.Create(); aes.Key = key;
            var cipher = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7); long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            LJ.Require((path == "/v2/delete_list" ? ((long)cipher.Length + 2) / 3 * 4 : cipher.Length) <= 65536, "LIMIT");
            var query = new Dictionary<string, string> { ["appid"] = "1005", ["clientver"] = "20489", ["mid"] = mid, ["dfid"] = dfid,
                ["clienttime"] = time.ToString(System.Globalization.CultureInfo.InvariantCulture), ["key"] = PlatformProtocols.Md5($"1005{PlatformProtocols.KgSalt}20489{time}"), ["p"] = portrait };
            HttpRequestMessage request;
            if (path == "/v2/delete_list")
            {
                // This older endpoint signs and posts the BASE64 text, unlike binary sort.
                string text = Convert.ToBase64String(cipher); query["last_area"] = "gztx"; query["last_time"] = query["clienttime"];
                query["uuid"] = "-"; query["userid"] = session.UserId; query["token"] = PJ.Cookie(cookies, "token");
                query["signature"] = PlatformProtocols.Md5(PlatformProtocols.KgSalt + string.Concat(query.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + text + PlatformProtocols.KgSalt);
                request = new(HttpMethod.Post, "https://gateway.kugou.com" + path + "?" + PlatformProtocols.Form(query))
                { Content = new StringContent(text, Encoding.UTF8, "application/x-www-form-urlencoded") };
            }
            else request = new(HttpMethod.Post, "https://gateway.kugou.com" + path + "?" + PlatformProtocols.Form(query)) { Content = new ByteArrayContent(cipher) };
            request.Content!.Headers.ContentType ??= new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            foreach (var (name, value) in new[] { ("x-router", "cloudlist.service.kugou.com"), ("mid", mid), ("dfid", dfid), ("clienttime", query["clienttime"]),
                ("kg-rc", "1"), ("kg-thash", "5d816a0"), ("kg-rec", "1"), ("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F") }) request.Headers.TryAddWithoutValidation(name, value);
            using (request) { token.ThrowIfCancellationRequested(); raw = await transport(request, token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested(); LJ.Require(raw.Length is > 0 and <= 1048576, "INVALID_RESPONSE");
            try { decoded = aes.DecryptCbc(raw, iv, PaddingMode.PKCS7); }
            catch (CryptographicException) when (path == "/v1/modify_song_sort") { decoded = raw.ToArray(); }
            _ = PlatformProtocols.KgSuccess(PJ.Parse(decoded, 1048576), path);
        }
        catch (MusicLibraryException) { throw; }
        catch (MusicPlatformException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new MusicLibraryException("CLOUD_RESPONSE_UNCONFIRMED"); }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv); CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(credential);
            if (raw is not null) CryptographicOperations.ZeroMemory(raw); if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
        }
    }
}
