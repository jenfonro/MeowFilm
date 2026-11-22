using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Isolated playback &amp; transport state slice for player chrome and fullscreen record views.</summary>
public sealed class MusicPlaybackState(MusicCapability owner) : INotifyPropertyChanged
{
    internal MusicCapability Owner { get; } = owner;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public bool IsPlaying => Owner.IsPlaying;
    public bool CanPlay => Owner.CanPlay;
    public bool CanTogglePlayback => Owner.CanTogglePlayback;
    public bool CanSeek => Owner.CanSeek;
    public bool CanChangePlaybackMode => Owner.CanChangePlaybackMode;
    public bool CanChangePlaybackRate => Owner.CanChangePlaybackRate;
    public bool CanSwitchSource => Owner.CanSwitchSource;
    public bool CanLoadMoreSources => Owner.CanLoadMoreSources;
    public bool CanToggleDesktopLyrics => Owner.CanToggleDesktopLyrics;
    public bool IsCurrentFavorite => Owner.IsCurrentFavorite;
    public bool IsLoadingSources => Owner.IsLoadingSources;
    public bool HasEmptySources => Owner.HasEmptySources;
    public bool HasQualityButton => Owner.HasQualityButton;

    public string CurrentSongId => Owner.CurrentSongId;
    public string CurrentTitle => Owner.CurrentTitle;
    public string CurrentArtist => Owner.CurrentArtist;
    public string CurrentAlbum => Owner.CurrentAlbum;
    public string CurrentArtwork => Owner.CurrentArtwork;
    public string CurrentArtworkIdentity => Owner.CurrentArtworkIdentity;
    public string CurrentTrackIdentity => Owner.CurrentTrackIdentity;
    public string CurrentSource => Owner.CurrentSource;
    public string CurrentSourceDescription => Owner.CurrentSourceDescription;
    public string CurrentSourceQuality => Owner.CurrentSourceQuality;
    public string CurrentSourceSymbol => Owner.CurrentSourceSymbol;
    public string CurrentQualityLabel => Owner.CurrentQualityLabel;
    public string CurrentFavoriteLabel => Owner.CurrentFavoriteLabel;
    public string DesktopLyricsLabel => Owner.DesktopLyricsLabel;
    public string PlaybackModeIcon => Owner.PlaybackModeIcon;
    public string PlaybackModeText => Owner.PlaybackModeText;
    public string PlaybackFmActionKey => Owner.PlaybackFmActionKey;
    public string PlaybackFmTitle => Owner.PlaybackFmTitle;
    public string PlaybackPresentationKey => Owner.PlaybackPresentationKey;
    public string PlaybackRateText => Owner.PlaybackRateText;
    public string PositionText => Owner.PositionText;
    public string DurationText => Owner.DurationText;

    public double PositionSeconds => Owner.PositionSeconds;
    public double DurationSeconds => Owner.DurationSeconds;
    public double ChorusPositionSeconds => Owner.ChorusPositionSeconds;
    public double PlaybackCacheFraction => Owner.PlaybackCacheFraction;
    public double PlaybackRate => Owner.PlaybackRate;
    public double? AcceptedPlaybackRate => Owner.AcceptedPlaybackRate;
    public int Volume { get => Owner.Volume; set => Owner.Volume = value; }

    public Brush PlayerBackground => Owner.PlayerBackground;
    public Brush PlayerProgressBrush => Owner.PlayerProgressBrush;

    public MusicSourceChoice? AutomaticSourceChoice => Owner.AutomaticSourceChoice;
    public ObservableCollection<MusicSourceChoice> SourceChoices => Owner.SourceChoices;
    public ObservableCollection<PlaylistRow> Playlists => Owner.Playlists;
}
