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
            new RuntimeModule("netease", "0.1.0")
        });
    /// <summary>Trusted native content collections. Music sources are stored by the shared runtime module.</summary>
    public static IReadOnlyList<string> NativeCollections { get; } = Array.AsReadOnly(Array.Empty<string>());
    public static IReadOnlyList<string> ProtectedNamespaces { get; } =
        Array.AsReadOnly(new[] { "music.accounts.v1", "music.platform-context.v1" });
    public static IReadOnlyList<string> NativeNamespaces { get; } = Array.AsReadOnly(new[]
    {
        "app.appearance.v1", "app.window.v1", "app.media-controls.v1", "music.library.v1", "music.files.v1", "music.accounts.v1", "music.feedback.v1",
        "music.playback-outbox.v1", "music.platform-context.v1",
        "music.fm-selection.v1", "film.video-sources.v1", "film.library.v1",
        "novel.library.v1"
    });
    public static IReadOnlyList<string> CacheNamespaces { get; } = Array.AsReadOnly(new[]
    {
        "music.lyric-display.v1", "music.account-details.v1", "music.playback-sources.v1",
        "music.playback-files.v1", "music.chorus.v1", "music.song-matches.v1", "music.home.v1"
    });
    public static IReadOnlyList<string> CacheProtectedNamespaces { get; } =
        Array.AsReadOnly(new[] { "music.account-details.v1" });
}
