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
        Array.AsReadOnly(new[] { new RuntimeModule("music", "0.7.0"), new RuntimeModule("cat", "0.1.0") });
    public static IReadOnlyList<string> NativeCollections { get; } =
        Array.AsReadOnly(new[] { "music.script-sources.v1" });
    public static IReadOnlyList<string> NativeNamespaces { get; } = Array.AsReadOnly(new[]
    {
        "music.library.v1", "music.files.v1", "music.accounts.v1", "music.feedback.v1",
        "music.playback-sources.v1", "music.playback-files.v1", "music.chorus.v1", "music.platform-context.v1",
        "music.song-matches.v1"
    });
}
