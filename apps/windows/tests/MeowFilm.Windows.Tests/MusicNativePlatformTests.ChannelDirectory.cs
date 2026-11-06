using System.Net.Http;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativePlatformTests
{
    private const string WyDirectoryPath = "/eapi/link/position/show/resource";
    private const string WyChannelContentPath = "/eapi/link/page/rcmd/resource/show";

    private static IEnumerable<TestCase> ChannelDirectoryCases()
    {
        foreach (bool signed in new[] { false, true })
        {
            bool authenticated = signed;
            yield return Case("channel directory is read once before the default channel " + (signed ? "signed" : "anonymous"), async () =>
            {
                int directories = 0, contents = 0;
                using var f = new Fixture(request =>
                {
                    if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                    { directories++; return Directory(); }
                    Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                    Equal("HOME_DISCOVERY_PAGE", S(Eapi(request).Body["pageCode"]));
                    contents++;
                    return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
                }, authenticated ? Session("wy") : null);
                var tabs = await f.Catalog.ChannelsAsync(new("wy"));
                var first = tabs.GetProperty("items")[0];
                Equal("feature", first.GetProperty("id").GetString());
                Equal("channel", first.GetProperty("kind").GetString());
                Equal("primary", first.GetProperty("placement").GetString());
                var page = await f.Catalog.ChannelAsync(new("wy", first.GetProperty("id").GetString()!));
                Equal("feature", page.GetProperty("channel").GetProperty("id").GetString());
                Equal("500", page.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetString());
                Equal(1, directories); Equal(1, contents); Equal(2, f.Calls);
            });
        }
        yield return Case("direct channel paging and channel changes reuse the directory without relaxing identity", async () =>
        {
            int directories = 0, contents = 0;
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                { directories++; return Directory(); }
                contents++;
                if (request.RequestUri.AbsolutePath == "/weapi/v1/discovery/new/songs")
                    return Json(J(new { code = 200, data = new[] { Wy("1") } }));
                Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                string cursor = S(Eapi(request).Body["cursor"]);
                True(cursor is "0" or "1");
                return Home(cursor == "0", cursor == "0" ? "1" : "2",
                    J(new { resourceType = "playlist", resourceId = cursor == "0" ? "500" : "501", name = "列表" }));
            });
            var first = await f.Catalog.ChannelAsync(new("wy", "feature"));
            var next = await f.Catalog.ChannelAsync(new("wy", "feature", Continuation: first.GetProperty("continuation")));
            Equal("501", next.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetString());
            True(!next.GetProperty("more").GetBoolean());
            Song((await f.Catalog.ChannelAsync(new("wy", "newsong"))).GetProperty("sections")[0].GetProperty("items")[0], "wy", "1");
            await Error("IDENTITY_MISMATCH", () => f.Catalog.ChannelAsync(new("wy", "rock", PageCode: "OTHER_PAGE")));
            await Error("NOT_FOUND", () => f.Catalog.ChannelAsync(new("wy", "missing")));
            Equal(1, directories); Equal(3, contents); Equal(4, f.Calls);
        });
        yield return Case("directory refresh replaces descriptors while content refresh reuses the current directory", async () =>
        {
            int directories = 0, contents = 0;
            string title = "旧精选";
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                { directories++; return NamedChannelDirectory(title); }
                Equal(WyChannelContentPath, request.RequestUri.AbsolutePath); contents++;
                return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
            }, Session("wy"));
            await f.Catalog.ChannelsAsync(new("wy", Refresh: true));
            Equal(title, (await f.Catalog.ChannelAsync(new("wy", "feature", Refresh: true))).GetProperty("title").GetString());
            Equal(1, directories); Equal(1, contents);
            title = "新精选";
            var refreshed = await f.Catalog.ChannelsAsync(new("wy", Refresh: true));
            Equal(title, refreshed.GetProperty("items")[0].GetProperty("name").GetString());
            Equal(title, (await f.Catalog.ChannelAsync(new("wy", "feature", Refresh: true))).GetProperty("title").GetString());
            Equal(title, (await f.Catalog.ChannelsAsync(new("wy"))).GetProperty("items")[0].GetProperty("name").GetString());
            Equal(2, directories); Equal(2, contents);
        });
        yield return Case("failed directory reads and refreshes remain retryable without caching empty success", async () =>
        {
            int directories = 0, contents = 0;
            bool fail = true;
            string title = "旧精选";
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                {
                    directories++;
                    return fail ? Json(J(new { code = 500 })) : NamedChannelDirectory(title);
                }
                Equal(WyChannelContentPath, request.RequestUri.AbsolutePath); contents++;
                return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
            });
            await Error("UPSTREAM_ERROR", () => f.Catalog.ChannelAsync(new("wy", "feature")));
            fail = false;
            await f.Catalog.ChannelsAsync(new("wy"));
            Equal(title, (await f.Catalog.ChannelAsync(new("wy", "feature"))).GetProperty("title").GetString());
            Equal(2, directories); Equal(1, contents);
            fail = true;
            await Error("UPSTREAM_ERROR", () => f.Catalog.ChannelsAsync(new("wy", Refresh: true)));
            fail = false; title = "新精选";
            await f.Catalog.ChannelsAsync(new("wy", Refresh: true));
            Equal(title, (await f.Catalog.ChannelAsync(new("wy", "feature", Refresh: true))).GetProperty("title").GetString());
            Equal(4, directories); Equal(2, contents);
        });
        yield return Case("directory snapshots do not cross anonymous account or generation boundaries", async () =>
        {
            int directories = 0;
            string title = "匿名精选";
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                { directories++; return NamedChannelDirectory(title); }
                Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
            });
            await f.Catalog.ChannelsAsync(new("wy"));
            foreach (var session in new[] { Session("wy"), Session("wy", 2),
                new MusicPlatformSession("wy", "other-synthetic-scope", 1, "2", "MUSIC_U=SYNTHETIC_OTHER") })
            {
                f.Sessions.Current = session;
                title = "账号精选" + (directories + 1);
                Equal(title, (await f.Catalog.ChannelAsync(new("wy", "feature"))).GetProperty("title").GetString());
            }
            Equal(4, directories);
            f.Sessions.Current = null;
            f.Catalog.Invalidate("wy");
            title = "退出精选";
            Equal(title, (await f.Catalog.ChannelAsync(new("wy", "feature"))).GetProperty("title").GetString());
            Equal(5, directories);
        });
        foreach (bool direct in new[] { false, true })
        {
            bool standalone = direct;
            yield return Case("directory response client update reaches channel content " + (direct ? "direct" : "after tabs"), async () =>
            {
                int directories = 0;
                using var f = new Fixture(request =>
                {
                    if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                    {
                        directories++;
                        var response = Directory();
                        response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_DIRECTORY; Domain=music.163.com");
                        return response;
                    }
                    Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                    Equal("SYNTHETIC_DIRECTORY", S(Eapi(request).Body["header"]!["NMTID"]));
                    return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
                }, Session("wy"));
                if (!standalone) await f.Catalog.ChannelsAsync(new("wy"));
                var page = await f.Catalog.ChannelAsync(new("wy", "feature"));
                True(!page.GetRawText().Contains("SYNTHETIC_DIRECTORY", StringComparison.Ordinal));
                Equal(1, directories); Equal(2, f.Calls);
            });
        }
        yield return Case("directory cache follows the confirmed client generation", async () =>
        {
            int directories = 0;
            using var f = new Fixture(request =>
            {
                if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                { directories++; return Directory(); }
                Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                Equal("SYNTHETIC_ADVANCED_DIRECTORY", S(Eapi(request).Body["header"]!["NMTID"]));
                return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
            });
            await f.Catalog.ChannelsAsync(new("wy"));
            var client = await f.Clients.Owner.AcquireWyAsync();
            await f.Clients.Owner.AcceptWyAsync(client, null,
                ["NMTID=SYNTHETIC_ADVANCED_DIRECTORY; Domain=music.163.com"], () => { }, default);
            await f.Catalog.ChannelAsync(new("wy", "feature"));
            Equal(2, directories);
            await f.Catalog.ChannelsAsync(new("wy"));
            Equal(2, directories);
        });
        foreach (bool cancelChannel in new[] { false, true })
        {
            bool channelFirst = cancelChannel;
            yield return Case("inflight directory is shared when one subscriber cancels " + (cancelChannel ? "channel" : "tabs"), async () =>
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool physicallyCancelled = false;
                int directories = 0, contents = 0;
                using var f = new Fixture(async (request, token) =>
                {
                    if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                    {
                        Interlocked.Increment(ref directories);
                        using var registration = token.Register(() => physicallyCancelled = true);
                        entered.TrySetResult(); await release.Task;
                        return Directory();
                    }
                    Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                    Interlocked.Increment(ref contents);
                    return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
                });
                using var cancellation = new CancellationTokenSource();
                var cancelled = channelFirst
                    ? f.Catalog.ChannelAsync(new("wy", "feature"), cancellation.Token)
                    : f.Catalog.ChannelsAsync(new("wy"), cancellation.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    var survivor = channelFirst
                        ? f.Catalog.ChannelsAsync(new("wy"))
                        : f.Catalog.ChannelAsync(new("wy", "feature"));
                    cancellation.Cancel();
                    await Error("CANCELLED", () => cancelled);
                    True(!physicallyCancelled && !f.Catalog.DrainAsync().IsCompleted);
                    release.TrySetResult();
                    var result = await survivor.WaitAsync(TimeSpan.FromSeconds(5));
                    Equal("wy", result.GetProperty("platform").GetString());
                    await f.Catalog.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Equal(1, directories); Equal(1, contents);
                }
                finally { release.TrySetResult(); }
            });
        }
        foreach (string retirement in new[] { "invalidate", "close", "client-reload" })
        {
            string reason = retirement;
            yield return Case("retired directory cannot publish channel content after " + retirement, async () =>
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int directories = 0, contents = 0;
                using var f = new Fixture(async (request, token) =>
                {
                    if (request.RequestUri!.AbsolutePath == WyDirectoryPath)
                    {
                        if (Interlocked.Increment(ref directories) != 1) return NamedChannelDirectory("新精选");
                        using var registration = token.Register(() => cancelled.TrySetResult());
                        entered.TrySetResult(); await release.Task;
                        return NamedChannelDirectory("旧精选");
                    }
                    Equal(WyChannelContentPath, request.RequestUri.AbsolutePath);
                    Interlocked.Increment(ref contents);
                    return Home(false, "1", J(new { resourceType = "playlist", resourceId = "500", name = "列表" }));
                });
                var old = f.Catalog.ChannelAsync(new("wy", "feature"));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    if (reason == "close") f.Catalog.Dispose();
                    else if (reason == "invalidate") f.Catalog.Invalidate("wy");
                    else await f.Clients.Owner.ReloadAsync();
                    if (reason != "client-reload")
                        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var drain = f.Catalog.DrainAsync();
                    True(!drain.IsCompleted);
                    release.TrySetResult();
                    await Error(reason == "close" ? "CLOSED" : reason == "invalidate" ? "STALE_SESSION" : "STORAGE_CONFLICT", () => old);
                    await drain.WaitAsync(TimeSpan.FromSeconds(5));
                    Equal(0, contents);
                    if (reason == "close")
                        await Error("CLOSED", () => f.Catalog.ChannelsAsync(new("wy")));
                    else
                    {
                        Equal("新精选", (await f.Catalog.ChannelAsync(new("wy", "feature"))).GetProperty("title").GetString());
                        await f.Catalog.ChannelsAsync(new("wy"));
                        Equal(2, directories); Equal(1, contents);
                    }
                }
                finally { release.TrySetResult(); }
            });
        }
    }

    private static HttpResponseMessage NamedChannelDirectory(string title) => Json(J(new
    {
        code = 200,
        data = new { channels = new[] { new { code = "feature", name = title, url = "" } } }
    }));
}
