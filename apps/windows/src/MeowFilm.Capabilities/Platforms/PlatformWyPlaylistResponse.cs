using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Platforms;

internal static class PlatformWyPlaylistResponse
{
    private enum Position { Other, Root, Playlist, Tracks, Track }

    internal static JsonObject Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes));
            return Read(document.RootElement, Position.Root) as JsonObject ??
                throw new MusicPlatformException("INVALID_RESPONSE");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or DecoderFallbackException)
        { throw new MusicPlatformException("INVALID_RESPONSE"); }
    }

    private static JsonNode? Read(JsonElement value, Position position)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                // WY recommendation playlists repeat alg (null, then a string).
                // Playlist membership/display does not consume this reporting metadata.
                if (position == Position.Track && property.Name == "alg") continue;
                PJ.Require(!result.ContainsKey(property.Name), "INVALID_RESPONSE");
                var next = (position, property.Name) switch
                {
                    (Position.Root, "playlist") => Position.Playlist,
                    (Position.Playlist, "tracks") => Position.Tracks,
                    _ => Position.Other
                };
                result.Add(property.Name, Read(property.Value, next));
            }
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in value.EnumerateArray())
                result.Add(Read(item, position == Position.Tracks ? Position.Track : Position.Other));
            return result;
        }
        return value.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(value.Clone());
    }
}
