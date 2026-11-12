using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities.Platforms;

/// <summary>Borrows the App runtime only for fresh subscription tokens. Owns no session, Cookie or token cache.</summary>
internal sealed class MusicNeteaseTokenProvider(IRuntimeClient runtime)
{
    private static bool KnownCode(string code) => code is "INVALID_REQUEST" or "CLOSED" or "CANCELLED" or
        "TIMEOUT" or "NETWORK_ERROR" or "UPSTREAM_HTTP" or "UPSTREAM_FORMAT" or "SDK_FAILURE" or "SDK_CLEANUP";

    // RFC6265 cookie-octet: the EAPI header is also serialized as the native HTTP Cookie header.
    internal static bool ValidToken(string token) => token.Length > 0 &&
        token.All(c => c is >= '!' and <= '~' && c is not ('"' or ',' or ';' or '\\'));

    internal async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonElement result;
        try
        {
            result = await runtime.CallAsync("netease.token.get", new { }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (BridgeException error)
        {
            throw new MusicPlatformException(KnownCode(error.Code) ? error.Code :
                error.Code == "INVALID_RESPONSE" ? "UPSTREAM_FORMAT" : "SDK_FAILURE");
        }
        catch { throw new MusicPlatformException("SDK_FAILURE"); }
        try
        {
            PJ.Require(result.ValueKind == JsonValueKind.Object && result.EnumerateObject().Count() == 3, "UPSTREAM_FORMAT");
            bool ok = result.GetProperty("ok").GetBoolean();
            string contract = result.GetProperty("contract").GetString()!;
            PJ.Require(contract == "netease.token.v1" || !ok && contract == RuntimeHttpProtocol.Name, "UPSTREAM_FORMAT");
            if (!ok)
            {
                string code = result.GetProperty("code").GetString()!;
                throw new MusicPlatformException(KnownCode(code) ? code : "UPSTREAM_FORMAT");
            }
            var value = result.GetProperty("value");
            PJ.Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1, "UPSTREAM_FORMAT");
            string token = value.GetProperty("token").GetString() ?? "";
            PJ.Require(ValidToken(token), "UPSTREAM_FORMAT");
            return token;
        }
        catch (MusicPlatformException) { throw; }
        catch { throw new MusicPlatformException("UPSTREAM_FORMAT"); }
    }
}
