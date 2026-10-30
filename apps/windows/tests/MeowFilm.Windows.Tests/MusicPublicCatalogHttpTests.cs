// SPDX-License-Identifier: GPL-3.0-only
// Synthetic HTTP only. Public SDK constants are not account/device credentials.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static class MusicPublicCatalogHttpTests
{
    private const string KwAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36";
    private const string MobileAgent = "Mozilla/5.0 (Linux; U; Android 11.0.0; zh-cn; MI 11 Build/OPR1.170623.032) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30";
    private const string PlaylistAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1";
    private const string Device = "963B7AA0D21511ED807EE5846EC87D20";
    private const string Songs = """{"song":1,"album":0,"singer":0,"tagSong":1,"mvSong":0,"bestShow":1,"songlist":0,"lyricSong":0}""";
    private const string Playlists = """{"song":0,"album":0,"singer":0,"tagSong":0,"mvSong":0,"bestShow":0,"songlist":1,"lyricSong":0}""";
    private static TestCase Case(string name, Func<Task> run) => new("music.public.catalog.http." + name, run);
    public static IEnumerable<TestCase> Cases()
    {
        for (int i = 1; i <= 9; i++)
        {
            int route = i;
            yield return Case("route-" + i + "-exact-anonymous-recipe-and-owned-disposal", () => Allowed(route));
            yield return Case("route-" + i + "-unknown-missing-duplicate-query-and-fixed-profile-rejected", () => QueryDenied(route));
        }
        yield return Case("variable-limits-unicode-and-both-search-modes", Variables);
        yield return Case("noncanonical-unsafe-and-cross-platform-routes-rejected", Routes);
        yield return Case("only-explicit-fixed-public-headers-in-either-collection", Headers);
        yield return Case("mg-signature-binds-exact-keyword-fixed-device-and-timestamp", Signatures);
        yield return Case("post-single-native-id-form-only-and-bounded-producer", Forms);
        yield return Case("producer-uri-header-method-and-content-swap-before-send-rejected", MutatingProducer);
        yield return Case("producer-cancellation-retains-physical-slot-until-settle", CancelProducer);
        yield return Case("all-public-origins-remain-denied-on-ordinary-content-and-lyrics", Ordinary);
        yield return Case("no-response-cookie-forwarding-or-retention", Cookies);
        yield return Case("non2xx-verification-redirect-and-network-errors-are-finite", Failures);
        yield return Case("response-declared-stream-header-and-empty-limits", ResponseLimits);
        yield return Case("caller-can-only-reduce-response-and-deadline-budget", CallerLimits);
        yield return Case("precancellation-disposes-without-http", Precancel);
        yield return Case("cancellation-waits-for-noncooperative-handler", CancelHandler);
        yield return Case("cancellation-waits-for-noncooperative-response-read", CancelRead);
        yield return Case("cancellation-during-disposeasync-rejects-late-publication", CancelDispose);
        yield return Case("short-deadline-still-waits-for-physical-settle", Deadline);
        yield return Case("close-fences-but-does-not-detach-physical-request", Close);
        yield return Case("global4-platform2-share-ordinary-budget-until-drained", Slots);
    }
    private static Func<string, HttpRequestMessage, CancellationToken, int, TimeSpan, Task<byte[]>> Port(MusicPlatformHttp http) =>
        typeof(MusicPlatformHttp).GetMethod("SendPublicCatalogReadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, HttpRequestMessage, CancellationToken, int, TimeSpan, Task<byte[]>>>(http);
    private static Task<byte[]> Send(MusicPlatformHttp http, int route, HttpRequestMessage request,
        CancellationToken token = default, int maximum = 4194304, TimeSpan? timeout = null) =>
        Port(http)(route < 6 ? "kw" : "mg", request, token, maximum, timeout ?? TimeSpan.FromSeconds(25));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message = "Public catalog HTTP assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Failure(string code, Func<Task> action, CancellationToken token = default)
    {
        try { await action(); }
        catch (MusicPlatformException e)
        {
            Check(e.Code == code, "Expected " + code + "; got " + e.Code);
            Check(e.Message == code && e.InnerException is null && !e.ToString().Contains("fixture-private"));
            return;
        }
        catch (OperationCanceledException e) when (code == "CANCELLED") { Check(e.CancellationToken == token); return; }
        throw new InvalidOperationException("Expected finite " + code);
    }
    private static HttpResponseMessage Response(byte[]? bytes = null) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? "{'metadata':'synthetic'}"u8.ToArray()) };
    private static string Url(int route) => route switch
    {
        1 or 2 => "http://search.kuwo.cn/r.s",
        3 => "http://nplserver.kuwo.cn/pl.svc",
        4 => "https://m.kuwo.cn/newh5/singles/songinfoandlrc",
        5 => "https://www.kuwo.cn/play_detail/123",
        6 => "https://jadeite.migu.cn/music_search/v3/search/searchAll",
        7 => "https://c.musicapp.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do",
        8 => "https://c.musicapp.migu.cn/MIGUM3.0/resource/playlist/v2.0",
        9 => "https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0",
        _ => throw new InvalidOperationException()
    };
    private static Dictionary<string, string> Query(int route) => route switch
    {
        1 => new() { ["all"] = "合成 song", ["pn"] = "0", ["rn"] = "100", ["rformat"] = "json",
            ["encoding"] = "utf8", ["client"] = "kt", ["uid"] = "794762570", ["ver"] = "kwplayer_ar_9.2.2.1",
            ["vipver"] = "1", ["show_copyright_off"] = "1", ["newver"] = "1", ["ft"] = "music", ["cluster"] = "0",
            ["strategy"] = "2012", ["vermerge"] = "1", ["mobi"] = "1", ["issubtitle"] = "1" },
        2 => new() { ["all"] = "合成 song", ["pn"] = "99", ["rn"] = "1", ["rformat"] = "json",
            ["encoding"] = "utf8", ["ver"] = "mbox", ["vipver"] = "MUSIC_8.7.7.0_BCS37", ["plat"] = "pc",
            ["devid"] = "28156413", ["ft"] = "playlist", ["pay"] = "0", ["needliveshow"] = "0" },
        3 => new() { ["op"] = "getlistinfo", ["pid"] = "123", ["pn"] = "9", ["rn"] = "1000", ["encode"] = "utf8",
            ["keyset"] = "pl2012", ["identity"] = "kuwo", ["pcmp4"] = "1", ["vipver"] = "MUSIC_9.0.5.0_W1", ["newver"] = "1" },
        4 => new() { ["musicId"] = "123" },
        5 => new(),
        6 => new() { ["isCorrect"] = "0", ["isCopyright"] = "1", ["pageSize"] = "100", ["text"] = "合成 song",
            ["pageNo"] = "1", ["sort"] = "0", ["sid"] = "USS", ["searchSwitch"] = Songs },
        7 => new() { ["resourceType"] = "2" },
        8 => new() { ["playlistId"] = "123" },
        9 => new() { ["playlistId"] = "123", ["pageNo"] = "200", ["pageSize"] = "50" },
        _ => throw new InvalidOperationException()
    };
    private static string Encode(Dictionary<string, string> query) =>
        query.Count == 0 ? "" : "?" + string.Join("&", query.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
    private static HttpRequestMessage Request(int route, Dictionary<string, string>? query = null)
    {
        query ??= Query(route);
        var request = new HttpRequestMessage(route == 7 ? HttpMethod.Post : HttpMethod.Get, Url(route) + Encode(query));
        request.Headers.TryAddWithoutValidation("User-Agent", route < 6 ? KwAgent : route < 8 ? MobileAgent : PlaylistAgent);
        request.Headers.TryAddWithoutValidation("Referer", route < 6 ? "https://www.kuwo.cn/" : "https://m.music.migu.cn/");
        if (route >= 6) request.Headers.TryAddWithoutValidation("channel", "0146921");
        if (route == 6) Sign(request, query.GetValueOrDefault("text", ""), "1792728000000");
        if (route == 7) request.Content = new FormUrlEncodedContent([new("resourceId", "Abc123")]);
        return request;
    }
    private static void Header(HttpRequestMessage request, string name, string value)
    { request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value); }
    private static void Sign(HttpRequestMessage request, string keyword, string time)
    {
        Header(request, "uiVersion", "A_music_3.6.1"); Header(request, "deviceId", Device);
        Header(request, "timestamp", time);
        Header(request, "sign", Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(keyword +
            "6cdc72a439cef99a3418d2a78aa28c73yyapp2d16148780a1dcc7408e06336b98cfd50" + Device + time))));
    }
    private static async Task Disposed(HttpContent content)
    {
        try { _ = await content.ReadAsByteArrayAsync(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Owned content not disposed");
    }
    private static async Task Denied(int route, HttpRequestMessage request, string code = "ORIGIN_DENIED")
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        var content = request.Content;
        await Failure(code, () => Send(http, route, request));
        Check(calls == 0);
        if (content is not null) await Disposed(content);
        else
        {
            try { request.Content = new ByteArrayContent([]); }
            catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("Owned GET request not disposed");
        }
    }
    private static async Task Allowed(int route)
    {
        int calls = 0;
        var request = Request(route); var content = request.Content;
        string uri = request.RequestUri!.AbsoluteUri;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (sent, _) => {
            calls++; Check(sent.RequestUri!.AbsoluteUri == uri);
            Check(!sent.Headers.Contains("Cookie") && !sent.Headers.Contains("Authorization"));
            if (route == 7) Check(await sent.Content!.ReadAsStringAsync() == "resourceId=Abc123");
            else Check(sent.Content is null);
            return Response([0, 255, 128, 1]);
        }));
        Check((await Send(http, route, request)).SequenceEqual(new byte[] { 0, 255, 128, 1 }));
        Check(calls == 1);
        if (content is not null) await Disposed(content);
    }
    private static async Task QueryDenied(int route)
    {
        var extra = Query(route); extra["cookie"] = "fixture-private"; await Denied(route, Request(route, extra));
        foreach (var key in Query(route).Keys)
        {
            var missing = Query(route); missing.Remove(key); await Denied(route, Request(route, missing));
            var duplicated = Request(route); duplicated.RequestUri = new(duplicated.RequestUri!.AbsoluteUri + "&" + key + "=1");
            await Denied(route, duplicated);
            var encoded = Request(route); encoded.RequestUri = new(encoded.RequestUri!.AbsoluteUri + "&%75in=1");
            await Denied(route, encoded);
        }
        var defaults = Query(route);
        string[] variables = ["all", "pn", "rn", "pid", "musicId", "text", "pageNo", "pageSize", "playlistId"];
        foreach (var key in defaults.Keys.Where(k => !variables.Contains(k)))
        {
            var changed = Query(route); changed[key] = "fixture-private"; await Denied(route, Request(route, changed));
        }
    }
    private static async Task Variables()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(Response())));
        foreach (int route in new[] { 1, 2, 6 })
        {
            string key = route == 6 ? "text" : "all";
            foreach (string value in new[] { "汉字😀+&=%", new string('a', 200), " x " })
            {
                var q = Query(route); q[key] = value; await Send(http, route, Request(route, q));
            }
            foreach (string value in new[] { "", "  ", "x\n", new string('a', 201) })
            {
                var q = Query(route); q[key] = value; await Denied(route, Request(route, q));
            }
            foreach (string bad in new[] { "%FF", "%C0%80", "%ED%A0%80", "%00", "%GG" })
            {
                var request = Request(route); request.RequestUri = new(Url(route) + Encode(Query(route)).Replace(
                    Uri.EscapeDataString(Query(route)[key]), bad, StringComparison.Ordinal));
                await Denied(route, request);
            }
        }
        foreach (var (route, key, min, max) in new[] { (1, "pn", 0, 99), (1, "rn", 1, 100),
            (2, "pn", 0, 99), (3, "pn", 0, 9), (6, "pageNo", 1, 100), (6, "pageSize", 1, 100), (9, "pageNo", 1, 200) })
            foreach (string bad in new[] { "-1", "+1", "01", "1.0", (min - 1).ToString(), (max + 1).ToString() })
            { var q = Query(route); q[key] = bad; await Denied(route, Request(route, q)); }
        foreach (var (route, key) in new[] { (3, "pid"), (4, "musicId"), (8, "playlistId"), (9, "playlistId") })
        {
            var q = Query(route); q[key] = "99999999999999999999"; await Send(http, route, Request(route, q));
            foreach (string bad in new[] { "0", "01", "x", "1,2", "1/2", "100000000000000000000" })
            { q = Query(route); q[key] = bad; await Denied(route, Request(route, q)); }
        }
        var list = Query(6); list["isCorrect"] = "1"; list["searchSwitch"] = Playlists;
        await Send(http, 6, Request(6, list));
        list["isCorrect"] = "0"; await Denied(6, Request(6, list));
    }
    private static async Task Routes()
    {
        foreach (int route in Enumerable.Range(1, 9))
        {
            var original = Request(route).RequestUri!;
            foreach (string value in new[] { original.GetLeftPart(UriPartial.Authority) + "/neighbor" + original.Query,
                original.AbsoluteUri + "#fragment", original.AbsoluteUri.Replace("://", "://user@", StringComparison.Ordinal),
                new UriBuilder(original) { Port = 444 }.Uri.AbsoluteUri,
                original.AbsoluteUri.Replace(original.Host, original.Host + ".invalid", StringComparison.Ordinal),
                original.AbsoluteUri.Replace(original.Scheme + ":", original.Scheme == "http" ? "https:" : "http:", StringComparison.Ordinal) })
            { var request = Request(route); request.RequestUri = new(value); await Denied(route, request); }
            var method = Request(route); method.Method = route == 7 ? HttpMethod.Get : HttpMethod.Post;
            await Denied(route, method);
            var put = Request(route); put.Method = HttpMethod.Put; await Denied(route, put);
            var relative = Request(route); relative.RequestUri = new("/relative", UriKind.Relative); await Denied(route, relative);
            var body = Request(route);
            if (route != 7) { body.Content = new ByteArrayContent([]); await Denied(route, body); } else body.Dispose();
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => throw new InvalidOperationException("No HTTP")));
            foreach (string platform in new[] { "wy", "tx", "kg", "", route < 6 ? "mg" : "kw" })
                await Failure("ORIGIN_DENIED", () => Port(http)(platform, Request(route), default, 4194304, TimeSpan.FromSeconds(25)));
        }
    }
    private static async Task Headers()
    {
        foreach (int route in Enumerable.Range(1, 9))
        {
            foreach (string name in new[] { "Cookie", "Authorization", "Proxy-Authorization", "x-router",
                "x-user-id", "x-device-id", "Accept", "Origin", "Host" })
            {
                var request = Request(route); Header(request, name, "fixture-private"); await Denied(route, request);
            }
            foreach (string name in Request(route).Headers.Select(h => h.Key).ToArray())
            {
                var missing = Request(route); missing.Headers.Remove(name); await Denied(route, missing);
                var doubled = Request(route); doubled.Headers.TryAddWithoutValidation(name, "fixture-private"); await Denied(route, doubled);
            }
        }
        var wrong = Request(7); wrong.Content!.Headers.TryAddWithoutValidation("x-device-id", "fixture-private"); await Denied(7, wrong);
        var encoding = Request(7); encoding.Content!.Headers.ContentEncoding.Add("gzip"); await Denied(7, encoding);
        var charset = Request(7); charset.Content!.Headers.ContentType!.CharSet = "utf-8"; await Denied(7, charset);
        var cross = Request(7); cross.Content!.Headers.TryAddWithoutValidation("channel", "0146921"); await Denied(7, cross);
    }
    private static async Task Signatures()
    {
        foreach (var (name, value) in new[] { ("sign", new string('0', 32)), ("deviceId", new string('0', 32)),
            ("timestamp", "179272800000"), ("timestamp", "17927280000000"), ("timestamp", "0179272800000"),
            ("uiVersion", "A_music_9.9.9"), ("channel", "0000000") })
        { var request = Request(6); Header(request, name, value); await Denied(6, request); }
        var upper = Request(6); Header(upper, "sign", upper.Headers.GetValues("sign").Single().ToUpperInvariant()); await Denied(6, upper);
        var mismatch = Request(6); var q = Query(6); q["text"] = "another"; mismatch.RequestUri = new(Url(6) + Encode(q));
        await Denied(6, mismatch);
        var kw = Request(1); Header(kw, "deviceId", Device); await Denied(1, kw);
    }
    private static async Task Forms()
    {
        foreach (string text in new[] { "resourceId=a,b", "resourceId=", "resourceId=" + new string('a', 33),
            "resourceId=a&resourceId=b", "copyrightId=1", "resourceId=a&uin=1", "resourceId=%41", "%72esourceId=A" })
        {
            var request = Request(7); request.Content!.Dispose(); request.Content = Form(text);
            await Denied(7, request, "INVALID_REQUEST");
        }
        var large = Request(7); large.Content!.Dispose(); large.Content = Form(new string('a', 65537));
        await Denied(7, large);
        var overrun = Request(7); overrun.Content!.Dispose();
        overrun.Content = new ProducerContent(16, async stream => await stream.WriteAsync(new byte[65537]));
        await Denied(7, overrun, "LIMIT");
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(Response())));
        var valid = Request(7); valid.Content!.Dispose(); valid.Content = Form("resourceId=" + new string('Z', 32));
        await Send(http, 7, valid);
    }
    private static ByteArrayContent Form(string form)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(form));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded"); return content;
    }
    private static async Task MutatingProducer()
    {
        foreach (int mutation in Enumerable.Range(0, 5))
        {
            var request = Request(7); request.Content!.Dispose();
            var producer = new ProducerContent(15, async stream => {
                await Task.Yield();
                if (mutation == 0) request.RequestUri = new("https://c.musicapp.migu.cn/private");
                if (mutation == 1) Header(request, "Cookie", "fixture-private");
                if (mutation == 2) request.Method = HttpMethod.Put;
                if (mutation == 3) request.Content = Form("resourceId=XYZ");
                if (mutation == 4) request.Content!.Headers.TryAddWithoutValidation("x-router", "fixture-private");
                await stream.WriteAsync("resourceId=Abc1"u8.ToArray());
            });
            request.Content = producer;
            await Denied(7, request, mutation == 4 ? "INVALID_REQUEST" : "ORIGIN_DENIED");
            Check(producer.WasDisposed);
            if (mutation == 3) await Disposed(request.Content!);
        }
    }
    private static async Task CancelProducer()
    {
        var entered = Signal(); var release = Signal(); int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        using var cancel = new CancellationTokenSource();
        var request = Request(7); request.Content!.Dispose();
        var content = new ProducerContent(15, async stream => {
            entered.TrySetResult(); await release.Task; await stream.WriteAsync("resourceId=Abc1"u8.ToArray());
        });
        request.Content = content;
        var pending = Send(http, 7, request, cancel.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Task.Delay(40); Check(!pending.IsCompleted && !content.WasDisposed); }
        finally { release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancel.Token); Check(content.WasDisposed && calls == 0);
    }
    private static async Task Ordinary()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => throw new InvalidOperationException("No HTTP")));
        foreach (int route in Enumerable.Range(1, 9))
            foreach (string platform in new[] { "wy", "tx", "kg", "kw", "mg" })
            {
                Check(!MusicPlatformHttp.IsAllowedOrigin(platform, new(Url(route))));
                await Failure("ORIGIN_DENIED", () => http.SendAsync(platform, Request(route), default));
                var lyric = typeof(MusicPlatformHttp).GetMethod("SendLyricAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Func<string, HttpRequestMessage, CancellationToken, TimeSpan, Task<MusicPlatformHttpBytesResult>>>(http);
                await Failure("ORIGIN_DENIED", () => lyric(platform, Request(route), default, TimeSpan.FromSeconds(1)));
            }
        Check(MusicPlatformHttp.IsAllowedOrigin("wy", new("https://music.163.com/weapi/v3/song/detail")));
        Check(MusicPlatformHttp.IsAllowedOrigin("tx", new("https://u.y.qq.com/cgi-bin/musicu.fcg")));
        Check(MusicPlatformHttp.IsAllowedOrigin("kg", new("https://gateway.kugou.com/v1/test")));
    }
    private static async Task Cookies()
    {
        int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((request, _) => {
            calls++; Check(!request.Headers.Contains("Cookie"));
            var response = Response(); response.Headers.TryAddWithoutValidation("Set-Cookie", "fixture-private=x; Secure"); return Task.FromResult(response);
        }));
        var result = await Send(http, 1, Request(1)); await Send(http, 6, Request(6));
        Check(calls == 2 && !Encoding.UTF8.GetString(result).Contains("fixture-private"));
    }
    private static async Task Failures()
    {
        foreach (var (status, code) in new[] { (301, "REDIRECT_DENIED"), (302, "REDIRECT_DENIED"), (307, "REDIRECT_DENIED"),
            (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"), (429, "RATE_LIMITED"), (500, "UPSTREAM_ERROR") })
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(
                new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("fixture-private") })));
            await Failure(code, () => Send(http, 1, Request(1)));
        }
        using (var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
            var response = Response(); response.Headers.TryAddWithoutValidation("ssa-code", "fixture-private"); return Task.FromResult(response);
        }))) await Failure("VERIFICATION_REQUIRED", () => Send(http, 6, Request(6)));
        using (var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
            var response = Response(); response.RequestMessage = new(HttpMethod.Get, "https://fixture.invalid/private"); return Task.FromResult(response);
        }))) await Failure("REDIRECT_DENIED", () => Send(http, 8, Request(8)));
        int calls = 0;
        using (var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
            calls++; throw new HttpRequestException("fixture-private");
        }))) await Failure("NETWORK_ERROR", () => Send(http, 7, Request(7)));
        Check(calls == 1);
    }
    private static async Task ResponseLimits()
    {
        for (int mode = 0; mode < 4; mode++)
        {
            using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => {
                var response = Response(mode == 3 ? [] : new byte[17]);
                if (mode == 1) { response.Content.Dispose(); response.Content = new StreamContent(new MemoryStream(new byte[17])); response.Content.Headers.ContentLength = 0; }
                if (mode == 2) response.Headers.TryAddWithoutValidation("x-large", new string('x', 32769));
                return Task.FromResult(response);
            }));
            await Failure(mode == 3 ? "INVALID_RESPONSE" : "LIMIT", () => Send(http, 4, Request(4), maximum: 16));
        }
    }
    private static async Task CallerLimits()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(Response([1]))));
        foreach (int max in new[] { 0, -1, 4194305 })
            await Failure("LIMIT", () => Send(http, 4, Request(4), maximum: max));
        foreach (double time in new[] { -1, 0, 0.1, 25001 })
            await Failure("LIMIT", () => Send(http, 4, Request(4), timeout: TimeSpan.FromMilliseconds(time)));
        Check((await Send(http, 4, Request(4), maximum: 1)).Length == 1);
        var request = Request(7); var content = request.Content!;
        await Failure("LIMIT", () => Send(http, 7, request, maximum: 4194305)); await Disposed(content);
    }
    private static async Task Precancel()
    {
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => throw new InvalidOperationException("No HTTP")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var request = Request(7); var content = request.Content!;
        await Failure("CANCELLED", () => Send(http, 7, request, cancel.Token), cancel.Token); await Disposed(content);
    }
    private static async Task CancelHandler()
    {
        var entered = Signal(); var release = Signal();
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, _) => {
            entered.TrySetResult(); await release.Task; return Response();
        }));
        using var cancel = new CancellationTokenSource();
        var pending = Send(http, 1, Request(1), cancel.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Task.Delay(40); Check(!pending.IsCompleted); }
        finally { release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancel.Token);
    }
    private static async Task CancelRead()
    {
        var stream = new GateStream(blockRead: true);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var cancel = new CancellationTokenSource();
        var pending = Send(http, 9, Request(9), cancel.Token);
        try { await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Task.Delay(40); Check(!pending.IsCompleted); }
        finally { stream.Release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancel.Token); Check(stream.WasDisposed);
    }
    private static async Task CancelDispose()
    {
        var stream = new GateStream(blockRead: false);
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        using var cancel = new CancellationTokenSource();
        var pending = Send(http, 6, Request(6), cancel.Token);
        try { await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel(); await Task.Delay(40); Check(!pending.IsCompleted); }
        finally { stream.Release.TrySetResult(); }
        await Failure("CANCELLED", () => pending, cancel.Token); Check(stream.WasDisposed);
    }
    private static async Task Deadline()
    {
        var cancelled = Signal(); var release = Signal();
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, token) => {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await release.Task; return Response();
        }));
        var pending = Send(http, 8, Request(8), timeout: TimeSpan.FromMilliseconds(60));
        try { await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check(!pending.IsCompleted); }
        finally { release.TrySetResult(); }
        await Failure("TIMEOUT", () => pending);
    }
    private static async Task Close()
    {
        var entered = Signal(); var release = Signal();
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, _) => {
            entered.TrySetResult(); await release.Task; return Response();
        }));
        var pending = Send(http, 2, Request(2));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); http.Dispose(); await Task.Delay(40); Check(!pending.IsCompleted); }
        finally { release.TrySetResult(); }
        await Failure("CLOSED", () => pending);
        await Failure("CLOSED", () => Send(http, 2, Request(2)));
    }
    private static async Task Slots()
    {
        var four = Signal(); var release = Signal(); int calls = 0;
        using var http = MusicPlatformHttp.CreateWithHandler(new Handler(async (_, _) => {
            if (Interlocked.Increment(ref calls) == 4) four.TrySetResult();
            await release.Task; return Response("{}"u8.ToArray());
        }));
        using var cancel = new CancellationTokenSource();
        var tasks = new List<Task<byte[]>> { Send(http, 1, Request(1), cancel.Token), Send(http, 2, Request(2), cancel.Token) };
        try
        {
            await Failure("RATE_LIMITED", () => Send(http, 3, Request(3)));
            tasks.Add(Send(http, 6, Request(6), cancel.Token)); tasks.Add(Send(http, 8, Request(8), cancel.Token));
            await four.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancel.Cancel(); await Task.Delay(40); Check(tasks.All(t => !t.IsCompleted));
            await Failure("RATE_LIMITED", () => http.SendAsync("tx", new(HttpMethod.Get, "https://u.y.qq.com/cgi-bin/musicu.fcg"), default));
        }
        finally { release.TrySetResult(); }
        foreach (var pending in tasks) await Failure("CANCELLED", () => pending, cancel.Token);
        await Send(http, 3, Request(3)); await http.SendAsync("tx", new(HttpMethod.Get, "https://u.y.qq.com/cgi-bin/musicu.fcg"), default);
        Check(calls == 6);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
    private sealed class ProducerContent : HttpContent
    {
        private readonly long length;
        private readonly Func<Stream, Task> produce;
        internal bool WasDisposed;
        internal ProducerContent(long length, Func<Stream, Task> produce)
        {
            this.length = length; this.produce = produce;
            Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
        }
        protected override bool TryComputeLength(out long result) { result = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => produce(stream);
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
    private sealed class GateStream(bool blockRead) : Stream
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal bool WasDisposed;
        private bool read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (blockRead) { Entered.TrySetResult(); await Release.Task; }
            if (read) return 0;
            read = true; buffer.Span[0] = 1; return 1;
        }
        public override async ValueTask DisposeAsync()
        {
            if (!blockRead) { Entered.TrySetResult(); await Release.Task; }
            WasDisposed = true; GC.SuppressFinalize(this);
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
