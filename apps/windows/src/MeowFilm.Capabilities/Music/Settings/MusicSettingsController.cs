using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

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
/// Standalone owner for all music settings: lifecycle, appearance, catalog source, desktop lyrics style,
/// device volume, feedback settings, metadata policy, quality policy, search preferences, script sources,
/// source exclusions, and playback cache clearing.
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

    // Explicit downstream reaction hooks wired during composition.
    internal Action<string> OnRefreshAccountPlatformSetting { get; set; } = null!;
    internal Func<CancellationToken, Task> OnActivateAccountPlatformSettingsAsync { get; set; } = null!;
    internal Func<Task> OnDeactivateAccountDetailsAsync { get; set; } = null!;
    internal Func<Task> OnDeactivateLibrarySyncAsync { get; set; } = null!;
    internal Func<Task> OnRetireAccountTransferAsync { get; set; } = null!;
    internal Func<string> GetCatalogSearchKind { get; set; } = null!;
    internal Action OnRetireCatalogSourcePages { get; set; } = null!;
    internal Action OnDesktopLyricBindings { get; set; } = null!;
    internal Func<bool> GetNeedsLyrics { get; set; } = null!;
    internal Action<JsonElement> OnPublishOnlineLyricsSettings { get; set; } = null!;
    internal Func<Task> OnRestoreLyricsAsync { get; set; } = null!;
    internal Action OnRetireLyrics { get; set; } = null!;
    internal Action<string, string, string> OnAdmitSourceExclusion { get; set; } = null!;
    internal Action OnCancelMusicPreload { get; set; } = null!;
    internal Func<SongRow?> GetCurrentSong { get; set; } = null!;
    internal Action OnNotifyVolumeChanged { get; set; } = null!;
    internal Action<string, string, string> OnSourceExclusionAdded { get; set; } = null!;
    internal Func<string, CancellationToken, Task> OnSourceExclusionRemovedAsync { get; set; } = null!;
    internal Action OnPlaybackSourceBindings { get; set; } = null!;
    internal Func<string?> GetPreparedAudioReceiptSignature { get; set; } = null!;
    internal Action<string> OnQualityPolicyChanged { get; set; } = null!;
    internal Func<string?, Task> OnRevokeChangedMusicSourceAsync { get; set; } = null!;
    internal Action OnClearSearchVisibleSnapshot { get; set; } = null!;
    internal Func<bool> GetIsSearchPage { get; set; } = null!;
    internal Func<Task> OnRetireSearchAsync { get; set; } = null!;

    public string Status => ctx.Status;
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
        OnCancelMusicPreload();
        ArtworkLoader.ClearMemory();
        await presentationCache.ClearAllAsync(lifetime.Token);
        await audioFileCache.ClearAsync(lifetime.Token);
        ctx.SetStatus("播放缓存文件已清理（当前播放不中断）");
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
        var reply = await CallSettledReceipt("music.device.settings.get", null, token);
        token.ThrowIfCancellationRequested();
        AcceptDeviceSettings(reply);
        if (edit == volumeEdit && (playback.OwnerId is null || playback.OwnerId == Id))
        {
            playback.Volume = savedVolume;
            OnNotifyVolumeChanged();
        }
    }

    public Task SaveVolumeAsync() => RunAsync(() => SaveVolumeCore(lifetime.Token));

    internal async Task SaveVolumeCore(CancellationToken token)
    {
        if (!deviceSettingsLoaded || volumeEdit == 0 || savedVolume == playback.Volume) return;
        // Runs in the existing command lane (or after its shutdown drain). Not per slider tick.
        AcceptDeviceSettings(await CallSettledReceipt("music.device.settings.save",
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
                ReleaseQualitySurface(QualitySettingsSurface.Settings);
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
        await OnActivateAccountPlatformSettingsAsync(token);
        token.ThrowIfCancellationRequested();

        try { await ReadSearchPreferences(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }
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
        try { await ReloadSourceExclusions(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { ctx.SetStatus(ErrorText(error)); }
        await searchHistory;
    }

    private async Task DeactivateSettingsCoreAsync()
    {
        Exception? failure = null;
        foreach (var retire in new Func<Task>[]
        {
            () => OnRetireAccountTransferAsync(),
            () => DeactivateSearchHistoryAsync(SearchHistorySurface.Settings),
            () => metadataWork,
            () => searchPreferencesWork,
            () => qualityWork,
            () => OnDeactivateLibrarySyncAsync(),
            () => OnDeactivateAccountDetailsAsync()
        })
        {
            try { await retire(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
