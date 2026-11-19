using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Platform.Storage;

public sealed record StorageRevision(long Revision, string Receipt)
{
    public static StorageRevision Empty { get; } = new(0, "");
}

/// <summary>One bounded native I/O lane. Repositories own SQL and business policy.</summary>
public sealed class NativeDatabase : IStorageDatabase
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
    private readonly string? productRoot;
    private SqliteConnection? connection;
    private DatabaseDirectory? directory;
    private long pendingBytes;
    private int commits;
    private bool closing;
    private Exception? openError;
    public Task Ready => ready.Task;
    public string? ProductRoot => productRoot;
    public string? DatabasePath => productRoot is null ? null : Path.Combine(productRoot, "storage-v1", "meowfilm.sqlite3");
    public string SQLiteVersion { get; private set; } = "";
    public string SQLiteSourceId { get; private set; } = "";
    public IReadOnlyList<string> SQLiteCompileOptions { get; private set; } = [];

    public NativeDatabase(string productRoot) : this(productRoot, false) { }
    private NativeDatabase(string? productRoot, bool memory)
    {
        if (!memory && string.IsNullOrEmpty(productRoot)) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        this.productRoot = productRoot;
        new Thread(Run) { IsBackground = true, Name = "meowfilm native database" }.Start();
    }
    public static NativeDatabase CreateMemory() => new(null, true);

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
            using var transaction = session.Connection.BeginTransaction();
            var result = write(new(session.Connection, transaction));
            bool committed = false;
            void Commit() { transaction.Commit(); committed = true; }
            if (commit is null) Commit(); else commit(Commit);
            if (!committed) throw new DocumentStoreException(DocumentStoreError.Conflict);
            commits++;
            return result;
        }, token, retainedBytes);

    public Task<StorageRevision> RevisionAsync(string owner, CancellationToken token = default) =>
        ReadAsync(session => session.Revision(owner), token);

    // Used only by the music owner's off-UI command lane for identity lookups.
    public T Read<T>(Func<DatabaseSession, T> read)
    {
        if (SynchronizationContext.Current is not null) throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
        return ReadAsync(read).GetAwaiter().GetResult();
    }
    public Task DrainAsync() => Enqueue(_ => true, CancellationToken.None, 0);
    public Task BackupAsync(string destination, CancellationToken token = default) =>
        Enqueue(source =>
        {
            string root = WindowsDocumentFiles.ValidateRoot(Path.GetDirectoryName(destination)!);
            if (Path.GetFileName(destination) != "meowfilm.sqlite3")
                throw new DocumentStoreException(DocumentStoreError.InvalidRequest);
            using var parent = new WindowsDocumentFiles.Root(root);
            using var claimed = WindowsDocumentFiles.OpenFile(Path.Combine(root, "meowfilm.sqlite3"),
                WindowsDocumentFiles.Read | WindowsDocumentFiles.Write, 3, WindowsDocumentFiles.CreateNew)!;
            bool completed = false;
            try
            {
                using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = destination, Pooling = false, Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private
                }.ToString());
                target.Open(); source.Connection.BackupDatabase(target);
                var snapshot = new DatabaseSession(target);
                VerifySchema(snapshot, ReadSchema());
                if (snapshot.Scalar<string>("PRAGMA quick_check") != "ok")
                    throw new DocumentStoreException(DocumentStoreError.Corrupt);
                RandomAccess.FlushToDisk(claimed);
                completed = true;
                return true;
            }
            finally
            {
                claimed.Dispose();
                if (!completed) File.Delete(destination);
            }
        }, token, 0, exclusive: true);

    private abstract class Operation(int bytes, bool exclusive)
    {
        internal int Bytes { get; } = bytes;
        internal bool Exclusive { get; } = exclusive;
        internal abstract void Execute(DatabaseSession session);
        internal abstract void Fail(Exception error);
    }
    private sealed class Operation<T>(Func<DatabaseSession, T> action, int bytes, bool exclusive) : Operation(bytes, exclusive)
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
    private Task<T> Enqueue<T>(Func<DatabaseSession, T> action, CancellationToken token, int bytes, bool exclusive = false)
    {
        token.ThrowIfCancellationRequested();
        if (bytes < 0) throw new DocumentStoreException(DocumentStoreError.Limit);
        var operation = new Operation<T>(action, bytes, exclusive);
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
        }
        return operation.Result.Task;
    }

    private void Run()
    {
        Exception? failure = null;
        bool opened = false;
        try
        {
            Open();
            opened = true;
            ready.TrySetResult();
            while (operations.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (operations.Reader.TryRead(out var operation))
                {
                    try
                    {
                        try
                        {
                            directory?.Check();
                            using var lease = EnterMaintenance(exclusive: operation.Exclusive, wait: true);
                            operation.Execute(new(connection!));
                        }
                        catch (Exception error) { operation.Fail(error); }
                    }
                    finally { lock (admission) pendingBytes -= operation.Bytes; }
                    if (commits >= 64)
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
                lock (admission) pendingBytes -= operation.Bytes;
            }
            try
            {
                if (connection is not null)
                {
                    if (opened)
                    {
                        using var lease = EnterMaintenance(exclusive: true, wait: true);
                        MaintainLocked();
                    }
                    connection.Dispose();
                }
            }
            catch (Exception error)
            {
                failure ??= Translate(error);
                try { connection?.Dispose(); } catch (Exception closingError) { failure ??= Translate(closingError); }
            }
            connection = null;
            try { directory?.Dispose(); } catch (Exception error) { failure ??= Translate(error); }
            directory = null;
            if (failure is null) stopped.TrySetResult();
            else stopped.TrySetException(failure);
        }
    }

    private void Open()
    {
        var schema = ReadSchema();
        if (productRoot is not null) directory = new DatabaseDirectory(Path.Combine(productRoot, "storage-v1"), "meowfilm.sqlite3");
        using var initialize = directory is null ? null : EnterLock("initialize.lock", exclusive: true, wait: true);
        directory?.OpenDatabase();
        using var validation = EnterMaintenance(exclusive: false, wait: true);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory?.DatabasePath ?? ":memory:", Pooling = false,
            Mode = directory is null ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        if (SQLitePCL.raw.sqlite3_db_config(connection.Handle, 1006 /* SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE */, 1, out _) != 0)
            throw new DocumentStoreException(DocumentStoreError.Storage);
        var session = new DatabaseSession(connection);
        SQLiteVersion = session.Scalar<string>("SELECT sqlite_version()")!;
        SQLiteSourceId = session.Scalar<string>("SELECT sqlite_source_id()")!;
        SQLiteCompileOptions = session.Query("PRAGMA compile_options", row => row.GetString(0));
        if (directory is not null && !SafeWalVersion(SQLiteVersion))
            throw new DocumentStoreException(DocumentStoreError.UnsupportedVersion);
        session.Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; PRAGMA temp_store=MEMORY");
        long identity = session.Scalar<long>("PRAGMA application_id");
        long version = session.Scalar<long>("PRAGMA user_version");
        long tableCount = session.Scalar<long>("SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'");
        if (identity == 0 && version == 0 && tableCount == 0)
        {
            if (directory is { Created: false }) throw new DocumentStoreException(DocumentStoreError.Corrupt);
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
        if (mode != (directory is null ? "memory" : "wal") ||
            session.Scalar<long>("PRAGMA synchronous") != 2 ||
            session.Scalar<long>("PRAGMA foreign_keys") != 1 ||
            session.Scalar<long>("PRAGMA trusted_schema") != 0 ||
            session.Scalar<long>("PRAGMA wal_autocheckpoint") != 0 ||
            session.Scalar<long>("PRAGMA auto_vacuum") != 2)
            throw new DocumentStoreException(DocumentStoreError.Storage);
        // Attach this long-lived connection to the WAL before another process
        // can become its only shared-memory participant and exit.
        _ = session.Scalar<long>("SELECT count(*) FROM storage_identity");
        directory?.Check();
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
        if (actual.Query("PRAGMA foreign_key_check", _ => true).Count != 0)
            throw new DocumentStoreException(DocumentStoreError.Corrupt);
    }
    private static bool SafeWalVersion(string text) => Version.TryParse(text, out var version) &&
        (version >= new Version(3, 51, 3) ||
            version.Major == 3 && version.Minor == 50 && version.Build >= 7 ||
            version.Major == 3 && version.Minor == 44 && version.Build >= 6);

    private SafeFileHandle? EnterMaintenance(bool exclusive, bool wait) => EnterLock("maintenance.lock", exclusive, wait);
    private SafeFileHandle? EnterLock(string name, bool exclusive, bool wait)
    {
        if (directory is null) return null;
        var limit = Environment.TickCount64 + (wait ? 5000 : 0);
        do
        {
            directory.CheckRoot();
            var handle = WindowsDocumentFiles.OpenFile(Path.Combine(Path.GetDirectoryName(directory.DatabasePath)!, name),
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
