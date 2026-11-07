import Foundation

public struct FilmSourceKey: Codable, Hashable, Sendable {
    public let installationId: String
    public let siteKey: String
    public init(installationId: String, siteKey: String) {
        self.installationId = installationId; self.siteKey = siteKey
    }

    // Cat identities use exact protocol strings, not Unicode canonical equivalence.
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.installationId.utf8.elementsEqual(rhs.installationId.utf8) &&
            lhs.siteKey.utf8.elementsEqual(rhs.siteKey.utf8)
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(installationId.utf8.count)
        for byte in installationId.utf8 { hasher.combine(byte) }
        hasher.combine(siteKey.utf8.count)
        for byte in siteKey.utf8 { hasher.combine(byte) }
    }

    static func ordered(_ lhs: Self, _ rhs: Self) -> Bool {
        if !lhs.installationId.utf8.elementsEqual(rhs.installationId.utf8) {
            return lhs.installationId.utf8.lexicographicallyPrecedes(rhs.installationId.utf8)
        }
        return lhs.siteKey.utf8.lexicographicallyPrecedes(rhs.siteKey.utf8)
    }
}

public struct FilmSourceSite: Codable, Equatable, Sendable {
    public let key: String
    public let name: String
    public let api: String
    public let actions: [String]
    public let methods: [String: String]
    public init(key: String, name: String, api: String, actions: [String], methods: [String: String]) {
        self.key = key; self.name = name; self.api = api; self.actions = actions; self.methods = methods
    }

    func identity(installationId: String) -> FilmSourceKey {
        FilmSourceKey(installationId: installationId, siteKey: key)
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.key.utf8.elementsEqual(rhs.key.utf8) && lhs.name == rhs.name && lhs.api == rhs.api &&
            lhs.actions == rhs.actions && lhs.methods == rhs.methods
    }
}

public struct FilmCatSource: Codable, Equatable, Sendable {
    public let installationId: String
    public var name: String
    public let sourceType: String
    public let sourceUrl: String?
    public let enabled: Bool
    public let importedAt: Int64?
    public let updatedAt: Int64?
    public let updateState: String
    public let updateError: String?
    public var generation: Int64
    public var state: String
    public let sourceHash: String?
    public var error: String?
    public var sites: [FilmSourceSite]
    public init(installationId: String, name: String, sourceType: String, sourceUrl: String? = nil,
                enabled: Bool = true, importedAt: Int64? = nil, updatedAt: Int64? = nil,
                updateState: String = "idle", updateError: String? = nil, generation: Int64 = 0,
                state: String = "stopped", sourceHash: String? = nil, error: String? = nil,
                sites: [FilmSourceSite] = []) {
        self.installationId = installationId; self.name = name; self.sourceType = sourceType
        self.sourceUrl = sourceUrl; self.enabled = enabled; self.importedAt = importedAt
        self.updatedAt = updatedAt; self.updateState = updateState; self.updateError = updateError
        self.generation = generation; self.state = state; self.sourceHash = sourceHash
        self.error = error; self.sites = sites
    }
    public var modifiedAt: Int64? { updatedAt ?? importedAt }
    public var canRestart: Bool { enabled }
    public var canDetect: Bool { enabled && state == "ready" && generation > 0 }
}

public struct FilmVideoOptions: Codable, Equatable, Sendable {
    public var enabled: Bool
    public var homeVisible: Bool
    public var searchEnabled: Bool
    public var aggregateImages: Bool
    public init(enabled: Bool = true, homeVisible: Bool = true,
                searchEnabled: Bool = true, aggregateImages: Bool = false) {
        self.enabled = enabled; self.homeVisible = homeVisible
        self.searchEnabled = searchEnabled; self.aggregateImages = aggregateImages
    }
}

public struct FilmSourceProbeStep: Codable, Equatable, Sendable {
    public let operation: String
    public let state: String
    public let error: String?
    public init(_ operation: String, _ state: String, _ error: String? = nil) {
        self.operation = operation; self.state = state; self.error = error
    }
}

public struct FilmSourceDetection: Codable, Equatable, Sendable {
    public let result: String
    public let checkedAt: Int64
    public let playbackResolved: Bool
    public let steps: [FilmSourceProbeStep]
    public init(result: String, checkedAt: Int64, playbackResolved: Bool, steps: [FilmSourceProbeStep]) {
        self.result = result; self.checkedAt = checkedAt; self.playbackResolved = playbackResolved; self.steps = steps
    }
    public var mediaPlaybackVerified: Bool { false }
    public var error: String? {
        let text = steps.compactMap { step in step.error.map { "\(step.operation): \($0)" } }.joined(separator: "  ")
        return text.isEmpty ? nil : text
    }
}

public struct FilmVideoSource: Codable, Equatable, Sendable {
    public let key: FilmSourceKey
    public var name: String
    public var api: String
    public var actions: [String]
    public var methods: [String: String]
    public var options: FilmVideoOptions
    public var order: Int
    public var detection: FilmSourceDetection?
    public init(key: FilmSourceKey, name: String, api: String, actions: [String], methods: [String: String],
                options: FilmVideoOptions = .init(), order: Int = 0, detection: FilmSourceDetection? = nil) {
        self.key = key; self.name = name; self.api = api; self.actions = actions; self.methods = methods
        self.options = options; self.order = order; self.detection = detection
    }
}

public struct FilmSourceSnapshot: Equatable, Sendable {
    public let cats: [FilmCatSource]
    public let videos: [FilmVideoSource]
    public let runtimeError: String?
    public init(cats: [FilmCatSource] = [], videos: [FilmVideoSource] = [], runtimeError: String? = nil) {
        self.cats = cats; self.videos = videos; self.runtimeError = runtimeError
    }
}

public struct FilmSourceError: Error, Equatable, Sendable, LocalizedError {
    public let code: String
    public init(code: String) { self.code = code }
    public var errorDescription: String? { code }
}
