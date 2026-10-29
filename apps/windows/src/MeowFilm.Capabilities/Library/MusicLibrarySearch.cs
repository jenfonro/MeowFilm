using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string SearchPolicyArea = "native-search-policy", SearchHistoryArea = "native-search-history";
    private static readonly string[] SearchPlatforms = ["wy", "tx", "kw", "kg", "mg"];

    private static JsonObject EmptySearchKind(string platform) =>
        LJ.Obj(("mode", "aggregate"), ("platform", platform), ("order", LJ.Arr(SearchPlatforms)),
            ("enabled", LJ.Arr(SearchPlatforms)));

    private JsonObject SearchPolicy(JsonObject doc) => Value(doc, SearchPolicyArea) as JsonObject ??
        LJ.Obj(("schema", 2), ("revision", 0), ("songs", EmptySearchKind("wy")),
            ("playlists", EmptySearchKind("wy")));

    private JsonObject SearchHistory(JsonObject doc) => Value(doc, SearchHistoryArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("items", new JsonArray()));

    private static string SearchText(JsonNode? value)
    {
        LJ.Require(value is JsonValue v && v.TryGetValue<string>(out _));
        string text = LJ.S(value).Trim();
        LJ.Require(text.Length is > 0 and <= 200 && !text.Any(char.IsControl));
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
                LJ.Require(++i < text.Length && char.IsLowSurrogate(text[i]));
            else LJ.Require(!char.IsLowSurrogate(text[i]));
        }
        return text;
    }

    private static void ValidateSearchKind(JsonObject value)
    {
        LJ.Fields(value, "mode platform order enabled", "mode platform order enabled");
        LJ.Require(LJ.S(value["mode"]) is "aggregate" or "single");
        LJ.Require(SearchPlatforms.Contains(LJ.S(value["platform"])));
        var order = LJ.Array(value["order"]).Select(item => LJ.Text(item, 2)).ToArray();
        var enabled = LJ.Array(value["enabled"]).Select(item => LJ.Text(item, 2)).ToArray();
        var enabledSet = enabled.ToHashSet(StringComparer.Ordinal);
        LJ.Require(order.Length == SearchPlatforms.Length &&
            order.Distinct(StringComparer.Ordinal).Count() == SearchPlatforms.Length &&
            order.All(SearchPlatforms.Contains) &&
            enabled.Length <= SearchPlatforms.Length &&
            enabled.Distinct(StringComparer.Ordinal).Count() == enabled.Length &&
            enabled.All(SearchPlatforms.Contains) &&
            enabled.SequenceEqual(order.Where(enabledSet.Contains)));
    }

    private static void ValidateSearchPolicy(JsonObject value)
    {
        LJ.Fields(value, "schema revision songs playlists", "schema revision songs playlists");
        LJ.Require(LJ.I(value["schema"]) == 2);
        _ = Revision(value);
        ValidateSearchKind(LJ.Object(value["songs"]));
        ValidateSearchKind(LJ.Object(value["playlists"]));
    }

    private static void ValidateSearchHistory(JsonObject value)
    {
        LJ.Fields(value, "schema revision items", "schema revision items");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        var items = LJ.Array(value["items"]);
        LJ.Require(items.Count <= 60);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in items)
        {
            var item = LJ.Object(raw);
            LJ.Fields(item, "text kind platform", "text kind platform");
            string text = SearchText(item["text"]);
            LJ.Require(text == LJ.S(item["text"]) && seen.Add(text));
            LJ.Require(LJ.S(item["kind"]) is "local" or "songs" or "playlists");
            string platform = LJ.S(item["platform"]);
            LJ.Require(SearchPlatforms.Contains(platform) || platform is "aggregate" or "local");
            LJ.Require((LJ.S(item["kind"]) == "local") == (platform == "local"));
        }
    }

    private (JsonObject Result, bool Mutate) SearchCommand(string method, JsonObject p, JsonObject doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        if (method.StartsWith("music.search.settings.", StringComparison.Ordinal))
        {
            var policy = SearchPolicy(doc);
            ValidateSearchPolicy(policy);
            if (method == "music.search.settings.get")
            {
                LJ.Fields(p, "");
                return (LJ.Obj(("settings", policy)), false);
            }

            LJ.Require(method == "music.search.settings.save", "CAPABILITY_UNAVAILABLE");
            LJ.Fields(p, "ifRevision songs playlists", "ifRevision songs playlists");
            LJ.Require(LJ.I(p["ifRevision"]) == Revision(policy), "REVISION_CONFLICT");
            var next = LJ.Obj(("schema", 2), ("revision", checked(Revision(policy) + 1)),
                ("songs", p["songs"]?.DeepClone()), ("playlists", p["playlists"]?.DeepClone()));
            ValidateSearchPolicy(next);
            Set(doc, SearchPolicyArea, next);
            return (LJ.Obj(("settings", next)), true);
        }

        var history = (JsonObject)SearchHistory(doc).DeepClone();
        var entries = LJ.Array(history["items"]);
        if (method == "music.search.history.get")
        {
            LJ.Fields(p, "");
            return (LJ.Obj(("history", history)), false);
        }
        if (method == "music.search.history.record")
        {
            LJ.Fields(p, "text kind platform", "text kind platform");
            string text = SearchText(p["text"]);
            var next = LJ.Obj(("text", text), ("kind", p["kind"]), ("platform", p["platform"]));
            foreach (var old in entries.OfType<JsonObject>().Where(n => LJ.S(n["text"]) == text).ToArray())
                entries.Remove(old);
            entries.Insert(0, next);
            while (entries.Count > 60) entries.RemoveAt(entries.Count - 1);
        }
        else
        {
            LJ.Require(method == "music.search.history.clear", "CAPABILITY_UNAVAILABLE");
            LJ.Fields(p, "ifRevision confirm", "ifRevision confirm");
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
