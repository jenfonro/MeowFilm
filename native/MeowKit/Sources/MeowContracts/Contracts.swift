import Foundation

public enum ContractError: Error, Equatable, LocalizedError {
    case invalidIdentifier
    case unsupportedVersion(Int)
    case invalidURL
    case capabilityUnavailable(String)
    case staleSession
    public var errorDescription: String? {
        switch self {
        case .invalidIdentifier: return "标识无效"
        case .unsupportedVersion(let version): return "不支持的数据版本 \(version)"
        case .invalidURL: return "地址无效"
        case .capabilityUnavailable(let name): return "\(name)不可用"
        case .staleSession: return "会话已失效"
        }
    }
}

public struct SourceAddress: Codable, Hashable, Sendable {
    public let installationID: UUID
    public let siteKey: String
    public init(installationID: UUID, siteKey: String) throws {
        guard !siteKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ContractError.invalidIdentifier
        }
        self.installationID = installationID
        self.siteKey = siteKey
    }
    private enum CodingKeys: CodingKey { case installationID, siteKey }
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(installationID: container.decode(UUID.self, forKey: .installationID),
                      siteKey: container.decode(String.self, forKey: .siteKey))
    }
    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(installationID.uuidString.lowercased(), forKey: .installationID)
        try container.encode(siteKey, forKey: .siteKey)
    }
    // A source key is an opaque protocol identifier, not display text. Swift's
    // default String equality canonicalizes Unicode. Keep byte-distinct
    // upstream keys distinct in native requests and Keychain.
    public static func == (lhs: SourceAddress, rhs: SourceAddress) -> Bool {
        lhs.installationID == rhs.installationID && lhs.siteKey.utf8.elementsEqual(rhs.siteKey.utf8)
    }
    public func hash(into hasher: inout Hasher) {
        hasher.combine(installationID)
        hasher.combine(siteKey.utf8.count)
        for byte in siteKey.utf8 { hasher.combine(byte) }
    }
}


public struct PlaybackResource: Sendable, Equatable {
    public let id: String
    public let url: URL
    public let title: String
    public let headers: [String: String]
    public let startPosition: TimeInterval
    public let byteSource: (any PlaybackByteSource)?
    public init(id: String, url: URL, title: String, headers: [String: String] = [:], startPosition: TimeInterval = 0,
                byteSource: (any PlaybackByteSource)? = nil) throws {
        guard !id.isEmpty, ["https", "http", "file"].contains(url.scheme?.lowercased() ?? ""),
              url.user == nil, url.password == nil, startPosition.isFinite, startPosition >= 0 else {
            throw ContractError.invalidURL
        }
        self.id = id; self.url = url; self.title = title
        self.headers = headers; self.startPosition = startPosition
        self.byteSource = byteSource
    }
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.id == rhs.id && lhs.url == rhs.url && lhs.title == rhs.title &&
            lhs.headers == rhs.headers && lhs.startPosition == rhs.startPosition &&
            lhs.byteSource?.id == rhs.byteSource?.id
    }
}

public enum PlaybackPhase: String, Codable, Sendable {
    case idle, preparing, playing, paused, buffering, ended, failed
}

public struct PlaybackSnapshot: Sendable, Equatable {
    public var owner: String?
    public var resourceID: String?
    public var phase: PlaybackPhase
    public var position: TimeInterval
    public var duration: TimeInterval?
    public var error: String?
    public init(owner: String? = nil, resourceID: String? = nil, phase: PlaybackPhase = .idle,
                position: TimeInterval = 0, duration: TimeInterval? = nil, error: String? = nil) {
        self.owner = owner; self.resourceID = resourceID; self.phase = phase
        self.position = position; self.duration = duration; self.error = error
    }
}
