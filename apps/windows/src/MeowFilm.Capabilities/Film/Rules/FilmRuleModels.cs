using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Film.Rules;

[JsonConverter(typeof(JsonStringEnumConverter<FilmRuleSection>))]
public enum FilmRuleSection { Episode, Movie, Search }

public sealed record FilmMatchRule(string Pattern, string Replace = "", string Flags = "");

public sealed record FilmRuleSet
{
    public IReadOnlyList<string> EpisodeCleanRegexRules { get; init; } = [];
    public IReadOnlyList<FilmMatchRule> EpisodeRules { get; init; } = [];
    public IReadOnlyList<FilmMatchRule> MovieRules { get; init; } = [];
    public IReadOnlyList<string> AggregateRegexRules { get; init; } = [];
}

public sealed record FilmRuleRequest(FilmRuleSection Section, string Text, string? Query = null);

/// <param name="Group">episodeClean, episode, movie or search.</param>
/// <param name="RuleIndex">One-based index in that group.</param>
public sealed record FilmRuleError(string Group, int RuleIndex, string Text);

public sealed record FilmRuleResult
{
    public FilmRuleSection Section { get; init; }
    /// <summary>Settings-page status: empty, success or error.</summary>
    public string Type { get; init; } = "";
    public string Text { get; init; } = "";
    public string InputText { get; init; } = "";
    /// <summary>Text after preprocessing, before any matching-rule replacement.</summary>
    public string ProcessedText { get; init; } = "";
    public bool Matched { get; init; }
    /// <summary>One-based; null when no matching rule was hit.</summary>
    public int? MatchedRuleIndex { get; init; }
    public bool RewriteApplied { get; init; }
    /// <summary>Null means no replacement; an empty string means replacement failed.</summary>
    public string? RewrittenText { get; init; }
    [JsonIgnore]
    public string OutputText => RewrittenText ?? ProcessedText;
    public string GroupKey { get; init; } = "";
    public string TrailingDigits { get; init; } = "";
    public IReadOnlyList<FilmRuleError> Errors { get; init; } = [];
    /// <summary>Cumulative cleaning previews, in the original row order.</summary>
    public IReadOnlyList<string> Previews { get; init; } = [];
}
