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

internal sealed record FilmMetadataSupplement(string MetadataId, string Kind, JsonElement Metadata)
{
    internal static FilmMetadataSupplement? Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string kind = FilmJson.Text(value, "kind");
        var metadata = FilmJson.Member(value, "metadata");
        if (FilmJson.Text(value, "provider") != "tmdb" || kind is not ("movie" or "tv") ||
            !FilmJson.IsIdentity(FilmJson.Member(value, "metadataId")) || metadata.ValueKind != JsonValueKind.Object)
            return null;
        return new(FilmJson.Text(value, "metadataId"), kind, metadata.Clone());
    }
}
