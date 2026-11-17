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
            new RuntimeModule("music", "0.8.0") { RequiredMethods =
            [
                "music.script.sources.list", "music.script.sources.detail", "music.script.sources.add",
                "music.script.sources.download", "music.script.sources.edit", "music.script.sources.update",
                "music.script.sources.rollback", "music.script.sources.enable", "music.script.sources.disable",
                "music.script.sources.remove", "music.script.sources.check", "music.script.resolve", "music.script.invoke"
            ] },
            new RuntimeModule("cat", "0.2.0") { RequiredMethods =
            [
                "cat.script.start", "cat.script.status", "cat.script.update", "cat.script.restart", "cat.script.invoke"
            ] },
            new RuntimeModule("source", "0.3.0") { RequiredMethods =
            [
                "source.runtime.library.apply", "source.runtime.library.invoke", "source.runtime.library.image",
                "source.runtime.library.releaseImage", "source.runtime.web.pending", "source.runtime.web.respond",
                "source.runtime.rules.process"
            ] },
            new RuntimeModule("pan", "0.1.0") { RequiredMethods =
            [
                "pan.login.start", "pan.login.poll", "pan.login.cancel", "pan.refresh"
            ] },
            new RuntimeModule("tmdb", "0.1.0"),
            new RuntimeModule("douban", "0.1.0"),
            new RuntimeModule("film", "0.1.0") { RequiredMethods =
            [
                "film.session.open", "film.session.command", "film.session.snapshot", "film.session.release",
                "film.session.attempt", "film.session.media.release", "film.library.get", "film.library.remove",
                "film.manual.update", "film.data.metadata.entry", "film.data.invalidate", "film.rules.preview",
                "film.search.open", "film.search.snapshot", "film.search.command", "film.search.release", "film.search.handoff"
            ] },
            new RuntimeModule("netease", "0.1.0"),
            new RuntimeModule("music-platform", "0.1.0") { RequiredMethods =
            [
                "music.platform.request", "music.platform.session.update", "music.platform.cache.clear"
            ] }
        });
    /// <summary>Trusted native content collections. Music sources are stored by the shared runtime module.</summary>
    public static IReadOnlyList<string> NativeCollections { get; } = Array.AsReadOnly(Array.Empty<string>());
    public static IReadOnlyList<string> ProtectedNamespaces { get; } =
        Array.AsReadOnly(new[] { "music.accounts.v2", "pan.accounts.v1", "film.tmdb.v1", "film.douban.v1", "film.cat-sources.v1" });
    public static IReadOnlyList<string> NativeNamespaces { get; } = Array.AsReadOnly(new[]
    {
        "app.appearance.v1", "app.window.v1", "app.media-controls.v1", "music.library.v2", "music.files.v2", "music.accounts.v2", "music.feedback.v2",
        "music.playback-outbox.v2",
        "music.fm-selection.v2", "film.video-sources.v1", "film.library.v1", "film.cat-sources.v1",
        "pan.accounts.v1", "film.tmdb.v1", "film.douban.v1", "film.rules.v1", "film.preferences.v1",
        "novel.library.v1", "novel.sources.v1"
    });
    public static IReadOnlyDictionary<string, string> RuntimeSettingsNamespaces { get; } = new Dictionary<string, string>
    {
        ["cat.sources"] = "film.cat-sources.v1", ["pan.accounts"] = "pan.accounts.v1",
        ["tmdb"] = "film.tmdb.v1", ["douban"] = "film.douban.v1",
        ["film.rules"] = "film.rules.v1", ["film.preferences"] = "film.preferences.v1"
    };
    public static IReadOnlyList<string> CacheNamespaces { get; } = Array.AsReadOnly(new[]
    {
        "music.presentation-resources.v1", "music.account-details.v2", "music.playback-sources.v2",
        "music.playback-files.v2", "music.chorus.v2", "music.source-discovery.v1", "music.home.v2"
    });
    public static IReadOnlyList<string> CacheProtectedNamespaces { get; } =
        Array.AsReadOnly(new[] { "music.account-details.v2" });
}
