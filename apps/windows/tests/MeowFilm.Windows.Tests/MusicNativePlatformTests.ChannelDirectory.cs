namespace MeowFilm.Windows.Tests;
public static partial class MusicNativePlatformTests
{
    private static IEnumerable<TestCase> ChannelDirectoryCases()
    {
        yield return Case("directory consumes every current channel without a layout count gate", async () =>
        {
            var channels = Enumerable.Range(0, 120).Select(i => new { id = "channel-" + i, title = "Channel " + i }).ToArray();
            await using var f = Peer(call => { Equal("channels", call.Operation); return Json(new { items = channels }); });
            Equal(120, (await f.Catalog.ChannelsAsync(new("wy"))).GetProperty("items").GetArrayLength());
            Equal(1, f.Requests.Count);
        });
        yield return Case("channel forwards opaque continuation and real page code without a directory reread", async () =>
        {
            var continuation = Json(new { cursor = "next", consumed = 37 });
            await using var f = Peer(call =>
            {
                Equal("channel", call.Operation); Equal("cube", call.Parameters.GetProperty("id").GetString());
                Equal("remote-page", call.Parameters.GetProperty("pageCode").GetString());
                Equal(continuation.GetRawText(), call.Parameters.GetProperty("continuation").GetRawText());
                return Json(new { sections = new object[] { }, more = true, continuation });
            });
            True((await f.Catalog.ChannelAsync(new("wy", "cube", "remote-page", continuation, "tag"))).GetProperty("more").GetBoolean());
            Equal(1, f.Requests.Count);
        });
    }
}
