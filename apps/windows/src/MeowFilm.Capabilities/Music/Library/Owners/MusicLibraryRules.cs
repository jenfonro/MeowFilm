using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed class MusicLibraryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class MusicRules
{
    internal const long MaxInteger = 9_007_199_254_740_991L;

    internal static void Require([DoesNotReturnIf(false)] bool condition, string code = "INVALID_REQUEST")
    {
        if (!condition) throw new MusicLibraryException(code);
    }

    internal static bool IsValidId(string value, int? max = 2048) =>
        value.Length > 0 && (max is null || value.Length <= max) &&
        value.Trim() == value && !value.Any(c => c < 32 || c == 127);

    internal static string RequireId(string value, int? max = 2048, string code = "INVALID_REQUEST")
    {
        Require(IsValidId(value, max), code);
        return value;
    }

    internal static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string HashUtf8(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string HashText(string canonical) =>
        HashUtf8(Encoding.UTF8.GetBytes(canonical));

    internal static string CanonicalArray(params string[] items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendQuoted(sb, items[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static string CanonicalArray(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendQuoted(sb, items[i]);
        }
        sb.Append(']');
        return sb.ToString();
    }

    internal static string CanonicalElement(JsonElement element)
    {
        var sb = new StringBuilder();
        AppendElement(sb, element);
        return sb.ToString();
    }

    internal static string CanonicalDigest(JsonElement element) =>
        HashText(CanonicalElement(element));

    internal static string CanonicalDigest(StorageValue value)
    {
        var sb = new StringBuilder();
        AppendStorageValue(sb, value);
        return HashText(sb.ToString());
    }

    internal static string CanonicalTupleHash(params string[] items) =>
        HashText(CanonicalArray(items));

    internal static void AppendQuoted(StringBuilder text, string value)
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
                    else if (c < 32 || char.IsSurrogate(c))
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }

    internal static string JsNumber(double number)
    {
        Require(double.IsFinite(number));
        if (number == 0) return "0";
        string sign = number < 0 ? "-" : "";
        double magnitude = Math.Abs(number);
        string raw = magnitude.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        int e = raw.IndexOf('e');
        int exponent = e < 0 ? 0 : int.Parse(raw[(e + 1)..], CultureInfo.InvariantCulture);
        string mantissa = e < 0 ? raw : raw[..e];
        int dot = mantissa.IndexOf('.');
        int point = (dot < 0 ? mantissa.Length : dot) + exponent;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        if (magnitude >= 1e-6 && magnitude < 1e21)
            return sign + (point <= 0 ? "0." + new string('0', -point) + digits :
                point >= digits.Length ? digits + new string('0', point - digits.Length) : digits.Insert(point, "."));
        return sign + mantissa + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static uint? ArrayIndex(string key) =>
        uint.TryParse(key, out uint n) && n < uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == key ? n : null;

    internal static void AppendElement(StringBuilder sb, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                sb.Append("null");
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.String:
                AppendQuoted(sb, el.GetString()!);
                break;
            case JsonValueKind.Number:
                sb.Append(JsNumber(el.GetDouble()));
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                int idx = 0;
                foreach (var item in el.EnumerateArray())
                {
                    if (idx++ > 0) sb.Append(',');
                    AppendElement(sb, item);
                }
                sb.Append(']');
                break;
            case JsonValueKind.Object:
                sb.Append('{');
                var props = el.EnumerateObject()
                    .OrderBy(p => ArrayIndex(p.Name) is null ? 1 : 0)
                    .ThenBy(p => ArrayIndex(p.Name) ?? 0)
                    .ThenBy(p => p.Name, StringComparer.Ordinal);
                bool first = true;
                foreach (var p in props)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendQuoted(sb, p.Name);
                    sb.Append(':');
                    AppendElement(sb, p.Value);
                }
                sb.Append('}');
                break;
        }
    }

    internal static void AppendStorageValue(StringBuilder sb, StorageValue? value)
    {
        switch (value?.Kind)
        {
            case null or StorageValueKind.Null:
                sb.Append("null");
                break;
            case StorageValueKind.Boolean:
                sb.Append(value.BoolValue ? "true" : "false");
                break;
            case StorageValueKind.Integer:
                sb.Append(value.IntValue.ToString(CultureInfo.InvariantCulture));
                break;
            case StorageValueKind.Real:
                sb.Append(JsNumber(value.RealValue));
                break;
            case StorageValueKind.ExactNumber:
                sb.Append(double.TryParse(value.TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                    ? JsNumber(d)
                    : value.TextValue ?? "0");
                break;
            case StorageValueKind.Text:
                AppendQuoted(sb, value.TextValue ?? "");
                break;
            case StorageValueKind.Array:
                sb.Append('[');
                var items = value.ArrayItems ?? [];
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendStorageValue(sb, items[i]);
                }
                sb.Append(']');
                break;
            case StorageValueKind.Object:
                sb.Append('{');
                var fields = (value.ObjectFields ?? [])
                    .OrderBy(p => ArrayIndex(p.Name) is null ? 1 : 0)
                    .ThenBy(p => ArrayIndex(p.Name) ?? 0)
                    .ThenBy(p => p.Name, StringComparer.Ordinal);
                bool first = true;
                foreach (var f in fields)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendQuoted(sb, f.Name);
                    sb.Append(':');
                    AppendStorageValue(sb, f.Value);
                }
                sb.Append('}');
                break;
        }
    }
}
