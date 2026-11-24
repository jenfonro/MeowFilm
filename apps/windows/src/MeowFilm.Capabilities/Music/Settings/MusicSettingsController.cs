using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
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
/// Lean Layer-1 coordinator for all music settings, delegating domain persistence and state
/// to cohesive standalone handlers and notifying downstream domains exclusively via explicit events.
/// </summary>
public sealed partial class MusicSettingsController : MusicController
{
    private readonly MusicSourceManagementService sourceManagement;
    private readonly MusicDesktopLyricsSettings desktopLyricsSettings;
    private readonly MusicSearchMetadataSettings searchMetadataSettings;
    private readonly MusicPlaybackQualitySettings qualitySettings;
    private readonly MusicAppearanceSettings appearanceSettings;
    private readonly MusicCatalogSourceSettings catalogSourceSettings;
    private readonly MusicFeedbackSettingsHandler feedbackSettings;

    private readonly object settingsLifecycleGate = new();
    private Task settingsLifecycleTail = Task.CompletedTask;
    private CancellationTokenSource? settingsActivation;
    private bool settingsRequested;

    private long deviceSettingsRevision, volumeEdit;
    private int savedVolume = 100;
    private bool deviceSettingsLoaded;

    internal MusicSettingsController(MusicRuntimeContext ctx) : base(ctx)
    {
        Action<string> notify = name => Changed(name);
        sourceManagement = new(ctx);
        desktopLyricsSettings = new(ctx, notify);
        searchMetadataSettings = new(ctx, notify);
        qualitySettings = new(ctx, notify);
        appearanceSettings = new(ctx, notify);
        catalogSourceSettings = new(ctx, notify);
        feedbackSettings = new(ctx, notify);

        desktopLyricsSettings.LayoutChanged += () => DesktopLyricLayoutChanged?.Invoke();
        catalogSourceSettings.CatalogSourceInvalidated += () => CatalogSourceInvalidated?.Invoke();
        feedbackSettings.FeedbackPlatformChanged += platform => FeedbackPlatformChanged?.Invoke(platform);
        qualitySettings.QualityPolicyAccepted += (sig, changed) => QualityPolicyAccepted?.Invoke(sig, changed);
        qualitySettings.OnlineLyricsEnabledChanged += enabled => OnlineLyricsEnabledChanged?.Invoke(enabled);
        qualitySettings.SourceExclusionAdmitted += (songId, provider, platform) => SourceExclusionAdmitted?.Invoke(songId, provider, platform);
        qualitySettings.SourceExclusionAdded += (songId, provider, platform) => SourceExclusionAdded?.Invoke(songId, provider, platform);
        qualitySettings.SourceExclusionRemoved += (songId, token) => SourceExclusionRemoved?.Invoke(songId, token) ?? Task.CompletedTask;
        searchMetadataSettings.SearchPolicyChanging += () => SearchPolicyChanging?.Invoke() ?? Task.CompletedTask;
        searchMetadataSettings.MetadataPolicyChanging += () => MetadataPolicyChanging?.Invoke();
        searchMetadataSettings.MetadataPolicyApplied += () => MetadataPolicyApplied?.Invoke() ?? Task.CompletedTask;
        searchMetadataSettings.MetadataArtworkRefreshRequested += token => MetadataArtworkRefreshRequested?.Invoke(token) ?? Task.CompletedTask;
    }

    // Explicit cross-domain lifecycle & state events (wired at MusicCapability composition root).
    internal event Func<CancellationToken, Task>? PlatformSettingsActivating;
    internal event Func<Task>? AccountTransferRetiring;
    internal event Func<Task>? LibrarySyncDeactivating;
    internal event Func<Task>? AccountDetailsDeactivating;
    internal event Action<string>? FeedbackPlatformChanged;
    internal event Action? CatalogSourceInvalidated;
    internal event Action? DesktopLyricLayoutChanged;
    internal event Action<bool>? OnlineLyricsEnabledChanged;
    internal event Action? MetadataPolicyChanging;
    internal event Func<Task>? MetadataPolicyApplied;
    internal event Func<CancellationToken, Task>? MetadataArtworkRefreshRequested;
    internal event Action? VolumeSynced;
    internal event Action<string, bool>? QualityPolicyAccepted;
    internal event Action? SourcesListUpdated;
    internal event Action? PreloadInvalidated;
    internal event Func<string?, Task>? SourceRevoking;
    internal event Action<string, string, string>? SourceExclusionAdmitted;
    internal event Action<string, string, string>? SourceExclusionAdded;
    internal event Func<string, CancellationToken, Task>? SourceExclusionRemoved;
    internal event Func<Task>? SearchPolicyChanging;

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

