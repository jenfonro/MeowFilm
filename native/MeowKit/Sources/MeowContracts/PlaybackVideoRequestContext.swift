import Foundation

/// Validation errors never contain an offending header name or value.
public enum PlaybackVideoRequestContextError: Error, Equatable, LocalizedError, Sendable {
    case invalidHeaders
    public var errorDescription: String? { "媒体请求头无效" }
}

/// Immutable HTTP request context for one native media instance, not a cookie jar,
/// UI snapshot, log payload or persistent credential store.
public struct PlaybackVideoRequestContext: Sendable, CustomStringConvertible,
    CustomDebugStringConvertible, CustomReflectable {
    public static let empty = try! PlaybackVideoRequestContext()

    /// Native adapter access only. Lowercase names, unchanged values, including
    /// the explicit Cookie request value. The receiver must not log these fields.
    public let headers: [String: String]
    public var cookie: String? { headers["cookie"] }

    /// Cookie may be supplied here or in headers, never both. No Set-Cookie
    /// parsing, shared cookie storage, automatic updates or network I/O.
    public init(headers: [String: String] = [:], cookie: String? = nil) throws {
        var normalized: [String: String] = [:]
        for (name, value) in headers {
            guard !name.isEmpty, name.utf8.allSatisfy({ Self.headerNameBytes.contains($0) }),
                  Self.isValidValue(value) else { throw PlaybackVideoRequestContextError.invalidHeaders }
            let key = name.lowercased()
            guard normalized[key] == nil else { throw PlaybackVideoRequestContextError.invalidHeaders }
            normalized[key] = value
        }
        if let cookie {
            guard normalized["cookie"] == nil, Self.isValidValue(cookie) else {
                throw PlaybackVideoRequestContextError.invalidHeaders
            }
            normalized["cookie"] = cookie
        }
        self.headers = normalized
    }

    private static let headerNameBytes = Set(
        "!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".utf8)

    private static func isValidValue(_ value: String) -> Bool {
        value.utf8.allSatisfy { $0 == 9 || ($0 >= 32 && $0 != 127) }
    }

    public var description: String { "PlaybackVideoRequestContext(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}
