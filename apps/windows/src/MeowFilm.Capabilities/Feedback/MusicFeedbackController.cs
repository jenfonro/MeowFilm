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
    private double position, duration, rate = 1;
    private string mode = "circulation";
    private long generation;
    private bool started, running, preparing, closed, loading, releaseFaulted;
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
        internal Task Begin = Task.CompletedTask;
        internal Task Cancel = Task.CompletedTask;
        internal Task? Release;
        internal bool Eligible, Running, Finishing, Cancelled;
        internal double Listened, MediaListened, Position, Duration, Rate = 1;
        internal long At;
    }

    public MusicFeedbackController(MusicFeedbackSettingsOwner settings, IMusicFeedbackProtocol protocol,
        IMusicPlatformSessionSource accounts, TimeProvider? clock = null)
    {
        this.settings = settings; this.protocol = protocol; this.accounts = accounts; this.clock = clock ?? TimeProvider.System;
        settings.Changed += SettingsChanged;
    }
    public string? LastError { get { lock (sync) return lastError; } }
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
        }
    }

    public void UpdateSelection(MusicFeedbackSelection value)
    {
        lock (sync)
        {
            if (selection?.EntryId != value.EntryId || closed || preparing) return;
            selection = value with { Song = value.Song.Clone(), Context = value.Context?.Clone() };
            foreach (var target in active.Values.ToArray())
                if (MusicFeedbackIdentity.Resolve(selection.Song, target.Platform, target.Recording) != target.Recording)
                { active.Remove(target.Platform); CancelLocked(target); }
        }
    }

    public void Started(string entryId, double position, double duration, double rate = 1, int mode = 1)
    {
        lock (sync)
        {
            if (closed || preparing || selection?.EntryId != entryId) return;
            ObserveLocked(position, duration, true, rate, mode);
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
        if (!ValidSample(nextPosition, nextDuration, nextRate))
        {
            running = false;
            // Unknown duration, invalid samples and discontinuities cannot bridge
            // two otherwise valid intervals into credited listening time.
            foreach (var target in active.Values) target.Running = false;
            return;
        }
        long at = clock.GetTimestamp();
        foreach (var target in active.Values.ToArray())
        {
            if (!Authorized(target))
            { active.Remove(target.Platform); CancelLocked(target); continue; }
            double wall = Math.Max(0, clock.GetElapsedTime(target.At, at).TotalSeconds);
            double delta = nextPosition - target.Position;
            if (target.Eligible && target.Running && target.Rate > 0 &&
                (target.Platform != "kg" || target.Rate >= .25) && delta >= 0 && delta <= wall * target.Rate + 1.5)
            {
                target.Listened += Math.Min(wall, delta / target.Rate);
                target.MediaListened += Math.Min(wall * target.Rate, delta);
            }
            target.At = at; target.Position = nextPosition; target.Duration = nextDuration;
            target.Rate = nextRate; target.Running = isPlaying;
        }
        position = nextPosition; duration = nextDuration; rate = nextRate; running = isPlaying;
        mode = nextMode is >= 0 and <= 3 ? new[] { "order", "circulation", "random", "single" }[nextMode] : "";
    }

    private bool Authorized(Target target) => !target.Cancelled && settings.IsEnabled(target.Platform) &&
        (target.Account is null || accounts.IsCurrent(target.Account));

    private static bool ValidSample(double position, double duration, double rate) =>
        double.IsFinite(position) && double.IsFinite(duration) && double.IsFinite(rate) &&
        duration is > 0 and <= 86400 && position >= 0 && position <= duration && rate is > 0 and <= 16;

    private void BeginEligibleLocked()
    {
        if (!started || !running || closed || preparing || selection is null || !settings.IsConfirmed ||
            !ValidSample(position, duration, rate)) return;
        foreach (string platform in Platforms)
        {
            if (!settings.IsEnabled(platform) || attempted.Contains(platform) || active.ContainsKey(platform) ||
                platform == "kg" && rate < .25) continue;
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
                target.Account = account; target.Position = position; target.Duration = duration;
                target.Rate = rate; target.At = clock.GetTimestamp(); target.Running = running;
                request = MusicFeedbackIdentity.Start(target.Instance, target.Selection, target.Recording, duration, position, rate, mode);
            }
            // Start's token is also the accepted session's revocation lifetime.
            // Its HTTP requests already have bounded deadlines in the transport.
            var session = await protocol.StartAsync(account, request, target.Cancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                target.Session = session;
                if (Authorized(target) && !closed && !target.Finishing && target.Generation == generation)
                {
                    target.Eligible = true;
                    target.Position = position; target.Duration = duration; target.Rate = rate;
                    target.At = clock.GetTimestamp(); target.Running = running;
                }
            }
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
        foreach (var target in active.Values.ToArray())
        {
            target.Finishing = true;
            var end = new MusicFeedbackEnd(target.Listened, target.MediaListened, target.Duration,
                target.Position, target.Rate, reason, MusicFeedbackIdentity.PlaybackMode(target.Selection, mode));
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

    public void AccountChanged(string platform)
    {
        lock (sync)
        {
            foreach (var target in targets.Where(t => t.Platform == platform).ToArray()) CancelLocked(target);
            active.Remove(platform); attempted.Remove(platform);
        }
    }
    private void SettingsChanged()
    {
        lock (sync)
        {
            foreach (var target in targets.Where(t => !settings.IsEnabled(t.Platform)).ToArray())
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
        lock (sync) { preparing = true; FinishLocked("interrupt"); }
        await DrainAsync(token).ConfigureAwait(false);
        // Unknown local preference save is not a successful shutdown preparation.
        if (settings.RequiresReconciliation) throw new MusicFeedbackException("COMMIT_UNKNOWN");
        lock (sync) if (releaseFaulted) throw new MusicFeedbackException("FEEDBACK_RELEASE_FAILED");
    }
    public void CancelShutdownPreparation() { lock (sync) if (!closed) preparing = false; }
    public async Task DrainAsync(CancellationToken token = default)
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
