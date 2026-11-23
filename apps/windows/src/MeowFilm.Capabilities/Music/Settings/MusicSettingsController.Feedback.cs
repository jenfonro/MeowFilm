using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Feedback;

namespace MeowFilm.Capabilities;

public sealed class MusicFeedbackPlatformRow(string platform) : INotifyPropertyChanged
{
    private bool enabled;
    public string Platform { get; } = platform;
    public string Label => MusicPlatformInfo.DisplayName(Platform);
    public bool Enabled => enabled;
    internal void Apply(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MusicSettingsController
{
    private MusicFeedbackController PlaybackFeedback => application.Feedback;
    private long feedbackRevision, feedbackSettingsGeneration;
    private Task feedbackSettingsWork = Task.CompletedTask;
    private bool feedbackSettingsLoaded, feedbackSettingsBusy;
    private string feedbackStatus = "";
    private string publishedFeedbackStatus = "";

    public ObservableCollection<MusicFeedbackPlatformRow> FeedbackPlatforms { get; } =
        [new("wy"), new("tx"), new("kg")];
    public bool CanEditFeedback => CanEdit && feedbackSettingsLoaded && !feedbackSettingsBusy;
    public string FeedbackStatus => feedbackStatus.Length > 0 ? feedbackStatus :
        PlaybackFeedback.LastError is { } ? "播放记录上报失败" : "";
    internal bool FeedbackSettingsLoaded => feedbackSettingsLoaded;

    internal void PublishFeedbackStatus()
    {
        var value = FeedbackStatus;
        if (value == publishedFeedbackStatus) return;
        publishedFeedbackStatus = value;
        Changed(nameof(FeedbackStatus));
    }

    private bool FeedbackSettingsCurrent(long generation) =>
        generation == feedbackSettingsGeneration && !stopping && !disposed;

    internal void RetireFeedbackSettings()
    {
        feedbackSettingsGeneration++;
        feedbackSettingsLoaded = false;
        feedbackSettingsBusy = false;
    }

    internal async Task ReadFeedbackSettings(CancellationToken token)
    {
        long generation = feedbackSettingsGeneration;
        await feedbackSettingsWork;
        if (!FeedbackSettingsCurrent(generation)) return;
        try
        {
            var value = await PlaybackFeedback.ReadSettingsAsync(refresh: true, token);
            token.ThrowIfCancellationRequested();
            if (FeedbackSettingsCurrent(generation)) PublishFeedbackSettings(value);
        }
        catch
        {
            if (FeedbackSettingsCurrent(generation))
            {
                feedbackSettingsLoaded = false; feedbackStatus = "上报设置读取失败";
                Changed(nameof(CanEditFeedback)); PublishFeedbackStatus();
            }
            throw;
        }
    }

    private void PublishFeedbackSettings(MusicFeedbackSettings settings)
    {
        feedbackRevision = settings.Revision;
        foreach (var row in FeedbackPlatforms)
        {
            row.Apply(settings.Enabled.Contains(row.Platform));
            OnRefreshAccountPlatformSetting(row.Platform);
        }
        feedbackSettingsLoaded = true; feedbackStatus = "";
        Changed(nameof(CanEditFeedback)); PublishFeedbackStatus();
    }

    public async Task SetFeedbackEnabledAsync(MusicFeedbackPlatformRow row, bool enabled)
    {
        if (!CanEditFeedback || !FeedbackPlatforms.Contains(row) || row.Enabled == enabled)
            return;
        if (enabled && !await ConfirmAsync($"向{row.Label}上报播放记录？", "播放记录上报", lifetime.Token))
            return;
        if (stopping || disposed || !CanEditFeedback || !FeedbackPlatforms.Contains(row) || row.Enabled == enabled)
            return;
        var targets = FeedbackPlatforms.Where(value => value.Enabled || ReferenceEquals(value, row) && enabled)
            .Where(value => !ReferenceEquals(value, row) || enabled)
            .Select(value => value.Platform).ToArray();
        long revision = feedbackRevision, generation = feedbackSettingsGeneration;
        feedbackSettingsBusy = true;
        Changed(nameof(CanEditFeedback));
        OnRefreshAccountPlatformSetting(row.Platform);
        var work = RunAsync(async () =>
        {
            long receipt = Receipts.NextSubmission();
            try
            {
                var value = await PlaybackFeedback.SaveSettingsAsync(revision, targets, lifetime.Token);
                Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: true);
                if (FeedbackSettingsCurrent(generation)) PublishFeedbackSettings(value);
            }
            catch (Exception error)
            {
                if (error is MusicFeedbackException { Code: "COMMIT_UNKNOWN" })
                    Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: false);
                if (!FeedbackSettingsCurrent(generation)) return;
                try
                {
                    // This is the save's own reconciliation, not a page read:
                    // ReadFeedbackSettings waits for this physical work to finish.
                    var value = await PlaybackFeedback.ReadSettingsAsync(refresh: true, lifetime.Token);
                    if (!FeedbackSettingsCurrent(generation)) return;
                    PublishFeedbackSettings(value);
                    feedbackStatus = FeedbackPlatforms.First(value => value.Platform == row.Platform).Enabled == enabled
                        ? "" : "播放上报设置未生效";
                }
                catch
                {
                    if (FeedbackSettingsCurrent(generation))
                    {
                        feedbackSettingsLoaded = false;
                        feedbackStatus = "播放上报设置保存失败";
                    }
                }
            }
            finally
            {
                if (FeedbackSettingsCurrent(generation))
                {
                    feedbackSettingsBusy = false;
                    Changed(nameof(CanEditFeedback));
                    PublishFeedbackStatus();
                    OnRefreshAccountPlatformSetting(row.Platform);
                }
            }
        }, publishStatus: false);
        feedbackSettingsWork = work;
        await work;
    }
}
