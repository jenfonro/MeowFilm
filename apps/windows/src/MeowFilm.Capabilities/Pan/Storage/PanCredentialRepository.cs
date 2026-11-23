using MeowFilm.Core.Runtime;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities;

internal sealed record PanAccountRecord(
    string Provider,
    long AccountRevision,
    long RowRevision,
    string? Cookie,
    string? Username,
    string? Password,
    string? Authorization,
    long? RefreshedAtMs);

internal readonly record struct PanCredentialsInput(
    string? Cookie,
    string? Username,
    string? Password,
    string? Authorization,
    long? RefreshedAtMs);

/// <summary>Five plaintext provider rows. Refreshes and replacements have separate durable revisions.</summary>
internal sealed class PanCredentialRepository(IStorageDatabase database)
{
    internal const string Owner = "pan.accounts";
    private const long Maximum = BridgeCodec.MaximumSafeInteger;

    private static PanAccountRecord ReadRow(Microsoft.Data.Sqlite.SqliteDataReader row) => new(
        row.GetString(0),
        row.GetInt64(1),
        row.GetInt64(2),
        row.GetNullableString(3),
        row.GetNullableString(4),
        row.GetNullableString(5),
        row.GetNullableString(6),
        row.GetNullableInt64(7));

    private static (IReadOnlyList<PanAccountRecord> Accounts, long Revision) ReadAll(DatabaseSession db)
    {
        long revision = db.Revision(Owner).Revision;
        var rows = db.Query("""
            SELECT provider,account_revision,row_revision,cookie,username,password,authorization,refreshed_at_ms
            FROM pan_accounts
            WHERE account_revision>0
            ORDER BY provider
            """, ReadRow);
        return (rows, revision);
    }

    internal Task<(IReadOnlyList<PanAccountRecord> Accounts, long Revision)> ReadAllAsync(CancellationToken token) =>
        database.ReadSnapshotAsync(ReadAll, token);

    internal Task<PanAccountRecord> ReadOneAsync(string provider, CancellationToken token) => database.ReadAsync(db =>
    {
        ValidateProvider(provider);
        return db.Query("""
            SELECT provider,account_revision,row_revision,cookie,username,password,authorization,refreshed_at_ms
            FROM pan_accounts
            WHERE provider=$provider
            """, ReadRow, ("$provider", provider)).Single();
    }, token);

    internal Task<(IReadOnlyList<PanAccountRecord> Accounts, long Revision)> SaveAsync(
        string provider,
        PanCredentialsInput credentials,
        long accountRevision,
        long rowRevision,
        bool refresh,
        int estimatedBytes,
        CancellationToken token) => database.WriteAsync(db =>
    {
        ValidateProvider(provider);
        Save(db, provider, credentials, accountRevision, rowRevision, refresh);
        return ReadAll(db);
    }, token, estimatedBytes);

    private static void Save(DatabaseSession db, string provider, PanCredentialsInput credentials,
        long accountRevision, long rowRevision, bool refresh)
    {
        if (accountRevision is < 0 or > Maximum || rowRevision is < 0 or > Maximum || refresh && accountRevision == 0)
            throw new BridgeException("INVALID_REQUEST");
        if (!refresh && accountRevision == Maximum || rowRevision == Maximum)
            throw new StorageException(StorageError.Limit);
        if (db.Execute("""
            UPDATE pan_accounts
            SET account_revision=$next, row_revision=row_revision+1,
                cookie=$cookie, username=$username, password=$password,
                authorization=$authorization, refreshed_at_ms=$refreshedAt
            WHERE provider=$provider AND account_revision=$account AND row_revision=$row
            """,
            ("$provider", provider),
            ("$account", accountRevision),
            ("$row", rowRevision),
            ("$next", accountRevision + (refresh ? 0 : 1)),
            ("$cookie", credentials.Cookie),
            ("$username", credentials.Username),
            ("$password", credentials.Password),
            ("$authorization", credentials.Authorization),
            ("$refreshedAt", credentials.RefreshedAtMs)) != 1)
            throw new StorageException(StorageError.Conflict);
        var current = db.Revision(Owner);
        if (current.Revision >= Maximum) throw new StorageException(StorageError.Limit);
        db.CommitRevision(Owner, current);
    }

    internal static void ValidateProvider(string provider)
    {
        if (provider is not ("baidu" or "quark" or "uc" or "tianyi" or "yidong"))
            throw new BridgeException("INVALID_REQUEST");
    }
}
