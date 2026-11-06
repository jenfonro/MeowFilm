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

    private static async Task AssertRecovered(FilmRuleService service)
    {
        var result = await service.ProcessAsync(new(FilmRuleSection.Search, "作品2", "作品2"));
        Check(result.Type == "success" && result.GroupKey == "作品2",
            "the same service resumes with its saved rules after an interrupted native call");
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
