using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSearchSettings
{
    internal const int MaxCacheMinutes = 1440;
    internal static bool ValidPages(int value) => value >= 1;
    internal static bool ValidConcurrency(int value) => value >= 1;
    internal static bool ValidCacheMinutes(int value) => value is >= 1 and <= MaxCacheMinutes;
    internal static int Pages(JsonNode? value) => (int)LJ.I(value, 1, int.MaxValue);
    internal static int Concurrency(JsonNode? value) => (int)LJ.I(value, 1, int.MaxValue);
    internal static int CacheMinutes(JsonNode? value) => (int)LJ.I(value, 1, MaxCacheMinutes);
    private static int Read(JsonElement value, string key, Func<int, bool> valid)
    {
        long number = MusicDto.Integer(value, key);
        MusicDto.Require(number is >= 1 and <= int.MaxValue && valid((int)number));
        return (int)number;
    }
    internal static int Pages(JsonElement value, string key) => Read(value, key, ValidPages);
    internal static int Concurrency(JsonElement value, string key) => Read(value, key, ValidConcurrency);
    internal static int CacheMinutes(JsonElement value, string key) => Read(value, key, ValidCacheMinutes);
}

public sealed partial class MusicLibraryOwner
{
    private const string SearchPolicyArea = "native-search-policy", SearchHistoryArea = "native-search-history";
    internal static readonly string[] SearchPlatforms = ["wy", "tx", "kw", "kg", "mg"];

    private static JsonObject EmptySearchKind() =>
        LJ.Obj(("order", LJ.Arr(SearchPlatforms)), ("enabled", LJ.Arr(SearchPlatforms)),
            ("pages", 1), ("concurrency", 1), ("cacheMinutes", 60));

    private JsonObject SearchPolicy(MusicLibraryData doc, string? kind = null)
    {
        var value = (kind is null ? Area(doc, SearchPolicyArea) : doc.Select(SearchPolicyArea, "schema", "revision", kind)) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("songs", EmptySearchKind()),
            ("playlists", EmptySearchKind()));
        try { ValidateSearchPolicy(value, kind); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private JsonObject SearchHistory(MusicLibraryData doc)
    {
        var value = Area(doc, SearchHistoryArea) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("items", new JsonArray()));
        try { ValidateSearchHistory(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static string SearchText(JsonNode? value)
    {
        LJ.Require(value is JsonValue v && v.TryGetValue<string>(out _));
        string text = LJ.S(value).Trim();
        LJ.Require(text.Length > 0 && !text.Any(char.IsControl));
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
                LJ.Require(++i < text.Length && char.IsLowSurrogate(text[i]));
            else LJ.Require(!char.IsLowSurrogate(text[i]));
        }
        return text;
    }

    private static JsonObject SearchKindInput(JsonNode? value)
    {
        var input = LJ.Object(value);
        return new JsonObject(new[] { "order", "enabled", "pages", "concurrency", "cacheMinutes" }
            .Where(input.ContainsKey).Select(name => KeyValuePair.Create(name, input[name]?.DeepClone())));
    }

    private static void ValidateSearchKind(JsonObject value)
    {
        LJ.Fields(value, "order enabled pages concurrency cacheMinutes");
        _ = MusicSearchSettings.Pages(value["pages"]);
        _ = MusicSearchSettings.Concurrency(value["concurrency"]);
        _ = MusicSearchSettings.CacheMinutes(value["cacheMinutes"]);
        var order = LJ.Array(value["order"]).Select(item => LJ.Text(item, 2)).ToArray();
        var enabled = LJ.Array(value["enabled"]).Select(item => LJ.Text(item, 2)).ToArray();
        LJ.Require(order.Distinct(StringComparer.Ordinal).Count() == order.Length &&
            order.All(SearchPlatforms.Contains) &&
            enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(SearchPlatforms.Contains));
    }

    private static void ValidateSearchPolicy(JsonObject value, string? kind = null)
    {
        LJ.Fields(value, "schema revision");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        foreach (string name in kind is null ? new[] { "songs", "playlists" } : new[] { kind })
            ValidateSearchKind(LJ.Object(value[name]));
    }

    private static void ValidateSearchHistory(JsonObject value)
    {
        LJ.Fields(value, "schema revision items");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        var items = LJ.Array(value["items"]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in items)
        {
            var item = LJ.Object(raw);
            LJ.Fields(item, "text kind");
            string text = SearchText(item["text"]);
            LJ.Require(text == LJ.S(item["text"]) && seen.Add(text));
            LJ.Require(LJ.S(item["kind"]) is "local" or "songs" or "playlists");
        }
    }

    private (JsonObject Result, bool Mutate) SearchCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        if (method.StartsWith("music.search.settings.", StringComparison.Ordinal))
        {
            string? kind = method == "music.search.settings.get" && p.ContainsKey("kind") ? LJ.Text(p["kind"]) : null;
            LJ.Require(kind is null or "songs" or "playlists");
            var policy = method == "music.search.settings.get" ? SearchPolicy(doc, kind) :
                OverwriteSettings(doc, SearchPolicyArea, 1, LJ.Obj(("schema", 1), ("revision", 0)));
            if (method == "music.search.settings.get")
            {
                return (LJ.Obj(("settings", kind is null ? policy : LJ.Obj(("schema", policy["schema"]),
                    ("revision", policy["revision"]), (kind, policy[kind])))), false);
            }

            LJ.Require(method == "music.search.settings.save", "CAPABILITY_UNAVAILABLE");
            LJ.Fields(p, "ifRevision songs playlists");
            LJ.Require(LJ.I(p["ifRevision"]) == Revision(policy), "REVISION_CONFLICT");
            var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(policy) + 1)),
                ("songs", SearchKindInput(p["songs"])), ("playlists", SearchKindInput(p["playlists"])));
            ValidateSearchPolicy(next);
            Set(doc, SearchPolicyArea, next, partial: true);
            return (LJ.Obj(("settings", next)), true);
        }

        var history = (JsonObject)SearchHistory(doc).DeepClone();
        var entries = LJ.Array(history["items"]);
        if (method == "music.search.history.get")
        {
            return (LJ.Obj(("history", history)), false);
        }
        if (method == "music.search.history.record")
        {
            LJ.Fields(p, "text kind");
            string text = SearchText(p["text"]);
            var next = LJ.Obj(("text", text), ("kind", p["kind"]?.DeepClone()));
            foreach (var old in entries.OfType<JsonObject>().Where(n => LJ.S(n["text"]) == text).ToArray())
                entries.Remove(old);
            entries.Insert(0, next);
        }
        else
        {
            LJ.Require(method == "music.search.history.clear", "CAPABILITY_UNAVAILABLE");
            LJ.Fields(p, "ifRevision confirm");
            LJ.Require(LJ.I(p["ifRevision"]) == Revision(history), "REVISION_CONFLICT");
            LJ.Require(LJ.B(p["confirm"]), "CONFIRMATION_REQUIRED");
            entries.Clear();
        }
        Increment(history);
        ValidateSearchHistory(history);
        Set(doc, SearchHistoryArea, history);
        return (LJ.Obj(("history", history)), true);
    }
}
