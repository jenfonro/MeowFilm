// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities.Platforms;

internal static class PJ
{
    internal static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static void Require(bool condition, string code = "INVALID_REQUEST")
    {
        if (!condition) throw new MusicPlatformException(code);
    }
    internal static JsonNode? At(JsonNode? node, params string[] path)
    {
        foreach (var key in path) node = node is JsonObject o ? o[key] : null;
        return node;
    }
    internal static JsonNode? First(params JsonNode?[] nodes) => nodes.FirstOrDefault(n => n is not null);
    internal static string Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    internal static string S(JsonNode? node, params string[] path) => Text(At(node, path));
    internal static string Scalar(JsonNode? node) => node is JsonValue ? Text(node) is { Length: > 0 } s ? s : node.ToJsonString(Options) : "";
    internal static string Input(string? value, int max = 200)
    {
        string text = InputAll(value);
        Require(text.Length <= max);
        return text;
    }
    internal static string InputAll(string? value)
    {
        Require(!string.IsNullOrEmpty(value) && !value.Any(char.IsControl));
        try { _ = new UTF8Encoding(false, true).GetBytes(value!); }
        catch (EncoderFallbackException) { throw new MusicPlatformException("INVALID_REQUEST"); }
        return value!;
    }
    internal static string Metadata(JsonNode? value)
    {
        var text = Text(value);
        var end = Math.Min(text.Length, 4096);
        if (end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        return text[..end];
    }
    internal static string Id(JsonNode? value, string pattern = @"^[0-9]{1,20}$", string code = "INVALID_RESPONSE")
    {
        var s = Scalar(value);
        Require(Regex.IsMatch(s, pattern, RegexOptions.CultureInvariant), code);
        return s;
    }
    internal static string IdInput(string value, string pattern = @"^[1-9][0-9]{0,19}$")
        => Id(JsonValue.Create(Input(value)), pattern, "INVALID_REQUEST");
    internal static long Count(JsonNode? value)
    {
        Require(long.TryParse(Scalar(value), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n is >= 0 and <= 9007199254740991, "INVALID_RESPONSE");
        return n;
    }
    internal static long? OptionalCount(JsonNode? value) => value is null ? null : Count(value);
    internal static double Number(JsonNode? value, double fallback = 0)
    {
        if (value is null) return fallback;
        Require(double.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.IsFinite(n) && n >= 0, "INVALID_RESPONSE");
        return n;
    }
    internal static bool Bool(JsonNode? value)
    {
        Require(value is JsonValue v && v.TryGetValue<bool>(out _), "INVALID_RESPONSE");
        return value!.GetValue<bool>();
    }
    internal static bool True(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    internal static List<JsonObject> Rows(JsonNode? value, int max = 100)
    {
        var rows = RowsAll(value);
        Require(rows.Count <= max, "INVALID_RESPONSE");
        return rows;
    }
    internal static List<JsonObject> RowsAll(JsonNode? value)
    {
        Require(value is JsonArray a && a.All(v => v is JsonObject), "INVALID_RESPONSE");
        return ((JsonArray)value!).Cast<JsonObject>().ToList();
    }
    internal static IEnumerable<JsonNode?> Array(JsonNode? value) => value is JsonArray a ? a : [];
    internal static JsonNode? Node(object? value) => value switch
    {
        null => null, JsonNode n => n.DeepClone(),
        JsonElement e => JsonNode.Parse(e.GetRawText()), _ => JsonSerializer.SerializeToNode(value, Options)
    };
    internal static JsonObject Obj(params (string Key, object? Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Key, Node(f.Value))));
    internal static JsonArray Arr(IEnumerable<JsonNode?> values) => new(values.Select(n => n?.DeepClone()).ToArray());
    internal static string Encode(JsonNode? node) => node?.ToJsonString(Options) ?? "null";
    internal static string Hash(JsonNode? value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(value))));
    internal static JsonObject Parse(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(text);
            return Read(document.RootElement) as JsonObject ?? throw new MusicPlatformException("INVALID_RESPONSE");
        }
        catch (MusicPlatformException) { throw; }
        catch (Exception e) when (e is JsonException or ArgumentException or DecoderFallbackException)
        { throw new MusicPlatformException("INVALID_RESPONSE"); }
    }
    internal static JsonNode? Read(JsonElement value)
    {
        // JsonObject's lazy materialization rejects duplicate names. Materialize
        // provider data with normal last-property-wins semantics instead.
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in value.EnumerateObject())
                result[property.Name] = Read(property.Value);
            return result;
        }
        if (value.ValueKind == JsonValueKind.Array)
            return new JsonArray(value.EnumerateArray().Select(Read).ToArray());
        return value.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(value.Clone());
    }
    internal static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, Options);
    internal static string Image(JsonNode? value)
    {
        var s = Text(value).Replace("{size}", "480", StringComparison.Ordinal);
        if (s.Length is 0 or > 4096 || !Uri.TryCreate(s, UriKind.Absolute, out var u) ||
            u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0 ||
            u.IsLoopback || u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return "";
        if (u.Scheme == "http" && u.IsDefaultPort && Regex.IsMatch(u.Host,
            @"(?:^|\.)music\.(?:126|163)\.net$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            u = new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri;
        if (u.Query.Length > 0 && u.Fragment.Length == 0 && u.IsDefaultPort &&
            Regex.IsMatch(u.Host, @"^p[1-4]\.music\.(?:126|163)\.net$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var recognized = true;
            foreach (var part in u.Query[1..].Split('&'))
            {
                var pair = part.Split('=', 2);
                var key = pair[0];
                var content = pair.Length == 2 ? pair[1] : "";
                if (!keys.Add(key) || !(key switch
                {
                    "imageView" => content is "" or "1",
                    "type" => content is "webp" or "jpg" or "jpeg" or "png",
                    "thumbnail" => Regex.IsMatch(content,
                        @"^[1-9][0-9]{0,3}y[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant),
                    "param" => Regex.IsMatch(content,
                        @"^[1-9][0-9]{0,3}y[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant),
                    _ => false
                }))
                {
                    recognized = false;
                    break;
                }
            }
            if (recognized && (keys.Contains("imageView") || keys.SetEquals(["param"])))
                u = new Uri(u.GetLeftPart(UriPartial.Path), UriKind.Absolute);
        }
        return u.AbsoluteUri;
    }
    internal static void PageInput(int page, int size) => Require(page >= 1 && size is >= 1 and <= 100);
    internal static JsonObject Page(string platform, IEnumerable<JsonNode?> values, int page, int limit, long total, bool more)
    {
        var items = Arr(values);
        Require(items.Count <= limit, "INVALID_RESPONSE");
        var start = (page - 1L) * limit;
        Require(items.Count > 0 || start >= total, "INCOMPLETE_PAGE");
        Require(start + items.Count <= total || (start >= total && items.Count == 0), "INVALID_RESPONSE");
        if (more)
        {
            Require(items.Count > 0, "PAGINATION_STALLED");
            Require(start + items.Count < total, "INVALID_RESPONSE");
        }
        else Require(start + items.Count >= total, "INCOMPLETE_PAGE");
        return Obj(("platform", platform), ("items", items), ("page", page), ("limit", limit),
            ("total", total), ("more", more), ("continuation", more ? Obj(("page", checked(page + 1))) : null));
    }
    internal static JsonObject SearchPage(string platform, IEnumerable<JsonNode?> values, int page, int limit, long total,
        bool declaredMore, long? next = null, long? offset = null, bool shortPageEnds = true)
    {
        var items = Arr(values);
        Require(items.Count <= limit, "INVALID_RESPONSE");
        long nextPage = next ?? page + 1L;
        // A short page ends search only without an explicit cursor; an empty page always ends it.
        bool more = declaredMore && items.Count > 0 && (items.Count == limit || !shortPageEnds) && nextPage <= int.MaxValue;
        if (more) Require(nextPage > page, "PAGINATION_STALLED");
        var result = Obj(("platform", platform), ("items", items), ("page", page), ("limit", limit),
            ("total", total), ("more", more), ("continuation", more ? Obj(("page", nextPage)) : null));
        if (offset is not null) result["offset"] = offset;
        return result;
    }
    internal static Dictionary<string, string> Cookies(MusicPlatformSession? session)
        => CookieHeader(session?.Cookie);
    internal static Dictionary<string, string> CookieHeader(string? header)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (header is null) return result;
        Require(header.Length is > 0 and <= 16384 && header.All(c => c is >= ' ' and <= '~'), "INVALID_CREDENTIALS");
        foreach (var part in header.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var at = part.IndexOf('=');
            Require(at > 0, "INVALID_CREDENTIALS");
            var key = part[..at].Trim(); var value = part[(at + 1)..].Trim();
            Require(Regex.IsMatch(key, @"^[A-Za-z0-9_.$-]+$") && result.TryAdd(key, value) && result.Count <= 64 &&
                key.ToLowerInvariant() is not ("path" or "domain" or "expires" or "max-age" or "samesite" or "secure" or "httponly"),
                "INVALID_CREDENTIALS");
        }
        return result;
    }
    internal static string Cookie(Dictionary<string, string> cookies, string key, string fallback = "")
        => cookies.GetValueOrDefault(key, fallback);
}
