namespace MeowFilm.Platform.Storage;

/// <summary>Borrowed database I/O. Repositories declare tables, transactions and owner policy.</summary>
public interface IStorageDatabase : IAsyncDisposable
{
    DatabaseRole Role { get; }
    Task Ready { get; }
    string ProductRoot { get; }
    string DatabasePath { get; }
    /// <summary>Single-statement or auto-commit read on the dedicated WAL read-only lane (no BEGIN/COMMIT overhead).</summary>
    Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default);
    /// <summary>Multi-statement consistent read snapshot wrapped in BEGIN DEFERRED / COMMIT on the read-only lane.</summary>
    Task<T> ReadSnapshotAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default);
    Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null);
    T Read<T>(Func<DatabaseSession, T> read);
    T ReadSnapshot<T>(Func<DatabaseSession, T> read);
    Task DrainAsync();
}
