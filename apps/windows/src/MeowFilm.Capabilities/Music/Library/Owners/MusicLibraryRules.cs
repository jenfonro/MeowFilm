using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
}
