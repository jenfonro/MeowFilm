// SPDX-License-Identifier: GPL-3.0-only
namespace MeowFilm.Capabilities.Accounts;

/// <summary>Null Vip means unknown, never a cached negative membership decision.</summary>
public sealed record MusicAccountMembership(bool? Vip, string Tier, long? ExpiresAt);

public enum MusicAccountDetailsFreshness { Missing, Fresh, Stale, Deferred }

/// <summary>
/// Safe display snapshot. All times are Unix milliseconds; zero means unavailable.
/// State is the owner's none/saved/verified/revoked state, not a claim derived from
/// membership or device data. AudioLevels are descriptive, never playback authority.
/// </summary>
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

/// <summary>Safe, bounded protocol observation; no raw response, cookie or device.</summary>
public sealed record MusicAccountEntitlements(
    MusicAccountMembership Membership, long CheckedAt, long RecheckAt,
    IReadOnlyList<string> AudioLevels, bool VerificationDeferred = false,
    string? ErrorCode = null, long RetryAt = 0);

public sealed record MusicAccountDetailsObservation(
    MusicAccountProfile Profile, long ProfileFetchedAt, MusicAccountEntitlements Entitlements);
