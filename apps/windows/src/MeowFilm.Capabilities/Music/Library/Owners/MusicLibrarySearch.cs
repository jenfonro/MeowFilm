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

    private static MusicSearchPolicyRecord SearchPolicy(MusicLibrarySession doc) =>
        doc.ReadSearchPolicy() ?? DefaultSearchPolicy;

    private static MusicSearchHistoryRecord SearchHistory(MusicLibrarySession doc) =>
        doc.ReadSearchHistory() ?? DefaultSearchHistory;

    private static string ValidateSearchText(string raw)
    {
        string text = raw.Trim();
        MusicRules.Require(text.Length > 0 && !text.Any(char.IsControl));
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

    public Task<MusicSearchPolicyRecord> GetSearchPolicyAsync(string? kind = null, CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(kind is null or "songs" or "playlists");
            return SearchPolicy(doc);
        }, cancellationToken);

    public async Task<MusicSearchPolicyRecord> SaveSearchPolicyAsync(
        long ifRevision,
        MusicSearchKindRecord songs,
        MusicSearchKindRecord playlists,
        CancellationToken cancellationToken = default)
    {
        bool mutated = false;
        var saved = await RunLibraryWriteAsync(doc =>
        {
            var policy = SearchPolicy(doc);
            MusicRules.Require(ifRevision == policy.Revision, "REVISION_CONFLICT");
            var next = new MusicSearchPolicyRecord(checked(policy.Revision + 1), songs, playlists);
            ValidateSearchPolicy(next);
            doc.SetSearchPolicy(next);
            return (next, true);
        }, cancellationToken, onCommitted: () => mutated = true).ConfigureAwait(false);
        if (mutated) InvokeSafe(SearchPolicyChanged, saved);
        return saved;
    }

    public Task<MusicSearchHistoryRecord> GetSearchHistoryAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(SearchHistory, cancellationToken);

    public Task<MusicSearchHistoryRecord> RecordSearchHistoryAsync(
        string text,
        string kind,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            var history = SearchHistory(doc);
            string normalized = ValidateSearchText(text);
            MusicRules.Require(kind is "local" or "songs" or "playlists");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextItems = new List<MusicSearchHistoryItemRecord> { new(normalized, kind, now) };
            foreach (var old in history.Items)
                if (old.Text != normalized)
                    nextItems.Add(old);
            var nextHistory = new MusicSearchHistoryRecord(checked(history.Revision + 1), nextItems.ToArray());
            doc.SetSearchHistory(nextHistory);
            return (nextHistory, true);
        }, cancellationToken);

    public Task<MusicSearchHistoryRecord> ClearSearchHistoryAsync(
        long ifRevision,
        bool confirm,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(doc =>
        {
            var history = SearchHistory(doc);
            MusicRules.Require(ifRevision == history.Revision, "REVISION_CONFLICT");
            MusicRules.Require(confirm, "CONFIRMATION_REQUIRED");
            var nextHistory = new MusicSearchHistoryRecord(checked(history.Revision + 1), []);
            doc.SetSearchHistory(nextHistory);
            return (nextHistory, true);
        }, cancellationToken);
}
