using System.Text.Json;
using MeowFilm.AppHost;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmRulesTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-rules: saved preview omits rules and draft preview preserves all groups without commit", Preview);
        yield return new("film-rules: native CAS failure preserves snapshot and SHA revision; reentry reads the unique document", Save);
        yield return new("film-rules: restore normalize and clear edit the native document with complete draft", Editing);
        yield return new("film-rules: isolated UTF16 input results errors and previews survive the wire", Utf16);
        yield return new("film-rules: shutdown cancels drains queues and resume reopens admission", Lifetime);
        yield return new("film-rules: admitted native write drains after cancellation and pre-cancelled edits never write", NativeWrite);
        yield return new("film-rules: native read cancellation drains before shutdown without publishing", NativeRead);
        yield return new("film-rules: App notification delay and failure never delay or roll back a committed native save", NotificationFailure);
        yield return new("film-rules: corrupt native storage is not replaced by defaults", Corrupt);
        yield return new("film-rules: offline settings remain editable but actual preview fails", Unavailable);
    }
    private static void Check(bool value, string reason) => SearchFixture.Check(value, reason);
    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left, FilmRuleService.Json) == JsonSerializer.Serialize(right, FilmRuleService.Json);
    private static async Task Preview()
    {
        var runtime = new FilmRuleConsumerRuntime();
        await using var store = new FilmNativeSettingsFixture();
        await using var service = new FilmRuleService(runtime, store);
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
        Check(Same(original, service.GetSavedSnapshot()) && runtime.Calls.All(call => call.Method == "film.rules.preview"), "preview never commits");
    }
    private static async Task Save()
    {
        var runtime = new FilmRuleConsumerRuntime();
        await using var store = new FilmNativeSettingsFixture();
        await using var service = new FilmRuleService(runtime, store);
        await service.InitializeAsync();
        var original = service.GetSavedSnapshot();
        string revision = service.Revision;
        var next = original with { AggregateRegexRules = ["new"] };
        foreach (var failure in new[] { DocumentStoreError.Storage, DocumentStoreError.Conflict })
        {
            store.WriteError = failure;
            await StoreError(failure, () => service.SaveAsync(next));
            Check(Same(original, service.GetSavedSnapshot()) && service.Revision == revision && runtime.Notifications.Count == 0,
                "a failed native CAS cannot publish or notify");
        }
        store.WriteError = null;
        var concurrent = original with { AggregateRegexRules = ["concurrent"] };
        store.BeforeWrite = (_) =>
        {
            store.Seed("film.rules.v1", JsonSerializer.SerializeToElement(new { version = 1, rules = concurrent }, FilmRuleService.Json));
            return Task.CompletedTask;
        };
        await StoreError(DocumentStoreError.Conflict, () => service.SaveAsync(next));
        Check(Same(store.Value("film.rules.v1").GetProperty("rules").Deserialize<FilmRuleSet>(FilmRuleService.Json), concurrent) &&
            Same(service.GetSavedSnapshot(), original), "the real fixture CAS preserves a concurrently committed document and does not publish this caller's draft");
        store.Writing = null; store.BeforeWrite = null;
        store.Seed("film.rules.v1", JsonSerializer.SerializeToElement(new { version = 1, rules = original }, FilmRuleService.Json));
        await service.InitializeAsync();
        runtime.Notify = _ => Check(Same(store.Value("film.rules.v1").GetProperty("rules").Deserialize<FilmRuleSet>(FilmRuleService.Json), next),
            "the block notification runs only after the native document commits");
        Check(Same(await service.SaveAsync(next), next) &&
            service.Revision == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(next, FilmRuleService.Json))),
            "the committed local content determines the SHA revision");
        Check(runtime.Calls.Count == 0 && runtime.Notifications.SequenceEqual(["film.rules"]) &&
            Same(store.Value("film.rules.v1").GetProperty("rules").Deserialize<FilmRuleSet>(FilmRuleService.Json), next),
            "the unique native rules document commits before its block notification");
        store.Seed("film.rules.v1", JsonSerializer.SerializeToElement(new { version = 1, rules = original }, FilmRuleService.Json));
        await service.InitializeAsync();
        Check(Same(service.GetSavedSnapshot(), original) && service.Revision == revision, "reentry reads the native owner");
        await using var reopened = new FilmRuleService(runtime, store);
        await reopened.InitializeAsync();
        Check(Same(reopened.GetSavedSnapshot(), original), "a new consumer reads the same native document");
        store.Seed("film.rules.v1", JsonSerializer.SerializeToElement(new { rules = original with { MovieRules = [new("mutated")] } }, FilmRuleService.Json));
        Check(Same(service.GetSavedSnapshot(), original), "display owns an immutable copy, not a live store collection");
    }
    private static async Task Editing()
    {
        var runtime = new FilmRuleConsumerRuntime();
        await using var store = new FilmNativeSettingsFixture();
        await using var service = new FilmRuleService(runtime, store);
        await service.InitializeAsync();
        var defaults = service.GetSavedSnapshot();
        var draft = defaults with { MovieRules = [new("unsaved")] };
        var restored = await service.RestoreDefaultsAsync(FilmRuleSection.Episode, draft);
        Check(restored.MovieRules[0].Pattern == "unsaved" && restored.EpisodeRules.Count > 0,
            "native section restoration saves the whole draft and preserves other groups");
        Check(await service.NormalizeNewMatchRuleAsync(" /added/gi ", " $1 ") == new FilmMatchRule("added", "$1", "gi") &&
            await service.NormalizeNewCleanRuleAsync("  raw  ") == "raw" &&
            await service.NormalizeNewCleanRuleAsync(" /clean/gy ") == "/clean/gy" &&
            await service.NormalizeNewMatchRuleAsync("   ") is null, "normalization is native input parsing, not execution");
        await service.ClearAsync();
        Check(Same(service.GetSavedSnapshot(), defaults) && runtime.Calls.Count == 0 &&
            runtime.Notifications.SequenceEqual(["film.rules", "film.rules"]), "clear persists native initial defaults and only changed blocks are notified");
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
        await using var store = new FilmNativeSettingsFixture();
        await using var service = new FilmRuleService(runtime, store);
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
        await using var store = new FilmNativeSettingsFixture();
        await using var service = new FilmRuleService(runtime, store);
        var work = service.ProcessAsync(new(FilmRuleSection.Movie, "first")); await entered.Task;
        var queued = service.ProcessAsync(new(FilmRuleSection.Movie, "queued"));
        var close = service.PrepareShutdownAsync();
        Check(!close.IsCompleted, "preparation waits physical RPC drain");
        drained.SetResult();
        foreach (var task in new[] { work, queued }) { try { await task; } catch (OperationCanceledException) { } }
        await close;
        Check(runtime.Calls.Count == 1, "queued calls do not execute after preparation");
        await service.DisposeAsync(); runtime.Respond = null;
        await using var reopened = new FilmRuleService(runtime, store);
        await reopened.InitializeAsync();
        Check(reopened.GetSavedSnapshot().EpisodeRules.Count > 0 && runtime.Calls.Count == 1, "a new owner reads native settings without new RPCs");
    }
    private static async Task Unavailable()
    {
        const string code = "NETWORK_ERROR";
        var runtime = new FilmRuleConsumerRuntime { Respond = (_, _, _) => throw new BridgeException(code) };
        await using var store = new FilmNativeSettingsFixture();
        await using var service = new FilmRuleService(runtime, store);
        await service.InitializeAsync();
        var next = service.GetSavedSnapshot() with { MovieRules = [new("offline", "$&", "gy")] };
        await service.SaveAsync(next);
        Check(Same(service.GetSavedSnapshot(), next) && await service.NormalizeNewCleanRuleAsync(" /offline/g ") == "/offline/g" &&
            runtime.Calls.Count == 0, "offline native read save and normalization never ask the runtime");
        try { await service.ProcessAsync(new(FilmRuleSection.Movie, "offline")); throw new Exception("Expected runtime error"); }
        catch (BridgeException error) { Check(error.Code == code, "actual preview preserves the runtime failure"); }
        Check(runtime.Calls.Count == 1 && Same(service.GetSavedSnapshot(), next), "no local engine fallback or configuration rollback");
    }
    private static async Task NativeWrite()
    {
        await using var store = new FilmNativeSettingsFixture();
        var runtime = new FilmRuleConsumerRuntime();
        await using var service = new FilmRuleService(runtime, store);
        await service.InitializeAsync();
        var next = service.GetSavedSnapshot() with { MovieRules = [new("admitted")] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Writing = async (_) => { entered.TrySetResult(); await receipt.Task; };
        using var caller = new CancellationTokenSource();
        var save = service.SaveAsync(next, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            var close = service.PrepareShutdownAsync();
            Check(!save.IsCompleted && !close.IsCompleted && !store.WriteToken.CanBeCanceled,
                "native admission strips caller cancellation and shutdown waits for the actual receipt");
            receipt.TrySetResult(); await save; await close;
            Check(Same(service.GetSavedSnapshot(), next), "an admitted write still publishes its committed snapshot");
            await service.DisposeAsync();
            await using var reopened = new FilmRuleService(runtime, store);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int writes = store.Writes;
            try { await reopened.SaveAsync(new(), cancelled.Token); throw new Exception("Cancelled edit wrote"); }
            catch (OperationCanceledException) { }
            Check(store.Writes == writes && runtime.Notifications.Count == 1, "pre-admission cancellation never writes or notifies");
        }
        finally { receipt.TrySetResult(); }
    }
    private static async Task NativeRead()
    {
        await using var store = new FilmNativeSettingsFixture();
        var runtime = new FilmRuleConsumerRuntime();
        await using var service = new FilmRuleService(runtime, store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Reading = async (_) => { entered.TrySetResult(); await receipt.Task; };
        var read = service.InitializeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = service.PrepareShutdownAsync();
            Check(!close.IsCompleted, "shutdown waits for the actual admitted native read");
            receipt.TrySetResult();
            try { await read; throw new Exception("Cancelled read published"); }
            catch (OperationCanceledException) { }
            await close;
            Check(store.Writes == 0 && runtime.Calls.Count == 0, "cancelled local read never writes or falls back to RPC");
            await service.DisposeAsync(); store.Reading = null;
            await using var reopened = new FilmRuleService(runtime, store);
            await reopened.InitializeAsync();
            Check(reopened.GetSavedSnapshot().EpisodeRules.Count > 0, "a new owner reads native defaults after physical drain");
        }
        finally { receipt.TrySetResult(); }
    }
    private static async Task NotificationFailure()
    {
        await using var store = new FilmNativeSettingsFixture();
        var session = new FilmSettingsNotificationSession();
        await using var runtime = new AppRuntimeConnectionHost(_ => Task.FromResult<IRuntimeSession>(session));
        runtime.Start(); await runtime.CaptureConnectionAsync();
        await using var service = new FilmRuleService(runtime, store);
        await service.InitializeAsync();
        var next = service.GetSavedSnapshot() with { AggregateRegexRules = ["notify"] };
        var save = service.SaveAsync(next);
        try
        {
            await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await save.WaitAsync(TimeSpan.FromSeconds(5));
            Check(save.IsCompletedSuccessfully && !session.Finished.Task.IsCompleted &&
                session.Input.EnumerateObject().Count() == 1 && session.Input.GetProperty("block").GetString() == "film.rules",
                "the production App facade sends only the block name and the native save does not wait for its transport receipt");
            await save; string revision = service.Revision;
            session.Receipt.TrySetException(new BridgeException("NETWORK_ERROR"));
            await session.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.InitializeAsync();
            Check(Same(service.GetSavedSnapshot(), next) && service.Revision == revision,
                "a failed notification transport never rolls back the committed native settings");
        }
        finally { session.Receipt.TrySetException(new BridgeException("CLOSED")); }
    }
    private static async Task Corrupt()
    {
        await using var store = new FilmNativeSettingsFixture();
        store.Seed("film.rules.v1", JsonSerializer.SerializeToElement(new { version = 2, rules = new FilmRuleSet() }, FilmRuleService.Json));
        var runtime = new FilmRuleConsumerRuntime();
        await using var service = new FilmRuleService(runtime, store);
        await StoreError(DocumentStoreError.Corrupt, () => service.InitializeAsync());
        await StoreError(DocumentStoreError.Corrupt, () => service.SaveAsync(new()));
        Check(store.Writes == 0 && runtime.Calls.Count == 0 && runtime.Notifications.Count == 0,
            "corruption is a local failure, never a default overwrite or RPC fallback");
    }
    private static async Task StoreError(DocumentStoreError code, Func<Task> operation)
    {
        try { await operation(); throw new Exception("Expected native storage error"); }
        catch (DocumentStoreException error) { Check(error.Error == code, "native storage failure is preserved"); }
    }
}
