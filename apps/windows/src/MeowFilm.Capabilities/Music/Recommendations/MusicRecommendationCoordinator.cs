using System.Text.Json;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using static MeowFilm.Capabilities.Recommendations.RecommendationJson;
using static MeowFilm.Capabilities.Recommendations.MusicRecommendationQueuePort;

namespace MeowFilm.Capabilities.Recommendations;

public sealed record MusicRecommendationStartRequest(
    string Kind,
    string Platform,
    string? Mode = null,
    string? Submode = null,
    string? RuleId = null,
    string? TabTagId = null,
    string[]? SourceIds = null,
    string? SourceType = null,
    string? Entry = null,
    string? SongId = null,
    string? PlaylistId = null,
    string? SeedEntryId = null,
    bool Local = true,
    bool Restore = false,
    bool Continue = true,
    bool Play = true);

/// <summary>Event-driven, native recommendation sessions over the existing catalog and sole library writer.</summary>
public sealed partial class MusicRecommendationCoordinator : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly MusicRecommendationQueuePort queue;
    private readonly Func<MusicRecommendationRequest, CancellationToken, Task<JsonElement>> readAsync;
    private readonly Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync;
    private static TimeProvider clock => TimeProvider.System;
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
        internal Session? PreviousSession;
        internal readonly Dictionary<string, MusicRecommendationFeedback> Feedback = [];
        internal readonly HashSet<string> Attempted = [];
        internal bool Active = true, Loading, Continue = config.Continue,
            Play = config.Play, InitialPending = true, CommittingInitial, Returning, Exhausted, RecommendationStarted;
        internal string Status = "idle", Error = "", Checkpoint = "", Anchor = "", FixedSeed = "";
        internal int Offset;
        internal int Batch, Generation, Navigation, MediaIntent, Failures, ModeIntent;
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
    private sealed record Song(MusicSongRecord Record, string NativeId, string Algorithm, string FeedbackSource,
        JsonElement? Context, JsonElement? Metadata);
    private sealed record HeartbeatContext(string PlaylistId, string StartMusicId, string SongId, bool FromHome, long Count, string AccountId);
    private sealed record Candidate(string Id, Song Song);
    private sealed record CandidateBatch(long Epoch, MusicRecommendationRequest Request,
        Candidate[] Items, bool HasMore, int Offset, int ModeIntent);
    private sealed record PendingOperation(string Nonce, string Action, Song[] Songs,
        MusicRecommendationRequest? Request, string Seed, bool Select, bool Automatic, long QueueEpoch,
        int? SelectIndex = null, bool HasMore = true, HeartbeatContext? Heartbeat = null, int Offset = 0, int? Navigation = null,
        int? ModeIntent = null);

    public MusicRecommendationCoordinator(MusicPlatformCatalog catalog, MusicRecommendationQueuePort queue,
        Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync,
        Func<MusicRecommendationRequest, CancellationToken, Task<JsonElement>>? readAsync = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.selectAsync = selectAsync ?? throw new ArgumentNullException(nameof(selectAsync));
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
    public event Action<MusicRecommendationSnapshot>? Changed;

    public Task<MusicRecommendationSnapshot> StartAsync(MusicRecommendationStartRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StartCoreAsync(Parse(request), cancellationToken);
    }

    public Task<MusicRecommendationSnapshot> GetAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (id is not null)
            {
                var found = session?.Id == id ? session : unresolved.FirstOrDefault(s => s.Id == id);
                Require(found is not null, "SESSION_NOT_FOUND");
                return Task.FromResult(SnapshotLocked(found));
            }
            return Task.FromResult(SnapshotLocked(session));
        }
    }

    public async Task<MusicRecommendationSnapshot> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session target; Task pending;
        lock (gate)
        {
            target = RequireSessionLocked(id, allowUnresolved: false);
            RetireLocked(target, "cancelled");
            pending = target.Cleanup;
        }
        await pending.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(target);
    }

    public async Task<MusicRecommendationSnapshot> ReconcileAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session target; Task pending;
        lock (gate)
        {
            target = RequireSessionLocked(id, allowUnresolved: true);
            Require(!closed, "CLOSED");
            pending = LaunchLocked(() => ReconcileOperationAsync(target, cancellationToken));
        }
        await pending.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(session?.Id == target.Id ? session : target);
    }

    public async Task<MusicRecommendationSnapshot> ReturnAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session target; Task pending;
        lock (gate)
        {
            target = RequireSessionLocked(id, allowUnresolved: false);
            Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
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
        }
        await pending.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(target);
    }

    public async Task<MusicRecommendationSnapshot> SetContinueAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session target; Task? pending = null;
        lock (gate)
        {
            target = RequireSessionLocked(id, allowUnresolved: false);
            Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
            if (enabled)
            {
                int intent = ++target.ModeIntent;
                pending = LaunchLocked(() => EnableContinuationAsync(target, intent, cancellationToken));
            }
            else DisableContinuationLocked(target);
        }
        if (pending is not null) await pending.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(target);
    }

    public async Task<MusicRecommendationSnapshot> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session target; Task pending;
        lock (gate)
        {
            target = RequireSessionLocked(id, allowUnresolved: false);
            Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
            Require(target.Pending is null, "COMMIT_UNKNOWN");
            pending = BeginBatchLocked(target, target.InitialPending, target.RecommendationStarted, cancellationToken);
        }
        await pending.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(target);
    }

    public async Task<MusicRecommendationSnapshot> ChooseAsync(string id, string candidateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session target; Task pending;
        lock (gate)
        {
            target = RequireSessionLocked(id, allowUnresolved: false);
            Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            GuardLocked(); Require(CurrentLocked(target) && !target.Returning, "STALE_SESSION");
            Require(target.Config.Kind == "queue", "INVALID_REQUEST");
            Require(target.Pending is null, "COMMIT_UNKNOWN");
            Require(!target.Loading, "BUSY");
            pending = BeginChooseLocked(target, candidateId, cancellationToken);
        }
        await pending.ConfigureAwait(false);
        lock (gate) return SnapshotLocked(target);
    }

    private Session RequireSessionLocked(string id, bool allowUnresolved)
    {
        var unknown = allowUnresolved ? unresolved.FirstOrDefault(s => s.Id == id) : null;
        var found = unknown ?? (session?.Id == id ? session : null);
        Require(found is not null, "SESSION_NOT_FOUND");
        return found!;
    }

    private static Configuration Parse(MusicRecommendationStartRequest p)
    {
        string kind = p.Kind ?? "", platform = p.Platform ?? "";
        Require(kind is "fm" or "similar" or "heartbeat" or "queue");
        Require(platform is "wy" or "tx" or "kw" or "kg" or "mg");
        Require(platform == "wy", "UNSUPPORTED");
        Require(p.Local, "REMOTE_NOT_SUPPORTED");
        MusicFmRequest? fm = null; MusicHeartbeatRequest? heartbeat = null; string[] ids = [];
        if (kind == "fm")
        {
            string mode = p.Mode ?? "default";
            Require(mode is "default" or "familiar" or "explore" or "puzzle" or "scene");
            var value = mode switch
            {
                "familiar" => MusicFmMode.Familiar,
                "explore" => MusicFmMode.Explore,
                "puzzle" => MusicFmMode.Puzzle,
                "scene" => MusicFmMode.Scene,
                _ => MusicFmMode.Default
            };
            fm = new(platform, value, p.Submode ?? "", false, p.RuleId, p.TabTagId);
        }
        else if (kind == "similar")
        {
            Require(p.SourceType is "song" or "artist");
            Require(p.SourceIds is { Length: > 0 } && p.SourceIds.All(id => !string.IsNullOrEmpty(id)));
            ids = p.SourceIds!.ToArray();
        }
        else if (kind == "heartbeat")
        {
            string entry = p.Entry ?? "", song = p.SongId ?? "";
            Require(entry is "favorites" or "current");
            Require(entry == "favorites" || song.Length > 0);
            heartbeat = new(platform, entry == "favorites" ? MusicHeartbeatEntry.Favorites : MusicHeartbeatEntry.CurrentSong,
                song.Length > 0 ? song : null, p.PlaylistId);
        }
        else Require(!string.IsNullOrEmpty(p.SeedEntryId));
        return new(kind, platform, fm, ids, p.SourceType ?? "song", heartbeat, p.SeedEntryId ?? "",
            kind == "heartbeat" || kind == "queue" && p.Restore,
            (kind != "queue" || p.Restore) && p.Continue, p.Play);
    }

    private static bool SameTargetConfiguration(Configuration left, Configuration right) =>
        left.Kind == right.Kind &&
        left.Platform == right.Platform &&
        left.Fm == right.Fm &&
        left.SourceIds.SequenceEqual(right.SourceIds, StringComparer.Ordinal) &&
        left.SourceType == right.SourceType && left.Restore == right.Restore &&
        left.Heartbeat == right.Heartbeat &&
        left.Seed == right.Seed &&
        left.Continue == right.Continue;

    internal Task<MusicRecommendationSnapshot>? TryReuseFmStart(MusicRecommendationStartRequest input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = Parse(input);
        lock (gate)
        {
            GuardLocked(); Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            if (session is not { Active: true, Returning: false } current || current.Config.Kind != "fm") return null;
            if (input.Mode is null)
            {
                // An implicit homepage entry means the last committed choice,
                // not a channel switch that has not yet succeeded.
                if (current.InitialPending) return null;
                config = config with { Fm = current.Config.Fm };
            }
            return SameTargetConfiguration(current.Config, config) ? StartCoreAsync(config, token) : null;
        }
    }

    private async Task<MusicRecommendationSnapshot> StartCoreAsync(Configuration config, CancellationToken token)
    {
        Session next; Task flight;
        MusicRecommendationSelection? resumeSelection = null;
        lock (gate)
        {
            GuardLocked(); Require(unresolved.Count == 0, "COMMIT_UNKNOWN");
            var old = session;
            if (old is { Active: true, Returning: false } &&
                SameTargetConfiguration(old.Config, config) &&
                (old.Config.Kind == "fm" || old.Config.Play == config.Play))
            {
                next = old;
                int intent = old.ModeIntent;
                if (config.Continue && !old.Continue && !old.InitialPending) intent = ++old.ModeIntent;
                flight = config.Continue && !old.Continue && !old.InitialPending
                    ? LaunchLocked(() => EnableContinuationAsync(old, intent, token)) : old.Flight;
                if (config.Play && !old.Play && !old.InitialPending && !old.Loading && old.Queue is { CurrentId.Length: > 0 } q)
                {
                    old.Play = true; old.MediaIntent++;
                    resumeSelection = SelectionLocked(old, q.CurrentId, play: true, automatic: false, restoreOnly: false);
                    PublishLocked();
                }
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
                next = new(config) { QueueEpoch = queue.Epoch, Predecessor = predecessor };
                if (old is { Active: true })
                {
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
        if (resumeSelection is not null) await SelectAsync(next, resumeSelection, token).ConfigureAwait(false);
        lock (gate) return SnapshotLocked(!next.Active && !ReferenceEquals(session, next) ? session : next);
    }
    private bool CurrentLocked(Session s) => !closed && !stopping && ReferenceEquals(session, s) && s.Active;
    private void GuardLocked() => Require(!closed && !stopping, "CLOSED");
    private static string ErrorCode(Exception error) => error switch
    {
        RecommendationException e => e.Code,
        MusicPlatformException e when SafeCode(e.Code) => e.Code,
        Library.MusicLibraryException e when SafeCode(e.Code) => e.Code,
        OperationCanceledException => "CANCELLED",
        _ => "INVALID_RESPONSE"
    };
    private static bool SafeCode(string code) => code.Length is > 0 and <= 64 &&
        code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    private MusicRecommendationSnapshot SnapshotLocked(Session? s)
    {
        // A pending request owns loading/error/cancellation, but cannot become the
        // playback source until a usable result has been admitted by the queue owner.
        var source = s is { InitialPending: false } ? s : s?.PreviousSession;
        var fm = CommittedFm(s);
        return new MusicRecommendationSnapshot(
            Id: s?.Id ?? "",
            Revision: revision,
            Kind: source?.Config.Kind ?? "",
            Platform: source?.Config.Platform ?? "",
            Active: s is not null && CurrentLocked(s) && source is { Active: true },
            Status: s?.Status ?? "idle",
            Loading: s?.Loading ?? false,
            Continue: source?.Continue ?? false,
            Play: s?.Play ?? false,
            Error: s?.Error ?? "",
            Batch: source?.Batch ?? 0,
            CanRetry: s is not null && CurrentLocked(s) && !s.Loading && !s.Returning && s.Pending is null && unresolved.Count == 0,
            CanReturn: s is not null && CurrentLocked(s) && !s.Loading && s.Checkpoint.Length > 0 && s.Config.Restore && s.Pending is null && unresolved.Count == 0,
            RequiresReconcile: s is not null && (s.Pending is not null || unresolved.Any(x => x.Id == s.Id)),
            ReconcileIds: unresolved.Select(x => x.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            QueueVersion: (source ?? s)?.Queue?.Version ?? 0,
            CurrentId: (source ?? s)?.Queue?.CurrentId ?? "",
            AnchorId: source?.Anchor ?? "",
            Count: (source ?? s)?.Queue?.Entries.Length ?? 0,
            Failures: s?.Failures ?? 0,
            Candidates: s?.Candidates?.Items.Select(c => new MusicRecommendationCandidateSnapshot(c.Id, c.Song.Record)).ToArray() ?? [],
            Exhausted: s?.Exhausted ?? false,
            SourceType: source?.Config.SourceType ?? "",
            SeedSongId: s?.FixedSeed ?? "",
            Started: s?.RecommendationStarted ?? false,
            Offset: s?.Offset ?? 0,
            HeartbeatPlaylistId: source?.Heartbeat?.PlaylistId ?? "",
            Mode: fm?.Mode.ToString().ToLowerInvariant() ?? "",
            Submode: fm?.Submode ?? "",
            RuleId: fm?.RuleId ?? "",
            TabTagId: fm?.TabTagId ?? "");
    }
    private static MusicFmRequest? CommittedFm(Session? s) =>
        s is { InitialPending: true } ? s.PreviousSession?.Config.Fm : s?.Config.Fm;
    internal bool IsFmSelectionCurrent(MusicRecommendationSnapshot snapshot)
    {
        lock (gate)
        {
            var fm = CommittedFm(session);
            return session is { } s && CurrentLocked(s) && s.Id == snapshot.Id && fm is not null &&
                fm.Mode.ToString().ToLowerInvariant() == snapshot.Mode && fm.Submode == snapshot.Submode &&
                (fm.RuleId ?? "") == snapshot.RuleId && (fm.TabTagId ?? "") == snapshot.TabTagId;
        }
    }
}
