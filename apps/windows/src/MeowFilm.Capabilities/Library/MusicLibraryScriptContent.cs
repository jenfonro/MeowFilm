using System.Text;
using MeowFilm.Platform.Files;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Library;

public sealed record MusicScriptContent(string Sha256, int Length);

public sealed partial class MusicLibraryOwner
{
    public const string ScriptContentCollection = "music.script-sources.v2";
    private static string ContentKey(string hash)
    {
        LJ.Require(hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "CORRUPT_STORAGE");
        return $"{ScriptContentCollection}.{hash}";
    }
    private static void ContentIdentity(string hash, int length)
    {
        _ = ContentKey(hash);
        LJ.Require(length > 0 && length <= SelectedScriptSnapshot.MaximumBytes, "CORRUPT_STORAGE");
    }

    /// <summary>The import hash identifies a source file, not immutable contents.
    /// Publishing metadata selects it; users may edit the saved file afterwards.</summary>
    public async Task<MusicScriptContent> StoreScriptContentAsync(ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        LJ.Require(source.Length > 0, "SCRIPT_ENCODING");
        LJ.Require(source.Length <= SelectedScriptSnapshot.MaximumBytes, "LIMIT");
        byte[] owned = source.ToArray();
        try
        {
            string text = new UTF8Encoding(false, true).GetString(owned);
            LJ.Require(!string.IsNullOrWhiteSpace(text) && !text.StartsWith('\uFEFF'), "SCRIPT_ENCODING");
        }
        catch (DecoderFallbackException) { throw new MusicLibraryException("SCRIPT_ENCODING"); }
        string hash = LJ.Hash(owned);
        string key = ContentKey(hash);
        var existing = await store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var saved = await store.WriteAsync(key, owned, existing.Fingerprint, cancellationToken).ConfigureAwait(false);
        LJ.Require(saved == new DocumentFingerprint(true, owned.Length, hash), "COMMIT_UNKNOWN");
        return new(hash, owned.Length);
    }

    /// <summary>Read the current file bytes. The import hash only locates the file;
    /// neither its original length nor its original contents gate execution.</summary>
    public async Task<byte[]> ReadScriptContentAsync(string hash, CancellationToken cancellationToken = default)
    {
        var source = await store.ReadAsync(ContentKey(hash), cancellationToken).ConfigureAwait(false);
        LJ.Require(source.Data is not null, "NOT_FOUND");
        LJ.Require(source.Data.Length > 0, "SCRIPT_ENCODING");
        LJ.Require(source.Data.Length <= SelectedScriptSnapshot.MaximumBytes, "LIMIT");
        return source.Data;
    }
}
