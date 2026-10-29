using System.IO;
using System.Runtime.CompilerServices;

namespace MeowFilm.Windows.Tests;

/// <summary>Developer/test paths only. Never used by a product data owner.</summary>
internal static class TestWorkspace
{
    // Bound to the source that was actually compiled, not a shell, environment
    // override, another checkout with a similar marker, or the process cwd.
    internal static readonly string SourceRoot = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(SourceFile())!, "..", "..", "..", ".."));
    internal static readonly string BuildRoot = Path.Combine(SourceRoot, "build", "windows");
    internal static readonly string TmpRoot = Path.Combine(SourceRoot, "tmp");
    internal static string ResultsRoot => Path.Combine(TmpRoot, "test-results");
    internal static string DataRoot => ResolveDataRoot(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT"));

    private static string SourceFile([CallerFilePath] string path = "") => path;

    internal static string ResolveDataRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException("An explicit absolute Windows test-data root is required.");
        return ValidatePath(ResultsRoot, value);
    }

    internal static string DataPath(params string[] components) =>
        ValidatePath(DataRoot, Path.Combine([DataRoot, .. components]));

    internal static string ValidatePath(string root, string path, bool allowRoot = false)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Test paths must be absolute.");
        string owner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!(allowRoot && string.Equals(owner, full, StringComparison.OrdinalIgnoreCase)) &&
            !full.StartsWith(owner + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test path leaves its explicit Windows owner.");
        RejectReparseAncestors(full);
        return full;
    }

    internal static void RejectReparseAncestors(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Test paths cannot traverse links or junctions.");
        }
    }

    // The Windows test entry passes the developer's Node from PATH.
    internal static string NodePath
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("MEOWFILM_TEST_NODE");
            if (value is null || !Path.IsPathFullyQualified(value) || !File.Exists(value))
                throw new InvalidOperationException("An explicit absolute Node executable is required for Windows tests.");
            string node = Path.GetFullPath(value);
            ValidateOptionalExact("MEOW_FILM_TEST_NODE", node);
            return node;
        }
    }

    private static void ValidateOptionalExact(string name, string expected)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is not null && (!Path.IsPathFullyQualified(value) ||
            !string.Equals(Path.GetFullPath(value), expected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Foreign Windows test environment: " + name);
    }

    internal static void Initialize()
    {
        RejectReparseAncestors(SourceRoot);
        RejectReparseAncestors(BuildRoot);
        RejectReparseAncestors(TmpRoot);
        if (!File.Exists(Path.Combine(SourceRoot, "apps", "windows", "global.json")))
            throw new InvalidOperationException("The compiled Windows source checkout is not available.");
        ValidateOptionalExact("MEOW_FILM_TEST_ROOT", SourceRoot);
        if (!Directory.Exists(DataRoot)) throw new InvalidOperationException("The explicit test-data root must already exist.");
        Environment.SetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT", DataRoot);
        string node = NodePath;
        Environment.SetEnvironmentVariable("MEOW_FILM_TEST_ROOT", SourceRoot);
        Environment.SetEnvironmentVariable("MEOWFILM_TEST_NODE", node);
        Environment.SetEnvironmentVariable("MEOW_FILM_TEST_NODE", node);
        foreach (string name in new[] { "MEOWFILM_TEST_RUNTIME_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                Environment.SetEnvironmentVariable(name, ValidatePath(ResultsRoot, value));
        foreach (string name in new[] { "MEOWFILM_SELECTED_FILES_TEST_ROOT",
            "MEOW_FILM_COMPAT_TEST_DATA", "MEOWFILM_NATIVE_RECOMMENDATIONS_DATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value)
                Environment.SetEnvironmentVariable(name, ValidatePath(DataRoot, value, allowRoot: true));
        if (Environment.GetEnvironmentVariable("MEOWFILM_TEST_VLC_ROOT") is { } vlc)
            ValidatePath(AppContext.BaseDirectory, vlc);
        string temporary = DataPath("process-temp");
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEMP", temporary);
        Environment.SetEnvironmentVariable("TMP", temporary);
    }
}
