using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using MeowFilm.Core.Debug;

namespace MeowFilm.Platform.Storage;

public readonly record struct StorageRevision(long Revision)
{
    public static StorageRevision Empty { get; } = new(0);
}

/// <summary>Raised only after SQLite transaction.Commit() was invoked and failed, leaving commit durability uncertain.</summary>
public sealed class TransactionCommitUnknownException : Exception
{
    public StorageError Error { get; }
    public TransactionCommitUnknownException(StorageError error) : base(error.ToString()) => Error = error;
}

/// <summary>
/// WAL-separated native SQLite engine: dedicated write lane and dedicated read-only lane,
/// each with a bounded prepared-statement LRU cache. Supports dual roles (Data vs Cache).
/// </summary>
public sealed class NativeDatabase : IStorageDatabase, IDebugStorageQueryHost
{
    public const int DataApplicationId = 0x4D464442;
    public const int CacheApplicationId = 0x4D464348;
    public const int FormatVersion = 1;
    public const string DataFileName = "meowfilm-data.sqlite3";
    public const string CacheFileName = "meowfilm-cache.sqlite3";
    public const string RelativePath = "storage-v1/" + DataFileName;
    public const string CacheRelativePath = "storage-v1/" + CacheFileName;
    private const int MaximumPendingOperations = 128;
    private const int MaximumPendingBytes = 32 * 1024 * 1024;
    private const int PreparedStatementCacheCapacity = 256;
    private readonly Channel<Operation> writeOperations = Channel.CreateBounded<Operation>(new BoundedChannelOptions(MaximumPendingOperations)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    private readonly Channel<Operation> readOperations = Channel.CreateBounded<Operation>(new BoundedChannelOptions(MaximumPendingOperations)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    private readonly object admission = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource readStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string productRoot;
    private readonly DatabaseRole role;
    private SqliteConnection? writeConnection;
    private PreparedStatementCache? writeStatements;
    private SqliteConnection? readConnection;
    private PreparedStatementCache? readStatements;
    private DatabaseDirectory? directory;
    private bool readLaneStarted;
    private int writeLaneThreadId;
    private int readLaneThreadId;
    private long pendingBytes;
    private int activeOperations;
    private TaskCompletionSource drained = CompletedDrain();
    private int commits;
    private bool closing;
    private Exception? openError;
    public DatabaseRole Role => role;
    public Task Ready => ready.Task;
    public string ProductRoot => productRoot;
    private string FileName => role == DatabaseRole.Cache ? CacheFileName : DataFileName;
    private int ExpectedApplicationId => role == DatabaseRole.Cache ? CacheApplicationId : DataApplicationId;
    private string SynchronousMode => role == DatabaseRole.Cache ? "NORMAL" : "FULL";
    private long ExpectedSynchronousLevel => role == DatabaseRole.Cache ? 1L : 2L;
    private string InitializeLockName => role == DatabaseRole.Cache ? "cache-initialize.lock" : "data-initialize.lock";
    private string MaintenanceLockName => role == DatabaseRole.Cache ? "cache-maintenance.lock" : "data-maintenance.lock";
    public string DatabasePath => Path.Combine(productRoot, "storage-v1", FileName);
    public string SQLiteVersion { get; private set; } = "";
    public string SQLiteSourceId { get; private set; } = "";
    public IReadOnlyList<string> SQLiteCompileOptions { get; private set; } = [];

    public NativeDatabase(string productRoot, DatabaseRole role = DatabaseRole.Data)
    {
        if (string.IsNullOrEmpty(productRoot)) throw new StorageException(StorageError.InvalidRequest);
        if (!Enum.IsDefined(role)) throw new StorageException(StorageError.InvalidRequest);
        this.productRoot = productRoot;
        this.role = role;
        new Thread(RunWriteLane) { IsBackground = true, Name = $"meowfilm native {role.ToString().ToLowerInvariant()} db write" }.Start();
    }
    public static NativeDatabase OpenData(string productRoot) => new(productRoot, DatabaseRole.Data);
    public static NativeDatabase OpenCache(string productRoot) => new(productRoot, DatabaseRole.Cache);

    public Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default) =>
        EnqueueRead(session =>
        {
            token.ThrowIfCancellationRequested();
            var result = read(new(session.Connection, null, session.StatementCache));
            token.ThrowIfCancellationRequested();
            return result;
        }, token);

    public Task<T> ReadSnapshotAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default) =>
        EnqueueRead(session =>
        {
            token.ThrowIfCancellationRequested();
            using var transaction = session.Connection.BeginTransaction(deferred: true);
            try
            {
                var result = read(new(session.Connection, transaction, session.StatementCache));
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                return result;
            }
            catch
            {
                try { transaction.Rollback(); } catch { }
                throw;
            }
        }, token);

