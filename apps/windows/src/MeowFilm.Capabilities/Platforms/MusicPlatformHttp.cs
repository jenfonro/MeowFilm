// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>Native composition port for platform content and account protocols. Not a UI/RPC URL-fetch API.</summary>
public sealed partial class MusicPlatformHttp : IDisposable
{
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    // In-flight count is lifetime bookkeeping only. The transport never throttles;
    // request volume is owned by business logic (search concurrency, caches, paging).
    private int active;
    private bool closed, cancellationSent;
    public MusicPlatformHttp() : this(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 32
    }) { }
    private MusicPlatformHttp(HttpMessageHandler handler) =>
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    public static MusicPlatformHttp CreateWithHandler(HttpMessageHandler trustedHandler)
    { ArgumentNullException.ThrowIfNull(trustedHandler); return new(trustedHandler); }

    public static bool IsAllowedOrigin(string platform, Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && (platform switch {
            "wy" => uri.Host is "music.163.com" or "interfacepc.music.163.com" ||
                uri.Host == "interface.music.163.com" && uri.AbsolutePath == "/api/bsr/sk/get" ||
                uri.Host == "interface3.music.163.com" && uri.AbsolutePath is "/xeapi/v3/discovery/recommend/songs" or "/xeapi/homepage/category/daily/song/list" or "/xeapi/song/enhance/player/url/v1",
            "tx" => uri.Host == "u.y.qq.com", "kg" => uri.Host == "gateway.kugou.com" ||
                uri.Host == "lyrics.kugou.com" && uri.AbsolutePath is "/v1/search" or "/download", _ => false });
    private static int HeaderBytes(HttpHeaders headers)
    {
        var count = 0;
        foreach (var header in headers)
        {
            count += header.Key.Length;
            foreach (var value in header.Value) count += value.Length;
            if (count > 32768) break;
        }
        return count;
    }

    // Ownership of request transfers on every call, including rejected calls. Do not reuse it.
    public Task<MusicPlatformHttpResult> SendAsync(string platform, HttpRequestMessage request,
        CancellationToken cancellationToken, int maxResponseBytes = 4 * 1024 * 1024, TimeSpan? timeout = null)
        => SendCoreAsync(platform, request, cancellationToken, maxResponseBytes, timeout,
            (bytes, response, _) => JsonResult(PJ.Parse(bytes, maxResponseBytes), response));
    internal Task<MusicPlatformHttpResult> SendXeapiAsync(HttpRequestMessage request,
        CancellationToken token, int maximum, TimeSpan timeout)
        => SendCoreAsync("wy", request, token, maximum, timeout,
            (bytes, response, ct) => JsonResult(PlatformXeapi.DecodeResponse(bytes, maximum, ct), response));
    private static MusicPlatformHttpResult JsonResult(JsonObject body, HttpResponseMessage response) =>
        new(body, response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToArray() : []);

    // Native write protocols use their own exact route policy, not the catalog allowlist.
    // A cancellation/deadline revokes publication but MUST NOT detach the physical write.
    internal Task<MusicPlatformHttpResult> SendFeedbackJsonAsync(string platform,
        HttpRequestMessage request, CancellationToken token) =>
        SendCoreAsync(platform, request, token, 65536, TimeSpan.FromSeconds(20),
            (bytes, response, _) => JsonResult(PJ.Parse(bytes, 65536), response),
            nativePolicy: NativeHttpPolicy.Feedback);

    internal Task<byte[]> SendMusicLibrarySyncCloudAsync(HttpRequestMessage request, CancellationToken token) =>
        SendCoreAsync("kg", request, token, 1048576, TimeSpan.FromSeconds(20),
            (bytes, _, _) => { PJ.Require(bytes.Length > 0, "INVALID_RESPONSE"); return bytes; },
            nativePolicy: NativeHttpPolicy.MusicLibrarySyncCloud);

    internal Task<MusicPlatformHttpResult> SendQqDeviceJsonAsync(MusicQqDeviceHttpStep step,
        HttpRequestMessage request, CancellationToken token) =>
        SendCoreAsync("tx", request, token, 65536, TimeSpan.FromSeconds(20),
            (bytes, response, _) => JsonResult(PJ.Parse(bytes, 65536), response),
            nativePolicy: step switch {
                MusicQqDeviceHttpStep.Qimei => NativeHttpPolicy.QqQimei,
                MusicQqDeviceHttpStep.Account => NativeHttpPolicy.QqAccount,
                MusicQqDeviceHttpStep.Profile => NativeHttpPolicy.QqProfile,
                MusicQqDeviceHttpStep.Session => NativeHttpPolicy.QqSession,
                _ => NativeHttpPolicy.Denied });

    internal Task<MusicPlatformHttpResult> SendQqFeedbackJsonAsync(HttpRequestMessage request, CancellationToken token) =>
        SendCoreAsync("tx", request, token, 65536, TimeSpan.FromSeconds(20),
            (bytes, response, _) => JsonResult(PJ.Parse(bytes, 65536), response),
            nativePolicy: NativeHttpPolicy.QqFeedbackJson);

    internal Task<byte[]> SendQqFeedbackReportAsync(HttpRequestMessage request, CancellationToken token) =>
        SendCoreAsync("tx", request, token, 65536, TimeSpan.FromSeconds(20),
            (bytes, _, _) => { PJ.Require(bytes.Length > 0, "INVALID_RESPONSE"); return bytes; },
            nativePolicy: NativeHttpPolicy.QqFeedbackReport);

    internal Task<MusicPlatformHttpResult> SendChorusAsync(HttpRequestMessage request, CancellationToken token, TimeSpan timeout) =>
        SendCoreAsync("kg", request, token, 65536, timeout,
            (bytes, response, _) => JsonResult(PJ.Parse(bytes, 65536), response),
            nativePolicy: NativeHttpPolicy.Chorus);
    internal Task<MusicPlatformHttpResult> SendVipPageEapiAsync(HttpRequestMessage request,
        CancellationToken token, int maximum) =>
        SendCoreAsync("wy", request, token, maximum, TimeSpan.FromSeconds(20),
            (bytes, response, ct) => JsonResult(PlatformXeapi.DecodeResponse(bytes, maximum, ct), response),
            nativePolicy: NativeHttpPolicy.VipPage);
    internal Task<MusicPlatformHttpResult> SendVipPageWeapiAsync(HttpRequestMessage request,
        CancellationToken token, int maximum) =>
        SendCoreAsync("wy", request, token, maximum, TimeSpan.FromSeconds(20),
            (bytes, response, _) => JsonResult(PJ.Parse(bytes, maximum), response),
            nativePolicy: NativeHttpPolicy.VipPage);
    private enum NativeHttpPolicy { Feedback, MusicLibrarySyncCloud, QqQimei, QqAccount, QqProfile,
        QqSession, QqFeedbackJson, QqFeedbackReport, PublicCatalogRead, AccountDetails, Chorus, VipPage, Denied }
    // HttpHeaders.Contains throws for a known header in the other header collection.
    // Enumerate names when checking cross-collection smuggling, without normalizing values.
    private static bool HasHeader(HttpHeaders headers, string name) =>
        headers.Any(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase));
    private static bool HeaderIs(HttpRequestMessage request, string name, string expected) =>
        request.Headers.TryGetValues(name, out var values) &&
        values.SequenceEqual([expected], StringComparer.Ordinal) &&
        (request.Content is null || !HasHeader(request.Content.Headers, name));
    private static bool NoHeader(HttpRequestMessage request, string name) =>
        !HasHeader(request.Headers, name) && (request.Content is null || !HasHeader(request.Content.Headers, name));
    private static bool NativeOrigin(NativeHttpPolicy policy, string platform, HttpRequestMessage request)
    {
        if (policy == NativeHttpPolicy.Chorus) return platform == "kg" && request.Method == HttpMethod.Get && request.Content is null &&
            request.RequestUri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0, Fragment.Length: 0,
                Host: "expendablekmrcdn.kugou.com", AbsolutePath: "/v1/audio_climax/audio" } point && point.Query.StartsWith("?data=", StringComparison.Ordinal) &&
            point.Query.Length <= 512 && !point.Query.Contains('&') && !request.Headers.Any();
        if (policy == NativeHttpPolicy.PublicCatalogRead) return PublicCatalogOrigin(platform, request);
        if (policy == NativeHttpPolicy.AccountDetails) return AccountDetailsOrigin(platform, request);
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || request.Method != HttpMethod.Post) return false;
        return policy switch {
            NativeHttpPolicy.Feedback => platform == "wy" && uri.Query.Length == 0 &&
                (uri.Host == "music.163.com" && uri.AbsolutePath is "/weapi/w/nuser/account/get" or "/weapi/v3/song/detail" ||
                 uri.Host == "clientlog.music.163.com" && uri.AbsolutePath == "/eapi/feedback/weblog"),
            NativeHttpPolicy.MusicLibrarySyncCloud => platform == "kg" && uri.Host == "gateway.kugou.com" &&
                uri.AbsolutePath is "/v2/delete_list" or "/v1/modify_song_sort" &&
                request.Headers.TryGetValues("x-router", out var router) &&
                router.SequenceEqual(["cloudlist.service.kugou.com"], StringComparer.Ordinal) &&
                request.Content?.Headers.Contains("x-router") != true,
            NativeHttpPolicy.QqQimei => platform == "tx" && uri.Host == "api.tencentmusic.com" &&
                uri.AbsolutePath == "/tme/trpc/proxy" && uri.Query.Length == 0 &&
                HeaderIs(request, "method", "GetQimei") &&
                HeaderIs(request, "service", "trpc.tme_datasvr.qimeiproxy.QimeiProxy") &&
                HeaderIs(request, "appid", "qimei_qq_android") &&
                NoHeader(request, "Cookie") && NoHeader(request, "Authorization"),
            NativeHttpPolicy.QqAccount or NativeHttpPolicy.QqProfile or NativeHttpPolicy.QqSession or
                NativeHttpPolicy.QqFeedbackJson => platform == "tx" && uri.Host == "u.y.qq.com" &&
                uri.AbsolutePath == "/cgi-bin/musicu.fcg" && uri.Query.Length == 0 &&
                (policy != NativeHttpPolicy.QqSession || NoHeader(request, "Cookie") && NoHeader(request, "Authorization")),
            NativeHttpPolicy.QqFeedbackReport => platform == "tx" && uri.Host == "stat6.y.qq.com" &&
                uri.AbsolutePath == "/android/fcgi-bin/imusic_tj" && uri.Query.Length == 0 &&
                request.Content is not null && request.Content.Headers.ContentEncoding.SequenceEqual(["gzip"], StringComparer.Ordinal) &&
                !HasHeader(request.Headers, "Content-Encoding"),
            NativeHttpPolicy.VipPage => platform == "wy" && uri.Query.Length == 0 &&
                ((uri.Host == "interface.music.163.com" &&
                    uri.AbsolutePath is ("/eapi/vipnewcenter/app/minidesk/music/sign/pc" or
                        "/eapi/vipnewcenter/app/resource/common/list")) ||
                 (uri.Host == "music.163.com" &&
                    uri.AbsolutePath == "/weapi/vip-center-bff/task/sign")),
            _ => false };
    }

    // Request inspection belongs to the tracked request and its deadline. Never buffer
    // an arbitrary producer without a limit or release a cancelled producer early.
    private static async Task CheckNativeBody(NativeHttpPolicy? policy, HttpRequestMessage request, CancellationToken token)
    {
        if (policy == NativeHttpPolicy.AccountDetails)
        {
            await CheckAccountDetailsBody(request, token).ConfigureAwait(false);
            return;
        }
        if (policy == NativeHttpPolicy.PublicCatalogRead)
        {
            await CheckPublicCatalogBody(request, token).ConfigureAwait(false);
            return;
        }
        if (policy == NativeHttpPolicy.VipPage)
        {
            PJ.Require(request.Content is not null &&
                request.Content.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded" &&
                request.Content.Headers.ContentEncoding.Count == 0, "INVALID_REQUEST");
            try { await request.Content!.LoadIntoBufferAsync(65536, token).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new MusicPlatformException("LIMIT"); }
            byte[] formBytes = await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                PJ.Require(formBytes.Length is > 0 and <= 65536 &&
                    formBytes.All(value => value is >= 0x20 and <= 0x7e),
                    "INVALID_REQUEST");
                var fields = ParseFormFields(formBytes);
                if (request.RequestUri!.Host == "interface.music.163.com")
                    PJ.Require(fields.Count == 1 && fields.TryGetValue("params", out var eapiParams) &&
                        eapiParams.Length is > 0 and <= 65536 && eapiParams.Length % 32 == 0 &&
                        eapiParams.All(Uri.IsHexDigit), "INVALID_REQUEST");
                else
                    PJ.Require(fields.Count == 2 && fields.TryGetValue("params", out var weapiParams) &&
                        weapiParams.Length > 0 && fields.TryGetValue("encSecKey", out var key) &&
                        key.Length == 256 && key.All(Uri.IsHexDigit), "INVALID_REQUEST");
            }
            finally { CryptographicOperations.ZeroMemory(formBytes); }
            return;
        }
        if (policy is null or NativeHttpPolicy.Feedback or NativeHttpPolicy.MusicLibrarySyncCloud or NativeHttpPolicy.Chorus) return;
        PJ.Require(request.Content is not null, "INVALID_REQUEST");
        try { await request.Content!.LoadIntoBufferAsync(65536, token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new MusicPlatformException("LIMIT"); }
        byte[] bytes = await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            PJ.Require(bytes.Length is > 0 and <= 65536, "LIMIT");
            if (policy == NativeHttpPolicy.QqFeedbackReport) return; // XML/gzip validation belongs to its typed consumer.
            PJ.Require(request.Content.Headers.ContentType?.MediaType == "application/json" &&
                request.Content.Headers.ContentEncoding.Count == 0, "INVALID_REQUEST");
            var body = PJ.Parse(bytes, 65536);
            static bool Fields(JsonObject value, params string[] keys) =>
                value.Count == keys.Length && keys.All(value.ContainsKey);
            if (policy == NativeHttpPolicy.QqQimei)
            {
                PJ.Require(Fields(body, "app", "os", "qimeiParams") && PJ.Scalar(body["app"]) == "0" &&
                    PJ.Scalar(body["os"]) == "1" && body["qimeiParams"] is JsonObject, "INVALID_REQUEST");
                var q = body["qimeiParams"]!.AsObject();
                PJ.Require(Fields(q, "key", "params", "time", "nonce", "sign", "extra") &&
                    q.All(pair => pair.Value is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var s) &&
                        s.Length > 0) && PJ.Text(q["extra"]) == """{"appKey":"0AND0HD6FE4HY80F"}""", "INVALID_REQUEST");
                return;
            }
            PJ.Require(Fields(body, "comm", "req_0") && body["comm"] is JsonObject && body["req_0"] is JsonObject,
                "INVALID_REQUEST");
            var req = body["req_0"]!.AsObject();
            PJ.Require(Fields(req, "module", "method", "param") && req["param"] is JsonObject, "INVALID_REQUEST");
            string module = PJ.Text(req["module"]), method = PJ.Text(req["method"]);
            PJ.Require(policy switch {
                NativeHttpPolicy.QqAccount => module == "music.UserInfo.userInfoServer" && method == "GetLoginUserInfo",
                NativeHttpPolicy.QqProfile => module == "music.UnifiedHomepage.UnifiedHomepageSrv" && method == "GetHomepageHeader",
                NativeHttpPolicy.QqSession => module == "music.getSession.session" && method == "GetSession",
                NativeHttpPolicy.QqFeedbackJson => module == "music.trackInfo.UniformRuleCtrl" && method == "CgiGetTrackInfo" ||
                    module == "music.vkey.GetVkey" && method == "UrlGetVkey",
                _ => false }, "ORIGIN_DENIED");
            if (policy == NativeHttpPolicy.QqSession)
            {
                var comm = body["comm"]!.AsObject();
                PJ.Require(Fields(comm, "ct", "cv", "v", "chid", "tmeAppID", "traceid", "QIMEI36", "OpenUDID",
                    "udid", "OpenUDID2", "aid", "os_ver", "phonetype"), "INVALID_REQUEST");
                PJ.Require(comm.All(pair => pair.Value is System.Text.Json.Nodes.JsonValue value &&
                    value.TryGetValue<string>(out var s) && s.Length > 0) && PJ.Text(comm["ct"]) == "11", "INVALID_REQUEST");
                var param = req["param"]!.AsObject();
                PJ.Require(Fields(param, "uid", "vkey", "caller") && param["uid"] is System.Text.Json.Nodes.JsonValue uid &&
                    uid.TryGetValue<string>(out _) && PJ.Scalar(param["vkey"]) == "0" &&
                    PJ.Scalar(param["caller"]) is "1" or "2", "INVALID_REQUEST");
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static Dictionary<string, string> ParseFormFields(byte[] bytes)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in System.Text.Encoding.ASCII.GetString(bytes).Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) throw new MusicPlatformException("INVALID_REQUEST");
            var name = DecodeFormField(pair[..separator]);
            var value = DecodeFormField(pair[(separator + 1)..]);
            if (name.Length == 0 || !fields.TryAdd(name, value))
                throw new MusicPlatformException("INVALID_REQUEST");
        }
        return fields;
    }

    private static string DecodeFormField(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (value[index] == '%' &&
                (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) ||
                    !Uri.IsHexDigit(value[index + 2])))
                throw new MusicPlatformException("INVALID_REQUEST");
        try { return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal)); }
        catch (UriFormatException) { throw new MusicPlatformException("INVALID_REQUEST"); }
    }

    // Separate policy: these origins NEVER enter IsAllowedOrigin/ordinary content or lyrics.
    internal Task<MusicAccountHttpResult> SendAccountStepAsync(MusicAccountHttpStep step,
        HttpRequestMessage request, CancellationToken token, TimeSpan remaining) =>
        SendCoreAsync<MusicAccountHttpResult>(AccountPlatform(step), request, token,
            step == MusicAccountHttpStep.QqImage ? 524288 : step == MusicAccountHttpStep.KgRegister ? 1048576 : 65536, remaining,
            (bytes, response, _) => new(bytes,
                response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToArray() : [],
                response.Headers.Location?.OriginalString, response.Content.Headers.ContentType?.MediaType,
                (int)response.StatusCode), step);

    private static string AccountPlatform(MusicAccountHttpStep step) => step switch
    {
        MusicAccountHttpStep.QqImage or MusicAccountHttpStep.QqPoll or MusicAccountHttpStep.QqSignature or
            MusicAccountHttpStep.QqAuthorize => "tx",
        MusicAccountHttpStep.KgRegister or MusicAccountHttpStep.KgQrKey or MusicAccountHttpStep.KgQrCheck => "kg",
        MusicAccountHttpStep.WySmsSend or MusicAccountHttpStep.WySmsLogin => "wy",
        _ => ""
    };
    private static bool AccountOrigin(MusicAccountHttpStep step, HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return step switch {
            MusicAccountHttpStep.WySmsSend => request.Method == HttpMethod.Post && uri.Host == "music.163.com" &&
                uri.AbsolutePath == "/weapi/sms/captcha/sent" && uri.Query.Length == 0,
            MusicAccountHttpStep.WySmsLogin => request.Method == HttpMethod.Post && uri.Host == "music.163.com" &&
                uri.AbsolutePath == "/weapi/w/login/cellphone" && uri.Query.Length == 0,
            MusicAccountHttpStep.QqImage => request.Method == HttpMethod.Get && uri.Host == "ssl.ptlogin2.qq.com" && uri.AbsolutePath == "/ptqrshow",
            MusicAccountHttpStep.QqPoll => request.Method == HttpMethod.Get && uri.Host == "ssl.ptlogin2.qq.com" && uri.AbsolutePath == "/ptqrlogin",
            MusicAccountHttpStep.QqSignature => request.Method == HttpMethod.Get && uri.Host == "ssl.ptlogin2.graph.qq.com" && uri.AbsolutePath == "/check_sig",
            MusicAccountHttpStep.QqAuthorize => request.Method == HttpMethod.Post && uri.Host == "graph.qq.com" && uri.AbsolutePath == "/oauth2.0/authorize" && uri.Query.Length == 0,
            MusicAccountHttpStep.KgRegister => request.Method == HttpMethod.Post && uri.Host == "userservice.kugou.com" &&
                uri.AbsolutePath == "/risk/v2/r_register_dev",
            MusicAccountHttpStep.KgQrKey => request.Method == HttpMethod.Get && uri.Host == "login-user.kugou.com" &&
                uri.AbsolutePath == "/v2/qrcode",
            MusicAccountHttpStep.KgQrCheck => request.Method == HttpMethod.Get && uri.Host == "login-user.kugou.com" &&
                uri.AbsolutePath == "/v2/get_userinfo_qrcode",
            _ => false };
    }

    // Lyrics decode their encrypted/base64 representation in the native consumer.
    // Response cookies remain transient; cancellation includes physical cleanup.
    internal Task<MusicPlatformHttpBytesResult> SendLyricAsync(string platform, HttpRequestMessage request,
        CancellationToken token, TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        if (uri is null || !IsAllowedOrigin(platform, uri) || !(platform switch {
            "wy" => uri.Host == "interfacepc.music.163.com" && uri.AbsolutePath == "/eapi/song/lyric/v1",
            "tx" => uri.Host == "u.y.qq.com" && uri.AbsolutePath == "/cgi-bin/musicu.fcg",
            "kg" => uri.Host == "lyrics.kugou.com" && uri.AbsolutePath is "/v1/search" or "/download",
            _ => false }))
        { request.Dispose(); throw new MusicPlatformException("ORIGIN_DENIED"); }
        return SendCoreAsync(platform, request, token, 1024 * 1024, remaining,
            (bytes, response, _) => new MusicPlatformHttpBytesResult(bytes,
                response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.ToArray() : []));
    }
    private async Task<T> SendCoreAsync<T>(string platform, HttpRequestMessage request,
        CancellationToken cancellationToken, int maxResponseBytes, TimeSpan? timeout,
        Func<byte[], HttpResponseMessage, CancellationToken, T> decode, MusicAccountHttpStep? accountStep = null,
        NativeHttpPolicy? nativePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var interval = timeout ?? TimeSpan.FromSeconds(25);
        CancellationTokenSource deadline, linked;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PJ.Require(maxResponseBytes >= 1 && interval > TimeSpan.Zero);
            PJ.Require(request.RequestUri is not null && (nativePolicy is { } policy
                ? accountStep is null && NativeOrigin(policy, platform, request)
                : accountStep is { } step
                    ? platform.Length > 0 && platform == AccountPlatform(step) && AccountOrigin(step, request)
                    : IsAllowedOrigin(platform, request.RequestUri)), "ORIGIN_DENIED");
            PJ.Require(request.Method == HttpMethod.Post || request.Method == HttpMethod.Get);
            PJ.Require(request.Content is null || request.Content.Headers.ContentLength is >= 0 and <= 65536, "LIMIT");
            PJ.Require(HeaderBytes(request.Headers) +
                (nativePolicy is not null && request.Content is not null ? HeaderBytes(request.Content.Headers) : 0) <= 32768, "LIMIT");
            PJ.Require(request.Headers.Host is null || request.Headers.Host == request.RequestUri!.Authority, "ORIGIN_DENIED");
            lock (gate)
            {
                PJ.Require(!closed, "CLOSED");
                deadline = new(interval);
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, lifetime.Token);
                active++;
            }
        }
        catch { request.Dispose(); throw; }
        var token = linked.Token;
        var destination = request.RequestUri;
        async Task<T> Physical()
        {
            try
            {
                using (request)
                {
                await CheckNativeBody(nativePolicy, request, token).ConfigureAwait(false);
                if (nativePolicy is NativeHttpPolicy.QqQimei or NativeHttpPolicy.QqAccount or NativeHttpPolicy.QqProfile or
                    NativeHttpPolicy.QqSession or NativeHttpPolicy.QqFeedbackJson or NativeHttpPolicy.QqFeedbackReport or
                    NativeHttpPolicy.PublicCatalogRead or NativeHttpPolicy.AccountDetails or NativeHttpPolicy.Chorus or
                    NativeHttpPolicy.VipPage)
                {
                    // The body producer may have awaited. Recheck before the first byte
                    // can leave; a redirected response check alone is too late here.
                    token.ThrowIfCancellationRequested();
                    PJ.Require(request.RequestUri == destination && NativeOrigin(nativePolicy.Value, platform, request) &&
                        (request.Headers.Host is null || request.Headers.Host == destination!.Authority), "ORIGIN_DENIED");
                    PJ.Require(HeaderBytes(request.Headers) +
                        (request.Content is null ? 0 : HeaderBytes(request.Content.Headers)) <= 32768, "LIMIT");
                }
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    token.ThrowIfCancellationRequested();
                    PJ.Require(response.RequestMessage?.RequestUri is null || response.RequestMessage.RequestUri == destination, "REDIRECT_DENIED");
                    var status = (int)response.StatusCode;
                    bool accountRedirect = status == 302 && accountStep is MusicAccountHttpStep.QqSignature or MusicAccountHttpStep.QqAuthorize;
                    // Only SMS parses the fixed business error body on non-success HTTP.
                    // The protocol still forbids accepting code=200 with a failed HTTP status.
                    bool smsError = status is >= 400 and <= 599 &&
                        accountStep is MusicAccountHttpStep.WySmsSend or MusicAccountHttpStep.WySmsLogin;
                    if (status is >= 300 and < 400 && !accountRedirect) throw new MusicPlatformException("REDIRECT_DENIED");
                    if (!smsError && status == 401) throw new MusicPlatformException("AUTH_REQUIRED");
                    if (!smsError && status == 403) throw new MusicPlatformException("FORBIDDEN");
                    if (!smsError && status == 429) throw new MusicPlatformException("RATE_LIMITED");
                    PJ.Require(status is >= 200 and < 300 || accountRedirect || smsError, "UPSTREAM_ERROR");
                    if (nativePolicy is NativeHttpPolicy.QqQimei or NativeHttpPolicy.QqFeedbackReport)
                        PJ.Require(status == 200, "UPSTREAM_ERROR");
                    if (nativePolicy == NativeHttpPolicy.VipPage)
                        PJ.Require(status == 200, "UPSTREAM_ERROR");
                    PJ.Require(HeaderBytes(response.Headers) + HeaderBytes(response.Content.Headers) <= 32768, "LIMIT");
                    if (response.Headers.TryGetValues("ssa-code", out var ssa) && ssa.Any(v => v.Length > 0))
                        throw new MusicPlatformException("VERIFICATION_REQUIRED");
                    PJ.Require((response.Content.Headers.ContentLength ?? 0) <= maxResponseBytes, "LIMIT");
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var output = new MemoryStream();
                    var buffer = new byte[16384];
                    while (true)
                    {
                        var n = await stream.ReadAsync(buffer, token);
                        token.ThrowIfCancellationRequested();
                        if (n == 0) break;
                        PJ.Require(output.Length + n <= maxResponseBytes, "LIMIT");
                        output.Write(buffer, 0, n);
                    }
                    var data = decode(output.ToArray(), response, token);
                    token.ThrowIfCancellationRequested();
                    return data;
                }
                }
            }
            finally
            {
                linked.Dispose(); deadline.Dispose();
                lock (gate)
                {
                    active--;
                    if (closed && cancellationSent && active == 0) lifetime.Dispose();
                }
            }
        }
        try
        {
            // Cancellation revokes publication, not ownership of handler/stream cleanup.
            var result = await Physical();
            // Includes stream/request disposal: cancellation during cleanup cannot publish a late ACK.
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (MusicPlatformException) { throw; }
        catch (OperationCanceledException)
        {
            lock (gate) if (closed) throw new MusicPlatformException("CLOSED");
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new MusicPlatformException("TIMEOUT");
        }
        catch { throw new MusicPlatformException("NETWORK_ERROR"); }
    }
    public void Dispose()
    {
        lock (gate) { if (closed) return; closed = true; }
        try { lifetime.Cancel(); }
        catch (AggregateException) { /* A trusted handler's callback cannot reopen the transport. */ }
        finally
        {
            client.Dispose();
            lock (gate)
            {
                cancellationSent = true;
                if (active == 0) lifetime.Dispose();
            }
        }
        // Non-cooperative trusted handlers remain tracked with their CTS until they settle.
    }
}

