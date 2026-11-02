using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

internal sealed record MusicSearchPage(SearchCandidate[] Items, bool More, int Next,
    string TotalUnit, long? Total, int? GroupCount, int? RawItemCount)
{
    internal static MusicSearchPage Read(JsonElement response, string platform, MusicSearchKind kind, int page)
    {
        var data = LJ.Object(JsonNode.Parse(response.GetRawText(),
            documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }));
        LJ.Require(LJ.S(data["platform"]) == platform && LJ.I(data["page"], 1, int.MaxValue) == page, "IDENTITY_MISMATCH");
        int limit = (int)LJ.I(data["limit"], 1, 100);
        string unit = data["totalUnit"] is null ? "items" : LJ.Text(data["totalUnit"], 16);
        LJ.Require(unit is "items" or "groups", "INVALID_RESPONSE");
        bool groups = unit == "groups";
        LJ.Require(!groups || platform == "mg" && kind == MusicSearchKind.Songs, "INVALID_RESPONSE");
        var items = LJ.Array(data["items"]); LJ.Require(LJ.IsBool(data["more"]), "INVALID_RESPONSE");
        bool more = LJ.B(data["more"]);
        int next = data["continuation"] is JsonObject continuation
            ? (int)LJ.I(continuation["page"], page + 1L, int.MaxValue) : more ? checked(page + 1) : page;
        long? total = data["total"] is null ? null : LJ.I(data["total"]);
        int? groupCount = null, rawItemCount = null;
        // A multi-page offset batch declares its own offset; rank stays a platform-global position.
        long offset = data["offset"] is null ? (page - 1) * (long)limit : LJ.I(data["offset"]);
        LJ.Require(data["offset"] is null || MusicSearchPaging.For(platform).PagesPerRequest > 1 &&
            offset == (page - 1) * (long)MusicSearchPaging.For(platform).PageSize, "INVALID_RESPONSE");
        if (groups)
        {
            LJ.Require(total is not null, "INVALID_RESPONSE");
            groupCount = (int)LJ.I(data["groupCount"], 0, limit);
            rawItemCount = (int)LJ.I(data["rawItemCount"], 0, int.MaxValue);
            LJ.Require((!more || groupCount == limit && page * (long)limit < total.Value) &&
                rawItemCount >= groupCount && items.Count <= rawItemCount &&
                (rawItemCount == 0 || items.Count > 0), "INVALID_RESPONSE");
            // A group page is one native page, not a page of flattened recordings.
            LJ.Require(!more || data["continuation"] is JsonObject && next == page + 1, "INVALID_RESPONSE");
            LJ.Require(more || data["continuation"] is null, "INVALID_RESPONSE");
        }
        else
            LJ.Require(items.Count <= limit && !data.ContainsKey("groupCount") && !data.ContainsKey("rawItemCount"), "INVALID_RESPONSE");
        List<SearchCandidate> rows = [];
        HashSet<string> copyrights = new(StringComparer.Ordinal);
        for (int index = 0; index < items.Count; index++)
        {
            var raw = LJ.Object(items[index]);
            JsonObject itemInput;
            double rank;
            if (groups)
            {
                LJ.Fields(raw, "rank song", "rank song");
                rank = LJ.N(raw["rank"], double.NaN);
                itemInput = LJ.Object(raw["song"]);
            }
            else
            {
                rank = offset + index + 1;
                itemInput = raw;
            }
            LJ.Require(double.IsFinite(rank) && rank > 0 && rank <= LJ.MaxInteger, "INVALID_RESPONSE");
            if (groups)
            {
                // Provider retains the original group rank; versions of one group share it.
                LJ.Require(rank == Math.Truncate(rank) &&
                    rank > offset && rank <= offset + groupCount, "INVALID_RESPONSE");
            }
            JsonObject item; string key;
            if (kind == MusicSearchKind.Songs)
            {
                item = NativeMusicSong.Normalize(itemInput);
                var native = NativeMusicSong.SourceData(item);
                LJ.Require(LJ.S(native["source"]) == platform, "IDENTITY_MISMATCH");
                LJ.Require(LJ.Id(NativeMusicSong.InfoId(native), 512), "IDENTITY_MISMATCH");
                key = NativeMusicSong.SourceKey(item);
                LJ.Require(key.Length > 0, "IDENTITY_MISMATCH");
                if (groups)
                {
                    string copyright = LJ.Scalar(native["copyrightId"]);
                    LJ.Require(LJ.Id(copyright, 512) && copyrights.Add(copyright), "IDENTITY_MISMATCH");
                    // Dedup identity is not the resource id: keep native songId/songmid untouched.
                    key = LJ.Stringify(new JsonArray("mg-copyright", copyright));
                }
            }
            else
            {
                LJ.Require(!groups, "INVALID_RESPONSE");
                string id = LJ.Scalar(itemInput["id"]);
                LJ.Require(LJ.Id(id, 512) && (itemInput["source"] is null || LJ.S(itemInput["source"]) == platform), "IDENTITY_MISMATCH");
                item = new JsonObject(itemInput.Where(p => "id name cover count creator source engine description".Split(' ').Contains(p.Key))
                    .Select(p => KeyValuePair.Create(p.Key, NativeMusicSong.Durable(p.Value, p.Key))));
                item["id"] = id; item["source"] = platform;
                foreach (var field in new[] { "name", "cover", "creator", "description" })
                    LJ.Require(item[field] is null || item[field] is JsonValue v && v.TryGetValue<string>(out _), "INVALID_RESPONSE");
                key = id;
            }
            rows.Add(new(platform, key, item, rank));
        }
        return new(rows.ToArray(), more, next, unit, total, groupCount, rawItemCount);
    }
}
