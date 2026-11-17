using System.Security.Cryptography;
using System.Text.Json;

namespace MeowFilm.Platform.Storage;

/// <summary>Native settings edits use the document store's real CAS receipt, not a runtime acknowledgement.</summary>
public sealed class SettingsDocument(IDocumentStore store, string documentNamespace)
{
    private readonly SemaphoreSlim serial = new(1, 1);

    public static JsonElement SetMember(JsonElement original, string name, JsonElement value) =>
        Patch(original, new Dictionary<string, JsonElement> { [name] = value });

    // Utf8JsonReader locates members; untouched spans retain exact numbers, escaped
    // names and duplicate extensions without decoding or rebuilding their values.
    public static JsonElement Patch(JsonElement original, IReadOnlyDictionary<string, JsonElement> changes,
        IReadOnlySet<string>? removed = null)
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes(original.GetRawText());
        var reader = new Utf8JsonReader(input);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)'{');
        var written = new HashSet<string>(StringComparer.Ordinal);
        bool first = true;
        void Member(ReadOnlySpan<byte> bytes)
        {
            if (!first) buffer.WriteByte((byte)',');
            first = false; buffer.Write(bytes);
        }
        void Replacement(string key, JsonElement value)
        {
            Member(JsonSerializer.SerializeToUtf8Bytes(key));
            buffer.WriteByte((byte)':');
            buffer.Write(System.Text.Encoding.UTF8.GetBytes(value.GetRawText()));
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            int start = checked((int)reader.TokenStartIndex);
            string? changed = null;
            foreach (string key in changes.Keys) if (reader.ValueTextEquals(key)) { changed = key; break; }
            bool remove = false;
            if (changed is null && removed is not null)
                foreach (string key in removed) if (reader.ValueTextEquals(key)) { remove = true; break; }
            reader.Read(); reader.Skip();
            if (changed is not null) { Replacement(changed, changes[changed]); written.Add(changed); }
            else if (!remove) Member(input.AsSpan(start, checked((int)reader.BytesConsumed) - start));
        }
        foreach (var (key, value) in changes) if (written.Add(key)) Replacement(key, value);
        buffer.WriteByte((byte)'}');
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    public async Task<JsonElement?> ReadAsync(CancellationToken token = default)
    {
        var snapshot = await store.ReadAsync(documentNamespace, token).ConfigureAwait(false);
        try { return Decode(snapshot.Data); }
        finally { if (snapshot.Data is not null) CryptographicOperations.ZeroMemory(snapshot.Data); }
    }

    public async Task<JsonElement> UpdateAsync(Func<JsonElement?, JsonElement> edit, CancellationToken token = default)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = await store.ReadAsync(documentNamespace, token).ConfigureAwait(false);
            byte[]? bytes = null;
            try
            {
                var next = edit(Decode(before.Data));
                bytes = System.Text.Encoding.UTF8.GetBytes(next.GetRawText());
                var expected = DocumentFingerprint.FromBytes(bytes);
                token.ThrowIfCancellationRequested();
                if (expected != before.Fingerprint)
                {
                    // After admission a cancelled page cannot revoke a durable write.
                    var receipt = await store.WriteAsync(documentNamespace, bytes, before.Fingerprint,
                        CancellationToken.None).ConfigureAwait(false);
                    if (receipt != expected) throw new DocumentStoreException(DocumentStoreError.Storage);
                }
                return next.Clone();
            }
            finally
            {
                if (before.Data is not null) CryptographicOperations.ZeroMemory(before.Data);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally { serial.Release(); }
    }

    private static JsonElement? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return json.RootElement.Clone();
        }
        catch (JsonException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }
}
