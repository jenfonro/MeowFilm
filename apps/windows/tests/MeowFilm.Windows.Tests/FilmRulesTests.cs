using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

public static class FilmRulesTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-rules: saved preview omits rules and draft preview preserves all groups without commit", Preview);
        yield return new("film-rules: failed save preserves acknowledged snapshot and revision; get reads the Node owner", Save);
        yield return new("film-rules: restore normalize and clear use business operations with complete draft", Editing);
        yield return new("film-rules: isolated UTF16 input results errors and previews survive the wire", Utf16);
        yield return new("film-rules: shutdown cancels drains queues and resume reopens admission", Lifetime);
        yield return new("film-rules: unavailable Node errors surface without a native engine or storage fallback", Unavailable);
    }
    private static void Check(bool value, string reason) => SearchFixture.Check(value, reason);
    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left, FilmRuleService.Json) == JsonSerializer.Serialize(right, FilmRuleService.Json);
    private static async Task Preview()
    {
        var runtime = new FilmRuleConsumerRuntime();
        await using var service = new FilmRuleService(runtime);
        await service.InitializeAsync();
        var original = service.GetSavedSnapshot();
        var draft = original with { MovieRules = [new("draft", "$1", "gy")] };
        var expected = new FilmRuleResult { Section = FilmRuleSection.Movie, Type = "success", InputText = "原文本", ProcessedText = "原文本",
            Matched = true, MatchedRuleIndex = 2, RewriteApplied = true, RewrittenText = "Node输出", Errors = [new("movie", 1, "错误")], Previews = ["累计"] };
        runtime.Respond = (method, input, _) =>
        {
            Check(method == "film.rules.preview", "only business preview runs");
            Check(input.GetProperty("request").GetProperty("text").GetString() == "原文本" &&
                input.GetProperty("request").GetProperty("query").GetString() == "原查询2", "original input and query survive");
            return Task.FromResult(JsonSerializer.SerializeToElement(expected, FilmRuleService.Json));
        };
        var request = new FilmRuleRequest(FilmRuleSection.Movie, "原文本", "原查询2");
        Check(Same(await service.ProcessAsync(request), expected) && Same(await service.ProcessAsync(request, draft), expected), "the complete Node result is displayed");
        Check(!runtime.Calls[^2].Input.TryGetProperty("rules", out _) &&
            Same(runtime.Calls[^1].Input.GetProperty("rules").Deserialize<FilmRuleSet>(FilmRuleService.Json), draft), "only the explicit preview draft is sent");
        runtime.Respond = (method, input, token) =>
        {
            Check(method == "film.rules.preview" &&
                input.GetProperty("request").GetProperty("text").GetString() == "原文本" &&
                !input.GetProperty("request").TryGetProperty("query", out _), "default query is omitted rather than sent as null");
            return Task.FromResult(JsonSerializer.SerializeToElement(expected, FilmRuleService.Json));
        };
        var defaultQuery = new FilmRuleRequest(FilmRuleSection.Movie, "原文本");
        Check(Same(await service.ProcessAsync(defaultQuery), expected) &&
            Same(await service.ProcessAsync(defaultQuery, draft), expected), "saved and draft previews without a query consume the complete Node result");
        Check(Same(original, service.GetSavedSnapshot()) && runtime.Calls.All(call => call.Method is "film.rules.get" or "film.rules.preview"), "preview never commits");
    }
    private static async Task Save()
    {
        var runtime = new FilmRuleConsumerRuntime();
        await using var service = new FilmRuleService(runtime);
        await service.InitializeAsync();
        var original = service.GetSavedSnapshot();
        var next = original with { AggregateRegexRules = ["new"] };
        runtime.Respond = (_, _, _) => throw new FilmPlaybackException("STORAGE_ERROR");
        try { await service.SaveAsync(next); throw new Exception("Expected failure"); }
        catch (FilmPlaybackException error) { Check(error.Code == "STORAGE_ERROR", "real failure surfaces"); }
        Check(Same(original, service.GetSavedSnapshot()) && service.Revision == "original-rules", "failed write cannot publish");
        runtime.Respond = null;
        Check(Same(await service.SaveAsync(next), next) && service.Revision == runtime.Revision, "only successful Node receipt changes the opaque revision");
        runtime.Saved = original; runtime.Revision = "a-different-snapshot";
        await service.InitializeAsync();
        Check(Same(service.GetSavedSnapshot(), original) && service.Revision == "a-different-snapshot", "reentry refreshes display from the unique owner");
        await using var reopened = new FilmRuleService(runtime);
        await reopened.InitializeAsync();
        Check(Same(reopened.GetSavedSnapshot(), original), "new consumer reads the current Node snapshot");
        runtime.Saved = runtime.Saved with { MovieRules = [new("mutated")] };
        Check(service.GetSavedSnapshot().MovieRules[0].Pattern == "movie", "display snapshot owns its immutable collections");
    }
    private static async Task Editing()
    {
        var runtime = new FilmRuleConsumerRuntime();
        await using var service = new FilmRuleService(runtime);
        var draft = runtime.Saved with { MovieRules = [new("unsaved")] };
        runtime.Respond = (method, input, _) =>
        {
            if (method == "film.rules.restore")
            {
                Check(input.GetProperty("section").GetString() == "episode" &&
                    Same(input.GetProperty("rules").Deserialize<FilmRuleSet>(FilmRuleService.Json), draft), "whole draft and section go to Node");
                return Task.FromResult(FilmRuleConsumerRuntime.Snapshot(draft with { EpisodeRules = [new("restored")] }, "restored-snapshot"));
            }
            Check(method == "film.rules.normalize", "normalization belongs to Node");
            return Task.FromResult(input.GetProperty("kind").GetString() == "match"
                ? FilmJson.Encode(new FilmMatchRule("added", "$1", "gi")) : FilmJson.Encode("added-clean"));
        };
        var restored = await service.RestoreDefaultsAsync(FilmRuleSection.Episode, draft);
        Check(restored.MovieRules[0].Pattern == "unsaved" && restored.EpisodeRules[0].Pattern == "restored", "restored receipt keeps other draft groups");
        Check(await service.NormalizeNewMatchRuleAsync(" /added/gi ", "$1") == new FilmMatchRule("added", "$1", "gi") &&
            await service.NormalizeNewCleanRuleAsync("  raw  ") == "added-clean", "normalizer receipt is consumed verbatim");
        Check(runtime.Calls[^1].Input.GetProperty("pattern").GetString() == "  raw  ", "client does not trim or parse rule text");
        runtime.Respond = null;
        await service.ClearAsync();
        Check(runtime.Calls[^1].Method == "film.rules.clear" && service.GetSavedSnapshot().MovieRules.Count == 0, "clear goes to owner and consumes defaults receipt");
    }
    private static async Task Utf16()
    {
        var runtime = new FilmRuleConsumerRuntime();
        string isolated = "A\ud800B\udc00";
        var expected = new FilmRuleResult { Section = FilmRuleSection.Search, InputText = isolated, ProcessedText = isolated,
            RewrittenText = isolated, GroupKey = isolated, Previews = [isolated], Errors = [new("search", 1, isolated)] };
        runtime.Respond = (_, input, _) =>
        {
            Check(input.GetProperty("request").GetProperty("text").EnumerateArray().Select(unit => (char)unit.GetUInt16()).SequenceEqual(isolated), "isolated units use UInt16 arrays");
            Check(input.GetProperty("request").GetProperty("query").GetString() == "正常🐱", "valid text remains JSON string");
            return Task.FromResult(JsonSerializer.SerializeToElement(expected, FilmRuleService.Json));
        };
        await using var service = new FilmRuleService(runtime);
        Check(Same(await service.ProcessAsync(new(FilmRuleSection.Search, isolated, "正常🐱")), expected), "all result text fields preserve isolated units");
    }
    private static async Task Lifetime()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FilmRuleConsumerRuntime();
        runtime.Respond = async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await drained.Task; }
            return default;
        };
        await using var service = new FilmRuleService(runtime);
        var work = service.InitializeAsync(); await entered.Task;
        var queued = service.InitializeAsync();
        var close = service.PrepareShutdownAsync();
        Check(!close.IsCompleted, "preparation waits physical RPC drain");
        drained.SetResult();
        foreach (var task in new[] { work, queued }) { try { await task; } catch (OperationCanceledException) { } }
        await close;
        Check(runtime.Calls.Count == 1, "queued calls do not execute after preparation");
        runtime.Respond = null; service.Resume(); await service.InitializeAsync();
        Check(service.GetSavedSnapshot().MovieRules.Count == 1, "resume accepts owner reads");
    }
    private static async Task Unavailable()
    {
        const string code = "NETWORK_ERROR";
        var runtime = new FilmRuleConsumerRuntime { Respond = (_, _, _) => throw new BridgeException(code) };
        await using var service = new FilmRuleService(runtime);
        try { await service.InitializeAsync(); throw new Exception("Expected runtime error"); }
        catch (BridgeException error) { Check(error.Code == code, "runtime error is preserved"); }
        Check(runtime.Calls.Count == 1, "no alternate execution path");
    }
}
