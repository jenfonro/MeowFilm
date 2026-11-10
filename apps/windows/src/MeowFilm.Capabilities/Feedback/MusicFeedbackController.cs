using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>
/// Native event consumer, never a reporting timer. URL resolution, queue reads and
/// restored positions cannot create listens. Network failures never block playback.
/// </summary>
public sealed class MusicFeedbackController : IMusicPlaybackFeedback, IAsyncDisposable
{
    private readonly MusicFeedbackSettingsOwner settings;
    private readonly IMusicFeedbackProtocol protocol;
    private readonly IMusicPlatformSessionSource accounts;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly Dictionary<string, Target> active = new(StringComparer.Ordinal);
    private readonly HashSet<Target> targets = [];
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private readonly HashSet<Task> work = [];
    private MusicFeedbackSelection? selection;
    private double position, duration, rate = 1, listened, mediaListened;
    private string mode = "circulation";
    private long generation, observedAt;
    private Guid playbackId;
    private DateTimeOffset startedAt;
    private bool started, running, validSample, preparing, closed, loading, releaseFaulted;
    private Task? disposal;
    private string? lastError;
    private static readonly string[] Platforms = ["wy", "tx", "kg"];

    private sealed class Target(string platform, MusicFeedbackSelection selection, MusicFeedbackRecording recording, long generation)
    {
        internal readonly string Platform = platform;
        internal readonly MusicFeedbackSelection Selection = selection;
        internal readonly MusicFeedbackRecording Recording = recording;
        internal readonly long Generation = generation;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly MusicFeedbackAttempt Instance = new();
        internal MusicPlatformSession? Account;
        internal IMusicFeedbackSession? Session;
        internal MusicFeedbackStart? Start;
        internal Task Begin = Task.CompletedTask;
        internal Task Cancel = Task.CompletedTask;
        internal Task? Release;
        internal bool Eligible, AdmissionPending, Finishing, Cancelled;
        internal double BeforeAdmission, MediaBeforeAdmission;
        internal long AdmittedTimestamp;
    }

