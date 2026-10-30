// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

// Real account/client owners and protocol; only the fixed HTTP peer is synthetic.
public static class MusicWyAccountClientTests
{
    private const string Cookie = "MUSIC_U=SYNTHETIC_CLIENT_ACCOUNT; __csrf=SYNTHETIC_CLIENT_CSRF";
    private const string Update = "NMTID=SYNTHETIC_CLIENT_UPDATE; Domain=.music.163.com; Path=/; Secure";
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("validated-profile-updates-client-without-granting-account", ProfileUpdate);
        foreach (var (name, body, code) in new[]
        {
            ("missing", """{"code":200}""", "INVALID_RESPONSE"),
            ("expired", """{"code":200,"profile":null}""", "AUTH_EXPIRED"),
            ("identity", """{"code":200,"profile":{"userId":"42","nickname":"Synthetic"},"account":{"id":"43"}}""", "IDENTITY_MISMATCH"),
            ("profile", """{"code":200,"profile":{"userId":"42","nickname":"SYNTHETIC_CLIENT_ACCOUNT"}}""", "INVALID_RESPONSE"),
            ("status", """{"code":301}""", "AUTH_EXPIRED"),
            ("string-status", """{"code":"200","profile":{"userId":"42","nickname":"Synthetic"}}""", "INVALID_RESPONSE")
        })
        {
            var b = body; var c = code;
            yield return Case("invalid-profile-" + name + "-cannot-update-client", () => InvalidProfile(b, c));
        }
        yield return Case("validated-QR-steps-reuse-each-confirmed-client-state", QrUpdate);
        yield return Case("invalid-QR-key-cannot-update-client", InvalidQr);
        yield return Case("validated-SMS-steps-reuse-each-confirmed-client-state", SmsUpdate);
        foreach (string step in new[] { "login", "status" })
        {
            foreach (string kind in new[] { "identity", "phone", "captcha" })
            {
                var s = step; var k = kind;
                yield return Case("SMS-invalid-" + step + "-" + kind + "-cannot-update-client", () => InvalidSms(s, k));
            }
        }
        foreach (string step in new[] { "profile", "rights" })
        {
            var s = step;
            yield return Case("details-invalid-" + step + "-cannot-update-client", () => InvalidDetails(s));
        }
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.wy-account-client." + name, run);
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("WY account/client boundary invariant"); }
    private static bool Ok(JsonElement value) => value.GetProperty("ok").GetBoolean();
    private static string Profile(string id = "42", string accountId = "42", string nickname = "Synthetic account") => JsonSerializer.Serialize(new
    {
        code = 200, account = new { id = accountId }, profile = new { userId = id, nickname }
    });
    private static HttpResponseMessage Json(string body, params string[] cookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (cookies.Length > 0) response.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
        return response;
    }
    private static Task<JsonElement> Import(Fixture f) => f.Accounts.CallAsync("music.accounts.import",
        new { platform = "wy", cookie = Cookie });
    private static async Task MissingAccount(Fixture f)
    {
        Check(await f.Accounts.AcquireAsync("wy") is null);
        var document = await f.Store.ReadAsync(MusicAccountOwner.DocumentNamespace);
        try { Check(document.Data is null && document.Fingerprint == DocumentFingerprint.Missing); }
        finally { if (document.Data is not null) CryptographicOperations.ZeroMemory(document.Data); }
    }
    private static async Task ProfileUpdate()
    {
        MusicWyClientContext? expected = null;
        await using var f = new Fixture(request =>
        {
            Check(request.RequestUri!.AbsolutePath == "/weapi/w/nuser/account/get");
            MusicClientContextFixture.VerifyLocalWyCookie(request, expected!, Cookie);
            return Task.FromResult(Json(Profile(), Update));
        });
        expected = await f.Clients.Owner.AcquireWyAsync();
        var before = expected;
        Check((await f.Protocol.ValidateAsync("wy", Cookie)).UserId == "42");
        expected = await f.Clients.Owner.AcquireWyAsync();
        Check(expected.Generation == before.Generation + 1 && expected.Nmtid == "SYNTHETIC_CLIENT_UPDATE");
        await MissingAccount(f);
        var reply = await Import(f);
        Check(Ok(reply) && !reply.GetRawText().Contains("SYNTHETIC_CLIENT_UPDATE", StringComparison.Ordinal));
        Check(ReferenceEquals(expected, await f.Clients.Owner.AcquireWyAsync()) && f.Calls == 2);
        var account = await f.Accounts.AcquireAsync("wy");
        Check(account is not null && account.Cookie == Cookie && f.Accounts.IsCurrent(account));
    }
    private static async Task InvalidProfile(string body, string code)
    {
        await using var f = new Fixture(_ => Task.FromResult(Json(body, Update)));
        var before = await f.Clients.Owner.AcquireWyAsync();
        var reply = await Import(f);
        Check(!Ok(reply) && reply.GetProperty("code").GetString() == code);
        Check(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()) && f.Calls == 1);
        await MissingAccount(f);
    }
    private static async Task QrUpdate()
    {
        Fixture? f = null;
        int polls = 0;
        await using (f = new Fixture(async request =>
        {
            var client = await f!.Clients.Owner.AcquireWyAsync();
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/eapi/login/qrcode/unikey")
            {
                Check(client.Nmtid is null);
                return Json("""{"code":200,"unikey":"SYNTHETIC_CLIENT_QR"}""",
                    "NMTID=SYNTHETIC_QR_CLIENT; Domain=.music.163.com");
            }
            if (path == "/eapi/login/qrcode/client/login")
            {
                if (++polls == 1)
                {
                    Check(client.Nmtid == "SYNTHETIC_QR_CLIENT");
                    return Json("""{"code":801}""", "NMTID=SYNTHETIC_WAITING_CLIENT; Domain=.music.163.com");
                }
                Check(client.Nmtid == "SYNTHETIC_WAITING_CLIENT");
                return Json("""{"code":803}""", "MUSIC_U=SYNTHETIC_CLIENT_ACCOUNT; Domain=.music.163.com",
                    "__csrf=SYNTHETIC_CLIENT_CSRF; Domain=.music.163.com",
                    "NMTID=SYNTHETIC_LOGIN_CLIENT; Domain=.music.163.com");
            }
            Check(path == "/weapi/w/nuser/account/get" && client.Nmtid == "SYNTHETIC_LOGIN_CLIENT");
            MusicClientContextFixture.VerifyLocalWyCookie(request, client, Cookie);
            return Json(Profile(), Update);
        }))
        {
            var begin = await f.Accounts.CallAsync("music.accounts.qr.begin", new { platform = "wy" });
            Check(Ok(begin));
            var parameters = new
            {
                platform = "wy", challengeId = begin.GetProperty("challenge").GetProperty("challengeId").GetString()
            };
            var waiting = await f.Accounts.CallAsync("music.accounts.qr.poll", parameters);
            Check(Ok(waiting) && waiting.GetProperty("status").GetString() == "waiting");
            await MissingAccount(f);
            var reply = await f.Accounts.CallAsync("music.accounts.qr.poll", parameters);
            Check(Ok(reply) && reply.GetProperty("status").GetString() == "authenticated" && f.Calls == 4);
            Check((await f.Clients.Owner.AcquireWyAsync()).Nmtid == "SYNTHETIC_CLIENT_UPDATE");
            Check((await f.Accounts.AcquireAsync("wy"))?.Cookie == Cookie);
        }
    }
    private static async Task InvalidQr()
    {
        await using var f = new Fixture(_ => Task.FromResult(Json("""{"code":200,"unikey":"bad/key"}""", Update)));
        var before = await f.Clients.Owner.AcquireWyAsync();
        var reply = await f.Accounts.CallAsync("music.accounts.qr.begin", new { platform = "wy" });
        Check(!Ok(reply) && reply.GetProperty("code").GetString() == "INVALID_RESPONSE");
        Check(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()));
        await MissingAccount(f);
    }
    private static async Task SmsUpdate()
    {
        Fixture? f = null;
        await using (f = new Fixture(async request =>
        {
            var client = await f!.Clients.Owner.AcquireWyAsync();
            var path = request.RequestUri!.AbsolutePath;
            MusicClientContextFixture.VerifyLocalWyCookie(request, client,
                path == "/weapi/w/nuser/account/get" ? Cookie : null);
            if (path == "/weapi/sms/captcha/sent")
            {
                Check(client.Nmtid is null);
                return Json("""{"code":200}""", "NMTID=SYNTHETIC_SEND_CLIENT; Domain=.music.163.com");
            }
            if (path == "/weapi/w/login/cellphone")
            {
                Check(client.Nmtid == "SYNTHETIC_SEND_CLIENT");
                return Json(Profile(), "MUSIC_U=SYNTHETIC_CLIENT_ACCOUNT; Domain=.music.163.com",
                    "__csrf=SYNTHETIC_CLIENT_CSRF; Domain=.music.163.com",
                    "NMTID=SYNTHETIC_LOGIN_CLIENT; Domain=.music.163.com");
            }
            Check(path == "/weapi/w/nuser/account/get" && client.Nmtid == "SYNTHETIC_LOGIN_CLIENT");
            return Json(Profile(), Update);
        }))
        {
            var send = await f.Accounts.CallAsync("music.accounts.sms.send", new { platform = "wy", phone = "10000000000" });
            Check(Ok(send));
            await MissingAccount(f);
            var reply = await f.Accounts.CallAsync("music.accounts.sms.login", new
            { platform = "wy", phone = "10000000000", captcha = "864209" });
            Check(Ok(reply) && f.Calls == 3 && (await f.Accounts.AcquireAsync("wy"))?.Cookie == Cookie);
            Check((await f.Clients.Owner.AcquireWyAsync()).Nmtid == "SYNTHETIC_CLIENT_UPDATE");
        }
    }
    private static async Task InvalidSms(string step, string kind)
    {
        string InvalidProfile() => kind == "identity" ? step == "login" ? Profile(accountId: "43") : Profile("43", "43")
            : Profile(nickname: kind == "phone" ? "10000000000" : "864209");
        await using var f = new Fixture(request =>
        {
            bool login = request.RequestUri!.AbsolutePath == "/weapi/w/login/cellphone";
            Check(login || request.RequestUri.AbsolutePath == "/weapi/w/nuser/account/get");
            if (login)
                return Task.FromResult(step == "login"
                    ? Json(InvalidProfile(), "MUSIC_U=SYNTHETIC_CLIENT_ACCOUNT; Domain=.music.163.com", Update)
                    : Json(Profile(), "MUSIC_U=SYNTHETIC_CLIENT_ACCOUNT; Domain=.music.163.com"));
            return Task.FromResult(Json(InvalidProfile(), Update));
        });
        var before = await f.Clients.Owner.AcquireWyAsync();
        var reply = await f.Accounts.CallAsync("music.accounts.sms.login", new
        { platform = "wy", phone = "10000000000", captcha = "864209" });
        Check(!Ok(reply) && reply.GetProperty("code").GetString() == (kind == "identity" ? "IDENTITY_MISMATCH" : "INVALID_RESPONSE"));
        Check(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()) && f.Calls == (step == "login" ? 1 : 2));
        await MissingAccount(f);
    }
    private static async Task InvalidDetails(string step)
    {
        bool fail = false;
        await using var f = new Fixture(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/weapi/w/nuser/account/get")
                return Task.FromResult(fail && step == "profile" ? Json(Profile("43", "43"), Update) : Json(Profile()));
            Check(request.RequestUri.AbsolutePath == "/weapi/music-vip-membership/client/vip/info" && fail && step == "rights");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                code = 200, data = new { userId = "43", now, associator = new { expireTime = now + 120000 },
                    musicPackage = new { expireTime = 0 }, redplus = new { expireTime = 0 } }
            }), Update));
        });
        Check(Ok(await Import(f)));
        var before = await f.Clients.Owner.AcquireWyAsync();
        fail = true;
        try { await f.Accounts.RefreshDetailsAsync("wy", 1); throw new InvalidOperationException("Expected exact account refusal"); }
        catch (MusicAccountException error) { Check(error.Code == "IDENTITY_MISMATCH"); }
        Check(ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return send(request); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly MusicClientContextFixture.MemoryStore Store = new();
        private readonly Handler handler;
        private readonly MusicPlatformHttp http;
        internal readonly MusicClientContextFixture Clients;
        internal readonly MusicAccountProtocol Protocol;
        internal readonly MusicAccountOwner Accounts;
        internal int Calls => handler.Calls;
        internal Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            handler = new(send); http = MusicPlatformHttp.CreateWithHandler(handler);
            Clients = new(http, Store); Protocol = Clients.CreateAccountProtocol();
            Accounts = Clients.CreateAccountOwner(Store, Protocol);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Accounts.DisposeAsync(); }
            finally { try { await Clients.DisposeAsync(); } finally { http.Dispose(); await Store.DisposeAsync(); } }
        }
    }
}
