namespace MeowFilm.Windows.Tests;
public static partial class MusicNativePlatformTests
{
    private static IEnumerable<TestCase> ChartCases()
    {
        yield return Case("charts preserve canonical album items and their action locators", async () =>
        {
            await using var f = Peer(call => { Equal("charts", call.Operation); return Json(new { sections = new[] {
                new { id = "new-chart", type = "albums", items = new[] { new { id = "canonical", name = "Current album",
                    cover = "", kind = "album", action = new { type = "album", id = "canonical" } } } }
            } }); });
            var result = await f.Catalog.ChartAsync(new("wy", "albums", "daily", 2026));
            Equal("canonical", result.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("id").GetString());
            Equal(1, f.Requests.Count);
        });
        yield return Case("chorus keeps actual time and null independently of native recording guesses", async () =>
        {
            foreach (object? point in new object?[] { null, new { start = 0d, end = 10d }, new { start = 71.2, end = 82d } })
            {
                await using var f = Peer(_ => Json(new { point }));
                Equal(Json(new { point }).GetRawText(), (await f.Catalog.ChorusAsync("wy", Huge)).GetRawText());
            }
        });
    }
}