    public MusicFeedbackController(MusicFeedbackSettingsOwner settings, IMusicFeedbackProtocol protocol,
        IMusicPlatformSessionSource accounts, TimeProvider? clock = null)
    {
        this.settings = settings; this.protocol = protocol; this.accounts = accounts; this.clock = clock ?? TimeProvider.System;
        settings.Changed += SettingsChanged;
    }
    public string? LastError { get { lock (sync) return lastError ?? (protocol as IMusicFeedbackPendingProtocol)?.LastError; } }
    internal async Task InitializeAsync(CancellationToken token = default)
    {
        if (protocol is not IMusicFeedbackPendingProtocol pending) return;
        try { await pending.InitializeAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { SetError("FEEDBACK_STORAGE_FAILED"); } // Pending feedback cannot block startup/audio.
    }
    public Task<MusicFeedbackSettings> ReadSettingsAsync(bool refresh = false, CancellationToken token = default) =>
        settings.ReadAsync(refresh, token);
    public Task<MusicFeedbackSettings> SaveSettingsAsync(long revision, IReadOnlyList<string> enabled, CancellationToken token = default) =>
        settings.SaveAsync(revision, enabled, token);

    public void Select(MusicFeedbackSelection value)
    {
        if (string.IsNullOrEmpty(value.EntryId) || value.EntryId.Length > 200) return;
        lock (sync)
        {
            if (closed || preparing) return;
            FinishLocked("ui");
            selection = value with { Song = value.Song.Clone(), Context = value.Context?.Clone() };
            generation++; attempted.Clear(); started = false; running = false;
            validSample = false; listened = mediaListened = 0; playbackId = Guid.NewGuid();
        }
    }

    public void UpdateSelection(MusicFeedbackSelection value)
    {
        lock (sync)
        {
            if (selection is not { } current || current.EntryId != value.EntryId || closed || preparing) return;
            // Mapping enrichment and handoffs cannot replace this occurrence's origin.
            selection = current with { Song = value.Song.Clone() };
            foreach (var target in active.Values.ToArray())
                if (MusicFeedbackIdentity.Resolve(selection.Song, target.Platform, target.Recording) != target.Recording)
                { active.Remove(target.Platform); CancelLocked(target); }
            BeginEligibleLocked();
        }
    }

    public void Started(string entryId, double position, double duration, double rate = 1, int mode = 1)
    {
        lock (sync)
        {
            if (closed || preparing || selection?.EntryId != entryId) return;
            ObserveLocked(position, duration, true, rate, mode);
            if (!started) startedAt = clock.GetUtcNow();
            started = true; BeginEligibleLocked();
            if (!settings.IsConfirmed && !loading)
            {
                loading = true;
                QueueLocked(async () =>
                {
                    try { await settings.ReadAsync().ConfigureAwait(false); }
                    catch { SetError("FEEDBACK_SETTINGS_UNAVAILABLE"); }
                    finally { lock (sync) { loading = false; BeginEligibleLocked(); } }
                });
            }
        }
    }

    public void Observe(string entryId, double position, double duration, bool isPlaying, double rate = 1, int mode = 1)
    {
        lock (sync)
        {
            if (closed || preparing || selection?.EntryId != entryId || !started) return;
            ObserveLocked(position, duration, isPlaying, rate, mode); BeginEligibleLocked();
        }
    }

    private void ObserveLocked(double nextPosition, double nextDuration, bool isPlaying, double nextRate, int nextMode)
    {
        long at = clock.GetTimestamp();
        foreach (var target in active.Values.ToArray())
            if (!Authorized(target))
            { active.Remove(target.Platform); CancelLocked(target); }
        if (!ValidSample(nextPosition, nextDuration, nextRate))
        {
            running = false; validSample = false; observedAt = at;
            foreach (var target in active.Values) target.AdmissionPending = false;
            return;
        }
        double wall = validSample ? Math.Max(0, clock.GetElapsedTime(observedAt, at).TotalSeconds) : 0;
        double delta = nextPosition - position;
        double wallCredit = 0, mediaCredit = 0;
        if (validSample && running && delta >= 0 && delta <= wall * rate + 1.5)
        {
            wallCredit = Math.Min(wall, delta / rate);
            mediaCredit = Math.Min(wall * rate, delta);
        }
        // Measure one interval once. A platform's admission only excludes the
        // part before its account/consent became eligible; ACK is not a clock.
        foreach (var target in active.Values.Where(t => t.Eligible && t.AdmissionPending))
        {
            double before = Math.Clamp(clock.GetElapsedTime(observedAt, target.AdmittedTimestamp).TotalSeconds, 0, wall);
            target.BeforeAdmission += Math.Min(wallCredit, before);
            target.MediaBeforeAdmission += Math.Min(mediaCredit, before * rate);
            target.AdmissionPending = false;
        }
        listened += wallCredit; mediaListened += mediaCredit;
        position = nextPosition; duration = nextDuration; rate = nextRate; running = isPlaying;
        observedAt = at; validSample = true;
        mode = nextMode switch { 1 => "circulation", 2 => "random", 3 => "single", _ => "" };
    }

    private bool Authorized(Target target) => !target.Cancelled &&
        (settings.IsEnabled(target.Platform) || target.Platform == "wy" && settings.PreservesWyConsent) &&
        (target.Account is null || accounts.IsCurrent(target.Account));

    private static bool ValidSample(double position, double duration, double rate) =>
        double.IsFinite(position) && double.IsFinite(duration) && double.IsFinite(rate) &&
        duration >= 0 && position >= 0 && (duration == 0 || position <= duration) && rate > 0;

    private void BeginEligibleLocked()
    {
        if (!started || !running || closed || preparing || selection is null || !settings.IsConfirmed ||
            !ValidSample(position, duration, rate)) return;
        foreach (string platform in Platforms)
        {
            if (!settings.IsEnabled(platform) || attempted.Contains(platform) || active.ContainsKey(platform) ||
                protocol.RequiresDuration(platform) && duration == 0 || platform == "kg" && rate < .25) continue;
            var recording = MusicFeedbackIdentity.Resolve(selection.Song, platform);
            if (recording is null) continue;
            attempted.Add(platform);
            var target = new Target(platform, selection, recording, generation);
            active.Add(platform, target); targets.Add(target);
            target.Begin = QueueLocked(() => BeginTarget(target));
        }
    }

    private async Task BeginTarget(Target target)
    {
        try
        {
            MusicPlatformSession? account;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(target.Cancellation.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(25));
                account = await accounts.AcquireAsync(target.Platform, deadline.Token).ConfigureAwait(false);
                if (account is not null && protocol is IMusicFeedbackPendingProtocol pending)
                    await pending.QualifyAccountAsync(account, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            MusicFeedbackStart request;
            lock (sync)
            {
                if (account is null || !accounts.IsCurrent(account) || !Authorized(target) || closed ||
                    target.Finishing || target.Generation != generation) return;
                if (!running || !ValidSample(position, duration, rate))
                {
                    // No protocol attempt was made. A later real sample may admit it.
                    attempted.Remove(target.Platform);
                    return;
                }
                target.Account = account; target.Eligible = true;
                target.BeforeAdmission = listened; target.MediaBeforeAdmission = mediaListened;
                target.AdmittedTimestamp = clock.GetTimestamp(); target.AdmissionPending = true;
                request = target.Start = MusicFeedbackIdentity.Start(target.Instance, target.Selection, target.Recording,
                    duration, position, rate, mode) with
                {
                    PlaybackId = playbackId, EntryId = target.Selection.EntryId,
                    StartedAt = startedAt, AdmittedAt = clock.GetUtcNow()
                };
            }
            // Start's token is also the accepted session's revocation lifetime.
            // Its HTTP requests already have bounded deadlines in the transport.
            var session = await protocol.StartAsync(account, request, target.Cancellation.Token).ConfigureAwait(false);
            lock (sync) target.Session = session;
        }
        catch (OperationCanceledException) { }
        catch { SetError("FEEDBACK_FAILED"); }
        finally
        {
            bool release;
            lock (sync) release = target.Cancelled || target.Session is null && !target.Finishing;
            if (release) await ReleaseTarget(target).ConfigureAwait(false);
        }
    }

    public void Finish(string reason = "ui")
    { lock (sync) FinishLocked(reason); }
    private void FinishLocked(string reason)
    {
        reason = reason switch
        {
            "end" or "playend" => "playend",
            "error" or "exception" => "exception",
            "ui" => "ui",
            _ => "interrupt"
        };
        var endedAt = clock.GetUtcNow();
        foreach (var target in active.Values.ToArray())
        {
            target.Finishing = true;
            var end = new MusicFeedbackEnd(target.Eligible ? Math.Max(0, listened - target.BeforeAdmission) : 0,
                target.Eligible ? Math.Max(0, mediaListened - target.MediaBeforeAdmission) : 0, duration,
                position, rate, reason, target.Start is { Recommendation.Kind: "fm" or "heartbeat" } start ? start.Mode : mode)
            {
                PlaybackId = playbackId, EndedAt = endedAt,
                PlaybackListened = listened, PlaybackMediaListened = mediaListened
            };
            QueueLocked(async () =>
            {
                try
                {
                    await target.Begin.ConfigureAwait(false);
                    bool allowed;
                    lock (sync) allowed = Authorized(target) && target.Session is not null;
                    if (allowed)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(target.Cancellation.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(25));
                        await target.Session!.FinishAsync(end, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch { SetError("FEEDBACK_FAILED"); }
                finally { await ReleaseTarget(target).ConfigureAwait(false); }
            });
        }
        active.Clear(); selection = null; started = false; running = false; generation++; attempted.Clear();
    }

    public void Invalidate()
    {
        lock (sync)
        {
            foreach (var target in targets.Where(t => t.Generation == generation).ToArray()) CancelLocked(target);
            active.Clear(); attempted.Clear(); generation++;
            started = running = validSample = false; listened = mediaListened = 0; playbackId = Guid.NewGuid();
        }
    }

    public void AccountChanged(string platform)
    {
        if (platform == "wy" && protocol is IMusicFeedbackPendingProtocol pending)
            pending.AuthorizationChanged(accountChanged: true);
        lock (sync)
        {
            foreach (var target in targets.Where(t => t.Platform == platform).ToArray()) CancelLocked(target);
            active.Remove(platform); attempted.Remove(platform);
        }
    }
    private void SettingsChanged()
    {
        if (protocol is IMusicFeedbackPendingProtocol pending) pending.AuthorizationChanged(accountChanged: false);
        lock (sync)
        {
            foreach (var target in targets.Where(t => !settings.IsEnabled(t.Platform) &&
                !(t.Platform == "wy" && settings.PreservesWyConsent)).ToArray())
            { active.Remove(target.Platform); CancelLocked(target); }
            foreach (string platform in Platforms.Where(settings.IsEnabled))
                if (!active.ContainsKey(platform)) attempted.Remove(platform);
            BeginEligibleLocked();
        }
    }

    private void CancelLocked(Target target)
    {
        if (target.Cancelled) return;
        target.Cancelled = true; target.Cancel = target.Cancellation.CancelAsync();
        if (target.Begin.IsCompleted && !target.Finishing) QueueLocked(() => ReleaseTarget(target));
    }
    private Task ReleaseTarget(Target target)
    {
        lock (sync)
        {
            if (target.Release is not null) return target.Release;
            if (active.GetValueOrDefault(target.Platform) == target) active.Remove(target.Platform);
            return target.Release = ReleaseTargetCore(target);
        }
    }
    private async Task ReleaseTargetCore(Target target)
    {
        await Task.Yield();
        try
        {
            await target.Cancel.ConfigureAwait(false);
            if (target.Session is { } session) await session.DisposeAsync().ConfigureAwait(false);
        }
        catch { lock (sync) { lastError = "FEEDBACK_RELEASE_FAILED"; releaseFaulted = true; } }
        finally
        {
            target.Cancellation.Dispose();
            lock (sync) { target.Session = null; targets.Remove(target); }
        }
    }
    private Task QueueLocked(Func<Task> action)
    {
        var task = Task.Run(action); work.Add(task);
        _ = task.ContinueWith(done =>
        {
            _ = done.Exception;
            lock (sync) work.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    private void SetError(string code) { lock (sync) lastError = code; }

    public async Task PrepareShutdownAsync(CancellationToken token = default)
    {
        lock (sync)
        {
            preparing = true;
            foreach (var target in targets.Where(t => !t.Eligible).ToArray()) CancelLocked(target);
            FinishLocked("interrupt");
        }
        // Freeze local terminal work first. Suspend/cancel network separately
        // from revocation, so safe pending events survive a normal exit.
        var pending = protocol as IMusicFeedbackPendingProtocol;
        Task suspend = pending?.PrepareShutdownAsync() ?? Task.CompletedTask;
        await DrainLocalAsync(token).ConfigureAwait(false);
        await suspend.WaitAsync(token).ConfigureAwait(false);
        if (pending is not null) await pending.FlushLocalAsync().WaitAsync(token).ConfigureAwait(false);
        if (pending?.LastError == "FEEDBACK_STORAGE_FAILED")
            throw new MusicFeedbackException("FEEDBACK_STORAGE_FAILED");
        // Unknown local preference save is not a successful shutdown preparation.
        if (settings.RequiresReconciliation) throw new MusicFeedbackException("COMMIT_UNKNOWN");
        lock (sync) if (releaseFaulted) throw new MusicFeedbackException("FEEDBACK_RELEASE_FAILED");
    }
    public void CancelShutdownPreparation()
    {
        lock (sync) if (!closed) preparing = false;
        (protocol as IMusicFeedbackPendingProtocol)?.Resume();
    }
    public async Task DrainAsync(CancellationToken token = default)
    {
        await DrainLocalAsync(token).ConfigureAwait(false);
        if (protocol is IMusicFeedbackPendingProtocol pending) await pending.DrainAsync(token).ConfigureAwait(false);
        await DrainLocalAsync(token).ConfigureAwait(false);
    }
    private async Task DrainLocalAsync(CancellationToken token)
    {
        for (;;)
        {
            Task[] tasks; lock (sync) tasks = work.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            closed = true; settings.Changed -= SettingsChanged;
            foreach (var target in targets.ToArray()) CancelLocked(target);
            active.Clear(); selection = null;
            return new(disposal = DisposeCore());
        }
    }
    private async Task DisposeCore()
    {
        await Task.Yield();
        await DrainAsync().ConfigureAwait(false);
        Exception? failure = null;
        try { await protocol.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { await settings.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        lock (sync) if (releaseFaulted) failure ??= new MusicFeedbackException("FEEDBACK_RELEASE_FAILED");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
