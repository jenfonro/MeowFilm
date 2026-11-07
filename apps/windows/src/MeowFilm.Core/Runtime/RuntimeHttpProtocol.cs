using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowFilm.Core.Runtime;

/// <summary>The public contracts declared by apps/runtime/contracts.mjs.</summary>
public static partial class RuntimeHttpProtocol
{
    public const string Name = "runtime.http.v1";
    internal sealed record ModuleContract(string Version, string Name, string Prefix,
        string[] Operations, HashSet<string> Codes);
    internal static readonly HashSet<string> PublicCodes = new(StringComparer.Ordinal)
    {
        "INVALID_REQUEST", "METHOD_NOT_FOUND", "PERMISSION_DENIED", "CLOSED", "CANCELLED", "TIMEOUT", "INVALID_RESPONSE"
    };
    internal static readonly IReadOnlyDictionary<string, ModuleContract> Modules =
        new Dictionary<string, ModuleContract>(StringComparer.Ordinal)
    {
        ["music"] = new("0.8.0", "music.script.v3", "music.script.",
            ["sources.list", "sources.detail", "sources.add", "sources.download",
                "sources.edit", "sources.update", "sources.rollback", "sources.enable",
                "sources.disable", "sources.remove", "sources.check", "resolve",
                "invoke", "close"],
            new(StringComparer.Ordinal) { "INVALID_REQUEST", "CLOSED", "CANCELLED", "LIMIT",
                "SCRIPT_INTEGRITY", "SCRIPT_ENCODING", "STALE_SOURCE", "SOURCE_NOT_FOUND",
                "SOURCE_DISABLED", "CAPABILITY_UNAVAILABLE", "PERMISSION_DENIED", "TIMEOUT",
                "SCRIPT_CLEANUP", "SCRIPT_FAILURE", "SCRIPT_PROTOCOL_ERROR", "NETWORK_ERROR",
                "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "STORAGE_ERROR", "CORRUPT_STORAGE",
                "ROLLBACK_UNAVAILABLE", "INVALID_SOURCE_URL", "SOURCE_DOWNLOAD_FAILED", "SOURCE_REDIRECT",
                "MEDIA_UNAVAILABLE", "INVALID_MEDIA_CANDIDATE", "EXPIRED_MEDIA_CANDIDATE", "MEDIA_HEADERS_UNSUPPORTED",
                "TARGET_CHANGED", "RECORDING_MISMATCH" }),
        ["source"] = new("0.2.0", "source.runtime.v2", "source.runtime.",
            ["load", "unload", "invoke", "status",
                "close"],
            new(StringComparer.Ordinal) { "INVALID_REQUEST", "CLOSED", "CANCELLED", "LIMIT",
                "SOURCE_INTEGRITY", "SOURCE_ENCODING", "STALE_SOURCE", "SOURCE_NOT_FOUND",
                "SOURCE_DISABLED", "UNSUPPORTED_PROTOCOL", "UNSUPPORTED_ACTION", "UNSUPPORTED_RULE",
                "UNSUPPORTED_DEPENDENCY", "CAPABILITY_UNAVAILABLE", "PERMISSION_DENIED", "WEB_INTERACTION_REQUIRED",
                "TIMEOUT", "SOURCE_CLEANUP", "SOURCE_FAILURE", "NETWORK_ERROR",
                "HTTP_ERROR", "INVALID_RESPONSE", "STORAGE_ERROR", "VIRTUAL_ROUTE_NOT_FOUND" }),
        ["cat"] = new("0.2.0", "cat.script.v2", "cat.script.",
            ["start", "status", "refresh", "install",
                "import", "edit", "update", "restart",
                "enable", "uninstall", "invoke", "management.describe",
                "management.sync"],
            new(StringComparer.Ordinal) { "INVALID_ARGUMENT", "HOST_CLOSED", "CANCELLED", "RESOURCE_LIMIT",
                "SCRIPT_PROTOCOL_ERROR", "STALE_SESSION", "NOT_FOUND", "DISABLED",
                "ALREADY_EXISTS", "SCRIPT_EXCEPTION", "STARTING", "CORRUPT_STORAGE",
                "SERVICE_ERROR", "TIMEOUT", "PERMISSION_DENIED", "CAPABILITY_UNAVAILABLE",
                "UNSUPPORTED_PROTOCOL", "UNSUPPORTED_ACTION", "NETWORK_ERROR", "STORAGE_ERROR",
                "SCRIPT_CLEANUP", "INVALID_JSON", "UPSTREAM_HTTP_ERROR", "UNSUPPORTED_DEPENDENCY",
                "CLOSED", "STORAGE_CONFLICT", "COMMIT_UNKNOWN", "SERVICE_EXISTS",
                "SERVICE_CLOSED", "ROLLBACK_UNAVAILABLE", "UNSUPPORTED_RULE", "UNSUPPORTED_SCRIPT_ASYNC",
                "HTTP_ERROR", "INVALID_RESPONSE", "UNSUPPORTED_OPTION", "UNSUPPORTED_CRYPTO",
                "WEB_INTERACTION_REQUIRED", "VIRTUAL_ROUTE_NOT_FOUND", "UNMAPPED_LOCAL_URL" }),
    };

    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();
    [GeneratedRegex(@"\Ahttp://127\.0\.0\.1:([1-9][0-9]{0,4})/?\z", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointPattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static bool ValidMethod(string? method) => method is not null && MethodPattern().IsMatch(method);

    public static Uri ValidateEndpoint(string baseUrl)
    {
        if (baseUrl is null || !EndpointPattern().IsMatch(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || endpoint.Port is < 1 or > 65535)
            throw new BridgeException("INVALID_REQUEST");
        return endpoint;
    }

    internal static void ValidateSession(string sessionId, string requestKey)
    {
        if (sessionId is null || sessionId.Length != 36 || !Guid.TryParseExact(sessionId, "D", out _)
            || requestKey is null || !KeyPattern().IsMatch(requestKey))
            throw new BridgeException("INVALID_REQUEST");
    }

    public static HashSet<string> ValidateDescription(JsonElement description, string product,
        IReadOnlyList<RuntimeModule> expectedModules)
    {
        try
        {
            BridgeCodec.ValidateJSONValue(description);
            if (description.ValueKind != JsonValueKind.Object || description.EnumerateObject().Count() != 3
                || description.GetProperty("protocol").GetString() != Name
                || description.GetProperty("product").GetString() != product
                || expectedModules.Count != Modules.Count) throw new BridgeException("INVALID_RESPONSE");
            var remaining = expectedModules.ToDictionary(module => module.Id, module => module.Version, StringComparer.Ordinal);
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in description.GetProperty("modules").EnumerateArray())
            {
                var id = module.GetProperty("id").GetString()!;
                if (module.EnumerateObject().Count() != 4 || !remaining.Remove(id, out var version)
                    || !Modules.TryGetValue(id, out var contract) || version != contract.Version
                    || module.GetProperty("version").GetString() != version
                    || module.GetProperty("contract").GetString() != contract.Name)
                    throw new BridgeException("INVALID_RESPONSE");
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in module.GetProperty("methods").EnumerateArray())
                {
                    var method = item.GetString()!;
                    if (!declared.Add(method) || !methods.Add(method)) throw new BridgeException("INVALID_RESPONSE");
                }
                if (!declared.SetEquals(contract.Operations.Select(operation => contract.Prefix + operation)))
                    throw new BridgeException("INVALID_RESPONSE");
            }
            if (remaining.Count != 0) throw new BridgeException("INVALID_RESPONSE");
            return methods;
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }

    internal static JsonElement ValidateEnvelope(ReadOnlyMemory<byte> bytes, string contract, IReadOnlySet<string> codes)
    {
        try
        {
            var value = BridgeCodec.ParseJSON(bytes);
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3)
                throw new BridgeException("INVALID_RESPONSE");
            var ok = value.GetProperty("ok").GetBoolean();
            var actualContract = value.GetProperty("contract").GetString();
            if (actualContract == Name && contract != Name)
            {
                if (ok) throw new BridgeException("INVALID_RESPONSE");
                codes = PublicCodes;
            }
            else if (actualContract != contract) throw new BridgeException("INVALID_RESPONSE");
            if (ok) BridgeCodec.ValidateJSONValue(value.GetProperty("value"));
            else if (!codes.Contains(value.GetProperty("code").GetString()!)) throw new BridgeException("INVALID_RESPONSE");
            return value;
        }
        catch { throw new BridgeException("INVALID_RESPONSE"); }
    }
}
