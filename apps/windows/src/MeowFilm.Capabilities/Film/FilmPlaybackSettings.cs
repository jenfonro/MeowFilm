using System.Text.Json;
using MeowFilm.Core.Runtime;

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
        if (Original.ValueKind == JsonValueKind.Undefined) return edited;
        return field is null ? FilmDocument.Merge(Original, FilmJson.Encode(Read(Original)), edited)
            : FilmDocument.Set(Original, field, FilmJson.Member(edited, field));
    }
}

internal sealed class FilmPlaybackSettings(IRuntimeClient runtime)
{
    private readonly FilmPlaybackClient client = new(runtime);
    internal async Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) =>
        FilmPreferences.Read(await client.CallAsync("settings.get", new { }, token));
    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences, string? field = null) =>
        FilmPreferences.Read(await client.CallAsync("settings.set", preferences.Wire(field)));
    internal Task<JsonElement> ReadLibraryAsync(CancellationToken token) => client.CallAsync("library.get", new { }, token);
    internal Task<JsonElement> SaveManualAsync(JsonElement item) => client.CallAsync("manual.update", new { action = "save", item });
    internal Task<JsonElement> RemoveManualAsync(string id) => client.CallAsync("manual.update", new { action = "remove", id });
    internal Task<JsonElement> RemoveBlockAsync(JsonElement? block) => client.CallAsync("library.remove", new { kind = "blocks", block });
}
