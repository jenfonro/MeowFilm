using System.Security.Cryptography;
using System.Text.Json;

namespace MeowFilm.Platform.Storage;

/// <summary>In-memory settings edits commit fixed rows and a monotonic receipt in one transaction.</summary>
public sealed class SettingsDocument(TypedRecordRepository store)
{

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
        var snapshot = await store.ReadAsync(token).ConfigureAwait(false);
        try { return Decode(snapshot.Data); }
        finally { if (snapshot.Data is not null) CryptographicOperations.ZeroMemory(snapshot.Data); }
    }

    public Task<JsonElement> UpdateAsync(Func<JsonElement?, JsonElement> edit, CancellationToken token = default) =>
        store.UpdateAsync(edit, token);

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
