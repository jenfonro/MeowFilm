using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using MeowFilm.Core.Debug;

namespace MeowFilm.Platform.Storage;

public sealed record StorageRevision(long Revision, string Receipt)
{
    public static StorageRevision Empty { get; } = new(0, "");
}

/// <summary>Raised only after SQLite transaction.Commit() was invoked and failed, leaving commit durability uncertain.</summary>
public sealed class TransactionCommitUnknownException : Exception
{
    public DocumentStoreError Error { get; }
    public TransactionCommitUnknownException(DocumentStoreError error) : base(error.ToString()) => Error = error;
}

/// <summary>One bounded native I/O lane. Repositories own SQL and business policy.</summary>
public sealed class NativeDatabase : IStorageDatabase, IDebugStorageQueryHost
{
    public const int ApplicationId = 1296450626;
    public const int FormatVersion = 1;
    public const string RelativePath = "storage-v1/meowfilm.sqlite3";
    private const int MaximumPendingOperations = 128;
    private const int MaximumPendingBytes = 16 * 1024 * 1024;
    private readonly Channel<Operation> operations = Channel.CreateBounded<Operation>(new BoundedChannelOptions(MaximumPendingOperations)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    private readonly object admission = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string productRoot;
    private SqliteConnection? connection;
    private DatabaseDirectory? directory;
    private long pendingBytes;
    private int activeOperations;
    private TaskCompletionSource drained = CompletedDrain();
    private int commits;
    private bool closing;
    private Exception? openError;
    public Task Ready => ready.Task;
    public string ProductRoot => productRoot;
    public string DatabasePath => Path.Combine(productRoot, "storage-v1", "meowfilm.sqlite3");
    public string SQLiteVersion { get; private set; } = "";
    public string SQLiteSourceId { get; private set; } = "";
    public IReadOnlyList<string> SQLiteCompileOptions { get; private set; } = [];

    public NativeDatabase(string productRoot)
    {
        if (string.IsNullOrEmpty(productRoot)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        this.productRoot = productRoot;
        new Thread(Run) { IsBackground = true, Name = "meowfilm native database" }.Start();
    }

    public Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default) =>
        Enqueue(session =>
        {
            token.ThrowIfCancellationRequested();
            using var transaction = session.Connection.BeginTransaction(deferred: true);
            var result = read(new(session.Connection, transaction));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }, token, 0);

    /// <summary>Cancellation applies to admission, not to an accepted transaction or its receipt.</summary>
    public Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default, int retainedBytes = 0, Action<Action>? commit = null) =>
        Enqueue(session =>
        {
            var started = DebugLog.IsEnabled ? Stopwatch.GetTimestamp() : 0L;
            using var transaction = session.Connection.BeginTransaction();
            bool commitAttempted = false;
            bool committed = false;
            try
            {
                var result = write(new(session.Connection, transaction));
                void Commit()
                {
                    commitAttempted = true;
                    transaction.Commit();
                    committed = true;
                }
                if (commit is null) Commit(); else commit(Commit);
                if (!committed) throw new DocumentStoreException(DocumentStoreError.Conflict);
                commits++;
                if (started != 0)
                {
                    DebugLog.Trace(DebugCategories.Storage, "sqlite", "tx.commit",
                        new { commitCount = commits, retainedBytes },
                        durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                return result;
            }
            catch (Exception ex)
            {
                if (started != 0)
                {
                    DebugLog.Warn(DebugCategories.Storage, "sqlite", "tx.error",
                        new { retainedBytes, commitAttempted, committed }, error: ex,
                        durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                if (commitAttempted && !committed)
                {
                    var translated = Translate(ex);
                    var error = translated is DocumentStoreException storeError
                        ? storeError.Error
                        : DocumentStoreError.Storage;
                    throw new TransactionCommitUnknownException(error);
                }
                throw;
            }
        }, token, retainedBytes);

    public async Task<object> ExecuteReadOnlyQueryAsync(string sql, JsonElement? parameters, int maxRows, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Contains('\0'))
            throw DebugApiException.InvalidArgument("SQL query must be a non-empty string.");
        ValidateReadOnlySql(sql);
        maxRows = Math.Clamp(maxRows, 1, 5000);
        var started = Stopwatch.GetTimestamp();
        var result = await Enqueue(session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var on = session.Connection.CreateCommand())
            {
                on.CommandText = "PRAGMA query_only = ON";
                on.ExecuteNonQuery();
            }
            try
            {
                using var cmd = session.Connection.CreateCommand();
                cmd.CommandText = sql;
                BindDebugParameters(cmd, parameters);
                VerifyPreparedStatementReadOnly(session.Connection, cmd.CommandText);
                using var reader = cmd.ExecuteReader();
                var columns = new string[reader.FieldCount];
                var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < columns.Length; i++)
                {
                    var rawName = reader.GetName(i);
                    if (seenNames.TryGetValue(rawName, out var count))
                    {
                        seenNames[rawName] = count + 1;
                        columns[i] = $"{rawName}_{count + 1}";
                    }
                    else
                    {
                        seenNames[rawName] = 1;
                        columns[i] = rawName;
                    }
                }
                var rows = new List<Dictionary<string, object?>>();
                bool truncated = false;
                while (reader.Read())
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }
                    var row = new Dictionary<string, object?>(columns.Length, StringComparer.Ordinal);
                    for (int i = 0; i < columns.Length; i++)
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        if (value is byte[] blob) value = Convert.ToBase64String(blob);
                        row[columns[i]] = value;
                    }
                    rows.Add(row);
                }
                return (columns, rows, truncated);
            }
            catch (SqliteException ex)
            {
                throw DebugApiException.InvalidArgument($"Read-only SQL query failed: {ex.Message}");
            }
            finally
            {
                using var off = session.Connection.CreateCommand();
                off.CommandText = "PRAGMA query_only = OFF";
                off.ExecuteNonQuery();
            }
        }, cancellationToken, 0).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        DebugLog.Info(DebugCategories.Storage, "debug-sql", "query.readonly",
            new { sql, rowCount = result.rows.Count, truncated = result.truncated },
            durationMs: elapsedMs);
        return new
        {
            columns = result.columns,
            rows = result.rows,
            rowCount = result.rows.Count,
            truncated = result.truncated,
            durationMs = Math.Round(elapsedMs, 3)
        };
    }

    private static void ValidateReadOnlySql(string sql)
    {
        bool inSingle = false, inDouble = false, inLineComment = false, inBlockComment = false;
        int len = sql.Length;
        for (int i = 0; i < len; i++)
        {
            char c = sql[i];
            char next = i + 1 < len ? sql[i + 1] : '\0';
            if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
            if (inSingle) { if (c == '\'' && next == '\'') { i++; continue; } if (c == '\'') inSingle = false; continue; }
            if (inDouble) { if (c == '"' && next == '"') { i++; continue; } if (c == '"') inDouble = false; continue; }
            if (c == '-' && next == '-') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }
            if (c == ';')
            {
                var rest = StripLeadingSqlComments(sql[(i + 1)..]).Trim();
                if (rest.Length > 0)
                    throw DebugApiException.InvalidArgument("Only a single read-only SQL statement is allowed.");
            }
        }
        var stripped = StripLeadingSqlComments(sql).TrimStart();
        int wordEnd = 0;
        while (wordEnd < stripped.Length && char.IsAsciiLetter(stripped[wordEnd])) wordEnd++;
        var keyword = stripped[..wordEnd].ToUpperInvariant();
        if (keyword is not ("SELECT" or "PRAGMA" or "EXPLAIN" or "WITH"))
            throw DebugApiException.InvalidArgument($"Only SELECT/WITH/PRAGMA/EXPLAIN queries are allowed (got '{keyword}').");
        if (keyword == "PRAGMA")
        {
            ValidateReadOnlyPragma(stripped[wordEnd..]);
        }
    }

    private static readonly HashSet<string> ForbiddenMutatingPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "optimize", "shrink_memory", "wal_checkpoint", "incremental_vacuum", "analysis_limit"
    };

    private static readonly HashSet<string> AllowedFunctionPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "table_info", "table_xinfo", "index_list", "index_info", "index_xinfo",
        "foreign_key_list", "foreign_key_check", "table_list", "integrity_check", "quick_check"
    };

    private static void ValidateReadOnlyPragma(string remainder)
    {
        var cursor = StripLeadingSqlComments(remainder);
        if (cursor.Length == 0)
            throw DebugApiException.InvalidArgument("PRAGMA statement is missing a pragma name.");

        int firstEnd = 0;
        while (firstEnd < cursor.Length && (char.IsAsciiLetterOrDigit(cursor[firstEnd]) || cursor[firstEnd] == '_'))
            firstEnd++;
        if (firstEnd == 0)
            throw DebugApiException.InvalidArgument("Invalid PRAGMA identifier.");
        var firstIdent = cursor[..firstEnd];
        cursor = StripLeadingSqlComments(cursor[firstEnd..]);

        string pragmaName;
        if (cursor.Length > 0 && cursor[0] == '.')
        {
            cursor = StripLeadingSqlComments(cursor[1..]);
            int secondEnd = 0;
            while (secondEnd < cursor.Length && (char.IsAsciiLetterOrDigit(cursor[secondEnd]) || cursor[secondEnd] == '_'))
                secondEnd++;
            if (secondEnd == 0)
                throw DebugApiException.InvalidArgument("Invalid schema-qualified PRAGMA identifier.");
            pragmaName = cursor[..secondEnd];
            cursor = StripLeadingSqlComments(cursor[secondEnd..]);
        }
        else
        {
            pragmaName = firstIdent;
        }

        if (ForbiddenMutatingPragmas.Contains(pragmaName))
            throw DebugApiException.InvalidArgument($"Mutating PRAGMA '{pragmaName}' is not allowed.");

        if (cursor.Length == 0 || (cursor[0] == ';' && StripLeadingSqlComments(cursor[1..]).Length == 0))
            return;

        if (cursor[0] == '(')
        {
            if (!AllowedFunctionPragmas.Contains(pragmaName))
                throw DebugApiException.InvalidArgument($"Parameterized PRAGMA '{pragmaName}(...)' is not allowed.");
            int depth = 0;
            bool inSingle = false, inDouble = false, inLineComment = false, inBlockComment = false;
            int closeIdx = -1;
            for (int i = 0; i < cursor.Length; i++)
            {
                char c = cursor[i];
                char next = i + 1 < cursor.Length ? cursor[i + 1] : '\0';
                if (inLineComment) { if (c == '\n') inLineComment = false; continue; }
                if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; i++; } continue; }
                if (inSingle) { if (c == '\'' && next == '\'') { i++; continue; } if (c == '\'') inSingle = false; continue; }
                if (inDouble) { if (c == '"' && next == '"') { i++; continue; } if (c == '"') inDouble = false; continue; }
                if (c == '-' && next == '-') { inLineComment = true; i++; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
                if (c == '\'') { inSingle = true; continue; }
                if (c == '"') { inDouble = true; continue; }
                if (c == '=') throw DebugApiException.InvalidArgument("Write or mutating PRAGMA statements are not allowed.");
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) { closeIdx = i; break; }
                }
            }
            if (closeIdx < 0)
                throw DebugApiException.InvalidArgument("Malformed parenthesized PRAGMA statement.");
            var trailing = StripLeadingSqlComments(cursor[(closeIdx + 1)..]);
            if (trailing.Length == 0 || (trailing[0] == ';' && StripLeadingSqlComments(trailing[1..]).Length == 0))
                return;
            throw DebugApiException.InvalidArgument("Unexpected trailing tokens in PRAGMA statement.");
        }

        throw DebugApiException.InvalidArgument("Write or mutating PRAGMA statements are not allowed.");
    }

    private static void VerifyPreparedStatementReadOnly(SqliteConnection conn, string sql)
    {
        int rc = SQLitePCL.raw.sqlite3_prepare_v2(conn.Handle, sql, out var stmt, out string tail);
        if (rc != SQLitePCL.raw.SQLITE_OK)
        {
            stmt?.Dispose();
            var err = SQLitePCL.raw.sqlite3_errmsg(conn.Handle).utf8_to_string();
            throw DebugApiException.InvalidArgument($"Read-only SQL query failed: {err}");
        }
        using (stmt)
        {
            var remaining = StripLeadingSqlComments(tail ?? "");
            if (remaining.Length > 0 && !(remaining[0] == ';' && StripLeadingSqlComments(remaining[1..]).Length == 0))
                throw DebugApiException.InvalidArgument("Only a single read-only SQL statement is allowed.");
            if (stmt is null || SQLitePCL.raw.sqlite3_stmt_readonly(stmt) == 0)
                throw DebugApiException.InvalidArgument("SQLite rejected statement as non-readonly.");
        }
    }

    private static string StripLeadingSqlComments(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n') i++;
            }
            else if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(sql.Length, i + 2);
            }
            else break;
        }
        return sql[i..];
    }

    private static void BindDebugParameters(SqliteCommand cmd, JsonElement? parameters)
    {
        if (parameters is not { } p) return;
        if (p.ValueKind == JsonValueKind.Array)
        {
            var sqlText = cmd.CommandText;
            string? numberedPrefix = sqlText.Contains("$1", StringComparison.Ordinal) ? "$"
                : sqlText.Contains("?1", StringComparison.Ordinal) ? "?"
                : sqlText.Contains("@1", StringComparison.Ordinal) ? "@"
                : sqlText.Contains(":1", StringComparison.Ordinal) ? ":"
                : null;
            var values = p.EnumerateArray().Select(JsonToDbValue).ToArray();
            if (numberedPrefix is not null)
            {
                for (int i = 0; i < values.Length; i++)
                    cmd.Parameters.AddWithValue($"{numberedPrefix}{i + 1}", values[i]);
            }
            else
            {
                cmd.CommandText = RewritePositionalPlaceholders(sqlText, values.Length);
                for (int i = 0; i < values.Length; i++)
                    cmd.Parameters.AddWithValue($"$__p{i + 1}", values[i]);
            }
        }
        else if (p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject())
            {
                var name = prop.Name.StartsWith('$') || prop.Name.StartsWith('@') || prop.Name.StartsWith(':')
                    ? prop.Name : "$" + prop.Name;
                cmd.Parameters.AddWithValue(name, JsonToDbValue(prop.Value));
            }
        }
    }

    private static string RewritePositionalPlaceholders(string sql, int count)
    {
        if (count <= 0 || !sql.Contains('?')) return sql;
        var sb = new StringBuilder(sql.Length + count * 4);
        bool inSingle = false, inDouble = false, inLineComment = false, inBlockComment = false;
        int nextIndex = 1;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (inLineComment) { sb.Append(c); if (c == '\n') inLineComment = false; continue; }
            if (inBlockComment) { sb.Append(c); if (c == '*' && next == '/') { sb.Append(next); inBlockComment = false; i++; } continue; }
            if (inSingle) { sb.Append(c); if (c == '\'' && next == '\'') { sb.Append(next); i++; continue; } if (c == '\'') inSingle = false; continue; }
            if (inDouble) { sb.Append(c); if (c == '"' && next == '"') { sb.Append(next); i++; continue; } if (c == '"') inDouble = false; continue; }
            if (c == '-' && next == '-') { inLineComment = true; sb.Append(c); sb.Append(next); i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; sb.Append(c); sb.Append(next); i++; continue; }
            if (c == '\'') { inSingle = true; sb.Append(c); continue; }
            if (c == '"') { inDouble = true; sb.Append(c); continue; }
            if (c == '?' && !char.IsAsciiDigit(next) && nextIndex <= count)
            {
                sb.Append("$__p").Append(nextIndex++);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static object JsonToDbValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Number when value.TryGetInt64(out var i) => i,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.GetRawText()
    };

    public Task<StorageRevision> RevisionAsync(string owner, CancellationToken token = default) =>
        ReadAsync(session => session.Revision(owner), token);

    // Used only by the music owner's off-UI command lane for identity lookups.
    public T Read<T>(Func<DatabaseSession, T> read)
    {
        if (SynchronizationContext.Current is not null) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        return ReadAsync(read).GetAwaiter().GetResult();
    }
    public Task DrainAsync()
    {
        lock (admission) return closing ? stopped.Task : drained.Task;
    }

    private static TaskCompletionSource CompletedDrain()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private abstract class Operation(int bytes)
    {
        internal int Bytes { get; } = bytes;
        internal abstract void Execute(DatabaseSession session);
        internal abstract void Fail(Exception error);
    }
    private sealed class Operation<T>(Func<DatabaseSession, T> action, int bytes) : Operation(bytes)
    {
        internal TaskCompletionSource<T> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override void Execute(DatabaseSession session)
        {
            try { Result.TrySetResult(action(session)); }
            catch (OperationCanceledException error) { Result.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { Fail(error); }
        }
        internal override void Fail(Exception error) => Result.TrySetException(Translate(error));
    }
    private Task<T> Enqueue<T>(Func<DatabaseSession, T> action, CancellationToken token, int bytes)
    {
        token.ThrowIfCancellationRequested();
        if (bytes < 0) throw new DocumentStoreException(DocumentStoreError.Limit);
        var operation = new Operation<T>(action, bytes);
        lock (admission)
        {
            token.ThrowIfCancellationRequested();
            if (openError is not null) return Task.FromException<T>(openError);
            if (closing) throw new DocumentStoreException(DocumentStoreError.Closed);
            if (pendingBytes != 0 && pendingBytes > MaximumPendingBytes - (long)bytes) throw new DocumentStoreException(DocumentStoreError.Limit);
            pendingBytes += bytes;
            if (!operations.Writer.TryWrite(operation))
            {
                pendingBytes -= bytes;
                throw new DocumentStoreException(DocumentStoreError.Limit);
            }
            if (activeOperations++ == 0)
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return operation.Result.Task;
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            Open();
            ready.TrySetResult();
            while (operations.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (operations.Reader.TryRead(out var operation))
                {
                    try
                    {
                        try
                        {
                            directory?.Check();
                            using var lease = EnterMaintenance(exclusive: false, wait: true, skipInitialRootCheck: true);
                            operation.Execute(new(connection!));
                        }
                        catch (Exception error) { operation.Fail(error); }
                    }
                    finally
                    {
                        lock (admission)
                        {
                            pendingBytes -= operation.Bytes;
                            if (--activeOperations == 0) drained.TrySetResult();
                        }
                    }
                    if (commits >= 64 && !closing)
                    {
                        commits = 0;
                        Maintain();
                    }
                }
        }
        catch (Exception error)
        {
            failure = Translate(error);
            lock (admission)
            {
                openError = failure;
                closing = true;
                operations.Writer.TryComplete();
            }
            ready.TrySetException(failure);
        }
        finally
        {
            // A failed open/maintenance must settle every accepted operation.
            // Successful COMMIT receipts already delivered are never revoked.
            while (operations.Reader.TryRead(out var operation))
            {
                operation.Fail(failure ?? new DocumentStoreException(DocumentStoreError.Closed));
                lock (admission)
                {
                    pendingBytes -= operation.Bytes;
                    if (--activeOperations == 0) drained.TrySetResult();
                }
            }
            Exception? releaseFailure = null;
            try
            {
                // COMMIT is durable in the WAL. Closing is not a maintenance operation.
                connection?.Dispose();
            }
            catch (Exception error)
            {
                releaseFailure = Translate(error);
            }
            connection = null;
            try { directory?.Dispose(); } catch (Exception error) { releaseFailure ??= Translate(error); }
            directory = null;
            if (releaseFailure is null) stopped.TrySetResult();
            else stopped.TrySetException(releaseFailure);
        }
    }

    private void Open()
    {
        var schema = ReadSchema();
        directory = new DatabaseDirectory(Path.Combine(productRoot, "storage-v1"), "meowfilm.sqlite3");
        using var initialize = EnterLock("initialize.lock", exclusive: true, wait: true);
        directory.OpenDatabase();
        using var validation = EnterMaintenance(exclusive: false, wait: true);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        if (SQLitePCL.raw.sqlite3_db_config(connection.Handle, 1006 /* SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE */, 1, out _) != 0)
            throw new DocumentStoreException(DocumentStoreError.Storage);
        var session = new DatabaseSession(connection);
        SQLiteVersion = session.Scalar<string>("SELECT sqlite_version()")!;
        SQLiteSourceId = session.Scalar<string>("SELECT sqlite_source_id()")!;
        SQLiteCompileOptions = session.Query("PRAGMA compile_options", row => row.GetString(0));
        if (!SafeWalVersion(SQLiteVersion))
            throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        session.Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; PRAGMA temp_store=MEMORY");
        long identity = session.Scalar<long>("PRAGMA application_id");
        long version = session.Scalar<long>("PRAGMA user_version");
        long tableCount = session.Scalar<long>("SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'");
        if (identity == 0 && version == 0 && tableCount == 0)
        {
            if (!directory.Created) throw new DocumentStoreException(DocumentStoreError.Corrupt);
            // The current namespace starts empty. No probes, upgrades or conversions.
            session.Execute("PRAGMA auto_vacuum=INCREMENTAL");
            using var transaction = connection.BeginTransaction();
            var creating = new DatabaseSession(connection, transaction);
            creating.Execute(schema.Sql);
            creating.Execute("INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,$hash)", ("$hash", schema.Hash));
            creating.Execute($"PRAGMA application_id={ApplicationId}; PRAGMA user_version={FormatVersion}");
            transaction.Commit();
        }
        else if (identity != ApplicationId || version != FormatVersion)
            throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        VerifySchema(session, schema);
        string mode = session.Scalar<string>("PRAGMA journal_mode=WAL")!;
        if (mode != "wal" ||
            session.Scalar<long>("PRAGMA synchronous") != 2 ||
            session.Scalar<long>("PRAGMA foreign_keys") != 1 ||
            session.Scalar<long>("PRAGMA trusted_schema") != 0 ||
            session.Scalar<long>("PRAGMA wal_autocheckpoint") != 0 ||
            session.Scalar<long>("PRAGMA auto_vacuum") != 2)
            throw new DocumentStoreException(DocumentStoreError.Storage);
        // Attach this long-lived connection to the WAL before another process
        // can become its only shared-memory participant and exit.
        _ = session.Scalar<long>("SELECT count(*) FROM storage_identity");
        directory.Check();
    }

    private static (string Sql, string Hash) ReadSchema()
    {
        var assembly = typeof(NativeDatabase).Assembly;
        using var stream = assembly.GetManifestResourceStream("MeowFilm.Storage.schema.sql")
            ?? throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        return (new UTF8Encoding(false, true).GetString(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    private static void VerifySchema(DatabaseSession actual, (string Sql, string Hash) schema)
    {
        if (actual.Scalar<string>("SELECT schema_hash FROM storage_identity WHERE singleton=1") != schema.Hash)
            throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        using var expectedConnection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        expectedConnection.Open();
        var expected = new DatabaseSession(expectedConnection);
        expected.Execute(schema.Sql);
        static IReadOnlyList<string> Structure(DatabaseSession session) => session.Query(
            "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name",
            row => string.Join("\n", row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? "" : row.GetString(3)));
        if (!Structure(actual).SequenceEqual(Structure(expected), StringComparer.Ordinal))
            throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
    }
    private static bool SafeWalVersion(string text) => Version.TryParse(text, out var version) &&
        (version >= new Version(3, 51, 3) ||
            version.Major == 3 && version.Minor == 50 && version.Build >= 7 ||
            version.Major == 3 && version.Minor == 44 && version.Build >= 6);

    private SafeFileHandle? EnterMaintenance(bool exclusive, bool wait, bool skipInitialRootCheck = false) =>
        EnterLock("maintenance.lock", exclusive, wait, skipInitialRootCheck);
    private SafeFileHandle? EnterLock(string name, bool exclusive, bool wait, bool skipInitialRootCheck = false)
    {
        if (directory is null) return null;
        var lockPath = Path.Combine(directory.DirectoryPath, name);
        var limit = Environment.TickCount64 + (wait ? 5000 : 0);
        bool checkRoot = !skipInitialRootCheck;
        do
        {
            if (checkRoot) directory.CheckRoot();
            checkRoot = true;
            var handle = WindowsDocumentFiles.OpenFile(lockPath,
                exclusive ? WindowsDocumentFiles.Read | WindowsDocumentFiles.Write : WindowsDocumentFiles.Read,
                exclusive ? 0u : 1u, WindowsDocumentFiles.OpenAlways, sharingIsBusy: true);
            if (handle is not null || !wait) return handle;
            Thread.Sleep(20);
        } while (Environment.TickCount64 < limit);
        throw new DocumentStoreException(DocumentStoreError.Busy);
    }
    private void Maintain()
    {
        if (directory is null) return;
        try
        {
            using var lease = EnterMaintenance(exclusive: true, wait: false);
            if (lease is null) return;
            MaintainLocked();
        }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Busy) { }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
    }
    private void MaintainLocked()
    {
        if (directory is null || connection is null) return;
        var session = new DatabaseSession(connection);
        // PASSIVE never waits for another App/runtime reader or truncates its files.
        session.Execute("PRAGMA wal_checkpoint(PASSIVE)");
        if (session.Scalar<long>("PRAGMA freelist_count") >= 256) session.Execute("PRAGMA incremental_vacuum(128)");
    }
    private static Exception Translate(Exception error) => error is SqliteException sqlite
        ? new DocumentStoreException(sqlite.SqliteErrorCode switch
        {
            11 or 26 => DocumentStoreError.Corrupt,
            19 => DocumentStoreError.Conflict,
            5 or 6 => DocumentStoreError.Busy,
            13 => DocumentStoreError.Full,
            18 => DocumentStoreError.Limit,
            8 => DocumentStoreError.ReadOnly,
            10 or 14 => DocumentStoreError.Io,
            _ => DocumentStoreError.Storage
        })
        : error;

    public ValueTask DisposeAsync()
    {
        lock (admission)
        {
            if (!closing) { closing = true; operations.Writer.TryComplete(); }
        }
        return new(stopped.Task);
    }
}

/// <summary>A single short database snapshot/transaction, never exposed to scripts.</summary>
public sealed class DatabaseSession
{
    internal SqliteConnection Connection { get; }
    private readonly SqliteTransaction? transaction;
    internal DatabaseSession(SqliteConnection connection, SqliteTransaction? transaction = null)
    { Connection = connection; this.transaction = transaction; }

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        if (sql.Contains('\0')) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value is bool flag ? flag ? 1 : 0 : value ?? DBNull.Value);
        return command;
    }
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, parameters); return command.ExecuteNonQuery(); }
    public T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(read(reader));
        }
        return result;
    }
    public StorageRevision Revision(string owner) => Query("SELECT revision,receipt FROM owner_revisions WHERE owner=$owner",
        row => new StorageRevision(row.GetInt64(0), row.GetString(1)), ("$owner", owner)).SingleOrDefault() ?? StorageRevision.Empty;

    public StorageRevision CommitRevision(string owner, StorageRevision expected, string operationId)
    {
        if (expected.Revision is < 0 or >= 9007199254740991 || string.IsNullOrEmpty(operationId))
            throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        if (Revision(owner) != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
        var next = new StorageRevision(expected.Revision + 1, operationId);
        Execute("""
            INSERT INTO owner_revisions(owner,revision,receipt) VALUES($owner,$revision,$receipt)
            ON CONFLICT(owner) DO UPDATE SET revision=excluded.revision,receipt=excluded.receipt
            """, ("$owner", owner), ("$revision", next.Revision), ("$receipt", operationId));
        Execute("INSERT INTO operation_receipts(owner,operation_id,revision) VALUES($owner,$id,$revision)",
            ("$owner", owner), ("$id", operationId), ("$revision", next.Revision));
        Execute("DELETE FROM operation_receipts WHERE owner=$owner AND revision<$revision-256",
            ("$owner", owner), ("$revision", next.Revision));
        return next;
    }
}
