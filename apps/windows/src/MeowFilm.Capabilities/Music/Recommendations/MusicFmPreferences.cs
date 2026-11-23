using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Capabilities.Recommendations;

/// <summary>Device-only choices read and update one actual account row.</summary>
internal sealed class MusicFmPreferences(IStorageDatabase database)
{
    internal const string DocumentNamespace = "music.fm-selection.v2";
    internal Task<JsonObject> ReadAsync(string accountId, CancellationToken token) => database.ReadAsync(db =>
        FMStorageRecords.Selection.Read(db, [accountId]) ?? new JsonObject { ["mode"] = "default", ["submode"] = "", ["ruleId"] = "", ["tabTagId"] = "" }, token);
    internal async Task SaveAsync(string accountId, JsonElement snapshot, Func<bool> current, CancellationToken token)
    {
        var value = new JsonObject();
        foreach (var key in new[] { "mode", "submode", "ruleId", "tabTagId" }) value[key] = snapshot.GetProperty(key).GetString();
        Validate(value);
        if (!current()) return;
        await database.WriteAsync(db =>
        {
            if (!current()) return false;
            FMStorageRecords.Selection.Save(db, value, FMStorageRecords.Selection.Read(db, [accountId]), [accountId]);
            return true;
        }, token, 2048, commit: commit => { if (!current()) throw new OperationCanceledException(token); commit(); }).ConfigureAwait(false);
    }
    private static void Validate(JsonObject value)
    {
        var json = JsonSerializer.SerializeToElement(value);
        RecommendationJson.Fields(json, "mode submode ruleId tabTagId");
        RecommendationJson.Require(RecommendationJson.Text(json, "mode") is "default" or "familiar" or "explore" or "puzzle" or "scene",
            "CORRUPT_STORAGE");
        foreach (string key in new[] { "submode", "ruleId", "tabTagId" }) _ = RecommendationJson.Text(json, key);
    }
}
