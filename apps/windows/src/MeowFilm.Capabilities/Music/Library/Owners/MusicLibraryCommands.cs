using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private async Task<(JsonElement Result, bool Mutate)> DispatchAsync(
        string method,
        JsonElement parameters,
        MusicLibrarySession document,
        CancellationToken cancellationToken,
        bool full)
    {
        if (method.StartsWith("music.queue.recommendation.", StringComparison.Ordinal))
            return await RecommendationCommandAsync(method, parameters, document, cancellationToken).ConfigureAwait(false);
        if (method.StartsWith("music.library.network.", StringComparison.Ordinal))
            return NetworkPlaylistCommand(method, parameters, document);
        if (method.StartsWith("music.library.", StringComparison.Ordinal))
            return CollectionCommand(method, parameters, document);
        if (method.StartsWith("music.queue.", StringComparison.Ordinal))
            return QueueCommand(method, parameters, document, full);
        if (method.StartsWith("music.history.", StringComparison.Ordinal))
            return HistoryCommand(method, parameters, document);
        if (method is "music.settings.get" or "music.settings.save")
            return SettingsCommand(method, parameters, document);
        if (method is "music.catalogSource.settings.get" or "music.catalogSource.settings.save")
            return CatalogSourceCommand(method, parameters, document);
        if (method is "music.officialSources.settings.get" or "music.officialSources.settings.save")
            return OfficialSourcesCommand(method, parameters, document, cancellationToken);
        if (method is "music.quality.settings.get" or "music.quality.settings.save")
            return QualityCommand(method, parameters, document, cancellationToken);
        if (method.StartsWith("music.metadata.settings.", StringComparison.Ordinal))
            return MetadataPolicyCommand(method, parameters, document);
        if (method.StartsWith("music.appearance.", StringComparison.Ordinal))
            return AppearanceCommand(method, parameters, document, cancellationToken);
        if (method.StartsWith("music.device.settings.", StringComparison.Ordinal))
            return DeviceSettingsCommand(method, parameters, document);
        if (method.StartsWith("music.lyrics.desktop.settings.", StringComparison.Ordinal))
            return DesktopLyricSettingsCommand(method, parameters, document);
        if (method.StartsWith("music.sourceExclusions.", StringComparison.Ordinal))
            return SourceExclusionsCommand(method, parameters, document);
        if (method.StartsWith("music.search.settings.", StringComparison.Ordinal) ||
            method.StartsWith("music.search.history.", StringComparison.Ordinal))
            return SearchCommand(method, parameters, document);
        if (method.StartsWith("music.lyrics.local.", StringComparison.Ordinal))
            return LocalLyricsCommand(method, parameters, document);
        throw new MusicLibraryException("CAPABILITY_UNAVAILABLE");
    }
}