    // General status & cache
    public string Status => ctx.Status;
    public bool CanClearPlaybackCache => CanEdit;
    internal bool IsSettingsRequested => settingsRequested;

    internal void NotifyStatusChanged() => Changed(nameof(Status));
    internal void NoteVolumeEdited() => volumeEdit++;

    // Appearance settings projection
    public MusicCatalogChoice[] ColorModes => appearanceSettings.ColorModes;
    public MusicCatalogChoice[] PlaylistViews => appearanceSettings.PlaylistViews;
    public string AppearanceColorMode => appearanceSettings.ColorMode;
    public string AppearanceColorPalette => appearanceSettings.ColorPalette;
    public string AppearancePlaylistView => appearanceSettings.PlaylistView;
    public bool PlaylistListView => appearanceSettings.PlaylistListView;
    public bool CanEditFixedAppearance => appearanceSettings.CanEditFixed;
    public bool CanEditAppearance => appearanceSettings.CanEdit;
    public string AppearanceStatus => appearanceSettings.Status;
    internal Brush PlayerPalette(Brush cover) => appearanceSettings.PlayerPalette(cover);
    internal void SetAppearanceReadFailed() => appearanceSettings.SetReadFailed();
    internal void RetireAppearanceSettings() => appearanceSettings.Retire();
    internal Task ReadAppearance(CancellationToken token) => appearanceSettings.ReadAsync(token);
    internal Task EnsureAppearanceAsync() => appearanceSettings.EnsureAsync();
    internal Task ReloadAppearanceAsync() => appearanceSettings.ReloadAsync();
    public Task SetAppearanceAsync(string setting, string value) => appearanceSettings.SetAsync(setting, value);

    // Catalog source settings projection
    public MusicCatalogChoice[] CatalogSourceChoices => catalogSourceSettings.Choices;
    public string CatalogSourcePlatform => catalogSourceSettings.Platform;
    public string CatalogSourceStatus => catalogSourceSettings.Status;
    public bool HasCatalogSourceError => catalogSourceSettings.HasError;
    public bool CanEditCatalogSource => catalogSourceSettings.CanEdit;
    public bool CanReadCatalogSource => catalogSourceSettings.CanRead;
    internal bool CatalogSourceLoaded => catalogSourceSettings.Loaded;
    internal void CancelCatalogSourceReadForShutdown() => catalogSourceSettings.CancelReadForShutdown();
    internal Task EnsureCatalogSourceSettingsAsync(CancellationToken token) => catalogSourceSettings.EnsureAsync(token);
    public Task ReadCatalogSourceSettingsAsync() => catalogSourceSettings.ReadAsync();
    public Task SetCatalogSourceAsync(string platform) => catalogSourceSettings.SetAsync(platform);
    internal Task DeactivateCatalogSourceSettingsAsync(bool retirePage = false) => catalogSourceSettings.DeactivateAsync(retirePage);

    // Desktop lyrics settings projection
    public string DesktopLyricLayout => desktopLyricsSettings.Layout;
    public string DesktopLyricNormalColor => desktopLyricsSettings.NormalColor;
    public string DesktopLyricHighlightColor => desktopLyricsSettings.HighlightColor;
    public bool CanEditDesktopLyricSettings => desktopLyricsSettings.CanEdit;
    internal MusicDesktopLyricsGeometry? DesktopLyricGeometry => desktopLyricsSettings.Geometry;
    internal bool DesktopLyricSettingsLoaded => desktopLyricsSettings.Loaded;
    internal Task EnsureDesktopLyricSettingsAsync() => desktopLyricsSettings.EnsureAsync();
    public Task SetDesktopLyricLayoutAsync(string layout) => desktopLyricsSettings.SetLayoutAsync(layout);
    public Task SetDesktopLyricColorAsync(string role, string value) => desktopLyricsSettings.SetColorAsync(role, value);
    internal Task SaveDesktopLyricGeometryAsync(MusicDesktopLyricsGeometry geometry) => desktopLyricsSettings.SaveGeometryAsync(geometry);

    // Feedback settings projection
    public ObservableCollection<MusicFeedbackPlatformRow> FeedbackPlatforms => feedbackSettings.Platforms;
    public bool CanEditFeedback => feedbackSettings.CanEdit;
    public string FeedbackStatus => feedbackSettings.Status;
    internal bool FeedbackSettingsLoaded => feedbackSettings.Loaded;
    internal void PublishFeedbackStatus() => feedbackSettings.PublishStatus();
    internal Task ReadFeedbackSettings(CancellationToken token) => feedbackSettings.ReadAsync(token);
    public Task SetFeedbackEnabledAsync(MusicFeedbackPlatformRow row, bool enabled) => feedbackSettings.SetEnabledAsync(row, enabled);

