using System.ComponentModel;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private MusicPlaybackState? playbackState;
    private MusicQueueState? queueState;
    private MusicLyricsState? lyricsState;
    private MusicCatalogState? catalogState;
    private MusicAccountsState? accountsState;
    private MusicSettingsState? settingsState;

    public MusicPlaybackState PlaybackState => playbackState ??= new MusicPlaybackState(this);
    public MusicQueueState QueueState => queueState ??= new MusicQueueState(this);
    public MusicLyricsState LyricsState => lyricsState ??= new MusicLyricsState(this);
    public MusicCatalogState CatalogState => catalogState ??= new MusicCatalogState(this);
    public MusicAccountsState AccountsState => accountsState ??= new MusicAccountsState(this);
    public MusicSettingsState SettingsState => settingsState ??= new MusicSettingsState(this);

    private void RouteStateNotification(string? property)
    {
        if (string.IsNullOrEmpty(property))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            playbackState?.Notify("");
            queueState?.Notify("");
            lyricsState?.Notify("");
            catalogState?.Notify("");
            accountsState?.Notify("");
            settingsState?.Notify("");
            return;
        }

        // Shell IDesktopModule contract properties stay on MusicCapability itself.
        if (property is nameof(IDesktopModule.Navigation) or nameof(IDesktopModule.View) or
            nameof(IDesktopModule.SettingsView) or nameof(IDesktopModule.PlayerView) or nameof(IDesktopModule.Title))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            return;
        }

        switch (property)
        {
            // High-frequency playback clock & transport (strictly isolated from Catalog/Queue/Accounts/Settings).
            case nameof(PositionSeconds):
            case nameof(PositionText):
            case nameof(DurationSeconds):
            case nameof(DurationText):
            case nameof(ChorusPositionSeconds):
            case nameof(PlaybackCacheFraction):
            case nameof(PlaybackRate):
            case nameof(PlaybackRateText):
            case nameof(Volume):
            case nameof(CanSeek):
            case nameof(CanChangePlaybackMode):
            case nameof(CanChangePlaybackRate):
            case nameof(CanSwitchSource):
            case nameof(CanLoadMoreSources):
            case nameof(CanToggleDesktopLyrics):
            case nameof(IsDesktopLyricsOpen):
            case nameof(HasCurrent):
            case nameof(HasSourceChoices):
            case nameof(HasConfirmedSourceChoices):
            case nameof(HasChorus):
            case nameof(ChorusLabel):
            case nameof(HasCurrentRecommendationIdentity):
            case nameof(CanHeartbeatCurrent):
            case nameof(CanRecommendCurrent):
            case nameof(Mode):
            case nameof(ModeText):
            case nameof(PlaybackModeActionKey):
            case nameof(IsCurrentFavorite):
            case nameof(IsLoadingSources):
            case nameof(HasEmptySources):
            case nameof(HasQualityButton):
            case nameof(CurrentSongId):
            case nameof(CurrentTitle):
            case nameof(CurrentArtist):
            case nameof(CurrentAlbum):
            case nameof(CurrentArtwork):
            case nameof(CurrentArtworkIdentity):
            case nameof(CurrentTrackIdentity):
            case nameof(CurrentSource):
            case nameof(CurrentSourceDescription):
            case nameof(CurrentSourceQuality):
            case nameof(CurrentSourceSymbol):
            case nameof(CurrentQualityLabel):
            case nameof(CurrentFavoriteLabel):
            case nameof(DesktopLyricsLabel):
            case nameof(PlaybackModeIcon):
            case nameof(PlaybackModeText):
            case nameof(PlaybackFmActionKey):
            case nameof(PlaybackFmTitle):
            case nameof(PlayerBackground):
            case nameof(PlayerProgressBrush):
            case nameof(AutomaticSourceChoice):
                playbackState?.Notify(property);
                break;

            // Shared between Playback & Lyrics (transport state needed by lyrics/desktop lyrics).
            case nameof(IsPlaying):
            case nameof(CanPlay):
            case nameof(CanTogglePlayback):
            case nameof(AcceptedPlaybackRate):
            case nameof(PlaybackPresentationKey):
                playbackState?.Notify(property);
                lyricsState?.Notify(property);
                break;

            // Lyrics & desktop lyrics (strictly isolated from Playback/Queue/Catalog/Accounts/Settings).
            case nameof(ActiveLyricIndex):
            case nameof(LyricDisplayIndex):
            case nameof(LyricPositionSeconds):
            case nameof(LyricStatus):
            case nameof(LyricPreviewTime):
            case nameof(IsLyrics):
            case nameof(ShowLyricTranslation):
            case nameof(HasLyricPreview):
            case nameof(CanConfirmLyricPreview):
            case nameof(CanAdjustLyricOffset):
            case nameof(CanCancelLyrics):
            case nameof(CanImportLocalLyrics):
            case nameof(CanRemoveLocalLyrics):
            case nameof(LyricOffsetSeconds):
            case nameof(LyricsEnabled):
            case nameof(SelectedLyricSource):
            case nameof(DesktopLyricGeometry):
            case nameof(DesktopLyricTopRow):
            case nameof(DesktopLyricBottomRow):
            case nameof(DesktopLyricText):
            case nameof(DesktopLyricTranslation):
            case nameof(DesktopLyricLayout):
            case nameof(DesktopLyricNormalColor):
            case nameof(DesktopLyricHighlightColor):
            case nameof(CanEditDesktopLyricSettings):
                lyricsState?.Notify(property);
                break;

            // Play queue & recommendations.
            case nameof(QueueSurfaceCountText):
            case nameof(QueueSurfaceStatus):
            case nameof(HasQueueSurfaceItems):
            case nameof(CanLoadMoreQueueSurface):
            case nameof(CanClearQueueSurface):
            case nameof(HasRecommendationPanel):
            case nameof(RecommendationTitle):
            case nameof(RecommendationCandidates):
            case nameof(CanChooseRecommendation):
            case nameof(CanRetryRecommendation):
            case nameof(CanReconcileRecommendation):
                queueState?.Notify(property);
                break;

            // Accounts & platform login/sync/feedback.
            case nameof(CanEditFeedback):
            case nameof(AccountBusy):
            case nameof(AccountNotice):
            case nameof(AccountStatus):
            case nameof(FeedbackStatus):
            case nameof(HasAccountQr):
            case nameof(CanBeginAccountQr):
            case nameof(CanCheckAccountQr):
            case nameof(CanSaveAccountQr):
            case nameof(AccountQrImage):
            case nameof(AccountQrAccessibleName):
            case nameof(ShowAccountSms):
            case nameof(CanSendAccountSms):
            case nameof(CanLoginAccountSms):
            case nameof(AccountSmsSendText):
            case nameof(AccountSmsRetrySeconds):
            case nameof(AccountSmsInputEpoch):
            case nameof(SelectedAccount):
                accountsState?.Notify(property);
                break;

            // Settings, sources, quality, exclusions, appearance, home source, cache.
            case nameof(CanEditQuality):
            case nameof(CanEditMetadata):
            case nameof(CanEditSearchPreferences):
            case nameof(CanTransferAccounts):
            case nameof(CanClearPlaybackCache):
            case nameof(CanEditScripts):
            case nameof(CanEditOfficialSources):
            case nameof(CanCheckSelectedSources):
            case nameof(CanRemoveSelectedSources):
            case nameof(CanCancelSourceTransfer):
            case nameof(PlaybackQualityOptions):
            case nameof(PlaybackQualityRequest):
            case nameof(QualityStatus):
            case nameof(HasSourceExclusions):
            case nameof(SearchPreferenceStatus):
            case nameof(CanEditAppearance):
            case nameof(CanEditFixedAppearance):
            case nameof(AppearanceColorMode):
            case nameof(AppearanceColorPalette):
            case nameof(AppearancePlaylistView):
            case nameof(AppearanceStatus):
            case nameof(CanEditCatalogSource):
            case nameof(CanReadCatalogSource):
            case nameof(HasCatalogSourceError):
            case nameof(CatalogSourcePlatform):
            case nameof(CatalogSourceStatus):
                settingsState?.Notify(property);
                break;

            // Cross-cutting edit/status/history flags shared by Catalog & Settings/Accounts.
            case nameof(CanEdit):
                catalogState?.Notify(property);
                accountsState?.Notify(property);
                settingsState?.Notify(property);
                break;
            case nameof(Status):
                catalogState?.Notify(property);
                settingsState?.Notify(property);
                break;
            case nameof(HasSearchHistory):
                catalogState?.Notify(property);
                settingsState?.Notify(property);
                break;
            case nameof(PlaylistListView):
                catalogState?.Notify(property);
                settingsState?.Notify(property);
                break;

            // Everything else belongs to Catalog / Discovery / Library / Music Search.
            default:
                catalogState?.Notify(property);
                break;
        }
    }
}
