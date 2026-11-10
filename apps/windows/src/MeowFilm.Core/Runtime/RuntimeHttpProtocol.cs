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
        ["tmdb"] = new("0.1.0", "tmdb.data.v1", "tmdb.data.",
            ["settings.get", "settings.update", "request"],
            new(StringComparer.Ordinal) { "INVALID_REQUEST", "CLOSED", "CANCELLED", "NETWORK_ERROR",
                "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "WEB_INTERACTION_REQUIRED", "STORAGE_ERROR", "CORRUPT_STORAGE" }),
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
        ["pan"] = new("0.1.0", "pan.runtime.v1", "pan.",
            ["providers", "settings.get", "settings.set", "settings.clear", "login.start",
                "login.poll", "login.cancel", "list", "prepare", "play", "refresh"],
            new(StringComparer.Ordinal) { "INVALID_REQUEST", "UNSUPPORTED_OPERATION", "NOT_CONFIGURED",
                "AUTH_REQUIRED", "AUTH_FAILED", "CHALLENGE_REQUIRED", "NOT_FOUND", "UPSTREAM_HTTP",
                "UPSTREAM_FORMAT", "UPSTREAM_ERROR", "NETWORK_ERROR", "TIMEOUT", "CANCELLED", "CLOSED",
                "STALE_ACCOUNT", "STORAGE_ERROR", "CORRUPT_STORAGE", "LOGIN_EXPIRED", "PAN_FAILURE" }),
        ["douban"] = new("0.1.0", "douban.data.v1", "douban.data.",
            ["settings.get", "settings.update", "request", "search"],
            new(StringComparer.Ordinal) { "INVALID_REQUEST", "CLOSED", "CANCELLED", "NETWORK_ERROR",
                "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "WEB_INTERACTION_REQUIRED", "STORAGE_ERROR", "CORRUPT_STORAGE" }),
        ["film"] = new("0.1.0", "film.playback.v1", "film.",
            ["session.open", "session.command", "session.snapshot", "session.release",
                "session.attempt", "session.media.release", "settings.get", "settings.set",
                "library.get", "library.remove", "manual.update", "data.search", "data.detail",
                "data.catalog", "data.metadata", "data.metadata.entry", "data.cache.entry", "data.invalidate",
                "rules.get", "rules.set", "rules.restore", "rules.preview", "rules.normalize", "rules.clear",
                "search.open", "search.snapshot", "search.command", "search.release", "search.handoff"],
            new(StringComparer.Ordinal) { "INVALID_REQUEST", "CLOSED", "CANCELLED", "RESOURCE_LIMIT", "NOT_FOUND", "NO_MATCH",
                "SOURCE_NOT_FOUND", "SOURCE_DISABLED", "STALE_SOURCE", "STALE_SESSION", "STALE_ACCOUNT", "STALE_METADATA",
                "NOT_CONFIGURED", "AUTH_REQUIRED", "AUTH_FAILED", "CHALLENGE_REQUIRED",
                "UNSUPPORTED_OPERATION", "CAPABILITY_UNAVAILABLE", "MEDIA_UNAVAILABLE",
                "NETWORK_ERROR", "TIMEOUT", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "UPSTREAM_ERROR",
                "WEB_INTERACTION_REQUIRED", "STORAGE_ERROR", "CORRUPT_STORAGE", "COMMIT_UNKNOWN",
                "NO_EPISODE_MAPPING", "INVALID_PREFERENCES", "INVALID_RULES", "INVALID_DETAIL",
                "INVALID_RESULT", "CACHE_LOAD_FAILED", "STORAGE_CHANGED", "LIBRARY_UPDATE_FAILED",
                "PAN_FAILURE", "SOURCE_FAILURE", "CAT_FAILURE", "SCRIPT_FAILURE", "SCRIPT_PROTOCOL_ERROR", "LOGIN_EXPIRED",
                "MEDIA_BUSY", "MEDIA_CANCELLED", "MEDIA_CLOSED", "MEDIA_CLOSE_FAILED", "MEDIA_DRM_UNSUPPORTED",
                "MEDIA_EMPTY_RESPONSE", "MEDIA_INVALID_DESCRIPTOR", "MEDIA_INVALID_REQUEST",
                "MEDIA_LISTEN_FAILED", "MEDIA_MANIFEST_INVALID", "MEDIA_MANIFEST_LIMIT", "MEDIA_MANIFEST_UNSUPPORTED",
                "MEDIA_METHOD_NOT_ALLOWED", "MEDIA_NOT_FOUND", "MEDIA_PREFETCH_DISABLED", "MEDIA_PREFETCH_HTTP_ERROR",
                "MEDIA_PREFETCH_LIMIT", "MEDIA_PREFETCH_TIMEOUT", "MEDIA_PREFETCH_UNSUPPORTED", "MEDIA_RANGE_UNSUPPORTED",
                "MEDIA_REDIRECT_ERROR", "MEDIA_RESOURCE_CHANGED", "MEDIA_RESOURCE_LIMIT", "MEDIA_UPSTREAM_ERROR",
                "MEDIA_UPSTREAM_TIMEOUT" }),
    };

    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_.-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();
    [GeneratedRegex(@"\A/v1/tmdb/images/(?:original|[wh][1-9][0-9]*)/[A-Za-z0-9_-]+\.[A-Za-z0-9]+\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();
    [GeneratedRegex(@"\A/v1/douban/images/(?!\.{1,2}(?:/|\z))[A-Za-z0-9._~-]+(?:/(?!\.{1,2}(?:/|\z))[A-Za-z0-9._~-]+)*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex DoubanImagePattern();

    public static bool ValidImageReference(string? reference) => ImageProvider(reference) is not null;

    internal static string? ImageProvider(string? reference) => reference is null ? null :
        ImagePattern().IsMatch(reference) ? "tmdb" : DoubanImagePattern().IsMatch(reference) ? "douban" : null;

    public static bool ValidMethod(string? method) => method is not null && MethodPattern().IsMatch(method);

    public static Uri ValidateEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(endpoint.Host) || endpoint.Port is < 1 or > 65535
            || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new BridgeException("INVALID_REQUEST");
        return endpoint;
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
