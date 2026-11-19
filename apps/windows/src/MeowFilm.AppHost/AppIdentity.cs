using MeowFilm.Core.Runtime;

namespace MeowFilm.AppHost;

/// <summary>
/// One Windows application. These values are the current persistence and wire
/// identities, not product variants.
/// </summary>
public static class AppIdentity
{
    public const string Name = "meowfilm";
    public const string StorageId = "aggregate";
    public const string RuntimeId = "aggregate";
    public static IReadOnlyList<RuntimeModule> ScriptModules { get; } =
        Array.AsReadOnly(new[]
        {
            new RuntimeModule("music", "0.8.0"),
            new RuntimeModule("cat", "0.2.0"),
            new RuntimeModule("source", "0.3.0"),
            new RuntimeModule("pan", "0.1.0"),
            new RuntimeModule("tmdb", "0.1.0"),
            new RuntimeModule("douban", "0.1.0"),
            new RuntimeModule("film", "0.1.0"),
            new RuntimeModule("netease", "0.1.0"),
            new RuntimeModule("music-platform", "0.1.0")
        });
}
