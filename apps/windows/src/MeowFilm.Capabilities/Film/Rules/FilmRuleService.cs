using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Film.Rules;

/// <summary>Native rule configuration and durable drafts. Only execution and previews belong to Node.</summary>
public sealed class FilmRuleService : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = CreateJson();
    private readonly FilmPlaybackClient client;
    private readonly IStorageDatabase database;
    private StorageRevision storageRevision = StorageRevision.Empty;
    private readonly IRuntimeSettingsNotifications? notifications;
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

    public FilmRuleService(IRuntimeClient runtime, IStorageDatabase database)
    {
        client = new(runtime ?? throw new ArgumentNullException(nameof(runtime)));
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        notifications = runtime as IRuntimeSettingsNotifications;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async token =>
        {
            var (rules, revision) = await FilmRulesStore.ReadAsync(database, token).ConfigureAwait(false);
            storageRevision = revision;
            AcceptLocal(rules ?? Defaults("initial"));
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
        return RunAsync(token => PersistAsync(owned, token), cancellationToken);
    }

    /// <summary>Restores the selected section, then saves the entire supplied panel draft.</summary>
    public Task<FilmRuleSet> RestoreDefaultsAsync(FilmRuleSection section, FilmRuleSet draft,
        CancellationToken cancellationToken = default)
    {
        _ = SectionKey(section);
        var owned = Copy(draft);
        return RunAsync(token =>
        {
            var defaults = Defaults("restore");
            var next = section switch
            {
                FilmRuleSection.Episode => owned with { EpisodeCleanRegexRules = defaults.EpisodeCleanRegexRules, EpisodeRules = defaults.EpisodeRules },
                FilmRuleSection.Movie => owned with { MovieRules = defaults.MovieRules },
                FilmRuleSection.Season => owned with { SeasonRules = defaults.SeasonRules },
                _ => owned with { AggregateRegexRules = defaults.AggregateRegexRules }
            };
            return PersistAsync(next, token);
        }, cancellationToken);
    }

    private async Task<FilmRuleSet> PersistAsync(FilmRuleSet candidate, CancellationToken token)
    {
        Validate(candidate);
        var normalized = NormalizeForSave(candidate);
        if (saved is { } current && SameRules(current, normalized))
            return Copy(current);
        storageRevision = await FilmRulesStore.SaveAsync(database, normalized, storageRevision, token).ConfigureAwait(false);
        var committed = AcceptLocal(normalized);
        notifications?.SettingsChanged("film.rules");
        return committed;
    }

    public Task<FilmRuleResult> ProcessAsync(FilmRuleRequest request, FilmRuleSet rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = SectionKey(request.Section);
        var owned = Copy(rules);
        return RunAsync(async token =>
        {
            var previewRequest = new Dictionary<string, object?>
            {
                ["section"] = key, ["text"] = request.Text
            };
            if (request.Query is not null) previewRequest["query"] = request.Query;
            var input = new Dictionary<string, object?>
            {
                ["request"] = previewRequest,
                ["rules"] = owned
            };
            var result = await CallAsync("preview", input, token).ConfigureAwait(false);
            return result.Deserialize<FilmRuleResult>(Json) ??
                throw new InvalidOperationException("Film rule result is missing.");
        }, cancellationToken);
    }

    public FilmMatchRule? NormalizeNewMatchRule(string pattern, string replace = "")
    {
        var parsed = ParseInput(pattern);
        return parsed is null ? null : new FilmMatchRule(parsed.Value.Pattern, TrimInput(replace), parsed.Value.Flags);
    }

    public string NormalizeNewCleanRule(string pattern)
    {
        var parsed = ParseInput(pattern);
        return parsed is null ? "" : parsed.Value.Flags.Length == 0 ? parsed.Value.Pattern :
            "/" + parsed.Value.Pattern + "/" + parsed.Value.Flags;
    }

    private FilmRuleSet AcceptLocal(FilmRuleSet rules)
    {
        var next = Copy(rules);
        Revision = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(next, Json)));
        Volatile.Write(ref saved, next);
        return Copy(next);
    }

    private static (string Pattern, string Flags)? ParseInput(string value)
    {
        string pattern = TrimInput(value), flags = "";
        if (pattern.StartsWith('/') && pattern.LastIndexOf('/') is > 0 and var end)
        {
            flags = TrimInput(pattern[(end + 1)..]);
            pattern = TrimInput(pattern[1..end]);
        }
        pattern = NormalizeRegexText(pattern);
        return pattern.Length == 0 ? null : (pattern, flags);
    }

    private static string TrimInput(string value) => value.Trim(FilmJson.InputWhitespace);

    private static string NormalizeRegexText(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            result.Append(value[i]);
            if (value[i] == '\\' && i + 2 < value.Length && value[i + 1] == '\\' &&
                "dDsSwWbB.()[]{}+*?^$|\\-_/".Contains(value[i + 2])) i++;
        }
        return result.ToString();
    }

    // Settings encoding only; compiling and executing rules stays in Node.
    private static FilmRuleSet NormalizeForSave(FilmRuleSet rules)
    {
        static string[] Clean(IEnumerable<string> rows) =>
            rows.Select(TrimInput).Where(value => value.Length > 0).ToArray();
        static FilmMatchRule[] Matches(IEnumerable<FilmMatchRule> rows) =>
            rows.Select(row => row with
            {
                Pattern = NormalizeRegexText(TrimInput(row.Pattern)),
                Flags = TrimInput(row.Flags)
            }).Where(row => row.Pattern.Length > 0).ToArray();
        return new()
        {
            EpisodeCleanRegexRules = Clean(rules.EpisodeCleanRegexRules),
            EpisodeRules = Matches(rules.EpisodeRules),
            MovieRules = Matches(rules.MovieRules),
            AggregateRegexRules = Clean(rules.AggregateRegexRules),
            SeasonRules = Matches(rules.SeasonRules)
        };
    }

    private static bool SameRules(FilmRuleSet left, FilmRuleSet right) =>
        left.EpisodeCleanRegexRules.SequenceEqual(right.EpisodeCleanRegexRules, StringComparer.Ordinal) &&
        left.EpisodeRules.SequenceEqual(right.EpisodeRules) &&
        left.MovieRules.SequenceEqual(right.MovieRules) &&
        left.AggregateRegexRules.SequenceEqual(right.AggregateRegexRules, StringComparer.Ordinal) &&
        left.SeasonRules.SequenceEqual(right.SeasonRules);

    private static FilmRuleSet Defaults(string name)
    {
        using var stream = typeof(FilmRuleService).Assembly.GetManifestResourceStream("MeowFilm.FilmRuleDefaults.json")!;
        using var json = JsonDocument.Parse(stream);
        return Copy(json.RootElement.GetProperty(name).Deserialize<FilmRuleSet>(Json)!);
    }

    private static void Validate(FilmRuleSet rules)
    {
        static bool Valid(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                { if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false; }
                else if (char.IsLowSurrogate(value[i])) return false;
            }
            return true;
        }
        if (rules.EpisodeCleanRegexRules.Concat(rules.AggregateRegexRules).Any(value => !Valid(value)) ||
            rules.EpisodeRules.Concat(rules.MovieRules).Concat(rules.SeasonRules).Any(row => !Valid(row.Pattern) || !Valid(row.Replace) || !Valid(row.Flags)))
            throw new FilmPlaybackException("INVALID_RULES");
    }

    private Task<JsonElement> CallAsync(string operation, object input, CancellationToken token) =>
        client.CallAsync("rules." + operation, JsonSerializer.SerializeToElement(input, Json), token);

    private static FilmRuleSet Copy(FilmRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new FilmRuleSet
        {
            EpisodeCleanRegexRules = Array.AsReadOnly(rules.EpisodeCleanRegexRules.ToArray()),
            EpisodeRules = Array.AsReadOnly(rules.EpisodeRules.ToArray()),
            MovieRules = Array.AsReadOnly(rules.MovieRules.ToArray()),
            AggregateRegexRules = Array.AsReadOnly(rules.AggregateRegexRules.ToArray()),
            SeasonRules = Array.AsReadOnly(rules.SeasonRules.ToArray())
        };
    }

    private static string SectionKey(FilmRuleSection section) => section switch
    {
        FilmRuleSection.Episode => "episode",
        FilmRuleSection.Movie => "movie",
        FilmRuleSection.Search => "search",
        FilmRuleSection.Season => "season",
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
            var units = new List<char>();
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
