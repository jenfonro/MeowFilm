using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Platform.Runtime;

public sealed class RuntimeBundle
{
    public string DirectoryPath { get; }
    public string EntryPath { get; }
    public string Product { get; }

    private RuntimeBundle(string directory, string entry, string product)
        => (DirectoryPath, EntryPath, Product) = (directory, entry, product);

    public static RuntimeBundle Verify(string directory, string expectedProduct)
    {
        try
        {
            var root = Path.GetFullPath(directory);
            var manifestPath = Path.Combine(root, "runtime-manifest.json");
            if (!File.Exists(manifestPath)) Invalid();
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions { MaxDepth = 32 });
            var manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1
                || manifest.GetProperty("status").GetString() != "complete"
                || manifest.GetProperty("product").GetString() != expectedProduct) Invalid();
            var entry = manifest.GetProperty("entry").GetString()!;
            if (string.IsNullOrWhiteSpace(entry) || !entry.EndsWith(".mjs", StringComparison.Ordinal)) Invalid();
            var entryPath = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(entryPath)) Invalid();
            return new RuntimeBundle(root, entryPath, expectedProduct);
        }
        catch (BridgeException) { throw; }
        catch { throw new BridgeException("INVALID_MESSAGE"); }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid() => throw new BridgeException("INVALID_MESSAGE");
}
