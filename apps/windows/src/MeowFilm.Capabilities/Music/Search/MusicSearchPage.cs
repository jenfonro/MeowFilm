using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

internal sealed record MusicSearchPage(SearchCandidate[] Items, bool More, int Next)
{
    private static readonly string[] PlaylistFields =
        ["id", "name", "cover", "count", "creator", "source", "engine", "description"];

    internal static MusicSearchPage Read(JsonElement response, string platform, MusicSearchKind kind, int page)
    {
        bool more = response.GetProperty("more").GetBoolean();
        int next = more ? response.GetProperty("continuation").GetProperty("page").GetInt32() : page;
        // Only the actual cursor can establish progress; row counts do not.
        if (more && next <= page) throw new MusicPlatformException("PAGINATION_STALLED");
        long offset = response.TryGetProperty("offset", out var offEl) && offEl.ValueKind == JsonValueKind.Number
            ? offEl.GetInt64()
            : (page - 1) * (long)MusicSearchPaging.For(platform).PageSize;
        var rows = new List<SearchCandidate>();
        int index = 0;
        foreach (var raw in response.GetProperty("items").EnumerateArray())
        {
            double rank = raw.TryGetProperty("rank", out var rEl) && rEl.ValueKind == JsonValueKind.Number
                ? rEl.GetDouble() : offset + index + 1;
            JsonElement item;
            string key;
            if (kind == MusicSearchKind.Songs)
            {
                var normalized = NativeMusicSong.Normalize(MusicWireCodec.ReadSong(raw));
                key = NativeMusicSong.SourceKey(normalized);
                item = MusicWireCodec.SongToElement(normalized);
            }
            else
            {
                key = raw.GetProperty("id").GetString()!;
                item = MusicWireCodec.BuildObject(w =>
                {
                    foreach (string field in PlaylistFields)
                        if (raw.TryGetProperty(field, out var fv) && fv.ValueKind != JsonValueKind.Null)
                        {
                            w.WritePropertyName(field);
                            fv.WriteTo(w);
                        }
                });
            }
            rows.Add(new SearchCandidate(platform, key, item, rank));
            index++;
        }
        return new MusicSearchPage(rows.ToArray(), more, next);
    }
}
