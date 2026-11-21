namespace MeowFilm.Core.Debug;

public static class DebugCategories
{
    public const string Lifecycle = "lifecycle";
    public const string UiNav = "ui-nav";
    public const string UiAction = "ui-action";
    public const string RuntimeBridge = "runtime-bridge";
    public const string RuntimeNode = "runtime-node";
    public const string MusicPlatform = "music-platform";
    public const string MusicAccount = "music-account";
    public const string MusicCatalog = "music-catalog";
    public const string MusicLibrary = "music-library";
    public const string MusicPlayback = "music-playback";
    public const string FilmCatalog = "film-catalog";
    public const string FilmPlayback = "film-playback";
    public const string Novel = "novel";
    public const string Search = "search";
    public const string Storage = "storage";
    public const string Diagnostics = "diagnostics";

    public static IReadOnlyList<string> All { get; } =
    [
        Lifecycle,
        UiNav,
        UiAction,
        RuntimeBridge,
        RuntimeNode,
        MusicPlatform,
        MusicAccount,
        MusicCatalog,
        MusicLibrary,
        MusicPlayback,
        FilmCatalog,
        FilmPlayback,
        Novel,
        Search,
        Storage,
        Diagnostics
    ];

    private static readonly HashSet<string> ValidSet = new(All, StringComparer.Ordinal);

    public static bool IsValid(string? category) =>
        category is not null && ValidSet.Contains(category);

    public static (IReadOnlyList<string> Modules, string? Error) ParseModules(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return (["all"], null);
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (["all"], null);
        var result = new List<string>(parts.Length);
        bool hasAll = false;
        foreach (var raw in parts)
        {
            var name = raw.ToLowerInvariant();
            if (name == "all") { hasAll = true; continue; }
            if (!ValidSet.Contains(name))
                return ([], $"Unknown debug log category: '{raw}'.");
            if (!result.Contains(name, StringComparer.Ordinal))
                result.Add(name);
        }
        return (hasAll ? ["all"] : result, null);
    }
}
