using System.IO;
using MeowFilm.Core.Debug;

namespace MeowFilm.AppHost;

public sealed record AppDataPaths(string Documents)
{
    public string ScriptRuntime => Documents;
    public string Caches => Path.Combine(Documents, "resources-v1");
    public string Temp => Path.Combine(Documents, "tmp");
    public static AppDataPaths Current => ForIdentity(AppIdentity.StorageId);

    public void SweepTempDirectory()
    {
        try
        {
            if (Directory.Exists(Temp))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(Temp))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                    catch { }
                }
            }
            else
            {
                Directory.CreateDirectory(Temp);
            }
        }
        catch { }
    }

    public static AppDataPaths Resolve(DebugLaunchOptions? debugOptions) =>
        debugOptions is { Enabled: true, DataRoot: { Length: > 0 } customRoot }
            ? new AppDataPaths(customRoot)
            : Current;

    public static string DefaultDebugRoot(string identity) =>
        // Sibling of %LOCALAPPDATA%\meowfilm\<identity> so UserDataDirectory.Clear() never deletes active debug logs.
        ResolveRoot(identity) + "-debug";

    /// <summary>Fixed App-identity LocalAppData location, never a caller-supplied user path.</summary>
    public static AppDataPaths ForIdentity(string identity) => new(ResolveRoot(identity));

    private static string ResolveRoot(string identity)
    {
        if (identity is not { Length: > 0 and <= 64 } || !char.IsAsciiLetterOrDigit(identity[0]) ||
            identity.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("INVALID_PRODUCT_ID", nameof(identity));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(local)) throw new InvalidOperationException("PRODUCT_DATA_UNAVAILABLE");
        return Path.Combine(local, AppIdentity.Name, identity);
    }
}
