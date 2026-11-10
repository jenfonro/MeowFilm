import Foundation

public enum RuntimeModule: String, Sendable {
    case music, cat, source, tmdb, netease

    public var contract: String {
        switch self {
        case .music: return "music.script.v3"
        case .cat: return "cat.script.v2"
        case .source: return "source.runtime.v2"
        case .tmdb: return "tmdb.data.v1"
        case .netease: return "netease.token.v1"
        }
    }

    var codes: Set<String> {
        switch self {
        case .music: return Self.musicCodes
        case .cat: return Self.catCodes
        case .source: return Self.sourceCodes
        case .tmdb: return Self.metadataCodes
        case .netease: return Self.neteaseCodes
        }
    }

    static let publicCodes: Set<String> = [
        "INVALID_REQUEST", "METHOD_NOT_FOUND", "PERMISSION_DENIED",
        "CLOSED", "CANCELLED", "TIMEOUT", "INVALID_RESPONSE"
    ]
    private static let neteaseCodes: Set<String> = [
        "INVALID_REQUEST", "CLOSED", "CANCELLED", "TIMEOUT", "NETWORK_ERROR",
        "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "SDK_FAILURE", "SDK_CLEANUP"
    ]
    private static let metadataCodes: Set<String> = [
        "INVALID_REQUEST", "CLOSED", "CANCELLED", "NETWORK_ERROR", "UPSTREAM_HTTP",
        "UPSTREAM_FORMAT", "WEB_INTERACTION_REQUIRED", "STORAGE_ERROR", "CORRUPT_STORAGE"
    ]
    private static let musicCodes: Set<String> = [
        "INVALID_REQUEST", "CLOSED", "CANCELLED", "LIMIT", "SCRIPT_INTEGRITY",
        "SCRIPT_ENCODING", "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED",
        "CAPABILITY_UNAVAILABLE", "PERMISSION_DENIED", "TIMEOUT", "SCRIPT_CLEANUP", "SCRIPT_FAILURE",
        "SCRIPT_PROTOCOL_ERROR", "NETWORK_ERROR", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "STORAGE_ERROR",
        "CORRUPT_STORAGE", "ROLLBACK_UNAVAILABLE", "INVALID_SOURCE_URL", "SOURCE_DOWNLOAD_FAILED",
        "SOURCE_REDIRECT", "MEDIA_UNAVAILABLE", "INVALID_MEDIA_CANDIDATE", "EXPIRED_MEDIA_CANDIDATE",
        "MEDIA_HEADERS_UNSUPPORTED", "TARGET_CHANGED", "RECORDING_MISMATCH"
    ]
    private static let sourceCodes: Set<String> = [
        "INVALID_REQUEST", "CLOSED", "CANCELLED", "LIMIT", "SOURCE_INTEGRITY",
        "SOURCE_ENCODING", "STALE_SOURCE", "SOURCE_NOT_FOUND", "SOURCE_DISABLED",
        "UNSUPPORTED_PROTOCOL", "UNSUPPORTED_ACTION", "UNSUPPORTED_RULE", "UNSUPPORTED_DEPENDENCY",
        "CAPABILITY_UNAVAILABLE", "PERMISSION_DENIED", "WEB_INTERACTION_REQUIRED", "TIMEOUT",
        "SOURCE_CLEANUP", "SOURCE_FAILURE", "NETWORK_ERROR", "HTTP_ERROR", "INVALID_RESPONSE",
        "STORAGE_ERROR", "VIRTUAL_ROUTE_NOT_FOUND"
    ]
    private static let catCodes: Set<String> = [
        "INVALID_ARGUMENT", "HOST_CLOSED", "CANCELLED", "RESOURCE_LIMIT",
        "SCRIPT_PROTOCOL_ERROR", "STALE_SESSION", "NOT_FOUND", "DISABLED", "ALREADY_EXISTS",
        "SCRIPT_EXCEPTION", "STARTING", "CORRUPT_STORAGE", "SERVICE_ERROR", "TIMEOUT",
        "PERMISSION_DENIED", "CAPABILITY_UNAVAILABLE", "UNSUPPORTED_PROTOCOL", "UNSUPPORTED_ACTION",
        "NETWORK_ERROR", "STORAGE_ERROR", "SCRIPT_CLEANUP", "INVALID_JSON", "UPSTREAM_HTTP_ERROR",
        "UNSUPPORTED_DEPENDENCY", "CLOSED", "STORAGE_CONFLICT", "COMMIT_UNKNOWN", "SERVICE_EXISTS",
        "SERVICE_CLOSED", "ROLLBACK_UNAVAILABLE", "UNSUPPORTED_RULE", "UNSUPPORTED_SCRIPT_ASYNC",
        "HTTP_ERROR", "INVALID_RESPONSE", "UNSUPPORTED_OPTION", "UNSUPPORTED_CRYPTO",
        "WEB_INTERACTION_REQUIRED", "VIRTUAL_ROUTE_NOT_FOUND", "UNMAPPED_LOCAL_URL"
    ]
}
