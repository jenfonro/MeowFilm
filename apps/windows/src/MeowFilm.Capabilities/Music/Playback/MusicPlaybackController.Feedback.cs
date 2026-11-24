using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private MusicFeedbackController PlaybackFeedback => application.Feedback;
    private MusicPlayContextRecord? currentFeedbackContext;
    private MusicRecommendationFeedback? currentRecommendationFeedback;
    private bool currentFeedbackAutomatic, feedbackStarted, feedbackSuspended;

    internal MusicPlayContextRecord? CurrentFeedbackContext => currentFeedbackContext;

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

    internal (MusicPlayContextRecord? Context, MusicQueueSourceRecord? Source) CaptureFeedbackContext()
    {
        if (searchPort.IsSearchPage && searchPort.SearchKeyword.Length > 0)
        {
            // A fused result is not a WY playlist or a single-platform search.
            // The controller resolves the exact selected recording separately.
            return (
                MusicPlayContextRecord.Empty with { SourceType = "search", SearchKeyword = searchPort.SearchKeyword },
                new MusicQueueSourceRecord("search", "搜索结果", "search", null));
        }
        if (!catalogPort.IsCatalogPage || catalogPort.CatalogLoadedPlatform is not ("wy" or "tx" or "kg"))
            return (null, null);
        string? sourceType, sourceId = null, dailyBranch = null, searchKeyword = null;
        if (catalogPort.CatalogLoadedTarget is { } target)
        {
            sourceType = target.Operation;
            if (target.Params.Id is { Length: > 0 } sid)
                sourceId = sid;
            if (target.Operation == "daily" && target.Params.Branch is { Length: > 0 } db)
                dailyBranch = db;
        }
        else if (ctx.CurrentRoute == "catalog-search" && catalogPort.CatalogQuery.Length > 0)
        {
            sourceType = "search";
            searchKeyword = catalogPort.CatalogQuery;
        }
        else sourceType = "song";
        return (
            MusicPlayContextRecord.Empty with
            {
                SourcePlatform = catalogPort.CatalogLoadedPlatform,
                SourceType = sourceType,
                SourceId = sourceId,
                DailyBranch = dailyBranch,
                SearchKeyword = searchKeyword
            },
            null);
    }

    private void UpdateFeedbackSelection()
    {
        if (current?.EntryId is { } entry)
            PlaybackFeedback.UpdateSelection(new(entry, FeedbackSong(current), currentFeedbackContext, currentFeedbackAutomatic)
                { Recommendation = FeedbackRecommendation(entry) });
    }

    private void SampleFeedback(bool? running = null)
    {
        if (!recorded || openedEntry is null || musicRecovery?.Pending is { Reopening: true }) return;
        if (playback.OwnerId != Id || playback.Identity != openedEntry)
        { CompleteFeedback("interrupt"); return; }
        if (!feedbackStarted && playback.HasMedia && playback.IsPlaying && musicRecovery?.Pending is null)
            StartFeedback();
        if (!feedbackStarted) return;
        PlaybackFeedback.Observe(openedEntry, playback.PositionSeconds, playback.DurationSeconds,
            !feedbackSuspended && (running ?? playback.IsPlaying), FeedbackPlaybackRate, mode);
        settings.PublishFeedbackStatus();
    }

    private double FeedbackPlaybackRate => playbackRateReceipt is { } receipt &&
        receipt.Scope.OwnerId == Id && receipt.Scope.Identity == openedEntry ? receipt.RequestedRate : double.NaN;

    private void StartFeedback(bool observedStart = false)
    {
        if (openedEntry is null || feedbackStarted || !observedStart && !playback.IsPlaying) return;
        feedbackStarted = true;
        PlaybackFeedback.Started(openedEntry, playback.PositionSeconds, playback.DurationSeconds, FeedbackPlaybackRate, mode);
        PlaybackFeedback.Observe(openedEntry, playback.PositionSeconds, playback.DurationSeconds,
            playback.IsPlaying && !feedbackSuspended, FeedbackPlaybackRate, mode);
    }

    internal void FinishFeedback(string reason)
    { SampleFeedback(); CompleteFeedback(reason); }

    private void CompleteFeedback(string reason)
    { feedbackStarted = false; PlaybackFeedback.Finish(reason); }

    private void InvalidateFeedback()
    { feedbackStarted = false; PlaybackFeedback.Invalidate(); }

    private void SuspendFeedback()
    { SampleFeedback(false); feedbackSuspended = true; }

    private void ResumeFeedback(long epoch)
    {
        if (epoch != playbackEpoch || stopping) return;
        feedbackSuspended = false; SampleFeedback();
    }
}
