using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

internal static class StorageValueJson
{
    internal static StorageValue FromJsonNode(JsonNode? node)
    {
        if (node is null) return StorageValue.Null;
        if (node is JsonObject obj)
        {
            var fields = new List<StorageValueField>(obj.Count);
            foreach (var pair in obj)
                fields.Add(new StorageValueField(pair.Key, FromJsonNode(pair.Value)));
            return StorageValue.FromObject(fields);
        }
        if (node is JsonArray arr)
        {
            var items = new List<StorageValue>(arr.Count);
            foreach (var item in arr)
                items.Add(FromJsonNode(item));
            return StorageValue.FromArray(items);
        }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<JsonElement>(out var el))
                return FromJsonElement(el);
            if (v.TryGetValue<bool>(out var b))
                return StorageValue.FromBool(b);
            if (v.TryGetValue<string>(out var s))
                return StorageValue.FromText(s);
            if (v.TryGetValue<long>(out var l))
                return StorageValue.FromInt64(l);
            if (v.TryGetValue<int>(out var i))
                return StorageValue.FromInt64(i);
            if (v.TryGetValue<double>(out var d))
                return double.IsFinite(d) ? StorageValue.FromReal(d) : StorageValue.Null;
            if (v.TryGetValue<decimal>(out var m))
                return StorageValue.FromExactNumber(m.ToString(CultureInfo.InvariantCulture));
            return FromRawNumberText(v.ToJsonString());
        }
        return StorageValue.Null;
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
                return StorageValue.FromReal(d, raw);
            return StorageValue.FromExactNumber(raw);
        }
        return StorageValue.FromExactNumber(raw);
    }

    internal static JsonNode? ToJsonNode(StorageValue? value) => value?.Kind switch
    {
        null or StorageValueKind.Null => null,
        StorageValueKind.Boolean => JsonValue.Create(value.BoolValue),
        StorageValueKind.Integer => JsonValue.Create(value.IntValue),
        StorageValueKind.Real => JsonValue.Create(value.RealValue),
        StorageValueKind.ExactNumber => JsonNode.Parse(value.TextValue ?? "0"),
        StorageValueKind.Text => JsonValue.Create(value.TextValue),
        StorageValueKind.Array => new JsonArray((value.ArrayItems ?? []).Select(ToJsonNode).ToArray()),
        StorageValueKind.Object => BuildObject(value),
        _ => null
    };

    internal static JsonObject? ToJsonObject(StorageValue? value) =>
        value is { Kind: StorageValueKind.Object } ? BuildObject(value) : null;

    private static JsonObject BuildObject(StorageValue obj)
    {
        var result = new JsonObject();
        if (obj.ObjectFields is { } fields)
            foreach (var field in fields)
                result[field.Name] = ToJsonNode(field.Value);
        return result;
    }
}
