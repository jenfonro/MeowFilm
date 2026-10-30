// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>
/// Complete current native account protocol. Every owner receives this exact shape;
/// partial/older protocol implementations are not accepted at runtime.
/// </summary>
internal interface IMusicAccountProtocol : IAsyncDisposable, IMusicAccountSmsProtocol,
    IMusicAccountDetailsProtocol, IMusicQqFeedbackAccountProtocol
{
    Task<MusicAccountDetailsObservation> ValidateAsync(string platform, string cookie, CancellationToken cancellationToken = default);
    Task<MusicAccountChallenge> BeginAsync(string platform, CancellationToken cancellationToken = default);
    Task<MusicAccountPoll> PollAsync(string platform, string challengeId,
        MusicAccountSessionHandoff acceptSession, CancellationToken cancellationToken = default);
    void Cancel(string platform);
}

// Only safe profile metadata is a DTO. Neither requests nor handoffs are records.
public sealed record MusicAccountProfile(string Platform, string UserId, string DisplayName, string? Avatar = null);
public sealed record MusicAccountPoll(string Platform, string ChallengeId, string Status);

/// <summary>The URL is transient QR display content, not a log or persistence field.</summary>
[DebuggerDisplay("MusicAccountChallenge (transient)")]
public sealed class MusicAccountChallenge(string platform, string challengeId, string url, long expiresAt,
    string? imageBase64 = null)
{
    public string Platform { get; } = platform;
    public string ChallengeId { get; } = challengeId;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string Url { get; } = url;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string? ImageBase64 { get; } = imageBase64;
    public string DisplayKind => ImageBase64 is null ? "url" : "png";
    public long ExpiresAt { get; } = expiresAt;
    [JsonIgnore] internal MusicKgClientContext? KgClient { get; init; }
    public override string ToString() => "MusicAccountChallenge (transient)";
}

/// <summary>Only the native owner supplies this callback. JSON commands cannot supply a handoff.</summary>
public delegate Task<bool> MusicAccountSessionHandoff(MusicAccountHandoff handoff, CancellationToken cancellationToken);

[DebuggerDisplay("MusicAccountHandoff (redacted)")]
public sealed class MusicAccountHandoff(string platform, string challengeId, string cookie, MusicAccountDetailsObservation details)
{
    [JsonIgnore] public string Platform { get; } = platform;
    [JsonIgnore] public string ChallengeId { get; } = challengeId;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string Cookie { get; } = cookie;
    [JsonIgnore] public MusicAccountDetailsObservation Details { get; } = details;
    [JsonIgnore] public MusicAccountProfile Profile => Details.Profile;
    public override string ToString() => "MusicAccountHandoff (redacted)";
}

/// <summary>Finite diagnostics, never an upstream message, cause, URI, payload or cookie.</summary>
public sealed class MusicAccountException : Exception
{
    public string Code { get; }
    public int RetryAfterSeconds { get; }
    public MusicAccountException(string code) : this(code, 0) { }
    public MusicAccountException(string code, int retryAfterSeconds) : base(AccountRules.SafeCode(code))
    { Code = Message; RetryAfterSeconds = Math.Clamp(retryAfterSeconds, 0, 600); }
    public override string ToString() => $"{nameof(MusicAccountException)}: {Code}";
}
