using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Feedback;

/// <summary>Native recording identity, never the platform delivering the media URL.</summary>
public sealed record MusicFeedbackRecording(string Platform, string Id, string? MixSongId = null);

/// <summary>One platform attempt belonging to one playback, never a reusable string identity.</summary>
public sealed class MusicFeedbackAttempt
{
    internal readonly object Gate = new();
    internal object? State;
    public override string ToString() => "MusicFeedbackAttempt (transient)";
}

public sealed record MusicFeedbackStart(MusicFeedbackAttempt Attempt, MusicFeedbackRecording Recording,
    double Duration, double Position, double Rate, bool Automatic,
    string Source = "unknown", string SourceId = "", string SourceType = "", string Algorithm = "",
    string Mode = "")
{
    // Only a live native recommendation proof can supply an upstream sourceMap value.
    internal bool IsQueueRecommendation { get; init; }
}

public sealed record MusicFeedbackEnd(double Listened, double MediaListened, double Duration,
    double Position, double Rate, string Reason, string Mode = "");

/// <summary>
/// Current in-process playback feedback owner exposed to the Windows capability.
/// Implementations own settings, exact-account reporting sessions and shutdown drain.
/// </summary>
public interface IMusicPlaybackFeedback
{
    string? LastError { get; }
    Task<MusicFeedbackSettings> ReadSettingsAsync(bool refresh = false, CancellationToken token = default);
    Task<MusicFeedbackSettings> SaveSettingsAsync(long revision, IReadOnlyList<string> enabled,
        CancellationToken token = default);
    void Select(MusicFeedbackSelection value);
    void UpdateSelection(MusicFeedbackSelection value);
    void Started(string entryId, double position, double duration, double rate = 1, int mode = 1);
    void Observe(string entryId, double position, double duration, bool isPlaying, double rate = 1, int mode = 1);
    void Finish(string reason = "ui");
}

/// <summary>
/// Start returns only after the exact account/recording has been accepted. No retries or
/// durable outbound queue: a lost upstream acknowledgment must not duplicate a report.
/// Implementations borrow accounts/HTTP. Dispose cancels/drains; it never fabricates an end.
/// </summary>
public interface IMusicFeedbackProtocol : IAsyncDisposable
{
    Task<IMusicFeedbackSession> StartAsync(MusicPlatformSession account, MusicFeedbackStart start,
        CancellationToken cancellationToken = default);
}

public interface IMusicFeedbackSession : IAsyncDisposable
{
    MusicFeedbackRecording Recording { get; }
    Task FinishAsync(MusicFeedbackEnd end, CancellationToken cancellationToken = default);
}

public sealed class MusicFeedbackException(string code) : Exception(code)
{
    public string Code { get; } = code;
    public override string ToString() => $"{nameof(MusicFeedbackException)}: {Code}";
}
