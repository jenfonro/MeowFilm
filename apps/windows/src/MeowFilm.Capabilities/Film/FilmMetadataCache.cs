using System.Text.Json;

namespace MeowFilm.Capabilities;

internal sealed record FilmMetadataCache(string Status, string ErrorCode, JsonElement Components)
{
    internal static FilmMetadataCache? Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string status = FilmJson.Text(value, "status");
        var components = FilmJson.Member(value, "components");
        return new(status, FilmJson.Text(FilmJson.Member(value, "error"), "code"),
            components.ValueKind == JsonValueKind.Object ? components.Clone() : default);
    }
}
