// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace MeowFilm.Capabilities.Accounts;

// Only safe profile metadata is a DTO. Neither requests nor handoffs are records.
public sealed record MusicAccountProfile(string Platform, string UserId, string DisplayName, string? Avatar = null);
public sealed record MusicAccountPoll(string Platform, string ChallengeId, string Status);
public sealed record MusicAccountStatusRecord(
    string Platform,
    long Generation,
    string State,
    MusicAccountProfile? Profile,
    long? ValidatedAt);
public sealed record MusicAccountListResult(IReadOnlyList<MusicAccountStatusRecord> Items);
public sealed record MusicAccountQrChallenge(
    string Platform,
    string ChallengeId,
    long ExpiresAt,
    string DisplayKind,
    string? Url = null,
    string? ImageBase64 = null);
public sealed record MusicAccountQrPollResult(
    string Platform,
    string ChallengeId,
    string Status,
    MusicAccountStatusRecord? Account = null);
public sealed record MusicAccountSmsSendResult(string Platform, bool Sent, int RetryAfterSeconds);

/// <summary>The credential actually authenticated by Node, kept off ordinary profile DTOs.</summary>
[DebuggerDisplay("MusicAccountVerifiedCredential (redacted)")]
internal sealed class MusicAccountVerifiedCredential
{
    internal MusicAccountVerifiedCredential(string cookie, MusicAccountDetailsObservation details)
    {
        AccountRules.Require(!string.IsNullOrEmpty(cookie), "INVALID_RESPONSE");
        Cookie = cookie; Details = details;
    }
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] internal string Cookie { get; }
    [JsonIgnore] internal MusicAccountDetailsObservation Details { get; }
    public override string ToString() => "MusicAccountVerifiedCredential (redacted)";
}

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

public sealed record MusicAccountSmsSent(string Platform, bool Sent, int RetryAfterSeconds);

[DebuggerDisplay("MusicAccountSmsInput (redacted)")]
public sealed class MusicAccountSmsInput(string platform, string phone, string countryCode = "86", string? captcha = null)
{
    [JsonIgnore] public string Platform { get; } = platform;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string Phone { get; } = phone;
    [JsonIgnore] public string CountryCode { get; } = countryCode;
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)] public string? Captcha { get; } = captcha;
    public override string ToString() => "MusicAccountSmsInput (redacted)";
}

public sealed record MusicAccountMembership(bool? Vip, string Tier, long? ExpiresAt);

public enum MusicAccountDetailsFreshness { Missing, Fresh, Stale, Deferred }

public sealed record MusicAccountDetails(
    string Platform,
    long Generation,
    string State,
    MusicAccountProfile? Profile,
    long ProfileFetchedAt,
    long ProfileExpiresAt,
    MusicAccountMembership Membership,
    long MembershipCheckedAt,
    long MembershipRecheckAt,
    IReadOnlyList<string> AudioLevels,
    bool VerificationDeferred = false,
    string? ErrorCode = null,
    long RetryAt = 0,
    MusicAccountDetailsFreshness ProfileFreshness = MusicAccountDetailsFreshness.Missing,
    MusicAccountDetailsFreshness MembershipFreshness = MusicAccountDetailsFreshness.Missing);

public sealed record MusicAccountEntitlements(
    MusicAccountMembership Membership, long CheckedAt, long RecheckAt,
    IReadOnlyList<string> AudioLevels, bool VerificationDeferred = false,
    string? ErrorCode = null, long RetryAt = 0);

public sealed record MusicAccountDetailsObservation(
    MusicAccountProfile Profile, long ProfileFetchedAt, MusicAccountEntitlements Entitlements);
