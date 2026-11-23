using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

internal sealed class CatScriptTransport(IRuntimeClient runtime)
{
    internal async Task<JsonElement> CallAsync(string operation, object parameters, CancellationToken token)
    {
        try
        {
            var reply = await runtime.CallAsync("cat.script." + operation, parameters, token).ConfigureAwait(false);
            return Value(reply, token);
        }
        catch (BridgeException error) when (error.Code == "CANCELLED")
        { throw new OperationCanceledException(error.Message, error, token); }
    }

    internal Task<JsonElement> InvokeAsync(FilmCatSource cat, FilmVideoSource site,
        string operation, object payload, CancellationToken token) =>
        CallAsync("invoke", new
        {
            installationId = cat.InstallationId, generation = cat.Generation,
            siteId = site.Key.SiteKey, operation, payload
        }, token);

    private static JsonElement Value(JsonElement reply, CancellationToken token)
    {
        // IRuntimeClient validates the complete cat.script.v2 envelope.
        if (!reply.GetProperty("ok").GetBoolean())
        {
            string code = reply.GetProperty("code").GetString()!;
            if (code == "CANCELLED") throw new OperationCanceledException(code, token);
            throw new FilmSourceException(code);
        }
        token.ThrowIfCancellationRequested();
        return reply.GetProperty("value");
    }

    internal static JsonElement Parse(string text)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException) { throw new FilmSourceException("INVALID_RESPONSE"); }
    }

    internal static string Text(JsonElement value, string property)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) &&
            text.ValueKind == JsonValueKind.String);
        return value.GetProperty(property).GetString()!;
    }

    internal static string? OptionalText(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) &&
        text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    internal static long Integer(JsonElement value, string property)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var number) &&
            number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out long integer) &&
            integer >= 0 && integer <= BridgeCodec.MaximumSafeInteger);
        return value.GetProperty(property).GetInt64();
    }

    internal static void Require(bool condition)
    {
        if (!condition) throw new FilmSourceException("INVALID_RESPONSE");
    }
}
