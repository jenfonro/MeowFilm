// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeAccountDetailsTests
{
    private static IEnumerable<TestCase> HttpCases()
    {
        yield return Case("HTTP-fixed-three-platform-read-recipes-no-SetCookie", HttpRecipes);
        yield return Case("HTTP-ordinary-and-public-catalog-not-widened", HttpSeparate);
        foreach (string fault in new[] { "http", "port", "fragment", "userinfo", "host", "path", "method",
            "cookie", "authorization", "cross-platform", "duplicate-query", "signature", "router", "body-batch", "body-pair", "body-comm" })
        { var f = fault; yield return Case("HTTP-denies-" + f, () => HttpDenied(f)); }
        yield return Case("HTTP-header-request-response-byte-budgets", HttpBudgets);
        yield return Case("HTTP-redirect-non2xx-ssa-invalid-json-finite", HttpResponses);
        yield return Case("HTTP-cancelled-body-producer-retains-shared-slot", HttpProducer);
        foreach (string mutation in new[] { "uri", "header", "content" })
        { var m = mutation; yield return Case("HTTP-awaiting-producer-cannot-mutate-" + m, () => HttpMutation(m)); }
        yield return Case("HTTP-deadline-includes-handler-and-physical-settle", HttpDeadline);
    }
    private static HttpRequestMessage Request(string platform, bool profile = false)
    {
        string cookie = MusicAccountDetailsFixture.Cookie(platform);
        if (platform == "wy") return MusicPlatformRequests.WyWithCookie(profile ? "/api/w/nuser/account/get" :
            "/api/music-vip-membership/client/vip/info", profile ? new() : new() { ["userId"] = UserId }, cookie, weapi: true);
        if (platform == "tx") return MusicPlatformRequests.TxWithCookie("VipLogin.VipLoginInter", "vip_login_base", new(), cookie);
        var request = MusicPlatformRequests.KgWithCookie(profile ? "/v3/get_my_info" : "/v1/get_union_vip",
            profile ? new() { ["plat"] = 1 } : new() { ["busi_type"] = "concept" }, cookie, "123456789",
            profile ? "usercenter.kugou.com" : null, profile ? new() { ["visit_time"] = 1800000000, ["usertype"] = 1,
                ["userid"] = JsonNode.Parse(UserId), ["p"] = new string('A', 256) } : null);
        if (!profile) request.RequestUri = new UriBuilder(request.RequestUri!) { Host = "kugouvip.kugou.com" }.Uri;
        return request;
    }
    private sealed class DetailsHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return run(request, cancellationToken); }
    }
    private static HttpResponseMessage Response(string text = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static async Task HttpRecipes()
    {
        var handler = new DetailsHandler((_, _) => {
            var response = Response();
            response.Headers.TryAddWithoutValidation("Set-Cookie", "SYNTHETIC_NEW_TOKEN=not-published");
            return Task.FromResult(response);
        });
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        foreach (string platform in new[] { "wy", "kg" })
            foreach (bool profile in new[] { false, true })
            {
                var result = await http.SendAccountDetailsAsync(platform, Request(platform, profile), default, TimeSpan.FromSeconds(1));
                Check(result.SetCookieHeaders.Count == 0);
            }
        foreach (var pair in new[] { ("VipLogin.VipLoginInter", "vip_login_base"),
            ("music.UserInfo.userInfoServer", "GetLoginUserInfo"), ("music.UnifiedHomepage.UnifiedHomepageSrv", "GetHomepageHeader") })
        {
            var request = MusicPlatformRequests.TxWithCookie(pair.Item1, pair.Item2, pair.Item2 == "GetHomepageHeader"
                ? new() { ["uin"] = MusicAccountDetailsFixture.Euin, ["IsQueryTabDetail"] = 1 } : new(),
                MusicAccountDetailsFixture.Cookie("tx"));
            await http.SendAccountDetailsAsync("tx", request, default, TimeSpan.FromSeconds(1));
        }
        Check(handler.Calls == 7);
    }
    private static async Task HttpSeparate()
    {
        var handler = new DetailsHandler((_, _) => Task.FromResult(Response()));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
            Check(!MusicPlatformHttp.IsAllowedOrigin(platform, new("https://kugouvip.kugou.com/v1/get_union_vip")));
        await Error(() => http.SendAsync("kg", Request("kg"), default), "ORIGIN_DENIED");
        await Error(() => http.SendCatalogAsync("kg", Request("kg"), default), "ORIGIN_DENIED");
        await Error(() => http.SendPublicCatalogReadAsync("kg", Request("kg"), default, 1024, TimeSpan.FromSeconds(1)), "ORIGIN_DENIED");
        Check(handler.Calls == 0);
    }
    private static async Task HttpDenied(string fault)
    {
        var handler = new DetailsHandler((_, _) => Task.FromResult(Response()));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        string platform = fault.StartsWith("body-", StringComparison.Ordinal) ? "tx" : "kg";
        var request = Request(platform);
        var uri = new UriBuilder(request.RequestUri!);
        string code = "ORIGIN_DENIED";
        switch (fault)
        {
            case "http": uri.Scheme = "http"; uri.Port = -1; request.RequestUri = uri.Uri; break;
            case "port": uri.Port = 444; request.RequestUri = uri.Uri; break;
            case "fragment": uri.Fragment = "fragment"; request.RequestUri = uri.Uri; break;
            case "userinfo": uri.UserName = "not-allowed"; request.RequestUri = uri.Uri; break;
            case "host": request.Headers.Host = "gateway.kugou.com"; break;
            case "path": uri.Path = "/v1/other"; request.RequestUri = uri.Uri; break;
            case "method": request.Method = HttpMethod.Post; break;
            case "cookie": request.Headers.TryAddWithoutValidation("Cookie", "SYNTHETIC=secret"); break;
            case "authorization": request.Headers.TryAddWithoutValidation("Authorization", "Bearer SYNTHETIC"); break;
            case "cross-platform": platform = "wy"; break;
            case "duplicate-query": uri.Query += "&userid=" + UserId; request.RequestUri = uri.Uri; break;
            case "signature":
                uri.Query = uri.Query[..uri.Query.LastIndexOf("signature=", StringComparison.Ordinal)] + "signature=" + new string('0', 32);
                request.RequestUri = uri.Uri; code = "INVALID_REQUEST"; break;
            case "router": request.Headers.TryAddWithoutValidation("x-router", "usercenter.kugou.com"); break;
            default:
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
                if (fault == "body-batch") body["req_1"] = body["req_0"]!.DeepClone();
                else if (fault == "body-pair") body["req_0"]!["method"] = "GetLoginUserInfo";
                else body["comm"]!["device"] = "SYNTHETIC";
                request.Content.Dispose(); request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                code = fault == "body-pair" ? "ORIGIN_DENIED" : "INVALID_REQUEST"; break;
        }
        await Error(() => http.SendAccountDetailsAsync(platform, request, default, TimeSpan.FromSeconds(1)), code);
        Check(handler.Calls == 0);
    }
    private static async Task HttpBudgets()
    {
        var handler = new DetailsHandler((_, _) => Task.FromResult(Response()));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        var request = Request("wy");
        request.Headers.Remove("Cookie"); request.Headers.TryAddWithoutValidation("Cookie", "MUSIC_U=" + new string('x', 33000));
        // Credential grammar is intentionally checked before the generic byte budget.
        await Error(() => http.SendAccountDetailsAsync("wy", request, default, TimeSpan.FromSeconds(1)), "INVALID_CREDENTIALS");
        request = Request("tx");
        request.Content!.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; synthetic=" + new string('x', 33000));
        await Error(() => http.SendAccountDetailsAsync("tx", request, default, TimeSpan.FromSeconds(1)), "LIMIT");
        request = Request("tx");
        request.Content!.Dispose(); request.Content = new StringContent(new string('x', 65537), Encoding.UTF8, "application/json");
        await Error(() => http.SendAccountDetailsAsync("tx", request, default, TimeSpan.FromSeconds(1)), "LIMIT");
        var streaming = Request("tx");
        streaming.Content!.Dispose(); streaming.Content = new UnboundedProducer(65537);
        await Error(() => http.SendAccountDetailsAsync("tx", streaming, default, TimeSpan.FromSeconds(1)), "LIMIT");
        Check(handler.Calls == 0);
        using var large = MusicPlatformHttp.CreateWithHandler(new DetailsHandler((_, _) => Task.FromResult(Response(new string('x', 1048577)))));
        await Error(() => large.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromSeconds(1)), "LIMIT");
        using var streamed = MusicPlatformHttp.CreateWithHandler(new DetailsHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLengthStream(1048577)) })));
        await Error(() => streamed.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromSeconds(1)), "LIMIT");
    }
    private static async Task HttpResponses()
    {
        foreach (var entry in new[] { (302, "REDIRECT_DENIED"), (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"),
            (429, "RATE_LIMITED"), (500, "UPSTREAM_ERROR") })
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new DetailsHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage((HttpStatusCode)entry.Item1) { Content = new StringContent("{}") })));
            await Error(() => http.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromSeconds(1)), entry.Item2);
        }
        foreach (var fault in new[] { "redirected", "ssa", "duplicate", "empty", "non-json" })
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new DetailsHandler((_, _) => {
                var response = Response(fault == "duplicate" ? """{"code":0,"code":0}""" : fault == "empty" ? "" : fault == "non-json" ? "secret-error" : "{}");
                if (fault == "redirected") response.RequestMessage = new(HttpMethod.Get, "https://untrusted.invalid/");
                if (fault == "ssa") response.Headers.TryAddWithoutValidation("ssa-code", "SYNTHETIC");
                return Task.FromResult(response);
            }));
            await Error(() => http.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromSeconds(1)),
                fault == "ssa" ? "VERIFICATION_REQUIRED" : fault == "redirected" ? "REDIRECT_DENIED" : "INVALID_RESPONSE");
        }
    }
    private sealed class Producer(byte[] bytes, Task release, Action entered) : HttpContent
    {
        internal bool Disposed;
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { entered(); await release; await stream.WriteAsync(bytes); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class UnboundedProducer(int count) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 10; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[count]).AsTask();
    }
    private sealed class UnknownLengthStream(int count) : MemoryStream(new byte[count])
    { public override bool CanSeek => false; }
    private static async Task HttpProducer()
    {
        var handler = new DetailsHandler((_, _) => Task.FromResult(Response()));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        var release = MusicAccountDetailsFixture.Signal(); int entered = 0;
        using var cancellation = new CancellationTokenSource();
        async Task Run()
        {
            var request = Request("tx"); byte[] bytes = await request.Content!.ReadAsByteArrayAsync(); request.Content.Dispose();
            request.Content = new Producer(bytes, release.Task, () => Interlocked.Increment(ref entered));
            request.Content.Headers.ContentType = new("application/json");
            await http.SendAccountDetailsAsync("tx", request, cancellation.Token, TimeSpan.FromSeconds(2));
        }
        var first = Run(); var second = Run();
        try
        {
            await Until(() => entered == 2); cancellation.Cancel(); await Pending(first); await Pending(second);
            await Error(() => http.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromSeconds(1)), "RATE_LIMITED");
            // Same physical counter is shared with ordinary content, not a separate account pool.
            await Error(() => http.SendAsync("tx", Request("tx"), default), "RATE_LIMITED");
        }
        finally { release.TrySetResult(); }
        await Error(() => first, "CANCELLED"); await Error(() => second, "CANCELLED");
        Check(handler.Calls == 0);
        await http.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromSeconds(1));
        Check(handler.Calls == 1);
    }
    private static async Task HttpMutation(string mutation)
    {
        var handler = new DetailsHandler((_, _) => Task.FromResult(Response()));
        using var http = MusicPlatformHttp.CreateWithHandler(handler);
        var request = Request("tx"); byte[] bytes = await request.Content!.ReadAsByteArrayAsync(); request.Content.Dispose();
        var entered = MusicAccountDetailsFixture.Signal(); var release = MusicAccountDetailsFixture.Signal();
        var producer = new Producer(bytes, release.Task, () => entered.TrySetResult());
        producer.Headers.ContentType = new("application/json"); request.Content = producer;
        var task = http.SendAccountDetailsAsync("tx", request, default, TimeSpan.FromSeconds(2));
        await Wait(entered.Task);
        if (mutation == "uri") request.RequestUri = new("https://untrusted.invalid/");
        else if (mutation == "header") request.Headers.TryAddWithoutValidation("Cookie", "SYNTHETIC=secret");
        else request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        release.TrySetResult(); await Error(() => task, "ORIGIN_DENIED", "INVALID_REQUEST");
        Check(handler.Calls == 0 && producer.Disposed);
    }
    private static async Task HttpDeadline()
    {
        var entered = MusicAccountDetailsFixture.Signal(); var cancelled = MusicAccountDetailsFixture.Signal(); var release = MusicAccountDetailsFixture.Signal();
        using var http = MusicPlatformHttp.CreateWithHandler(new DetailsHandler(async (_, token) => {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; return Response();
        }));
        var task = http.SendAccountDetailsAsync("tx", Request("tx"), default, TimeSpan.FromMilliseconds(50));
        try { await Wait(entered.Task); await Wait(cancelled.Task); await Pending(task); }
        finally { release.TrySetResult(); }
        await Error(() => task, "TIMEOUT");
    }
}
