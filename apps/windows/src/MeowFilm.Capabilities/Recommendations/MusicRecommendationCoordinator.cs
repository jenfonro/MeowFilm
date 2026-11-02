using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;
using static MeowFilm.Capabilities.Recommendations.MusicRecommendationQueuePort;

namespace MeowFilm.Capabilities.Recommendations;

/// <summary>Event-driven, native recommendation sessions over the existing catalog and sole library writer.</summary>
public sealed partial class MusicRecommendationCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly MusicRecommendationQueuePort queue;
    private readonly Func<MusicRecommendationRequest, CancellationToken, Task<JsonElement>> readAsync;
    private readonly Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim physical = new(1, 1);
    private readonly HashSet<Task> work = [];
    private readonly HashSet<Session> unresolved = [];
    private Session? session;
    private long revision;
    private bool stopping, closed;
    private Task? preparation, disposal;
    private sealed record Configuration(string Kind, string Platform, MusicFmRequest? Fm,
        string[] SourceIds, MusicHeartbeatRequest? Heartbeat, string Seed, bool Continue, bool Play);
    private sealed class Session(Configuration config, string? id = null)
    {
        internal readonly string Id = id ?? Guid.NewGuid().ToString("D");
        internal Configuration Config = config;
        internal Configuration? PreviousConfiguration;
        internal readonly Dictionary<string, MusicRecommendationFeedback> Feedback = [];
        internal readonly HashSet<string> Attempted = [];
        internal bool Active = true, Loading, Waiting, Continue = config.Kind == "fm" || config.Continue,
            Play = config.Play, InitialAppend, InitialPending = true, Returning;
        internal string Status = "idle", Error = "", Checkpoint = "", Anchor = "";
        internal int Batch, Generation, Navigation, MediaIntent, Failures;
        internal long QueueEpoch;
        internal DateTimeOffset RetryAt = DateTimeOffset.UnixEpoch;
        internal QueueSnapshot? Queue;
        internal MusicRecommendationPlaybackState? Playback;
        internal CancellationTokenSource? Cancellation;
        internal Task Flight = Task.CompletedTask, Cleanup = Task.CompletedTask, Advance = Task.CompletedTask;
        internal Task Predecessor = Task.CompletedTask;
        internal Session? TransferTo;
        internal PendingOperation? Pending;
    }
    private sealed record Song(JsonElement Value, string NativeId, string Algorithm);
    private sealed record PendingOperation(string Nonce, string Action, Song[] Songs,
        MusicRecommendationRequest? Request, string Seed, bool Select, bool Automatic, long QueueEpoch);

    public MusicRecommendationCoordinator(IMusicPlatformCatalog catalog, MusicRecommendationQueuePort queue,
        Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync,
        TimeProvider? timeProvider = null,
        Func<MusicRecommendationRequest, CancellationToken, Task<JsonElement>>? readAsync = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.selectAsync = selectAsync ?? throw new ArgumentNullException(nameof(selectAsync));
        clock = timeProvider ?? TimeProvider.System;
        this.readAsync = readAsync ?? ((request, ct) => request.ReadAsync(catalog, ct));
        queue.Changing += QueueChanging;
        queue.Changed += QueueChanged;
    }
    public event Action<JsonElement>? Changed;

    public async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = Json(parameters ?? new { });
            Require(System.Text.Encoding.UTF8.GetByteCount(p.GetRawText()) <= 8192, "LIMIT");
            if (method == "music.recommendations.start")
                return await StartAsync(Parse(p), cancellationToken).ConfigureAwait(false);
            if (method == "music.recommendations.get")
            {
                Fields(p, "id");
                lock (gate)
                {
                    if (p.TryGetProperty("id", out _))
                    {
                        var found = session?.Id == Text(p, "id") ? session : unresolved.FirstOrDefault(s => s.Id == Text(p, "id"));
                        Require(found is not null, "SESSION_NOT_FOUND");
                        return SnapshotLocked(found);
                    }
                    return SnapshotLocked(session);
                }
            }
            Fields(p, method == "music.recommendations.continue" ? "id enabled" : "id",
                method == "music.recommendations.continue" ? "id enabled" : "id");
            Session target; Task? pending = null;
            lock (gate)
            {
                var unknown = method == "music.recommendations.reconcile" ? unresolved.FirstOrDefault(s => s.Id == Text(p, "id")) : null;
                var found = unknown ?? (session?.Id == Text(p, "id") ? session : null);
                Require(found is not null, "SESSION_NOT_FOUND");
                target = found!;
                switch (method)
                {
                    case "music.recommendations.cancel":
                        RetireLocked(target, "cancelled");
                        pending = target.Cleanup;
                        break;
                    case "music.recommendations.reconcile":
                        Require(!closed, "CLOSED");
                        pending = LaunchLocked(() => ReconcileOperationAsync(target, cancellationToken));
                        break;
                    case "music.recommendations.return":
                        GuardLocked(); Require(target.Checkpoint.Length > 0 && target.Config.Kind != "fm", "CHECKPOINT_NOT_FOUND");
                        Require(target.Pending is null, "COMMIT_UNKNOWN");
                        if (!target.Returning)
                        {
                            target.Returning = true; target.Navigation++; target.Generation++;
                            CancelLocked(target.Cancellation);
                            target.Waiting = false; target.Status = "returning"; PublishLocked();
                            var previous = target.Flight;
                            pending = target.Flight = LaunchLocked(() => ReturnAsync(target, previous, cancellationToken));
                        }
                        else pending = target.Flight;
                        break;
                    case "music.recommendations.continue":
                        GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
                        Require(target.Config.Kind != "fm", "INVALID_REQUEST");
                        bool enabled = Bool(p, "enabled");
                        if (target.Continue != enabled)
                        {
                            target.Continue = enabled;
                            if (!enabled)
                            {
                                target.Generation++; target.Navigation++; target.Waiting = false;
                                CancelLocked(target.Cancellation);
                            }
                            PublishLocked();
                        }
                        pending = enabled ? MaybeBeginLocked(target) : target.Flight;
                        break;
                    case "music.recommendations.retry":
                        GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
                        Require(target.Pending is null, "COMMIT_UNKNOWN");
                        Require(target.Batch == 0 || target.Config.Kind == "fm" || target.Continue, "CONTINUATION_DISABLED");
                        pending = BeginBatchLocked(target, target.InitialPending, false, cancellationToken);
                        break;
                    default: throw new RecommendationException("METHOD_NOT_FOUND");
                }
            }
            if (pending is not null) await pending.ConfigureAwait(false);
            lock (gate) return SnapshotLocked(method == "music.recommendations.reconcile" &&
                session?.Id == target.Id ? session : target);
        }
        catch (Exception error) { return Json(new { ok = false, code = ErrorCode(error) }); }
    }

    private static Configuration Parse(JsonElement p)
    {
        Fields(p, "kind platform mode submode ruleId tabTagId sourceIds playlistId startMusicId songId fromHome seedEntryId continue play local", "kind platform");
        string kind = Text(p, "kind"), platform = Text(p, "platform");
        Require(kind is "fm" or "similar" or "heartbeat");
        Require(platform is "wy" or "tx" or "kw" or "kg" or "mg");
        Require(platform == "wy", "UNSUPPORTED");
        Require(Bool(p, "local", true), "REMOTE_NOT_SUPPORTED");
        MusicFmRequest? fm = null; MusicHeartbeatRequest? heartbeat = null; string[] ids = [];
        if (kind == "fm")
        {
            Fields(p, "kind platform mode submode ruleId tabTagId play local", "kind platform");
            string mode = Text(p, "mode", "default");
            Require(mode is "default" or "familiar" or "explore" or "puzzle" or "scene");
            var value = mode switch { "familiar" => MusicFmMode.Familiar, "explore" => MusicFmMode.Explore,
                "puzzle" => MusicFmMode.Puzzle, "scene" => MusicFmMode.Scene, _ => MusicFmMode.Default };
            fm = new(platform, value, Text(p, "submode", "", 64), false,
                p.TryGetProperty("ruleId", out _) ? Text(p, "ruleId", "", 64) : null,
                p.TryGetProperty("tabTagId", out _) ? Text(p, "tabTagId", "", 64) : null);
        }
        else if (kind == "similar")
        {
            Fields(p, "kind platform sourceIds seedEntryId continue play local", "kind platform sourceIds");
            Require(p.TryGetProperty("sourceIds", out var source) && source.ValueKind == JsonValueKind.Array &&
                source.GetArrayLength() is > 0 and <= 20);
            ids = source.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : "").ToArray();
            Require(ids.All(ValidId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length);
        }
        else
        {
            Fields(p, "kind platform playlistId startMusicId songId fromHome seedEntryId continue play local",
                "kind platform playlistId startMusicId songId");
            string playlist = Text(p, "playlistId"), start = Text(p, "startMusicId"), song = Text(p, "songId");
            Require(ValidId(playlist) && ValidId(start) && ValidId(song));
            heartbeat = new(platform, playlist, start, song, Bool(p, "fromHome"));
        }
        return new(kind, platform, fm, ids, heartbeat, Text(p, "seedEntryId", "", 80),
            Bool(p, "continue"), Bool(p, "play", true));
    }
    private static bool SameConfiguration(Configuration left, Configuration right) =>
        left.Kind == right.Kind &&
        left.Platform == right.Platform &&
        left.Fm == right.Fm &&
        left.SourceIds.SequenceEqual(right.SourceIds, StringComparer.Ordinal) &&
        left.Heartbeat == right.Heartbeat &&
        left.Seed == right.Seed &&
        left.Continue == right.Continue &&
        left.Play == right.Play;

    private async Task<JsonElement> StartAsync(Configuration config, CancellationToken token)
    {
        Session next; Task flight;
        lock (gate)
        {
            GuardLocked(); Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            var old = session;
            if (old is { Active: true, Returning: false } && SameConfiguration(old.Config, config))
            {
                next = old;
                flight = old.Flight;
            }
            else
            {
                bool switchFm = old is { Active: true, Batch: > 0 } && old.Config.Kind == "fm" && config.Kind == "fm";
                next = new(config, switchFm ? old!.Id : null) { QueueEpoch = queue.Epoch };
                if (old is { Active: true })
                {
                    next.InitialAppend = switchFm;
                    if (switchFm)
                    {
                        next.Batch = old.Batch; next.Queue = old.Queue;
                        next.PreviousConfiguration = old.PreviousConfiguration ?? old.Config;
                        foreach (var entry in old.Feedback) next.Feedback.Add(entry.Key, entry.Value);
                    }
                    if (config.Kind != "fm" && old.Config.Kind != "fm" && old.Checkpoint.Length > 0)
                    { next.Checkpoint = old.Checkpoint; old.Checkpoint = ""; }
                    if (config.Kind != "fm" && old.Config.Kind != "fm") old.TransferTo = next;
                    RetireLocked(old, "replaced");
                    next.Predecessor = old.Cleanup;
                }
                session = next;
                flight = BeginBatchLocked(next, true, false, token);
            }
        }
        await flight.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(next);
    }
    private bool CurrentLocked(Session s) => !closed && !stopping && ReferenceEquals(session, s) && s.Active;
    private void GuardLocked() => Require(!closed && !stopping, "CLOSED");
    private static string ErrorCode(Exception error) => error switch
    {
        RecommendationException e => e.Code,
        Platforms.MusicPlatformException e when SafeCode(e.Code) => e.Code,
        Library.MusicLibraryException e when SafeCode(e.Code) => e.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };
    private static bool SafeCode(string code) => code.Length is > 0 and <= 64 &&
        code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    private JsonElement SnapshotLocked(Session? s) => Json(new
    {
        ok = true, id = s?.Id ?? "", revision, kind = s?.Config.Kind ?? "", platform = s?.Config.Platform ?? "",
        active = s is not null && CurrentLocked(s), status = s?.Status ?? "idle",
        loading = s?.Loading ?? false, waiting = s?.Waiting ?? false, @continue = s?.Continue ?? false,
        play = s?.Play ?? false, error = s?.Error ?? "", batch = s?.Batch ?? 0,
        canRetry = s is not null && CurrentLocked(s) && !s.Loading && !s.Returning && s.Pending is null && unresolved.Count == 0 &&
            (s.Batch == 0 || s.Config.Kind == "fm" || s.Continue),
        canReturn = s is not null && s.Checkpoint.Length > 0 && s.Config.Kind != "fm" && s.Pending is null && unresolved.Count == 0,
        requiresReconcile = s is not null && (s.Pending is not null || unresolved.Any(x => x.Id == s.Id)),
        retryAt = s?.RetryAt.ToUnixTimeMilliseconds() ?? 0,
        reconcileIds = unresolved.Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        queueVersion = s?.Queue?.Version ?? 0, currentId = s?.Queue?.CurrentId ?? "", anchorId = s?.Anchor ?? "",
        count = s?.Queue?.Entries.Length ?? 0, failures = s?.Failures ?? 0
    });
}
