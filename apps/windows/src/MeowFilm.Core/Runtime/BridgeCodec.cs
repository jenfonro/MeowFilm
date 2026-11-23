using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeowFilm.Core.Runtime;

public static class BridgeCodec
{
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Serializer = new()
    {
        MaxDepth = 67, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new StrictStringConverter(), new StrictCharConverter(), new StrictNodeConverter(), new RawElementConverter() }
    };

    // System.Text.Json otherwise replaces invalid UTF-16 while writing strings.
    // Validating the already-serialized JSON is too late to detect that loss.
    private sealed class StrictStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.GetString()!;
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            _ = StrictUtf8.GetByteCount(value);
            writer.WriteStringValue(value);
        }
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            _ = StrictUtf8.GetByteCount(value);
            writer.WritePropertyName(value);
        }
    }

    private sealed class StrictCharConverter : JsonConverter<char>
    {
        public override char Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.GetString()![0];
        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            if (char.IsSurrogate(value)) Invalid();
            writer.WriteStringValue(value.ToString());
        }
    }

    private sealed class RawElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
    }

    // JsonValue's primitive writer bypasses the ordinary string converter.
    // Walk nodes explicitly so it cannot replace an invalid surrogate first.
    private sealed class StrictNodeConverter : JsonConverter<JsonNode>
    {
        public override bool CanConvert(Type type) => typeof(JsonNode).IsAssignableFrom(type);
        public override JsonNode? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, JsonNode value, JsonSerializerOptions options) =>
            WriteNode(writer, value, options);

        private static void WriteNode(Utf8JsonWriter writer, JsonNode? node, JsonSerializerOptions options)
        {
            if (writer.CurrentDepth > 64) Invalid();
            switch (node)
            {
                case null: writer.WriteNullValue(); break;
                case JsonObject obj:
                    writer.WriteStartObject();
                    foreach (var (key, value) in obj)
                    {
                        _ = StrictUtf8.GetByteCount(key);
                        writer.WritePropertyName(key);
                        WriteNode(writer, value, options);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonArray array:
                    writer.WriteStartArray();
                    foreach (var item in array) WriteNode(writer, item, options);
                    writer.WriteEndArray();
                    break;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    _ = StrictUtf8.GetByteCount(text);
                    writer.WriteStringValue(text);
                    break;
                case JsonValue value when value.TryGetValue<char>(out var character):
                    if (char.IsSurrogate(character)) Invalid();
                    writer.WriteStringValue(character.ToString());
                    break;
                default: node.WriteTo(writer, options); break;
            }
        }
    }

    public static byte[] EncodeJSONValue(object? value)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Serializer);
            using var document = ParseDocument(bytes);
            ValidateDepth(document.RootElement, 0);
            return bytes;
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    public static JsonElement Value(object? value)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Serializer);
            return DecodeJSONValue(bytes);
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    // Only consumed identity/control fields require an unambiguous member.
    // Ordinary extension members remain raw JSON, including repeated names.
    public static JsonElement RequiredProperty(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        bool found = false;
        JsonElement result = default;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) Invalid();
            found = true;
            result = property.Value;
        }
        if (!found) Invalid();
        return result;
    }

    public static JsonElement DecodeJSONValue(ReadOnlyMemory<byte> body)
    {
        var value = ParseJSON(body);
        ValidateJSONValue(value);
        return value;
    }

    // HTTP validates the outer envelope separately: it does not consume one
    // of the business value's 64 levels.
    internal static JsonElement ParseJSON(ReadOnlyMemory<byte> body)
    {
        using var document = ParseDocument(body);
        return document.RootElement.Clone();
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> body)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(body.Span);
            if (body.Span.StartsWith("\uFEFF"u8)) body = body[3..];
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 67 });
        }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    internal static void ValidateJSONValue(JsonElement value)
    {
        try { ValidateDepth(value, 0); }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    private static void ValidateDepth(JsonElement value, int depth)
    {
        if (depth > 64) Invalid();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    ValidateDepth(property.Value, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateDepth(item, depth + 1);
                break;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                break;
            default: Invalid(); break;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new BridgeException("INVALID_MESSAGE");

}
