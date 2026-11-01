// SPDX-License-Identifier: GPL-3.0-only
// Actual native service -> request builders -> bounded HTTP; synthetic peer/session only.
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchDiscoveryTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("fixed-wire-first-three-twenty-normalized-exact-order", WireAndShape);
        yield return Case("anonymous-never-borrows-auth-and-account-cookie-stays-native", Credentials);
        yield return Case("independent-primary-and-chart-reads-run-without-a-shared-request-quota", Parallel);
        yield return Case("display-lists-ignore-unused-row-count-within-response-bytes", DisplayRows);
        yield return Case("cache-copy-isolation-rotation-no-network", Cache);
        yield return Case("successful-response-client-generation-keeps-completed-cache", () => ClientCache(true));
        yield return Case("ordinary-client-cookie-update-keeps-rotation-and-completed-cache", () => ClientCache(false));
        yield return Case("authoritative-client-reload-fences-rotation-and-completed-cache", () => ClientCache(false, reload: true));
        yield return Case("empty-default-and-empty-sections-valid", Empty);
        yield return Case("default-hot-dedup-and-score-null", Dedup);
        foreach (var stage in new[] { "hot", "toplist", "default", "chart" })
        {
            var value = stage;
            yield return Case("partial-" + value, () => Partial(value));
        }
        yield return Case("both-primary-fail-default-alone-not-success", BothPrimaryFail);
        yield return Case("failed-refresh-retains-completed-cache", FailedRefresh);
        foreach (var fault in new[] { "playlist-id", "duplicate-track", "zero-track", "duplicate-song", "missing-song", "alien-song", "too-many-songs" })
        {
            var value = fault;
            yield return Case("identity-" + value, () => Identity(value));
        }
        yield return Case("empty-chart-no-detail-request", EmptyChart);
        yield return Case("embedded-chart-members-reused-no-song-detail", () => EmbeddedMembers(false));
        yield return Case("partial-chart-members-request-only-missing-display-rows", () => EmbeddedMembers(true));
        yield return Case("duplicate-chart-identities-fail-toplist", DuplicateCharts);
        foreach (var stage in new[] { "hot", "toplist", "default", "chart", "songs" })
        {
            var value = stage;
            yield return Case("invalid-" + value + "-does-not-update-client-context", () => UntrustedClientResponse(value));
        }
        yield return Case("expired-primary-authorization-is-auth-required-without-client-update", () => ExpiredAuthorization(true));
        yield return Case("expired-partial-authorization-is-auth-required-without-client-update", () => ExpiredAuthorization(false));
        foreach (var fault in new[] { "hot-nonstring", "default-nonstring", "score-negative", "keyword-control", "keyword-limit",
            "duplicate-json-key", "response-limit", "invalid-json" })
        {
            var value = fault;
            yield return Case("bounded-" + value, () => Bounded(value));
        }
        yield return Case("whole-snapshot-budget", WholeBudget);
        yield return Case("safe-json-fields-and-artwork", SafeFields);
        foreach (var pair in new[] { (401, "AUTH_REQUIRED"), (403, "FORBIDDEN"), (429, "RATE_LIMITED"),
            (302, "REDIRECT_DENIED"), (503, "UPSTREAM_ERROR"), (460, "VERIFICATION_REQUIRED") })
        {
            var value = pair;
            yield return Case("finite-error-" + value.Item1, () => FiniteError(value.Item1, value.Item2));
        }
        yield return Case("handler-exception-never-leaks", HandlerFailure);
        yield return Case("coalesced-reader-cancel-does-not-cancel-other", Coalescing);
        foreach (var stage in new[] { "handler", "read", "dispose" })
        {
            var value = stage;
            yield return Case("last-cancel-physical-" + value, () => Physical(value, "cancel"));
        }
        yield return Case("single-deadline-25s-awaits-physical", () => Physical("handler", "timeout"));
        yield return Case("invalidate-rejects-result-awaits-physical", () => Physical("handler", "invalidate"));
        yield return Case("prepare-drains-physical", () => Physical("handler", "prepare"));
        yield return Case("dispose-drains-physical-and-borrows-inputs", () => Physical("dispose", "close"));
        yield return Case("invalidation-then-read-drains-old-before-replacement", () => Replacement(false));
        yield return Case("refresh-drains-old-before-replacement", () => Replacement(true));
        yield return Case("cancelled-last-reader-replacement-cannot-join-retiring", LastReaderReplacement);
        yield return Case("anonymous-to-account-final-acquire-fenced", AnonymousChanged);
        yield return Case("final-acquire-same-generation-cookie-change-fenced", CookieChanged);
        yield return Case("account-revoked-during-http-without-event-fenced", Revoked);
        yield return Case("account-acquire-cancellation-drains-before-return", AcquireDrain);
        yield return Case("cached-session-current-check-and-nonwy-invalidation", CacheSession);
        yield return Case("acquire-error-redacted-no-http", AcquireError);
        yield return Case("initial-acquire-wrong-platform-and-stale-refused", InvalidSession);
        yield return Case("pre-cancelled-no-acquire-or-http", PreCancelled);
        yield return Case("prepare-retains-cache-close-idempotent", PrepareAndClose);
        yield return Case("rotate-generation-refuses-empty-or-revoked", RotateFence);
    }
    private static TestCase Case(string name, Func<Task> run) => new("music.search.discovery." + name, run);
    private static async Task EmbeddedMembers(bool partial)
    {
        await using var f = new Fixture();
        f.Peer.Reply = (wire, _) =>
        {
            var data = Default(wire);
            if (wire.Path == "/api/v6/playlist/detail")
            {
                Check(wire.Data["n"]!.GetValue<int>() == 20);
                int first = int.Parse(Id(wire)) * 100 + 1;
                data["playlist"]!["tracks"] = new JsonArray(Enumerable.Range(first, partial ? 19 : 20)
                    .Reverse().Select(i => (JsonNode)Song(i)).ToArray());
            }
            if (wire.Path == "/api/v3/song/detail")
                Check(partial && SongIds(wire).Length == 1 && SongIds(wire)[0] % 100 == 20);
            return Task.FromResult(Response(data));
        };
        var result = await f.Service.ReadAsync();
        Check(result.Charts.Length == 3 && result.Charts.All(chart => chart.Songs.Length == 20));
        Check(f.Peer.Calls.Count(call => call.Path == "/api/v3/song/detail") == (partial ? 3 : 0));
        Check(f.Peer.Calls.Count == (partial ? 9 : 6));
    }
    private static void Check(bool condition, string message = "Discovery assertion")
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5));
    private static async Task<MusicPlatformException> Failure(string code, Func<Task> action)
    {
        try { await action(); }
        catch (MusicPlatformException error)
        {
            Check(error.Code == code, "Expected " + code + ", got " + error.Code);
            Check(error.Message == code && error.InnerException is null &&
                error.ToString() == "MusicPlatformException: " + code, "Unsafe error");
            return error;
        }
        throw new InvalidOperationException("Expected " + code);
    }
    private static MusicPlatformSession Account(long generation = 1, string platform = "wy", string cookie = "MUSIC_U=synthetic-only; __csrf=fixture") =>
        new(platform, "discovery-fixture", generation, "123", cookie);

    private sealed class Sessions : IMusicPlatformSessionSource, IDisposable
    {
        internal MusicPlatformSession? Value;
        internal int Count, Disposed;
        internal bool RejectCurrent;
        internal Func<int, CancellationToken, ValueTask<MusicPlatformSession?>>? Acquire;
        internal Func<MusicPlatformSession, bool>? Current;
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken token)
        {
            Check(platform == "wy", "Wrong platform");
            int n = Interlocked.Increment(ref Count);
            return Acquire?.Invoke(n, token) ?? ValueTask.FromResult(Value);
        }
        public bool IsCurrent(MusicPlatformSession session) => Current?.Invoke(session) ?? (!RejectCurrent && ReferenceEquals(Value, session));
        public void Dispose() => Disposed++;
    }
    private sealed record Wire(string Path, JsonObject Data, string Cookie);
    private sealed class Peer : HttpMessageHandler
    {
        internal Func<Wire, CancellationToken, Task<HttpResponseMessage>>? Reply;
        internal readonly ConcurrentQueue<Wire> Calls = new();
        internal int Active, Maximum, Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active);
            int old; do { old = Maximum; } while (active > old && Interlocked.CompareExchange(ref Maximum, active, old) != old);
            try
            {
                var wire = await Decode(request, token); Calls.Enqueue(wire);
                return Reply is null ? Response(Default(wire)) : await Reply(wire, token);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed++; base.Dispose(disposing); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Sessions Sessions = new();
        internal readonly Peer Peer = new();
        internal readonly MusicPlatformHttp Http;
        internal readonly NativeMusicSearchDiscovery Service;
        internal readonly MusicClientContextFixture Clients;
        internal Fixture(TimeProvider? clock = null)
        {
            Http = MusicPlatformHttp.CreateWithHandler(Peer);
            Clients = new(Http, clock: clock); Service = new(Sessions, Http, Clients.Owner, clock);
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Clients.DisposeAsync(); Http.Dispose(); Sessions.Dispose(); }
    }
    private static async Task<Wire> Decode(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        Check(request.Method == HttpMethod.Post && uri.Scheme == "https" && uri.IsDefaultPort &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0, "Fixed TLS POST");
        Check(request.Headers.Referrer?.AbsoluteUri == "https://music.163.com/" &&
            request.Headers.Authorization is null && request.Content!.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded");
        var form = (await request.Content!.ReadAsStringAsync(token)).Split('&').ToDictionary(p => p[..p.IndexOf('=')],
            p => Uri.UnescapeDataString(p[(p.IndexOf('=') + 1)..].Replace("+", " ", StringComparison.Ordinal)));
        var cookie = string.Join("; ", request.Headers.GetValues("Cookie"));
        if (uri.AbsolutePath == "/weapi/hotsearchlist/get")
        {
            Check(uri.Host == "music.163.com" && form.Count == 2 && form.ContainsKey("params") &&
                form["encSecKey"].Length == 256 && form["encSecKey"].All(Uri.IsHexDigit));
            Check(Convert.FromBase64String(form["params"]).Length > 0);
            return new("/api/hotsearchlist/get", new(), cookie);
        }
        Check(uri.Host == "interfacepc.music.163.com" && uri.AbsolutePath.StartsWith("/eapi/", StringComparison.Ordinal) && form.Count == 1);
        using var aes = Aes.Create(); aes.Key = "e82ckenh8dichen8"u8.ToArray();
        var text = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(form["params"]), PaddingMode.PKCS7));
        var pieces = text.Split("-36cd479b6b5-", StringSplitOptions.None);
        Check(pieces.Length == 3 && pieces[0] == "/api/" + uri.AbsolutePath[6..]);
        Check(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{pieces[0]}use{pieces[1]}md5forencrypt"))) == pieces[2], "EAPI digest");
        var data = JsonNode.Parse(pieces[1])!.AsObject();
        Check(data["e_r"]!.GetValue<bool>() == false && data["header"] is JsonObject);
        var expected = pieces[0] switch
        {
            "/api/toplist" or "/api/search/defaultkeyword/get" => new[] { "e_r", "header" },
            "/api/v6/playlist/detail" => ["id", "n", "s", "e_r", "header"],
            "/api/v3/song/detail" => ["c", "e_r", "header"],
            _ => throw new InvalidOperationException("Unexpected route")
        };
        Check(data.Count == expected.Length && expected.All(data.ContainsKey));
        if (pieces[0] == "/api/v6/playlist/detail")
            Check(data["n"]!.GetValue<int>() == 20 && data["s"]!.GetValue<int>() == 8, "Bounded discovery member recipe");
        if (pieces[0] == "/api/v3/song/detail")
        {
            var ids = JsonNode.Parse(data["c"]!.GetValue<string>())!.AsArray();
            Check(ids.Count <= 20 && ids.All(n => n is JsonObject obj && obj.Count == 1 && obj["id"] is JsonValue), "Exact slice recipe");
        }
        return new(pieces[0], data, cookie);
    }
    private static string Id(Wire wire) => wire.Data["id"]?.GetValue<string>() ?? "";
    private static long[] SongIds(Wire wire) => JsonNode.Parse(wire.Data["c"]!.GetValue<string>())!.AsArray()
        .Select(n => n!["id"]!.GetValue<long>()).ToArray();
    private static JsonObject Song(long id) => new()
    {
        ["id"] = id, ["name"] = "song-" + id, ["dt"] = 180000,
        ["ar"] = new JsonArray(new JsonObject { ["id"] = 7, ["name"] = "artist" }),
        ["al"] = new JsonObject { ["id"] = 8, ["name"] = "album", ["picUrl"] = "https://p1.music.126.net/image.jpg" },
        ["ignoredRaw"] = "synthetic-secret-do-not-copy"
    };
    private static JsonObject Default(Wire wire) => wire.Path switch
    {
        "/api/hotsearchlist/get" => new() { ["code"] = 200, ["data"] = new JsonArray(Enumerable.Range(0, 23)
            .Select(i => (JsonNode)new JsonObject { ["searchWord"] = "word-" + i, ["score"] = 100 - i }).ToArray()) },
        "/api/search/defaultkeyword/get" => new() { ["code"] = 200, ["data"] = new JsonObject { ["realkeyword"] = "default" } },
        "/api/toplist" => new() { ["code"] = 200, ["list"] = new JsonArray(Enumerable.Range(100, 4).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = i, ["name"] = "chart-" + i, ["coverImgUrl"] = "https://p1.music.126.net/chart.jpg",
            ["trackCount"] = 24, ["creator"] = new JsonObject { ["nickname"] = "creator" }
        }).ToArray()) },
        "/api/v6/playlist/detail" => new() { ["code"] = 200, ["playlist"] = new JsonObject { ["id"] = long.Parse(Id(wire)),
            ["trackIds"] = new JsonArray(Enumerable.Range(1, 24).Select(i => (JsonNode)new JsonObject
                { ["id"] = long.Parse(Id(wire)) * 100 + i }).ToArray()) } },
        "/api/v3/song/detail" => new() { ["code"] = 200, ["songs"] = new JsonArray(SongIds(wire).Reverse().Select(i => (JsonNode)Song(i)).ToArray()) },
        _ => throw new InvalidOperationException("Unexpected fixture route")
    };
    private static HttpResponseMessage Response(JsonObject data) => Raw(data.ToJsonString());
    private static HttpResponseMessage Raw(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
    private static string Stage(Wire wire) => wire.Path switch
    {
        "/api/hotsearchlist/get" => "hot", "/api/toplist" => "toplist", "/api/search/defaultkeyword/get" => "default", _ => "chart"
    };
    private static async Task WireAndShape()
    {
        await using var f = new Fixture();
        var snapshot = await f.Service.ReadAsync();
        Check(snapshot.Generation > 0 && snapshot.Hot.Length == 20 && snapshot.Suggestions.Length == 21 && snapshot.Recommended.Length == 6);
        Check(snapshot.Charts.Select(c => c.Id).SequenceEqual(["100", "101", "102"]) && snapshot.Failures.Length == 0);
        Check(f.Peer.Calls.Count == 9 && f.Sessions.Count == 2 && f.Peer.Active == 0);
        foreach (var chart in snapshot.Charts)
        {
            Check(chart.Count == 24 && chart.Name == "chart-" + chart.Id && chart.Creator == "creator" && chart.Songs.Length == 20);
            Check(chart.Songs.Select(MusicSongFixture.NativeId).SequenceEqual(
                Enumerable.Range(1, 20).Select(i => (long.Parse(chart.Id) * 100 + i).ToString())));
            var song = chart.Songs[0];
            Check(song.GetProperty("duration").GetDouble() == 180 && song.GetProperty("songId").GetString()!.StartsWith("song:", StringComparison.Ordinal));
            Check(MusicSongFixture.Info(song).GetProperty("source").GetString() == "wy" &&
                MusicSongFixture.Info(song).GetProperty("songmid").GetString() == MusicSongFixture.NativeId(song));
        }
        Check(snapshot.FetchedAt > 0);
    }
    private static async Task Credentials()
    {
        await using var f = new Fixture();
        var client = await f.Clients.Owner.AcquireWyAsync();
        var local = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deviceId"] = client.DeviceId, ["_ntes_nuid"] = client.Nuid,
            ["_ntes_nnid"] = client.Nnid, ["WNMCID"] = client.Wnmcid
        };
        if (client.Nmtid is { } nmtid) local["NMTID"] = nmtid;
        bool ExactCookie(Wire wire, bool signedIn)
        {
            var values = wire.Cookie.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim().Split('=', 2)).ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
            var expected = new Dictionary<string, string>(local, StringComparer.Ordinal);
            if (signedIn) { expected["MUSIC_U"] = "synthetic-only"; expected["__csrf"] = "fixture"; }
            if (wire.Path == "/api/hotsearchlist/get") expected["__remember_me"] = "true";
            else
            {
                var header = wire.Data["header"]!.AsObject();
                var build = header["buildver"]!.GetValue<string>();
                var requestId = header["requestId"]!.GetValue<string>();
                var parts = requestId.Split('_');
                if (parts.Length != 2 || parts[1].Length != 3 || !parts[1].All(char.IsAsciiDigit) ||
                    !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ||
                    !long.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                    seconds <= 0 || milliseconds / 1000 != seconds) return false;
                expected["os"] = "pc"; expected["appver"] = "3.1.17.204416";
                expected["osver"] = "Microsoft-Windows-10-Professional-build-19045-64bit";
                expected["channel"] = "netease"; expected["versioncode"] = "140"; expected["resolution"] = "1920x1080";
                expected["__csrf"] = signedIn ? "fixture" : ""; expected["buildver"] = build; expected["requestId"] = requestId;
                if (header.Count != expected.Count || !expected.All(part =>
                    header[part.Key] is JsonValue value && value.TryGetValue<string>(out var text) && text == part.Value)) return false;
            }
            return values.Count == expected.Count && expected.All(part => values.GetValueOrDefault(part.Key) == part.Value);
        }
        await f.Service.ReadAsync();
        Check(f.Peer.Calls.All(w => !w.Cookie.Contains("MUSIC_U", StringComparison.Ordinal)));
        Check(f.Peer.Calls.All(w => ExactCookie(w, false)), "Anonymous requests carry their exact native envelope and local client context");
        f.Sessions.Value = Account(); f.Service.Invalidate("wy");
        var current = await f.Service.ReadAsync();
        Check(f.Peer.Calls.Count == 18 && f.Peer.Calls.Skip(9).All(w => ExactCookie(w, true)),
            "Authorized requests combine the same client with the exact account cookie");
        Check(!JsonSerializer.Serialize(current).Contains("synthetic-only", StringComparison.Ordinal));
    }
    private static async Task ClientCache(bool responseUpdate, bool reload = false)
    {
        await using var f = new Fixture();
        var before = await f.Clients.Owner.AcquireWyAsync();
        if (responseUpdate)
            f.Peer.Reply = (wire, _) =>
            {
                var response = Response(Default(wire));
                if (Stage(wire) == "default")
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_DISCOVERY_CONTEXT; Domain=music.163.com");
                return Task.FromResult(response);
            };
        var first = await f.Service.ReadAsync();
        Check(first.Failures.Length == 0 && first.Charts.Length == 3 && f.Peer.Calls.Count == 9);
        if (responseUpdate)
        {
            var client = await f.Clients.Owner.AcquireWyAsync();
            Check(client.Generation == before.Generation + 1 && client.Nmtid == "SYNTHETIC_DISCOVERY_CONTEXT");
        }
        else
        {
            await f.Clients.Owner.AcceptWyAsync(before, null,
                ["NMTID=SYNTHETIC_DISCOVERY_CONTEXT; Domain=music.163.com"], () => { }, default);
        }
        if (reload)
        {
            await f.Clients.Owner.ReloadAsync();
            await Failure("STORAGE_CONFLICT", () => { f.Service.Rotate(first.Generation); return Task.CompletedTask; });
        }
        else f.Service.Rotate(first.Generation);
        var next = await f.Service.ReadAsync();
        Check(f.Peer.Calls.Count == (reload ? 18 : 9) &&
            (reload ? next.Generation != first.Generation : next.Generation == first.Generation),
            "Only authoritative client replacement invalidates the completed cache");
        Check((await f.Service.ReadAsync()).Generation == next.Generation &&
            f.Peer.Calls.Count == (reload ? 18 : 9));
    }
    private static async Task Parallel()
    {
        await using var f = new Fixture(); var primary = Signal(); var charts = Signal(); var release = Signal(); var releaseCharts = Signal();
        int primaries = 0, details = 0;
        f.Peer.Reply = async (wire, token) =>
        {
            if (Stage(wire) is "hot" or "toplist" or "default")
            { if (Interlocked.Increment(ref primaries) == 3) primary.TrySetResult(); await release.Task; }
            if (wire.Path == "/api/v6/playlist/detail")
            { if (Interlocked.Increment(ref details) == 3) charts.TrySetResult(); await releaseCharts.Task; }
            return Response(Default(wire));
        };
        var read = f.Service.ReadAsync();
        try
        {
            await Wait(primary.Task); Check(f.Peer.Maximum == 3 && f.Peer.Calls.Count == 3);
            release.TrySetResult(); await Wait(charts.Task); Check(f.Peer.Maximum == 3 && !read.IsCompleted);
        }
        finally { release.TrySetResult(); releaseCharts.TrySetResult(); }
        Check((await read).Charts.Length == 3 && f.Peer.Maximum == 3 && f.Peer.Active == 0);
    }
    private static async Task DisplayRows()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (wire, _) =>
        {
            var data = Default(wire);
            var field = Stage(wire) switch { "hot" => "data", "toplist" => "list", _ => null };
            if (field is not null)
            {
                var rows = data[field]!.AsArray();
                while (rows.Count < 10001) rows.Add(new JsonObject());
            }
            return Task.FromResult(Response(data));
        };
        var result = await f.Service.ReadAsync();
        Check(result.Failures.Length == 0 && result.Hot.Length == 20 && result.Charts.Length == 3 &&
            f.Peer.Calls.Count == 9);
    }
    private static async Task Cache()
    {
        await using var f = new Fixture(); var first = await f.Service.ReadAsync();
        var next = f.Service.Rotate(first.Generation);
        Check(next.Recommended.SequenceEqual(next.Suggestions.Skip(6).Take(6)) && f.Peer.Calls.Count == 9);
        for (int i = 0; i < 2; i++) next = f.Service.Rotate(first.Generation);
        Check(next.Recommended.SequenceEqual(new[] { "word-17", "word-18", "word-19", "default", "word-0", "word-1" }));
        next.Hot[0] = new("mutated", null); next.Suggestions[0] = "mutated"; next.Recommended[0] = "mutated";
        next.Charts[0].Songs[0] = MusicSongFixture.Element(MusicSongFixture.Online("wy", "mutated"));
        next.Charts[0] = next.Charts[0] with { Name = "mutated" };
        var again = await f.Service.ReadAsync();
        Check(again.Hot[0].Keyword == "word-0" && again.Suggestions[0] == "default" && again.Recommended[0] == "word-17" &&
            MusicSongFixture.NativeId(again.Charts[0].Songs[0]) == "10001" && f.Sessions.Count == 2 && f.Peer.Calls.Count == 9);
    }
    private static async Task Empty()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) => Task.FromResult(Response(Stage(w) switch
        {
            "hot" => new() { ["code"] = 200, ["data"] = new JsonArray() },
            "toplist" => new() { ["code"] = 200, ["list"] = new JsonArray() },
            _ => new() { ["code"] = 200, ["data"] = new JsonObject { ["realkeyword"] = "" } }
        }));
        var s = await f.Service.ReadAsync();
        Check(s.Hot.Length == 0 && s.Charts.Length == 0 && s.Recommended.Length == 0 && s.Failures.Length == 0 && f.Peer.Calls.Count == 3);
        Check(f.Service.Rotate(s.Generation).Recommended.Length == 0);
    }
    private static async Task Dedup()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w);
            if (Stage(w) == "default") data["data"]!["realkeyword"] = "word-0";
            if (Stage(w) == "hot") { data["data"]![1]!["searchWord"] = "word-0"; data["data"]![0]!["score"] = null; }
            return Task.FromResult(Response(data));
        };
        var s = await f.Service.ReadAsync();
        Check(s.Hot.Length == 20 && s.Hot[0].Score is null && s.Suggestions.Length == 19 && s.Suggestions[0] == "word-0");
    }
    private static async Task Partial(string stage)
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) => Task.FromResult(Response(Stage(w) == stage && (stage != "chart" || Id(w) == "100")
            ? new() { ["code"] = 403, ["message"] = "synthetic-secret-do-not-copy" } : Default(w)));
        var s = await f.Service.ReadAsync();
        Check(s.Failures.Length == 1 && s.Failures[0].Stage == stage && s.Failures[0].Code == "FORBIDDEN");
        Check(s.Charts.Length == (stage == "toplist" ? 0 : stage == "chart" ? 2 : 3));
        Check(s.Hot.Length == (stage == "hot" ? 0 : 20) && s.Suggestions.Length == (stage == "hot" ? 1 : stage == "default" ? 20 : 21));
        Check(s.Failures[0].ChartId == (stage == "chart" ? "100" : null));
    }
    private static async Task BothPrimaryFail()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) => Task.FromResult(Response(Stage(w) is "hot" or "toplist" ? new() { ["code"] = 301 } : Default(w)));
        var error = await Failure("AUTH_REQUIRED", () => f.Service.ReadAsync());
        Check(error.Platform == "wy" && error.Operation == "discovery" && f.Peer.Calls.Count == 3);
    }
    private static async Task FailedRefresh()
    {
        await using var f = new Fixture(); var saved = await f.Service.ReadAsync();
        f.Peer.Reply = (w, _) => Task.FromResult(Response(Stage(w) is "hot" or "toplist" ? new() { ["code"] = 403 } : Default(w)));
        await Failure("FORBIDDEN", () => f.Service.ReadAsync(true));
        Check((await f.Service.ReadAsync()).Generation == saved.Generation && f.Peer.Calls.Count == 12);
        Check(f.Service.Rotate(saved.Generation).Recommended[0] == "word-5");
    }
    private static async Task Identity(string kind)
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w);
            if (w.Path == "/api/v6/playlist/detail" && Id(w) == "100")
            {
                if (kind == "playlist-id") data["playlist"]!["id"] = 101;
                if (kind == "duplicate-track") data["playlist"]!["trackIds"]![1]!["id"] = 10001;
                if (kind == "zero-track") data["playlist"]!["trackIds"]![0]!["id"] = 0;
            }
            if (w.Path == "/api/v3/song/detail" && SongIds(w)[0] == 10001)
            {
                var songs = data["songs"]!.AsArray();
                if (kind == "duplicate-song") songs[1]!["id"] = songs[0]!["id"]!.DeepClone();
                if (kind == "missing-song") songs.RemoveAt(0);
                if (kind == "alien-song") songs[0]!["id"] = 99999;
                if (kind == "too-many-songs") songs.Add(Song(99999));
            }
            return Task.FromResult(Response(data));
        };
        var s = await f.Service.ReadAsync();
        Check(s.Charts.Select(c => c.Id).SequenceEqual(["101", "102"]) && s.Failures.Length == 1 && s.Failures[0].ChartId == "100");
        Check(s.Failures[0].Code == (kind is "zero-track" or "too-many-songs" ? "INVALID_RESPONSE" : "IDENTITY_MISMATCH"));
    }
    private static async Task EmptyChart()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w);
            if (w.Path == "/api/v6/playlist/detail" && Id(w) == "100") data["playlist"]!["trackIds"] = new JsonArray();
            return Task.FromResult(Response(data));
        };
        var s = await f.Service.ReadAsync();
        Check(s.Charts[0].Songs.Length == 0 && s.Failures.Length == 0 && f.Peer.Calls.Count == 8);
    }
    private static async Task DuplicateCharts()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w);
            if (Stage(w) == "toplist") data["list"]![1]!["id"] = 100;
            return Task.FromResult(Response(data));
        };
        var s = await f.Service.ReadAsync();
        Check(s.Charts.Length == 0 && s.Hot.Length == 20 && s.Failures.Single().Code == "IDENTITY_MISMATCH");
    }
    private static async Task UntrustedClientResponse(string stage)
    {
        await using var f = new Fixture();
        var before = await f.Clients.Owner.AcquireWyAsync();
        f.Peer.Reply = (wire, _) =>
        {
            var data = Default(wire);
            bool target = stage switch
            {
                "chart" => wire.Path == "/api/v6/playlist/detail" && Id(wire) == "100",
                "songs" => wire.Path == "/api/v3/song/detail" && SongIds(wire)[0] == 10001,
                _ => Stage(wire) == stage
            };
            if (target)
            {
                switch (stage)
                {
                    case "hot": data["data"]![0]!["searchWord"] = 42; break;
                    case "toplist": data["list"]![1]!["id"] = 100; break;
                    case "default": data["data"]!["realkeyword"] = new JsonObject(); break;
                    case "chart": data["playlist"]!["id"] = 101; break;
                    case "songs": data["songs"]![1]!["id"] = data["songs"]![0]!["id"]!.DeepClone(); break;
                }
            }
            var response = Response(data);
            if (target)
                response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_UNTRUSTED_DISCOVERY_CONTEXT; Domain=music.163.com");
            return Task.FromResult(response);
        };
        var result = await f.Service.ReadAsync();
        Check(result.Failures.Length == 1 && result.Failures[0].Stage == (stage == "songs" ? "chart" : stage) &&
            result.Failures[0].Code == (stage is "hot" or "default" ? "INVALID_RESPONSE" : "IDENTITY_MISMATCH"));
        Check(f.Clients.Owner.IsCurrent(before) && ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()),
            "Invalid discovery content must not authorize a client-state CAS");
    }
    private static async Task ExpiredAuthorization(bool primary)
    {
        await using var f = new Fixture();
        f.Sessions.Value = Account();
        var before = await f.Clients.Owner.AcquireWyAsync();
        f.Peer.Reply = (wire, _) =>
        {
            var response = Response(Default(wire));
            if (primary ? Stage(wire) is "hot" or "toplist" : Stage(wire) == "default")
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie",
                    "NMTID=SYNTHETIC_EXPIRED_DISCOVERY_CONTEXT; Domain=music.163.com");
                response.Headers.TryAddWithoutValidation("Set-Cookie",
                    "MUSIC_U=; Max-Age=0; Domain=music.163.com");
            }
            return Task.FromResult(response);
        };
        if (primary)
            await Failure("AUTH_REQUIRED", () => f.Service.ReadAsync());
        else
        {
            var result = await f.Service.ReadAsync();
            Check(result.Hot.Length == 20 && result.Charts.Length == 3 &&
                result.Failures.Length == 1 && result.Failures[0].Stage == "default" &&
                result.Failures[0].Code == "AUTH_REQUIRED");
        }
        Check(f.Clients.Owner.IsCurrent(before) && ReferenceEquals(before, await f.Clients.Owner.AcquireWyAsync()),
            "Expired authorization must not authorize otherwise valid client fields");
        Check(f.Peer.Calls.Count == (primary ? 3 : 9) && f.Peer.Active == 0);
    }
    private static async Task Bounded(string kind)
    {
        await using var f = new Fixture();
        var stage = kind == "default-nonstring" ? "default" : "hot";
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w);
            if (Stage(w) != stage) return Task.FromResult(Response(data));
            if (kind == "duplicate-json-key") return Task.FromResult(Raw("""{"code":200,"data":[],"code":200}"""));
            if (kind == "invalid-json") return Task.FromResult(Raw("synthetic-secret-do-not-copy"));
            if (kind == "response-limit") return Task.FromResult(Raw(new string('x', 2 * 1024 * 1024 + 1)));
            if (kind == "default-nonstring") data["data"]!["realkeyword"] = new JsonObject { ["secret"] = "not-a-string" };
            if (kind == "hot-nonstring") data["data"]![0]!["searchWord"] = 42;
            if (kind == "score-negative") data["data"]![0]!["score"] = -1;
            if (kind == "keyword-control") data["data"]![0]!["searchWord"] = "word\0other";
            if (kind == "keyword-limit") data["data"]![0]!["searchWord"] = new string('x', 201);
            return Task.FromResult(Response(data));
        };
        var s = await f.Service.ReadAsync();
        Check(s.Failures.Single().Stage == stage && s.Failures[0].Code == (kind == "response-limit" ? "LIMIT" : "INVALID_RESPONSE"));
        Check(s.Charts.Length == 3);
    }
    private static async Task WholeBudget()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w);
            if (w.Path == "/api/v3/song/detail")
                foreach (var song in data["songs"]!.AsArray()) song!["name"] = new string('长', 4096);
            return Task.FromResult(Response(data));
        };
        await Failure("LIMIT", () => f.Service.ReadAsync());
    }
    private static async Task SafeFields()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            var data = Default(w); data["Cookie"] = "synthetic-secret-do-not-copy"; data["url"] = "https://invalid.example/?token=synthetic";
            if (Stage(w) == "toplist") data["list"]![0]!["coverImgUrl"] = "https://p1.music.126.net/chart.jpg?token=synthetic";
            if (w.Path == "/api/v3/song/detail") data["songs"]![0]!["al"]!["picUrl"] = "https://p1.music.126.net/cover.jpg?token=synthetic";
            var response = Response(data); response.Headers.TryAddWithoutValidation("Set-Cookie", "fixture_set=synthetic-only");
            return Task.FromResult(response);
        };
        var s = await f.Service.ReadAsync(); var text = JsonSerializer.Serialize(s);
        Check(!text.Contains("synthetic", StringComparison.Ordinal) && !text.Contains("ignoredRaw", StringComparison.Ordinal) && s.Charts[0].Cover == "");
        var roundtrip = JsonSerializer.Deserialize<MusicSearchDiscoverySnapshot>(text)!;
        Check(roundtrip.Charts.Length == 3 && roundtrip.Hot.Length == 20 && roundtrip.Charts[0].Songs.Length == 20);
    }
    private static async Task FiniteError(int status, string code)
    {
        await using var f = new Fixture();
        f.Peer.Reply = (w, _) =>
        {
            if (Stage(w) is not ("hot" or "toplist")) return Task.FromResult(Response(Default(w)));
            if (status == 460) return Task.FromResult(Response(new() { ["code"] = 460, ["message"] = "synthetic-secret-do-not-copy" }));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("synthetic-secret-do-not-copy") });
        };
        await Failure(code, () => f.Service.ReadAsync());
    }
    private static async Task HandlerFailure()
    {
        await using var f = new Fixture();
        f.Peer.Reply = (_, _) => throw new HttpRequestException("synthetic-secret-do-not-copy");
        await Failure("NETWORK_ERROR", () => f.Service.ReadAsync());
    }

    private sealed class Stall(string stage)
    {
        internal readonly TaskCompletionSource Entered = Signal(), Released = Signal(), Cancelled = Signal();
        internal int Disposed;
        internal async Task<HttpResponseMessage> ResponseAsync(Wire wire, CancellationToken token)
        {
            token.Register(() => Cancelled.TrySetResult());
            var bytes = Encoding.UTF8.GetBytes(Default(wire).ToJsonString());
            if (stage == "handler") { Entered.TrySetResult(); await Released.Task; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }
            return new(HttpStatusCode.OK) { Content = new StreamContent(new StallStream(this, bytes, stage)) };
        }
    }
    private sealed class StallStream(Stall owner, byte[] data, string stage) : MemoryStream(data, writable: false)
    {
        private int read, disposing;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref read, 1) == 0)
            {
                // HttpClient retires the handler's linked token after ResponseHeadersRead.
                // Stream/cleanup assertions must observe the still-live physical-read token.
                token.Register(() => owner.Cancelled.TrySetResult());
                if (stage == "read") { owner.Entered.TrySetResult(); await owner.Released.Task; }
            }
            // Deliberately ignores cancellation: the real HTTP layer must still fence publication.
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposing, 1) == 0)
            {
                if (stage == "dispose") { owner.Entered.TrySetResult(); await owner.Released.Task; }
                owner.Disposed++;
            }
            await base.DisposeAsync();
        }
    }
    private static async Task Coalescing()
    {
        await using var f = new Fixture(); var stall = new Stall("handler"); using var c1 = new CancellationTokenSource();
        f.Peer.Reply = (w, t) => Stage(w) == "hot" ? stall.ResponseAsync(w, t) : Task.FromResult(Response(Default(w)));
        var a = f.Service.ReadAsync(cancellationToken: c1.Token);
        await Wait(stall.Entered.Task); var b = f.Service.ReadAsync();
        try
        {
            c1.Cancel(); await Wait(Failure("CANCELLED", () => a));
            Check(!stall.Cancelled.Task.IsCompleted && !b.IsCompleted && f.Peer.Calls.Count <= 9);
        }
        finally { stall.Released.TrySetResult(); }
        Check((await b).Charts.Length == 3 && f.Peer.Calls.Count == 9 && f.Sessions.Count == 2);
    }
    private static async Task Physical(string stage, string action)
    {
        var clock = new Clock(); await using var f = new Fixture(clock); var stall = new Stall(stage); using var cancel = new CancellationTokenSource();
        f.Peer.Reply = (w, t) => Stage(w) == "hot" ? stall.ResponseAsync(w, t) : Task.FromResult(Response(Default(w)));
        var read = f.Service.ReadAsync(cancellationToken: cancel.Token); Task? cleanup = null;
        await Wait(stall.Entered.Task);
        try
        {
            switch (action)
            {
                case "cancel": cancel.Cancel(); break;
                case "timeout":
                    Check(clock.DueTimes.SequenceEqual([TimeSpan.FromSeconds(25)]));
                    clock.Advance(TimeSpan.FromSeconds(24)); Check(!stall.Cancelled.Task.IsCompleted);
                    clock.Advance(TimeSpan.FromSeconds(1)); break;
                case "invalidate": f.Service.Invalidate("wy"); cleanup = f.Service.CancelAndDrainAsync(); break;
                case "prepare": cleanup = f.Service.CancelAndDrainAsync(); break;
                case "close": cleanup = f.Service.DisposeAsync().AsTask(); break;
            }
            await Wait(stall.Cancelled.Task); await Task.Delay(35);
            Check(!read.IsCompleted && (cleanup is null || !cleanup.IsCompleted), "Returned before physical settle");
        }
        finally { stall.Released.TrySetResult(); }
        await Failure(action switch { "timeout" => "TIMEOUT", "invalidate" => "STALE_SESSION", "close" => "CLOSED", _ => "CANCELLED" }, () => read);
        if (cleanup is not null) await cleanup;
        Check(f.Peer.Active == 0 && (stage == "handler" || stall.Disposed == 1));
        Check(f.Peer.Disposed == 0 && f.Sessions.Disposed == 0, "Borrowed resource disposed");
    }
    private static async Task Replacement(bool refresh)
    {
        await using var f = new Fixture(); var stall = new Stall("handler"); int hot = 0;
        f.Peer.Reply = (w, t) => Stage(w) == "hot" && Interlocked.Increment(ref hot) == 1
            ? stall.ResponseAsync(w, t) : Task.FromResult(Response(Default(w)));
        var first = f.Service.ReadAsync(); await Wait(stall.Entered.Task);
        if (!refresh) f.Service.Invalidate();
        var second = f.Service.ReadAsync(refresh);
        try
        {
            await Wait(stall.Cancelled.Task); await Task.Delay(35);
            Check(!second.IsCompleted && hot == 1, "Replacement started before drain");
        }
        finally { stall.Released.TrySetResult(); }
        await Failure("STALE_SESSION", () => first); Check((await second).Charts.Length == 3 && hot == 2);
    }
    private static async Task LastReaderReplacement()
    {
        await using var f = new Fixture(); var stall = new Stall("handler"); using var cancel = new CancellationTokenSource(); int hot = 0;
        f.Peer.Reply = (w, t) => Stage(w) == "hot" && Interlocked.Increment(ref hot) == 1
            ? stall.ResponseAsync(w, t) : Task.FromResult(Response(Default(w)));
        var first = f.Service.ReadAsync(cancellationToken: cancel.Token); await Wait(stall.Entered.Task);
        cancel.Cancel(); await Wait(stall.Cancelled.Task); var second = f.Service.ReadAsync();
        try { await Task.Delay(35); Check(hot == 1 && !second.IsCompleted); }
        finally { stall.Released.TrySetResult(); }
        await Failure("CANCELLED", () => first); Check((await second).Charts.Length == 3 && hot == 2);
    }
    private static async Task AnonymousChanged()
    {
        await using var f = new Fixture();
        f.Sessions.Acquire = (n, _) =>
        {
            if (n == 2) f.Sessions.Value = Account();
            return ValueTask.FromResult(f.Sessions.Value);
        };
        await Failure("STALE_SESSION", () => f.Service.ReadAsync());
    }
    private static async Task CookieChanged()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account();
        // A broken owner that keeps IsCurrent(old)==true must still fail final equality.
        f.Sessions.Current = _ => true;
        f.Sessions.Acquire = (n, _) => ValueTask.FromResult<MusicPlatformSession?>(n == 1 ? f.Sessions.Value :
            Account(cookie: "MUSIC_U=synthetic-second; __csrf=fixture"));
        await Failure("STALE_SESSION", () => f.Service.ReadAsync());
    }
    private static async Task Revoked()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account();
        f.Peer.Reply = (w, _) => { f.Sessions.RejectCurrent = true; return Task.FromResult(Response(Default(w))); };
        await Failure("STALE_SESSION", () => f.Service.ReadAsync());
    }
    private static async Task AcquireDrain()
    {
        await using var f = new Fixture(); var entered = Signal(); var released = Signal(); var cancelled = Signal(); using var cancel = new CancellationTokenSource();
        f.Sessions.Acquire = async (_, token) => { token.Register(() => cancelled.TrySetResult()); entered.TrySetResult(); await released.Task; return null; };
        var read = f.Service.ReadAsync(cancellationToken: cancel.Token); await Wait(entered.Task); cancel.Cancel();
        try { await Wait(cancelled.Task); await Task.Delay(35); Check(!read.IsCompleted && f.Peer.Calls.Count == 0); }
        finally { released.TrySetResult(); }
        await Failure("CANCELLED", () => read);
    }
    private static async Task CacheSession()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account(); var first = await f.Service.ReadAsync();
        f.Service.Invalidate("tx"); f.Service.Invalidate("kg");
        Check((await f.Service.ReadAsync()).Generation == first.Generation && f.Peer.Calls.Count == 9);
        f.Sessions.Value = Account(2);
        var second = await f.Service.ReadAsync();
        Check(second.Generation != first.Generation && f.Peer.Calls.Count == 18);
        f.Service.Invalidate("wy"); Check((await f.Service.ReadAsync()).Generation != second.Generation && f.Peer.Calls.Count == 27);
    }
    private static async Task AcquireError()
    {
        await using var f = new Fixture();
        f.Sessions.Acquire = (_, _) => throw new IOException("synthetic-secret-do-not-copy");
        await Failure("CREDENTIALS_UNAVAILABLE", () => f.Service.ReadAsync()); Check(f.Peer.Calls.Count == 0);
    }
    private static async Task InvalidSession()
    {
        await using var f = new Fixture(); f.Sessions.Value = Account(platform: "tx");
        await Failure("STALE_SESSION", () => f.Service.ReadAsync());
        f.Sessions.Value = Account(); f.Sessions.RejectCurrent = true;
        await Failure("STALE_SESSION", () => f.Service.ReadAsync()); Check(f.Peer.Calls.Count == 0);
    }
    private static async Task PreCancelled()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Failure("CANCELLED", () => f.Service.ReadAsync(cancellationToken: cancel.Token));
        Check(f.Sessions.Count == 0 && f.Peer.Calls.Count == 0);
    }
    private static async Task PrepareAndClose()
    {
        await using var f = new Fixture(); var saved = await f.Service.ReadAsync();
        await f.Service.CancelAndDrainAsync();
        Check((await f.Service.ReadAsync()).Generation == saved.Generation && f.Peer.Calls.Count == 9);
        var a = f.Service.DisposeAsync().AsTask(); var b = f.Service.DisposeAsync().AsTask(); Check(ReferenceEquals(a, b)); await a;
        await Failure("CLOSED", () => f.Service.ReadAsync());
        await Failure("CLOSED", () => Task.FromResult(f.Service.Rotate(saved.Generation)));
        f.Service.Invalidate(); await f.Service.CancelAndDrainAsync();
        Check(f.Peer.Disposed == 0 && f.Sessions.Disposed == 0);
    }
    private static async Task RotateFence()
    {
        await using var f = new Fixture();
        await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(0)));
        var s = await f.Service.ReadAsync();
        await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(s.Generation + 1)));
        f.Service.Invalidate(); await Failure("STALE_SESSION", () => Task.FromResult(f.Service.Rotate(s.Generation)));
        Check(f.Peer.Calls.Count == 9);
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private DateTimeOffset now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        internal TimeSpan[] DueTimes { get { lock (gate) return timers.Select(t => t.Initial).ToArray(); } }
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Check(period == Timeout.InfiniteTimeSpan, "Deadline is not a poller");
            lock (gate) { var timer = new Timer(this, callback, state, dueTime); timers.Add(timer); return timer; }
        }
        internal void Advance(TimeSpan delta)
        {
            Timer[] due;
            lock (gate) { now += delta; due = timers.Where(t => !t.Disposed && t.At <= now).ToArray(); foreach (var timer in due) timer.At = DateTimeOffset.MaxValue; }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal readonly TimeSpan Initial = due;
            internal DateTimeOffset At = owner.now + due;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner.gate) { if (Disposed) return false; At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime; return true; } }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
