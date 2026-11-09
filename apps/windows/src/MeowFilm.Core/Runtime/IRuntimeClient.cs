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
}

/// <summary>An App-owned script connection; callers never own its lifetime.</summary>
public interface IRuntimeSession : IRuntimeClient, IRuntimeImageClient, IAsyncDisposable
{
    bool IsConnected => true;
}

public sealed record RuntimeModule(string Id, string Version);

/// <summary>Authenticated image bytes from a validated runtime-relative reference.
/// Completion includes physical response disposal, including cancellation.</summary>
public interface IRuntimeImageClient
{
    Task<byte[]> ReadImageAsync(string reference, int maximumBytes, CancellationToken cancellationToken = default);
}
