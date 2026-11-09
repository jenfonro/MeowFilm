using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Recommendations;

/// <summary>Device-only FM choices, keyed by the actual NetEase account. No live session is persisted.</summary>
internal sealed class MusicFmPreferences(IDocumentStore store)
{
    internal const string DocumentNamespace = "music.fm-selection.v1";
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<JsonObject> ReadAsync(string accountId, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (document, _) = await LoadAsync(token).ConfigureAwait(false);
            return document["accounts"]![accountId]?.DeepClone().AsObject() ??
                new JsonObject { ["mode"] = "default", ["submode"] = "", ["ruleId"] = "", ["tabTagId"] = "" };
        }
        finally { gate.Release(); }
    }
    internal async Task SaveAsync(string accountId, JsonElement snapshot, Func<bool> current, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!current()) return;
            var (document, fingerprint) = await LoadAsync(token).ConfigureAwait(false);
            var value = new JsonObject();
            foreach (string key in new[] { "mode", "submode", "ruleId", "tabTagId" })
                value[key] = snapshot.GetProperty(key).GetString();
            Validate(value);
            if (JsonNode.DeepEquals(document["accounts"]![accountId], value)) return;
            document["accounts"]![accountId] = value;
            if (!current()) return;
            await store.WriteAsync(DocumentNamespace, JsonSerializer.SerializeToUtf8Bytes(document), fingerprint, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async Task<(JsonObject Document, DocumentFingerprint Fingerprint)> LoadAsync(CancellationToken token)
    {
        var saved = await store.ReadAsync(DocumentNamespace, token).ConfigureAwait(false);
        if (saved.Data is null)
            return (new JsonObject { ["schema"] = 1, ["accounts"] = new JsonObject() }, saved.Fingerprint);
        try
        {
            var value = JsonNode.Parse(saved.Data)!.AsObject();
            RecommendationJson.Require(value.Count == 2 && value["schema"]!.GetValue<int>() == 1 &&
                value["accounts"] is JsonObject, "CORRUPT_STORAGE");
            foreach (var account in value["accounts"]!.AsObject())
            {
                RecommendationJson.Require(RecommendationJson.ValidId(account.Key), "CORRUPT_STORAGE");
                Validate(account.Value!.AsObject());
            }
            return (value, saved.Fingerprint);
        }
        catch (RecommendationException) { throw; }
        catch { throw new RecommendationException("CORRUPT_STORAGE"); }
    }
    private static void Validate(JsonObject value)
    {
        var json = JsonSerializer.SerializeToElement(value);
        RecommendationJson.Fields(json, "mode submode ruleId tabTagId", "mode submode ruleId tabTagId");
        RecommendationJson.Require(RecommendationJson.Text(json, "mode") is "default" or "familiar" or "explore" or "puzzle" or "scene",
            "CORRUPT_STORAGE");
        foreach (string key in new[] { "submode", "ruleId", "tabTagId" }) _ = RecommendationJson.Text(json, key, "", 64);
    }
}
