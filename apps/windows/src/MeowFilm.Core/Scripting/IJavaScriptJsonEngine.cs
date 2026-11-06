using System.Text.Json;

namespace MeowFilm.Core.Scripting;

/// <summary>Executes an application-owned JSON function; not a user script host.</summary>
public interface IJavaScriptJsonEngine : IAsyncDisposable
{
    Task<JsonElement> InvokeAsync(object request, CancellationToken cancellationToken = default);
}

public sealed class JavaScriptEngineException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
