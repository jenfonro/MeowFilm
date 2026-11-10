using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Recommendations;

namespace MeowFilm.Capabilities;

public sealed class MusicFeedbackPlatformRow(string platform) : INotifyPropertyChanged
{
    private bool enabled;
    public string Platform { get; } = platform;
    public string Label => new MusicMetadataPlatformRow(Platform, false).Label;
    public bool Enabled => enabled;
    internal void Apply(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MusicCapability
{
    private IMusicPlaybackFeedback PlaybackFeedback => application.Feedback;
    private JsonElement? currentFeedbackContext;
    private MusicRecommendationFeedback? currentRecommendationFeedback;
    private bool currentFeedbackAutomatic;
    private long feedbackRevision;
    private bool feedbackSettingsLoaded, feedbackSettingsBusy;
    private string feedbackStatus = "";
    private string publishedFeedbackStatus = "";
    public ObservableCollection<MusicFeedbackPlatformRow> FeedbackPlatforms { get; } =
        [new("wy"), new("tx"), new("kg")];
    public bool CanEditFeedback => CanEdit && feedbackSettingsLoaded && !feedbackSettingsBusy;
    public string FeedbackStatus => feedbackStatus.Length > 0 ? feedbackStatus :
        PlaybackFeedback.LastError is { } ? "播放记录上报失败" : "";
    private void PublishFeedbackStatus()
    {
        var value = FeedbackStatus;
        if (value == publishedFeedbackStatus) return;
        publishedFeedbackStatus = value;
        Changed(nameof(FeedbackStatus));
    }

    private async Task ReadFeedbackSettings(CancellationToken token, bool refresh = false)
    {
        try
        {
            var value = await PlaybackFeedback.ReadSettingsAsync(refresh, token);
            PublishFeedbackSettings(value);
        }
        catch
        {
            feedbackSettingsLoaded = false; feedbackStatus = "上报设置读取失败";
            Changed(nameof(CanEditFeedback)); PublishFeedbackStatus(); throw;
        }
    }
    private void PublishFeedbackSettings(MusicFeedbackSettings settings)
    {
        feedbackRevision = settings.Revision;
        foreach (var row in FeedbackPlatforms)
        {
            row.Apply(settings.Enabled.Contains(row.Platform));
            RefreshPlatformSetting(row.Platform);
        }
        feedbackSettingsLoaded = true; feedbackStatus = "";
        Changed(nameof(CanEditFeedback)); PublishFeedbackStatus();
    }

    public Task SetFeedbackEnabledAsync(MusicFeedbackPlatformRow row, bool enabled)
    {
        if (!CanEditFeedback || !FeedbackPlatforms.Contains(row) || row.Enabled == enabled)
            return Task.CompletedTask;
        if (enabled && !dialogs.Confirm($"向{row.Label}上报播放记录？", "播放记录上报"))
            return Task.CompletedTask;
        var targets = FeedbackPlatforms.Where(value => value.Enabled || ReferenceEquals(value, row) && enabled)
            .Where(value => !ReferenceEquals(value, row) || enabled)
            .Select(value => value.Platform).ToArray();
        long revision = feedbackRevision;
        feedbackSettingsBusy = true;
        Changed(nameof(CanEditFeedback));
        RefreshPlatformSetting(row.Platform);
        return RunAsync(async () =>
        {
            try
            {
                PublishFeedbackSettings(await PlaybackFeedback.SaveSettingsAsync(revision, targets, lifetime.Token));
            }
            catch (MusicFeedbackException error)
            {
                try
                {
                    PublishFeedbackSettings(await PlaybackFeedback.ReadSettingsAsync(refresh: true, lifetime.Token));
                    feedbackStatus = FeedbackPlatforms.First(value => value.Platform == row.Platform).Enabled == enabled
                        ? "" : "播放上报设置未生效";
                }
                catch
                {
                    feedbackSettingsLoaded = false;
                    feedbackStatus = "播放上报设置保存失败";
                    throw new MusicOperationException(error.Code);
                }
            }
            finally
            {
                feedbackSettingsBusy = false;
                Changed(nameof(CanEditFeedback));
                PublishFeedbackStatus();
                RefreshPlatformSetting(row.Platform);
            }
        });
    }

    private void SelectFeedback(SongRow row, bool automatic, MusicRecommendationFeedback? recommendation = null)
    {
        if (row.EntryId is { } entry)
        {
            currentFeedbackAutomatic = automatic;
            currentRecommendationFeedback = recommendation ??
                (RecommendationSource.GetFeedback(entry) is { Kind: not "fm" } live ? live : null);
            PlaybackFeedback.Select(new(entry, FeedbackSong(row), currentFeedbackContext, automatic)
                { Recommendation = FeedbackRecommendation(entry) });
        }
    }
    private MusicFeedbackRecommendation? FeedbackRecommendation(string entry) =>
        currentRecommendationFeedback is { } origin && origin.EntryId == entry &&
        RecommendationSource.GetFeedback(entry) == origin
            ? new(origin, () => RecommendationSource.GetFeedback(entry) == origin) : null;
    private Dictionary<string, object> CaptureFeedbackContext()
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (IsSearchPage && searchKeyword.Length > 0)
        {
            // A fused result is not a WY playlist or a single-platform search.
            // The controller resolves the exact selected recording separately.
            result["sourceType"] = "search"; result["searchKeyword"] = searchKeyword;
            result["queueSource"] = new { id = "search", title = "搜索结果", kind = "search" };
            return result;
        }
        if (!IsCatalogPage || catalogLoadedPlatform is not ("wy" or "tx" or "kg")) return result;
        result["sourcePlatform"] = catalogLoadedPlatform;
        if (catalogLoadedTarget is { } target)
        {
            result["sourceType"] = target.Operation;
            if (target.Parameters.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 and <= 128 } sourceId)
                result["sourceId"] = sourceId;
            if (target.Operation == "daily" && target.Parameters.TryGetProperty("branch", out var branch) &&
                branch.ValueKind == JsonValueKind.String && branch.GetString() is { Length: > 0 and <= 128 } dailyBranch)
                result["dailyBranch"] = dailyBranch;
        }
        else if (location.Route == "catalog-search" && catalogQuery.Length > 0)
        {
            result["sourceType"] = "search";
            result["searchKeyword"] = catalogQuery;
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
        if (!recorded || openedEntry is null || musicRecovery?.Pending is not null) return;
        if (playback.OwnerId != Id || playback.Identity != openedEntry)
        { PlaybackFeedback.Finish("interrupt"); return; }
        PlaybackFeedback.Observe(openedEntry, playback.PositionSeconds, playback.DurationSeconds,
            running ?? playback.IsPlaying, AcceptedPlaybackRate ?? 1, mode);
        PublishFeedbackStatus();
    }
    private void StartFeedback()
    {
        if (openedEntry is not null)
            PlaybackFeedback.Started(openedEntry, playback.PositionSeconds, playback.DurationSeconds, AcceptedPlaybackRate ?? 1, mode);
    }
    private void FinishFeedback(string reason)
    { SampleFeedback(); CompleteFeedback(reason); }
    private void CompleteFeedback(string reason) => PlaybackFeedback.Finish(reason);
}
