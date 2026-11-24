using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Search;

public sealed record MusicSearchPlaylistRecord(
    string Id,
    string Name,
    string Cover,
    int? Count,
    string Creator,
    string Source,
    string Engine,
    string Description)
{
    public bool ContentEquals(MusicSearchPlaylistRecord other) =>
        Id == other.Id && Name == other.Name && Cover == other.Cover &&
        Count == other.Count && Creator == other.Creator && Source == other.Source &&
        Engine == other.Engine && Description == other.Description;
}

public sealed record MusicSearchPlatformSnapshot(
    string Platform,
    int Page,
    int Count,
    bool More,
    bool Loading,
    string Error,
    long CheckedAt = 0);

public sealed record MusicSearchObservation(
    string Platform,
    int Page,
    int Pages,
    int Limit,
    long CheckedAt,
    long Order,
    long Generation,
    bool Cached,
    string Error,
    MusicSongRecord[] Songs);

public sealed record MusicSearchSnapshot(
    string Id,
    long Revision,
    string Keyword,
    string Kind,
    int Round,
    MusicSongRecord[] Songs,
    MusicSearchPlaylistRecord[] Playlists,
    bool More,
    bool Loading,
    bool Partial,
    string Code,
    bool Cancelled,
    MusicSearchPlatformSnapshot[] Platforms,
    MusicSearchObservation[] Observations)
{
    public int ItemCount => Kind == "songs" ? Songs.Length : Playlists.Length;
}

internal sealed record MusicSearchPage(SearchCandidate[] Items, bool More, int Next)
{
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
            if (kind == MusicSearchKind.Songs)
            {
                var normalized = NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(raw));
                string key = NativeMusicSong.SourceKey(normalized);
                rows.Add(new SearchCandidate(platform, key, normalized, null, rank));
            }
            else
            {
                string id = ReadScalarText(raw, "id");
                MusicRules.Require(id.Length > 0, "INVALID_RESPONSE");
                var playlist = new MusicSearchPlaylistRecord(
                    Id: id,
                    Name: ReadStr(raw, "name"),
                    Cover: ReadStr(raw, "cover"),
                    Count: raw.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number && cEl.TryGetInt32(out int count) ? count : null,
                    Creator: ReadStr(raw, "creator"),
                    Source: ReadStr(raw, "source") is { Length: > 0 } src ? src : platform,
                    Engine: ReadStr(raw, "engine"),
                    Description: ReadStr(raw, "description"));
                rows.Add(new SearchCandidate(platform, id, null, playlist, rank));
            }
            index++;
        }
        return new MusicSearchPage(rows.ToArray(), more, next);
    }

    private static string ReadStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string ReadScalarText(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            _ => ""
        };
    }
}
