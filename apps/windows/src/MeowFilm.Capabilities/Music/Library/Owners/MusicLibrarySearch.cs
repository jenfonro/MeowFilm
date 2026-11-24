namespace MeowFilm.Capabilities.Library;

internal static class MusicSearchSettings
{
    internal const int MaxCacheMinutes = 1440;
    internal static bool ValidPages(int value) => value >= 1;
    internal static bool ValidConcurrency(int value) => value >= 1;
    internal static bool ValidCacheMinutes(int value) => value is >= 1 and <= MaxCacheMinutes;
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

    public Task<MusicSearchPolicyRecord> GetSearchPolicyAsync(string? kind = null, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            MusicRules.Require(kind is null or "songs" or "playlists");
            return SearchPolicy(doc);
        }, cancellationToken, authoritativeFor: PendingOwner.SearchPolicy);

    public Task<MusicSearchPolicyRecord> SaveSearchPolicyAsync(
        long ifRevision,
        MusicSearchKindRecord songs,
        MusicSearchKindRecord playlists,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.SearchPolicy, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var policy = SearchPolicy(doc);
            MusicRules.Require(ifRevision == policy.Revision, "REVISION_CONFLICT");
            var next = new MusicSearchPolicyRecord(checked(policy.Revision + 1), songs, playlists);
            ValidateSearchPolicy(next);
            doc.SetSearchPolicy(next);
            return (next, true);
        }, cancellationToken);

    public Task<MusicSearchHistoryRecord> GetSearchHistoryAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            return SearchHistory(doc);
        }, cancellationToken);

    public Task<MusicSearchHistoryRecord> RecordSearchHistoryAsync(
        string text,
        string kind,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.SearchHistory, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var history = SearchHistory(doc);
            string normalized = ValidateSearchText(text);
            MusicRules.Require(kind is "local" or "songs" or "playlists");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextItems = new List<MusicSearchHistoryItemRecord> { new(normalized, kind, now) };
            foreach (var old in history.Items)
                if (old.Text != normalized)
                    nextItems.Add(old);
            var nextHistory = new MusicSearchHistoryRecord(checked(history.Revision + 1), nextItems.ToArray());
            ValidateSearchHistory(nextHistory);
            doc.SetSearchHistory(nextHistory);
            return (nextHistory, true);
        }, cancellationToken);

    public Task<MusicSearchHistoryRecord> ClearSearchHistoryAsync(
        long ifRevision,
        bool confirm,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.SearchHistory, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var history = SearchHistory(doc);
            MusicRules.Require(ifRevision == history.Revision, "REVISION_CONFLICT");
            MusicRules.Require(confirm, "CONFIRMATION_REQUIRED");
            var nextHistory = new MusicSearchHistoryRecord(checked(history.Revision + 1), []);
            ValidateSearchHistory(nextHistory);
            doc.SetSearchHistory(nextHistory);
            return (nextHistory, true);
        }, cancellationToken);
}
