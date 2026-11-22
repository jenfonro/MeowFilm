using System.Collections.ObjectModel;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

/// <summary>Isolated lyrics &amp; desktop lyrics presentation state slice.</summary>
public sealed class MusicLyricsState(MusicCapability owner) : INotifyPropertyChanged
{
    internal MusicCapability Owner { get; } = owner;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public ObservableCollection<MusicLyricRow> Lyrics => Owner.Lyrics;
    public int ActiveLyricIndex => Owner.ActiveLyricIndex;
    public int LyricDisplayIndex => Owner.LyricDisplayIndex;
    public double LyricPositionSeconds => Owner.LyricPositionSeconds;
    public string LyricStatus => Owner.LyricStatus;
    public string LyricPreviewTime => Owner.LyricPreviewTime;
    public bool IsLyrics => Owner.IsLyrics;
    public bool ShowLyricTranslation => Owner.ShowLyricTranslation;
    public bool HasLyricPreview => Owner.HasLyricPreview;
    public bool CanConfirmLyricPreview => Owner.CanConfirmLyricPreview;
    public bool CanAdjustLyricOffset => Owner.CanAdjustLyricOffset;
    public bool CanCancelLyrics => Owner.CanCancelLyrics;

    public bool IsPlaying => Owner.IsPlaying;
    public bool CanPlay => Owner.CanPlay;
    public bool CanTogglePlayback => Owner.CanTogglePlayback;
    public double? AcceptedPlaybackRate => Owner.AcceptedPlaybackRate;
    public string PlaybackPresentationKey => Owner.PlaybackPresentationKey;

    public MusicLyricRow? DesktopLyricTopRow => Owner.DesktopLyricTopRow;
    public MusicLyricRow? DesktopLyricBottomRow => Owner.DesktopLyricBottomRow;
    public string DesktopLyricText => Owner.DesktopLyricText;
    public string DesktopLyricTranslation => Owner.DesktopLyricTranslation;
    public string DesktopLyricLayout => Owner.DesktopLyricLayout;
    public string DesktopLyricNormalColor => Owner.DesktopLyricNormalColor;
    public string DesktopLyricHighlightColor => Owner.DesktopLyricHighlightColor;
    public bool CanEditDesktopLyricSettings => Owner.CanEditDesktopLyricSettings;
}
