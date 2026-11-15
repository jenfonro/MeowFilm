using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
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
        data["songmid"] = nativeId;
        return NativeMusicSong.CreateOnline(platform, data, title ?? "Synthetic track " + nativeId,
            artist, album, duration, cover);
    }

    internal static JsonObject Local(string songId, string? title = null,
        string artist = "", string album = "", double duration = 0, string cover = "") =>
        NativeMusicSong.CreateLocal(songId, title ?? songId, artist, album, duration, cover);

    internal static JsonElement Element(JsonNode song) =>
        JsonSerializer.SerializeToElement(song);

    internal static async Task<JsonObject> AdoptAsync(MusicNativeApplication application, JsonObject song)
    {
        var platforms = song["sources"]!.AsArray().Select(source => source!["source"]!.GetValue<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        // All requested platforms are already present: register with the actual
        // application repository without a supplementary search or queue mutation.
        var result = await application.ExecuteAsync("music.songs.complete", new { song, platforms });
        if (!result.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("Song fixture admission: " + result.GetRawText());
        return JsonNode.Parse(result.GetProperty("song").GetRawText())!.AsObject();
    }

    internal static async Task<JsonObject> AdoptAsync(MusicLibraryOwner library, JsonObject song) =>
        JsonNode.Parse((await library.AdoptPlatformResultAsync(Element(song), default,
            shape: MusicLibraryOwner.PlatformSongShape.Song)).GetRawText())!.AsObject();

    internal static string NativeId(JsonElement song) =>
        Info(song).GetProperty("songmid").GetString()!;

    /// <summary>musicInfo of the primary (first) recording.</summary>
    internal static JsonElement Info(JsonElement song) =>
        song.GetProperty("sources")[0].GetProperty("musicInfo");
    internal static JsonObject Info(JsonNode song) =>
        song["sources"]![0]!["musicInfo"]!.AsObject();
}
