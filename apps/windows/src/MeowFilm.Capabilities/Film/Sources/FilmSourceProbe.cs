using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>Thin client adapter delegating cat source probing to Node's cat.script.probe owner.</summary>
internal sealed class FilmSourceProbe(CatScriptTransport transport, FilmCatSource cat, FilmVideoSource site)
{
    internal async Task<FilmSourceDetection> RunAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        JsonElement value;
        try
        {
            value = await transport.CallAsync("probe", new
            {
                installationId = cat.InstallationId,
                generation = cat.Generation,
                siteId = site.Key.SiteKey,
                siteApi = site.Api,
                actions = site.Actions
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (FilmSourceException error) when (IsStaleCancellation(error.Code))
        {
            throw new OperationCanceledException(error.Code, error, token);
        }
        catch (BridgeException error) when (IsStaleCancellation(error.Code))
        {
            throw new OperationCanceledException(error.Code, error, token);
        }

        token.ThrowIfCancellationRequested();
        string result = CatScriptTransport.Text(value, "result");
        CatScriptTransport.Require(result is "valid" or "search_error" or "category_error" or "invalid");
        long checkedAt = CatScriptTransport.Integer(value, "checkedAt");
        CatScriptTransport.Require(value.TryGetProperty("playbackResolved", out var resolved) &&
            resolved.ValueKind is JsonValueKind.True or JsonValueKind.False);
        CatScriptTransport.Require(value.TryGetProperty("steps", out var stepsValue) &&
            stepsValue.ValueKind == JsonValueKind.Array);

        var steps = new List<FilmSourceProbeStep>();
        foreach (var step in stepsValue.EnumerateArray())
        {
            CatScriptTransport.Require(step.ValueKind == JsonValueKind.Object);
            string operation = CatScriptTransport.Text(step, "operation");
            string status = CatScriptTransport.Text(step, "status");
            string? reason = CatScriptTransport.OptionalText(step, "reason");
            steps.Add(new(operation, status, reason));
        }

        return new(result, checkedAt, resolved.GetBoolean(), Array.AsReadOnly(steps.ToArray()));
    }

    private static bool IsStaleCancellation(string code) =>
        code is "CANCELLED" or "STALE_SESSION" or "HOST_CLOSED" or "CLOSED" or "DISABLED" or "STARTING";
}
