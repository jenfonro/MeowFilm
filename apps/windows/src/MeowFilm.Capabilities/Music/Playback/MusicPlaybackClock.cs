namespace MeowFilm.Capabilities;

/// <summary>Isolated high-frequency playback clock &amp; buffer state.</summary>
public sealed class MusicPlaybackClock : MusicStateObject
{
    private double positionSeconds;
    private double durationSeconds = 1;
    private double playbackCacheFraction;

    public double PositionSeconds => positionSeconds;
    public double DurationSeconds => durationSeconds;
    public double PlaybackCacheFraction => playbackCacheFraction;

    internal void Update(double positionSeconds, double durationSeconds, double cacheFraction)
    {
        UpdatePosition(positionSeconds);
        double clampedDuration = Math.Max(1, durationSeconds);
        SetField(ref this.durationSeconds, clampedDuration, nameof(DurationSeconds));
        UpdateCacheFraction(cacheFraction);
    }

    internal void UpdatePosition(double positionSeconds) =>
        SetField(ref this.positionSeconds, positionSeconds, nameof(PositionSeconds));

    internal void UpdateCacheFraction(double cacheFraction) =>
        SetField(ref playbackCacheFraction, cacheFraction, nameof(PlaybackCacheFraction));

    internal static string FormatClock(double value) => double.IsFinite(value) && value is >= 0 and < 86400000
        ? TimeSpan.FromSeconds(value).ToString(value >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : "0:00";
}
