using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed class MusicLibraryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class LJ
{
    internal const long MaxInteger = 9007199254740991;
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 32 };
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string code = "INVALID_REQUEST")
    { if (!value) throw new MusicLibraryException(code); }
    internal static JsonObject Obj(params (string Key, object? Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (key, value) in fields) result[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, Json);
        return result;
    }
    internal static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.Select(n => n?.DeepClone()).ToArray());
    internal static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => JsonValue.Create(s) as JsonNode).ToArray());
    internal static JsonObject Object(JsonNode? n, string code = "INVALID_REQUEST") { Require(n is JsonObject, code); return (JsonObject)n!; }
    internal static JsonArray Array(JsonNode? n, string code = "INVALID_REQUEST") { Require(n is JsonArray, code); return (JsonArray)n!; }
    internal static string S(JsonNode? n, string fallback = "") => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    internal static string Scalar(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString(Json) ?? "";
    internal static double N(JsonNode? n, double fallback = 0) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d :
        double.TryParse(Scalar(n), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
    internal static long I(JsonNode? n, long min = 0, long max = MaxInteger)
    { double d = N(n, double.NaN); Require(n is JsonValue && !((JsonValue)n).TryGetValue<string>(out _) && double.IsFinite(d) && Math.Truncate(d) == d && d >= min && d <= max); return (long)d; }
    internal static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    internal static bool IsBool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out _);
    internal static bool Id(string s, int max = 2048) => s.Length > 0 && s.Length <= max && s.Trim() == s && !s.Any(c => c < 32 || c == 127);
    internal static string Text(JsonNode? n, int max = 2048)
    { string s = S(n); Require(Id(s, max)); return s; }
    internal static void Fields(JsonObject o, string allowed, string required = "")
    {
        var fields = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        Require(o.All(p => fields.Contains(p.Key)) && required.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(o.ContainsKey));
    }
    // Persisted identity digests must match JSON.stringify, not a .NET encoder's
    // escaping of non-BMP/U+2028 characters or exponent formatting.
    internal static string Stringify(JsonNode? node)
    {
        var text = new StringBuilder();
        void Quote(string value)
        {
            text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                            text.Append(c).Append(value[++i]);
                        else if (c < 32 || char.IsSurrogate(c)) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
        void Write(JsonNode? value)
        {
            if (value is null) { text.Append("null"); return; }
            if (value is JsonObject obj)
            {
                text.Append('{'); bool first = true;
                // ECMAScript enumerates array-index property names before ordinary keys.
                foreach (var p in obj.OrderBy(p => ArrayIndex(p.Key) is null ? 1 : 0).ThenBy(p => ArrayIndex(p.Key) ?? 0))
                { if (!first) text.Append(','); first = false; Quote(p.Key); text.Append(':'); Write(p.Value); }
                text.Append('}'); return;
            }
            if (value is JsonArray array)
            {
                text.Append('[');
                for (int i = 0; i < array.Count; i++) { if (i > 0) text.Append(','); Write(array[i]); }
                text.Append(']'); return;
            }
            var scalar = (JsonValue)value;
            if (scalar.TryGetValue<string>(out string? s)) Quote(s);
            else if (scalar.TryGetValue<bool>(out bool b)) text.Append(b ? "true" : "false");
            else text.Append(JsNumber(N(value, double.NaN)));
        }
        Write(node); return text.ToString();
    }
    private static uint? ArrayIndex(string key) =>
        uint.TryParse(key, out uint n) && n < uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == key ? n : null;
    private static string JsNumber(double number)
    {
        Require(double.IsFinite(number)); if (number == 0) return "0";
        string sign = number < 0 ? "-" : ""; double magnitude = Math.Abs(number);
        string raw = magnitude.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        int e = raw.IndexOf('e'); int exponent = e < 0 ? 0 : int.Parse(raw[(e + 1)..], CultureInfo.InvariantCulture);
        string mantissa = e < 0 ? raw : raw[..e]; int dot = mantissa.IndexOf('.');
        int point = (dot < 0 ? mantissa.Length : dot) + exponent;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        if (magnitude >= 1e-6 && magnitude < 1e21)
            return sign + (point <= 0 ? "0." + new string('0', -point) + digits :
                point >= digits.Length ? digits + new string('0', point - digits.Length) : digits.Insert(point, "."));
        return sign + mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
    internal static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(Stringify(node));
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Digest(JsonNode node) => Hash(Bytes(Stable(node)!));
    private static JsonNode? Stable(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => uint.TryParse(p.Key, out uint n) && n < uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == p.Key ? 0 : 1)
            .ThenBy(p => uint.TryParse(p.Key, out uint n) && n < uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == p.Key ? n : 0)
            .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Stable(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Stable).ToArray()),
        _ => node?.DeepClone()
    };
    internal static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, Json);
    internal static JsonNode Parse(ReadOnlyMemory<byte> bytes, int maximum = 8 * 1024 * 1024)
    {
        Require(bytes.Length > 0 && bytes.Length <= maximum, "LIMIT");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            int budget = 250000;
            void Check(JsonElement e)
            {
                Require(--budget >= 0, "LIMIT");
                if (e.ValueKind == JsonValueKind.Object)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject()) { Require(p.Name.Length <= 8192 && seen.Add(p.Name), "CORRUPT_STORAGE"); Check(p.Value); }
                }
                else if (e.ValueKind == JsonValueKind.Array) foreach (var child in e.EnumerateArray()) Check(child);
                else if (e.ValueKind == JsonValueKind.String) Require(e.GetString()!.Length <= 512 * 1024, "LIMIT");
                else if (e.ValueKind == JsonValueKind.Number) { var d = e.GetDouble(); Require(double.IsFinite(d) && (Math.Truncate(d) != d || Math.Abs(d) <= MaxInteger)); }
            }
            Check(doc.RootElement);
            return JsonNode.Parse(doc.RootElement.GetRawText()) ?? throw new MusicLibraryException("CORRUPT_STORAGE");
        }
        catch (MusicLibraryException) { throw; }
        catch { throw new MusicLibraryException("CORRUPT_STORAGE"); }
    }
    internal static JsonObject PersistedDocument(ReadOnlyMemory<byte> bytes)
    {
        var document = Object(Parse(bytes), "CORRUPT_STORAGE");
        byte[] canonical = Bytes(document);
        try
        {
            Require(bytes.Span.SequenceEqual(canonical), "CORRUPT_STORAGE");
            return document;
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }
    internal static JsonObject Parameters(object? value, int max = 512 * 1024)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value ?? new { }, Json);
        return Object(Parse(bytes, max));
    }
    internal static JsonObject Page(IEnumerable<JsonNode?> values, JsonObject p, string field = "items")
    {
        long offset = p["offset"] is null ? 0 : I(p["offset"]);
        int limit = p["limit"] is null ? 50 : (int)I(p["limit"], 1, 100);
        var all = values.ToArray();
        return Obj((field, Arr(offset >= all.Length ? [] : all.Skip((int)offset).Take(limit))), ("total", all.Length), ("offset", offset), ("limit", limit));
    }
    internal static JsonObject Merge(JsonObject a, JsonObject b) { foreach (var p in b) a[p.Key] = p.Value?.DeepClone(); return a; }
}
