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
    private long feedbackRevision, feedbackGeneration;
    private Task feedbackWork = Task.CompletedTask;
    private bool feedbackLoaded, feedbackBusy;
    private string feedbackStatus = "";
    private string publishedFeedbackStatus = "";

    public ObservableCollection<MusicFeedbackPlatformRow> FeedbackPlatforms { get; } =
        [new("wy"), new("tx"), new("kg")];
    public bool CanEditFeedback => CanEdit && feedbackLoaded && !feedbackBusy;
    public string FeedbackStatus => feedbackStatus.Length > 0 ? feedbackStatus :
        PlaybackFeedback.LastError is { } ? "播放记录上报失败" : "";
    internal bool FeedbackSettingsLoaded => feedbackLoaded;

    internal void PublishFeedbackStatus()
    {
        var value = FeedbackStatus;
        if (value == publishedFeedbackStatus) return;
        publishedFeedbackStatus = value;
        Changed(nameof(FeedbackStatus));
    }

    private bool FeedbackCurrent(long currentGeneration) =>
        currentGeneration == feedbackGeneration && !stopping && !disposed;

    private void RetireFeedbackSettings()
    {
        feedbackGeneration++;
        feedbackLoaded = false;
        feedbackBusy = false;
    }

    internal async Task ReadFeedbackSettings(CancellationToken token)
    {
        long currentGeneration = feedbackGeneration;
        await feedbackWork;
        if (!FeedbackCurrent(currentGeneration)) return;
        try
        {
            var value = await PlaybackFeedback.ReadSettingsAsync(refresh: true, token);
            token.ThrowIfCancellationRequested();
            if (FeedbackCurrent(currentGeneration)) PublishFeedbackSettings(value);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (FeedbackCurrent(currentGeneration))
            {
                feedbackLoaded = false; feedbackStatus = "上报设置读取失败";
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
            FeedbackPlatformChanged?.Invoke(row.Platform);
        }
        feedbackLoaded = true; feedbackStatus = "";
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
        long expectedRevision = feedbackRevision, currentGeneration = feedbackGeneration;
        feedbackBusy = true;
        Changed(nameof(CanEditFeedback));
        FeedbackPlatformChanged?.Invoke(row.Platform);
        var nextWork = RunAsync(async () =>
        {
            long receipt = ctx.Receipts.NextSubmission();
            try
            {
                var value = await PlaybackFeedback.SaveSettingsAsync(expectedRevision, targets, lifetime.Token);
                ctx.Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: true);
                if (FeedbackCurrent(currentGeneration)) PublishFeedbackSettings(value);
            }
            catch (Exception error)
            {
                bool uncertain = error is MusicFeedbackException { Code: "COMMIT_UNKNOWN" };
                if (uncertain)
                    ctx.Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: false);
                if (!FeedbackCurrent(currentGeneration)) return;
                try
                {
                    var value = await PlaybackFeedback.ReadSettingsAsync(refresh: true, lifetime.Token);
                    if (!FeedbackCurrent(currentGeneration)) return;
                    PublishFeedbackSettings(value);
                    bool matched = FeedbackPlatforms.First(v => v.Platform == row.Platform).Enabled == enabled;
                    if (uncertain && matched)
                        ctx.Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: true);
                    feedbackStatus = matched ? "" : (uncertain ? "保存结果未确认，请重新读取" : "播放上报设置未生效");
                }
                catch
                {
                    if (FeedbackCurrent(currentGeneration))
                    {
                        feedbackLoaded = false;
                        feedbackStatus = uncertain ? "保存结果未确认，请重新读取" : "播放上报设置保存失败";
                    }
                }
            }
            finally
            {
                if (FeedbackCurrent(currentGeneration))
                {
                    feedbackBusy = false;
                    Changed(nameof(CanEditFeedback));
                    PublishFeedbackStatus();
                    FeedbackPlatformChanged?.Invoke(row.Platform);
                }
            }
        }, publishStatus: false);
        feedbackWork = nextWork;
        await nextWork;
    }
}