    // Search & metadata settings projection
    public MusicSearchKindSettings SongSearch => searchMetadataSettings.SongSearch;
    public MusicSearchKindSettings PlaylistSearch => searchMetadataSettings.PlaylistSearch;
    public ObservableCollection<MusicPlatformPriorityRow> MetadataPlatforms => searchMetadataSettings.MetadataPlatforms;
    public bool CanEditSearchPreferences => searchMetadataSettings.CanEditSearchPreferences;
    public bool HasSearchHistory => searchMetadataSettings.HasSearchHistory;
    public string SearchPreferenceStatus => searchMetadataSettings.SearchPreferenceStatus;
    public bool CanEditMetadata => searchMetadataSettings.CanEditMetadata;
    internal bool SearchPreferencesLoaded(string kind) => searchMetadataSettings.SearchPreferencesLoaded(kind);
    internal Task<bool> EnsureSearchPreferencesLoadedAsync(string kind, CancellationToken token) => searchMetadataSettings.EnsureSearchPreferencesLoadedAsync(kind, token);
    internal void CancelSearchHistoryForShutdown() => searchMetadataSettings.CancelSearchHistoryForShutdown();
    internal Task ReadSearchPreferences(CancellationToken token) => searchMetadataSettings.ReadSearchPreferencesAsync(token);
    internal Task ActivateSearchHistoryAsync(SearchHistorySurface surface) => searchMetadataSettings.ActivateSearchHistoryAsync(surface);
    internal Task DeactivateSearchHistoryAsync(SearchHistorySurface surface) => searchMetadataSettings.DeactivateSearchHistoryAsync(surface);
    internal Task RetireSearchHistoryAsync() => searchMetadataSettings.RetireSearchHistoryAsync();
    public Task SetSearchAdvancedAsync(string kind, int pages, int concurrency, int cacheMinutes) => searchMetadataSettings.SetSearchAdvancedAsync(kind, pages, concurrency, cacheMinutes);
    public Task SetSearchPlatformEnabledAsync(string kind, MusicPlatformPriorityRow row, bool enabled) => searchMetadataSettings.SetSearchPlatformEnabledAsync(kind, row, enabled);
    public Task MoveSearchPlatformAsync(string kind, MusicPlatformPriorityRow source, MusicPlatformPriorityRow target) => searchMetadataSettings.MoveSearchPlatformAsync(kind, source, target);
    public Task ClearSearchHistoryAsync() => searchMetadataSettings.ClearSearchHistoryAsync();
    internal Task RecordSearchHistory(string text, string kind, CancellationToken token) => searchMetadataSettings.RecordSearchHistoryAsync(text, kind, token);
    internal string[] SearchDispatchPlatforms(string kind) => searchMetadataSettings.SearchDispatchPlatforms(kind);
    internal Task ReadMetadataSettings(CancellationToken token) => searchMetadataSettings.ReadMetadataSettingsAsync(token);
    public Task SetMetadataEnabledAsync(MusicPlatformPriorityRow row, bool enabled) => searchMetadataSettings.SetMetadataEnabledAsync(row, enabled);
    public Task MoveMetadataPlatformAsync(MusicPlatformPriorityRow source, MusicPlatformPriorityRow target) => searchMetadataSettings.MoveMetadataPlatformAsync(source, target);

    // Quality & source exclusions projection
    public ObservableCollection<MusicQualityRow> QualityCategories => qualitySettings.QualityCategories;
    public ObservableCollection<MusicQualityRow> SpatialQualities => qualitySettings.SpatialQualities;
    public string[] PlaybackQualityOptions => qualitySettings.PlaybackQualityOptions;
    public string PlaybackQualityRequest => qualitySettings.PlaybackQualityRequest;
    public string QualityStatus => qualitySettings.QualityStatus;
    public bool CanEditQuality => qualitySettings.CanEditQuality;
    internal MusicQualityPolicyRecord? SavedQualityPolicy => qualitySettings.SavedQualityPolicy;
    internal string SavedQualitySignature => qualitySettings.SavedQualitySignature;
    internal void AcceptQuality(MusicQualitySettingsResult result) => qualitySettings.AcceptQuality(result);
    public Task SetPlaybackQualityRequestAsync(string request) => qualitySettings.SetPlaybackQualityRequestAsync(request);
    public Task SetQualityEnabledAsync(MusicQualityRow row, bool enabled, bool spatial) => qualitySettings.SetQualityEnabledAsync(row, enabled, spatial);
    public Task MoveQualityAsync(MusicQualityRow source, MusicQualityRow target, bool spatial) => qualitySettings.MoveQualityAsync(source, target, spatial);
    public Task ActivateQualitySettingsAsync() => qualitySettings.ActivateAsync(QualitySettingsSurface.Player);
    internal Task ActivateQualitySettingsAsync(QualitySettingsSurface surface) => qualitySettings.ActivateAsync(surface);
    public Task DeactivateQualityAsync() => qualitySettings.DeactivateAsync(QualitySettingsSurface.Player);
    internal Task DeactivateQualityAsync(QualitySettingsSurface surface) => qualitySettings.DeactivateAsync(surface);

