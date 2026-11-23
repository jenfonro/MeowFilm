using System.Text.Json;

namespace MeowFilm.Capabilities.Library;

internal static class MusicSearchSettings
{
    internal const int MaxCacheMinutes = 1440;
    internal static bool ValidPages(int value) => value >= 1;
    internal static bool ValidConcurrency(int value) => value >= 1;
    internal static bool ValidCacheMinutes(int value) => value is >= 1 and <= MaxCacheMinutes;

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
    internal static readonly string[] SearchPlatforms = ["wy", "tx", "kw", "kg", "mg"];
    private static readonly MusicSearchKindRecord DefaultSearchKind =
        new(SearchPlatforms, SearchPlatforms, 1, 1, 60);
    private static readonly MusicSearchPolicyRecord DefaultSearchPolicy =
        new(0, DefaultSearchKind, DefaultSearchKind);
    private static readonly MusicSearchHistoryRecord DefaultSearchHistory = new(0, []);

    private static MusicSearchPolicyRecord SearchPolicy(MusicLibrarySession doc)
    {
        var value = doc.ReadSearchPolicy() ?? DefaultSearchPolicy;
        try { ValidateSearchPolicy(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static MusicSearchHistoryRecord SearchHistory(MusicLibrarySession doc)
    {
        var value = doc.ReadSearchHistory() ?? DefaultSearchHistory;
        try { ValidateSearchHistory(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static string ValidateSearchText(string raw, bool requireAlreadyTrimmed = false)
    {
        string text = raw.Trim();
        MusicRules.Require(text.Length > 0 && !text.Any(char.IsControl));
        if (requireAlreadyTrimmed) MusicRules.Require(text == raw);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
                MusicRules.Require(++i < text.Length && char.IsLowSurrogate(text[i]));
            else MusicRules.Require(!char.IsLowSurrogate(text[i]));
        }
        return text;
    }

    private static MusicSearchKindRecord ReadSearchKindInput(JsonElement el)
    {
        MusicWireCodec.RequireFields(el, "order enabled pages concurrency cacheMinutes");
        var rec = new MusicSearchKindRecord(
            MusicWireCodec.RequireIdArrayProp(el, "order", 2),
            MusicWireCodec.RequireIdArrayProp(el, "enabled", 2),
            MusicWireCodec.RequireIntProp(el, "pages", 1, int.MaxValue),
            MusicWireCodec.RequireIntProp(el, "concurrency", 1, int.MaxValue),
            MusicWireCodec.RequireIntProp(el, "cacheMinutes", 1, MusicSearchSettings.MaxCacheMinutes));
        ValidateSearchKind(rec);
        return rec;
    }

    private static void ValidateSearchKind(MusicSearchKindRecord value)
    {
        MusicRules.Require(value.Pages is >= 1 and <= int.MaxValue &&
            value.Concurrency is >= 1 and <= int.MaxValue &&
            value.CacheMinutes is >= 1 and <= MusicSearchSettings.MaxCacheMinutes);
        MusicRules.Require(value.Order.Distinct(StringComparer.Ordinal).Count() == value.Order.Length &&
            value.Order.All(SearchPlatforms.Contains) &&
            value.Enabled.Distinct(StringComparer.Ordinal).Count() == value.Enabled.Length &&
            value.Enabled.All(SearchPlatforms.Contains));
    }

    private static void ValidateSearchPolicy(MusicSearchPolicyRecord value)
    {
        MusicRules.Require(value.Revision is >= 0 and <= MusicRules.MaxInteger);
        ValidateSearchKind(value.Songs);
        ValidateSearchKind(value.Playlists);
    }

    private static void ValidateSearchHistory(MusicSearchHistoryRecord value)
    {
        MusicRules.Require(value.Revision is >= 0 and <= MusicRules.MaxInteger);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.Items)
        {
            string text = ValidateSearchText(item.Text, requireAlreadyTrimmed: true);
            MusicRules.Require(seen.Add(text));
            MusicRules.Require(item.Kind is "local" or "songs" or "playlists");
        }
    }

    private (JsonElement Result, bool Mutate) SearchCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        MusicRules.Require(pending is null, "PENDING_WRITE");
        if (method.StartsWith("music.search.settings.", StringComparison.Ordinal))
        {
            string? kind = method == "music.search.settings.get" && MusicWireCodec.TryGet(p, "kind", out var kEl)
                ? MusicWireCodec.RequireId(kEl)
                : null;
            MusicRules.Require(kind is null or "songs" or "playlists");
            var policy = SearchPolicy(doc);
            if (method == "music.search.settings.get")
            {
                return (MusicWireCodec.Ok(w =>
                {
                    w.WritePropertyName("settings");
                    MusicWireCodec.WriteSearchPolicy(w, policy, kind);
                }), false);
            }

            MusicRules.Require(method == "music.search.settings.save", "CAPABILITY_UNAVAILABLE");
            MusicWireCodec.RequireFields(p, "ifRevision songs playlists");
            MusicRules.Require(MusicWireCodec.RequireIntProp(p, "ifRevision") == policy.Revision, "REVISION_CONFLICT");
            var next = new MusicSearchPolicyRecord(
                checked(policy.Revision + 1),
                ReadSearchKindInput(MusicWireCodec.RequireObjectProp(p, "songs")),
                ReadSearchKindInput(MusicWireCodec.RequireObjectProp(p, "playlists")));
            ValidateSearchPolicy(next);
            doc.SetSearchPolicy(next);
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteSearchPolicy(w, next);
            }), true);
        }

        var history = SearchHistory(doc);
        if (method == "music.search.history.get")
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("history");
                MusicWireCodec.WriteSearchHistory(w, history);
            }), false);
        }

        List<MusicSearchHistoryItemRecord> nextItems;
        if (method == "music.search.history.record")
        {
            MusicWireCodec.RequireFields(p, "text kind");
            string text = ValidateSearchText(MusicWireCodec.RequireStrProp(p, "text"));
            string itemKind = MusicWireCodec.RequireStrProp(p, "kind");
            MusicRules.Require(itemKind is "local" or "songs" or "playlists");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            nextItems = [new MusicSearchHistoryItemRecord(text, itemKind, now)];
            foreach (var old in history.Items)
                if (old.Text != text)
                    nextItems.Add(old);
        }
        else
        {
            MusicRules.Require(method == "music.search.history.clear", "CAPABILITY_UNAVAILABLE");
            MusicWireCodec.RequireFields(p, "ifRevision confirm");
            MusicRules.Require(MusicWireCodec.RequireIntProp(p, "ifRevision") == history.Revision, "REVISION_CONFLICT");
            MusicRules.Require(MusicWireCodec.RequireBoolProp(p, "confirm"), "CONFIRMATION_REQUIRED");
            nextItems = [];
        }

        var nextHistory = new MusicSearchHistoryRecord(checked(history.Revision + 1), nextItems.ToArray());
        ValidateSearchHistory(nextHistory);
        doc.SetSearchHistory(nextHistory);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("history");
            MusicWireCodec.WriteSearchHistory(w, nextHistory);
        }), true);
    }
}
