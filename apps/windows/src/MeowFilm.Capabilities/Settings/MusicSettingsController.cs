using System.Text.Json;

namespace MeowFilm.Capabilities;

/// <summary>
/// Standalone owner for all music settings: lifecycle, appearance, catalog source, desktop lyrics style,
/// device volume, feedback settings, metadata policy, quality policy, search preferences, script sources,
/// source exclusions, and playback cache clearing.
/// </summary>
public sealed partial class MusicSettingsController : MusicDomainController
{
    private readonly object settingsLifecycleGate = new();
    private Task settingsLifecycleTail = Task.CompletedTask;
    private CancellationTokenSource? settingsActivation;
    private bool settingsRequested;

    private long deviceSettingsRevision, volumeEdit;
    private int savedVolume = 100;
    private bool deviceSettingsLoaded;

    internal MusicSettingsController(MusicCapability host) : base(host) { }

    public string Status => host.Status;
    public bool CanClearPlaybackCache => CanEdit && audioFileCache is not null;
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
        SourceBindings();
        DesktopLyricSettingsBindings();
        CatalogSourceBindings();
        RefreshExclusionRestoreAvailability();
    }

    public Task ClearPlaybackCacheAsync() => RunAsync(async () =>
    {
        if (audioFileCache is null || !await ConfirmAsync("清空播放缓存？", "播放缓存", lifetime.Token)) return;
        host.Playback.CancelMusicPreload();
        ArtworkLoader.ClearMemory();
        await presentationCache.ClearAllAsync(lifetime.Token);
        await audioFileCache.ClearAsync(lifetime.Token);
        host.SetStatus("播放缓存文件已清理（当前播放不中断）");
    });

    private void AcceptDeviceSettings(JsonElement reply)
    {
        var value = reply.GetProperty("settings");
        deviceSettingsRevision = MusicDto.Integer(value, "revision");
        savedVolume = checked((int)MusicDto.Integer(value, "volume"));
        deviceSettingsLoaded = true;
    }

    internal async Task ReadDeviceSettings(CancellationToken token)
    {
        long edit = volumeEdit;
        var reply = await QualityCall("music.device.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        AcceptDeviceSettings(reply);
        if (edit == volumeEdit && (playback.OwnerId is null || playback.OwnerId == Id))
        {
            playback.Volume = savedVolume;
            host.Playback.NotifyVolumeChanged();
        }
    }

    public Task SaveVolumeAsync() => RunAsync(() => SaveVolumeCore(lifetime.Token));

    internal async Task SaveVolumeCore(CancellationToken token)
    {
        if (!deviceSettingsLoaded || volumeEdit == 0 || savedVolume == playback.Volume) return;
        // Runs in the existing command lane (or after its shutdown drain). Not per slider tick.
        AcceptDeviceSettings(await QualityCall("music.device.settings.save",
            new { ifRevision = deviceSettingsRevision, volume = playback.Volume }, token));
    }

    public Task ActivateSettingsAsync() => SetSettingsActive(true);
    public Task DeactivateSettingsAsync() => SetSettingsActive(false);

    private Task SetSettingsActive(bool active)
    {
        lock (settingsLifecycleGate)
        {
            if (active && (stopping || disposed)) return Task.CompletedTask;
            if (settingsRequested == active) return settingsLifecycleTail;
            settingsRequested = active;

            CancellationTokenSource? activation = null;
            CancellationTokenSource? retirement = null;
            if (active)
            {
                activation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                settingsActivation = activation;
            }
            else
            {
                RetireMetadataSettings();
                RetireQualitySettings();
                RetireSearchPreferencesSettings();
                RetireFeedbackSettings();
                retirement = settingsActivation;
                settingsActivation = null;
                try { retirement?.Cancel(); } catch (ObjectDisposedException) { }
            }

            var previous = settingsLifecycleTail;
            return settingsLifecycleTail = Transition(previous, active, activation, retirement);
        }
    }

    private async Task Transition(Task previous, bool active,
        CancellationTokenSource? activation, CancellationTokenSource? retirement)
    {
        try { await previous; }
        catch
        {
            // A failed visible generation cannot prevent the following explicit
            // Loaded/Unloaded generation from owning and cleaning the surface.
        }

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
        await host.Accounts.ActivatePlatformSettingsAsync(token);
        token.ThrowIfCancellationRequested();

        try { await ReadSearchPreferences(token); }
        catch { }
        token.ThrowIfCancellationRequested();
        if (!metadataLoaded)
        {
            try { await ReadMetadataSettings(token); }
            catch { }
            token.ThrowIfCancellationRequested();
        }
        if (!qualityLoaded)
            await EnsureQualitySettingsAsync();
        token.ThrowIfCancellationRequested();
        try { await ReloadSourceExclusions(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { host.SetStatus(ErrorText(error)); }
        await searchHistory;
    }

    private async Task DeactivateSettingsCoreAsync()
    {
        Exception? failure = null;
        foreach (var retire in new Func<Task>[]
        {
            host.Accounts.RetireAccountTransferAsync,
            () => DeactivateSearchHistoryAsync(SearchHistorySurface.Settings),
            () => metadataWork,
            () => searchPreferencesWork,
            DeactivateQualityAsync,
            () => host.Accounts.LibrarySync.DeactivateAsync(),
            host.Accounts.DeactivateAccountDetailsAsync
        })
        {
            try { await retire(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
