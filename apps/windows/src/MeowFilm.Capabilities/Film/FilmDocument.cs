using System.IO;
using System.Text.Json;

namespace MeowFilm.Capabilities;

// Patch a consumed projection, not the stored JSON. Unchanged values keep their
// original JsonElement, including extension keys and numbers outside CLR ranges.
internal static class FilmDocument
{
    internal static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    internal static JsonElement Set(JsonElement original, string name, JsonElement value) =>
        MeowFilm.Platform.Storage.SettingsDocument.SetMember(original, name, value);

    internal static JsonElement Merge(JsonElement original, JsonElement before, JsonElement after)
    {
        if (before.ValueKind == after.ValueKind &&
            (before.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(before, after))) return original;
        if (original.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object) return after;
        return Write(writer =>
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in original.EnumerateObject())
            {
                var old = FilmJson.Member(before, property.Name);
                var next = FilmJson.Member(after, property.Name);
                if (next.ValueKind == JsonValueKind.Undefined && old.ValueKind != JsonValueKind.Undefined) continue;
                writer.WritePropertyName(property.Name);
                (next.ValueKind == JsonValueKind.Undefined ? property.Value : Merge(property.Value, old, next)).WriteTo(writer);
                written.Add(property.Name);
            }
            foreach (var property in after.EnumerateObject())
                if (written.Add(property.Name)) property.WriteTo(writer);
            writer.WriteEndObject();
        });
    }
}
