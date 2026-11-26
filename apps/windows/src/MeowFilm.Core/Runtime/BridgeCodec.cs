using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowFilm.Core.Runtime;

public static class BridgeCodec
{
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions Serializer = new()
    {
        MaxDepth = 67,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new RawElementConverter() }
    };

    private sealed class RawElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
    }

    public static byte[] EncodeJSONValue(object? value)
    {
        try { return JsonSerializer.SerializeToUtf8Bytes(value, Serializer); }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    public static JsonElement Value(object? value) => DecodeJSONValue(EncodeJSONValue(value));

    public static JsonElement RequiredProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result)
            ? result
            : throw new BridgeException("INVALID_MESSAGE");

    public static JsonElement DecodeJSONValue(ReadOnlyMemory<byte> body) => ParseJSON(body);

    internal static JsonElement ParseJSON(ReadOnlyMemory<byte> body)
    {
        try
        {
            if (body.Span.StartsWith("\uFEFF"u8)) body = body[3..];
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 67 });
            return document.RootElement.Clone();
        }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }
}
