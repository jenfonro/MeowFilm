using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

/// <summary>Explicit business receipts; no JavaScript execution or native persistence.</summary>
internal sealed class FilmRuleConsumerRuntime : IRuntimeClient
{
    internal readonly List<(string Method, JsonElement Input)> Calls = [];
    internal FilmRuleSet Saved = new()
    {
        EpisodeCleanRegexRules = ["clean"], EpisodeRules = [new("episode", "E$1", "i")],
        MovieRules = [new("movie", "", "i")], AggregateRegexRules = ["search"]
    };
    internal string Revision = "original-rules";
    private int writes;
    internal Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Respond;
    internal static JsonElement Snapshot(FilmRuleSet rules, string revision) =>
        JsonSerializer.SerializeToElement(new { rules, revision }, FilmRuleService.Json);
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = FilmJson.Encode(parameters ?? new { });
        Calls.Add((method, input));
        var value = Respond is { } respond ? await respond(method, input, cancellationToken) : Handle(method, input);
        return FilmSearchConsumerRuntime.Success(value);
    }
    internal JsonElement Handle(string method, JsonElement input)
    {
        if (method == "film.rules.get") return Snapshot(Saved, Revision);
        if (method is "film.rules.set" or "film.rules.restore")
        {
            Saved = input.GetProperty("rules").Deserialize<FilmRuleSet>(FilmRuleService.Json)!;
            Revision = "saved-rules-" + ++writes;
            return Snapshot(Saved, Revision);
        }
        if (method == "film.rules.clear") { Saved = new(); Revision = "defaults"; return Snapshot(Saved, Revision); }
        throw new InvalidOperationException("Provide an explicit rule receipt for " + method);
    }
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Rules do not upload source.");
}
