using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativePlatformTests
{
    private static JsonObject StyleConfig() => J(new { code = 200, data = new { categorys = new[] {
        new { categoryId = 3000, categoryName = "服务分类", tagVOList = Enumerable.Range(1, 7)
            .Select(i => new { tagId = i, tagName = "服务标签" + i }).ToArray() },
        new { categoryId = 8000, categoryName = "另一分类", tagVOList = new[] { new { tagId = 99, tagName = "其他标签" } } }
    } } });
    private static JsonObject StyleSongs(int[] ids) => J(new { code = 200, data = new {
        dailySongs = new[] { Wy("123") }, tags = new { categoryId = 3000,
            tagVOList = ids.Select(i => new { tagId = i, tagName = "服务标签" + i }).ToArray() }
    } });

    private static IEnumerable<TestCase> DailyStyleCases()
    {
        yield return Case("style editor reads service ordering and saves seven same-category tags without invented caps", async () =>
        {
            using var wire = new XeapiFixture();
            var writes = 0; var reads = 0;
            var selected = new[] { 1 };
            using var f = new Fixture(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/bsr/sk/get") return wire.PublicKey(request);
                var body = wire.Open(request);
                if (path == "/xeapi/homepage/daily/song/config/get") return XeapiFixture.Response(StyleConfig());
                if (path == "/xeapi/homepage/daily/song/tag/save")
                {
                    writes++;
                    var form = Uri.UnescapeDataString(S(body["content"]).Replace("+", " "));
                    True(form.StartsWith("tags=", StringComparison.Ordinal));
                    var tags = JsonNode.Parse(form[5..])!;
                    Equal(3000, tags["categoryId"]!.GetValue<int>());
                    selected = tags["tagIds"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray();
                    Equal("1,2,3,4,5,6,7", string.Join(",", selected));
                    return XeapiFixture.Response(J(new { code = 200 }));
                }
                Equal("/xeapi/homepage/category/daily/song/list", path); reads++;
                return XeapiFixture.Response(StyleSongs(selected));
            }, DeviceSession());
            var before = await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
            Equal(1, before.GetProperty("tags").GetArrayLength());
            IMusicDailyStylePreferences preferences = f.Catalog;
            var editor = await preferences.OpenDailyStyleEditorAsync();
            Equal("服务分类", editor.Categories[0].Name); Equal("8000", editor.Categories[1].Id);
            Equal(7, editor.Categories[0].Tags.Count); Equal(0, writes);
            var result = await preferences.SaveDailyStyleAsync(editor, new("3000", Enumerable.Range(1, 7).Select(i => i.ToString()).ToArray()));
            Equal(MusicDailyStyleWriteStatus.Confirmed, result.Status); True(result.SelectionMatches == true);
            Equal(7, result.Readback!.Value.GetProperty("tags").GetArrayLength()); Equal(1, writes); Equal(2, reads);
            _ = await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style)); Equal(2, reads);
        });
        yield return Case("style refuses empty duplicate foreign and mixed-category drafts before writing", async () =>
        {
            using var wire = new XeapiFixture();
            using var f = new Fixture(request => request.RequestUri!.AbsolutePath == "/api/bsr/sk/get"
                ? wire.PublicKey(request) : XeapiFixture.Response(StyleConfig()), DeviceSession());
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            var calls = f.Calls;
            foreach (var ids in new string[][] { [], ["1", "1"], ["99"], ["1", "99"] })
            {
                var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ids));
                Equal(MusicDailyStyleWriteStatus.NotSent, result.Status); Equal("INVALID_REQUEST", result.ErrorCode);
                True(result.Readback is null); Equal(calls, f.Calls);
            }
        });
        yield return Case("style editor cannot save using a replacement account", async () =>
        {
            using var wire = new XeapiFixture();
            using var f = new Fixture(request => request.RequestUri!.AbsolutePath == "/api/bsr/sk/get"
                ? wire.PublicKey(request) : XeapiFixture.Response(StyleConfig()), DeviceSession());
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            var calls = f.Calls;
            f.Sessions.Current = new("wy", "replacement", 2, "234", "MUSIC_U=SYNTHETIC_REPLACEMENT");
            var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]));
            Equal(MusicDailyStyleWriteStatus.NotSent, result.Status); Equal("STALE_SESSION", result.ErrorCode);
            Equal(calls, f.Calls); True(result.Readback is null);
        });
        foreach (var phase in new[] { "config", "write" })
            yield return Case("style account changes during " + phase + " never read or write using new account", async () =>
            {
                using var wire = new XeapiFixture();
                Fixture? fixture = null; var configs = 0; var writes = 0; var reads = 0;
                using var f = new Fixture(request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path == "/api/bsr/sk/get") return wire.PublicKey(request);
                    if (path.EndsWith("config/get", StringComparison.Ordinal))
                    {
                        if (++configs == 2 && phase == "config")
                            fixture!.Sessions.Current = new("wy", "replacement", 2, "234", "MUSIC_U=SYNTHETIC_REPLACEMENT");
                        return XeapiFixture.Response(StyleConfig());
                    }
                    if (path.EndsWith("tag/save", StringComparison.Ordinal))
                    {
                        writes++;
                        fixture!.Sessions.Current = new("wy", "replacement", 2, "234", "MUSIC_U=SYNTHETIC_REPLACEMENT");
                        return XeapiFixture.Response(J(new { code = 200 }));
                    }
                    reads++; return XeapiFixture.Response(StyleSongs([1]));
                }, DeviceSession());
                fixture = f;
                var editor = await f.Catalog.OpenDailyStyleEditorAsync();
                var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]));
                Equal(phase == "config" ? MusicDailyStyleWriteStatus.NotSent : MusicDailyStyleWriteStatus.Unknown, result.Status);
                Equal("STALE_SESSION", result.ErrorCode); Equal(phase == "config" ? 0 : 1, writes);
                Equal(0, reads); True(result.Readback is null);
            });
        yield return Case("style configuration rejects duplicate identities without publishing editor", async () =>
        {
            foreach (var duplicateCategory in new[] { false, true })
            {
                using var wire = new XeapiFixture();
                var config = StyleConfig(); var categories = config["data"]!["categorys"]!.AsArray();
                if (duplicateCategory) categories.Add(categories[0]!.DeepClone());
                else categories[0]!["tagVOList"]!.AsArray().Add(categories[0]!["tagVOList"]![0]!.DeepClone());
                using var f = new Fixture(request => request.RequestUri!.AbsolutePath == "/api/bsr/sk/get"
                    ? wire.PublicKey(request) : XeapiFixture.Response(config), DeviceSession());
                await Error("INVALID_RESPONSE", () => f.Catalog.OpenDailyStyleEditorAsync());
            }
        });
        foreach (var outcome in new[] { "rejected", "unknown", "malformed", "readback-failed", "different" })
            yield return Case("style write " + outcome + " keeps explicit receipt and actual selection", async () =>
            {
                using var wire = new XeapiFixture(); var writes = 0;
                using var f = new Fixture(request =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path == "/api/bsr/sk/get") return wire.PublicKey(request);
                    if (path.EndsWith("config/get", StringComparison.Ordinal)) return XeapiFixture.Response(StyleConfig());
                    if (path.EndsWith("tag/save", StringComparison.Ordinal))
                    {
                        writes++;
                        if (outcome == "unknown") throw new System.Net.Http.HttpRequestException();
                        if (outcome == "malformed") return XeapiFixture.Response(new JsonObject());
                        return XeapiFixture.Response(J(new { code = outcome == "rejected" ? 403 : 200 }));
                    }
                    if (outcome == "readback-failed") return XeapiFixture.Response(J(new { code = 500 }));
                    return XeapiFixture.Response(StyleSongs([2]));
                }, DeviceSession());
                var editor = await f.Catalog.OpenDailyStyleEditorAsync();
                var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]));
                Equal(1, writes);
                Equal(outcome switch { "unknown" or "malformed" => MusicDailyStyleWriteStatus.Unknown,
                    "rejected" => MusicDailyStyleWriteStatus.Rejected, _ => MusicDailyStyleWriteStatus.Confirmed }, result.Status);
                if (outcome == "readback-failed") { True(result.Readback is null); Equal("UPSTREAM_ERROR", result.ErrorCode); }
                else { True(result.Readback is not null); True(result.SelectionMatches == false);
                    Equal("2", result.Readback!.Value.GetProperty("tags")[0].GetProperty("id").GetString()); }
                if (outcome == "rejected") Equal("FORBIDDEN", result.ErrorCode);
                if (outcome is "unknown" or "malformed") True(result.ErrorCode is not null);
            });
        yield return Case("style cancellation before admission sends nothing and after dispatch waits for receipt", async () =>
        {
            using var wire = new XeapiFixture();
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = 0; var reads = 0; var written = false;
            using var f = new Fixture(async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/bsr/sk/get") return wire.PublicKey(request);
                if (path.EndsWith("config/get", StringComparison.Ordinal)) return XeapiFixture.Response(StyleConfig());
                if (path.EndsWith("tag/save", StringComparison.Ordinal))
                { writes++; sent.TrySetResult(); await release.Task; written = true; return XeapiFixture.Response(J(new { code = 200 })); }
                reads++; return XeapiFixture.Response(StyleSongs(written ? [1] : [2]));
            }, DeviceSession());
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var skipped = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]), cancelled.Token);
            Equal(MusicDailyStyleWriteStatus.NotSent, skipped.Status); Equal("CANCELLED", skipped.ErrorCode); Equal(0, writes);
            using var token = new CancellationTokenSource();
            var pending = f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]), token.Token);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                token.Cancel(); True(!pending.IsCompleted);
                var interim = await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
                Equal("2", interim.GetProperty("tags")[0].GetProperty("id").GetString());
            }
            finally { release.TrySetResult(); }
            var receipt = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(MusicDailyStyleWriteStatus.Confirmed, receipt.Status); True(receipt.SelectionMatches == true); Equal(1, writes); Equal(2, reads);
            var current = await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
            Equal("1", current.GetProperty("tags")[0].GetProperty("id").GetString()); Equal(2, reads);
        });
        yield return Case("ordinary style reads merge and reuse the current epoch cache", async () =>
        {
            using var wire = new XeapiFixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            using var f = new Fixture(async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/bsr/sk/get") return wire.PublicKey(request);
                Interlocked.Increment(ref reads); entered.TrySetResult(); await release.Task;
                return XeapiFixture.Response(StyleSongs([1]));
            }, DeviceSession());
            var first = f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)); Equal(1, reads);
            var cached = await f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style)); Equal(1, reads);
            var remaining = cached.GetProperty("expiresAt").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            True(remaining is > 45000 and <= 60000);
        });
        yield return Case("style save fences old noncooperative flight and publishes fresh readback", async () =>
        {
            using var wire = new XeapiFixture();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            using var f = new Fixture(async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/bsr/sk/get") return wire.PublicKey(request);
                if (path.EndsWith("config/get", StringComparison.Ordinal)) return XeapiFixture.Response(StyleConfig());
                if (path.EndsWith("tag/save", StringComparison.Ordinal)) return XeapiFixture.Response(J(new { code = 200 }));
                if (Interlocked.Increment(ref reads) == 1)
                { entered.TrySetResult(); await release.Task; return XeapiFixture.Response(StyleSongs([2])); }
                return XeapiFixture.Response(StyleSongs([1]));
            }, DeviceSession());
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            var old = f.Catalog.DailyAsync(new(Branch: MusicDailyBranch.Style));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"])).WaitAsync(TimeSpan.FromSeconds(5));
                True(result.SelectionMatches == true); Equal(2, reads);
            }
            finally { release.TrySetResult(); }
            await Error("SNAPSHOT_CHANGED", () => old);
        });
        yield return Case("style preference routes reject GET before transport", async () =>
        {
            using var transport = MusicPlatformHttp.CreateWithHandler(new Handler((_, _) => throw new Exception("must not dispatch")));
            foreach (var path in new[] { "config/get", "tag/save" })
                await Error("ORIGIN_DENIED", () => transport.SendAsync("wy", new(System.Net.Http.HttpMethod.Get,
                    "https://interface3.music.163.com/xeapi/homepage/daily/song/" + path), default));
        });
        yield return Case("style XEAPI admission permits only exact preference routes", () =>
        {
            foreach (var path in new[] { "config/get", "tag/save" })
                True(MusicPlatformHttp.IsAllowedOrigin("wy", new("https://interface3.music.163.com/xeapi/homepage/daily/song/" + path)));
            foreach (var url in new[] { "http://interface3.music.163.com/xeapi/homepage/daily/song/tag/save",
                "https://interface3.music.163.com/xeapi/homepage/daily/song/tag/delete",
                "https://interface3.music.163.com/xeapi/homepage/daily/song/tag/save/extra",
                "https://example.com/xeapi/homepage/daily/song/tag/save",
                "https://interface3.music.163.com/xeapi/homepage/daily/song/tag/save?extra=1" })
                True(!MusicPlatformHttp.IsAllowedOrigin("wy", new(url)));
            return Task.CompletedTask;
        });
    }
}
