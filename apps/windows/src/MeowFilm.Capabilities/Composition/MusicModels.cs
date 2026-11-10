using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed record PlaylistRow(string Id, string Name, string Kind, long Revision, int Count,
    string Platform = "", string RemoteId = "", string Cover = "")
{
    public string CountText => $"{Count} 首";
    public string Route => Kind == "network" ? "catalog-playlist/" + Platform + "/" + Uri.EscapeDataString(RemoteId)
        : Kind == "favorites" ? "favorites" : "playlist/" + Id;
    public static PlaylistRow Read(JsonElement value)
    {
        var kind = MusicDto.Text(value, "kind");
        MusicDto.Require(kind is "favorites" or "playlist" or "network");
        string id = MusicDto.Text(value, "id"), platform = "", remoteId = "", cover = "";
        int count;
        if (kind == "network")
        {
            platform = MusicDto.Text(value, "platform"); remoteId = MusicDto.Text(value, "remoteId");
            MusicDto.Require(id == MusicLibraryOwner.NetworkPlaylistId(platform, remoteId) &&
                value.GetProperty("editable").ValueKind == JsonValueKind.False);
            cover = MusicCatalogDto.Text(value, "cover");
            long number = MusicDto.Integer(value, "count"); MusicDto.Require(number <= int.MaxValue); count = (int)number;
        }
        else count = MusicDto.Count(value, "count");
        return new(id, MusicDto.Text(value, "name"), kind,
            MusicDto.Integer(value, "revision"), count, platform, remoteId, cover);
    }
}

public sealed record SongRow(JsonElement Raw, string? EntryId = null)
{
    private string Text(string key) => Raw.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    public string Id => Text("songId");
    public string Title => Text("title");
    public string Artist => Text("artist");
    public string Album => Text("album");
    public string Cover => Text("cover_url");
    public bool IsOnline => Raw.GetProperty("sources").GetArrayLength() > 0;
    public string Duration => Raw.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
        ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "—";
    public string SelectionKey => EntryId ?? Id;
    internal static SongRow Read(JsonElement value, string? entryId = null)
    {
        try
        {
            var input = JsonNode.Parse(value.GetRawText());
            var normalized = NativeMusicSong.Normalize(input);
            MusicDto.Require(JsonNode.DeepEquals(input, normalized));
            return new(JsonSerializer.SerializeToElement(normalized), entryId);
        }
        catch (MusicOperationException) { throw; }
        catch { throw new MusicOperationException("INVALID_RESPONSE"); }
    }
}

/// <summary>Read-only accessors for the lean song wire shape (sources[0] is the primary recording).</summary>
internal static class SongJson
{
    internal static JsonElement PrimaryInfo(JsonElement song) =>
        song.ValueKind == JsonValueKind.Object && song.TryGetProperty("sources", out var sources) &&
        sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0 &&
        sources[0].TryGetProperty("musicInfo", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
    internal static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    internal static string Platform(JsonElement song) => Text(PrimaryInfo(song), "source");
    internal static string InfoId(JsonElement info) =>
        info.ValueKind == JsonValueKind.Object && info.TryGetProperty("songmid", out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind is JsonValueKind.Null ? "" : value.GetRawText()
            : "";
    internal static string NativeId(JsonElement song) => InfoId(PrimaryInfo(song));
}

internal sealed class MusicOperationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ArchivedPlaylist(string Id, string Title, int Count)
{
    public string CountText => $"{Count} 首";
}

internal static class MusicDto
{
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value)
    {
        if (!value) throw new MusicOperationException("INVALID_RESPONSE");
    }
    internal static string Text(JsonElement value, string key)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out _));
        var field = value.GetProperty(key);
        Require(field.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(field.GetString()));
        return field.GetString()!;
    }
    internal static long Integer(JsonElement value, string key)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out _));
        var field = value.GetProperty(key);
        Require(field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var parsed) && parsed is >= 0 and <= 9007199254740991);
        return field.GetInt64();
    }
    internal static int Count(JsonElement value, string key)
    {
        var number = Integer(value, key);
        Require(number <= 100_000);
        return (int)number;
    }
    internal static JsonElement[] Page(JsonElement reply, string field, int offset)
    {
        Require(reply.GetProperty(field).ValueKind == JsonValueKind.Array);
        Require(Integer(reply, "offset") == offset && Integer(reply, "limit") is >= 1 and <= 100);
        var items = reply.GetProperty(field).EnumerateArray().ToArray();
        var total = Count(reply, "total");
        Require(items.Length <= Integer(reply, "limit") && offset + items.Length <= Math.Max(offset, total));
        Require(offset >= total || items.Length > 0);
        return items;
    }
    internal static int Mode(JsonElement value)
    {
        var mode = Integer(value, "mode"); Require(mode is >= 1 and <= 3); return (int)mode;
    }
}
