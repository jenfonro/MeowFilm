using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MeowFilm.Platform.Files;

public sealed record SelectedLyricSnapshotData(string Text, string Sha256, string Basename);
public enum SelectedLyricSnapshotError { InvalidPath, Unavailable, UnsafeFile, TooLarge, InUse, Changed, ReadFailed, InvalidEncoding }
public sealed class SelectedLyricSnapshotException(SelectedLyricSnapshotError error) : Exception(error.ToString())
{
    public SelectedLyricSnapshotError Error { get; } = error;
}

/// <summary>Explicit, read-only local timed-text selection. No directory discovery, path grant or script execution.</summary>
public static class SelectedLyricSnapshot
{
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumCharacters = 65536;
    public static async Task<SelectedLyricSnapshotData> ReadAsync(string selectedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension;
        try { extension = Path.GetExtension(selectedPath).ToLowerInvariant(); }
        catch { throw new SelectedLyricSnapshotException(SelectedLyricSnapshotError.InvalidPath); }
        if (extension is not (".lrc" or ".qrc" or ".yrc" or ".txt"))
            throw new SelectedLyricSnapshotException(SelectedLyricSnapshotError.InvalidPath);
        SelectedFileSnapshotData? snapshot = null;
        try
        {
            snapshot = await SelectedFileSnapshot.ReadAsync(selectedPath, extension, MaximumBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = snapshot.Bytes.AsSpan();
            string text;
            // BOM is explicit. Otherwise require valid UTF-8 rather than silently
            // substituting mojibake or guessing a machine-specific ANSI encoding.
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                text = new UTF8Encoding(false, true).GetString(bytes[3..]);
            else if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
                text = new UnicodeEncoding(false, false, true).GetString(bytes[2..]);
            else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
                text = new UnicodeEncoding(true, false, true).GetString(bytes[2..]);
            else text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > MaximumCharacters) throw new SelectedLyricSnapshotException(SelectedLyricSnapshotError.TooLarge);
            if (text.Length == 0 || text.Any(c => c < 32 && c is not ('\r' or '\n' or '\t')))
                throw new SelectedLyricSnapshotException(SelectedLyricSnapshotError.InvalidEncoding);
            string sha256;
            try { sha256 = Convert.ToHexStringLower(SHA256.HashData(snapshot.Bytes)); }
            catch { throw new SelectedLyricSnapshotException(SelectedLyricSnapshotError.ReadFailed); }
            cancellationToken.ThrowIfCancellationRequested();
            return new(text, sha256, snapshot.Basename);
        }
        catch (SelectedFileSnapshotException error)
        { throw new SelectedLyricSnapshotException((SelectedLyricSnapshotError)error.Error); }
        catch (DecoderFallbackException)
        { throw new SelectedLyricSnapshotException(SelectedLyricSnapshotError.InvalidEncoding); }
        finally { if (snapshot is not null) CryptographicOperations.ZeroMemory(snapshot.Bytes); }
    }
}
