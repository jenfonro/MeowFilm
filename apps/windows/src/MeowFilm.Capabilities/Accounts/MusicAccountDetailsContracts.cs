// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

/// <summary>
/// Native account-owner capability, not a Node/RPC credential port.
/// Read observes local storage only and NEVER starts HTTP.
/// Refresh is an explicit action, bypasses display caches and requires the exact displayed generation.
/// Neither operation renews credentials or writes anything to the cloud.
/// </summary>
public interface IMusicAccountDetailsSource
{
    Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default);
    Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
        CancellationToken token = default);
}

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
    MusicAccountDetailsFreshness MembershipFreshness = MusicAccountDetailsFreshness.Missing)
{
    public long RecheckAt => Math.Min(ProfileExpiresAt, MembershipRecheckAt);
}

/// <summary>Safe, bounded protocol observation; no raw response, cookie or device.</summary>
public sealed record MusicAccountEntitlements(
    MusicAccountMembership Membership, long CheckedAt, long RecheckAt,
    IReadOnlyList<string> AudioLevels, bool VerificationDeferred = false,
    string? ErrorCode = null, long RetryAt = 0);

public sealed record MusicAccountDetailsObservation(
    MusicAccountProfile Profile, long ProfileFetchedAt, MusicAccountEntitlements Entitlements);

/// <summary>
/// Current trusted-native details contract. The owner supplies its exact issued
/// account; protocol observations alone never create a login or persist credentials.
/// </summary>
internal interface IMusicAccountDetailsProtocol
{
    Task<MusicAccountDetailsObservation> ReadAccountDetailsAsync(
        MusicPlatformSession account, CancellationToken token = default);
}
