using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;
using MeowFilm.Core.Debug;

namespace MeowFilm.Capabilities;

public sealed class MusicPlatformPriorityRow(string platform, bool enabled) : ISettingsPriorityItem
{
    private bool isEnabled = enabled;
    public string Platform { get; } = platform;
    public string Label => MusicPlatformInfo.DisplayName(Platform);
    public string? LogoUri => MusicPlatformInfo.LogoUri(Platform);
    public bool Enabled => isEnabled;
    internal void Apply(bool value)
    {
        if (isEnabled == value) return;
        isEnabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Layer-1 owner for all music settings, organized into cohesive partial files by setting domain
/// and notifying downstream domains exclusively via explicit events.
/// </summary>
public sealed partial class MusicSettingsController : MusicController
{
    private readonly object settingsLifecycleGate = new();
    private Task settingsLifecycleTail = Task.CompletedTask;
    private CancellationTokenSource? settingsActivation;
    private bool settingsRequested;

    private long deviceSettingsRevision, volumeEdit;
    private int savedVolume = 100;
    private bool deviceSettingsLoaded;

    internal MusicSettingsController(MusicRuntimeContext ctx) : base(ctx) { }

    internal static void ReconcilePriorityRows<T>(ObservableCollection<T> destination, IReadOnlyList<string> keys,
        Func<T, string> keyOf, Func<string, T> create, Action<T, string> apply)
    {
        var existing = destination.ToDictionary(keyOf, StringComparer.Ordinal);
        for (int index = 0; index < keys.Count; index++)
        {
            string key = keys[index];
            if (!existing.TryGetValue(key, out var row))
                row = create(key);
            else
                apply(row, key);
            int currentIndex = destination.IndexOf(row);
            if (currentIndex < 0) destination.Insert(index, row);
            else if (currentIndex != index) destination.Move(currentIndex, index);
        }
        while (destination.Count > keys.Count) destination.RemoveAt(destination.Count - 1);
    }

    public string Status => ctx.Status;
    public bool CanClearPlaybackCache => CanEdit;
    internal bool IsSettingsRequested => settingsRequested;

    internal void NotifyStatusChanged() => Changed(nameof(Status));
    internal void NoteVolumeEdited() => volumeEdit++;

    internal override void RefreshAvailability()
    {
        base.RefreshAvailability();
        Changed(nameof(Status));
        Changed(nameof(CanEditQuality));
        Changed(nameof(CanEditAppearance));
        Changed(nameof(CanEditFixedAppearance));
        Changed(nameof(CanClearPlaybackCache));
        Changed(nameof(CanEditMetadata));
        Changed(nameof(CanEditFeedback));
        Changed(nameof(CanEditSearchPreferences));
        Changed(nameof(CanEditDesktopLyricSettings));
        SourceBindings();
        NotifyCatalogSourceBindings();
        RefreshExclusionRestoreAvailability();
    }

    public Task ClearPlaybackCacheAsync() => RunAsync(async () =>
    {
        if (!await ConfirmAsync("清空播放缓存？", "播放缓存", lifetime.Token)) return;
        await ctx.ClearAllMusicCachesAsync(lifetime.Token);
        ctx.SetStatus("播放缓存文件已清理（当前播放不中断）");
    });

    private void AcceptDeviceSettings(MusicDeviceSettingsRecord value)
    {
        deviceSettingsRevision = value.Revision;
        savedVolume = checked((int)value.Volume);
        deviceSettingsLoaded = true;
    }

    internal async Task ReadDeviceSettings(CancellationToken token)
    {
        long edit = volumeEdit;
        var settings = await application.Library.GetDeviceSettingsAsync(token);
        token.ThrowIfCancellationRequested();
        AcceptDeviceSettings(settings);
        if (edit == volumeEdit && (playback.OwnerId is null || playback.OwnerId == Id))
            playback.Volume = savedVolume;
    }

    public Task SaveVolumeAsync() => RunAsync(() => SaveVolumeCore(lifetime.Token));

    internal async Task SaveVolumeCore(CancellationToken token)
    {
        if (!deviceSettingsLoaded || volumeEdit == 0 || savedVolume == playback.Volume) return;
        AcceptDeviceSettings(await application.Library.SaveDeviceSettingsAsync(deviceSettingsRevision, playback.Volume, token));
    }

    public Task ActivateSettingsAsync() => SetSettingsActive(true);
    public Task DeactivateSettingsAsync() => SetSettingsActive(false);

    private Task SetSettingsActive(bool active)
    {
        CancellationTokenSource? activation = null;
        CancellationTokenSource? retirement = null;
        TaskCompletionSource? gateReady = null;
        Task tail;
        lock (settingsLifecycleGate)
        {
            if (active && (stopping || disposed)) return Task.CompletedTask;
            if (settingsRequested == active) return settingsLifecycleTail;
            settingsRequested = active;

            if (active)
            {
                activation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                settingsActivation = activation;
            }
            else
            {
                retirement = settingsActivation;
                settingsActivation = null;
                gateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var previous = settingsLifecycleTail;
            tail = settingsLifecycleTail = Transition(previous, active, activation, retirement, gateReady?.Task);
        }

        if (gateReady is not null)
        {
            try
            {
                RetireMetadataSettings();
                ReleaseQualitySurface(QualitySettingsSurface.Settings);
                searchPreferencesGeneration++;
                RetireFeedbackSettings();
                try { retirement?.Cancel(); } catch (ObjectDisposedException) { }
            }
            finally { gateReady.TrySetResult(); }
        }

        return tail;
    }

    private async Task Transition(Task previous, bool active,
        CancellationTokenSource? activation, CancellationTokenSource? retirement, Task? ready)
    {
        if (ready is not null) await ready;
        try { await previous; }
        catch { }

        if (active)
        {
            try
            {
                var token = activation!.Token;
                token.ThrowIfCancellationRequested();
                await ActivateSettingsCoreAsync(token);
            }
            catch (OperationCanceledException) when (activation!.IsCancellationRequested) { }
            return;
        }

        try { await DeactivateSettingsCoreAsync(); }
        finally { retirement?.Dispose(); }
    }

    private async Task ActivateSettingsCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var searchHistory = ActivateSearchHistoryAsync(SearchHistorySurface.Settings);
        await ctx.ActivatePlatformSettingsAsync(token);
        token.ThrowIfCancellationRequested();

        try { await ReadSearchPreferences(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "music-settings", "activate-search-prefs-failed",
                new { error = error.Message });
        }
        token.ThrowIfCancellationRequested();
        if (!metadataLoaded)
        {
            try { await ReadMetadataSettings(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { ctx.SetStatus(ErrorText(error)); }
            token.ThrowIfCancellationRequested();
        }
        await ActivateQualitySettingsAsync(QualitySettingsSurface.Settings);
        token.ThrowIfCancellationRequested();
        try { await ReloadExclusionsAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { ctx.SetStatus(ErrorText(error)); }
        await searchHistory;
    }

    private async Task DeactivateSettingsCoreAsync()
    {
        Exception? failure = null;
        foreach (var retire in new Func<Task>[]
        {
            ctx.RetireAccountTransferAsync,
            () => DeactivateSearchHistoryAsync(SearchHistorySurface.Settings),
            () => metadataWork,
            () => searchPreferencesWork,
            () => qualityWork,
            ctx.DeactivatePlatformSyncAsync,
            ctx.DeactivateAccountDetailsAsync
        })
        {
            try { await retire(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal async Task RefreshAfterExternalImportAsync(CancellationToken token)
    {
        try { await ReadAppearance(token); } catch { }
        catalogSourceLoaded = false;
        try { await EnsureCatalogSourceSettingsAsync(token); } catch { }
        try { await ReadDeviceSettings(token); } catch { }
        desktopLyricLoaded = false;
        try { await EnsureDesktopLyricSettingsAsync(); } catch { }
        if (qualityLoaded || settingsRequested)
        {
            qualityLoaded = false;
            try { await ActivateQualitySettingsAsync(qualitySurfaces == QualitySettingsSurface.None ? QualitySettingsSurface.Settings : qualitySurfaces); } catch { }
        }
        if (officialSourcesLoaded || settingsRequested)
        {
            try { await RefreshSourceManagementAsync(); } catch { }
        }
        if (metadataLoaded || settingsRequested)
        {
            metadataLoaded = false;
            try { await ReadMetadataSettings(token); } catch { }
        }
        if (songSearchLoaded || playlistSearchLoaded || settingsRequested)
        {
            songSearchLoaded = false;
            playlistSearchLoaded = false;
            try { await ReadSearchPreferences(token); } catch { }
        }
        if (feedbackLoaded || settingsRequested)
        {
            feedbackLoaded = false;
            try { await ReadFeedbackSettings(token); } catch { }
        }
    }
}
