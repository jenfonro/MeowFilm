using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowFilm.Core.Scripting;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Film.Rules;

/// <summary>Owns the engine and saved rules; borrows the application's document store.</summary>
public sealed class FilmRuleService : IAsyncDisposable
{
    public const string DocumentNamespace = "film.magic-rules.v1";
    public const string ProgramResourceName = "MeowFilm.FilmRules.js";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IJavaScriptJsonEngine engine;
    private readonly IDocumentStore store;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object lifecycle = new();
    private CancellationTokenSource pending = new();
    private FilmRuleSet? saved;
    private DocumentFingerprint fingerprint = DocumentFingerprint.Missing;
    private TaskCompletionSource? drained;
    private Task? preparation;
    private Task? disposal;
    private int active;
    private int closed;

    public FilmRuleService(IJavaScriptJsonEngine engine, IDocumentStore store)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public static string LoadProgramSource()
    {
        using var stream = typeof(FilmRuleService).Assembly.GetManifestResourceStream(ProgramResourceName)
            ?? throw new InvalidOperationException("Film rule program resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token => { await InitializeCoreAsync(token).ConfigureAwait(false); return true; }, cancellationToken);

    public FilmRuleSet GetSavedSnapshot()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
        return Copy(Volatile.Read(ref saved) ??
            throw new InvalidOperationException("Film rules have not been initialized."));
    }

    public Task<FilmRuleSet> SaveAsync(FilmRuleSet rules, CancellationToken cancellationToken = default)
    {
        var owned = Copy(rules);
        return RunAsync(async token =>
        {
            await InitializeCoreAsync(token).ConfigureAwait(false);
            var result = await engine.InvokeAsync(new { operation = "save", rules = Encode(owned) }, token).ConfigureAwait(false);
            return await CommitAsync(result, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<FilmRuleSet> RestoreDefaultsAsync(FilmRuleSection section, CancellationToken cancellationToken = default) =>
        RestoreAsync(section, null, cancellationToken);

    /// <summary>Restores the selected section, then saves the entire supplied panel draft.</summary>
    public Task<FilmRuleSet> RestoreDefaultsAsync(FilmRuleSection section, FilmRuleSet draft,
        CancellationToken cancellationToken = default) => RestoreAsync(section, Copy(draft), cancellationToken);

    private Task<FilmRuleSet> RestoreAsync(FilmRuleSection section, FilmRuleSet? draft, CancellationToken cancellationToken)
    {
        string key = SectionKey(section);
        return RunAsync(async token =>
        {
            await InitializeCoreAsync(token).ConfigureAwait(false);
            var result = await engine.InvokeAsync(new
            {
                operation = "restore", section = key, rules = Encode(draft ?? saved!)
            }, token).ConfigureAwait(false);
            return await CommitAsync(result, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<FilmRuleResult> ProcessAsync(FilmRuleRequest request, CancellationToken cancellationToken = default) =>
        ProcessCoreAsync(request, null, cancellationToken);

    public Task<FilmRuleResult> ProcessAsync(FilmRuleRequest request, FilmRuleSet rules,
        CancellationToken cancellationToken = default) => ProcessCoreAsync(request, Copy(rules), cancellationToken);

    private Task<FilmRuleResult> ProcessCoreAsync(FilmRuleRequest request, FilmRuleSet? rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = SectionKey(request.Section);
        return RunAsync(async token =>
        {
            if (rules is null) await InitializeCoreAsync(token).ConfigureAwait(false);
            var result = await engine.InvokeAsync(new
            {
                operation = "process",
                request = new { section = key, text = request.Text, query = request.Query },
                rules = Encode(rules ?? saved!)
            }, token).ConfigureAwait(false);
            return result.Deserialize<FilmRuleResult>(Json) ??
                throw new InvalidOperationException("Film rule result is missing.");
        }, cancellationToken);
    }

    public Task<FilmMatchRule?> NormalizeNewMatchRuleAsync(string pattern, string replace = "",
        CancellationToken cancellationToken = default) => RunAsync(async token =>
        {
            var result = await engine.InvokeAsync(new { operation = "newMatch", pattern, replace }, token).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Null ? null : result.Deserialize<FilmMatchRule>(Json);
        }, cancellationToken);

    public Task<string> NormalizeNewCleanRuleAsync(string pattern, CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var result = await engine.InvokeAsync(new { operation = "newClean", pattern }, token).ConfigureAwait(false);
            return result.GetString() ?? "";
        }, cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (saved is not null) return;
        var snapshot = await store.ReadAsync(DocumentNamespace, cancellationToken).ConfigureAwait(false);
        JsonElement? document = null;
        if (snapshot.Data is not null)
        {
            try
            {
                using var parsed = JsonDocument.Parse(snapshot.Data);
                ValidateDocument(parsed.RootElement);
                document = parsed.RootElement.Clone();
            }
            catch (JsonException) { throw new DocumentStoreException(DocumentStoreError.Corrupt); }
        }
        var result = await engine.InvokeAsync(new { operation = "initialize", document }, cancellationToken).ConfigureAwait(false);
        var next = DecodeRules(result);
        var nextFingerprint = snapshot.Fingerprint;
        if (result.GetProperty("changed").GetBoolean())
        {
            byte[] bytes = DocumentBytes(result);
            nextFingerprint = await store.WriteAsync(DocumentNamespace, bytes, snapshot.Fingerprint, cancellationToken).ConfigureAwait(false);
        }
        fingerprint = nextFingerprint;
        Volatile.Write(ref saved, next);
    }

    private async Task<FilmRuleSet> CommitAsync(JsonElement result, CancellationToken cancellationToken)
    {
        var next = DecodeRules(result);
        byte[] bytes = DocumentBytes(result);
        var receipt = await store.WriteAsync(DocumentNamespace, bytes, fingerprint, cancellationToken).ConfigureAwait(false);
        // An accepted store write can finish despite caller cancellation. Publish
        // its receipt and matching snapshot together; never report a false rollback.
        fingerprint = receipt;
        Volatile.Write(ref saved, next);
        return Copy(next);
    }

    private static byte[] DocumentBytes(JsonElement result)
    {
        var document = result.GetProperty("document");
        ValidateDocument(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, Json);
    }

    private static void ValidateDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object) throw new DocumentStoreException(DocumentStoreError.Corrupt);
        foreach (string group in new[] { "episodeCleanRegexRules", "episodeRules", "movieRules", "aggregateRegexRules" })
        {
            if (!document.TryGetProperty(group, out var rows) || rows.ValueKind != JsonValueKind.Array ||
                rows.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.String))
                throw new DocumentStoreException(DocumentStoreError.Corrupt);
        }
    }

    private static FilmRuleSet DecodeRules(JsonElement result) => Copy(
        result.GetProperty("rules").Deserialize<FilmRuleSet>(Json) ??
            throw new InvalidOperationException("Film rule snapshot is missing."));

    private static JsonElement Encode(FilmRuleSet rules) => JsonSerializer.SerializeToElement(rules, Json);

    private static FilmRuleSet Copy(FilmRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new FilmRuleSet
        {
            EpisodeCleanRegexRules = Array.AsReadOnly(rules.EpisodeCleanRegexRules.ToArray()),
            EpisodeRules = Array.AsReadOnly(rules.EpisodeRules.ToArray()),
            MovieRules = Array.AsReadOnly(rules.MovieRules.ToArray()),
            AggregateRegexRules = Array.AsReadOnly(rules.AggregateRegexRules.ToArray())
        };
    }

    private static string SectionKey(FilmRuleSection section) => section switch
    {
        FilmRuleSection.Episode => "episode",
        FilmRuleSection.Movie => "movie",
        FilmRuleSection.Search => "search",
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed != 0, this);
            if (preparation is not null) throw new InvalidOperationException("SHUTTING_DOWN");
            cancellationToken.ThrowIfCancellationRequested();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pending.Token);
            // Register before waiting for serial so preparation also owns callers
            // that have been admitted but have not entered the semaphore yet.
            active++;
        }
        return ExecuteAsync();

        async Task<T> ExecuteAsync()
        {
            bool entered = false;
            try
            {
                await serial.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                linked.Token.ThrowIfCancellationRequested();
                return await operation(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                if (entered) serial.Release();
                linked.Dispose();
                lock (lifecycle)
                {
                    if (--active == 0) drained?.TrySetResult();
                }
            }
        }
    }

    /// <summary>
    /// Pauses admission and cancels/drains admitted work, including serial waiters
    /// and accepted storage writes. Cancellation stops only this caller's wait.
    /// </summary>
    public Task PrepareShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycle)
        {
            var prepare = PrepareLocked();
            return cancellationToken.CanBeCanceled ? prepare.WaitAsync(cancellationToken) : prepare;
        }
    }

    private Task PrepareLocked()
    {
        if (preparation is not null) return preparation;
        drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (active == 0) drained.TrySetResult();
        // CancelAsync marks the token immediately and runs callbacks outside this
        // lock. The shared preparation also waits for those callbacks to finish.
        return preparation = DrainAsync(pending.CancelAsync(), drained.Task);
    }

    private static async Task DrainAsync(Task cancellation, Task work)
    {
        try { await cancellation.ConfigureAwait(false); }
        catch (AggregateException) { /* Callback errors must not skip physical drain. */ }
        await work.ConfigureAwait(false);
    }

    /// <summary>Resumes the same engine and saved snapshot only after preparation has drained.</summary>
    public void Resume()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(closed != 0, this);
            if (preparation is null) return;
            if (!preparation.IsCompletedSuccessfully) throw new InvalidOperationException("SHUTDOWN_NOT_DRAINED");
            pending.Dispose();
            pending = new();
            drained = null;
            preparation = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is not null) return new(disposal);
            Volatile.Write(ref closed, 1);
            return new(disposal = CloseAsync(PrepareLocked()));
        }
    }

    private async Task CloseAsync(Task prepare)
    {
        await Task.Yield();
        await prepare.ConfigureAwait(false);
        try { await engine.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            serial.Dispose();
            pending.Dispose();
        }
    }
}
