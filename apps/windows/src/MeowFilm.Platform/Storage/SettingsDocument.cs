using System.Security.Cryptography;
using System.Text.Json;

namespace MeowFilm.Platform.Storage;

/// <summary>Native settings edits use the document store's real CAS receipt, not a runtime acknowledgement.</summary>
public sealed class SettingsDocument(IDocumentStore store, string documentNamespace)
{
    private readonly SemaphoreSlim serial = new(1, 1);

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
                bytes = JsonSerializer.SerializeToUtf8Bytes(next);
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
