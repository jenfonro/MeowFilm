using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Platform.Debug;

public sealed class DebugBlobStore(string blobsDirectory)
{
    private const int HeadLength = 512;
    private const int TailLength = 128;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public string BlobsDirectory { get; } = blobsDirectory;

    public JsonNode? ExternalizeData(object? data, int inlineLimit, int eventBytesLimit)
    {
        if (data is null) return null;
        JsonNode? node;
        try
        {
            node = data switch
            {
                JsonNode existing => existing.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                _ => JsonSerializer.SerializeToNode(data, SerializerOptions)
            };
        }
        catch (Exception ex)
        {
            node = new JsonObject
            {
                ["serializationError"] = ex.GetType().Name,
                ["message"] = ex.Message
            };
        }
        if (node is null) return null;
        var folded = inlineLimit > 0 ? FoldStrings(node, inlineLimit) : node;
        if (eventBytesLimit <= 0) return folded;
        var rawJson = folded.ToJsonString(SerializerOptions);
        var rawBytes = Encoding.UTF8.GetBytes(rawJson);
        if (rawBytes.Length <= eventBytesLimit) return folded;
        var sha = WriteDeduplicated(rawBytes, "json");
        var summary = BuildSummary(folded);
        return new JsonObject
        {
            ["$blob"] = $"blobs/{sha}.json",
            ["sha256"] = sha,
            ["bytes"] = rawBytes.Length,
            ["summary"] = summary
        };
    }

    public static bool TryReadBlob(string sessionDir, string sha256, out byte[] bytes, out string contentType, out string fileName)
    {
        bytes = [];
        contentType = "application/octet-stream";
        fileName = "";
        var normalized = sha256?.Trim().ToLowerInvariant();
        if (!IsValidSha256(normalized)) return false;
        var blobsDir = Path.Combine(sessionDir, "blobs");
        if (!Directory.Exists(blobsDir)) return false;
        foreach (var candidateExt in new[] { "txt", "json" })
        {
            var direct = Path.Combine(blobsDir, $"{normalized}.{candidateExt}");
            if (File.Exists(direct))
            {
                bytes = ReadAllBytesShared(direct);
                contentType = candidateExt == "json" ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
                fileName = Path.GetFileName(direct);
                return true;
            }
        }
        foreach (var file in Directory.EnumerateFiles(blobsDir, normalized + ".*"))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            bytes = ReadAllBytesShared(file);
            fileName = Path.GetFileName(file);
            contentType = ContentTypeForExtension(Path.GetExtension(file));
            return true;
        }
        return false;
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream(fs.CanSeek && fs.Length <= int.MaxValue ? (int)fs.Length : 0);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static bool IsValidSha256(string? value)
    {
        if (value is not { Length: 64 }) return false;
        foreach (char c in value)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        return true;
    }

    public static string ContentTypeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".json" or ".heapsnapshot" or ".cpuprofile" => "application/json; charset=utf-8",
        ".jsonl" or ".txt" or ".log" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private JsonNode FoldStrings(JsonNode node, int inlineLimit)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > inlineLimit:
                return FoldSingleString(text);
            case JsonObject obj:
            {
                if (IsFoldedBlob(obj)) return obj;
                var keys = obj.Select(pair => pair.Key).ToArray();
                foreach (var key in keys)
                {
                    if (obj[key] is { } child)
                    {
                        var replaced = FoldStrings(child, inlineLimit);
                        if (!ReferenceEquals(replaced, child)) obj[key] = replaced;
                    }
                }
                return obj;
            }
            case JsonArray array:
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child)
                    {
                        var replaced = FoldStrings(child, inlineLimit);
                        if (!ReferenceEquals(replaced, child)) array[i] = replaced;
                    }
                }
                return array;
            }
            default:
                return node;
        }
    }

    private static bool IsFoldedBlob(JsonObject obj) =>
        obj["$blob"] is JsonValue b && b.TryGetValue<string>(out _) &&
        obj["sha256"] is JsonValue s && s.TryGetValue<string>(out _);

    private JsonObject FoldSingleString(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var sha = WriteDeduplicated(utf8, "txt");
        var head = SafeSliceHead(text, HeadLength);
        var tail = SafeSliceTail(text, TailLength);
        return new JsonObject
        {
            ["$blob"] = $"blobs/{sha}.txt",
            ["sha256"] = sha,
            ["length"] = text.Length,
            ["bytes"] = utf8.Length,
            ["head"] = head,
            ["tail"] = tail
        };
    }

    private static string SafeSliceHead(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        int len = maxLen;
        if (len > 0 && char.IsHighSurrogate(text[len - 1])) len--;
        return text[..len];
    }

    private static string SafeSliceTail(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        int start = text.Length - maxLen;
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        return text[start..];
    }

    private string WriteDeduplicated(ReadOnlySpan<byte> content, string extension)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var target = Path.Combine(BlobsDirectory, $"{sha}.{extension}");
        if (File.Exists(target)) return sha;
        Directory.CreateDirectory(BlobsDirectory);
        var temp = Path.Combine(BlobsDirectory, $".{sha}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, content);
            try
            {
                File.Move(temp, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another writer stored the exact same content-addressed blob concurrently.
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { }
            }
        }
        return sha;
    }

    private static JsonObject BuildSummary(JsonNode node)
    {
        var summary = new JsonObject();
        if (node is JsonObject obj)
        {
            var keys = new JsonArray();
            var counts = new JsonObject();
            foreach (var pair in obj)
            {
                keys.Add(pair.Key);
                if (pair.Value is JsonArray arr) counts[pair.Key] = arr.Count;
                else if (pair.Value is JsonObject childObj) counts[pair.Key] = childObj.Count;
            }
            summary["keys"] = keys;
            if (counts.Count > 0) summary["counts"] = counts;
        }
        else if (node is JsonArray arr)
        {
            summary["type"] = "array";
            summary["count"] = arr.Count;
        }
        return summary;
    }
}
