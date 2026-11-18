using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Core.Runtime;
namespace MeowFilm.Windows.Tests;
public static partial class MusicNativePlatformTests
{
    private static IEnumerable<TestCase> DailyStyleCases()
    {
        yield return Case("style editor consumes canonical categories in service order and sends selected tags", async () =>
        {
            await using var f = Peer(call => call.Operation == "dailyStyle.get" ? StyleCategories() : Json(new {
                status = "confirmed", selectionMatches = true, readback = new { tags = call.Parameters.GetProperty("tagIds") } }), Session("wy"));
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            Equal("3000", editor.Categories[0].Id); Equal(7, editor.Categories[0].Tags.Count);
            var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", Enumerable.Range(1, 7).Select(i => i.ToString()).ToArray()));
            Equal(MusicDailyStyleWriteStatus.Confirmed, result.Status); True(result.SelectionMatches == true);
            Equal(7, result.Readback!.Value.GetProperty("tags").GetArrayLength()); Equal(2, f.Requests.Count);
        });
        yield return Case("style editor cannot publish a write with a replacement account", async () =>
        {
            await using var f = Peer(_ => StyleCategories(), Session("wy"));
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            f.Sessions.Current = new("wy", "replacement", 2, "other", "SYNTHETIC_OTHER=value");
            var result = await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]));
            Equal(MusicDailyStyleWriteStatus.NotSent, result.Status); Equal("STALE_SESSION", result.ErrorCode); Equal(1, f.Requests.Count);
        });
        yield return Case("style reads retain categories and tags without a field completeness gate", async () =>
        {
            foreach (var categories in new object?[] { null, new { changed = true }, new object?[] {
                new { name = "Current", tags = new object?[] { new { name = "Visible tag" }, null } },
                new { id = "other", tags = new { changed = true } }, null
            } })
            {
                await using var f = Peer(_ => Json(new { categories }), Session("wy"));
                var editor = await f.Catalog.OpenDailyStyleEditorAsync();
                Equal(categories is object?[] ? 3 : 0, editor.Categories.Count);
                if (editor.Categories.Count == 3)
                {
                    Equal("", editor.Categories[0].Id); Equal("Current", editor.Categories[0].Name);
                    Equal(2, editor.Categories[0].Tags.Count); Equal("Visible tag", editor.Categories[0].Tags[0].Name);
                    Equal("", editor.Categories[0].Tags[1].Name);
                    Equal("other", editor.Categories[1].Id); Equal(0, editor.Categories[1].Tags.Count);
                    Equal("", editor.Categories[2].Id);
                }
                Equal(1, f.Requests.Count);
            }
        });
        foreach (string status in new[] { "confirmed", "rejected", "notSent", "unknown", "bad" })
        {
            string s = status;
            yield return Case("style retains the canonical " + s + " receipt", async () =>
            {
                await using var f = Peer(call => call.Operation == "dailyStyle.get" ? StyleCategories() : Json(new { status = s, errorCode = "FORBIDDEN" }), Session("wy"));
                var result = await f.Catalog.SaveDailyStyleAsync(await f.Catalog.OpenDailyStyleEditorAsync(), new("3000", ["1"]));
                Equal(s switch { "confirmed" => MusicDailyStyleWriteStatus.Confirmed, "rejected" => MusicDailyStyleWriteStatus.Rejected,
                    "notSent" => MusicDailyStyleWriteStatus.NotSent, _ => MusicDailyStyleWriteStatus.Unknown }, result.Status);
                Equal(2, f.Requests.Count);
            });
        }
        yield return Case("style cancellation before admission sends nothing; dispatched write settles after cancellation", async () =>
        {
            await using var f = Peer(_ => StyleCategories(), Session("wy"));
            var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Equal(MusicDailyStyleWriteStatus.NotSent, (await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]), cancelled.Token)).Status);
            Equal(1, f.Requests.Count);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnRequest = async (_, token) => { True(!token.CanBeCanceled); entered.SetResult(); await release.Task; return Json(new { status = "confirmed" }); };
            using var cancel = new CancellationTokenSource();
            var pending = f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]), cancel.Token);
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); True(!pending.IsCompleted); }
            finally { release.TrySetResult(); }
            Equal(MusicDailyStyleWriteStatus.Confirmed, (await pending).Status); Equal(2, f.Requests.Count);
        });
        yield return Case("style exchange loss is unknown and does not retry", async () =>
        {
            await using var f = Peer(_ => StyleCategories(), Session("wy")); var editor = await f.Catalog.OpenDailyStyleEditorAsync();
            f.OnRequest = (_, _) => throw new BridgeException("NETWORK_ERROR");
            Equal(MusicDailyStyleWriteStatus.Unknown, (await f.Catalog.SaveDailyStyleAsync(editor, new("3000", ["1"]))).Status);
            Equal(2, f.Requests.Count);
        });
    }
    private static JsonElement StyleCategories() => Json(new { categories = new[] { new {
        id = "3000", name = "服务分类", tags = Enumerable.Range(1, 7).Select(i => new { id = i.ToString(), name = "标签" + i }).ToArray()
    } } });
}
