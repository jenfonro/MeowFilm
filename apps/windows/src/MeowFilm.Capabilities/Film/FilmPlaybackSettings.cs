using System.Text.Json;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record FilmPreferences(string[] RuleOrder, string[] PanOrder, string[] Keywords,
    string[] SiteCleanKeywords, bool IosAutoPrepare)
{
    private JsonElement Original { get; init; }
    internal static FilmPreferences Defaults => new(["quality", "pan", "keyword"],
        ["yidong", "tianyi", "quark", "uc", "baidu"], [], ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], true);

    internal static FilmPreferences Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FilmPlaybackException("INVALID_RESPONSE");
        string[] Values(string name)
        {
            return FilmJson.Array(value, name).Where(row => row.ValueKind == JsonValueKind.String)
                .Select(row => row.GetString()!).ToArray();
        }
        return new(Values("ruleOrder"), Values("panOrder"), Values("keywords"),
            Values("siteCleanKeywords"), FilmJson.Bool(value, "iosAutoPrepare")) { Original = value.Clone() };
    }

    internal JsonElement Wire(string? field)
    {
        var edited = FilmJson.Encode(this);
        var before = Original.ValueKind == JsonValueKind.Undefined ? default : FilmJson.Encode(Read(Original));
        var next = Original.ValueKind == JsonValueKind.Undefined ? edited : Original;
        // Detect edits before normalization so untouched raw fields retain their original shape.
        foreach (string name in new[] { "ruleOrder", "panOrder", "keywords", "siteCleanKeywords", "iosAutoPrepare" })
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
    internal async Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) =>
        (await document.ReadAsync(token)) is { } saved ? FilmPreferences.Read(saved.GetProperty("preferences")) : FilmPreferences.Defaults;
    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences, string? field = null)
    {
        var saved = await document.UpdateAsync(current =>
        {
            var before = current?.GetProperty("preferences") ?? FilmPreferences.Defaults.Wire(null);
            var next = field is null ? preferences.Wire(null) : FilmDocument.Set(before, field, FilmJson.Member(preferences.Wire(field), field));
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
