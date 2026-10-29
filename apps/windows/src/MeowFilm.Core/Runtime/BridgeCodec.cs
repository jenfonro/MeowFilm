using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MeowFilm.Core.Runtime;

public static partial class BridgeCodec
{
    public const int MaximumFrameBytes = 1024 * 1024;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Serializer = new()
    {
        MaxDepth = 67, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new StrictStringConverter(), new StrictCharConverter(), new StrictNodeConverter() }
    };

    // System.Text.Json otherwise replaces invalid UTF-16 while writing strings.
    // Validating the already-serialized JSON is too late to detect that loss.
    private sealed class StrictStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.GetString()!;
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (StrictUtf8.GetByteCount(value) > MaximumFrameBytes) throw new BridgeException("LIMIT");
            writer.WriteStringValue(value);
        }
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.Any(c => c > 127)) Invalid();
            if (value.Length > MaximumFrameBytes) throw new BridgeException("LIMIT");
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
                        if (key.Any(c => c > 127)) Invalid();
                        if (key.Length > MaximumFrameBytes) throw new BridgeException("LIMIT");
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
                    if (StrictUtf8.GetByteCount(text) > MaximumFrameBytes) throw new BridgeException("LIMIT");
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

    private sealed class BoundedBuffer(int maximum) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > maximum) throw new BridgeException("LIMIT");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maximum) throw new BridgeException("LIMIT");
            base.Write(buffer, offset, count);
        }
    }

    private static byte[] Serialize(object? value, int maximum)
    {
        try
        {
            using var buffer = new BoundedBuffer(maximum);
            JsonSerializer.Serialize(buffer, value, Serializer);
            return buffer.ToArray();
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9_.:-]{1,80}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();
    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();

    public static bool ValidSession(string? value) => value is not null && SessionPattern().IsMatch(value);
    public static bool ValidMethod(string? value) => value is not null && MethodPattern().IsMatch(value);

    public static JsonElement Value(object? value)
    {
        try
        {
            using var document = JsonDocument.Parse(Serialize(value, MaximumFrameBytes),
                new JsonDocumentOptions { MaxDepth = 67 });
            ValidateValue(document.RootElement, 0);
            return document.RootElement.Clone();
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    public static byte[] Encode(object message, int maximumBytes = MaximumFrameBytes)
    {
        if (maximumBytes is < 128 or > MaximumFrameBytes) throw new BridgeException("INVALID_MESSAGE");
        var body = Serialize(message, maximumBytes);
        _ = Decode(body);
        var result = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(result, body.Length);
        body.CopyTo(result, 4);
        return result;
    }

    public static JsonElement Decode(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > MaximumFrameBytes) throw new BridgeException("LIMIT");
        try
        {
            _ = StrictUtf8.GetCharCount(body.Span);
            // Match JS TextDecoder's default initial UTF-8 BOM handling. Size
            // was checked on the actual wire bytes, before discarding the BOM.
            if (body.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) body = body[3..];
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 67 });
            var message = document.RootElement;
            ValidateValue(message, 0);
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("version", out var version) || version.GetDouble() != 1
                || !message.TryGetProperty("session", out var session) || !ValidSession(session.GetString()!)
                || !message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                || !ValidId(id.GetDouble())
                || !message.TryGetProperty("kind", out _)) Invalid();
            var kind = message.GetProperty("kind");
            string[] fields = kind.GetString() switch
            {
                "request" => ["version", "session", "id", "kind", "method", "params"],
                "result" => ["version", "session", "id", "kind", "value"],
                "error" => ["version", "session", "id", "kind", "code"],
                "cancel" => ["version", "session", "id", "kind"],
                _ => throw new BridgeException("INVALID_MESSAGE")
            };
            if (message.EnumerateObject().Count() != fields.Length
                || fields.Any(field => !message.TryGetProperty(field, out _))) Invalid();
            if (kind.GetString() == "request" && !ValidMethod(message.GetProperty("method").GetString()!)) Invalid();
            if (kind.GetString() == "error" && !BridgeException.Codes.Contains(message.GetProperty("code").GetString()!)) Invalid();
            return message.Clone();
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    private static bool ValidId(double number) =>
        double.IsFinite(number) && number > 0 && number <= MaximumSafeInteger && Math.Truncate(number) == number;

    private static void ValidateValue(JsonElement value, int depth)
    {
        if (depth > 64) Invalid();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Any(c => c > 127) || !keys.Add(property.Name)) Invalid();
                    ValidateValue(property.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) ValidateValue(item, depth + 1);
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                for (var i = 0; i < text.Length; i++)
                {
                    if (char.IsHighSurrogate(text[i]))
                    {
                        if (++i >= text.Length || !char.IsLowSurrogate(text[i])) Invalid();
                    }
                    else if (char.IsLowSurrogate(text[i])) Invalid();
                }
                break;
            case JsonValueKind.Number:
                var number = value.GetDouble();
                if (!double.IsFinite(number)
                    || (Math.Truncate(number) == number && Math.Abs(number) > MaximumSafeInteger)) Invalid();
                break;
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                break;
            default: Invalid(); break;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new BridgeException("INVALID_MESSAGE");

    public static async Task<JsonElement> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaximumFrameBytes) throw new BridgeException("LIMIT");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return Decode(body);
    }
}
