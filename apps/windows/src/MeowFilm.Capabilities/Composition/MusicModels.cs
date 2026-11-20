using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
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

public sealed record SongRow(JsonElement Raw, string? EntryId = null) : System.ComponentModel.INotifyPropertyChanged
{
    public JsonElement Raw { get; private set; } = Raw;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal long RepositoryPublication { get; private set; } = -1;
    internal bool ApplyRepository(JsonElement? value, long publication = -1)
    {
        if (value is not { } next || publication < RepositoryPublication) return false;
        RepositoryPublication = publication;
        if (Raw.GetRawText() == next.GetRawText()) return false;
        Raw = next.Clone();
        foreach (var name in new[] { nameof(Raw), nameof(Title), nameof(Artist), nameof(Album),
            nameof(Cover), nameof(Duration), nameof(IsOnline) })
            PropertyChanged?.Invoke(this, new(name));
        return true;
    }
    private string Text(string key) => Raw.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    public string Id => Text("songId");
    public string Title => Text("title");
    public string Artist => Text("artist");
    public string Album => Text("album");
    public string Cover => Text("cover_url");
    public bool IsOnline => MusicCatalogDto.Rows(MusicCatalogDto.Field(Raw, "sources")).Any();
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
            return new(JsonSerializer.SerializeToElement(normalized), entryId);
        }
        catch (MusicOperationException) { throw; }
        catch { throw new MusicOperationException("INVALID_RESPONSE"); }
    }
}

public sealed partial class MusicCapability
{
    private static void ReconcileSongRows(ObservableCollection<SongRow> target, IReadOnlyList<SongRow> next)
    {
        // A queue occurrence has EntryId; catalog members can repeat the same permanent song.
        // Match each occurrence once, in order, rather than deduplicating songs by their Id.
        var available = target.GroupBy(row => row.SelectionKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<SongRow>(group), StringComparer.Ordinal);
        var rows = next.Select(song =>
        {
            if (!available.TryGetValue(song.SelectionKey, out var occurrences) || occurrences.Count == 0) return song;
            var retained = occurrences.Dequeue();
            if (!ReferenceEquals(retained, song)) retained.ApplyRepository(song.Raw, song.RepositoryPublication);
            return retained;
        }).ToArray();
        var retainedRows = rows.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!retainedRows.Contains(target[index])) target.RemoveAt(index);
        for (int index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (index < target.Count && ReferenceEquals(target[index], row)) continue;
            int previous = -1;
            for (int position = index + 1; position < target.Count; position++)
                if (ReferenceEquals(target[position], row)) { previous = position; break; }
            if (previous >= 0) target.Move(previous, index);
            else target.Insert(index, row);
        }
    }
}

/// <summary>Read-only accessors for the lean song wire shape (sources[0] is the primary recording).</summary>
internal static class SongJson
{
    internal static JsonElement PrimaryInfo(JsonElement song) =>
        song.ValueKind == JsonValueKind.Object && song.TryGetProperty("sources", out var sources) &&
        sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0 &&
        sources[0].ValueKind == JsonValueKind.Object && sources[0].TryGetProperty("musicInfo", out var info) &&
        info.ValueKind == JsonValueKind.Object ? info : default;
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
        decimal parsed = 0;
        Require(field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out parsed) &&
            parsed is >= 0 and <= 9007199254740991 && decimal.Truncate(parsed) == parsed);
        return (long)parsed;
    }
    internal static int Count(JsonElement value, string key)
    {
        var number = Integer(value, key);
        Require(number <= int.MaxValue);
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
