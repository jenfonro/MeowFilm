using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private async Task<(JsonObject Result, bool Mutate)> DispatchAsync(
        string method,
        JsonObject parameters,
        JsonObject document,
        CancellationToken cancellationToken,
        bool full)
    {
        if (method.StartsWith("music.queue.recommendation.", StringComparison.Ordinal))
            return await RecommendationCommandAsync(method, parameters, document, cancellationToken).ConfigureAwait(false);
        if (method.StartsWith("music.library.", StringComparison.Ordinal))
            return CollectionCommand(method, parameters, document, full);
        if (method.StartsWith("music.queue.", StringComparison.Ordinal))
            return QueueCommand(method, parameters, document, full);
        if (method.StartsWith("music.history.", StringComparison.Ordinal))
            return HistoryCommand(method, parameters, document);
        if (method is "music.settings.get" or "music.settings.save")
            return SettingsCommand(method, parameters, document);
        if (method is "music.quality.settings.get" or "music.quality.settings.save")
            return await QualityCommandAsync(method, parameters, document, cancellationToken).ConfigureAwait(false);
        if (method.StartsWith("music.metadata.settings.", StringComparison.Ordinal))
            return MetadataPolicyCommand(method, parameters, document);
        if (method.StartsWith("music.appearance.", StringComparison.Ordinal))
            return await AppearanceCommand(method, parameters, document, cancellationToken).ConfigureAwait(false);
        if (method.StartsWith("music.device.settings.", StringComparison.Ordinal))
            return DeviceSettingsCommand(method, parameters, document);
        if (method.StartsWith("music.search.settings.", StringComparison.Ordinal) ||
            method.StartsWith("music.search.history.", StringComparison.Ordinal))
            return SearchCommand(method, parameters, document);
        if (method.StartsWith("music.lyrics.local.", StringComparison.Ordinal))
            return LocalLyricsCommand(method, parameters, document);
        if (method.StartsWith("music.lyrics.display.", StringComparison.Ordinal))
            return LyricDisplayCommand(method, parameters, document);
        if (method == "music.online.song")
        {
            LJ.Fields(parameters, "platform musicInfo title artist album duration", "platform musicInfo title");
            string platform = LJ.Text(parameters["platform"]);
            LJ.Require(NativeMusicSong.Platforms.Contains(platform));
            var info = (JsonObject)LJ.Object(parameters["musicInfo"]).DeepClone();
            var song = NativeMusicSong.CreateOnline(platform, info,
                LJ.Text(parameters["title"], 512), LJ.S(parameters["artist"]),
                LJ.S(parameters["album"]), LJ.N(parameters["duration"]));
            return (LJ.Obj(("song", NativeMusicSong.Normalize(song))), false);
        }
        throw new MusicLibraryException("CAPABILITY_UNAVAILABLE");
    }
}
