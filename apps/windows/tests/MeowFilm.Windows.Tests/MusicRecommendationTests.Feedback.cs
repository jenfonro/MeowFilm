using System.Text.Json;
using System.Text.Json.Nodes;
using static MeowFilm.Windows.Tests.RecommendationTest;

namespace MeowFilm.Windows.Tests;

public static partial class MusicRecommendationTests
{
    private static async Task SourceFacts()
    {
        await using var f = new Fixture();
        f.Catalog.Handle = (request, _) =>
        {
            var response = JsonNode.Parse(Batch(request.Method,
                request.Method == "similar" ? ["10", "11"] : ["20", "21"]).GetRawText())!.AsObject();
            response["context"] = request.Method == "similar"
                ? JsonSerializer.SerializeToNode(new { sourceIds = new[] { "77", "88" }, sourceType = "artist" })
                : JsonSerializer.SerializeToNode(new { mode = "SCENE_RCMD", submode = "HOT_SONG" });
            foreach (var metadata in response["recommendations"]!.AsArray().OfType<JsonObject>())
            {
                metadata["feedbackSource"] = "upstream-source";
                metadata["feedbackSourceId"] = "origin-" + metadata["id"]!.GetValue<string>();
            }
            return Task.FromResult(J(response));
        };
        var similar = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "similar", platform = "wy", sourceIds = new[] { "77", "88" }, sourceType = "artist" });
        Check(S(similar, "error") == "");
        var entries = (await f.Q()).GetProperty("entries").EnumerateArray().ToArray();
        foreach (var entry in entries)
        {
            var feedback = f.Rec.GetFeedback(S(entry, "id"))!;
            Check(feedback.Context is { } context &&
                context.GetProperty("sourceIds").EnumerateArray().Select(id => id.GetString()).SequenceEqual(["77", "88"]) &&
                S(context, "sourceType") == "artist");
            Check(feedback.Metadata is { } metadata && S(metadata, "id") == SongId(entry.GetProperty("song")) &&
                feedback.Algorithm == "alg-" + S(metadata, "id") && feedback.FeedbackSource == "upstream-source" &&
                S(metadata, "feedbackSourceId") == "origin-" + S(metadata, "id"));
        }
        await f.Port.ExecuteAsync("music.queue.append", new { songs = new[] { Song("99") }, next = true });
        Check(f.Rec.GetFeedback(S(entries[0], "id"))?.Context is not null &&
            f.Rec.GetFeedback(Current(await f.Q())) is not null, "inserting an unrelated song cannot replace live occurrence origins");
        var fm = await f.Rec.ExecuteAsync("music.recommendations.start",
            new { kind = "fm", platform = "wy", mode = "scene", submode = "HOT_SONG" });
        Check(S(fm, "error") == "" && f.Rec.GetFeedback(S(entries[0], "id")) is null);
        var scene = f.Rec.GetFeedback(Current(await f.Q()))!;
        Check(scene is { Mode: "scene", Submode: "HOT_SONG" } && scene.Context is { } fmContext &&
            S(fmContext, "mode") == "SCENE_RCMD" && S(fmContext, "submode") == "HOT_SONG");
    }
}
