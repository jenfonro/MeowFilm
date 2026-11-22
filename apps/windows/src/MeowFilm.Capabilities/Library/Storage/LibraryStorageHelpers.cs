using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Capabilities.Library;

internal static class LibraryStorageHelpers
{
    internal static string? Str(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<string>(out var s) ? s
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
            : null : null;

    internal static long? Int64(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<decimal>(out var m) && decimal.Truncate(m) == m ? (long)m
            : v.TryGetValue<double>(out var d) && double.IsFinite(d) && Math.Truncate(d) == d ? (long)d
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var en) ? en
            : null : null;

    internal static double? Dbl(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<long>(out var l) ? l
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<float>(out var f) ? f
            : v.TryGetValue<decimal>(out var m) ? (double)m
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var ed) ? ed
            : null : null;

    internal static bool? Bool(JsonObject? obj, string key) =>
        obj is not null && obj[key] is JsonValue v
            ? v.TryGetValue<bool>(out var b) ? b
            : v.TryGetValue<JsonElement>(out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean()
            : null : null;

    internal static JsonNode WholeOrDouble(double value) =>
        double.IsFinite(value) && Math.Truncate(value) == value && value is >= -9007199254740991 and <= 9007199254740991
            ? JsonValue.Create((long)value)!
            : JsonValue.Create(value)!;

    internal static void SetNonNull(JsonObject obj, string key, string? val) { if (val is not null) obj[key] = val; }
    internal static void SetNonNull(JsonObject obj, string key, long? val) { if (val is { } v) obj[key] = v; }
    internal static void SetNonNull(JsonObject obj, string key, double? val) { if (val is { } v) obj[key] = v; }
    internal static void SetNonNull(JsonObject obj, string key, bool? val) { if (val is { } v) obj[key] = v; }

    internal static JsonObject? SelectMembers(JsonObject? source, string[]? members)
    {
        if (source is null) return null;
        if (members is null) return source;
        var selected = new JsonObject();
        foreach (string member in members)
            if (source.ContainsKey(member))
                selected[member] = source[member]?.DeepClone();
        return selected;
    }

    internal static bool MatchesSubset(JsonObject? stored, JsonObject expected)
    {
        if (stored is null) return false;
        foreach (var pair in expected)
        {
            if (!stored.TryGetPropertyValue(pair.Key, out var actual)) return false;
            if (!JsonValuesEqual(actual, pair.Value)) return false;
        }
        return true;
    }

    internal static bool JsonValuesEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is JsonObject lo && right is JsonObject ro)
        {
            if (lo.Count != ro.Count) return false;
            foreach (var pair in lo)
                if (!ro.TryGetPropertyValue(pair.Key, out var rightProp) || !JsonValuesEqual(pair.Value, rightProp))
                    return false;
            return true;
        }
        if (left is JsonArray la && right is JsonArray ra)
        {
            if (la.Count != ra.Count) return false;
            for (int i = 0; i < la.Count; i++)
                if (!JsonValuesEqual(la[i], ra[i])) return false;
            return true;
        }
        if (left is JsonValue lv && right is JsonValue rv)
        {
            var lk = lv.GetValueKind();
            if (lk != rv.GetValueKind()) return false;
            return lk switch
            {
                JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False => true,
                JsonValueKind.String => string.Equals(ScalarString(lv), ScalarString(rv), StringComparison.Ordinal),
                JsonValueKind.Number => JsonNumbersEqual(lv, rv),
                _ => JsonNode.DeepEquals(lv, rv)
            };
        }
        return false;
    }

    private static string? ScalarString(JsonValue v) =>
        v.TryGetValue<string>(out var s) ? s
        : v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString()
        : null;

    private static bool JsonNumbersEqual(JsonValue left, JsonValue right)
    {
        if (TryGetDecimal(left, out var lm) && TryGetDecimal(right, out var rm))
            return lm == rm;
        if (TryGetDouble(left, out var ld) && TryGetDouble(right, out var rd))
            return ld == rd;
        return JsonNode.DeepEquals(left, right);
    }

    private static bool TryGetDecimal(JsonValue v, out decimal value)
    {
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<decimal>(out value)) return true;
        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetDecimal(out value) ||
                decimal.TryParse(el.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d))
            return decimal.TryParse(d.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(JsonValue v, out double value)
    {
        if (v.TryGetValue<double>(out value)) return double.IsFinite(value);
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<float>(out var f) && float.IsFinite(f)) { value = f; return true; }
        if (v.TryGetValue<decimal>(out var m)) { value = (double)m; return true; }
        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return double.IsFinite(value);
        value = 0;
        return false;
    }

    internal static int CommonPrefixLength<T>(IReadOnlyList<T> previous, IReadOnlyList<T> next, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        int limit = Math.Min(previous.Count, next.Count);
        int i = 0;
        while (i < limit && comparer.Equals(previous[i], next[i])) i++;
        return i;
    }

    internal static JsonObject ReadContext(SqliteDataReader row, int offset) =>
        MusicPlayContextRecord.ReadFrom(row, offset).ToJson();

    internal static (string Name, object? Value)[] ContextParams(JsonObject? ctx) =>
        MusicPlayContextRecord.FromJson(ctx).ToParams();

    internal static JsonObject? ReadQueueSource(SqliteDataReader row, int offset) =>
        MusicQueueSourceRecord.ReadFrom(row, offset)?.ToJson();

    internal static (string Name, object? Value)[] QueueSourceParams(string prefix, JsonObject? src) =>
        (MusicQueueSourceRecord.FromJson(src) ?? new MusicQueueSourceRecord(null, null, null, null, null, null, null, null, null, null, null, null)).ToParams(prefix);
}
