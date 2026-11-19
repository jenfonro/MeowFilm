using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record FilmPreferences(string[] RuleOrder, string[] PanOrder, string[] Keywords,
    string[] SiteCleanKeywords, bool IosAutoPrepare, string MetadataViewProvider = "tmdb")
{
    private JsonElement Original { get; init; }
    internal static FilmPreferences Defaults => new(["quality", "pan", "keyword"],
        ["yidong", "tianyi", "quark", "uc", "baidu"], [], ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], true);

    internal static FilmPreferences Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FilmPlaybackException("INVALID_RESPONSE");
        string[] Values(string name)
        {
            var items = FilmJson.Member(value, name);
            if (items.ValueKind != JsonValueKind.Array || items.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.String))
                throw new FilmPlaybackException("INVALID_RESPONSE");
            return items.EnumerateArray().Select(row => row.GetString()!).ToArray();
        }
        if (value.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal).SetEquals(
                ["ruleOrder", "panOrder", "keywords", "siteCleanKeywords", "iosAutoPrepare", "metadataViewProvider"]) is false ||
            FilmJson.Member(value, "iosAutoPrepare").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        string provider = FilmJson.RequiredText(value, "metadataViewProvider");
        if (provider is not ("tmdb" or "douban")) throw new FilmPlaybackException("INVALID_RESPONSE");
        return new(Values("ruleOrder"), Values("panOrder"), Values("keywords"),
            Values("siteCleanKeywords"), FilmJson.Bool(value, "iosAutoPrepare"), provider) { Original = value.Clone() };
    }

    internal JsonElement Wire(string? field)
    {
        var edited = FilmJson.Encode(this);
        var before = Original.ValueKind == JsonValueKind.Undefined ? default : FilmJson.Encode(Read(Original));
        var next = Original.ValueKind == JsonValueKind.Undefined ? edited : Original;
        // Detect edits before normalization so untouched raw fields retain their original shape.
        foreach (string name in new[] { "ruleOrder", "panOrder", "keywords", "siteCleanKeywords", "iosAutoPrepare", "metadataViewProvider" })
        {
            var value = FilmJson.Member(edited, name);
            if (field is not null ? name != field :
                before.ValueKind != JsonValueKind.Undefined && FilmJson.Equal(FilmJson.Member(before, name), value)) continue;
            next = FilmDocument.Set(next, name, Normalize(name, value));
        }
        return next;
    }

    private static readonly char[] InputWhitespace =
        "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
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
        var items = value.EnumerateArray().Select(item => item.GetString()!.Trim(InputWhitespace)).ToArray();
        if (items.Any(item => item.Length == 0) || items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new FilmPlaybackException("INVALID_PREFERENCES");
        return FilmJson.Encode(items);
    }
}

internal sealed class FilmPlaybackSettings(IRuntimeClient runtime, IStorageDatabase database)
{
    private readonly FilmPlaybackClient client = new(runtime);
    private readonly SettingsDocument document = new(new(database, "film.preferences.v1", FilmStorageRecords.Preferences));
    private static FilmPreferences Read(JsonElement? saved)
    {
        if (saved is null) return FilmPreferences.Defaults;
        try
        {
            var value = saved.Value;
            if (value.EnumerateObject().Count() != 2 || !value.TryGetProperty("version", out var version) ||
                !version.TryGetInt32(out int number) || number != 1 || !value.TryGetProperty("preferences", out var preferences))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
            return FilmPreferences.Read(preferences);
        }
        catch (Exception error) when (error is FilmPlaybackException or InvalidOperationException or FormatException)
        { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
    }
    internal async Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) => Read(await document.ReadAsync(token));
    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences, string? field = null)
    {
        var edited = preferences.Wire(field);
        var saved = await document.UpdateAsync(current =>
        {
            var before = Read(current).Wire(null);
            var next = field is null ? edited : FilmDocument.Set(before, field, FilmJson.Member(edited, field));
            return FilmDocument.Set(current ?? FilmJson.Encode(new { version = 1 }), "preferences", next);
        });
        (runtime as IRuntimeSettingsNotifications)?.SettingsChanged("film.preferences");
        return FilmPreferences.Read(saved.GetProperty("preferences"));
    }
    internal Task<JsonElement> ReadLibraryAsync(CancellationToken token) => client.CallAsync("library.get", new { }, token);
    internal Task<JsonElement> SaveManualAsync(JsonElement item) => client.CallAsync("manual.update", new { action = "save", item });
    internal Task<JsonElement> RemoveManualAsync(string id) => client.CallAsync("manual.update", new { action = "remove", id });
    internal Task<JsonElement> RemoveBlockAsync(JsonElement? block) => client.CallAsync("library.remove", new { kind = "blocks", block });
}
