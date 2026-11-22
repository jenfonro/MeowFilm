// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

// Current account value. Generation is the per-account logical revision.
internal sealed class StoredAccount
{
    internal long Generation { get; init; }
    internal string? Cookie { get; init; }
    internal MusicAccountProfile? Profile { get; init; }
    internal string? Scope { get; init; }
    internal long? ValidatedAt { get; init; }
    internal string? ValueFingerprint { get; init; }
    public override string ToString() => "StoredAccount (redacted)";
}

internal sealed class AccountSnapshot(IReadOnlyDictionary<string, StoredAccount> accounts, StorageRevision revision)
{
    internal StorageRevision Revision { get; } = revision;
    internal StoredAccount Get(string platform) =>
        accounts.TryGetValue(platform, out var account) ? account : new StoredAccount();
    public override string ToString() => "AccountSnapshot (redacted)";
}

/// <summary>Plaintext typed account rows; routine reads materialize only the requested platform.</summary>
internal sealed class MusicAccountRepository(IStorageDatabase database)
{
    internal Task<AccountSnapshot> ReadAsync(string? platform, CancellationToken token) => database.ReadSnapshotAsync(db =>
    {
        var revision = db.Revision(MusicAccountOwner.DocumentNamespace);
        var accounts = new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
        if (platform is not null)
        {
            string target = AccountRules.Platform(platform);
            if (ReadPlatform(db, target) is { } account)
                accounts.Add(target, account);
        }
        else
        {
            foreach (var name in AccountRules.Platforms)
                if (ReadPlatform(db, name) is { } account)
                    accounts.Add(name, account);
        }
        return new AccountSnapshot(accounts, revision);
    }, token);

    internal Task<(StoredAccount Committed, StorageRevision Revision)> ReplaceAsync(
        string platform,
        StoredAccount expected,
        StoredAccount next,
        StorageRevision expectedRevision,
        CancellationToken token)
    {
        string target = AccountRules.Platform(platform);
        return database.WriteAsync(db =>
        {
            if (db.Revision(MusicAccountOwner.DocumentNamespace) != expectedRevision)
                throw new DocumentStoreException(DocumentStoreError.Conflict);

            var current = ReadPlatform(db, target) ?? new StoredAccount();
            if (current.Generation != expected.Generation || current.ValueFingerprint != expected.ValueFingerprint)
                throw new DocumentStoreException(DocumentStoreError.Conflict);
            AccountRules.Require(next.Generation == current.Generation + 1 && next.Generation <= AccountRules.MaxInteger, "LIMIT");

            var committed = WithFingerprint(target, next);
            db.Execute("""
                INSERT INTO windows_music_accounts(
                    platform,generation,cookie,scope,validated_at_ms,
                    profile_platform,profile_user_id,profile_display_name,profile_avatar)
                VALUES($plat,$gen,$cookie,$scope,$valAt,$pPlat,$pUid,$pName,$pAvatar)
                ON CONFLICT(platform) DO UPDATE SET
                    generation=excluded.generation,
                    cookie=excluded.cookie,
                    scope=excluded.scope,
                    validated_at_ms=excluded.validated_at_ms,
                    profile_platform=excluded.profile_platform,
                    profile_user_id=excluded.profile_user_id,
                    profile_display_name=excluded.profile_display_name,
                    profile_avatar=excluded.profile_avatar
                """,
                ("$plat", target),
                ("$gen", committed.Generation),
                ("$cookie", committed.Cookie),
                ("$scope", committed.Scope),
                ("$valAt", committed.ValidatedAt),
                ("$pPlat", committed.Profile?.Platform),
                ("$pUid", committed.Profile?.UserId),
                ("$pName", committed.Profile?.DisplayName),
                ("$pAvatar", committed.Profile?.Avatar));

            var actual = db.CommitRevision(MusicAccountOwner.DocumentNamespace, expectedRevision);
            return (committed, actual);
        }, token, (next.Cookie?.Length ?? 0) * 2 + 256);
    }

    private static StoredAccount? ReadPlatform(DatabaseSession db, string platform)
    {
        if (!db.TryQuerySingle("""
            SELECT generation,cookie,scope,validated_at_ms,
                   profile_platform,profile_user_id,profile_display_name,profile_avatar
            FROM windows_music_accounts
            WHERE platform=$plat
            """,
            row =>
            {
                long generation = row.GetInt64(0);
                string? cookie = row.GetNullableString(1);
                string? scope = row.GetNullableString(2);
                long? validatedAt = row.GetNullableInt64(3);
                string? pPlatform = row.GetNullableString(4);
                string? pUserId = row.GetNullableString(5);
                string? pDisplayName = row.GetNullableString(6);
                string? pAvatar = row.GetNullableString(7);
                AccountRules.Require(generation is > 0 and <= AccountRules.MaxInteger, "CORRUPT_STORAGE");
                MusicAccountProfile? profile = null;
                if (cookie is null)
                {
                    AccountRules.Require(scope is null && validatedAt is null &&
                        pPlatform is null && pUserId is null && pDisplayName is null && pAvatar is null,
                        "CORRUPT_STORAGE");
                }
                else
                {
                    AccountRules.Require(cookie.Length > 0 && scope is not null &&
                        scope.Length > 0 && scope.Length <= 128 && scope.Trim() == scope &&
                        validatedAt is >= 0 and <= AccountRules.MaxInteger &&
                        pPlatform == platform && !string.IsNullOrEmpty(pUserId) && pDisplayName is not null,
                        "CORRUPT_STORAGE");
                    profile = new MusicAccountProfile(pPlatform!, pUserId!, pDisplayName!, pAvatar);
                }
                return WithFingerprint(platform, new StoredAccount
                {
                    Generation = generation,
                    Cookie = cookie,
                    Profile = profile,
                    Scope = scope,
                    ValidatedAt = validatedAt
                });
            },
            out var account,
            ("$plat", platform)))
            return null;
        return account;
    }

    internal static StoredAccount WithFingerprint(string platform, StoredAccount account)
    {
        var sb = new StringBuilder(256);
        sb.Append("v1|").Append(platform).Append('|').Append(account.Generation.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, account.Cookie);
        AppendField(sb, account.Scope);
        AppendField(sb, account.ValidatedAt?.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, account.Profile?.Platform);
        AppendField(sb, account.Profile?.UserId);
        AppendField(sb, account.Profile?.DisplayName);
        AppendField(sb, account.Profile?.Avatar);
        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        string fingerprint;
        try { fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return new StoredAccount
        {
            Generation = account.Generation,
            Cookie = account.Cookie,
            Profile = account.Profile,
            Scope = account.Scope,
            ValidatedAt = account.ValidatedAt,
            ValueFingerprint = fingerprint
        };
    }

    private static void AppendField(StringBuilder sb, string? value)
    {
        if (value is null) sb.Append("|-");
        else sb.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }
}