    public ObservableCollection<MusicSourceExclusionRow> SourceExclusions => qualitySettings.SourceExclusions;
    public ObservableCollection<MusicExclusionOption> ExclusionSongs => qualitySettings.ExclusionSongs;
    public ObservableCollection<MusicExclusionOption> ExclusionProviders => qualitySettings.ExclusionProviders;
    public IReadOnlyList<MusicExclusionOption> ExclusionPlatforms => qualitySettings.ExclusionPlatforms;
    public bool HasSourceExclusions => qualitySettings.HasSourceExclusions;
    public Task RestoreSourceExclusionAsync(MusicSourceExclusionRow? row) => qualitySettings.RestoreExclusionAsync(row);
    public Task<bool> PrepareSourceExclusionFormAsync() => qualitySettings.PrepareExclusionFormAsync();
    public Task AddSourceExclusionAsync(MusicExclusionOption? song, MusicExclusionOption? provider, MusicExclusionOption? platform) => qualitySettings.AddExclusionAsync(song, provider, platform);

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
        desktopLyricsSettings.NotifyBindings();
        catalogSourceSettings.NotifyBindings();
        qualitySettings.RefreshExclusionRestoreAvailability();
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
        var settings = await RunSettingReceiptAsync(MusicSettingReceiptKeys.DeviceSettings,
            ct => application.GetDeviceSettingsAsync(ct), token);
        token.ThrowIfCancellationRequested();
        AcceptDeviceSettings(settings);
        if (edit == volumeEdit && (playback.OwnerId is null || playback.OwnerId == Id))
        {
            playback.Volume = savedVolume;
            VolumeSynced?.Invoke();
        }
    }

    public Task SaveVolumeAsync() => RunAsync(() => SaveVolumeCore(lifetime.Token));

    internal async Task SaveVolumeCore(CancellationToken token)
    {
        if (!deviceSettingsLoaded || volumeEdit == 0 || savedVolume == playback.Volume) return;
        // Runs in the existing command lane (or after its shutdown drain). Not per slider tick.
        AcceptDeviceSettings(await RunSettingReceiptAsync(MusicSettingReceiptKeys.DeviceSettings,
            ct => application.SaveDeviceSettingsAsync(deviceSettingsRevision, playback.Volume, ct), token));
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
                searchMetadataSettings.RetireMetadata();
                qualitySettings.ReleaseSurface(QualitySettingsSurface.Settings);
                searchMetadataSettings.RetireSearchPreferences();
                feedbackSettings.Retire();
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
        var searchHistory = searchMetadataSettings.ActivateSearchHistoryAsync(SearchHistorySurface.Settings);
        if (PlatformSettingsActivating is { } activatePlatforms)
            await activatePlatforms(token);
        token.ThrowIfCancellationRequested();

        try { await searchMetadataSettings.ReadSearchPreferencesAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            DebugLog.Warn(DebugCategories.MusicLibrary, "music-settings", "activate-search-prefs-failed",
                new { error = error.Message });
        }
        token.ThrowIfCancellationRequested();
        if (!searchMetadataSettings.MetadataLoaded)
        {
            try { await searchMetadataSettings.ReadMetadataSettingsAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { ctx.SetStatus(ErrorText(error)); }
            token.ThrowIfCancellationRequested();
        }
        await qualitySettings.ActivateAsync(QualitySettingsSurface.Settings);
        token.ThrowIfCancellationRequested();
        try { await qualitySettings.ReloadExclusionsAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { ctx.SetStatus(ErrorText(error)); }
        await searchHistory;
    }

    private async Task DeactivateSettingsCoreAsync()
    {
        Exception? failure = null;
        foreach (var retire in new Func<Task>[]
        {
            () => AccountTransferRetiring?.Invoke() ?? Task.CompletedTask,
            () => searchMetadataSettings.DeactivateSearchHistoryAsync(SearchHistorySurface.Settings),
            () => searchMetadataSettings.MetadataWork,
            () => searchMetadataSettings.SearchPreferencesWork,
            () => qualitySettings.QualityWork,
            () => LibrarySyncDeactivating?.Invoke() ?? Task.CompletedTask,
            () => AccountDetailsDeactivating?.Invoke() ?? Task.CompletedTask
        })
        {
            try { await retire(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
