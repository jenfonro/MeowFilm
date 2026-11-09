using System.Text.Json;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static partial class FilmSourcesTests
{
    internal static async Task SearchWire()
    {
        var runtime = new Runtime();
        runtime.Add("a"); runtime.Add("b"); runtime.Add("disabled"); runtime.Nodes["disabled"].Enabled = false;
        await using var service = new FilmSourceService(runtime, new Store());
        await service.RefreshAsync();
        await service.SaveVideoOptionsAsync(Key("b"), new(true, true, false, false));
        var sources = await service.ReadSearchSourcesAsync(default);
        Check(sources.Count == 1 && sources[0].Cat.InstallationId == "a", "only enabled ready searchable source is selected");
        runtime.Invoke = (call, _) =>
        {
            Check(call.InstallationId == "a" && call.Generation == runtime.Nodes["a"].Generation &&
                call.SiteId == sources[0].Site.Key.SiteKey && call.Operation == "search", "production transport preserves runtime identity");
            Fields(call.Payload, "wd", "page");
            Check(call.Payload.GetProperty("wd").GetString() == "原词" && call.Payload.GetProperty("page").GetInt32() == 4,
                "production search forwards original query and independently selected page");
            return Task.FromResult(Element(JsonSerializer.Serialize(new { pageCount = 4,
                list = Enumerable.Range(0, 141).Select(i => new { vod_id = i.ToString(), vod_name = i + "影片" }).ToArray() })));
        };
        var payload = await service.SearchAsync(sources[0], "原词", 4, default);
        Check(payload.GetProperty("list").GetArrayLength() == 141, "structured string result retains the complete page");
        runtime.Invoke = (_, _) => Task.FromResult(Element(new { success = false, list = Array.Empty<object>() }));
        try { await service.SearchAsync(sources[0], "原词", 4, default); throw new InvalidOperationException("expected source error"); }
        catch (FilmSourceException) { }
        await service.EnableCatAsync("a", false);
        Check(!service.IsSearchSourceCurrent(sources[0]), "disabled generation loses search authority");
    }
}
