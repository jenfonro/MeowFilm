using System.Text.Json;
using System.Text.Json.Serialization;
using MeowFilm.Capabilities.Accounts;

namespace MeowFilm.Capabilities;

public interface IMusicAccountDetailsNotifications
{
    event Action<string>? AccountDetailsInvalidated;
}

public sealed partial class MusicNativeApplication
{
    private static readonly JsonSerializerOptions AccountDetailsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<MusicAccountDetailsFreshness>(JsonNamingPolicy.CamelCase, false) }
    };
    public event Action<string>? AccountDetailsInvalidated;
    private void AccountDetailsChanged(string platform)
    {
        foreach (var observer in AccountDetailsInvalidated?.GetInvocationList() ?? [])
        {
            try { ((Action<string>)observer)(platform); }
            catch { /* Display listeners cannot change account revocation or its physical drain. */ }
        }
    }
    public Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default) =>
        AccountDetailsCall("music.accountDetails.read", new { platform }, token);
    public Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
        CancellationToken token = default) =>
        AccountDetailsCall("music.accountDetails.refresh", new { platform, expectedGeneration }, token);

    private async Task<MusicAccountDetails> AccountDetailsCall(string method, object input, CancellationToken token)
    {
        // Route through the same native admission/close/drain fence as all other application calls.
        var reply = await ExecuteAsync(method, input, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!Okay(reply)) throw new MusicOperationException(reply.GetProperty("code").GetString() ?? "INVALID_RESPONSE");
        return reply.GetProperty("details").Deserialize<MusicAccountDetails>(AccountDetailsJson) ??
            throw new MusicOperationException("INVALID_RESPONSE");
    }
    private async Task<JsonElement> ExecuteAccountDetailsAsync(string method, object? parameters, CancellationToken token)
    {
        try
        {
            var input = parameters is JsonElement element ? element : JsonSerializer.SerializeToElement(parameters);
            if (input.ValueKind != JsonValueKind.Object || method is not ("music.accountDetails.read" or "music.accountDetails.refresh"))
                return Failed("INVALID_REQUEST");
            var fields = input.EnumerateObject().ToArray();
            if (fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                fields.Any(f => f.Name is not ("platform" or "expectedGeneration")) ||
                !input.TryGetProperty("platform", out var platform) || platform.ValueKind != JsonValueKind.String ||
                platform.GetString() is not ("wy" or "tx" or "kg"))
                return Failed("INVALID_REQUEST");
            long generation = 0;
            bool refresh = method == "music.accountDetails.refresh";
            if (refresh
                ? fields.Length != 2 || !input.TryGetProperty("expectedGeneration", out var version) ||
                  version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out generation) ||
                  generation is < 1 or > 9_007_199_254_740_991L
                : fields.Length != 1) return Failed("INVALID_REQUEST");
            lock (gate) if (accountReceiptUnknown) return Failed("COMMIT_UNKNOWN");
            var details = refresh
                ? await accounts.RefreshDetailsAsync(platform.GetString()!, generation, token).ConfigureAwait(false)
                : await accounts.ReadDetailsAsync(platform.GetString()!, token).ConfigureAwait(false);
            lock (gate) if (accountReceiptUnknown) return Failed("COMMIT_UNKNOWN");
            return JsonSerializer.SerializeToElement(new { ok = true, details }, AccountDetailsJson);
        }
        catch (MusicAccountException error) { return Failed(error.Code); }
    }
}
