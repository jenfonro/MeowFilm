import Foundation

/// Native media input, never a UI snapshot, log payload or persisted history.
/// Required HTTP headers have already been applied by the runtime media owner.
public struct PlaybackVideoInput: Sendable, CustomStringConvertible, CustomDebugStringConvertible, CustomReflectable {
    public enum Format: String, Sendable {
        case automatic, progressive, hls, dash

        public init(runtimeValue: String?) {
            switch runtimeValue?.lowercased() {
            case "hls", "m3u8", "application/vnd.apple.mpegurl", "application/x-mpegurl": self = .hls
            case "dash", "mpd", "application/dash+xml": self = .dash
            case "mp4", "mkv", "webm", "progressive": self = .progressive
            default: self = .automatic
            }
        }
    }

    public let resourceID: String
    public let title: String
    public let url: URL
    public let format: Format
    public let startPosition: TimeInterval
    public let decryption: PlaybackVideoDecryption?

    public init(resourceID: String, url: URL, title: String, format: Format = .automatic,
                startPosition: TimeInterval = 0,
                decryption: PlaybackVideoDecryption? = nil) throws {
        guard !resourceID.isEmpty, startPosition.isFinite, startPosition >= 0,
              ["http", "https", "file"].contains(url.scheme?.lowercased() ?? ""),
              url.isFileURL || url.host?.isEmpty == false else { throw ContractError.invalidURL }
        self.resourceID = resourceID; self.url = url; self.title = title
        self.format = format; self.startPosition = startPosition
        self.decryption = decryption
    }

    public var description: String { "PlaybackVideoInput(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["format": format.rawValue]) }
}

/// Only locally supplied, legally obtained ClearKey material. This is not a
/// license client or a Widevine/PlayReady/FairPlay CDM.
public final class PlaybackVideoDecryption: @unchecked Sendable, CustomStringConvertible,
    CustomDebugStringConvertible, CustomReflectable {
    private let keys: [String: Data]

    /// Accepts the reference client's hex or base64url 128-bit KID/key pairs.
    /// Construction is passive; no license URL, HTTP headers or network I/O.
    public init(clearKeys: [String: String]) throws {
        guard !clearKeys.isEmpty else { throw ContractError.capabilityUnavailable("ClearKey") }
        var normalized: [String: Data] = [:]
        for (kid, key) in clearKeys {
            guard let id = Self.bytes(kid), let value = Self.bytes(key) else {
                throw ContractError.capabilityUnavailable("ClearKey")
            }
            let hex = id.map { String(format: "%02x", $0) }.joined()
            guard normalized[hex] == nil else { throw ContractError.capabilityUnavailable("ClearKey") }
            normalized[hex] = value
        }
        keys = normalized
    }

    /// Native adapter access only. The receiver must not publish or retain
    /// another persistent copy; the decoder owns its private transient copy.
    public func withKeyMaterial<T>(_ body: ([String: Data]) throws -> T) rethrows -> T { try body(keys) }

    private static func bytes(_ input: String) -> Data? {
        if input.utf8.count == 32, input.utf8.allSatisfy({
            (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
        }) {
            var bytes = Data()
            var index = input.startIndex
            for _ in 0..<16 {
                let end = input.index(index, offsetBy: 2)
                guard let byte = UInt8(input[index..<end], radix: 16) else { return nil }
                bytes.append(byte); index = end
            }
            return bytes
        }
        let token = input.trimmingCharacters(in: CharacterSet(charactersIn: "="))
        guard token.utf8.count == 22, token.utf8.allSatisfy({
            (65...90).contains($0) || (97...122).contains($0) || (48...57).contains($0) || $0 == 45 || $0 == 95
        }), let bytes = Data(base64Encoded: token.replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/") + "=="), bytes.count == 16 else { return nil }
        return bytes
    }

    public var description: String { "PlaybackVideoDecryption(redacted)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}
