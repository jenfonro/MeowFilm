using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record FilmPreferences(
    string[] RuleOrder,
    string[] PanOrder,
    string[] Keywords,
    string[] SiteCleanKeywords,
    bool IosAutoPrepare,
    string MetadataViewProvider = "tmdb")
{
    internal static FilmPreferences Defaults => new(["quality", "pan", "keyword"],
        ["yidong", "tianyi", "quark", "uc", "baidu"], [], ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], true);

    private static readonly char[] InputWhitespace =
        "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();

    internal static string[] NormalizeList(string[]? value)
    {
        if (value is null || value.Any(item => item is null))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        var items = value.Select(item => item.Trim(InputWhitespace)).ToArray();
        if (items.Any(item => item.Length == 0) || items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return items;
    }

    internal static string NormalizeProvider(string? value)
    {
        if (value is not ("tmdb" or "douban"))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return value;
    }
}

internal sealed class FilmPlaybackSettings(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly FilmPlaybackClient client = new(runtime);

    internal Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) =>
        FilmPreferencesStore.ReadAsync(database, token);

    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences, string? field = null)
    {
        var saved = await FilmPreferencesStore.UpdateAsync(database, current => field switch
        {
            "ruleOrder" => current with { RuleOrder = FilmPreferences.NormalizeList(preferences.RuleOrder) },
            "panOrder" => current with { PanOrder = FilmPreferences.NormalizeList(preferences.PanOrder) },
            "keywords" => current with { Keywords = FilmPreferences.NormalizeList(preferences.Keywords) },
            "siteCleanKeywords" => current with { SiteCleanKeywords = FilmPreferences.NormalizeList(preferences.SiteCleanKeywords) },
            "iosAutoPrepare" => current with { IosAutoPrepare = preferences.IosAutoPrepare },
            "metadataViewProvider" => current with { MetadataViewProvider = FilmPreferences.NormalizeProvider(preferences.MetadataViewProvider) },
            null => new FilmPreferences(
                FilmPreferences.NormalizeList(preferences.RuleOrder),
                FilmPreferences.NormalizeList(preferences.PanOrder),
                FilmPreferences.NormalizeList(preferences.Keywords),
                FilmPreferences.NormalizeList(preferences.SiteCleanKeywords),
                preferences.IosAutoPrepare,
                FilmPreferences.NormalizeProvider(preferences.MetadataViewProvider)),
            _ => throw new FilmPlaybackException("INVALID_PREFERENCES")
        }, CancellationToken.None).ConfigureAwait(false);
        (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("film.preferences");
        return saved;
    }
    internal Task<JsonElement> ReadLibraryAsync(CancellationToken token) => client.CallAsync("library.get", new { }, token);
    internal Task<JsonElement> SaveManualAsync(JsonElement item) => client.CallAsync("manual.update", new { action = "save", item });
    internal Task<JsonElement> RemoveManualAsync(string id) => client.CallAsync("manual.update", new { action = "remove", id });
    internal Task<JsonElement> RemoveBlockAsync(JsonElement? block) => client.CallAsync("library.remove", new { kind = "blocks", block });
}
