namespace MeowFilm.Platform.Storage;

/// <summary>Borrowed database I/O. Repositories declare tables, transactions and owner policy.</summary>
public interface IStorageDatabase : IAsyncDisposable
{
    Task Ready { get; }
    string ProductRoot { get; }
    string DatabasePath { get; }
    Task<T> ReadAsync<T>(Func<DatabaseSession, T> read, CancellationToken token = default);
    Task<T> WriteAsync<T>(Func<DatabaseSession, T> write, CancellationToken token = default,
        int retainedBytes = 0, Action<Action>? commit = null);
    Task<StorageRevision> RevisionAsync(string owner, CancellationToken token = default);
    T Read<T>(Func<DatabaseSession, T> read);
    Task DrainAsync();
}