internal enum MusicAccountHttpStep { QqImage, QqPoll, QqSignature, QqAuthorize, KgRegister, KgQrKey, KgQrCheck, WySmsSend, WySmsLogin }
internal enum MusicQqDeviceHttpStep { Qimei, Account, Profile, Session }
internal sealed class MusicAccountHttpResult(byte[] bytes, string[] cookies, string? location, string? mediaType, int status = 200)
{
    internal int Status { get; } = status;
    [JsonIgnore] internal byte[] Bytes { get; } = bytes;
    [JsonIgnore] internal string[] Cookies { get; } = cookies;
    [JsonIgnore] internal string? Location { get; } = location;
    [JsonIgnore] internal string? MediaType { get; } = mediaType;
    public override string ToString() => "MusicAccountHttpResult (redacted)";
}

internal sealed class MusicPlatformHttpBytesResult(byte[] bytes, string[] cookies)
{
    [JsonIgnore] internal byte[] Bytes { get; } = bytes;
    [JsonIgnore] internal string[] Cookies { get; } = cookies;
    public override string ToString() => "MusicPlatformHttpBytesResult (redacted)";
}

/// <summary>Transient native protocol result. Set-Cookie is never persisted or shared by transport.</summary>
public sealed class MusicPlatformHttpResult
{
    internal MusicPlatformHttpResult(JsonObject body, string[] setCookies) { Body = body; SetCookieHeaders = Array.AsReadOnly(setCookies); }
    [JsonIgnore] public JsonObject Body { get; }
    [JsonIgnore] public IReadOnlyList<string> SetCookieHeaders { get; }
    public override string ToString() => "MusicPlatformHttpResult (redacted)";
}
