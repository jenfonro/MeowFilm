using System.Text.RegularExpressions;

namespace MeowFilm.Capabilities;

internal sealed record NovelDiscoveryGroup(string Title, IReadOnlyList<NovelKind> Kinds);

internal static class NovelDiscoveryKinds
{
    internal static IReadOnlyList<NovelDiscoveryGroup> Groups(IEnumerable<NovelKind> kinds, bool rankingsOnly = false)
    {
        var groups = new List<NovelDiscoveryGroup>();
        string title = "";
        List<NovelKind>? entries = null;
        foreach (var kind in kinds)
        {
            if (string.IsNullOrWhiteSpace(kind.Url))
            {
                title = kind.Title; entries = null;
                continue;
            }
            if (rankingsOnly && !RankingLabel(title) && !RankingLabel(kind.Title) && !RankingUrl(kind.Url))
                continue;
            if (entries is null)
            {
                entries = [];
                groups.Add(new(title, entries));
            }
            entries.Add(kind);
        }
        return groups;
    }

    internal static NovelKind? Select(IEnumerable<NovelKind> kinds, NovelKind? selected, bool rankingsOnly)
    {
        var entries = Groups(kinds, rankingsOnly).SelectMany(group => group.Kinds).ToArray();
        return entries.FirstOrDefault(kind => kind.Url == selected?.Url) ?? entries.FirstOrDefault();
    }

    internal static bool IsRanking(IEnumerable<NovelKind> kinds, NovelKind? selected) =>
        selected is not null && Groups(kinds, rankingsOnly: true)
            .Any(group => group.Kinds.Any(kind => kind.Url == selected.Url));

    private static bool RankingLabel(string value) => value.Contains('榜') || value.Contains("排行", StringComparison.Ordinal) ||
        Regex.IsMatch(value, @"(?:^|[^a-z])(?:rank(?:ing|list)?s?|charts?|toplist)(?:$|[^a-z])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool RankingUrl(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri)) return false;
        if (uri.IsAbsoluteUri)
        {
            if (uri.Scheme is not ("http" or "https")) return false;
            value = uri.AbsolutePath;
        }
        else value = value.Split(['?', '#'], 2)[0];
        return Regex.IsMatch(value, @"(?:^|[/._-])(?:rank(?:ing|list)?s?|charts?|toplist)(?:$|[/._-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
