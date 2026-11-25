using System.Globalization;
using System.Text.Json;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.PlaybackCache;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Core.Debug;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private MusicFeedbackController PlaybackFeedback => application.Feedback;

    private CancellationTokenSource? chorusCancellation;
    private string? chorusKey;
    private double? chorusStart, chorusEnd;
    private long chorusAccountEpoch, chorusIssuedEpoch;

    private MusicPlayContextRecord? currentFeedbackContext;
    private MusicRecommendationFeedback? currentRecommendationFeedback;
    private bool currentFeedbackAutomatic, feedbackStarted, feedbackSuspended;

    private double playbackRate = 1;
    private AudioPlaybackRate? playbackRateReceipt;

    internal MusicPlayContextRecord? CurrentFeedbackContext => currentFeedbackContext;

    public bool HasChorus => chorusStart is { } start && chorusEnd is { } end &&
        chorusIssuedEpoch == Interlocked.Read(ref chorusAccountEpoch) && OwnsCurrent && playback.HasMedia &&
        start < DurationSeconds && end <= DurationSeconds;

    public string ChorusLabel => HasChorus ? "官方高潮 " + MusicPlaybackClock.FormatClock(chorusStart!.Value) : "";
    public double ChorusPositionSeconds => HasChorus ? chorusStart!.Value : double.NaN;
    public double PlaybackRate => playbackRate;
    public string PlaybackRateText => playbackRate.ToString("0.0", CultureInfo.InvariantCulture) + "×";
    public bool CanChangePlaybackRate => CanSeek;

    public double? AcceptedPlaybackRate => OwnsCurrent && playback.HasMedia &&
        playbackRateReceipt is { } receipt && receipt.Scope.Identity == current?.EntryId &&
        Math.Abs(receipt.RequestedRate - playbackRate) <= 0.001 ? receipt.RequestedRate : null;

    private void ChorusBindings()
    {
        Changed(nameof(HasChorus));
        Changed(nameof(ChorusLabel));
        Changed(nameof(ChorusPositionSeconds));
    }

    private void ClearChorus()
    {
        var cancellation = chorusCancellation;
        chorusCancellation = null;
        cancellation?.Cancel();
        bool hadState = chorusKey is not null || chorusStart is not null || chorusEnd is not null;
        chorusKey = null; chorusStart = chorusEnd = null;
        if (hadState) ChorusBindings();
    }

    private void OnChorusAccountChanged()
    {
        Interlocked.Increment(ref chorusAccountEpoch);
        if (!ctx.Dispatcher.IsClosing)
            _ = ctx.Dispatcher.EnqueueAsync(() =>
            {
                if (stopping || disposed) return;
                ClearChorus();
                SyncChorus();
            });
    }

    private void SyncChorus()
    {
        if (stopping || !OwnsCurrent || !playback.HasMedia || current is null ||
            musicRecovery?.Media?.Target is not { SourceKey: not null } target)
        {
            if (chorusKey is not null) ClearChorus();
            return;
        }
        var rows = current.Record.Sources.Where(row => row.SourceKey == target.SourceKey).ToArray();
        if (rows.Length != 1) { ClearChorus(); return; }
        var source = rows[0];
        string platform = target.Platform;
        string id = platform switch { "wy" or "tx" => source.SongMid ?? "", "kg" => source.Hash ?? "", _ => "" };
        if (id.Length == 0) { ClearChorus(); return; }
        if (source.Duration is not { } expected || !double.IsFinite(expected) || expected <= 0 || Math.Abs(expected - DurationSeconds) > 10)
        {
            if (chorusKey is not null) ClearChorus();
            return;
        }
        var epoch = Interlocked.Read(ref chorusAccountEpoch);
        var key = PlaybackPresentationKey + "\0" + target.SourceKey + "\0" + epoch;
        var durableKey = new MusicChorusKey(target.AccountScope ?? "script:" + target.Installation,
            platform, id, expected);
        if (key == chorusKey) { ChorusBindings(); return; }
        ClearChorus(); chorusKey = key; chorusIssuedEpoch = epoch;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        chorusCancellation = request;
        var work = Read(); AddEventWrite(work); _ = Observe();
        async Task Read()
        {
            try
            {
                var cached = await persistentChorus.ReadAsync(durableKey, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                if (cached is { } hit && hit.End <= DurationSeconds)
                { chorusStart = hit.Start; chorusEnd = hit.End; ChorusBindings(); return; }
                var reply = await application.GetCatalogChorusAsync(platform, id, request.Token);
                if (request.IsCancellationRequested || chorusKey != key || epoch != Interlocked.Read(ref chorusAccountEpoch)) return;
                var point = reply.GetProperty("point");
                if (point.ValueKind != JsonValueKind.Object) return;
                double start = point.GetProperty("start").GetDouble(), end = point.GetProperty("end").GetDouble();
                if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start &&
                    end <= expected && end <= DurationSeconds)
                {
                    chorusStart = start; chorusEnd = end; ChorusBindings();
                    await persistentChorus.PutAsync(durableKey, start, end, request.Token);
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
        async Task Observe() { try { await work; } finally { RemoveEventWrite(work); } }
    }

    private MusicSongRecord FeedbackSong(SongRow song)
    {
        var snapshot = song.Record;
        if (musicRecovery?.Media?.Target is not { Official: true, SourceKey: not null } target) return snapshot;
        return snapshot with
        {
            Sources = snapshot.Sources
                .Where(row => row.Platform != target.Platform || row.SourceKey == target.SourceKey)
                .ToArray()
        };
    }

    private void SelectFeedback(SongRow row, bool automatic, MusicRecommendationFeedback? recommendation = null)
    {
        if (row.EntryId is { } entry)
        {
            currentFeedbackAutomatic = automatic;
            currentRecommendationFeedback = recommendation ?? application.GetFeedback(entry);
            feedbackStarted = false; feedbackSuspended = false;
            PlaybackFeedback.Select(new(entry, FeedbackSong(row), currentFeedbackContext, automatic)
                { Recommendation = FeedbackRecommendation(entry) });
        }
    }

    private MusicFeedbackRecommendation? FeedbackRecommendation(string entry) =>
        currentRecommendationFeedback is { } origin && origin.EntryId == entry &&
        application.GetFeedback(entry) == origin
            ? new(origin, () => application.GetFeedback(entry) == origin) : null;

    private void UpdateFeedbackSelection()
    {
        if (current?.EntryId is { } entry)
            PlaybackFeedback.UpdateSelection(new(entry, FeedbackSong(current), currentFeedbackContext, currentFeedbackAutomatic)
                { Recommendation = FeedbackRecommendation(entry) });
    }

    private double FeedbackPlaybackRate(string? entry) => playbackRateReceipt is { } receipt &&
        receipt.Scope.OwnerId == Id && receipt.Scope.Identity == entry ? receipt.RequestedRate : double.NaN;

    private void SampleFeedback(bool? running = null)
    {
        if (!recorded || openedEntry is null || musicRecovery?.Pending is { Reopening: true }) return;
        if (playback.OwnerId != Id || playback.Identity != openedEntry)
        { CompleteFeedback("interrupt"); return; }
        if (!feedbackStarted && playback.HasMedia && playback.IsPlaying && musicRecovery?.Pending is null)
            StartFeedback();
        if (!feedbackStarted) return;
        PlaybackFeedback.Observe(openedEntry, playback.PositionSeconds, playback.DurationSeconds,
            !feedbackSuspended && (running ?? playback.IsPlaying), FeedbackPlaybackRate(openedEntry), mode);
        settings.PublishFeedbackStatus();
    }

    private void StartFeedback(bool observedStart = false)
    {
        if (openedEntry is null || feedbackStarted || !observedStart && !playback.IsPlaying) return;
        feedbackStarted = true;
        var rate = FeedbackPlaybackRate(openedEntry);
        PlaybackFeedback.Started(openedEntry, playback.PositionSeconds, playback.DurationSeconds, rate, mode);
        PlaybackFeedback.Observe(openedEntry, playback.PositionSeconds, playback.DurationSeconds,
            playback.IsPlaying && !feedbackSuspended, rate, mode);
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

    private void InvalidateFeedback()
    {
        feedbackStarted = false;
        PlaybackFeedback.Invalidate();
    }

    private void SuspendFeedback()
    {
        SampleFeedback(false);
        feedbackSuspended = true;
    }

    private void ResumeFeedback(long epoch)
    {
        if (epoch != playbackEpoch || stopping) return;
        feedbackSuspended = false;
        SampleFeedback();
    }

    private void ClearRateReceipt() => playbackRateReceipt = null;

    public Task ChangePlaybackRateAsync(double value)
    {
        if (!CanChangePlaybackRate) return Task.CompletedTask;
        var song = current!;
        var epoch = playbackEpoch;
        return RunPlaybackActionAsync(async _ =>
        {
            if (!double.IsFinite(value) || value is < 0.5 or > 3 ||
                Math.Abs(value * 10 - Math.Round(value * 10)) > 0.000001)
                throw new MusicOperationException(PlaybackError.InvalidRate);
            if (!CurrentRateTarget(song, epoch)) return;
            var requested = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            SuspendFeedback();
            try
            {
                var receipt = await ApplyRateToCurrentAsync(song, epoch, requested, lifetime.Token);
                if (!CurrentRateTarget(song, epoch)) return;
                playbackRate = requested;
                playbackRateReceipt = receipt;
                PlaybackRateBindings();
            }
            finally { ResumeFeedback(epoch); }
        }, supersede: false);
    }

    private bool CurrentRateTarget(SongRow song, long epoch) =>
        !stopping && !disposed && epoch == playbackEpoch && ReferenceEquals(song, current) &&
        playback.OwnerId == Id && playback.Identity == song.EntryId && playback.HasMedia;

    private async Task<AudioPlaybackRate> ApplyRateToCurrentAsync(SongRow song, long epoch,
        double rate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch)) throw new OperationCanceledException();
        var snapshot = await playback.ReadRateAsync(token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch) || snapshot is null || snapshot.Scope.OwnerId != Id ||
            snapshot.Scope.Identity != song.EntryId || snapshot.Scope.Generation <= 0)
            throw new OperationCanceledException();
        playbackRateReceipt = double.IsFinite(snapshot.RequestedRate) && snapshot.RequestedRate > 0 ? snapshot : null;
        var receipt = await playback.SetRateAsync(snapshot.Scope, rate, token);
        token.ThrowIfCancellationRequested();
        if (!CurrentRateTarget(song, epoch) || receipt.Scope != snapshot.Scope)
            throw new OperationCanceledException();
        playbackRateReceipt = double.IsFinite(receipt.RequestedRate) && receipt.RequestedRate > 0 ? receipt : null;
        if (!double.IsFinite(receipt.RequestedRate) || Math.Abs(receipt.RequestedRate - rate) > 0.001)
            throw new MusicOperationException(PlaybackError.PlaybackFailed);
        return receipt;
    }

    private async Task RestorePlaybackRateAsync(SongRow song, long epoch, CancellationToken token)
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
            if (CurrentRateTarget(song, epoch)) ctx.SetStatus("当前音频未接受播放倍速");
        }
        finally { ResumeFeedback(epoch); PlaybackRateBindings(); }
    }

    private void PlaybackRateBindings()
    {
        Changed(nameof(PlaybackRate));
        Changed(nameof(PlaybackRateText));
        playbackPresentation.Refresh();
    }
}
