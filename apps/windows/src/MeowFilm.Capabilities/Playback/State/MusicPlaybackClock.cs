namespace MeowFilm.Capabilities;

/// <summary>Isolated high-frequency playback clock &amp; buffer state.</summary>
public sealed class MusicPlaybackClock : MusicStateObject
{
    private double positionSeconds;
    private string positionText = "0:00";
    private double durationSeconds = 1;
    private string durationText = "0:00";
    private double playbackCacheFraction;

    public double PositionSeconds => positionSeconds;
    public string PositionText => positionText;
    public double DurationSeconds => durationSeconds;
    public string DurationText => durationText;
    public double PlaybackCacheFraction => playbackCacheFraction;

    internal void Update(double positionSeconds, double durationSeconds, double cacheFraction)
    {
        UpdatePosition(positionSeconds);
        double clampedDuration = Math.Max(1, durationSeconds);
        SetField(ref this.durationSeconds, clampedDuration, nameof(DurationSeconds));
        SetField(ref durationText, FormatClock(durationSeconds), nameof(DurationText));
        UpdateCacheFraction(cacheFraction);
    }

    internal void UpdatePosition(double positionSeconds)
    {
        SetField(ref this.positionSeconds, positionSeconds, nameof(PositionSeconds));
        SetField(ref positionText, FormatClock(positionSeconds), nameof(PositionText));
    }

    internal void UpdateCacheFraction(double cacheFraction) =>
        SetField(ref playbackCacheFraction, cacheFraction, nameof(PlaybackCacheFraction));

    internal static string FormatClock(double value) => double.IsFinite(value) && value is >= 0 and < 86400000
        ? TimeSpan.FromSeconds(value).ToString(value >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : "0:00";
}
