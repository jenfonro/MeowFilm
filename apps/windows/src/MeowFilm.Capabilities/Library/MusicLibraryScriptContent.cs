using System.Security.Cryptography;
using System.Text;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed record MusicScriptContent(string Sha256, int Length);

public sealed partial class MusicLibraryOwner
{
    public const string ScriptContentCollection = "music.script-sources.v1";
    private const int SourceChunkBytes = 1024 * 1024;
    private static string ContentKey(string hash, int chunk) =>
        $"{ScriptContentCollection}.{hash}.{chunk.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    private static void ContentIdentity(string hash, int length) =>
        LJ.Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            length > 0 && length <= Array.MaxLength,
            "CORRUPT_STORAGE");

    /// <summary>Durable, immutable protected chunks. Only metadata publication
    /// selects a source. Orphaned content is never silently deleted or treated
    /// as an installation. No whole-library/source-string size gate applies.</summary>
    public async Task<MusicScriptContent> StoreScriptContentAsync(ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        LJ.Require(source.Length > 0, "SCRIPT_ENCODING");
        byte[] owned = source.ToArray();
        try
        {
            try
            {
                string text = new UTF8Encoding(false, true).GetString(owned);
                LJ.Require(!string.IsNullOrWhiteSpace(text) && !text.StartsWith('\uFEFF') &&
                    Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(owned), "SCRIPT_ENCODING");
            }
            catch (DecoderFallbackException) { throw new MusicLibraryException("SCRIPT_ENCODING"); }
            string hash = LJ.Hash(owned);
            for (int offset = 0, index = 0; offset < owned.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(SourceChunkBytes, owned.Length - offset);
                var chunk = owned.AsMemory(offset, count);
                var expected = DocumentFingerprint.FromBytes(chunk.Span);
                string key = ContentKey(hash, index);
                var existing = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (existing.Data is not null)
                        LJ.Require(existing.Fingerprint == expected && existing.Data.AsSpan().SequenceEqual(chunk.Span), "CORRUPT_STORAGE");
                    else
                    {
                        LJ.Require(existing.Fingerprint == DocumentFingerprint.Missing, "CORRUPT_STORAGE");
                        try
                        {
                            var saved = await store.WriteAsync(key, chunk, DocumentFingerprint.Missing,
                                cancellationToken).ConfigureAwait(false);
                            LJ.Require(saved == expected, "COMMIT_UNKNOWN");
                        }
                        catch (Exception error) when (error is ProtectedDocumentException or MusicLibraryException)
                        {
                            // Immutable content can reconcile an interrupted or
                            // concurrent identical write without mutating metadata.
                            var saved = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
                            try { LJ.Require(saved.Data is not null && saved.Fingerprint == expected &&
                                saved.Data.AsSpan().SequenceEqual(chunk.Span), "STORAGE_ERROR"); }
                            finally { if (saved.Data is not null) CryptographicOperations.ZeroMemory(saved.Data); }
                        }
                    }
                }
                finally { if (existing.Data is not null) CryptographicOperations.ZeroMemory(existing.Data); }
                offset += count;
            }
            return new(hash, owned.Length);
        }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }

    /// <summary>Caller-owned original bytes; verifies every protected chunk and
    /// complete content hash before execution. Never substitutes migrated text.</summary>
    public async Task<byte[]> ReadScriptContentAsync(string hash, int length,
        CancellationToken cancellationToken = default)
    {
        ContentIdentity(hash, length);
        byte[] result = new byte[length];
        try
        {
            for (int offset = 0, index = 0; offset < length; index++)
            {
                var chunk = await store.ReadAsync(ContentKey(hash, index), cancellationToken).ConfigureAwait(false);
                try
                {
                    int count = Math.Min(SourceChunkBytes, length - offset);
                    LJ.Require(chunk.Data is not null && chunk.Data.Length == count &&
                        DocumentFingerprint.FromBytes(chunk.Data) == chunk.Fingerprint, "CORRUPT_STORAGE");
                    chunk.Data.CopyTo(result, offset); offset += count;
                }
                finally { if (chunk.Data is not null) CryptographicOperations.ZeroMemory(chunk.Data); }
            }
            LJ.Require(LJ.Hash(result) == hash, "CORRUPT_STORAGE");
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }
}
