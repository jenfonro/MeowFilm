using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Platform.Runtime;

public sealed partial class RuntimeBundle
{
    public string DirectoryPath { get; }
    public string EntryPath { get; }
    public string Product { get; }

    private RuntimeBundle(string directory, string entry, string product)
        => (DirectoryPath, EntryPath, Product) = (directory, entry, product);

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    public static RuntimeBundle Verify(string directory, string expectedProduct, IReadOnlyList<RuntimeModule> expectedModules)
    {
        try
        {
            var root = Path.GetFullPath(directory);
            EnsureOrdinaryAncestors(root);
            var manifestPath = Path.Combine(root, "runtime-manifest.json");
            EnsureOrdinaryAncestors(manifestPath);
            var info = new FileInfo(manifestPath);
            if (!info.Exists || info.Length is 0 or > 4 * 1024 * 1024) Invalid();
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions { MaxDepth = 32 });
            var manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1
                || manifest.GetProperty("status").GetString() != "complete"
                || manifest.GetProperty("product").GetString() != expectedProduct
                || manifest.GetProperty("transport").GetString() != "windows-named-pipe-v1") Invalid();
            var modules = manifest.GetProperty("modules").EnumerateArray().Select(item => item.GetString()).ToArray();
            if (modules.Length != expectedModules.Count || modules.Distinct(StringComparer.Ordinal).Count() != modules.Length
                || !modules.ToHashSet(StringComparer.Ordinal).SetEquals(expectedModules.Select(item => item.Id))) Invalid();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("path").GetString()!;
                var hash = file.GetProperty("sha256").GetString()!;
                var length = file.GetProperty("bytes").GetInt64();
                if (!names.Add(name.Normalize()) || !HashPattern().IsMatch(hash) || length < 0) Invalid();
                var path = SafePath(root, name);
                EnsureOrdinaryAncestors(path);
                var actual = new FileInfo(path);
                if (!actual.Exists || actual.Length != length) Invalid();
                using var stream = actual.OpenRead();
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), hash, StringComparison.Ordinal)) Invalid();
            }
            var entry = manifest.GetProperty("entry").GetString()!;
            if (!names.Contains(entry) || !entry.EndsWith(".mjs", StringComparison.Ordinal)) Invalid();
            return new RuntimeBundle(root, SafePath(root, entry), expectedProduct);
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(part => part is "" or "." or ".."
                || part.Any(char.IsControl) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || part.EndsWith(' ') || part.EndsWith('.'))) Invalid();
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Invalid();
        return path;
    }

    public static void EnsureOrdinaryAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) Invalid();
            if (Path.GetPathRoot(current) == current) break;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new BridgeException("INVALID_MESSAGE");
}