    /// <summary>Cancellation applies to admission, not to an accepted transaction or its commit.</summary>
    public Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default, int retainedBytes = 0, Action<Action>? commit = null) =>
        EnqueueWrite(session =>
        {
            var started = DebugLog.IsEnabled ? Stopwatch.GetTimestamp() : 0L;
            int beforeChanges = SQLitePCL.raw.sqlite3_total_changes(session.Connection.Handle);
            using var transaction = session.Connection.BeginTransaction();
            bool commitAttempted = false;
            bool committed = false;
            try
            {
                var result = write(new(session.Connection, transaction, session.StatementCache));
                void Commit()
                {
                    commitAttempted = true;
                    transaction.Commit();
                    committed = true;
                }
                if (commit is null) Commit(); else commit(Commit);
                if (!committed) throw new StorageException(StorageError.Conflict);
                if (SQLitePCL.raw.sqlite3_total_changes(session.Connection.Handle) != beforeChanges)
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
                    var error = translated is StorageException storeError
                        ? storeError.Error
                        : StorageError.Storage;
                    throw new TransactionCommitUnknownException(error);
                }
                throw;
            }
        }, token, retainedBytes);

    public async Task<object> ExecuteReadOnlyQueryAsync(string sql, JsonElement? parameters, int maxRows, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Contains('\0'))
            throw DebugApiException.InvalidArgument("SQL query must be a non-empty string.");
        maxRows = Math.Clamp(maxRows, 1, 5000);
        var started = Stopwatch.GetTimestamp();
        var result = await EnqueueRead(session =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var cmd = session.Connection.CreateCommand();
                cmd.CommandText = sql;
                BindDebugParameters(cmd, parameters);
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
            catch (InvalidOperationException ex)
            {
                throw DebugApiException.InvalidArgument($"Read-only SQL query failed: {ex.Message}");
            }
        }, cancellationToken).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var dbLabel = role == DatabaseRole.Cache ? "cache" : "data";
        DebugLog.Info(DebugCategories.Storage, "debug-sql", "query.readonly",
            new { db = dbLabel, sql, rowCount = result.rows.Count, truncated = result.truncated },
            durationMs: elapsedMs);
        return new
        {
            db = dbLabel,
            columns = result.columns,
            rows = result.rows,
            rowCount = result.rows.Count,
            truncated = result.truncated,
            durationMs = Math.Round(elapsedMs, 3)
        };
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
        EnsureOffLaneThread();
        return ReadAsync(read).GetAwaiter().GetResult();
    }
    public T ReadSnapshot<T>(Func<DatabaseSession, T> read)
    {
        EnsureOffLaneThread();
        return ReadSnapshotAsync(read).GetAwaiter().GetResult();
    }

    private void EnsureOffLaneThread()
    {
        int currentThread = Environment.CurrentManagedThreadId;
        if (SynchronizationContext.Current is not null ||
            currentThread == Volatile.Read(ref readLaneThreadId) ||
            currentThread == Volatile.Read(ref writeLaneThreadId))
            throw new StorageException(StorageError.InvalidRequest);
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
    private Task<T> EnqueueWrite<T>(Func<DatabaseSession, T> action, CancellationToken token, int bytes)
    {
        token.ThrowIfCancellationRequested();
        if (bytes < 0) throw new StorageException(StorageError.Limit);
        var operation = new Operation<T>(action, bytes);
        lock (admission)
        {
            token.ThrowIfCancellationRequested();
            if (openError is not null) return Task.FromException<T>(openError);
            if (closing) throw new StorageException(StorageError.Closed);
            if (pendingBytes != 0 && pendingBytes > MaximumPendingBytes - (long)bytes) throw new StorageException(StorageError.Limit);
            pendingBytes += bytes;
            if (!writeOperations.Writer.TryWrite(operation))
            {
                pendingBytes -= bytes;
                throw new StorageException(StorageError.Limit);
            }
            if (activeOperations++ == 0)
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return operation.Result.Task;
    }

    private Task<T> EnqueueRead<T>(Func<DatabaseSession, T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var operation = new Operation<T>(action, 0);
        lock (admission)
        {
            token.ThrowIfCancellationRequested();
            if (openError is not null) return Task.FromException<T>(openError);
            if (closing) throw new StorageException(StorageError.Closed);
            if (!readOperations.Writer.TryWrite(operation)) throw new StorageException(StorageError.Limit);
            if (activeOperations++ == 0)
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return operation.Result.Task;
    }

    private void RunWriteLane()
    {
        Volatile.Write(ref writeLaneThreadId, Environment.CurrentManagedThreadId);
        Exception? failure = null;
        try
        {
            Open();
            ready.TrySetResult();
            while (writeOperations.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (writeOperations.Reader.TryRead(out var operation))
                {
                    try
                    {
                        try
                        {
                            Exception? latched;
                            lock (admission) latched = openError;
                            if (latched is not null)
                            {
                                operation.Fail(latched);
                                continue;
                            }
                            operation.Execute(new(writeConnection!, null, writeStatements));
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
                writeOperations.Writer.TryComplete();
                readOperations.Writer.TryComplete();
            }
            ready.TrySetException(failure);
        }
        finally
        {
            // A failed open/maintenance must settle every accepted operation.
            // Successful COMMIT results already delivered are never revoked.
            while (writeOperations.Reader.TryRead(out var operation))
            {
                operation.Fail(failure ?? new StorageException(StorageError.Closed));
                lock (admission)
                {
                    pendingBytes -= operation.Bytes;
                    if (--activeOperations == 0) drained.TrySetResult();
                }
            }
            if (readLaneStarted)
            {
                try { readStopped.Task.GetAwaiter().GetResult(); } catch { }
            }
            else
            {
                while (readOperations.Reader.TryRead(out var readOp))
                {
                    readOp.Fail(failure ?? new StorageException(StorageError.Closed));
                    lock (admission)
                    {
                        if (--activeOperations == 0) drained.TrySetResult();
                    }
                }
                readStopped.TrySetResult();
            }
            Exception? releaseFailure = null;
            try
            {
                writeStatements?.Dispose();
            }
            catch (Exception error) { releaseFailure = Translate(error); }
            writeStatements = null;
            try
            {
                // COMMIT is durable in the WAL. Closing is not a maintenance operation.
                writeConnection?.Dispose();
            }
            catch (Exception error)
            {
                releaseFailure ??= Translate(error);
            }
            writeConnection = null;
            try { readStatements?.Dispose(); } catch (Exception error) { releaseFailure ??= Translate(error); }
            readStatements = null;
            try { readConnection?.Dispose(); } catch (Exception error) { releaseFailure ??= Translate(error); }
            readConnection = null;
            try { directory?.Dispose(); } catch (Exception error) { releaseFailure ??= Translate(error); }
            directory = null;
            if (releaseFailure is null) stopped.TrySetResult();
            else stopped.TrySetException(releaseFailure);
        }
    }

    private void RunReadLane()
    {
        Volatile.Write(ref readLaneThreadId, Environment.CurrentManagedThreadId);
        try
        {
            while (readOperations.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (readOperations.Reader.TryRead(out var operation))
                {
                    try
                    {
                        try
                        {
                            Exception? latched;
                            lock (admission) latched = openError;
                            if (latched is not null)
                            {
                                operation.Fail(latched);
                                continue;
                            }
                            operation.Execute(new(readConnection!, null, readStatements));
                        }
                        catch (Exception error) { operation.Fail(error); }
                    }
                    finally
                    {
                        lock (admission)
                        {
                            if (--activeOperations == 0) drained.TrySetResult();
                        }
                    }
                }
        }
        finally
        {
            while (readOperations.Reader.TryRead(out var operation))
            {
                operation.Fail(openError ?? new StorageException(StorageError.Closed));
                lock (admission)
                {
                    if (--activeOperations == 0) drained.TrySetResult();
                }
            }
            try { readStatements?.Dispose(); } catch { }
            readStatements = null;
            try { readConnection?.Dispose(); } catch { }
            readConnection = null;
            readStopped.TrySetResult();
        }
    }

    private void Open()
    {
        var schema = StorageSchema.Read(role);
        directory = new DatabaseDirectory(Path.Combine(productRoot, "storage-v1"), FileName);
        using var initialize = EnterLock(InitializeLockName, exclusive: true, wait: true);
        directory.OpenDatabase();
        bool schemaCommitted = false;
        try
        {
            using var validation = EnterMaintenance(exclusive: false, wait: true);
            writeConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = directory.DatabasePath, Pooling = false,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private, DefaultTimeout = 5
            }.ToString());
            writeConnection.Open();
            if (SQLitePCL.raw.sqlite3_db_config(writeConnection.Handle, 1006 /* SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE */, 1, out _) != 0)
                throw new StorageException(StorageError.Storage);
            var session = new DatabaseSession(writeConnection);
            SQLiteVersion = session.Scalar<string>("SELECT sqlite_version()")!;
            SQLiteSourceId = session.Scalar<string>("SELECT sqlite_source_id()")!;
            SQLiteCompileOptions = session.Query("PRAGMA compile_options", row => row.GetString(0));
            if (!SafeWalVersion(SQLiteVersion))
                throw new StorageException(StorageError.UnsupportedVersion);
            session.ExecuteScript(
                $"PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; " +
                $"PRAGMA synchronous={SynchronousMode}; PRAGMA wal_autocheckpoint=0; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16384;");
            long identity = session.Scalar<long>("PRAGMA application_id");
            long version = session.Scalar<long>("PRAGMA user_version");
            long tableCount = session.Scalar<long>("SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'");
            if (identity == 0 && version == 0 && tableCount == 0)
            {
                if (!directory.Created) throw new StorageException(StorageError.Corrupt);
                // The current namespace starts empty. No probes, upgrades or conversions.
                session.Execute("PRAGMA auto_vacuum=INCREMENTAL");
                using var transaction = writeConnection.BeginTransaction();
                var creating = new DatabaseSession(writeConnection, transaction);
                creating.ExecuteScript(schema.Sql);
                creating.Execute("INSERT INTO storage_identity(singleton,schema_hash) VALUES(1,$hash)", ("$hash", schema.Hash));
                creating.ExecuteScript($"PRAGMA application_id={ExpectedApplicationId}; PRAGMA user_version={FormatVersion};");
                transaction.Commit();
                schemaCommitted = true;
            }
            else if (identity != ExpectedApplicationId || version != FormatVersion)
                throw new StorageException(StorageError.UnsupportedVersion);
            StorageSchema.Verify(session, schema);
            string mode = session.Scalar<string>("PRAGMA journal_mode=WAL")!;
            if (mode != "wal" ||
                session.Scalar<long>("PRAGMA synchronous") != ExpectedSynchronousLevel ||
                session.Scalar<long>("PRAGMA foreign_keys") != 1 ||
                session.Scalar<long>("PRAGMA trusted_schema") != 0 ||
                session.Scalar<long>("PRAGMA wal_autocheckpoint") != 0 ||
                session.Scalar<long>("PRAGMA auto_vacuum") != 2 ||
                session.Scalar<long>("PRAGMA temp_store") != 2 ||
                session.Scalar<long>("PRAGMA cache_size") != -16384 ||
                session.Scalar<long>("PRAGMA busy_timeout") != 5000)
                throw new StorageException(StorageError.Storage);
            // Attach this long-lived connection to the WAL before another process
            // can become its only shared-memory participant and exit.
            _ = session.Scalar<long>("SELECT count(*) FROM storage_identity");
            directory.Check();
            writeStatements = new PreparedStatementCache(writeConnection, PreparedStatementCacheCapacity);

            readConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = directory.DatabasePath, Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private, DefaultTimeout = 5
            }.ToString());
            readConnection.Open();
            if (SQLitePCL.raw.sqlite3_db_config(readConnection.Handle, 1006 /* SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE */, 1, out _) != 0)
                throw new StorageException(StorageError.Storage);
            var readSession = new DatabaseSession(readConnection);
            readSession.ExecuteScript(
                "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; " +
                "PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16384; PRAGMA query_only=ON;");
            if (readSession.Scalar<string>("PRAGMA journal_mode") != "wal" ||
                readSession.Scalar<long>("PRAGMA auto_vacuum") != 2 ||
                readSession.Scalar<long>("PRAGMA query_only") != 1)
                throw new StorageException(StorageError.Storage);
            _ = readSession.Scalar<long>("SELECT count(*) FROM storage_identity");
            readStatements = new PreparedStatementCache(readConnection, PreparedStatementCacheCapacity);
            var readThread = new Thread(RunReadLane) { IsBackground = true, Name = $"meowfilm native {role.ToString().ToLowerInvariant()} db read" };
            readThread.Start();
            readLaneStarted = true;
        }
        catch
        {
            if (directory is { Created: true } && !schemaCommitted)
            {
                try { readStatements?.Dispose(); } catch { }
                readStatements = null;
                try { readConnection?.Dispose(); } catch { }
                readConnection = null;
                try { writeStatements?.Dispose(); } catch { }
                writeStatements = null;
                try { writeConnection?.Dispose(); } catch { }
                writeConnection = null;
                directory.DeleteFailedCreation();
            }
            throw;
        }
    }

    private static bool SafeWalVersion(string text) => Version.TryParse(text, out var version) &&
        (version >= new Version(3, 51, 3) ||
            version.Major == 3 && version.Minor == 50 && version.Build >= 7 ||
            version.Major == 3 && version.Minor == 44 && version.Build >= 6);

    private SafeFileHandle? EnterMaintenance(bool exclusive, bool wait) => EnterLock(MaintenanceLockName, exclusive, wait);
    private SafeFileHandle? EnterLock(string name, bool exclusive, bool wait)
    {
        if (directory is null) return null;
        var lockPath = Path.Combine(Path.GetDirectoryName(directory.DatabasePath)!, name);
        var limit = Environment.TickCount64 + (wait ? 5000 : 0);
        do
        {
            var handle = WindowsStorageFiles.OpenHandle(lockPath, FileMode.OpenOrCreate,
                exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read,
                sharingIsBusy: true);
            if (handle is not null || !wait) return handle;
            Thread.Sleep(20);
        } while (Environment.TickCount64 < limit);
        throw new StorageException(StorageError.Busy);
    }
    private void Maintain()
    {
        if (directory is null) return;
        try
        {
            using var lease = EnterMaintenance(exclusive: true, wait: false);
            if (lease is null) return;
            directory.Check();
            MaintainLocked();
        }
        catch (StorageException error) when (error.Error == StorageError.Busy) { }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
        catch (Exception error)
        {
            var translated = Translate(error);
            DebugLog.Warn(DebugCategories.Storage, "sqlite", "maintain.error", new { commits }, error: translated);
            if (translated is StorageException { Error: StorageError.Corrupt or StorageError.UnsafePath } fatal)
            {
                lock (admission) { openError ??= fatal; }
            }
        }
    }
    private void MaintainLocked()
    {
        if (directory is null || writeConnection is null) return;
        var session = new DatabaseSession(writeConnection, null, writeStatements);
        // PASSIVE never waits for another App/runtime reader or truncates its files.
        bool busy = !session.TryQuerySingle("PRAGMA wal_checkpoint(PASSIVE)", row => row.GetInt64(0), out var checkpointBusy) || checkpointBusy != 0;
        if (!busy && session.Scalar<long>("PRAGMA freelist_count") >= 256) session.Execute("PRAGMA incremental_vacuum(128)");
    }
    private static Exception Translate(Exception error) => error is SqliteException sqlite
        ? new StorageException(sqlite.SqliteErrorCode switch
        {
            11 or 26 => StorageError.Corrupt,
            19 => StorageError.Constraint,
            5 or 6 => StorageError.Busy,
            13 => StorageError.Full,
            18 => StorageError.Limit,
            8 => StorageError.ReadOnly,
            10 or 14 => StorageError.Io,
            _ => StorageError.Storage
        })
        : error;

    public ValueTask DisposeAsync()
    {
        lock (admission)
        {
            if (!closing)
            {
                closing = true;
                writeOperations.Writer.TryComplete();
                readOperations.Writer.TryComplete();
            }
        }
        return new(stopped.Task);
    }
}
