using System.Text.Json;

namespace MeowFilm.Core.Runtime;

public interface IRuntimeClient
{
    /// <summary>Returns the validated complete {ok, contract, value|code} HTTP envelope.</summary>
    Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    /// <summary>Borrows source until the returned task settles, including cancellation;
    /// returns the validated complete HTTP envelope.</summary>
    Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default);
    /// <summary>Observes current snapshots only. Disposing the enumerator releases its HTTP
    /// subscription, never the App runtime or the business session.</summary>
    IAsyncEnumerable<JsonElement> EventsAsync(string module, Guid sessionId, long after = 0,
        CancellationToken cancellationToken = default) => throw new BridgeException("METHOD_UNAVAILABLE");
}

/// <summary>An App-owned script connection; callers never own its lifetime.</summary>
public interface IRuntimeSession : IRuntimeClient, IRuntimeImageClient, IAsyncDisposable
{
    bool IsConnected => true;
}

/// <summary>
/// Borrowed from one verified App runtime epoch. Client never reconnects; the
/// process/epoch owner revokes this capability, not the capturing view.
/// </summary>
public sealed record RuntimeConnection(IRuntimeClient Client, Uri BusinessOrigin, CancellationToken Revoked);

public interface IRuntimeConnectionSource
{
    Task<RuntimeConnection> CaptureConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed record RuntimeModule(string Id, string Version);

/// <summary>Image bytes from a validated runtime-relative reference, without HTTP access credentials.
/// Completion includes physical response disposal, including cancellation.</summary>
public interface IRuntimeImageClient
{
    Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default);
}
