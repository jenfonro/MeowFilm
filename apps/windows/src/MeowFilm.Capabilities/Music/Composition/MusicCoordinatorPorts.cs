using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Presentation shell contract consumed by <see cref="MusicRuntimeContext"/> and controllers.</summary>
internal interface IMusicPresentationShell
{
    FrameworkElement ResolveModalAnchor(MusicModalAnchor anchor);
    XamlRoot? XamlRoot { get; }
    double CaptureScroll();
    void RestoreScroll(double value);
    void ResetSearchPresentation();
    bool IsSettingsActive { get; }
    Brush PlayerBackground { get; }
    Brush PlayerProgress { get; }
    void RefreshAppearance(Brush sampled, bool animate = true);
    void ReapplyAppearance();
    DesktopNavigationGroupAction CreatePlaylistAction { get; }
    MusicDesktopLyricsWindow CreateDesktopLyricsWindow(Window owner, MusicDesktopLyricsGeometry? geometry);
}

/// <summary>Capability-level navigation, cache, and coordination contract consumed by <see cref="MusicRuntimeContext"/>.</summary>
internal interface IMusicCapabilityShell
{
    Task RequestNavigationAsync(string destination);
    Task OpenPlayerDetailAsync();
    Task OpenPlayQueueAsync();
    Task ExecuteMediaShortcutAsync(MediaShortcutAction action);
    Task SearchAsync(string query, CancellationToken token);
    Task LoadAsync(MusicLocation target, CancellationToken token, int retry = 0, bool publishStatus = true);
    Task LoadUiAsync(MusicLocation target);
    Task ClearAllMusicCachesAsync(CancellationToken cancellationToken);
    (string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) CaptureContentSelection();
    void SetLocation(MusicLocation next, string? heading = null, bool clearLibrary = false,
        bool clearCatalogSongs = false, bool clearSelection = false, bool clearTotal = false, bool clearCollection = false);
    bool PublishContentSelection((string Route, string SearchView, ObservableCollection<MusicCatalogSection> Sections) previous,
        bool refreshSearch = true);
    MusicLocation CaptureLocation();
    void RememberLocation();
    long AdvanceLoadGeneration();
    ObservableCollection<SongRow> ActiveSongs { get; }
    string CatalogAccountNotice { get; }
    void ClearCatalogAccountNotice();
    void CancelOnlinePlayback();
    void RefreshPlaybackPresentationAvailability();
}

internal interface IMusicAccountsCoordinator
{
    void RefreshPlatformSetting(string platform);
    Task ActivatePlatformSettingsAsync(CancellationToken token);
    Task DeactivateAccountDetailsAsync();
    Task DeactivateLibrarySyncAsync();
    Task RetireAccountTransferAsync();
}

internal interface IMusicCatalogCoordinator
{
    string CatalogSearchKind { get; }
    string HomePlatform { get; }
    bool IsCatalogPage { get; }
    string CatalogLoadedPlatform { get; }
    CatalogTarget? CatalogLoadedTarget { get; }
    string CatalogQuery { get; }
    long CatalogActionGeneration { get; }
    MusicCatalogChoice[] CatalogFmScenes { get; }
    MusicCatalogCard? PreparingCatalogCard { get; }
    void RetireCatalogSourcePages();
    void BeginHomeAccountRecovery(bool forceRead);
    void RefreshHomeDisplay();
    void RetireCatalogSession();
    void CommitCatalogPlayback();
    void InvalidateCatalogAction();
    long CatalogAccountObservation(string platform);
    void ClearPreparingCatalogCard();
    void PublishCatalogActionError(Exception error);
    void SetPreparingCatalogCard(MusicCatalogCard? card);
    void ClearCatalogSectionsAndTags();
    Task OpenCatalogTargetAsync(CatalogTarget target, string? id);
}

internal interface IMusicLyricsCoordinator
{
    bool IsLyrics { get; }
    bool NeedsLyrics { get; }
    void DesktopLyricBindings();
    void PublishOnlineSettings(bool lyricsEnabled);
    Task RestoreLyricsAsync();
    void RetireLyrics();
}

internal interface IMusicPlaybackCoordinator
{
    SongRow? CurrentSong { get; }
    bool CanSeek { get; }
    bool OwnsCurrent { get; }
    long PlaybackEpoch { get; }
    double PositionSeconds { get; }
    bool QueueSaved { get; }
    string? PreparedAudioReceiptSignature { get; }
    void AdmitSourceExclusion(string songId, string provider, string platform);
    void CancelMusicPreload();
    void NotifyVolumeChanged();
    void OnSourceExclusionAdded(string songId, string provider, string platform);
    Task OnSourceExclusionRemovedAsync(string songId, CancellationToken token);
    void PlaybackSourceBindings();
    void QualityPolicyChanged(string signature);
    Task RevokeChangedMusicSourceAsync(string? installation);
    Task ResumePlaybackAsync();
    Task SeekWithFeedbackGuardAsync(double seconds, long epoch);
}

internal interface IMusicQueueCoordinator
{
    void RecommendationBindings();
    void NotifyPlaybackModeAvailability();
    Task<bool> AdvanceRecommendationAsync(bool naturalEnd);
    void ClearSearchRecommendationStart();
    void InvalidateQueueSurface();
    void NotifyModeChanged();
    void NotifyRecommendationPlaybackChanged();
    void ObserveExplicitRecommendationPause();
    void ObserveRecommendationPlayback();
    void RefreshQueueSurfaceCurrent();
    Task RefreshRecommendationQueueAsync(CancellationToken token);
    void StartPendingSearchRecommendationPreview();
}

internal interface IMusicLibraryCoordinator
{
    ObservableCollection<PlaylistRow> Playlists { get; }
    ObservableCollection<PlaylistRow> NetworkPlaylists { get; }
    ObservableCollection<SongRow> CatalogSongsSource { get; }
    string Heading { get; }
    int Total { get; }
    SongRow? SelectedSong { get; set; }
    Task RefreshPlaylistsAsync(CancellationToken token);
    Task RefreshAfterEditAsync(long origin);
    void ClearSelectionForCatalog(string heading);
    void ClearSelectionOnly();
    Task AppendSongNextAsync(SongRow song);
    Task FavoriteSongAsync(SongRow song);
    Task ToggleSongFavoriteAsync(SongRow song);
    void NotifyDetailBindings();
    void NotifySelectionChanged();
    void PublishCatalogSummary(int total, bool clearSelection);
    void ClearTotal();
    void PublishSearchSummary(string heading, int count);
}

internal interface IMusicSearchCoordinator
{
    bool IsSearchPage { get; }
    bool IsSearchSongs { get; }
    bool HasSearchSession { get; }
    string SearchKeyword { get; }
    string SearchView { get; }
    long SearchAccountEpoch { get; }
    ObservableCollection<SongRow> SearchSongsSource { get; }
    void ClearSearchVisibleSnapshot();
    Task RetireSearchAsync();
    Task BeginSearchAsync(string query, CancellationToken token, bool refresh);
    Task CancelSearchAsync();
    void RememberSearch();
    Task RestoreSearchAsync(CancellationToken token);
    void RetireSearchTracked();
    void SearchBindings();
    Task SwitchSearchKindAsync();
}
