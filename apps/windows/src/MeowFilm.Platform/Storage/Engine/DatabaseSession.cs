using Microsoft.Data.Sqlite;

namespace MeowFilm.Platform.Storage;

/// <summary>A single short database snapshot/transaction, never exposed to scripts.</summary>
public sealed class DatabaseSession
{
    internal SqliteConnection Connection { get; }
    private readonly SqliteTransaction? transaction;
    internal PreparedStatementCache? StatementCache { get; }

    internal DatabaseSession(SqliteConnection connection, SqliteTransaction? transaction = null,
        PreparedStatementCache? statementCache = null)
    {
        Connection = connection;
        this.transaction = transaction;
        StatementCache = statementCache;
    }

    public int ExecuteScript(string sql)
    {
        if (sql.Contains('\0')) throw new StorageException(StorageError.InvalidRequest);
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var lease = Rent(sql, parameters);
        try { return lease.Command.ExecuteNonQuery(); }
        catch (Exception ex) { if (ShouldEvictStatement(ex)) lease.MarkFaulted(); throw; }
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var lease = Rent(sql, parameters);
        try
        {
            var value = lease.Command.ExecuteScalar();
            return value is null or DBNull
                ? default
                : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { if (ShouldEvictStatement(ex)) lease.MarkFaulted(); throw; }
    }

    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var lease = Rent(sql, parameters);
        try
        {
            using var reader = lease.Command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read())
                result.Add(read(reader));
            return result;
        }
        catch (Exception ex) { if (ShouldEvictStatement(ex)) lease.MarkFaulted(); throw; }
    }

    public bool TryQuerySingle<T>(string sql, Func<SqliteDataReader, T> read, out T value,
        params (string Name, object? Value)[] parameters)
    {
        using var lease = Rent(sql, parameters);
        try
        {
            using var reader = lease.Command.ExecuteReader();
            if (!reader.Read())
            {
                value = default!;
                return false;
            }
            value = read(reader);
            if (reader.Read()) throw new StorageException(StorageError.Corrupt);
            return true;
        }
        catch (Exception ex) { if (ShouldEvictStatement(ex)) lease.MarkFaulted(); throw; }
    }

    /// <summary>
    /// Ordinary constraint violations, transient busy/locked errors, and caller-side row-mapping exceptions
    /// leave the compiled statement valid in the LRU cache; only connection/IO/corrupt/schema SQLite errors evict it.
    /// </summary>
    private static bool ShouldEvictStatement(Exception ex) =>
        ex is SqliteException sqlite && sqlite.SqliteErrorCode is not (19 or 5 or 6);

    public StorageRevision Revision(string owner) =>
        TryQuerySingle(
            "SELECT revision FROM owner_revisions WHERE owner=$owner",
            row => new StorageRevision(row.GetInt64(0)),
            out var revision,
            ("$owner", owner))
            ? revision
            : StorageRevision.Empty;

    public StorageRevision CommitRevision(string owner, StorageRevision expected)
    {
        if (expected.Revision < 0)
            throw new StorageException(StorageError.InvalidRequest);
        if (Revision(owner) != expected) throw new StorageException(StorageError.Conflict);
        var next = new StorageRevision(expected.Revision + 1);
        Execute("""
            INSERT INTO owner_revisions(owner,revision) VALUES($owner,$revision)
            ON CONFLICT(owner) DO UPDATE SET revision=excluded.revision
            """, ("$owner", owner), ("$revision", next.Revision));
        return next;
    }

    private PreparedStatementCache.Lease Rent(string sql, (string Name, object? Value)[] parameters)
    {
        if (StatementCache is not null)
            return StatementCache.Rent(sql, transaction, parameters);
        if (sql.Contains('\0')) throw new StorageException(StorageError.InvalidRequest);
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is bool flag ? flag ? 1L : 0L : value ?? DBNull.Value);
        return new PreparedStatementCache.Lease(null, null, command);
    }
}

public static class DatabaseReaderExtensions
{
    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? GetNullableInt32(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static double? GetNullableDouble(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    public static bool GetBool(this SqliteDataReader reader, int ordinal) =>
        reader.GetInt64(ordinal) != 0;

    public static bool? GetNullableBool(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
}
