using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>One fixed runtime epoch supplies both content and the optional original cache receipt.</summary>
internal static class FilmSearchDataClient
{
    internal static async Task<FilmSearchData> ReadAsync(IRuntimeClient runtime, string operation,
        object parameters, object cacheIdentity, CancellationToken token)
    {
        if (runtime is not IRuntimeConnectionSource source) throw new BridgeException("CAPABILITY_UNAVAILABLE");
        var owner = await source.CaptureConnectionAsync(token).ConfigureAwait(false);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Revoked);
        wait.Token.ThrowIfCancellationRequested();
        var client = new FilmPlaybackClient(owner.Client);
        var value = await client.CallAsync("data." + operation, parameters, wait.Token).ConfigureAwait(false);
        wait.Token.ThrowIfCancellationRequested();
        var receipt = await client.CallAsync("data.cache.entry", cacheIdentity, wait.Token).ConfigureAwait(false);
        wait.Token.ThrowIfCancellationRequested();
        if (receipt.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new FilmPlaybackException("INVALID_RESPONSE");
        return new(value, owner, receipt.ValueKind == JsonValueKind.Object ? receipt : null);
    }
}
