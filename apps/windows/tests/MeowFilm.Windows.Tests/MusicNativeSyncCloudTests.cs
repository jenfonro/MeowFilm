using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Sync;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    public static IEnumerable<TestCase> CloudCases()
    {
        yield return new("Music sync KG cloud delete encrypted request and success", () => CloudRoundTrip("/v2/delete_list"));
        yield return new("Music sync KG cloud order encrypted request and success", () => CloudRoundTrip("/v1/modify_song_sort"));
        yield return new("Music sync KG cloud encrypted business errors remain classified", CloudBusinessErrors);
        yield return new("Music sync KG cloud only order accepts explicit plaintext response", CloudPlaintext);
        yield return new("Music sync KG cloud corrupt ciphertext is never successful", CloudCorrupt);
        yield return new("Music sync KG cloud response limits zero owned buffers", CloudResponseLimits);
        yield return new("Music sync KG cloud encrypted JSON validation remains strict", CloudJson);
        yield return new("Music sync KG cloud request limit prevents dispatch", CloudRequestLimit);
        yield return new("Music sync KG cloud credentials and identity reject before dispatch", CloudCredentials);
        yield return new("Music sync KG cloud local identity is independent of legacy account device fields", CloudLocalIdentity);
        yield return new("Music sync KG cloud caller cancellation before dispatch", CloudPreCancelled);
        yield return new("Music sync KG cloud in-flight cancellation disposes request", CloudCancelled);
        yield return new("Music sync KG cloud transport errors never retry or expose context", CloudTransportErrors);
        yield return new("Music sync KG cloud native entropy is fresh without public test hook", CloudEntropy);
        yield return new("Music sync KG cloud order ACK requires full readback", CloudOrder);
        yield return new("Music sync KG cloud unknown delete survives reopen without replay", CloudDeleteRecovery);
        yield return new("Music sync KG cloud stale session after dispatch cannot ACK", CloudStaleSession);
        yield return new("Music sync KG cloud declared readback list identity is strict", CloudListIdentity);
    }

    private const string CloudSecret = "a1b2c3";
    private static JsonObject CloudBody(string path) => path == "/v2/delete_list" ?
        new() { ["listid"] = 101, ["total_ver"] = 0, ["type"] = 1 } :
        new() { ["listid"] = 101, ["list_ver"] = 1, ["type"] = 0,
            ["data"] = new JsonArray(new JsonObject { ["fileid"] = 5001, ["sort"] = 0 }, new JsonObject { ["fileid"] = 5000, ["sort"] = 1 }) };
    private static string CloudMd5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    private static byte[] CloudCipher(byte[] plain)
    {
        string digest = CloudMd5(CloudSecret);
        using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(digest[..16]);
        return aes.EncryptCbc(plain, Encoding.ASCII.GetBytes(digest[16..]), PaddingMode.PKCS7);
    }
    private static byte[] CloudCipher(string text) => CloudCipher(Encoding.UTF8.GetBytes(text));
    private static byte[] CloudDecode(byte[] cipher)
    {
        string digest = CloudMd5(CloudSecret);
        using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(digest[..16]);
        return aes.DecryptCbc(cipher, Encoding.ASCII.GetBytes(digest[16..]), PaddingMode.PKCS7);
    }
    // Same-module codec unit coverage fixes only the entropy input. No production public crypto/test hook,
    // mutable singleton, request.Options secret, platform private key or reference code execution is used.
    // Coordinator integration below uses only the public constructor, owner and injected HTTP delegate.
    private static Task CloudCall(string path, Func<HttpRequestMessage, CancellationToken, Task<byte[]>> send,
        CancellationToken token = default, JsonObject? body = null, MusicPlatformSession? session = null, bool nativeEntropy = false)
    {
        var type = typeof(MusicLibrarySyncService).Assembly.GetType("MeowFilm.Capabilities.Sync.MusicSyncKgCloud", true)!;
        var method = type.GetMethod(nativeEntropy ? "SendAsync" : "SendCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] args = nativeEntropy ? [session ?? Sessions.Make("kg", 1, "42"), MusicClientContextFixture.Kg, path, body ?? CloudBody(path), send, token] :
            [session ?? Sessions.Make("kg", 1, "42"), MusicClientContextFixture.Kg, path, body ?? CloudBody(path), send, token, CloudSecret];
        try { return (Task)method.Invoke(null, args)!; }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static async Task CloudFailure(string expected, Func<Task> run)
    {
        try { await run(); }
        catch (OperationCanceledException) when (expected == "CANCELLED") { return; }
        catch (MusicLibraryException e) { Check(e.Code == expected, "EXPECTED_" + expected + "_GOT_" + e.Code); return; }
        catch (MusicPlatformException e) { Check(e.Code == expected, "EXPECTED_" + expected + "_GOT_" + e.Code); return; }
        throw new InvalidOperationException("EXPECTED_CLOUD_FAILURE_" + expected);
    }
    private static Dictionary<string, string> CloudQuery(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    private static async Task CloudDisposed(HttpRequestMessage? request)
    {
        Check(request is not null);
        try { _ = await request!.Content!.ReadAsByteArrayAsync(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("CLOUD_REQUEST_NOT_DISPOSED");
    }
    private static async Task CloudRoundTrip(string path)
    {
        int calls = 0; HttpRequestMessage? captured = null; byte[] response = CloudCipher("""{"status":1,"error_code":0,"data":{}}""");
        await CloudCall(path, async (request, token) =>
        {
            calls++; captured = request; token.ThrowIfCancellationRequested();
            Check(request.Method == HttpMethod.Post && request.RequestUri!.Scheme == "https" &&
                request.RequestUri.Host == "gateway.kugou.com" && request.RequestUri.Port == 443 &&
                request.RequestUri.AbsolutePath == path && request.Headers.GetValues("x-router").SequenceEqual(["cloudlist.service.kugou.com"]));
            Check(request.Headers.Host is null && !request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Forwarded-For"));
            var query = CloudQuery(request); Check(query["appid"] == "1005" && query["clientver"] == "20489" && query["mid"] == "123456789");
            Check(query["p"].Length == 256 && query["p"].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'));
            Check(query["key"] == CloudMd5("1005OIlwieks28dk2k092lksi2UIkp20489" + query["clienttime"]));
            Check(request.Headers.GetValues("clienttime").Single() == query["clienttime"]);
            byte[] raw = await request.Content!.ReadAsByteArrayAsync(token), cipher = raw;
            if (path == "/v2/delete_list")
            {
                string text = Encoding.UTF8.GetString(raw);
                cipher = Convert.FromBase64String(text);
                Check(request.Content.Headers.ContentType!.MediaType == "application/x-www-form-urlencoded");
                string signed = "OIlwieks28dk2k092lksi2UIkp" +
                    string.Concat(query.Where(p => p.Key != "signature").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) +
                    text + "OIlwieks28dk2k092lksi2UIkp";
                Check(query["signature"] == CloudMd5(signed) && query["userid"] == "42" && query["last_area"] == "gztx");
            }
            else
            {
                Check(request.Content.Headers.ContentType!.MediaType == "application/json");
                Check(!query.ContainsKey("token") && !query.ContainsKey("userid") && !query.ContainsKey("signature"));
            }
            Check(JsonNode.DeepEquals(JsonNode.Parse(CloudDecode(cipher)), CloudBody(path)));
            Check(cipher.SequenceEqual(CloudCipher(CloudBody(path).ToJsonString())));
            return response;
        });
        Check(calls == 1 && response.All(b => b == 0)); await CloudDisposed(captured);
    }
    private static async Task CloudBusinessErrors()
    {
        foreach (string path in new[] { "/v2/delete_list", "/v1/modify_song_sort" })
            foreach (var (json, code) in new[] {
                ("""{"status":0,"error_code":20018}""", "AUTH_REQUIRED"),
                ("""{"status":0,"error_code":20028}""", "VERIFICATION_REQUIRED"),
                ("""{"status":0,"error_code":5}""", "UPSTREAM_ERROR"),
                ("""{"status":1,"error_code":0,"ssaCode":"synthetic"}""", "VERIFICATION_REQUIRED") })
            {
                byte[] bytes = CloudCipher(json); int calls = 0;
                await CloudFailure(code, () => CloudCall(path, (_, _) => { calls++; return Task.FromResult(bytes); }));
                Check(calls == 1 && bytes.All(b => b == 0));
            }
    }
    private static async Task CloudPlaintext()
    {
        await CloudCall("/v1/modify_song_sort", (_, _) => Task.FromResult(Encoding.UTF8.GetBytes("""{"status":1,"error_code":0}""")));
        await CloudFailure("CLOUD_RESPONSE_UNCONFIRMED", () => CloudCall("/v2/delete_list",
            (_, _) => Task.FromResult(Encoding.UTF8.GetBytes("""{"status":1,"error_code":0}"""))));
    }
    private static async Task CloudCorrupt()
    {
        foreach (var (path, code) in new[] { ("/v2/delete_list", "CLOUD_RESPONSE_UNCONFIRMED"), ("/v1/modify_song_sort", "INVALID_RESPONSE") })
        {
            var bytes = new byte[] { 255, 0, 128 };
            await CloudFailure(code, () => CloudCall(path, (_, _) => Task.FromResult(bytes)));
            Check(bytes.All(b => b == 0));
        }
    }
    private static async Task CloudResponseLimits()
    {
        foreach (int length in new[] { 0, 1048577 })
        {
            byte[] bytes = Enumerable.Repeat((byte)42, length).ToArray();
            await CloudFailure("INVALID_RESPONSE", () => CloudCall("/v1/modify_song_sort", (_, _) => Task.FromResult(bytes)));
            Check(bytes.All(b => b == 0));
        }
    }
    private static async Task CloudJson()
    {
        foreach (byte[] plain in new[] { Encoding.UTF8.GetBytes("""{"status":1,"status":0}"""), new byte[] { 255 },
            Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("""{"status":1,"error_code":""") })
            await CloudFailure("INVALID_RESPONSE", () => CloudCall("/v2/delete_list", (_, _) => Task.FromResult(CloudCipher(plain))));
    }
    private static async Task CloudRequestLimit()
    {
        int calls = 0; var body = CloudBody("/v1/modify_song_sort"); body["synthetic"] = new string('x', 66000);
        await CloudFailure("LIMIT", () => CloudCall("/v1/modify_song_sort", (_, _) => { calls++; return Task.FromResult(Array.Empty<byte>()); }, body: body));
        Check(calls == 0);
    }
    private static async Task CloudCredentials()
    {
        int calls = 0;
        foreach (var (cookie, code) in new[] {
            ("userid=77; token=synthetic-only; KUGOU_API_MID=123", "INVALID_CREDENTIALS"),
            ("userid=42; token=synthetic-only; KUGOU_API_MID=bad", "DEVICE_CONTEXT_REQUIRED"),
            ("userid=42; token=synthetic-only; KUGOU_API_MID=", "DEVICE_CONTEXT_REQUIRED"),
            ("userid=42; token=synthetic-only; KUGOU_API_MID=123; dfid=bad", "DEVICE_CONTEXT_REQUIRED"),
            ("userid=42; KUGOU_API_MID=123", "AUTH_REQUIRED"),
            ("userid=42; token=" + new string('x', 150) + "; KUGOU_API_MID=123", "INVALID_CREDENTIALS") })
            await CloudFailure(code, () => CloudCall("/v1/modify_song_sort", (_, _) => { calls++; return Task.FromResult(Array.Empty<byte>()); },
                session: new("kg", "synthetic", 1, "42", cookie)));
        await CloudFailure("INVALID_REQUEST", () => CloudCall("/wrong-path", (_, _) => { calls++; return Task.FromResult(Array.Empty<byte>()); }));
        Check(calls == 0);
    }
    private static async Task CloudLocalIdentity()
    {
        foreach (string cookie in new[] { "userid=42; token=synthetic-only", "userid=42; token=synthetic-only; dfid=SYNTHETIC_UNBOUND" })
        {
            int calls = 0;
            await CloudCall("/v1/modify_song_sort", (request, _) =>
            {
                calls++; var query = CloudQuery(request);
                Check(query["mid"] == MusicClientContextFixture.Kg.Mid && query["dfid"] == "-" &&
                    request.Headers.GetValues("mid").Single() == query["mid"] &&
                    request.Headers.GetValues("dfid").Single() == query["dfid"]);
                return Task.FromResult(CloudCipher("""{"status":1,"error_code":0}"""));
            }, session: new("kg", "synthetic", 1, "42", cookie));
            Check(calls == 1);
        }
    }
    private static async Task CloudPreCancelled()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel(); int calls = 0;
        await CloudFailure("CANCELLED", () => CloudCall("/v1/modify_song_sort", (_, _) => { calls++; return Task.FromResult(Array.Empty<byte>()); }, cts.Token, nativeEntropy: true));
        Check(calls == 0);
    }
    private static async Task CloudCancelled()
    {
        using var cts = new CancellationTokenSource(); int calls = 0; HttpRequestMessage? captured = null;
        await CloudFailure("CANCELLED", () => CloudCall("/v1/modify_song_sort", async (request, token) =>
        { captured = request; calls++; cts.Cancel(); await Task.Delay(Timeout.Infinite, token); return []; }, cts.Token));
        Check(calls == 1); await CloudDisposed(captured);
    }
    private static async Task CloudTransportErrors()
    {
        foreach (string code in new[] { "ORIGIN_DENIED", "LIMIT", "REDIRECT_DENIED", "AUTH_REQUIRED", "FORBIDDEN", "RATE_LIMITED", "UPSTREAM_ERROR", "NETWORK_ERROR", "TIMEOUT", "CLOSED" })
        {
            int calls = 0; HttpRequestMessage? captured = null;
            await CloudFailure(code, () => CloudCall("/v1/modify_song_sort", (request, _) =>
            { calls++; captured = request; throw new MusicPlatformException(code); }));
            Check(calls == 1); await CloudDisposed(captured);
        }
        await CloudFailure("CLOUD_RESPONSE_UNCONFIRMED", () => CloudCall("/v1/modify_song_sort", (_, _) => throw new InvalidOperationException("synthetic-secret-must-not-escape")));
    }
    private static async Task CloudEntropy()
    {
        var portraits = new HashSet<string>(); var bodies = new HashSet<string>();
        for (int i = 0; i < 2; i++) await CloudCall("/v1/modify_song_sort", async (request, token) =>
        {
            var q = CloudQuery(request); Check(!q.ContainsKey("token") && q["p"].Length == 256);
            Check(portraits.Add(q["p"]) && bodies.Add(Convert.ToHexString(await request.Content!.ReadAsByteArrayAsync(token))));
            return Encoding.UTF8.GetBytes("""{"status":1,"error_code":0}""");
        }, nativeEntropy: true);
    }
    private static async Task InstallCloud(Fixture f, Func<HttpRequestMessage, CancellationToken, Task<byte[]>> transport)
    { await f.Sync.DisposeAsync(); f.Sync = new(f.Owner, f.Sessions, f.Http, f.Clients.Owner, transport); }
    private static async Task CloudOrder()
    {
        await using var f = new Fixture("kg"); await Reorder(f); int writes = 0;
        await InstallCloud(f, async (request, token) =>
        {
            writes++; Check(request.RequestUri!.AbsolutePath == "/v1/modify_song_sort");
            Check((await request.Content!.ReadAsByteArrayAsync(token)).Length > 0);
            f.Server.Favorite.Songs.Reverse(); f.Server.Favorite.Files.Reverse();
            return Encoding.UTF8.GetBytes("""{"status":1,"error_code":0}""");
        });
        Ok(await f.Run()); Check(writes == 1 && f.Server.Writes == 0 && f.Server.Favorite.Songs.SequenceEqual([2, 1]));
        Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null);
        Ok(await f.Run()); Check(writes == 1);
    }
    private static async Task CloudDeleteRecovery()
    {
        await using var f = new Fixture("kg"); f.Server.Entries.Add(new("0:701", "Local")); await f.Enable(false, true); Ok(await f.Run());
        var c = Ok(await f.Owner.ExecuteAsync("music.library.list")).GetProperty("items").EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "playlist");
        Ok(await f.Owner.ExecuteAsync("music.library.remove", new { id = c.GetProperty("id").GetString(), ifRevision = c.GetProperty("revision").GetInt64() }));
        int writes = 0;
        await InstallCloud(f, (request, _) =>
        {
            writes++; Check(request.RequestUri!.AbsolutePath == "/v2/delete_list"); f.Server.FailReads = true;
            // Lost/corrupt response: no assertion that the remote side accepted the deletion.
            return Task.FromResult(new byte[] { 1, 2, 3 });
        });
        Code(await f.Run(), "CONFIRMATION_REQUIRED"); await Confirm(f); Code(await f.Run(), "UPSTREAM_ERROR");
        Check(writes == 1 && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").GetProperty("operation").GetString() == "delete");
        await f.Restart(); f.Server.FailReads = false;
        Code(await f.Run(), "DELETE_UNCONFIRMED"); Check(writes == 1);
        f.Server.Entries.RemoveAll(e => e.Id == "0:701"); Ok(await f.Run());
        Check((await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Null && writes == 1);
    }
    private static async Task CloudStaleSession()
    {
        await using var f = new Fixture("kg"); await Reorder(f); int writes = 0;
        await InstallCloud(f, (_, _) =>
        {
            writes++; f.Sessions.Values["kg"] = Sessions.Make("kg", 2, "77");
            return Task.FromResult(Encoding.UTF8.GetBytes("""{"status":1,"error_code":0}"""));
        });
        Code(await f.Run(), "STALE_SESSION");
        Check(writes == 1 && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Object);
        Code(await f.Run(), "ACCOUNT_REPLACED"); Check(writes == 1);
    }
    private static async Task CloudListIdentity()
    {
        foreach (var (field, value) in new[] { ("listid", "999"), ("list_create_userid", "77"), ("type", "1"), ("global_collection_id", "wrong") })
        {
            await using var f = new Fixture("kg"); f.Server.Favorite.Add(1); await f.Enable();
            f.Server.KgListInfo = info => info[field] = value;
            Code(await f.Run(), "IDENTITY_MISMATCH"); Check(f.Server.Writes == 0);
        }
    }
}
