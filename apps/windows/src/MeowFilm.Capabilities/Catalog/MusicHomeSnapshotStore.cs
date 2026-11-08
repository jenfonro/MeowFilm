using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record MusicHomeSnapshot(string Platform, JsonElement[] Pages);

// Display content only. Restored actions still require current account authority.
internal sealed class MusicHomeSnapshotStore(IDocumentStore store)
{
    internal const string Namespace = "music.home.v1";
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<MusicHomeSnapshot?> ReadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return Decode(await store.ReadAsync(Namespace, token).ConfigureAwait(false), token);
        }
        finally { gate.Release(); }
    }

    internal async Task SaveAsync(MusicHomeSnapshot snapshot, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] bytes = Encode(snapshot, token);
            var before = await store.ReadAsync(Namespace, token).ConfigureAwait(false);
            _ = Decode(before, token);
            var expected = DocumentFingerprint.FromBytes(bytes);
            token.ThrowIfCancellationRequested();
            // Once admitted, the store owns the write. Await its real receipt even
            // if the page leaves; never replay this snapshot over a CAS winner.
            var receipt = await store.WriteAsync(Namespace, bytes, before.Fingerprint, token).ConfigureAwait(false);
            if (receipt != expected) throw new DocumentStoreException(DocumentStoreError.Storage);
        }
        finally { gate.Release(); }
    }

    private static MusicHomeSnapshot? Decode(DocumentSnapshot saved, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (saved.Data is null)
        {
            Require(saved.Fingerprint == DocumentFingerprint.Missing);
            return null;
        }
        if (saved.Data.Length > LocalDocumentStore.MaximumDocumentBytes)
            throw new DocumentStoreException(DocumentStoreError.Limit);
        Require(saved.Data.Length > 0 && DocumentFingerprint.FromBytes(saved.Data) == saved.Fingerprint);
        try
        {
            using var document = JsonDocument.Parse(saved.Data);
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                Require(fields.Add(property.Name) && property.Name is "schema" or "platform" or "pages");
            Require(fields.Count == 3);
            if (root.GetProperty("schema").GetRawText() != "1")
                throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
            string platform = MusicCatalogDto.Text(root, "platform");
            Require(platform is "wy" or "kg");
            var pages = root.GetProperty("pages");
            Require(pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 0);
            var result = new JsonElement[pages.GetArrayLength()];
            int index = 0;
            foreach (var page in pages.EnumerateArray())
            {
                ValidatePage(page, platform, stored: true, token);
                result[index++] = page.Clone();
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
                writer.WriteNumber("schema", 1);
                writer.WriteString("platform", snapshot.Platform);
                writer.WriteStartArray("pages");
                foreach (var page in snapshot.Pages)
                {
                    ValidatePage(page, snapshot.Platform, stored: false, token);
                    writer.WriteStartObject();
                    foreach (var property in page.EnumerateObject())
                        if (PageField(property.Name)) property.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.Flush();
                    if (output.Length > LocalDocumentStore.MaximumDocumentBytes)
                        throw new DocumentStoreException(DocumentStoreError.Limit);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            if (output.Length > LocalDocumentStore.MaximumDocumentBytes)
                throw new DocumentStoreException(DocumentStoreError.Limit);
            token.ThrowIfCancellationRequested();
            return output.ToArray();
        }
        catch (Exception error) when (IsFormatError(error))
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }

    private static void ValidatePage(JsonElement page, string platform, bool stored, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(page.ValueKind == JsonValueKind.Object && MusicCatalogDto.Text(page, "platform") == platform);
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool hasContent = false;
        foreach (var property in page.EnumerateObject())
        {
            Require(names.Add(property.Name));
            if (!PageField(property.Name))
            {
                Require(!stored && (property.Name != "ok" || property.Value.ValueKind == JsonValueKind.True));
                continue;
            }
            if (property.Name == "platform") continue;
            hasContent = true;
            CheckContent(property.Value, token);
        }
        Require(hasContent);
        _ = MusicCatalogDto.Read(page, platform, "home", "");
    }

    private static bool PageField(string name) => name is "platform" or "entries" or "sections" or "items";

    private static void CheckContent(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                // The DTO retains raw actions and songs. A durable document must
                // not smuggle credentials or request authority through those fields.
                Require(names.Add(property.Name) && !Regex.IsMatch(property.Name,
                    "cookie|token|password|secret|credential|authorization|api[-_]?key|^(headers|scope|cursor|continuation|expiresAt|snapshot|accountStamp)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                CheckContent(property.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckContent(item, token);
    }

    private static bool IsFormatError(Exception error) => error is JsonException or MusicOperationException or
        InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException;

    private static void Require(bool condition)
    {
        if (!condition) throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }
}
