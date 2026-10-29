using System.Text.Json;

namespace MeowFilm.Core.Runtime;

public interface IRuntimeClient
{
    Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
}

/// <summary>An App-owned script connection; callers never own its lifetime.</summary>
public interface IRuntimeSession : IRuntimeClient, IAsyncDisposable
{
    bool IsConnected => true;
}

public sealed record RuntimeModule(string Id, string Version);
