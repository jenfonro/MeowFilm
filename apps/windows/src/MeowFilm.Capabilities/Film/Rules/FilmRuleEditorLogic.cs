using System.Globalization;
using MeowFilm.Capabilities.Film.Rules;

namespace MeowFilm.Capabilities;

public sealed class FilmRuleResultLine(string name, string display)
{
    public string Name { get; } = name;
    public string Display { get; } = display;
}

internal static class FilmRuleEditorLogic
{
    internal static string SectionTitle(FilmRuleSection section) => section switch
    {
        FilmRuleSection.Episode => "列表净化",
        FilmRuleSection.Movie => "电影匹配",
        _ => "搜索列表净化"
    };

    internal static FilmRuleSet ApplyGroupEdit(FilmRuleSet draft, string groupTag, FilmSettingsRuleAction action,
        int index, bool hasReplacement, string pattern, string replace, FilmRuleService rules)
    {
        FilmMatchRule? addedMatch = null;
        string? addedClean = null;
        if (action == FilmSettingsRuleAction.Add)
        {
            if (hasReplacement)
                addedMatch = rules.NormalizeNewMatchRule(pattern, replace)
                    ?? throw new ArgumentException("请输入正则");
            else
            {
                addedClean = rules.NormalizeNewCleanRule(pattern);
                if (addedClean.Length == 0) throw new ArgumentException("请输入正则");
            }
        }
        return groupTag switch
        {
            "episodeClean" => draft with { EpisodeCleanRegexRules = Edit(draft.EpisodeCleanRegexRules, action, index, addedClean) },
            "episode" => draft with { EpisodeRules = Edit(draft.EpisodeRules, action, index, addedMatch) },
            "movie" => draft with { MovieRules = Edit(draft.MovieRules, action, index, addedMatch) },
            "search" => draft with { AggregateRegexRules = Edit(draft.AggregateRegexRules, action, index, addedClean) },
            _ => throw new InvalidOperationException("未知规则组")
        };

        static T[] Edit<T>(IReadOnlyList<T> values, FilmSettingsRuleAction change, int at, T? added)
        {
            var copy = values.ToList();
            if (change == FilmSettingsRuleAction.Delete) copy.RemoveAt(at);
            else if (change == FilmSettingsRuleAction.Add) copy.Add(added!);
            return copy.ToArray();
        }
    }

    internal static IReadOnlyList<FilmRuleResultLine> BuildResultLines(FilmRuleResult result)
    {
        var lines = new List<FilmRuleResultLine>();
        void Add(string name, string value) => lines.Add(new(name, name + "：" + value));
        Add("结果", result.Text);
        if (result.InputText.Length != 0) Add(result.Section == FilmRuleSection.Movie ? "匹配文本" : "净化后", result.ProcessedText);
        if (result.MatchedRuleIndex is { } index) Add("命中规则", index.ToString(CultureInfo.InvariantCulture));
        if (result.RewrittenText is { } rewrite) Add("改写结果", rewrite);
        if (result.Section == FilmRuleSection.Search && result.InputText.Length != 0)
        {
            Add("聚合键", result.GroupKey);
            if (result.TrailingDigits.Length != 0) Add("搜索尾号保护", result.TrailingDigits);
        }
        foreach (var error in result.Errors) Add($"规则 {error.RuleIndex}", error.Text);
        return lines;
    }
}
