using System.Text.Json;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

public sealed partial class MusicPlaybackController
{
    private MusicFeedbackController PlaybackFeedback => application.Feedback;
    private JsonElement? currentFeedbackContext;
    private MusicRecommendationFeedback? currentRecommendationFeedback;
    private bool currentFeedbackAutomatic, feedbackStarted, feedbackSuspended;

    internal JsonElement? CurrentFeedbackContext => currentFeedbackContext;

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

    internal Dictionary<string, object> CaptureFeedbackContext()
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (GetIsSearchPage() && GetSearchKeyword().Length > 0)
        {
            // A fused result is not a WY playlist or a single-platform search.
            // The controller resolves the exact selected recording separately.
            result["sourceType"] = "search"; result["searchKeyword"] = GetSearchKeyword();
            result["queueSource"] = new { id = "search", title = "搜索结果", kind = "search" };
            return result;
        }
        if (!GetIsCatalogPage() || GetCatalogLoadedPlatform() is not ("wy" or "tx" or "kg")) return result;
        result["sourcePlatform"] = GetCatalogLoadedPlatform();
        if (GetCatalogLoadedTarget() is { } target)
        {
            result["sourceType"] = target.Operation;
            if (target.Parameters.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 } sourceId)
                result["sourceId"] = sourceId;
            if (target.Operation == "daily" && target.Parameters.TryGetProperty("branch", out var branch) &&
                branch.ValueKind == JsonValueKind.String && branch.GetString() is { Length: > 0 } dailyBranch)
                result["dailyBranch"] = dailyBranch;
        }
        else if (ctx.CurrentRoute == "catalog-search" && GetCatalogQuery().Length > 0)
        {
            result["sourceType"] = "search";
            result["searchKeyword"] = GetCatalogQuery();
        }
        else result["sourceType"] = "song";
        return result;
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
