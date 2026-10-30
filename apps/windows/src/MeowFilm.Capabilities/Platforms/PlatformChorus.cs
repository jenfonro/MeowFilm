// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>Exact recording metadata, seconds. Never a preview URL or inferred audio highlight.</summary>
public interface IMusicChorusCatalog
{
    Task<JsonElement> ChorusAsync(string platform, string id, CancellationToken token = default);
}

public sealed partial class MusicPlatformCatalog : IMusicChorusCatalog
{
    public Task<JsonElement> ChorusAsync(string platform, string id, CancellationToken token = default)
    {
        PJ.Require(platform is "wy" or "tx" or "kg", "UNSUPPORTED");
        id = PJ.IdInput(id, platform == "kg" ? "^[a-fA-F0-9]{32}$" :
            platform == "tx" ? "^[a-zA-Z0-9]{1,64}$" : "^[1-9][0-9]{0,15}$");
        if (platform == "wy") PJ.Require(long.Parse(id, CultureInfo.InvariantCulture) <= 9007199254740991);
        if (platform == "kg") { id = id.ToUpperInvariant(); PJ.Require(id.Any(c => c != '0')); }
        return RunAsync(platform, "chorus", new { id }, false, async c =>
        {
            JsonObject? point = null;
            // The fixed original QQ implementation explicitly returns no official point.
            if (platform == "tx") return PJ.Obj(("platform", platform), ("id", id), ("point", null));
            if (platform == "wy")
            {
                try
                {
                    point = await c.Wy("/api/song/chorus", PJ.Obj(("ids", JsonSerializer.Serialize(new[] { id }))), data =>
                    {
                        var decoded = ChorusPoint(data, platform, id);
                        PJ.Require(decoded is not null, "NOT_FOUND");
                        return decoded!;
                    });
                }
                catch (MusicPlatformException error) when (error.Code == "NOT_FOUND") { }
            }
            else
            {
                var query = Uri.EscapeDataString(PJ.Encode(PJ.Node(new[] { new { hash = id } })));
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://expendablekmrcdn.kugou.com/v1/audio_climax/audio?data=" + query);
                // Public CDN metadata must not receive account cookies or source-script headers.
                await c.CheckSessionAsync();
                var response = await http.SendChorusAsync(request, c.Token, timeout);
                await c.CheckSessionAsync();
                var data = response.Body;
                PJ.Require(PJ.Number(data["status"]) == 1, "INVALID_RESPONSE");
                point = ChorusPoint(data, platform, id);
            }
            return PJ.Obj(("platform", platform), ("id", id), ("point", point));
        }, token, privateCache: true);
    }
    private static JsonObject? ChorusPoint(JsonObject data, string platform, string id)
    {
        var rows = data[platform == "wy" ? "chorus" : "data"] ?? (platform == "wy" ? data["data"] : null);
        if (rows is not JsonArray list) return null;
        PJ.Require(list.Count <= 100, "LIMIT");
        var matches = list.OfType<JsonObject>().Where(row => platform == "wy"
            ? PJ.Scalar(row["id"]) == id
            : string.Equals(PJ.Scalar(row["hash"]), id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var row = matches[0];
        if (Milliseconds(row[platform == "wy" ? "startTime" : "start_time"]) is { } start &&
            Milliseconds(row[platform == "wy" ? "endTime" : "end_time"]) is { } end && end > start &&
            (platform == "wy" || start > 0 && Milliseconds(row["timelength"]) == end - start))
            return PJ.Obj(("start", start / 1000d), ("end", end / 1000d));
        return null;
    }
    private static long? Milliseconds(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) && number is >= 0 and <= 86400000
            ? number : null;
}
