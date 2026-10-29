using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Platform.Files;

public sealed record GrantedFile(string Id, string Path, string Title);

/// <summary>Paths explicitly chosen in native file UI. Never expose this namespace to script RPC.</summary>
public sealed class GrantedMediaFiles
{
    private const int MaximumDocumentBytes = 8 * 1024 * 1024, MaximumItems = 100_000;
    private readonly IProtectedDocumentStore store;
    private readonly string documentNamespace;
    private readonly HashSet<string> extensions;
    private readonly SemaphoreSlim gate = new(1, 1);

    public GrantedMediaFiles(IProtectedDocumentStore store, string documentNamespace, IEnumerable<string> extensions)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.documentNamespace = !DocumentRules.ValidNamespace(documentNamespace)
            ? throw new ArgumentException(nameof(documentNamespace)) : documentNamespace;
        this.extensions = (extensions ?? throw new ArgumentNullException(nameof(extensions)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (this.extensions.Count == 0 || this.extensions.Any(extension => !IsValidExtension(extension)))
            throw new ArgumentException(nameof(extensions));
    }

    public async Task<IReadOnlyList<GrantedFile>> GrantAsync(IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default)
    {
        var selected = selectedPaths?.ToArray() ?? throw new InvalidDataException("FILE_LIMIT");
        if (selected.Length is 0 or > 1000) throw new InvalidDataException("FILE_LIMIT");
        var files = selected.Select(Grant).DistinctBy(file => file.Id).ToArray();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (current, fingerprint) = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            var known = current.ToDictionary(file => file.Id, StringComparer.Ordinal);
            foreach (var file in files) known[file.Id] = file;
            if (known.Count > MaximumItems) throw new InvalidDataException("FILE_LIMIT");
            await WriteLockedAsync(known.Values, fingerprint, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        return files;
    }

    public async Task<Uri> ResolveAsync(string id, CancellationToken cancellationToken = default)
    {
        GrantedFile? file;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (items, _) = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            file = items.SingleOrDefault(item => item.Id == id);
        }
        finally { gate.Release(); }
        if (file is null) throw new InvalidDataException("FILE_UNAVAILABLE");
        try
        {
            if (!File.Exists(file.Path)) throw new InvalidDataException("FILE_UNAVAILABLE");
            RuntimeBundle.EnsureOrdinaryAncestors(file.Path);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or BridgeException)
        { throw new InvalidDataException("FILE_UNAVAILABLE"); }
        return new Uri(file.Path);
    }

    public async Task<IReadOnlyList<GrantedFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (items, _) = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            return items.ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<(List<GrantedFile> Items, DocumentFingerprint Fingerprint)> ReadLockedAsync(CancellationToken token)
    {
        var saved = await store.ReadAsync(documentNamespace, token).ConfigureAwait(false);
        try { return (Decode(saved), saved.Fingerprint); }
        finally { if (saved.Data is not null) CryptographicOperations.ZeroMemory(saved.Data); }
    }

    private List<GrantedFile> Decode(ProtectedDocument document)
    {
        if (document.Data is null)
        {
            if (document.Fingerprint != DocumentFingerprint.Missing) throw Corrupt();
            return [];
        }
        if (document.Data.Length == 0 || document.Data.Length > MaximumDocumentBytes ||
            DocumentFingerprint.FromBytes(document.Data) != document.Fingerprint) throw Corrupt();
        try
        {
            using var json = JsonDocument.Parse(document.Data, new JsonDocumentOptions { MaxDepth = 4 });
            CheckJson(json.RootElement);
            RequireFields(json.RootElement, "Schema", "Items");
            var schema = json.RootElement.GetProperty("Schema");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != "1") throw Corrupt();
            var rawItems = json.RootElement.GetProperty("Items");
            if (rawItems.ValueKind != JsonValueKind.Array || rawItems.GetArrayLength() > MaximumItems) throw Corrupt();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<GrantedFile>(rawItems.GetArrayLength());
            foreach (var raw in rawItems.EnumerateArray())
            {
                RequireFields(raw, "Id", "Path", "Title");
                var item = new GrantedFile(Text(raw.GetProperty("Id"), 70),
                    Text(raw.GetProperty("Path"), 32767), Text(raw.GetProperty("Title"), 32767));
                ValidateCurrent(item);
                if (!ids.Add(item.Id)) throw Corrupt();
                result.Add(item);
            }
            return result;
        }
        catch (InvalidDataException) { throw; }
        catch { throw Corrupt(); }
    }

    private async Task WriteLockedAsync(IEnumerable<GrantedFile> values, DocumentFingerprint before, CancellationToken token)
    {
        var items = values.ToArray();
        if (items.Length > MaximumItems) throw new InvalidDataException("FILE_LIMIT");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var array = new JsonArray();
        foreach (var item in items)
        {
            ValidateCurrent(item);
            if (!ids.Add(item.Id)) throw Corrupt();
            array.Add(new JsonObject { ["Id"] = item.Id, ["Path"] = item.Path, ["Title"] = item.Title });
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["Schema"] = 1, ["Items"] = array });
        try
        {
            if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("FILE_LIMIT");
            var expected = DocumentFingerprint.FromBytes(bytes);
            var written = await store.WriteAsync(documentNamespace, bytes, before, token).ConfigureAwait(false);
            if (written != expected) throw new InvalidDataException("COMMIT_UNKNOWN");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
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

    private static void CheckJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Corrupt();
                CheckJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJson(item);
    }

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Corrupt();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
            throw Corrupt();
    }

    private static string Text(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text ||
            text.Length > maximum) throw Corrupt();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 32 || c == 127 || char.IsLowSurrogate(c)) throw Corrupt();
            if (char.IsHighSurrogate(c) && (++i >= text.Length || !char.IsLowSurrogate(text[i]))) throw Corrupt();
        }
        return text;
    }

    private static InvalidDataException Corrupt() => new("CORRUPT_DOCUMENT");
}
