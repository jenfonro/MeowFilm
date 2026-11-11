using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed record FilmPreferences(string[] RuleOrder, string[] PanOrder, string[] Keywords,
    string[] SiteCleanKeywords, bool IosAutoPrepare)
{
    internal static FilmPreferences Defaults => new(["quality", "pan", "keyword"],
        ["yidong", "tianyi", "quark", "uc", "baidu"], [], ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"], true);

    internal static FilmPreferences Read(JsonElement value)
    {
        string[] Values(string name)
        {
            var member = FilmJson.Member(value, name);
            if (member.ValueKind != JsonValueKind.Array || member.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.String))
                throw new FilmPlaybackException("INVALID_RESPONSE");
            return member.EnumerateArray().Select(row => row.GetString()!).ToArray();
        }
        var result = new FilmPreferences(Values("ruleOrder"), Values("panOrder"), Values("keywords"),
            Values("siteCleanKeywords"), FilmJson.Bool(value, "iosAutoPrepare"));
        if (!result.RuleOrder.Order().SequenceEqual(Defaults.RuleOrder.Order()) ||
            !result.PanOrder.Order().SequenceEqual(Defaults.PanOrder.Order()) ||
            FilmJson.Member(value, "iosAutoPrepare").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return result;
    }
}

internal sealed class FilmPlaybackSettings(IRuntimeClient runtime)
{
    private readonly FilmPlaybackClient client = new(runtime);
    internal async Task<FilmPreferences> ReadPreferencesAsync(CancellationToken token) =>
        FilmPreferences.Read(await client.CallAsync("settings.get", new { }, token));
    internal async Task<FilmPreferences> SavePreferencesAsync(FilmPreferences preferences) =>
        FilmPreferences.Read(await client.CallAsync("settings.set", preferences));
    internal Task<JsonElement> ReadLibraryAsync(CancellationToken token) => client.CallAsync("library.get", new { }, token);
    internal Task<JsonElement> SaveManualAsync(JsonElement item) => client.CallAsync("manual.update", new { action = "save", item });
    internal Task<JsonElement> RemoveManualAsync(string id) => client.CallAsync("manual.update", new { action = "remove", id });
    internal Task<JsonElement> RemoveBlockAsync(JsonElement? block) => client.CallAsync("library.remove", new { kind = "blocks", block });
}
