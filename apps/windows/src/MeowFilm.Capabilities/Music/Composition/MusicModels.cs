using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed record PlaylistRow(string Id, string Name, string Kind, long Revision, int Count,
    string Platform = "", string RemoteId = "", string Cover = "")
{
    public string CountText => $"{Count} 首";
    public string Route => Kind == "network" ? "catalog-playlist/" + Platform + "/" + Uri.EscapeDataString(RemoteId)
        : Kind == "favorites" ? "favorites" : "playlist/" + Id;

    internal static PlaylistRow FromSummary(MusicCollectionSummaryRecord value)
    {
        MusicDto.Require(value.Kind is "favorites" or "playlist" or "network");
        if (value.Kind == "network")
            MusicDto.Require(value.Id == MusicLibraryOwner.NetworkPlaylistId(value.Platform, value.RemoteId));
        return new(value.Id, value.Name, value.Kind, value.Revision, value.Count, value.Platform, value.RemoteId, value.Cover);
    }
}

public sealed record SongRow(MusicSongRecord Record, string? EntryId = null) : System.ComponentModel.INotifyPropertyChanged
{
    public MusicSongRecord Record { get; private set; } = Record;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal long RepositoryPublication { get; private set; } = -1;

    internal bool ApplyRepository(MusicSongRecord? value, long publication = -1)
    {
        if (value is not { } next || publication >= 0 && publication < RepositoryPublication) return false;
        if (publication >= 0) RepositoryPublication = publication;
        if (Record.ContentEquals(next)) return false;
        var before = (Title, Artist, Album, Cover, Duration, IsOnline);
        Record = next;
        PropertyChanged?.Invoke(this, new(nameof(Record)));
        if (before.Title != Title) PropertyChanged?.Invoke(this, new(nameof(Title)));
        if (before.Artist != Artist) PropertyChanged?.Invoke(this, new(nameof(Artist)));
        if (before.Album != Album) PropertyChanged?.Invoke(this, new(nameof(Album)));
        if (before.Cover != Cover) PropertyChanged?.Invoke(this, new(nameof(Cover)));
        if (before.Duration != Duration) PropertyChanged?.Invoke(this, new(nameof(Duration)));
        if (before.IsOnline != IsOnline) PropertyChanged?.Invoke(this, new(nameof(IsOnline)));
        return true;
    }

    public string Id => Record.SongId;
    public string Title => Record.Title;
    public string Artist => Record.Artist;
    public string Album => Record.Album;
    public string Cover => Record.CoverUrl;
    public bool IsOnline => Record.Sources.Length > 0;
    public string Duration => Record.Duration is { } seconds && double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
        ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "—";
    public string SelectionKey => EntryId ?? Id;

    internal static SongRow FromRecord(MusicSongRecord record, string? entryId = null) =>
        new(NativeMusicSong.Normalize(record), entryId);

    internal static SongRow Read(JsonElement value, string? entryId = null)
    {
        try
        {
            var normalized = NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(value));
            return new(normalized, entryId);
        }
        catch (MusicOperationException) { throw; }
        catch { throw new MusicOperationException("INVALID_RESPONSE"); }
    }

    internal static SongRow? TryReadCatalog(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || MusicCatalogDto.Text(value, "songId").Length == 0)
            return null;
        if (value.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            try { return new(NativeMusicSong.Normalize(MusicNodeSongCodec.ReadSong(value))); }
            catch (Exception error) when (error is MusicLibraryException or MusicOperationException or JsonException or FormatException or InvalidOperationException)
            {
                DebugLog.Warn(DebugCategories.MusicCatalog, "songRow", "tryReadCatalog.fallback", error: error);
            }
        }
        double? duration = value.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number && dEl.TryGetDouble(out var d)
            ? NativeMusicSong.ValidDuration(d) : null;
        return new(NativeMusicSong.Normalize(new MusicSongRecord(
            MusicCatalogDto.Text(value, "songId"),
            MusicCatalogDto.Text(value, "title"),
            MusicCatalogDto.Text(value, "artist"),
            MusicCatalogDto.Text(value, "album"),
            duration,
            duration,
            MusicCatalogDto.Text(value, "cover_url"),
            value.TryGetProperty("localFileId", out var lf) && lf.ValueKind == JsonValueKind.String ? lf.GetString() : null,
            null, null, [], [])));
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
    internal static string InfoId(JsonElement info) =>
        info.ValueKind == JsonValueKind.Object && info.TryGetProperty("songmid", out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind is JsonValueKind.Null ? "" : value.GetRawText()
            : "";
    internal static string NativeId(JsonElement song) => InfoId(PrimaryInfo(song));
    internal static string NativeId(MusicSongRecord song) => song.Sources.Length > 0 ? song.Sources[0].SongMid ?? "" : "";
}

internal sealed class MusicOperationException(string code, int retryAfterSeconds = 0) : Exception(code)
{
    public string Code { get; } = code;
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
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
}
