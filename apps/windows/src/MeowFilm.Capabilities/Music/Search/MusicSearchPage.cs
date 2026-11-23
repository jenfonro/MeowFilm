using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

internal sealed record MusicSearchPage(SearchCandidate[] Items, bool More, int Next)
{
    internal static MusicSearchPage Read(JsonElement response, string platform, MusicSearchKind kind, int page)
    {
        var data = JsonNode.Parse(response.GetRawText())!.AsObject();
        bool more = data["more"]!.GetValue<bool>();
        int next = more ? data["continuation"]!["page"]!.GetValue<int>() : page;
        // Only the actual cursor can establish progress; row counts do not.
        if (more && next <= page) throw new MusicPlatformException("PAGINATION_STALLED");
        long offset = data["offset"] is null
            ? (page - 1) * (long)MusicSearchPaging.For(platform).PageSize : data["offset"]!.GetValue<long>();
        var rows = new List<SearchCandidate>();
        foreach (var (node, index) in data["items"]!.AsArray().Select((node, index) => (node, index)))
        {
            var raw = node!.AsObject();
            var content = raw;
            double rank = raw["rank"] is null ? offset + index + 1 : raw["rank"]!.GetValue<double>();
            JsonObject item;
            string key;
            if (kind == MusicSearchKind.Songs)
            {
                item = NativeMusicSong.Normalize(content);
                key = NativeMusicSong.SourceKey(item);
            }
            else
            {
                key = content["id"]!.GetValue<string>();
                item = new JsonObject();
                foreach (string field in new[] { "id", "name", "cover", "count", "creator", "source", "engine", "description" })
                    if (content[field] is not null) item[field] = content[field]!.DeepClone();
            }
            rows.Add(new(platform, key, item, rank));
        }
        return new(rows.ToArray(), more, next);
    }
}
