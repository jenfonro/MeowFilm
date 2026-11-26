// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

internal sealed record StoredAccountDetails(
    long AccountGeneration, string AccountScope,
    string UserId, MusicAccountProfile Profile, long ProfileFetchedAt, long ProfileExpiresAt,
    MusicAccountEntitlements Rights)
{
    internal const long ProfileTtl = 7 * 86400000L;
    internal bool BelongsTo(StoredAccount account) => AccountGeneration == account.Generation &&
        AccountScope == account.Scope && UserId == account.Profile?.UserId;
    public override string ToString() => "StoredAccountDetails (private)";
}

internal static class AccountDetailsStore
{
    internal static Task<StoredAccountDetails?> ReadAsync(IStorageDatabase database, string platform, CancellationToken token) =>
        database.ReadSnapshotAsync(db => Read(db, platform), token);

    internal static StoredAccountDetails? Read(DatabaseSession db, string platform)
    {
        if (!db.TryQuerySingle("""
            SELECT account_generation,account_scope,user_id,
                   profile_fetched_at_ms,profile_expires_at_ms,
                   profile_platform,profile_user_id,profile_display_name,profile_avatar,
                   rights_vip,rights_tier,rights_expires_at_ms,rights_checked_at_ms,rights_recheck_at_ms,
                   rights_verification_deferred,rights_error_code,rights_retry_at_ms
            FROM windows_music_account_details
            WHERE platform=$plat
            """,
            row =>
            {
                long generation = row.GetInt64(0);
                string scope = row.GetString(1);
                string userId = row.GetString(2);
                long fetched = row.GetNullableInt64(3) ?? 0;
                long expires = row.GetNullableInt64(4) ?? 0;
                string pPlatform = row.GetNullableString(5) ?? platform;
                string pUserId = row.GetNullableString(6) ?? userId;
                string pDisplayName = row.GetNullableString(7) ?? "";
                string? pAvatar = row.GetNullableString(8);
                bool? vip = row.GetNullableBool(9);
                string tier = row.GetNullableString(10) ?? "unknown";
                long? rExpires = row.GetNullableInt64(11);
                long rChecked = row.GetNullableInt64(12) ?? 0;
                long rRecheck = row.GetNullableInt64(13) ?? 0;
                bool rDeferred = row.GetNullableBool(14) ?? false;
                string? rError = row.GetNullableString(15);
                long rRetry = row.GetNullableInt64(16) ?? 0;

                return (
                    Generation: generation,
                    Scope: scope,
                    UserId: userId,
                    Profile: new MusicAccountProfile(pPlatform, pUserId, pDisplayName, pAvatar),
                    Fetched: fetched,
                    Expires: expires,
                    Membership: new MusicAccountMembership(vip, tier, rExpires),
                    CheckedAt: rChecked,
                    RecheckAt: rRecheck,
                    Deferred: rDeferred,
                    ErrorCode: rError,
                    RetryAt: rRetry);
            },
            out var rowData,
            ("$plat", platform)))
            return null;

        var levels = db.Query(
            "SELECT level FROM windows_music_account_audio_levels WHERE platform=$plat ORDER BY position",
            row => row.GetString(0),
            ("$plat", platform)).ToArray();

        var rights = new MusicAccountEntitlements(
            rowData.Membership,
            rowData.CheckedAt,
            rowData.RecheckAt,
            Array.AsReadOnly(levels),
            rowData.Deferred,
            rowData.ErrorCode,
            rowData.RetryAt);
        return new StoredAccountDetails(
            rowData.Generation, rowData.Scope, rowData.UserId,
            rowData.Profile, rowData.Fetched, rowData.Expires, rights);
    }

    internal static void Write(DatabaseSession db, string platform, StoredAccountDetails next)
    {
        var rights = next.Rights;
        db.Execute("""
            INSERT INTO windows_music_account_details(
                platform,account_generation,account_scope,account_fingerprint,user_id,
                profile_fetched_at_ms,profile_expires_at_ms,
                profile_platform,profile_user_id,profile_display_name,profile_avatar,
                rights_vip,rights_tier,rights_expires_at_ms,rights_checked_at_ms,rights_recheck_at_ms,
                rights_verification_deferred,rights_error_code,rights_retry_at_ms)
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
                profile_fetched_at_ms=excluded.profile_fetched_at_ms,
                profile_expires_at_ms=excluded.profile_expires_at_ms,
                profile_platform=excluded.profile_platform,
                profile_user_id=excluded.profile_user_id,
                profile_display_name=excluded.profile_display_name,
                profile_avatar=excluded.profile_avatar,
                rights_vip=excluded.rights_vip,
                rights_tier=excluded.rights_tier,
                rights_expires_at_ms=excluded.rights_expires_at_ms,
                rights_checked_at_ms=excluded.rights_checked_at_ms,
                rights_recheck_at_ms=excluded.rights_recheck_at_ms,
                rights_verification_deferred=excluded.rights_verification_deferred,
                rights_error_code=excluded.rights_error_code,
                rights_retry_at_ms=excluded.rights_retry_at_ms
            """,
            ("$plat", platform),
            ("$gen", next.AccountGeneration),
            ("$scope", next.AccountScope),
            ("$fp", ""),
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

    internal static void Delete(DatabaseSession db, string platform)
    {
        db.Execute("DELETE FROM windows_music_account_details WHERE platform=$plat", ("$plat", platform));
    }
}
