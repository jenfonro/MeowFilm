using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record MusicHomeSnapshot(string Platform, JsonElement[] Pages);

// Display content only. Restored actions still require current account authority.
internal sealed class MusicHomeSnapshotStore(CacheDocumentStore store)
{
    internal const string Namespace = "music.home.v3";
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<MusicHomeSnapshot?> ReadAsync(Func<string, JsonElement?> song, CancellationToken token = default)
    {
        try
        {
            return Decode(await store.ReadAsync(Namespace, token).ConfigureAwait(false), song, token);
        }
        catch (Exception error) when (IsCacheFailure(error)) { return null; }
    }

    internal async Task SaveAsync(MusicHomeSnapshot snapshot, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] bytes = Encode(snapshot, token);
            token.ThrowIfCancellationRequested();
            store.Save(Namespace, bytes);
        }
        catch (Exception error) when (IsCacheFailure(error)) { }
        finally { gate.Release(); }
    }

    private static MusicHomeSnapshot? Decode(DocumentSnapshot saved, Func<string, JsonElement?> song, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (saved.Data is null || saved.Data.Length > CacheDocumentStore.MaximumDocumentBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(saved.Data);
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object);
            string platform = MusicCatalogDto.Text(root, "platform");
            Require(platform is "wy" or "kg");
            var pages = root.GetProperty("pages");
            Require(pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 0);
            var result = new JsonElement[pages.GetArrayLength()];
            int index = 0;
            foreach (var page in pages.EnumerateArray())
            {
                var restored = Restore(ProjectPage(page, platform, token), song, token);
                var current = JsonSerializer.SerializeToElement(restored);
                result[index++] = current;
            }
            token.ThrowIfCancellationRequested();
            return new(platform, result);
        }
        catch (Exception error) when (IsFormatError(error))
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }

    private static byte[] Encode(MusicHomeSnapshot snapshot, CancellationToken token)
    {
        Require(snapshot.Platform is "wy" or "kg" && snapshot.Pages.Length > 0);
        try
        {
            using var output = new MemoryStream();
            // Match JsonDocument's standard JSON nesting boundary.
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { MaxDepth = 64 }))
            {
                writer.WriteStartObject();
                writer.WriteString("platform", snapshot.Platform);
                writer.WriteStartArray("pages");
                foreach (var page in snapshot.Pages)
                {
                    JsonSerializer.Serialize(writer, ProjectPage(page, snapshot.Platform, token));
                    writer.Flush();
                    if (output.Length > CacheDocumentStore.MaximumDocumentBytes)
                        throw new DocumentStoreException(DocumentStoreError.Limit);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            if (output.Length > CacheDocumentStore.MaximumDocumentBytes)
                throw new DocumentStoreException(DocumentStoreError.Limit);
            token.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (Exception error) when (IsFormatError(error))
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }

    private static JsonObject ProjectPage(JsonElement page, string platform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new JsonObject { ["platform"] = platform };
        result["sections"] = new JsonArray(MusicCatalogDto.Rows(MusicCatalogDto.Field(page, "sections"))
            .Select(row => Project(row, "section", token)).ToArray());
        return result;
    }

    private static JsonNode? Project(JsonElement value, string kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.False)
            return new JsonObject { ["display"] = false };
        string fields = kind switch
        {
            "section" => "id title layout available errorCode requiresAccount display selectedTab selectedTag",
            "tab" => "id name",
            "tag" => "id name group selected",
            "action" => "type id branch pageCode",
            "channel" => "id name pageCode channelKind placement",
            "coverRef" => "kind id",
            "locator" => "kind id playlistId dirId listId version totalVersion creatorId creatorListId",
            "preview" => "title artist cover rank lastRank",
            "song" => "songId",
            _ => "id key title label subtitle contextText creator cover coverLabel kind pageCode channelKind available errorCode requiresAccount display placement canPlay rankText salesText relationship count playCount presentation dayOfMonth decoration updateFrequency"
        };
        var result = new JsonObject();
        foreach (string field in fields.Split(' '))
            if (value.TryGetProperty(field, out var scalar))
            {
                if (scalar.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                result[field] = JsonNode.Parse(scalar.GetRawText());
            }
        if (kind == "song") return result;
        if (kind is "card" or "preview" && value.TryGetProperty("song", out var song))
            result["song"] = Project(song, "song", token);
        foreach (string field in kind switch
        {
            "section" => new[] { "action" },
            "card" => new[] { "action", "locator", "playbackSource", "coverRef" },
            "action" => new[] { "channel", "locator" },
            _ => Array.Empty<string>()
        })
            if (value.TryGetProperty(field, out var nested))
                result[field] = Project(nested, field == "playbackSource" ? "action" : field, token);
        if (kind is "section" or "tab" && value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            result["items"] = new JsonArray(items.EnumerateArray().Select(item => Project(item, "card", token)).ToArray());
        if (kind == "section")
            foreach (var field in new[] { "tabs", "tags" })
                result[field] = new JsonArray(MusicCatalogDto.Rows(MusicCatalogDto.Field(value, field))
                    .Select(item => Project(item, field == "tabs" ? "tab" : "tag", token)).ToArray());
        if (kind == "card" && value.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Array)
            result["preview"] = new JsonArray(preview.EnumerateArray().Take(3).Select(item => Project(item, "preview", token)).ToArray());
        return result;
    }
    private static JsonNode? Restore(JsonNode? value, Func<string, JsonElement?> song, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value is JsonObject reference && reference["songId"] is JsonValue id)
        {
            var current = song(id.GetValue<string>());
            if (current is null) return null;
            var restored = JsonNode.Parse(current.Value.GetRawText())!.AsObject();
            foreach (var field in reference)
                if (field.Key != "songId") restored[field.Key] = field.Value?.DeepClone();
            return restored;
        }
        if (value is JsonObject obj)
            return new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Restore(pair.Value, song, token))));
        if (value is JsonArray array) return new JsonArray(array.Select(item => Restore(item, song, token)).ToArray());
        return value?.DeepClone();
    }

    private static bool IsCacheFailure(Exception error) => error is DocumentStoreException || IsFormatError(error);

    private static bool IsFormatError(Exception error) => error is JsonException or MusicOperationException or
        InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException;

    private static void Require(bool condition)
    {
        if (!condition) throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }
}
