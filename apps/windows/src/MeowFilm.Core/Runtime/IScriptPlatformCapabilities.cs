using System.Text.Json;

namespace MeowFilm.Core.Runtime;

/// <summary>
/// Explicit reverse capabilities exported by an assembled capability. The App
/// freezes these bindings before starting its script runtime; no service lookup,
/// document namespace, credential or cross-module authority is implied.
/// </summary>
public interface IScriptPlatformCapabilities
{
    IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> ScriptCapabilities { get; }
}
