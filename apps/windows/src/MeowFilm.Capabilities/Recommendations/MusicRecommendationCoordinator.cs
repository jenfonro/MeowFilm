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
    private readonly Func<bool> fetchOnRandomPlayback;
    private readonly SemaphoreSlim physical = new(1, 1);
    private readonly HashSet<Task> work = [];
    private readonly HashSet<Session> unresolved = [];
    private Session? session;
    private long revision;
    private bool stopping, closed;
    private Task? preparation, disposal;
    private sealed record Configuration(string Kind, string Platform, MusicFmRequest? Fm,
        string[] SourceIds, string SourceType, MusicHeartbeatRequest? Heartbeat, string Seed,
        bool Restore, bool Continue, bool Play);
    private sealed class Session(Configuration config, string? id = null)
    {
        internal readonly string Id = id ?? Guid.NewGuid().ToString("D");
        internal Configuration Config = config;
        internal Configuration? PreviousConfiguration;
        internal Session? PreviousSession;
        internal readonly Dictionary<string, MusicRecommendationFeedback> Feedback = [];
        internal readonly HashSet<string> Attempted = [];
        internal bool Active = true, Loading, Continue = config.Kind != "heartbeat" && config.Continue,
            Play = config.Play, InitialAppend, InitialPending = true, Returning, Exhausted, RecommendationStarted;
        internal string Status = "idle", Error = "", Checkpoint = "", Anchor = "", FixedSeed = "", LastPlayback = "";
        internal bool RandomFetch;
        internal int Offset;
        internal int Batch, Generation, Navigation, MediaIntent, Failures;
        internal long QueueEpoch;
        internal QueueSnapshot? Queue;
        internal MusicRecommendationPlaybackState? Playback;
        internal CancellationTokenSource? Cancellation;
        internal Task Flight = Task.CompletedTask, Cleanup = Task.CompletedTask;
        internal Task Predecessor = Task.CompletedTask;
        internal PendingOperation? Pending;
        internal CandidateBatch? Candidates;
        internal HeartbeatContext? Heartbeat;
    }
    private sealed record Song(JsonElement Value, string NativeId, string Algorithm, string FeedbackSource,
        JsonElement? Context, JsonElement Metadata);
    private sealed record HeartbeatContext(string PlaylistId, string StartMusicId, string SongId, bool FromHome, long Count);
    private sealed record Candidate(string Id, Song Song);
    private sealed record CandidateBatch(long Epoch, MusicRecommendationRequest Request,
        Candidate[] Items, bool HasMore, int Offset);
    private sealed record PendingOperation(string Nonce, string Action, Song[] Songs,
        MusicRecommendationRequest? Request, string Seed, bool Select, bool Automatic, long QueueEpoch,
        int? SelectIndex = null, bool HasMore = true, HeartbeatContext? Heartbeat = null, int Offset = 0, int? Navigation = null);

    public MusicRecommendationCoordinator(IMusicPlatformCatalog catalog, MusicRecommendationQueuePort queue,
        Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync,
        TimeProvider? timeProvider = null,
        Func<MusicRecommendationRequest, CancellationToken, Task<JsonElement>>? readAsync = null,
        Func<bool>? fetchOnRandomPlayback = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.selectAsync = selectAsync ?? throw new ArgumentNullException(nameof(selectAsync));
        clock = timeProvider ?? TimeProvider.System;
        this.fetchOnRandomPlayback = fetchOnRandomPlayback ?? (() => Random.Shared.Next(2) == 0);
        this.readAsync = readAsync ?? ((request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return request.ReadAsync(catalog, ct);
        });
        queue.Changing += QueueChanging;
        queue.Changed += QueueChanged;
        queue.Selected += QueueSelected;
        queue.SelectionSource = QueueSelectionSource;
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
            string fields = method switch
            {
                "music.recommendations.continue" => "id enabled",
                "music.recommendations.choose" => "id candidateId",
                _ => "id"
            };
            Fields(p, fields, fields);
            Session target; Task? pending = null;
            lock (gate)
            {
                var unknown = method == "music.recommendations.reconcile" ? unresolved.FirstOrDefault(s => s.Id == Text(p, "id")) : null;
                var found = unknown ?? (session?.Id == Text(p, "id") ? session : null);
                Require(found is not null, "SESSION_NOT_FOUND");
                target = found!;
                Require(unresolved.Count == 0 || method is "music.recommendations.cancel" or "music.recommendations.reconcile",
                    "COMMIT_UNKNOWN");
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
                        GuardLocked(); Require(CurrentLocked(target), "STALE_SESSION");
                        Require(target.Checkpoint.Length > 0 && target.Config.Restore, "CHECKPOINT_NOT_FOUND");
                        Require(target.Pending is null, "COMMIT_UNKNOWN");
                        if (!target.Returning)
                        {
                            target.Returning = true; target.Navigation++; target.Generation++;
                            CancelLocked(target.Cancellation);
                            target.Status = "returning"; PublishLocked();
                            var previous = target.Flight;
                            pending = target.Flight = LaunchLocked(() => ReturnAsync(target, previous, cancellationToken));
                        }
                        else pending = target.Flight;
                        break;
                    case "music.recommendations.continue":
                        GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
                        Require(target.Config.Kind != "heartbeat", "UNSUPPORTED");
                        bool enabled = Bool(p, "enabled");
                        if (target.Continue != enabled)
                        {
                            target.Continue = enabled;
                            PublishLocked();
                        }
                        pending = enabled ? MaybeBeginLocked(target) : null;
                        break;
                    case "music.recommendations.retry":
                        GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
                        Require(target.Pending is null, "COMMIT_UNKNOWN");
                        Require(target.Config.Kind != "heartbeat" || target.InitialPending, "UNSUPPORTED");
                        pending = BeginBatchLocked(target, target.InitialPending,
                            target.RecommendationStarted, cancellationToken);
                        break;
                    case "music.recommendations.choose":
                        GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
                        Require(target.Config.Kind == "queue", "INVALID_REQUEST");
                        Require(target.Pending is null, "COMMIT_UNKNOWN");
                        Require(!target.Loading, "BUSY");
                        pending = BeginChooseLocked(target, Text(p, "candidateId", "", 80), cancellationToken);
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
        Fields(p, "kind platform mode submode ruleId tabTagId sourceIds sourceType entry songId playlistId seedEntryId restore continue play local", "kind platform");
        string kind = Text(p, "kind"), platform = Text(p, "platform");
        Require(kind is "fm" or "similar" or "heartbeat" or "queue");
        Require(platform is "wy" or "tx" or "kw" or "kg" or "mg");
        Require(platform == "wy", "UNSUPPORTED");
        Require(Bool(p, "local", true), "REMOTE_NOT_SUPPORTED");
        MusicFmRequest? fm = null; MusicHeartbeatRequest? heartbeat = null; string[] ids = [];
        if (kind == "fm")
        {
            Fields(p, "kind platform mode submode ruleId tabTagId continue play local", "kind platform");
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
            Fields(p, "kind platform sourceIds sourceType continue play local", "kind platform sourceIds sourceType");
            Require(Text(p, "sourceType") is "song" or "artist");
            Require(p.TryGetProperty("sourceIds", out var source) && source.ValueKind == JsonValueKind.Array &&
                source.GetArrayLength() is > 0 and <= 20);
            ids = source.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : "").ToArray();
            Require(ids.All(ValidId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length);
        }
        else if (kind == "heartbeat")
        {
            Fields(p, "kind platform entry songId playlistId seedEntryId play local", "kind platform entry");
            string entry = Text(p, "entry"), song = Text(p, "songId");
            Require(entry is "favorites" or "current");
            Require(entry == "favorites" && song.Length == 0 || ValidId(song));
            Require(!p.TryGetProperty("playlistId", out _) || ValidId(Text(p, "playlistId")));
            heartbeat = new(platform, entry == "favorites" ? MusicHeartbeatEntry.Favorites : MusicHeartbeatEntry.CurrentSong,
                song.Length > 0 ? song : null, p.TryGetProperty("playlistId", out _) ? Text(p, "playlistId") : null);
        }
        else Fields(p, "kind platform seedEntryId restore continue play local", "kind platform seedEntryId");
        return new(kind, platform, fm, ids, Text(p, "sourceType", "song"), heartbeat, Text(p, "seedEntryId", "", 80),
            kind == "heartbeat" || kind == "queue" && Bool(p, "restore"),
            kind != "heartbeat" && Bool(p, "continue", true), Bool(p, "play", true));
    }
    private static bool SameConfiguration(Configuration left, Configuration right) =>
        left.Kind == right.Kind &&
        left.Platform == right.Platform &&
        left.Fm == right.Fm &&
        left.SourceIds.SequenceEqual(right.SourceIds, StringComparer.Ordinal) &&
        left.SourceType == right.SourceType && left.Restore == right.Restore &&
        left.Heartbeat == right.Heartbeat &&
        left.Seed == right.Seed &&
        left.Continue == right.Continue &&
        left.Play == right.Play;

    internal Task<JsonElement>? TryReuseFmStart(JsonElement input, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Require(System.Text.Encoding.UTF8.GetByteCount(input.GetRawText()) <= 8192, "LIMIT");
            var config = Parse(input);
            lock (gate)
            {
                GuardLocked(); Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
                if (session is not { Active: true, Returning: false } current || current.Config.Kind != "fm") return null;
                if (!input.TryGetProperty("mode", out _))
                {
                    // An implicit homepage entry means the last committed choice,
                    // not a channel switch that has not yet succeeded.
                    if (current.InitialPending) return null;
                    config = config with { Fm = current.Config.Fm };
                }
                return SameConfiguration(current.Config, config) ? StartAsync(config, token) : null;
            }
        }
        catch (Exception error) { return Task.FromResult(Json(new { ok = false, code = ErrorCode(error) })); }
    }

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
                Task predecessor = old?.Flight ?? Task.CompletedTask;
                if (old?.PreviousSession is { } committed)
                {
                    // Supersede only the pending intent, not the still-committed source.
                    old.PreviousSession = null;
                    if (old.Checkpoint == committed.Checkpoint) old.Checkpoint = "";
                    RetireLocked(old, "replaced");
                    old = committed;
                }
                if (old is { InitialPending: true })
                {
                    RetireLocked(old, "replaced");
                    old = null;
                }
                bool switchFm = old is { Active: true, InitialPending: false } && old.Config.Kind == "fm" && config.Kind == "fm";
                next = new(config, switchFm ? old!.Id : null) { QueueEpoch = queue.Epoch, Predecessor = predecessor };
                if (old is { Active: true })
                {
                    next.InitialAppend = switchFm;
                    if (switchFm)
                    {
                        next.Batch = old.Batch; next.Queue = old.Queue; next.Continue = old.Continue;
                        next.PreviousConfiguration = old.Config;
                        foreach (var entry in old.Feedback) next.Feedback.Add(entry.Key, entry.Value);
                    }
                    if (config.Restore && old.Checkpoint.Length > 0) next.Checkpoint = old.Checkpoint;
                    next.PreviousSession = old;
                    old.Generation++; old.Navigation++; CancelLocked(old.Cancellation);
                    next.Predecessor = Task.WhenAll(predecessor, old.Flight);
                }
                session = next;
                flight = BeginBatchLocked(next, true, false, token);
            }
        }
        await flight.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(!next.Active && !ReferenceEquals(session, next) ? session : next);
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
    private JsonElement SnapshotLocked(Session? s)
    {
        // A pending request owns loading/error/cancellation, but cannot become the
        // playback source until a usable result has been admitted by the queue owner.
        var source = s is { InitialPending: false } ? s : s?.PreviousSession;
        return Json(new
        {
            ok = true, id = s?.Id ?? "", revision, kind = source?.Config.Kind ?? "", platform = source?.Config.Platform ?? "",
            active = s is not null && CurrentLocked(s) && source is { Active: true }, status = s?.Status ?? "idle",
            loading = s?.Loading ?? false, @continue = source?.Continue ?? false,
            play = s?.Play ?? false, error = s?.Error ?? "", batch = source?.Batch ?? 0,
            canRetry = s is not null && CurrentLocked(s) && !s.Loading && !s.Returning && s.Pending is null && unresolved.Count == 0 &&
                (s.Config.Kind != "heartbeat" || s.InitialPending),
            canReturn = s is not null && CurrentLocked(s) && !s.Loading && s.Checkpoint.Length > 0 && s.Config.Restore && s.Pending is null && unresolved.Count == 0,
            requiresReconcile = s is not null && (s.Pending is not null || unresolved.Any(x => x.Id == s.Id)),
            reconcileIds = unresolved.Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            queueVersion = (source ?? s)?.Queue?.Version ?? 0, currentId = (source ?? s)?.Queue?.CurrentId ?? "", anchorId = source?.Anchor ?? "",
            count = (source ?? s)?.Queue?.Entries.Length ?? 0, failures = s?.Failures ?? 0,
            candidates = s?.Candidates?.Items.Select(c => new { id = c.Id, song = c.Song.Value }).ToArray() ?? [],
            exhausted = s?.Exhausted ?? false,
            sourceType = source?.Config.SourceType ?? "", seedSongId = s?.FixedSeed ?? "",
            started = s?.RecommendationStarted ?? false, offset = s?.Offset ?? 0,
            heartbeatPlaylistId = source?.Heartbeat?.PlaylistId ?? "",
            mode = CommittedFm(s)?.Mode.ToString().ToLowerInvariant() ?? "",
            submode = CommittedFm(s)?.Submode ?? "",
            ruleId = CommittedFm(s)?.RuleId ?? "", tabTagId = CommittedFm(s)?.TabTagId ?? ""
        });
    }
    private static MusicFmRequest? CommittedFm(Session? s) =>
        s is { InitialPending: true } ? s.PreviousSession?.Config.Fm : s?.Config.Fm;
    internal bool IsFmSelectionCurrent(JsonElement snapshot)
    {
        lock (gate)
        {
            var fm = CommittedFm(session);
            return session is { } s && CurrentLocked(s) && s.Id == Text(snapshot, "id") && fm is not null &&
                fm.Mode.ToString().ToLowerInvariant() == Text(snapshot, "mode") && fm.Submode == Text(snapshot, "submode") &&
                (fm.RuleId ?? "") == Text(snapshot, "ruleId") && (fm.TabTagId ?? "") == Text(snapshot, "tabTagId");
        }
    }
}
