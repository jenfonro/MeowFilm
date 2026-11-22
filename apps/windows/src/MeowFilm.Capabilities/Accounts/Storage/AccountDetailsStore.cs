// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

internal sealed record StoredAccountDetails(
    long AccountGeneration, string AccountScope, string AccountFingerprint,
    string UserId, MusicAccountProfile Profile, long ProfileFetchedAt, long ProfileExpiresAt,
    MusicAccountEntitlements Rights)
{
    internal bool BelongsTo(StoredAccount account) => AccountGeneration == account.Generation &&
        AccountScope == account.Scope && UserId == account.Profile?.UserId && AccountFingerprint == account.ValueFingerprint;
    public override string ToString() => "StoredAccountDetails (private)";
}

internal static class AccountDetailsStore
{
    internal static Task<StoredAccountDetails?> ReadAsync(IStorageDatabase database, string platform, CancellationToken token) =>
        database.ReadSnapshotAsync(db => Read(db, platform), token);

    internal static StoredAccountDetails? Read(DatabaseSession db, string platform)
    {
        var levels = db.Query(
            "SELECT level FROM windows_music_account_audio_levels WHERE platform=$plat ORDER BY position",
            row => row.GetString(0),
            ("$plat", platform)).ToArray();

        if (!db.TryQuerySingle("""
            SELECT account_generation,account_scope,account_fingerprint,user_id,
                   profile_fetched_at,profile_expires_at,
                   profile_platform,profile_user_id,profile_display_name,profile_avatar,
                   rights_vip,rights_tier,rights_expires_at,rights_checked_at,rights_recheck_at,
                   rights_verification_deferred,rights_error_code,rights_retry_at
            FROM windows_music_account_details
            WHERE platform=$plat
            """,
            row =>
            {
                long generation = row.GetInt64(0);
                string scope = row.GetString(1);
                string fingerprint = row.GetString(2);
                string userId = row.GetString(3);
                long fetched = row.GetNullableInt64(4) ?? 0;
                long expires = row.GetNullableInt64(5) ?? 0;
                string? pPlatform = row.GetNullableString(6);
                string? pUserId = row.GetNullableString(7);
                string? pDisplayName = row.GetNullableString(8);
                string? pAvatar = row.GetNullableString(9);
                bool? vip = row.GetNullableBool(10);
                string? tier = row.GetNullableString(11);
                long? rExpires = row.GetNullableInt64(12);
                long? rChecked = row.GetNullableInt64(13);
                long? rRecheck = row.GetNullableInt64(14);
                bool? rDeferred = row.GetNullableBool(15);
                string? rError = row.GetNullableString(16);
                long? rRetry = row.GetNullableInt64(17);

                AccountRules.Require(
                    generation is >= 0 and <= AccountRules.MaxInteger &&
                    scope.Length is > 0 and <= 128 &&
                    fingerprint.Length is > 0 and <= 64 &&
                    pPlatform == platform && pUserId is not null && pDisplayName is not null &&
                    fetched > 0 && expires == fetched + AccountDetailsSnapshot.ProfileTtl &&
                    tier is not null && rChecked is >= 0 and <= AccountRules.MaxInteger &&
                    rRecheck is >= 0 and <= AccountRules.MaxInteger &&
                    rDeferred is not null && rRetry is >= 0 and <= AccountRules.MaxInteger,
                    "CORRUPT_STORAGE");

                var profile = new MusicAccountProfile(pPlatform!, pUserId!, pDisplayName!, pAvatar);
                var rights = AccountDetailsSnapshot.Freeze(new(
                    new(vip, tier!, rExpires),
                    rChecked!.Value,
                    rRecheck!.Value,
                    levels,
                    rDeferred!.Value,
                    rError,
                    rRetry!.Value));
                return new StoredAccountDetails(generation, scope, fingerprint, userId, profile, fetched, expires, rights);
            },
            out var details,
            ("$plat", platform)))
            return null;

        return details;
    }

    internal static void Write(DatabaseSession db, string platform, StoredAccountDetails next)
    {
        var rights = next.Rights;
        db.Execute("""
            INSERT INTO windows_music_account_details(
                platform,account_generation,account_scope,account_fingerprint,user_id,
                profile_fetched_at,profile_expires_at,
                profile_platform,profile_user_id,profile_display_name,profile_avatar,
                rights_vip,rights_tier,rights_expires_at,rights_checked_at,rights_recheck_at,
                rights_verification_deferred,rights_error_code,rights_retry_at)
            VALUES(
                $plat,$gen,$scope,$fp,$uid,
                $pf,$pe,
                $pp,$puid,$pname,$pava,
                $rvip,$rtier,$rexp,$rchk,$rrech,
                $rdef,$rerr,$rret)
            ON CONFLICT(platform) DO UPDATE SET
                account_generation=excluded.account_generation,
                account_scope=excluded.account_scope,
                account_fingerprint=excluded.account_fingerprint,
                user_id=excluded.user_id,
                profile_fetched_at=excluded.profile_fetched_at,
                profile_expires_at=excluded.profile_expires_at,
                profile_platform=excluded.profile_platform,
                profile_user_id=excluded.profile_user_id,
                profile_display_name=excluded.profile_display_name,
                profile_avatar=excluded.profile_avatar,
                rights_vip=excluded.rights_vip,
                rights_tier=excluded.rights_tier,
                rights_expires_at=excluded.rights_expires_at,
                rights_checked_at=excluded.rights_checked_at,
                rights_recheck_at=excluded.rights_recheck_at,
                rights_verification_deferred=excluded.rights_verification_deferred,
                rights_error_code=excluded.rights_error_code,
                rights_retry_at=excluded.rights_retry_at
            """,
            ("$plat", platform),
            ("$gen", next.AccountGeneration),
            ("$scope", next.AccountScope),
            ("$fp", next.AccountFingerprint),
            ("$uid", next.UserId),
            ("$pf", next.ProfileFetchedAt),
            ("$pe", next.ProfileExpiresAt),
            ("$pp", next.Profile.Platform),
            ("$puid", next.Profile.UserId),
            ("$pname", next.Profile.DisplayName),
            ("$pava", next.Profile.Avatar),
            ("$rvip", rights.Membership.Vip),
            ("$rtier", rights.Membership.Tier),
            ("$rexp", rights.Membership.ExpiresAt),
            ("$rchk", rights.CheckedAt),
            ("$rrech", rights.RecheckAt),
            ("$rdef", rights.VerificationDeferred),
            ("$rerr", rights.ErrorCode),
            ("$rret", rights.RetryAt));

        db.Execute("DELETE FROM windows_music_account_audio_levels WHERE platform=$plat", ("$plat", platform));
        for (int i = 0; i < rights.AudioLevels.Count; i++)
            db.Execute(
                "INSERT INTO windows_music_account_audio_levels(platform,position,level) VALUES($plat,$pos,$lvl)",
                ("$plat", platform), ("$pos", i), ("$lvl", rights.AudioLevels[i]));
    }
}
