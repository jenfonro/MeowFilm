using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

/// <summary>
/// Test producer for the one current music-song contract. Tests that need an
/// invalid song must start from this shape and make the defect explicit.
/// </summary>
internal static class MusicSongFixture
{
    internal static JsonObject Online(string platform, string nativeId, string? title = null,
        string artist = "Synthetic artist", string album = "Synthetic album",
        double duration = 180, string cover = "", JsonObject? native = null)
    {
        var data = native is null ? new JsonObject() : (JsonObject)native.DeepClone();
        data["musicId"] = nativeId;
        data["songmid"] = nativeId;
        return NativeMusicSong.CreateOnline(platform, data, title ?? "Synthetic track " + nativeId,
            artist, album, duration, cover);
    }

    internal static JsonObject Local(string songId, string? title = null,
        string artist = "", string album = "", double duration = 0, string cover = "") =>
        NativeMusicSong.CreateLocal(songId, title ?? songId, artist, album, duration, cover);

    internal static JsonElement Element(JsonNode song) =>
        JsonSerializer.SerializeToElement(song);

    internal static string NativeId(JsonElement song) =>
        song.GetProperty("source_data").GetProperty("musicId").GetString()!;
}
