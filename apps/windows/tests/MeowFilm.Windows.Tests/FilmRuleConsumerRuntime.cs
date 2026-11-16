using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Windows.Tests;

/// <summary>Explicit preview receipts and block notifications; no execution or persistence.</summary>
internal sealed class FilmRuleConsumerRuntime : IRuntimeClient, IRuntimeSettingsNotifications
{
    internal readonly List<(string Method, JsonElement Input)> Calls = [];
    internal readonly List<string> Notifications = [];
    internal Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Respond;
    internal Action<string>? Notify;
    public void SettingsChanged(string block) { Notifications.Add(block); Notify?.Invoke(block); }
    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = FilmJson.Encode(parameters ?? new { });
        Calls.Add((method, input));
        if (Respond is not { } respond) throw new InvalidOperationException("Provide an explicit preview receipt for " + method);
        return FilmSearchConsumerRuntime.Success(await respond(method, input, cancellationToken));
    }
    public Task<JsonElement> UploadSourceAsync(string module, string installationId, long sourceRevision,
        ReadOnlyMemory<byte> source, string? basename = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Rules do not upload source.");
}
