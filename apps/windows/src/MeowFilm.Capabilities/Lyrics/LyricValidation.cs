// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Lyrics;

internal static class L
{
    internal const int MaxResponse = 1024 * 1024, MaxSegment = 65536, MaxDecodedBytes = 256 * 1024;
    internal static void Require(bool condition, string code) { if (!condition) throw new MusicPlatformException(code); }
    internal static Regex Regex(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    internal static bool Match(string value, string pattern) => Regex(pattern.Replace("$", "\\z", StringComparison.Ordinal)).IsMatch(value);
    internal static bool Timed(string value) => Match(value, @"\[(?:[0-9]{1,3}:[0-9]{2}(?:\.[0-9]{1,3})?|[0-9]+,[0-9]+)\]");
    internal static void Validate(NativeLyricRequest? request)
    {
        Require(request is not null && request.Platform is "wy" or "tx" or "kg", "INVALID_REQUEST");
        var r = request!;
        Require(r.RecordingId is { Length: > 0 and <= 64 } && Match(r.RecordingId, r.Platform switch {
            "wy" => "^[1-9][0-9]{0,19}$", "tx" => "^[A-Za-z0-9]{1,64}$", _ => "^[a-fA-F0-9]{32}$" }), "INVALID_REQUEST");
        Require(r.Platform != "kg" || r.RecordingId.Any(ch => ch != '0'), "INVALID_REQUEST");
        Require(double.IsFinite(r.DurationSeconds) && r.DurationSeconds >= 0 && r.DurationSeconds * 1000 <= 9007199254740990d, "INVALID_REQUEST");
        Require(r.AlbumAudioId is null || r.AlbumAudioId.Length <= 20 && Match(r.AlbumAudioId, "^[1-9][0-9]{0,19}$"), "INVALID_REQUEST");
    }
    internal static string Text(JsonNode? node, bool optional = false)
    {
        if (node is null && optional) return "";
        Require(node is JsonValue value && value.TryGetValue<string>(out _), "INVALID_RESPONSE");
        return node!.GetValue<string>();
    }
    internal static string Id(JsonNode? node, bool allowZero = false)
    {
        var value = PJ.Scalar(node);
        Require(value.Length <= 20 && Match(value, allowZero ? "^(0|[1-9][0-9]{0,19})$" : "^[1-9][0-9]{0,19}$"), "INVALID_RESPONSE");
        return value;
    }
    internal static long Integer(JsonNode? node)
    {
        Require(long.TryParse(PJ.Scalar(node), NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            value >= 0 && value <= 9007199254740991, "INVALID_RESPONSE");
        return value;
    }
    internal static JsonObject Parse(byte[] bytes, CancellationToken token)
    {
        Require(bytes.Length <= MaxResponse, "LIMIT");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            int nodes = 0;
            void Check(JsonElement value)
            {
                token.ThrowIfCancellationRequested(); Require(++nodes <= 20000, "LIMIT");
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    { Require(names.Add(property.Name), "INVALID_RESPONSE"); Check(property.Value); }
                }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
                else if (value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString()!; Require(s.Length <= MaxResponse, "LIMIT");
                    _ = new UTF8Encoding(false, true).GetByteCount(s);
                }
            }
            Check(doc.RootElement);
            Require(doc.RootElement.ValueKind == JsonValueKind.Object, "INVALID_RESPONSE");
            return (JsonObject)JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })!;
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or EncoderFallbackException or InvalidOperationException)
        { throw new MusicPlatformException("INVALID_RESPONSE"); }
    }
}
