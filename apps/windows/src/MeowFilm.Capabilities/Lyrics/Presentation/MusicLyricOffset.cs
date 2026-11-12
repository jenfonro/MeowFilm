namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private double lyricOffsetSeconds, publishedLyricPosition;
    private bool publishedCanAdjustLyricOffset;
    private string? lyricOffsetEntry;

    // Positive adjustment delays the lyrics, never the audio.
    public double LyricOffsetSeconds => lyricOffsetSeconds;
    public double LyricPositionSeconds => PositionSeconds - lyricOffsetSeconds;
    public bool CanAdjustLyricOffset => !stopping && OwnsCurrent && playback.HasMedia && Lyrics.Count > 0;

    public void AdjustLyricOffset(double delta)
    {
        if (!CanAdjustLyricOffset || !double.IsFinite(delta)) return;
        lyricOffsetSeconds += delta;
        PublishLyricOffset();
    }

    private void ResetLyricOffset()
    {
        if (lyricOffsetSeconds == 0) return;
        lyricOffsetSeconds = 0;
        PublishLyricOffset();
    }

    private void PublishLyricOffset()
    {
        Changed(nameof(LyricOffsetSeconds));
        Changed(nameof(LyricPreviewTime));
        RefreshLyricPosition();
    }

    private double LyricAudioPosition(MusicLyricRow row)
    {
        var position = Math.Max(0, row.Time + lyricOffsetSeconds);
        return playback.DurationSeconds > 0 ? Math.Min(position, playback.DurationSeconds) : position;
    }
}
