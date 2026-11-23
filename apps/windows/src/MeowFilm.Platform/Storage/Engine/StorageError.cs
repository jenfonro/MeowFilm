namespace MeowFilm.Platform.Storage;

public enum DatabaseRole
{
    Data,
    Cache
}

public enum StorageError
{
    InvalidRequest, Unavailable, UnsafePath, Limit, Conflict, Constraint, Corrupt,
    UnsupportedVersion, Storage, Closed, Busy, Full, ReadOnly, Io
}

/// <summary>Fixed diagnostics only: never contains paths, payloads or an inner exception.</summary>
public sealed class StorageException : Exception
{
    public StorageError Error { get; }
    public StorageException(StorageError error) : base(error.ToString()) => Error = error;
}
