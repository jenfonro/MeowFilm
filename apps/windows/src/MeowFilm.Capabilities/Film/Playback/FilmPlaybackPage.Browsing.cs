namespace MeowFilm.Capabilities;

public sealed partial class FilmPlaybackPage
{
    private long metadataChoiceGeneration;

    internal async Task BrowseSourceAsync(FilmSelection value)
    {
        if (!Sources.Contains(value)) throw new FilmPlaybackException("INVALID_REQUEST");
        if (value.Kind is "tmdb" or "douban" && preferences is not null)
        {
            long generation = ++metadataChoiceGeneration;
            await preferences.SavePreferencesAsync(
                FilmPreferences.Defaults with { MetadataViewProvider = value.Kind },
                "metadataViewProvider");
            if (generation != metadataChoiceGeneration || !Sources.Contains(value)) return;
        }
        await CommandAsync("browse.source", new { sourceId = value.Id });
    }

    internal Task BrowseMetadataMappingAsync(FilmSelection value)
    {
        if (!MetadataSources.Contains(value) || value.Kind is not ("tmdb" or "douban"))
            throw new FilmPlaybackException("INVALID_REQUEST");
        return BrowseSourceAsync(value);
    }

    internal Task BrowseLineAsync(FilmSelection value)
    {
        if (!Lines.Contains(value)) throw new FilmPlaybackException("INVALID_REQUEST");
        return CommandAsync("browse.line", new { lineId = value.Id });
    }

    internal Task BrowseSeasonAsync(FilmSelection value)
    {
        if (!Seasons.Contains(value) || value.Season is not { } season)
            return Task.CompletedTask;
        return CommandAsync("browse.season", new { season });
    }
}
