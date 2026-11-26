using System.Security.Cryptography;
using System.Text;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Platform.Files;

public sealed record GrantedFile(string Id, string Path, string Title);

/// <summary>Explicit native file grants. Ordinary playback resolves one typed row, not the full collection.</summary>
public sealed class GrantedMediaFiles
{
    private readonly IStorageDatabase database;
    private readonly HashSet<string> extensions;
    public GrantedMediaFiles(IStorageDatabase database, IEnumerable<string> extensions)
    {
        this.database = database;
        this.extensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (this.extensions.Count == 0 || this.extensions.Any(extension => !IsValidExtension(extension))) throw new ArgumentException(nameof(extensions));
    }
    public async Task<IReadOnlyList<GrantedFile>> GrantAsync(IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default)
    {
        var selected = selectedPaths?.ToArray() ?? throw new InvalidDataException("FILE_LIMIT");
        if (selected.Length == 0) throw new InvalidDataException("FILE_LIMIT");
        var files = await Task.Run(() => selected.Select(Grant).DistinctBy(file => file.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        await database.WriteAsync(db =>
        {
            long nextPosition = db.Scalar<long>("SELECT COALESCE(MAX(position) + 1, 0) FROM windows_file_grants");
            foreach (var file in files)
            {
                db.Execute("""
                    INSERT INTO windows_file_grants(grant_id,position,file_path,title)
                    VALUES($id,$position,$path,$title)
                    ON CONFLICT(grant_id) DO UPDATE SET file_path=excluded.file_path,title=excluded.title
                    """,
                    ("$id", file.Id), ("$position", nextPosition++), ("$path", file.Path), ("$title", file.Title));
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return files;
    }
    public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = await database.ReadAsync(db => FindPath(db, id), cancellationToken).ConfigureAwait(false);
        if (path is null || !Path.IsPathFullyQualified(path) || !extensions.Contains(Path.GetExtension(path)))
            throw new InvalidDataException("FILE_UNAVAILABLE");
        return await Task.Run(() =>
        {
            if (!File.Exists(path)) throw new InvalidDataException("FILE_UNAVAILABLE");
            return new Uri(path);
        }, cancellationToken).ConfigureAwait(false);
    }
    private static string? FindPath(DatabaseSession db, string id) =>
        db.TryQuerySingle(
            "SELECT file_path FROM windows_file_grants WHERE grant_id=$id",
            row => row.GetString(0),
            out var path, ("$id", id))
            ? path
            : null;

    private GrantedFile Grant(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("FILE_UNAVAILABLE");
            path = Path.GetFullPath(path);
            if (!extensions.Contains(Path.GetExtension(path)) || !File.Exists(path))
                throw new InvalidDataException("FILE_UNAVAILABLE");
            var title = Path.GetFileNameWithoutExtension(path);
            if (title.Length == 0) throw new InvalidDataException("FILE_UNAVAILABLE");
            var id = "local:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            return new GrantedFile(id, path, title);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or BridgeException)
        { throw new InvalidDataException("FILE_UNAVAILABLE"); }
    }

    private static bool IsValidExtension(string extension)
    {
        if (extension.Length is < 2 or > 16 || extension[0] != '.') return false;
        for (int i = 1; i < extension.Length; i++)
            if (!char.IsAsciiLetterOrDigit(extension[i])) return false;
        return true;
    }
}
