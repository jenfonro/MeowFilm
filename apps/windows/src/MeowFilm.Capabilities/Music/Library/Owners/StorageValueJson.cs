using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal static class StorageValueJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static JsonElement ToJsonElement(StorageValue? value)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteJson(writer, value);
            writer.Flush();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    internal static string ToJsonString(StorageValue? value)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteJson(writer, value);
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static StorageValue FromJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => StorageValue.Null,
        JsonValueKind.True => StorageValue.True,
        JsonValueKind.False => StorageValue.False,
        JsonValueKind.String => StorageValue.FromText(el.GetString()!),
        JsonValueKind.Number => FromRawNumberText(el.GetRawText()),
        JsonValueKind.Array => StorageValue.FromArray(el.EnumerateArray().Select(FromJsonElement).ToArray()),
        JsonValueKind.Object => StorageValue.FromObject(el.EnumerateObject()
            .Select(prop => new StorageValueField(prop.Name, FromJsonElement(prop.Value))).ToArray()),
        _ => StorageValue.Null
    };

    private static StorageValue FromRawNumberText(string raw)
    {
        if (raw == "-0")
            return StorageValue.FromExactNumber("-0");
        if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) &&
            l.ToString(CultureInfo.InvariantCulture) == raw)
            return StorageValue.FromInt64(l);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                return StorageValue.FromExactNumber(raw);
            if (d.ToString("R", CultureInfo.InvariantCulture) == raw ||
                d.ToString("G17", CultureInfo.InvariantCulture) == raw)
                return StorageValue.FromReal(d);
            return StorageValue.FromExactNumber(raw);
        }
        return StorageValue.FromExactNumber(raw);
    }

    internal static void WriteJson(Utf8JsonWriter writer, StorageValue? value)
    {
        switch (value?.Kind)
        {
            case null or StorageValueKind.Null:
                writer.WriteNullValue();
                break;
            case StorageValueKind.Boolean:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case StorageValueKind.Integer:
                writer.WriteNumberValue(value.IntValue);
                break;
            case StorageValueKind.Real:
                writer.WriteNumberValue(value.RealValue);
                break;
            case StorageValueKind.ExactNumber:
                writer.WriteRawValue(value.TextValue ?? "0", skipInputValidation: false);
                break;
            case StorageValueKind.Text:
                writer.WriteStringValue(value.TextValue ?? "");
                break;
            case StorageValueKind.Array:
                writer.WriteStartArray();
                if (value.ArrayItems is { } items)
                    foreach (var item in items)
                        WriteJson(writer, item);
                writer.WriteEndArray();
                break;
            case StorageValueKind.Object:
                writer.WriteStartObject();
                if (value.ObjectFields is { } fields)
                    foreach (var field in fields)
                    {
                        writer.WritePropertyName(field.Name);
                        WriteJson(writer, field.Value);
                    }
                writer.WriteEndObject();
                break;
        }
    }

    internal static string? ScalarText(StorageValue? value) => value?.Kind switch
    {
        StorageValueKind.Text => value.TextValue,
        StorageValueKind.Integer => value.IntValue.ToString(CultureInfo.InvariantCulture),
        StorageValueKind.Real => MusicRules.JsNumber(value.RealValue),
        StorageValueKind.ExactNumber => value.TextValue,
        StorageValueKind.Boolean => value.BoolValue ? "true" : "false",
        _ => null
    };

    internal static StorageValue? Field(StorageValue? obj, string name)
    {
        if (obj is not { Kind: StorageValueKind.Object, ObjectFields: { } fields }) return null;
        foreach (var field in fields)
            if (field.Name == name) return field.Value;
        return null;
    }
}
