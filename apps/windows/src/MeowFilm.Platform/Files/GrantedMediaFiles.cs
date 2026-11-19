using System.Security.Cryptography;
using System.Text;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;
namespace MeowFilm.Platform.Files;

public sealed record GrantedFile(string Id, string Path, string Title);

/// <summary>Explicit native file grants. Ordinary playback resolves one typed row, not the full collection.</summary>
public sealed class GrantedMediaFiles
{
    private const int MaximumItems = 100_000;
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
        if (selected.Length is 0 or > 1000) throw new InvalidDataException("FILE_LIMIT");
        var files = await Task.Run(() => selected.Select(Grant).DistinctBy(file => file.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        try { await database.WriteAsync(db =>
        {
            ValidateSchema(db);
            if (db.Scalar<long>("SELECT count(*) FROM windows_file_grants WHERE scope_key='device'") == 0)
                FileGrantRecords.Files.Save(db, new System.Text.Json.Nodes.JsonObject { ["Schema"] = 1, ["Items"] = new System.Text.Json.Nodes.JsonArray() }, null, ["device"]);
            long count = db.Scalar<long>("SELECT count(*) FROM windows_file_grants_Items WHERE scope_key='device'");
            foreach (var file in files)
            {
                if (Find(db, file.Id) is { } previous) ValidateCurrent(previous);
                db.Execute("INSERT INTO windows_file_grants_Items(scope_key,Items_key,ordinal,shape_mask,null_mask,Path,Title) " +
                    "VALUES('device',$id,$ordinal,3,0,$path,$title) ON CONFLICT(scope_key,Items_key) DO UPDATE SET Path=excluded.Path,Title=excluded.Title",
                    ("$id", file.Id), ("$ordinal", count++), ("$path", file.Path), ("$title", file.Title));
            }
            if (db.Scalar<long>("SELECT count(*) FROM windows_file_grants_Items WHERE scope_key='device'") > MaximumItems) throw new InvalidDataException("FILE_LIMIT");
            return true;
        }, cancellationToken, files.Sum(file => (file.Id.Length + file.Path.Length + file.Title.Length) * 2)).ConfigureAwait(false); }
        catch (DocumentStoreException error) when (error.Error is DocumentStoreError.Storage or DocumentStoreError.Io)
        { throw new InvalidDataException("COMMIT_UNKNOWN"); }
        return files;
    }
    public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        var file = await database.ReadAsync(db => { ValidateSchema(db); return Find(db, id); }, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new InvalidDataException("FILE_UNAVAILABLE");
        ValidateCurrent(file);
        return await Task.Run(() =>
        {
            if (!File.Exists(file.Path)) throw new InvalidDataException("FILE_UNAVAILABLE");
            RuntimeBundle.EnsureOrdinaryAncestors(file.Path);
            return new Uri(file.Path);
        }, cancellationToken).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<GrantedFile>> ListAsync(CancellationToken cancellationToken = default) => database.ReadAsync(db =>
    {
        ValidateSchema(db);
        var files = db.Query("SELECT Items_key,Path,Title FROM windows_file_grants_Items WHERE scope_key='device' ORDER BY ordinal,Items_key",
            row => new GrantedFile(row.GetString(0), row.GetString(1), row.GetString(2)));
        foreach (var file in files) ValidateCurrent(file);
        return files;
    }, cancellationToken);
    private static GrantedFile? Find(DatabaseSession db, string id) => db.Query(
        "SELECT Items_key,Path,Title FROM windows_file_grants_Items WHERE scope_key='device' AND Items_key=$id",
        row => new GrantedFile(row.GetString(0), row.GetString(1), row.GetString(2)), ("$id", id)).SingleOrDefault();
    private static void ValidateSchema(DatabaseSession db)
    {
        var schemas = db.Query("SELECT Schema FROM windows_file_grants WHERE scope_key='device'", row => row.GetInt64(0));
        if (schemas.Count > 0 && schemas[0] != 1)
            throw Corrupt();
    }
    private void ValidateCurrent(GrantedFile file)
    {
        if (file.Id.Length != 70 || !file.Id.StartsWith("local:", StringComparison.Ordinal) ||
            !IsLowerHex(file.Id.AsSpan(6)) ||
            !Path.IsPathFullyQualified(file.Path) || file.Path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(file.Path), file.Path, StringComparison.Ordinal) ||
            !extensions.Contains(Path.GetExtension(file.Path)) ||
            file.Title.Length == 0 || file.Title != Path.GetFileNameWithoutExtension(file.Path) ||
            file.Id != "local:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(file.Path.ToUpperInvariant()))))
            throw Corrupt();
    }

    private GrantedFile Grant(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException("FILE_UNAVAILABLE");
            path = Path.GetFullPath(path);
            if (!extensions.Contains(Path.GetExtension(path)) || !File.Exists(path))
                throw new InvalidDataException("FILE_UNAVAILABLE");
            RuntimeBundle.EnsureOrdinaryAncestors(path);
            var id = "local:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            return new GrantedFile(id, path, Path.GetFileNameWithoutExtension(path));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or BridgeException)
        { throw new InvalidDataException("FILE_UNAVAILABLE"); }
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static bool IsValidExtension(string extension)
    {
        if (extension.Length is < 2 or > 16 || extension[0] != '.') return false;
        for (int i = 1; i < extension.Length; i++)
            if (!char.IsAsciiLetterOrDigit(extension[i])) return false;
        return true;
    }

    private static InvalidDataException Corrupt() => new("CORRUPT_STORAGE");
}
