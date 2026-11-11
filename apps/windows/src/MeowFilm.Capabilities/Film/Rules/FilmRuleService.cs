using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities.Film.Rules;

/// <summary>Settings drafts and acknowledged display snapshots. Node owns rules and their execution.</summary>
public sealed class FilmRuleService : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private readonly FilmPlaybackClient client;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object lifecycle = new();
    private CancellationTokenSource pending = new();
    private FilmRuleSet? saved;
    public string Revision { get; private set; } = "";
    private TaskCompletionSource? drained;
    private Task? preparation;
    private Task? disposal;
    private int active;
    private int closed;

    public FilmRuleService(IRuntimeClient runtime)
    {
        client = new(runtime ?? throw new ArgumentNullException(nameof(runtime)));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            Accept(await CallAsync("get", new { }, token).ConfigureAwait(false));
            return true;
        }, cancellationToken);

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
            var result = await CallAsync("set", new { rules = owned }, token).ConfigureAwait(false);
            return Accept(result);
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
            var input = new System.Collections.Generic.Dictionary<string, object?> { ["section"] = key };
            if (draft is not null) input["rules"] = draft;
            return Accept(await CallAsync("restore", input, token).ConfigureAwait(false));
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
            var previewRequest = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["section"] = key, ["text"] = request.Text
            };
            if (request.Query is not null) previewRequest["query"] = request.Query;
            var input = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["request"] = previewRequest
            };
            if (rules is not null) input["rules"] = rules;
            var result = await CallAsync("preview", input, token).ConfigureAwait(false);
            return result.Deserialize<FilmRuleResult>(Json) ??
                throw new InvalidOperationException("Film rule result is missing.");
        }, cancellationToken);
    }

    public Task<FilmMatchRule?> NormalizeNewMatchRuleAsync(string pattern, string replace = "",
        CancellationToken cancellationToken = default) => RunAsync(async token =>
        {
            var result = await CallAsync("normalize", new { kind = "match", pattern, replace }, token).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Null ? null : result.Deserialize<FilmMatchRule>(Json);
        }, cancellationToken);

    public Task<string> NormalizeNewCleanRuleAsync(string pattern, CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var result = await CallAsync("normalize", new { kind = "clean", pattern }, token).ConfigureAwait(false);
            return result.Deserialize<string>(Json) ?? "";
        }, cancellationToken);

    public Task<FilmRuleSet> ClearAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token => Accept(await CallAsync("clear", new { }, token).ConfigureAwait(false)), cancellationToken);

    private Task<JsonElement> CallAsync(string operation, object input, CancellationToken token) =>
        client.CallAsync("rules." + operation, JsonSerializer.SerializeToElement(input, Json), token);

    private FilmRuleSet Accept(JsonElement result)
    {
        var next = DecodeRules(result);
        Revision = FilmJson.RequiredText(result, "revision");
        Volatile.Write(ref saved, next);
        return Copy(next);
    }

    private static FilmRuleSet DecodeRules(JsonElement result) => Copy(
        result.GetProperty("rules").Deserialize<FilmRuleSet>(Json) ??
            throw new InvalidOperationException("Film rule snapshot is missing."));

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
    /// and accepted RPCs. Cancellation stops only this caller's wait.
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

    /// <summary>Resumes admission only after preparation has drained.</summary>
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
        serial.Dispose();
        pending.Dispose();
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new RuleTextConverter());
        return options;
    }

    // FilmRuleText's wire representation: valid Unicode remains text; isolated
    // UTF16 code units use UInt16 arrays, including in nested errors and previews.
    private sealed class RuleTextConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Invalid rule text.");
            var units = new System.Collections.Generic.List<char>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) units.Add((char)reader.GetUInt16());
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Invalid rule text.");
            return new string(units.ToArray());
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            bool isolated = false;
            for (int index = 0; index < value.Length; index++)
            {
                char unit = value[index];
                if (!char.IsSurrogate(unit)) continue;
                if (char.IsHighSurrogate(unit) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
                else { isolated = true; break; }
            }
            if (!isolated) { writer.WriteStringValue(value); return; }
            writer.WriteStartArray();
            foreach (char unit in value) writer.WriteNumberValue(unit);
            writer.WriteEndArray();
        }
    }
}
