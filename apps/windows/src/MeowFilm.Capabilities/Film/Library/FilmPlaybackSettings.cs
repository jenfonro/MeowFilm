using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record FilmPreferences
{
    private JsonElement values;
    private JsonElement Original { get; init; }
    internal FilmPreferences(string[] RuleOrder, string[] PanOrder, string[] Keywords,
        string[] SiteCleanKeywords, bool IosAutoPrepare, string MetadataViewProvider = "tmdb")
    {
        values = FilmJson.Encode(new { RuleOrder, PanOrder, Keywords, SiteCleanKeywords, IosAutoPrepare, MetadataViewProvider });
    }
    private FilmPreferences(JsonElement value) { values = Original = value.Clone(); }
    public string[] RuleOrder { get => Values("ruleOrder"); init => Set("ruleOrder", value); }
    public string[] PanOrder { get => Values("panOrder"); init => Set("panOrder", value); }
    public string[] Keywords { get => Values("keywords"); init => Set("keywords", value); }
    public string[] SiteCleanKeywords { get => Values("siteCleanKeywords"); init => Set("siteCleanKeywords", value); }
    public bool IosAutoPrepare
    {
        get => FilmJson.Member(values, "iosAutoPrepare").ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new FilmPlaybackException("INVALID_RESPONSE")
        };
        init => Set("iosAutoPrepare", value);
    }
    public string MetadataViewProvider
    {
        get
        {
            string provider = FilmJson.RequiredText(values, "metadataViewProvider");
            return provider is "tmdb" or "douban" ? provider : throw new FilmPlaybackException("INVALID_RESPONSE");
        }
        init => Set("metadataViewProvider", value);
    }
    private void Set(string name, object? value) => values = SettingsDocument.SetMember(values, name, JsonSerializer.SerializeToElement(value, FilmJson.Options));
    private string[] Values(string name)
    {
        var items = FilmJson.Member(values, name);
        if (items.ValueKind != JsonValueKind.Array || items.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.String))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return items.EnumerateArray().Select(row => row.GetString()!).ToArray();
    }
    internal static FilmPreferences Defaults => new(["quality", "pan", "keyword"],
        ["yidong", "tianyi", "quark", "uc", "baidu"], [], ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], true);

    internal static FilmPreferences Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FilmPlaybackException("INVALID_RESPONSE");
        return new(value);
    }

    internal IReadOnlyDictionary<string, JsonElement> Edits(string? field)
    {
        var edits = new Dictionary<string, JsonElement>();
        foreach (string name in new[] { "ruleOrder", "panOrder", "keywords", "siteCleanKeywords", "iosAutoPrepare", "metadataViewProvider" })
        {
            var value = FilmJson.Member(values, name);
            if (field is not null ? name != field : Original.ValueKind != JsonValueKind.Undefined &&
                FilmJson.Equal(FilmJson.Member(Original, name), value)) continue;
            edits.Add(name, Normalize(name, value));
        }
        return edits;
    }

    private static JsonElement Normalize(string field, JsonElement value)
    {
        if (field == "iosAutoPrepare") return value;
        if (field == "metadataViewProvider")
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("tmdb" or "douban"))
                throw new FilmPlaybackException("INVALID_PREFERENCES");
            return value;
        }
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        var items = value.EnumerateArray().Select(item => item.GetString()!.Trim(FilmJson.InputWhitespace)).ToArray();
        if (items.Any(item => item.Length == 0) || items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return FilmJson.Encode(items);
    }
}

internal sealed class FilmPlaybackSettings(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly FilmPlaybackClient client = new(runtime);
    private readonly SettingsDocument document = new(new(database, "film.preferences.v1", FilmStorageRecords.Preferences));
    private static JsonElement Read(JsonElement? saved)
    {
        if (saved is null) return FilmJson.Encode(FilmPreferences.Defaults);
        var value = saved.Value;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1 ||
            !value.TryGetProperty("preferences", out var preferences) || preferences.ValueKind != JsonValueKind.Object)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
        return preferences;
    }
    internal async Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) => FilmPreferences.Read(Read(await document.ReadAsync(token)));
    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences, string? field = null)
    {
        var edits = preferences.Edits(field);
        var saved = await document.UpdateAsync(current =>
        {
            var next = Read(current);
            foreach (var (name, value) in edits) next = SettingsDocument.SetMember(next, name, value);
            return SettingsDocument.SetMember(current ?? FilmJson.Encode(new { version = 1 }), "preferences", next);
        });
        (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("film.preferences");
        return FilmPreferences.Read(saved.GetProperty("preferences"));
    }
    internal Task<JsonElement> ReadLibraryAsync(CancellationToken token) => client.CallAsync("library.get", new { }, token);
    internal Task<JsonElement> SaveManualAsync(JsonElement item) => client.CallAsync("manual.update", new { action = "save", item });
    internal Task<JsonElement> RemoveManualAsync(string id) => client.CallAsync("manual.update", new { action = "remove", id });
    internal Task<JsonElement> RemoveBlockAsync(JsonElement? block) => client.CallAsync("library.remove", new { kind = "blocks", block });
}
