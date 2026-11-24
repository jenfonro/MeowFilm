using System.Globalization;
using System.Text.Json;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Core.Debug;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

internal readonly record struct MusicPlaybackTelemetry(
    SongRow? Current,
    string? OpenedEntry,
    bool OwnsCurrent,
    bool Recorded,
    long PlaybackEpoch,
    int Mode,
    double DurationSeconds,
    string PresentationKey,
    MusicOnlineTarget? ActiveTarget,
    bool ReopeningPending,
    bool HasPending);

/// <summary>
/// Standalone Playback service owning chorus metadata resolution/caching,
/// listening feedback session tracking, and playback rate intent/receipt management.
/// </summary>
internal sealed class MusicPlaybackMetadataService(
    MusicRuntimeContext ctx,
    MusicSettingsController settings,
    Func<MusicPlaybackTelemetry> readTelemetry,
    Action<string> notify,
    Action refreshPresentation)
{
    private MusicFeedbackController PlaybackFeedback => ctx.Application.Feedback;

    // Chorus state
    private CancellationTokenSource? chorusCancellation;
    private string? chorusKey;
    private double? chorusStart, chorusEnd;
    private long chorusAccountEpoch, chorusIssuedEpoch;

    // Feedback state
    private MusicPlayContextRecord? currentFeedbackContext;
    private MusicRecommendationFeedback? currentRecommendationFeedback;
    private bool currentFeedbackAutomatic, feedbackStarted, feedbackSuspended;

    // Playback rate state
    // Original player behavior is session-scoped: a fresh App starts at 1x.
    // This is intent, not a measured output rate or a second media session.
    private double playbackRate = 1;
    private AudioPlaybackRate? playbackRateReceipt;

    internal MusicPlayContextRecord? CurrentFeedbackContext
    {
        get => currentFeedbackContext;
        set => currentFeedbackContext = value;
    }

    // Chorus presentation
    internal bool HasChorus
    {
        get
        {
            var t = readTelemetry();
            return chorusStart is { } start && chorusEnd is { } end &&
                chorusIssuedEpoch == Interlocked.Read(ref chorusAccountEpoch) && t.OwnsCurrent && ctx.PlaybackDevice.HasMedia &&
                start < t.DurationSeconds && end <= t.DurationSeconds;
        }
    }

    internal string ChorusLabel => HasChorus ? "官方高潮 " + MusicPlaybackClock.FormatClock(chorusStart!.Value) : "";
    internal double ChorusPositionSeconds => HasChorus ? chorusStart!.Value : double.NaN;

    private void ChorusBindings()
    {
        notify(nameof(MusicPlaybackController.HasChorus));
        notify(nameof(MusicPlaybackController.ChorusLabel));
        notify(nameof(MusicPlaybackController.ChorusPositionSeconds));
    }

    internal void ClearChorus()
    {
        var cancellation = chorusCancellation;
        chorusCancellation = null;
        cancellation?.Cancel();
        bool hadState = chorusKey is not null || chorusStart is not null || chorusEnd is not null;
        chorusKey = null; chorusStart = chorusEnd = null;
        if (hadState) ChorusBindings();
    }

    internal void OnChorusAccountChanged()
    {
        Interlocked.Increment(ref chorusAccountEpoch); // Fence before queued UI work.
        if (!ctx.Dispatcher.IsClosing)
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (ctx.Stopping || ctx.Disposed) return;
                ClearChorus();
                SyncChorus();
            });
    }

    internal void SyncChorus()
    {
        var t = readTelemetry();
        // No title matching or borrowing another edition/platform when the selected source lacks metadata.
        if (ctx.Stopping || !t.OwnsCurrent || !ctx.PlaybackDevice.HasMedia || t.Current is null ||
            t.ActiveTarget is not { SourceKey: not null } target)
        {
            if (chorusKey is not null) ClearChorus();
            return;
        }
        var rows = t.Current.Record.Sources.Where(row => row.SourceKey == target.SourceKey).ToArray();
        if (rows.Length != 1) { ClearChorus(); return; }
        var source = rows[0];
        string platform = target.Platform;
        string id = platform switch { "wy" or "tx" => source.SongMid ?? "", "kg" => source.Hash ?? "", _ => "" };
        if (id.Length == 0) { ClearChorus(); return; }
        if (source.Duration is not { } expected || !double.IsFinite(expected) || expected <= 0 || Math.Abs(expected - t.DurationSeconds) > 10)
        {
            if (chorusKey is not null) ClearChorus();
            return;
        }
        var epoch = Interlocked.Read(ref chorusAccountEpoch);
        var key = t.PresentationKey + "\0" + target.SourceKey + "\0" + epoch;
        var durableKey = new MusicChorusKey(target.AccountScope ?? "script:" + target.Installation,
            platform, id, expected);
        if (key == chorusKey) { ChorusBindings(); return; }
        ClearChorus(); chorusKey = key; chorusIssuedEpoch = epoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
        chorusCancellation = request;
        var work = Read(); ctx.AddEventWrite(work); _ = Observe();
        async Task Read()
        {
            try
            {
                var cached = await ctx.PersistentChorus.ReadAsync(durableKey, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                var liveDuration = readTelemetry().DurationSeconds;
                if (cached is { } hit && hit.End <= liveDuration)
                { chorusStart = hit.Start; chorusEnd = hit.End; ChorusBindings(); return; }
                var reply = await ctx.Application.GetCatalogChorusAsync(platform, id, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                var point = reply.GetProperty("point");
                if (point.ValueKind != JsonValueKind.Object) return;
                double start = point.GetProperty("start").GetDouble(), end = point.GetProperty("end").GetDouble();
                liveDuration = readTelemetry().DurationSeconds;
                if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start &&
                    end <= expected && end <= liveDuration)
                {
                    chorusStart = start; chorusEnd = end; ChorusBindings();
                    await ctx.PersistentChorus.PutAsync(durableKey, start, end, request.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                DebugLog.Warn(DebugCategories.MusicPlayback, "chorus", "read-failed",
                    new { platform, error = error.Message });
            }
            finally { if (ReferenceEquals(chorusCancellation, request)) chorusCancellation = null; request.Dispose(); }
        }
        async Task Observe() { try { await work; } finally { ctx.RemoveEventWrite(work); } }
    }

    // Feedback tracking
    internal MusicSongRecord FeedbackSong(SongRow song)
    {
        var snapshot = song.Record;
        if (readTelemetry().ActiveTarget is not { Official: true, SourceKey: not null } target) return snapshot;
        return snapshot with
        {
            Sources = snapshot.Sources
                .Where(row => row.Platform != target.Platform || row.SourceKey == target.SourceKey)
                .ToArray()
        };
    }

    internal void SelectFeedback(SongRow row, bool automatic, MusicRecommendationFeedback? recommendation = null)
    {
        if (row.EntryId is { } entry)
        {
            currentFeedbackAutomatic = automatic;
            currentRecommendationFeedback = recommendation ?? ctx.Application.GetFeedback(entry);
            feedbackStarted = false; feedbackSuspended = false;
            PlaybackFeedback.Select(new(entry, FeedbackSong(row), currentFeedbackContext, automatic)
                { Recommendation = FeedbackRecommendation(entry) });
        }
    }

    private MusicFeedbackRecommendation? FeedbackRecommendation(string entry) =>
        currentRecommendationFeedback is { } origin && origin.EntryId == entry &&
        ctx.Application.GetFeedback(entry) == origin
            ? new(origin, () => ctx.Application.GetFeedback(entry) == origin) : null;

    internal void UpdateFeedbackSelection()
    {
        var current = readTelemetry().Current;
        if (current?.EntryId is { } entry)
            PlaybackFeedback.UpdateSelection(new(entry, FeedbackSong(current), currentFeedbackContext, currentFeedbackAutomatic)
                { Recommendation = FeedbackRecommendation(entry) });
    }

    private double FeedbackPlaybackRate(string? openedEntry) => playbackRateReceipt is { } receipt &&
        receipt.Scope.OwnerId == ctx.Id && receipt.Scope.Identity == openedEntry ? receipt.RequestedRate : double.NaN;

    internal void SampleFeedback(bool? running = null)
    {
        var t = readTelemetry();
        if (!t.Recorded || t.OpenedEntry is null || t.ReopeningPending) return;
        if (ctx.PlaybackDevice.OwnerId != ctx.Id || ctx.PlaybackDevice.Identity != t.OpenedEntry)
        { CompleteFeedback("interrupt"); return; }
        if (!feedbackStarted && ctx.PlaybackDevice.HasMedia && ctx.PlaybackDevice.IsPlaying && !t.HasPending)
            StartFeedback();
        if (!feedbackStarted) return;
        PlaybackFeedback.Observe(t.OpenedEntry, ctx.PlaybackDevice.PositionSeconds, ctx.PlaybackDevice.DurationSeconds,
            !feedbackSuspended && (running ?? ctx.PlaybackDevice.IsPlaying), FeedbackPlaybackRate(t.OpenedEntry), t.Mode);
        settings.PublishFeedbackStatus();
    }

    internal void StartFeedback(bool observedStart = false)
    {
        var t = readTelemetry();
        if (t.OpenedEntry is null || feedbackStarted || !observedStart && !ctx.PlaybackDevice.IsPlaying) return;
        feedbackStarted = true;
        var rate = FeedbackPlaybackRate(t.OpenedEntry);
        PlaybackFeedback.Started(t.OpenedEntry, ctx.PlaybackDevice.PositionSeconds, ctx.PlaybackDevice.DurationSeconds, rate, t.Mode);
        PlaybackFeedback.Observe(t.OpenedEntry, ctx.PlaybackDevice.PositionSeconds, ctx.PlaybackDevice.DurationSeconds,
            ctx.PlaybackDevice.IsPlaying && !feedbackSuspended, rate, t.Mode);
    }

    internal void FinishFeedback(string reason)
    {
        SampleFeedback();
        CompleteFeedback(reason);
    }

    private void CompleteFeedback(string reason)
    {
        feedbackStarted = false;
        PlaybackFeedback.Finish(reason);
    }

    internal void InvalidateFeedback()
    {
        feedbackStarted = false;
        PlaybackFeedback.Invalidate();
    }

    internal void SuspendFeedback()
    {
        SampleFeedback(false);
        feedbackSuspended = true;
    }

    internal void ResumeFeedback(long epoch)
    {
        if (epoch != readTelemetry().PlaybackEpoch || ctx.Stopping) return;
        feedbackSuspended = false;
        SampleFeedback();
    }

    // Playback rate management
    internal double PlaybackRate => playbackRate;
    internal string PlaybackRateText => playbackRate.ToString("0.0", CultureInfo.InvariantCulture) + "×";

    internal double? AcceptedPlaybackRate
    {
        get
        {
            var t = readTelemetry();
            return t.OwnsCurrent && ctx.PlaybackDevice.HasMedia &&
                playbackRateReceipt is { } receipt && receipt.Scope.Identity == t.Current?.EntryId &&
                Math.Abs(receipt.RequestedRate - playbackRate) <= 0.001 ? receipt.RequestedRate : null;
        }
    }

    internal void ClearRateReceipt() => playbackRateReceipt = null;

    internal Task ChangePlaybackRateAsync(double value, bool canChange)
    {
        if (!canChange) return Task.CompletedTask;
        var t = readTelemetry();
        var song = t.Current!;
        var epoch = t.PlaybackEpoch;
        return ctx.RunPlaybackActionAsync(async _ =>
        {
            if (!double.IsFinite(value) || value is < 0.5 or > 3 ||
                Math.Abs(value * 10 - Math.Round(value * 10)) > 0.000001)
                throw new MusicOperationException(PlaybackError.InvalidRate);
            if (!CurrentRateTarget(song, epoch)) return;
            var requested = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            SuspendFeedback();
            try
            {
                var receipt = await ApplyRateToCurrentAsync(song, epoch, requested, ctx.Lifetime.Token);
                if (!CurrentRateTarget(song, epoch)) return;
                playbackRate = requested;
                playbackRateReceipt = receipt;
                PlaybackRateBindings();
            }
            finally { ResumeFeedback(epoch); }
        }, supersede: false);
    }

    private bool CurrentRateTarget(SongRow song, long epoch)
    {
        var t = readTelemetry();
        return !ctx.Stopping && !ctx.Disposed && epoch == t.PlaybackEpoch && ReferenceEquals(song, t.Current) &&
            ctx.PlaybackDevice.OwnerId == ctx.Id && ctx.PlaybackDevice.Identity == song.EntryId && ctx.PlaybackDevice.HasMedia;
    }

    private async Task<AudioPlaybackRate> ApplyRateToCurrentAsync(SongRow song, long epoch,
        double rate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch)) throw new OperationCanceledException();
        var snapshot = await ctx.PlaybackDevice.ReadRateAsync(token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch) || snapshot is null || snapshot.Scope.OwnerId != ctx.Id ||
            snapshot.Scope.Identity != song.EntryId || snapshot.Scope.Generation <= 0)
            throw new OperationCanceledException();
        playbackRateReceipt = double.IsFinite(snapshot.RequestedRate) && snapshot.RequestedRate > 0 ? snapshot : null;
        var receipt = await ctx.PlaybackDevice.SetRateAsync(snapshot.Scope, rate, token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch) || receipt.Scope != snapshot.Scope)
            throw new OperationCanceledException();
        // A mismatching readback is not success for the requested intent, but
        // its read-back, current-input value still belongs to listening facts.
        playbackRateReceipt = double.IsFinite(receipt.RequestedRate) && receipt.RequestedRate > 0 ? receipt : null;
        if (!double.IsFinite(receipt.RequestedRate) || Math.Abs(receipt.RequestedRate - rate) > 0.001)
            throw new MusicOperationException(PlaybackError.PlaybackFailed);
        return receipt;
    }

    internal async Task RestorePlaybackRateAsync(SongRow song, long epoch, CancellationToken token)
    {
        if (!CurrentRateTarget(song, epoch)) return;
        SuspendFeedback();
        playbackRateReceipt = null;
        try
        {
            var receipt = await ApplyRateToCurrentAsync(song, epoch, playbackRate, token);
            if (CurrentRateTarget(song, epoch)) playbackRateReceipt = receipt;
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        catch
        {
            // An unsupported rate does not silently claim success or discard the
            // chosen song. Retain intent for the next input, expose this failure.
            if (CurrentRateTarget(song, epoch)) ctx.SetStatus("当前音频未接受播放倍速");
        }
        finally { ResumeFeedback(epoch); PlaybackRateBindings(); }
    }

    private void PlaybackRateBindings()
    {
        notify(nameof(MusicPlaybackController.PlaybackRate));
        notify(nameof(MusicPlaybackController.PlaybackRateText));
        refreshPresentation();
    }
}
