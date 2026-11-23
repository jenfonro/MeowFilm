using System.Text.Json;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;

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
    string Source = "unknown", string SourceId = "", string Algorithm = "",
    string Mode = "")
{
    public Guid PlaybackId { get; init; }
    public string EntryId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset AdmittedAt { get; init; }
    // Occurrence-bound source facts, not a platform's serialized wire fields.
    public JsonElement? Context { get; init; }
    public MusicRecommendationFeedback? Recommendation { get; init; }
    // Only a live native recommendation proof can supply an upstream sourceMap value.
    internal bool IsQueueRecommendation { get; init; }
}

public sealed record MusicFeedbackEnd(double Listened, double MediaListened, double Duration,
    double Position, double Rate, string Reason, string Mode = "")
{
    public Guid PlaybackId { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public double PlaybackListened { get; init; }
    public double PlaybackMediaListened { get; init; }
}

internal abstract class MusicFeedbackSession : IAsyncDisposable
{
    public abstract MusicFeedbackRecording Recording { get; }
    public abstract Task FinishAsync(MusicFeedbackEnd end, CancellationToken cancellationToken = default);
    public abstract ValueTask DisposeAsync();
}

public sealed class MusicFeedbackException(string code, string? writeStatus = null) : Exception(code)
{
    public string Code { get; } = code;
    internal string? WriteStatus { get; } = writeStatus;
    public override string ToString() => $"{nameof(MusicFeedbackException)}: {Code}";
}
