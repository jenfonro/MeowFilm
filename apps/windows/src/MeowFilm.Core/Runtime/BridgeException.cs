namespace MeowFilm.Core.Runtime;

public sealed class BridgeException : Exception
{
    public static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.Ordinal)
    {
        "CANCELLED", "TIMEOUT", "CLOSED", "LIMIT", "INVALID_MESSAGE", "METHOD_UNAVAILABLE", "INTERNAL",
        "INVALID_REQUEST", "METHOD_NOT_FOUND", "PERMISSION_DENIED", "INVALID_RESPONSE",
        "RESOURCE_LIMIT", "NETWORK_ERROR", "UPSTREAM_HTTP", "UPSTREAM_FORMAT",
        "WEB_INTERACTION_REQUIRED", "STORAGE_ERROR", "CORRUPT_STORAGE",
        "SOURCE_INTEGRITY", "SOURCE_ENCODING", "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED",
        "UNSUPPORTED_PROTOCOL", "UNSUPPORTED_ACTION", "UNSUPPORTED_RULE", "UNSUPPORTED_DEPENDENCY",
        "CAPABILITY_UNAVAILABLE", "SOURCE_CLEANUP", "SOURCE_FAILURE", "HTTP_ERROR", "VIRTUAL_ROUTE_NOT_FOUND"
    };

    public string Code { get; }
    public BridgeException(string code) : base(Codes.Contains(code) ? code : "INTERNAL") => Code = Message;
}
