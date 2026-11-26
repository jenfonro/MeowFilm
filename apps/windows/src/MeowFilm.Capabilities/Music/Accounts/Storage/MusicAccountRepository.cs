// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

internal sealed class StoredAccount
{
    internal long Generation { get; init; }
    internal string? Cookie { get; init; }
    internal MusicAccountProfile? Profile { get; init; }
    internal string? Scope { get; init; }
    internal long? ValidatedAt { get; init; }
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
        var revision = db.Revision(MusicAccountOwner.StorageOwner);
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
            if (db.Revision(MusicAccountOwner.StorageOwner) != expectedRevision)
                throw new StorageException(StorageError.Conflict);

            var current = ReadPlatform(db, target) ?? new StoredAccount();
            if (current.Generation != expected.Generation ||
                current.Scope != expected.Scope ||
                current.Profile?.UserId != expected.Profile?.UserId)
                throw new StorageException(StorageError.Conflict);
            AccountRules.Require(next.Generation == current.Generation + 1 && next.Generation <= AccountRules.MaxInteger, "LIMIT");

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
                ("$gen", next.Generation),
                ("$cookie", next.Cookie),
                ("$scope", next.Scope),
                ("$valAt", next.ValidatedAt),
                ("$pPlat", next.Profile?.Platform),
                ("$pUid", next.Profile?.UserId),
                ("$pName", next.Profile?.DisplayName),
                ("$pAvatar", next.Profile?.Avatar));

            var actual = db.CommitRevision(MusicAccountOwner.StorageOwner, expectedRevision);
            return (next, actual);
        }, token);
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
                var profile = cookie is not null && pPlatform is not null && pUserId is not null && pDisplayName is not null
                    ? new MusicAccountProfile(pPlatform, pUserId, pDisplayName, pAvatar)
                    : null;
                return new StoredAccount
                {
                    Generation = generation,
                    Cookie = cookie,
                    Profile = profile,
                    Scope = scope,
                    ValidatedAt = validatedAt
                };
            },
            out var account,
            ("$plat", platform)))
            return null;
        return account;
    }
}
