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
}
