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
    private static readonly string[] ValidRuleOrder = ["quality", "pan", "keyword"];
    private static readonly string[] ValidPanOrder = ["yidong", "tianyi", "quark", "uc", "baidu"];

    internal static FilmPreferences Defaults => new(ValidRuleOrder.ToArray(),
        ValidPanOrder.ToArray(), [], ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], true);

    internal static string[] NormalizeList(string[]? value)
    {
        if (value is null || value.Any(item => item is null))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        var items = value.Select(item => item.Trim(FilmJson.InputWhitespace)).ToArray();
        if (items.Any(item => item.Length == 0) || items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return items;
    }

    internal static string[] NormalizeRuleOrder(string[]? value)
    {
        var items = NormalizeList(value);
        if (items.Length != ValidRuleOrder.Length ||
            items.Any(item => !ValidRuleOrder.Contains(item, StringComparer.Ordinal)))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return items;
    }

    internal static string[] NormalizePanOrder(string[]? value)
    {
        var items = NormalizeList(value);
        if (items.Any(item => !ValidPanOrder.Contains(item, StringComparer.Ordinal)))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        var result = new List<string>(ValidPanOrder.Length);
        result.AddRange(items);
        foreach (string provider in ValidPanOrder)
            if (!result.Contains(provider, StringComparer.Ordinal)) result.Add(provider);
        return result.ToArray();
    }

    internal static string NormalizeProvider(string? value)
    {
        if (value is not ("tmdb" or "douban"))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return value;
    }

    internal bool ContentEquals(FilmPreferences? other) =>
        other is not null &&
        IosAutoPrepare == other.IosAutoPrepare &&
        string.Equals(MetadataViewProvider, other.MetadataViewProvider, StringComparison.Ordinal) &&
        RuleOrder.AsSpan().SequenceEqual(other.RuleOrder) &&
        PanOrder.AsSpan().SequenceEqual(other.PanOrder) &&
        Keywords.AsSpan().SequenceEqual(other.Keywords) &&
        SiteCleanKeywords.AsSpan().SequenceEqual(other.SiteCleanKeywords);
}

internal sealed class FilmPlaybackSettings(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly FilmPlaybackClient client = new(runtime);

    internal Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) =>
        FilmPreferencesStore.ReadAsync(database, token);

    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences, string? field = null)
    {
        var (saved, changed) = await FilmPreferencesStore.UpdateAsync(database, current => field switch
        {
            "ruleOrder" => current with { RuleOrder = FilmPreferences.NormalizeRuleOrder(preferences.RuleOrder) },
            "panOrder" => current with { PanOrder = FilmPreferences.NormalizePanOrder(preferences.PanOrder) },
            "keywords" => current with { Keywords = FilmPreferences.NormalizeList(preferences.Keywords) },
            "siteCleanKeywords" => current with { SiteCleanKeywords = FilmPreferences.NormalizeList(preferences.SiteCleanKeywords) },
            "iosAutoPrepare" => current with { IosAutoPrepare = preferences.IosAutoPrepare },
            "metadataViewProvider" => current with { MetadataViewProvider = FilmPreferences.NormalizeProvider(preferences.MetadataViewProvider) },
            null => new FilmPreferences(
                FilmPreferences.NormalizeRuleOrder(preferences.RuleOrder),
                FilmPreferences.NormalizePanOrder(preferences.PanOrder),
                FilmPreferences.NormalizeList(preferences.Keywords),
                FilmPreferences.NormalizeList(preferences.SiteCleanKeywords),
                preferences.IosAutoPrepare,
                FilmPreferences.NormalizeProvider(preferences.MetadataViewProvider)),
            _ => throw new FilmPlaybackException("INVALID_PREFERENCES")
        }, CancellationToken.None).ConfigureAwait(false);
        if (changed) (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("film.preferences");
        return saved;
    }
    internal Task<JsonElement> ReadLibraryAsync(CancellationToken token) => client.CallAsync("library.get", new { }, token);
    internal Task<JsonElement> ReadHistoryListAsync(int limit, JsonElement? historyAfter, CancellationToken token) =>
        client.CallAsync("library.get",
            historyAfter is { ValueKind: JsonValueKind.Object } cursor
                ? new { limit, historyAfter = cursor }
                : new { limit },
            token);
    internal Task<JsonElement> ClearHistoryAsync(string? id = null, CancellationToken token = default) =>
        client.CallAsync("library.remove",
            id is null ? new { kind = "history" } : new { kind = "history", id },
            token);
    internal Task<JsonElement> SaveManualAsync(JsonElement item) => client.CallAsync("manual.update", new { action = "save", item });
    internal Task<JsonElement> RemoveManualAsync(string id) => client.CallAsync("manual.update", new { action = "remove", id });
    internal Task<JsonElement> RemoveBlockAsync(JsonElement? block) => client.CallAsync("library.remove", new { kind = "blocks", block });
}
