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

/// <summary>
/// Standalone domain handler for platform listening-feedback reporting settings.
/// </summary>
internal sealed class MusicFeedbackSettingsHandler(MusicRuntimeContext ctx, Action<string> notify)
{
    private MusicFeedbackController PlaybackFeedback => ctx.Application.Feedback;
    private long revision, generation;
    private Task work = Task.CompletedTask;
    private bool loaded, busy;
    private string status = "";
    private string publishedStatus = "";

    internal event Action<string>? FeedbackPlatformChanged;

    internal ObservableCollection<MusicFeedbackPlatformRow> Platforms { get; } =
        [new("wy"), new("tx"), new("kg")];
    internal bool CanEdit => ctx.CanEdit && loaded && !busy;
    internal string Status => status.Length > 0 ? status :
        PlaybackFeedback.LastError is { } ? "播放记录上报失败" : "";
    internal bool Loaded => loaded;

    internal void PublishStatus()
    {
        var value = Status;
        if (value == publishedStatus) return;
        publishedStatus = value;
        notify(nameof(MusicSettingsController.FeedbackStatus));
    }

    private bool IsCurrent(long currentGeneration) =>
        currentGeneration == generation && !ctx.Stopping && !ctx.Disposed;

    internal void Retire()
    {
        generation++;
        loaded = false;
        busy = false;
    }

    internal async Task ReadAsync(CancellationToken token)
    {
        long currentGeneration = generation;
        await work;
        if (!IsCurrent(currentGeneration)) return;
        try
        {
            var value = await PlaybackFeedback.ReadSettingsAsync(refresh: true, token);
            token.ThrowIfCancellationRequested();
            if (IsCurrent(currentGeneration)) Publish(value);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (IsCurrent(currentGeneration))
            {
                loaded = false; status = "上报设置读取失败";
                notify(nameof(MusicSettingsController.CanEditFeedback)); PublishStatus();
            }
            throw;
        }
    }

    private void Publish(MusicFeedbackSettings settings)
    {
        revision = settings.Revision;
        foreach (var row in Platforms)
        {
            row.Apply(settings.Enabled.Contains(row.Platform));
            FeedbackPlatformChanged?.Invoke(row.Platform);
        }
        loaded = true; status = "";
        notify(nameof(MusicSettingsController.CanEditFeedback)); PublishStatus();
    }

    internal async Task SetEnabledAsync(MusicFeedbackPlatformRow row, bool enabled)
    {
        if (!CanEdit || !Platforms.Contains(row) || row.Enabled == enabled)
            return;
        if (enabled && !await ctx.ConfirmAsync($"向{row.Label}上报播放记录？", "播放记录上报", ctx.Lifetime.Token))
            return;
        if (ctx.Stopping || ctx.Disposed || !CanEdit || !Platforms.Contains(row) || row.Enabled == enabled)
            return;
        var targets = Platforms.Where(value => value.Enabled || ReferenceEquals(value, row) && enabled)
            .Where(value => !ReferenceEquals(value, row) || enabled)
            .Select(value => value.Platform).ToArray();
        long expectedRevision = revision, currentGeneration = generation;
        busy = true;
        notify(nameof(MusicSettingsController.CanEditFeedback));
        FeedbackPlatformChanged?.Invoke(row.Platform);
        var nextWork = ctx.RunAsync(async () =>
        {
            long receipt = ctx.Receipts.NextSubmission();
            try
            {
                var value = await PlaybackFeedback.SaveSettingsAsync(expectedRevision, targets, ctx.Lifetime.Token);
                ctx.Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: true);
                if (IsCurrent(currentGeneration)) Publish(value);
            }
            catch (Exception error)
            {
                bool uncertain = error is MusicFeedbackException { Code: "COMMIT_UNKNOWN" };
                if (uncertain)
                    ctx.Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: false);
                if (!IsCurrent(currentGeneration)) return;
                try
                {
                    // This is the save's own reconciliation, not a page read:
                    // ReadAsync waits for this physical work to finish.
                    var value = await PlaybackFeedback.ReadSettingsAsync(refresh: true, ctx.Lifetime.Token);
                    if (!IsCurrent(currentGeneration)) return;
                    Publish(value);
                    bool matched = Platforms.First(v => v.Platform == row.Platform).Enabled == enabled;
                    if (uncertain && matched)
                        ctx.Receipts.Record("music.feedback.settings.save", "播放上报", receipt, confirmed: true);
                    status = matched ? "" : (uncertain ? "保存结果未确认，请重新读取" : "播放上报设置未生效");
                }
                catch
                {
                    if (IsCurrent(currentGeneration))
                    {
                        loaded = false;
                        status = uncertain ? "保存结果未确认，请重新读取" : "播放上报设置保存失败";
                    }
                }
            }
            finally
            {
                if (IsCurrent(currentGeneration))
                {
                    busy = false;
                    notify(nameof(MusicSettingsController.CanEditFeedback));
                    PublishStatus();
                    FeedbackPlatformChanged?.Invoke(row.Platform);
                }
            }
        }, publishStatus: false);
        work = nextWork;
        await nextWork;
    }
}
