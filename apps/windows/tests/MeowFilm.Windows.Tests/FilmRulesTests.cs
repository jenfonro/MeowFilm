using System.Diagnostics;
using System.Text.Json;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Core.Scripting;
using MeowFilm.Platform.Scripting;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class FilmRulesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("film-rules: draft and saved use the identical engine request without saving tests", SameRequest);
        yield return new("film-rules: failed writes preserve the last saved snapshot and fingerprint", FailedWrite);
        yield return new("film-rules: accepted writes settle on cancellation and shutdown", AcceptedWrite);
        yield return new("film-rules: damaged documents are reported without replacing them", CorruptStorage);
        yield return new("film-rules: shutdown cancels processing drains the engine and borrows storage", Shutdown);
        yield return new("film-rules: prepare cancels active and queued work then resume preserves rules", PrepareAndResume);
        yield return new("film-rules: cancelled preparation waits for accepted writes before resume", PrepareAcceptedWrite);
        yield return new("film-rules: preparation and disposal share a drain without losing queued calls", PrepareAndDispose);
        yield return new("film-rules: native engine matches fixed dashboard cases and resets cached expressions", NativeReferenceCases);
        yield return new("film-rules: native defaults full-panel save restart and restore match the dashboard", NativePersistence);
        yield return new("film-rules: native cancellation and timeout stop execution then recover", NativeInterruption);
        yield return new("film-rules: native cleaning saves match Go whitespace stages and preserve unsaved drafts", NativeSaveWhitespace);
        yield return new("film-rules: native compilation cancels drains shutdown resumes and disposes", NativeCompilationLifecycle);
        yield return new("film-rules: native empty episode matching keeps draft and saved cleaning without implicit writes", NativeEmptyEpisodeMatching);
        yield return new("film-rules: native forward capture prescans cancel drain and time out then recover", NativeForwardCaptureLifecycle);
        yield return new("film-rules: native global empty replacement cancels drains and times out then recovers", NativeGlobalCleaningLifecycle);
        yield return new("film-rules: warmed native E3 references cancel drain time out and recover saved rules", NativeReferenceLifecycle);
        yield return new("film-rules: native forward and backward references agree for draft and saved rules", NativeReferenceDirections);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static JsonElement Encode(object value) => JsonSerializer.SerializeToElement(value, Json);
    private static bool Same(object left, object right) => JsonElement.DeepEquals(Encode(left), Encode(right));
    private static FilmRuleSet FixtureRules(string replacement) => new()
    {
        EpisodeCleanRegexRules = ["noise"],
        EpisodeRules = [new("^.*$", replacement)],
        MovieRules = [new("^.*$", replacement)],
        AggregateRegexRules = ["noise"]
    };

    // Fixed protocol receipts only. The substitute never evaluates or rewrites rules.
    private static JsonElement Receipt(FilmRuleSet rules, bool changed = false) => Encode(new
    {
        rules,
        document = new
        {
            episodeCleanRegexRules = rules.EpisodeCleanRegexRules,
            episodeRules = rules.EpisodeRules.Select(row => JsonSerializer.Serialize(row, Json)).ToArray(),
            movieRules = rules.MovieRules.Select(row => JsonSerializer.Serialize(row, Json)).ToArray(),
            aggregateRegexRules = rules.AggregateRegexRules
        },
        changed
    });

    private static readonly FilmRuleResult FixedResult = new()
    {
        Section = FilmRuleSection.Movie, Type = "success", Text = "命中第 1 条：saved",
        InputText = "film", ProcessedText = "film", Matched = true, MatchedRuleIndex = 1,
        RewriteApplied = true, RewrittenText = "saved"
    };

    private static async Task SameRequest()
    {
        var rules = FixtureRules("saved");
        var store = new MemoryStore();
        var engine = new RecordingEngine((request, _) => Task.FromResult(
            request.GetProperty("operation").GetString() == "initialize" ? Receipt(rules, true) : Encode(FixedResult)));
        await using var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        await service.InitializeAsync();
        Check(store.Reads == 1 && store.Writes == 1, "initialization is idempotent");
        var request = new FilmRuleRequest(FilmRuleSection.Movie, "film", "query2");
        var savedResult = await service.ProcessAsync(request);
        var draftResult = await service.ProcessAsync(request, service.GetSavedSnapshot());
        Check(Same(savedResult, draftResult), "same snapshot and input yield the same public result");
        var calls = engine.Requests.Where(value => value.GetProperty("operation").GetString() == "process").ToArray();
        Check(calls.Length == 2 && JsonElement.DeepEquals(calls[0], calls[1]),
            "draft and saved dispatch identical operation, text, query and rules");
        Check(store.Reads == 1 && store.Writes == 1, "processing never writes the draft");
        var draft = service.GetSavedSnapshot() with { MovieRules = [new("draft", "draft")] };
        await service.ProcessAsync(request, draft);
        Check(service.GetSavedSnapshot().MovieRules[0].Replace == "saved", "draft does not replace saved state");
        Check(engine.Requests.Last().GetProperty("request").GetProperty("query").GetString() == "query2",
            "the optional search context survives service dispatch");
        Check(store.Namespaces.All(value => value == FilmRuleService.DocumentNamespace),
            "only the declared namespace is accessed");
    }

    private static async Task FailedWrite()
    {
        var original = FixtureRules("original");
        var next = FixtureRules("next");
        var store = new MemoryStore();
        var engine = new RecordingEngine((request, _) => Task.FromResult(
            request.GetProperty("operation").GetString() == "initialize" ? Receipt(original, true) : Receipt(next)));
        await using var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        byte[] before = store.Data!.ToArray();
        store.FailNextWrite = DocumentStoreError.Storage;
        try { await service.SaveAsync(next); throw new InvalidOperationException("Expected storage failure"); }
        catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Storage, "storage error surfaces"); }
        Check(Same(service.GetSavedSnapshot(), original) && store.Data!.SequenceEqual(before),
            "failed save publishes neither rules nor receipt");
        await service.SaveAsync(next);
        Check(Same(service.GetSavedSnapshot(), next), "retry still uses the last successful fingerprint");
        store.FailNextWrite = DocumentStoreError.Conflict;
        try { await service.SaveAsync(original); throw new InvalidOperationException("Expected conflict"); }
        catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Conflict, "CAS conflict surfaces"); }
        Check(Same(service.GetSavedSnapshot(), next), "conflict keeps last acknowledged rules");
    }

    private static async Task Shutdown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore();
        var engine = new RecordingEngine(async (request, token) =>
        {
            if (request.GetProperty("operation").GetString() == "initialize") return Receipt(FixtureRules("saved"));
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Encode(FixedResult);
        });
        var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        var processing = service.ProcessAsync(new(FilmRuleSection.Movie, "film"));
        await entered.Task;
        await service.DisposeAsync();
        await service.DisposeAsync();
        try { await processing; throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) {}
        Check(engine.Disposals == 1 && !store.Disposed, "engine disposed once; shared store remains owned by app");
        try { await service.ProcessAsync(new(FilmRuleSection.Movie, "film")); throw new InvalidOperationException("Expected closed service"); }
        catch (ObjectDisposedException) {}
    }

    private static async Task Failure<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task PrepareAndResume()
    {
        var rules = FixtureRules("saved");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var store = new MemoryStore();
        var engine = new RecordingEngine(async (request, token) =>
        {
            if (request.GetProperty("operation").GetString() == "initialize") return Receipt(rules, true);
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); await release.Task; }
            }
            return Encode(FixedResult);
        });
        await using var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        var request = new FilmRuleRequest(FilmRuleSection.Movie, "film");
        var processing = service.ProcessAsync(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = service.ProcessAsync(request);
            var queuedSave = service.SaveAsync(FixtureRules("unsaved"));
            var preparing = service.PrepareShutdownAsync();
            var repeated = service.PrepareShutdownAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Failure<OperationCanceledException>(() => queued, "queued processing must receive cancellation");
            await Failure<OperationCanceledException>(() => queuedSave, "queued save must receive cancellation");
            Check(!preparing.IsCompleted && !repeated.IsCompleted, "preparation waits for active engine work to settle");
            await Failure<InvalidOperationException>(() =>
            {
                service.Resume();
                return Task.CompletedTask;
            }, "resume before physical drain must fail");
            await Failure<InvalidOperationException>(() => service.ProcessAsync(request),
                "preparation must pause new admission");
            Check(Same(service.GetSavedSnapshot(), rules) && store.Writes == 1,
                "cancelled queued writes do not change saved rules or persistence");
            release.TrySetResult();
            await Task.WhenAll(preparing, repeated).WaitAsync(TimeSpan.FromSeconds(10));
            await Failure<OperationCanceledException>(() => processing, "active processing must receive cancellation");
            Check(engine.Disposals == 0, "preparation keeps the original engine alive");
            service.Resume();
            service.Resume();
            var result = await service.ProcessAsync(request);
            Check(Same(result, FixedResult) && Same(service.GetSavedSnapshot(), rules),
                "resume accepts new processing using the same saved snapshot");
            Check(calls == 2 && engine.Disposals == 0 && store.Reads == 1,
                "queued calls never enter the engine and resume does not reconstruct or reload it");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task PrepareAcceptedWrite()
    {
        var original = FixtureRules("original");
        var next = FixtureRules("next");
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore();
        var engine = new RecordingEngine((request, _) => Task.FromResult(
            request.GetProperty("operation").GetString() switch
            {
                "initialize" => Receipt(original, true),
                "save" => Receipt(next),
                _ => Encode(FixedResult)
            }));
        await using var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        store.BeforeCommit = async () => { admitted.TrySetResult(); await receipt.Task; };
        var save = service.SaveAsync(next);
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var request = new FilmRuleRequest(FilmRuleSection.Movie, "film");
            var queued = service.ProcessAsync(request);
            using var cancellation = new CancellationTokenSource();
            var waiting = service.PrepareShutdownAsync(cancellation.Token);
            cancellation.Cancel();
            await Failure<OperationCanceledException>(() => waiting, "caller may cancel only its preparation wait");
            var drained = service.PrepareShutdownAsync();
            Check(!drained.IsCompleted && Same(service.GetSavedSnapshot(), original),
                "cancelled wait leaves the accepted write draining and the old snapshot published");
            await Failure<InvalidOperationException>(() =>
            {
                service.Resume();
                return Task.CompletedTask;
            }, "cancelled preparation wait cannot authorize early resume");
            await Failure<InvalidOperationException>(() => service.ProcessAsync(request),
                "admission stays paused after the preparation waiter is cancelled");
            receipt.TrySetResult();
            Check(Same(await save.WaitAsync(TimeSpan.FromSeconds(10)), next), "accepted write returns its real receipt");
            await drained.WaitAsync(TimeSpan.FromSeconds(10));
            await Failure<OperationCanceledException>(() => queued, "queued process settles before resume");
            Check(Same(service.GetSavedSnapshot(), next), "prepared snapshot includes the acknowledged write");
            service.Resume();
            await service.ProcessAsync(request);
            Check(JsonElement.DeepEquals(engine.Requests.Last().GetProperty("rules"), Encode(next)),
                "resumed processing receives the acknowledged saved rules");
            store.BeforeCommit = null;
            await service.SaveAsync(next);
            Check(store.Writes == 3 && engine.Disposals == 0, "resumed saving retains the new CAS fingerprint and engine");
        }
        finally { receipt.TrySetResult(); }
    }

    private static async Task PrepareAndDispose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore();
        var engine = new RecordingEngine(async (request, token) =>
        {
            if (request.GetProperty("operation").GetString() == "initialize") return Receipt(FixtureRules("saved"), true);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); await release.Task; }
            return Encode(FixedResult);
        });
        await using var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        var request = new FilmRuleRequest(FilmRuleSection.Movie, "film");
        var processing = service.ProcessAsync(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = Enumerable.Range(0, 8).Select(_ => service.ProcessAsync(request)).ToArray();
            var preparation = service.PrepareShutdownAsync();
            var disposal = service.DisposeAsync().AsTask();
            var repeatedPreparation = service.PrepareShutdownAsync();
            var repeatedDisposal = service.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!disposal.IsCompleted && !repeatedDisposal.IsCompleted && engine.Disposals == 0,
                "neither shutdown path can release an active engine");
            await Failure<ObjectDisposedException>(() =>
            {
                service.Resume();
                return Task.CompletedTask;
            }, "disposal permanently fences resume");
            await Failure<ObjectDisposedException>(() => service.ProcessAsync(request), "closed admission rejects new calls");
            release.TrySetResult();
            await Failure<OperationCanceledException>(() => processing, "active processing settles");
            foreach (var call in queued)
                await Failure<OperationCanceledException>(() => call, "each queued call settles without a lost signal");
            await Task.WhenAll(preparation, repeatedPreparation, disposal, repeatedDisposal)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await service.PrepareShutdownAsync();
            await service.DisposeAsync();
            Check(engine.Disposals == 1 && !store.Disposed && engine.Requests.Count == 2,
                "shared drain releases the engine once, keeps borrowed storage and never replays queued calls");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task AcceptedWrite()
    {
        var rules = FixtureRules("saved");
        var store = new MemoryStore();
        var engine = new RecordingEngine((_, _) => Task.FromResult(Receipt(rules, true)));
        var service = new FilmRuleService(engine, store);
        await service.InitializeAsync();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeCommit = async () => { admitted.TrySetResult(); await receipt.Task; };
        using var cancellation = new CancellationTokenSource();
        var save = service.SaveAsync(rules, cancellation.Token);
        await admitted.Task;
        cancellation.Cancel();
        var closing = service.DisposeAsync().AsTask();
        Check(!closing.IsCompleted && engine.Disposals == 0, "shutdown waits for the admitted storage receipt");
        receipt.TrySetResult();
        Check(Same(await save, rules), "acknowledged write returns its saved snapshot despite caller cancellation");
        await closing;
        Check(engine.Disposals == 1 && store.Data is not null, "engine release follows the acknowledged write");
    }

    private static async Task CorruptStorage()
    {
        foreach (string data in new[] { "{", "[]", """{"episodeCleanRegexRules": [42], "episodeRules": [], "movieRules": [], "aggregateRegexRules": []}""" })
        {
            var store = new MemoryStore { Data = System.Text.Encoding.UTF8.GetBytes(data) };
            var engine = new RecordingEngine((_, _) => throw new InvalidOperationException("Engine must not receive corrupt storage"));
            await using var service = new FilmRuleService(engine, store);
            try { await service.InitializeAsync(); throw new InvalidOperationException("Expected corrupt storage"); }
            catch (DocumentStoreException error) { Check(error.Error == DocumentStoreError.Corrupt, "invalid document reported"); }
            Check(store.Writes == 0 && engine.Requests.Count == 0, "corrupt data is not silently seeded or overwritten");
        }
    }

    private static FilmRuleService NativeService(IDocumentStore store) =>
        new(new NativeJavaScriptJsonEngine(FilmRuleService.LoadProgramSource()), store);

    private static async Task NativeReferenceCases()
    {
        string package = Path.Combine(TestWorkspace.SourceRoot, "packages", "film-rules");
        Check(FilmRuleService.LoadProgramSource() == await File.ReadAllTextAsync(Path.Combine(package, "film-rules.js")),
            "embedded program is the final shared source");
        var store = new MemoryStore();
        await using var service = NativeService(store);
        await service.InitializeAsync();
        var initial = service.GetSavedSnapshot();
        foreach (var section in new[] { FilmRuleSection.Episode, FilmRuleSection.Movie, FilmRuleSection.Search })
            await service.RestoreDefaultsAsync(section);
        var restored = service.GetSavedSnapshot();
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package, "test", "reference-cases.json")));
        int count = 0;
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            string name = fixture.GetProperty("name").GetString()!;
            var rules = fixture.GetProperty("setup").GetString() switch
            {
                "initial" => initial, "restored" => restored, "empty" => new FilmRuleSet(),
                _ => throw new InvalidOperationException("Unknown fixture setup")
            };
            if (fixture.TryGetProperty("rules", out var edits)) rules = WithEdits(rules, edits);
            var request = fixture.GetProperty("request").Deserialize<FilmRuleRequest>(Json)!;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var actual = Encode(await service.ProcessAsync(request, rules));
                foreach (var expected in fixture.GetProperty("expected").EnumerateObject())
                    Check(JsonElement.DeepEquals(actual.GetProperty(expected.Name), expected.Value),
                        name + ": " + expected.Name + "; actual=" + actual.GetProperty(expected.Name));
            }
            count++;
        }
        Check(count > 0, "fixed dashboard fixture inventory is nonempty");
        int writes = store.Writes;
        var sameRequest = new FilmRuleRequest(FilmRuleSection.Search, "作品2", "作品2");
        Check(Same(await service.ProcessAsync(sameRequest), await service.ProcessAsync(sameRequest, restored)),
            "real native draft and saved outputs match");
        Check(writes == store.Writes, "real native process does not persist");
        Console.WriteLine($"EVIDENCE film-rules dashboard fixtures={count}; repeated=2; engine=native");
    }

    private static FilmRuleSet WithEdits(FilmRuleSet rules, JsonElement edits) => rules with
    {
        EpisodeCleanRegexRules = edits.TryGetProperty("episodeCleanRegexRules", out var clean)
            ? clean.Deserialize<string[]>(Json)! : rules.EpisodeCleanRegexRules,
        EpisodeRules = edits.TryGetProperty("episodeRules", out var episode)
            ? episode.Deserialize<FilmMatchRule[]>(Json)! : rules.EpisodeRules,
        MovieRules = edits.TryGetProperty("movieRules", out var movie)
            ? movie.Deserialize<FilmMatchRule[]>(Json)! : rules.MovieRules,
        AggregateRegexRules = edits.TryGetProperty("aggregateRegexRules", out var search)
            ? search.Deserialize<string[]>(Json)! : rules.AggregateRegexRules
    };

    private static async Task NativePersistence()
    {
        string root = TestWorkspace.DataPath("film-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var store = new LocalDocumentStore(root, [FilmRuleService.DocumentNamespace]))
            await using (var service = NativeService(store))
            {
                await service.InitializeAsync();
                Check(service.GetSavedSnapshot().EpisodeRules.Count == 2, "first initialization has database defaults");
                var empty = await service.SaveAsync(new());
                Check(Same(empty, new FilmRuleSet()), "all four groups remain empty immediately after save");
                await service.InitializeAsync();
                Check(Same(service.GetSavedSnapshot(), empty), "idempotent initialization does not refill this session");
                Check((await service.ProcessAsync(new(FilmRuleSection.Episode, "E1"))).Text == "无匹配规则",
                    "saved empty rules reach the same processing entry");
            }
            await using (var store = new LocalDocumentStore(root, [FilmRuleService.DocumentNamespace]))
            await using (var service = NativeService(store))
            {
                await service.InitializeAsync();
                var initialized = service.GetSavedSnapshot();
                Check(initialized.EpisodeRules.Count == 2 && initialized.EpisodeCleanRegexRules.Count == 6 &&
                    initialized.MovieRules.Count == 1 && initialized.AggregateRegexRules.Count == 7,
                    "restart refills each empty group from database defaults");
                var added = await service.NormalizeNewMatchRuleAsync(@" /(\d+)/g ", @" \1 ");
                Check(added == new FilmMatchRule(@"(\d+)", @"\1", "g"), "new-match parsing stays in shared JS");
                Check(await service.NormalizeNewCleanRuleAsync(@" /foo/m ") == "/foo/m",
                    "new-clean parsing preserves explicit flags");
                var movieRows = new[] { new FilmMatchRule("[", " keep invalid ", "") };
                var draft = initialized with { MovieRules = movieRows, AggregateRegexRules = ["  custom  ", "", "custom"] };
                var restored = await service.RestoreDefaultsAsync(FilmRuleSection.Episode, draft);
                movieRows[0] = new("changed after save");
                Check(restored.EpisodeRules.Count == 3 && restored.EpisodeCleanRegexRules.Count == 6,
                    "episode restore uses the page defaults for both groups");
                Check(restored.MovieRules[0] == new FilmMatchRule("[", " keep invalid ", "") &&
                    restored.AggregateRegexRules.SequenceEqual(["custom", "custom"]),
                    "restore also saves other drafts, preserves replace whitespace and keeps duplicate rows");
                var raw = await store.ReadAsync(FilmRuleService.DocumentNamespace);
                using var document = JsonDocument.Parse(raw.Data!);
                Check(document.RootElement.GetProperty("movieRules")[0].ValueKind == JsonValueKind.String,
                    "store retains encoded matching rows, avoiding extra normalization of the response snapshot");
            }
            await using (var store = new LocalDocumentStore(root, [FilmRuleService.DocumentNamespace]))
            await using (var service = NativeService(store))
            {
                await service.InitializeAsync();
                Check(service.GetSavedSnapshot().EpisodeRules.Count == 3 &&
                    service.GetSavedSnapshot().MovieRules[0].Pattern == "[",
                    "nonempty restored and invalid saved rules survive a real store reopen");
                Check((await service.ProcessAsync(new(FilmRuleSection.Movie, "film"))).Text == "未命中（#1 正则无效）",
                    "invalid rules are reported at processing, not rejected on save");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task NativeSaveWhitespace()
    {
        var store = new MemoryStore();
        var rows = new[] { "\u0085foo\u0085", "\u0085", "\u0085\ufeffbar\ufeff\u0085",
            "a\u0085b", "\ufeff\u0085baz\u0085\ufeff", "\u200bkeep\u200b" };
        var expected = new[] { "foo", "bar", "a\u0085b", "baz", "\u200bkeep\u200b" };
        var draft = new FilmRuleSet
        {
            EpisodeCleanRegexRules = rows, AggregateRegexRules = rows,
            EpisodeRules = [new("^$", "E1")],
            MovieRules = [new("\u0085movie\u0085", "\u0085keep\u0085")]
        };
        await using (var service = NativeService(store))
        {
            await service.InitializeAsync();
            var request = new FilmRuleRequest(FilmRuleSection.Search, "foo");
            Check((await service.ProcessAsync(request, draft)).ProcessedText == "foo",
                "draft cleaning retains NEL before the server save stage");
            var saved = await service.SaveAsync(draft);
            Check(saved.EpisodeCleanRegexRules.SequenceEqual(expected) && saved.AggregateRegexRules.SequenceEqual(expected),
                "both clean groups discard Go whitespace-only rows and decode mixed NEL/BOM correctly");
            Check(Same(saved.MovieRules, draft.MovieRules), "Go trim does not enter encoded matching-row JSON");
            using var raw = JsonDocument.Parse(store.Data!);
            Check(raw.RootElement.GetProperty("aggregateRegexRules")[1].GetString() == "\ufeffbar\ufeff",
                "Go save keeps BOM; response JS decode removes it without changing stored bytes");
            Check((await service.ProcessAsync(request)).ProcessedText == "" &&
                (await service.ProcessAsync(request, draft)).ProcessedText == "foo",
                "saved processing cleans foo without modifying the caller's unsaved draft");
            Check((await service.ProcessAsync(new(FilmRuleSection.Episode, "foo"))).ProcessedText == "",
                "episode cleaning uses the same normalized saved rules");
            var restored = await service.RestoreDefaultsAsync(FilmRuleSection.Movie, draft);
            Check(restored.AggregateRegexRules.SequenceEqual(expected), "section restore also uses the save chain");
        }
        await using var reopened = NativeService(store);
        await reopened.InitializeAsync();
        Check(reopened.GetSavedSnapshot().EpisodeCleanRegexRules.SequenceEqual(expected) &&
            reopened.GetSavedSnapshot().AggregateRegexRules.SequenceEqual(expected),
            "normalized nonempty clean rules survive a new native owner");
    }

    private static async Task NativeInterruption()
    {
        await using var service = NativeService(new MemoryStore());
        await service.InitializeAsync();
        var expensive = new FilmRuleSet { MovieRules = [new("^(a+)+$", "x")] };
        var request = new FilmRuleRequest(FilmRuleSection.Movie, new string('a', 64) + "!");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await service.ProcessAsync(request, expensive, cancellation.Token); throw new InvalidOperationException("Expected native cancellation"); }
            catch (OperationCanceledException) { Check(cancellation.IsCancellationRequested, "caller cancellation reached native execution"); }
        }
        await AssertRecovered(service);
        try { await service.ProcessAsync(request, expensive); throw new InvalidOperationException("Expected native timeout"); }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_TIMEOUT", "native deadline interrupts the regular expression"); }
        await AssertRecovered(service);
    }

    private static async Task NativeEmptyEpisodeMatching()
    {
        var store = new MemoryStore();
        await using var service = NativeService(store);
        await service.InitializeAsync();
        var original = service.GetSavedSnapshot();
        byte[] originalBytes = store.Data!.ToArray();
        int writes = store.Writes;
        var rules = new FilmRuleSet { EpisodeCleanRegexRules = [@"^noise\s*"] };
        var request = new FilmRuleRequest(FilmRuleSection.Episode, "noise E2");
        var draft = await service.ProcessAsync(request, rules);
        Check(store.Writes == writes && store.Data!.SequenceEqual(originalBytes) &&
            Same(service.GetSavedSnapshot(), original), "native draft processing does not save or replace the saved snapshot");
        var savedRules = await service.SaveAsync(rules);
        Check(Same(savedRules, rules) && Same(service.GetSavedSnapshot(), rules),
            "saving the empty matching group preserves all four groups without refilling defaults");
        byte[] savedBytes = store.Data!.ToArray();
        var saved = await service.ProcessAsync(request);
        Check(Same(draft, saved), "empty matching groups yield identical public draft and saved results");
        foreach (var result in new[] { draft, saved })
        {
            Check(result.Type == "error" && result.Text == "无匹配规则" &&
                !result.Matched && result.MatchedRuleIndex is null &&
                !result.RewriteApplied && result.RewrittenText is null,
                "empty matching groups preserve the reference error and no-match/no-rewrite state");
            Check(result.InputText == "noise E2" && result.ProcessedText == "E2" && result.OutputText == "E2" &&
                result.Previews.SequenceEqual(["E2"]) && result.Errors.Count == 0,
                "completed cleaning is exposed by both public text fields and the preview");
        }
        var control = await service.ProcessAsync(request, rules with { EpisodeRules = [new("E2")] });
        Check(control.Matched && control.ProcessedText == "E2" && control.OutputText == "E2" &&
            control.Previews.SequenceEqual(["E2"]), "nonempty matching control keeps the same cleaned output");
        Check(store.Writes == writes + 1 && store.Data!.SequenceEqual(savedBytes) &&
            Same(service.GetSavedSnapshot(), rules),
            "only the explicit save writes; saved processing and the control draft do not persist or refill defaults");
    }

    private static Task NativeForwardCaptureLifecycle()
    {
        string pattern = string.Concat(Enumerable.Repeat(@"\k<x>", 9999)) +
            new string('a', 1_048_576) + "(?<x>b)";
        return NativeFixedWorkLifecycle("forward-capture", new(FilmRuleSection.Movie, "b"),
            new FilmRuleSet { MovieRules = [new(pattern, "", "i")] });
    }

    private static Task NativeGlobalCleaningLifecycle() =>
        NativeFixedWorkLifecycle("global-empty-replacement", new(FilmRuleSection.Episode, new string('a', 1_048_576)),
            new FilmRuleSet { EpisodeCleanRegexRules = ["(?=a{4096})a"] });

    private static Task NativeReferenceLifecycle()
    {
        string pattern = "^(?=(a{1048576}))" + string.Concat(Enumerable.Repeat(@"(?=\1)", 9999));
        return NativeFixedWorkLifecycle("E3-successful-lookahead-reference", new(FilmRuleSection.Movie, new string('a', 1_048_576)),
            new FilmRuleSet { MovieRules = [new(pattern, "", "i")] }, new(FilmRuleSection.Movie, "b"));
    }

    private static async Task NativeReferenceDirections()
    {
        var store = new MemoryStore();
        await using var service = NativeService(store);
        await service.InitializeAsync();
        foreach (var (pattern, text, flags, matched) in new[]
        {
            (@"^(a)\1$", "aA", "i", true), (@"^(a)\1$", "aA", "u", false),
            (@"^..(?<=\1(a))$", "Aa", "i", true), (@"^..(?<=\1(a))$", "Aa", "u", false),
            (@"^(😀)\1$", "😀😀", "u", true), (@"^..(?<=\1(😀))$", "😀😀", "u", true)
        })
        {
            var rules = new FilmRuleSet { MovieRules = [new(pattern, "x", flags)] };
            var request = new FilmRuleRequest(FilmRuleSection.Movie, text);
            int writes = store.Writes;
            var draft = await service.ProcessAsync(request, rules);
            Check(store.Writes == writes, "reference draft processing does not write");
            await service.SaveAsync(rules);
            var saved = await service.ProcessAsync(request);
            Check(Same(draft, saved), "both reference directions use the identical draft/saved rule path");
            Check(saved.Matched == matched && saved.Errors.Count == 0 &&
                saved.RewriteApplied == matched && saved.OutputText == (matched ? "x" : text),
                "ordinary references preserve case/Unicode matching and replacement semantics");
            Check(store.Writes == writes + 1, "only the explicit reference rule save writes");
        }
    }

    private static async Task NativeFixedWorkLifecycle(string scenario, FilmRuleRequest request, FilmRuleSet rules,
        FilmRuleRequest? executionWarmup = null)
    {
        var store = new MemoryStore();
        await using var service = NativeService(store);
        var ordinary = new FilmRuleSet { MovieRules = [new("^b$", "x", "i")] };
        await service.SaveAsync(ordinary);
        await AssertOrdinaryMovieRecovered(service);
        int writes = store.Writes;
        byte[] savedBytes = store.Data!.ToArray();
        await WarmExecution();
        // The production engine keeps its two-second budget; this is only scheduling allowance.
        var allowance = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource();
        var callWatch = Stopwatch.StartNew();
        var active = service.ProcessAsync(request, rules, cancellation.Token);
        double setupMilliseconds = callWatch.Elapsed.TotalMilliseconds;
        await Task.Delay(200);
        double pendingMilliseconds = callWatch.Elapsed.TotalMilliseconds;
        Check(!active.IsCompleted, scenario + ": fixed native work is still pending before cancellation");
        var cancelWatch = Stopwatch.StartNew();
        cancellation.Cancel();
        var prepare = service.PrepareShutdownAsync();
        try
        {
            await Task.WhenAll(active, prepare).WaitAsync(allowance);
            throw new InvalidOperationException(scenario + ": expected native cancellation");
        }
        catch (OperationCanceledException) { Check(cancellation.IsCancellationRequested, scenario + ": caller cancellation requested"); }
        Check(active.IsCanceled && prepare.IsCompletedSuccessfully,
            scenario + ": both the original call and preparation really settled, not just their waiters");
        double cancelDrainMilliseconds = cancelWatch.Elapsed.TotalMilliseconds;
        Check(cancelWatch.Elapsed <= allowance, scenario + ": actual cancellation and drain exceeded five seconds");
        Console.WriteLine($"EVIDENCE film-rules {scenario} setupMs={setupMilliseconds:F3}; pendingMs={pendingMilliseconds:F3}; " +
            $"cancelDrainMs={cancelDrainMilliseconds:F3}; call={active.Status}; prepare={prepare.Status}");
        service.Resume();
        await AssertOrdinaryMovieRecovered(service);
        Check(Same(service.GetSavedSnapshot(), ordinary), scenario + ": resume preserves the saved ordinary rule");

        await WarmExecution();
        var budgetWatch = Stopwatch.StartNew();
        var budgetCall = service.ProcessAsync(request, rules);
        try
        {
            await budgetCall.WaitAsync(allowance);
            throw new InvalidOperationException(scenario + ": expected production execution timeout");
        }
        catch (JavaScriptEngineException error)
        {
            Check(error.Code == "EXECUTION_TIMEOUT", scenario + ": budget interruption must not become invalid-rule output");
        }
        Check(budgetCall.IsFaulted && budgetWatch.Elapsed <= allowance,
            scenario + ": the real budget call must return EXECUTION_TIMEOUT within five seconds without external cancellation");
        Console.WriteLine($"EVIDENCE film-rules {scenario} budgetMs={budgetWatch.Elapsed.TotalMilliseconds:F3}; " +
            $"call={budgetCall.Status}; code=EXECUTION_TIMEOUT; callerCancellation=false");
        await AssertOrdinaryMovieRecovered(service);
        Check(Same(service.GetSavedSnapshot(), ordinary), scenario + ": budget failure preserves saved rules");
        Check(store.Writes == writes && store.Data!.SequenceEqual(savedBytes),
            scenario + ": warmup cancellation budget and recovery never save the draft");
        await service.DisposeAsync().AsTask().WaitAsync(allowance);
        Console.WriteLine($"EVIDENCE film-rules {scenario} savedUnchanged=true; disposed=true");

        async Task WarmExecution()
        {
            if (executionWarmup is null) return;
            var warm = await service.ProcessAsync(executionWarmup, rules);
            Check(!warm.Matched && warm.Errors.Count == 0 && warm.OutputText == executionWarmup.Text,
                scenario + ": identical draft compiles and caches without a match or rule error");
            Console.WriteLine($"EVIDENCE film-rules {scenario} warmed=true; matched=false; errors=0");
        }
    }

    private static async Task AssertOrdinaryMovieRecovered(FilmRuleService service)
    {
        var result = await service.ProcessAsync(new(FilmRuleSection.Movie, "b"));
        Check(result.Type == "success" && result.Matched && result.OutputText == "x" && result.Errors.Count == 0,
            "the same public service processes its saved ordinary movie rule after native interruption");
    }

    private static async Task AssertRecovered(FilmRuleService service)
    {
        var result = await service.ProcessAsync(new(FilmRuleSection.Search, "作品2", "作品2"));
        Check(result.Type == "success" && result.GroupKey == "作品2",
            "the same service resumes with its saved rules after an interrupted native call");
    }

    private static async Task NativeCompilationLifecycle()
    {
        await using var service = NativeService(new MemoryStore());
        await service.InitializeAsync();
        var request = new FilmRuleRequest(FilmRuleSection.Movie, "b");
        string literal = new('a', 1_048_576);
        // A normal rule of the same size remains supported: no text-length cap.
        var ordinary = await service.ProcessAsync(request, new FilmRuleSet { MovieRules = [new(literal, "x")] });
        Check(!ordinary.Matched && ordinary.Errors.Count == 0, "large ordinary rules are not rejected");
        var expensive = new FilmRuleSet { MovieRules = [new("(?<=" + literal + ")b", "x")] };
        using var cancellation = new CancellationTokenSource();
        var active = service.ProcessAsync(request, expensive, cancellation.Token);
        await Task.Delay(100);
        Check(!active.IsCompleted, "the compile cancellation scenario reached active native work");
        cancellation.Cancel();
        var prepare = service.PrepareShutdownAsync();
        await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        Check(active.IsCompleted, "preparation drains the real active compile");
        try { await active; throw new InvalidOperationException("Expected compilation cancellation"); }
        catch (OperationCanceledException) { }
        service.Resume();
        await AssertRecovered(service);
        try
        {
            await service.ProcessAsync(request, expensive).WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Expected compilation timeout");
        }
        catch (JavaScriptEngineException error) { Check(error.Code == "EXECUTION_TIMEOUT", "compile errors are not swallowed as invalid rules"); }
        await AssertRecovered(service);
        active = service.ProcessAsync(request, expensive);
        await Task.Delay(100);
        Check(!active.IsCompleted, "preparation starts while native compilation is active");
        var queued = service.ProcessAsync(request);
        await service.PrepareShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var work in new[] { active, queued })
        {
            Check(work.IsCompleted, "preparation alone cancels and drains active and queued rules");
            try { await work; throw new InvalidOperationException("Prepared processing returned success"); }
            catch (OperationCanceledException) { }
        }
        service.Resume();
        await AssertRecovered(service);
        active = service.ProcessAsync(request, expensive);
        await Task.Delay(100);
        Check(!active.IsCompleted, "the disposal scenario reached active native work");
        queued = service.ProcessAsync(request);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var work in new[] { active, queued })
        {
            Check(work.IsCompleted, "disposal drains active and queued rules");
            try { await work; throw new InvalidOperationException("Disposed processing returned success"); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class RecordingEngine(Func<JsonElement, CancellationToken, Task<JsonElement>> handler) : IJavaScriptJsonEngine
    {
        internal readonly List<JsonElement> Requests = [];
        internal int Disposals;
        public Task<JsonElement> InvokeAsync(object request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = Encode(request);
            Requests.Add(value);
            return handler(value, cancellationToken);
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class MemoryStore : IDocumentStore
    {
        internal byte[]? Data;
        internal int Reads, Writes;
        internal bool Disposed;
        internal DocumentStoreError? FailNextWrite;
        internal Func<Task>? BeforeCommit;
        internal readonly List<string> Namespaces = [];
        public Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Namespaces.Add(documentNamespace);
            Reads++;
            return Task.FromResult(new DocumentSnapshot(Data?.ToArray(),
                Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data)));
        }
        public async Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> data,
            DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Namespaces.Add(documentNamespace);
            Writes++;
            if (FailNextWrite is { } error)
            {
                FailNextWrite = null;
                throw new DocumentStoreException(error);
            }
            var current = Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data);
            if (current != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            byte[] owned = data.ToArray();
            if (BeforeCommit is not null) await BeforeCommit();
            Data = owned;
            return DocumentFingerprint.FromBytes(Data);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
