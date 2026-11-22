namespace MeowFilm.Platform.Storage;

public enum DatabaseRole
{
    Data,
    Cache
}

public enum DocumentStoreError
{
    InvalidRequest, Unavailable, UnsafePath, Limit, Conflict, Constraint, Corrupt,
    UnsupportedVersion, Storage, Closed, Busy, Full, ReadOnly, Io
}

/// <summary>Fixed diagnostics only: never contains paths, payloads or an inner exception.</summary>
public sealed class DocumentStoreException : Exception
{
    public DocumentStoreError Error { get; }
    public DocumentStoreException(DocumentStoreError error) : base(error.ToString()) => Error = error;
}
