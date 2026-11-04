// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// v1 data-only NetEase home actions. These are not URLs, playable tracks,
/// account grants, or queue sessions. Executors must validate the descriptor
/// and resolve it in the current account scope before mutating playback.
public enum MusicHomeAction: Codable, Hashable, Sendable {
    public enum FMMode: String, Codable, Hashable, Sendable {
        case `default` = "DEFAULT", familiar = "FAMILIAR", explore = "EXPLORE"
        case puzzle = "PUZZLE_MODE_RCMD", scene = "SCENE_RCMD"
    }
    public enum FMEntry: String, Codable, Hashable, Sendable { case channel }
    public enum DailyBranch: String, Codable, Hashable, Sendable { case `default`, style }

    case fm(mode: FMMode, submode: String, entry: FMEntry?)
    /// Exactly one selector: a positive safe rule ID, or a numeric/official tab.
    case ruleFM(ruleID: String, tabTagID: String, sourceName: String)
    /// Ordered song seeds, including repetitions; never artist/playlist seeds.
    case similar(sourceIDs: [String], sourceName: String)
    case daily(branch: DailyBranch)
    /// Deliberately carries no resource/playlist/song ID. The account owner
    /// resolves the current user's real liked playlist and seed at execution.
    case heartbeat
    /// A finite ordered collection, not a playlist ID or an endless FM stream.
    case songCollection(ids: [String])

    public static let maximumSimilarSeeds = 20

    /// Reading capability, not execution authorization. FM has an anonymous
    /// provider path; personal recommendations require the account owner.
    public var requiresAccount: Bool {
        switch self {
        case .daily, .heartbeat, .similar: return true
        case .fm, .ruleFM, .songCollection: return false
        }
    }

    /// Structural validation only. A syntactically valid submode still needs
    /// the executor's supported-mode check; it must never fall back to DEFAULT.
    public func validate() throws {
        switch self {
        case .fm(_, let submode, _):
            guard submode.utf8.count <= 64, submode.utf8.allSatisfy({
                (65...90).contains($0) || (48...57).contains($0) || $0 == 95
            }) else { throw MusicServiceError.invalidRequest }
        case .ruleFM(let ruleID, let tabTagID, let sourceName):
            let tabs = ["rap", "electronic", "sleep", "chinese", "occident", "korea", "classical"]
            let validSelector = ruleID.isEmpty
                ? Self.safeID(tabTagID) || tabs.contains(tabTagID)
                : Self.safeID(ruleID) && tabTagID.isEmpty
            guard validSelector else { throw MusicServiceError.invalidRequest }
            try Self.validateName(sourceName)
        case .similar(let sourceIDs, let sourceName):
            guard sourceIDs.count <= Self.maximumSimilarSeeds else { throw MusicServiceError.limit }
            guard !sourceIDs.isEmpty, sourceIDs.allSatisfy(Self.safeID) else { throw MusicServiceError.invalidRequest }
            try Self.validateName(sourceName)
        case .songCollection(let ids):
            guard !ids.isEmpty, ids.allSatisfy({ Self.positiveID($0, digits: 18) }) else {
                throw MusicServiceError.invalidRequest
            }
        case .daily, .heartbeat: break
        }
    }

    private static func positiveID(_ value: String, digits: Int) -> Bool {
        let bytes = value.utf8
        return (1...digits).contains(bytes.count) && bytes.first.map { (49...57).contains($0) } == true &&
            bytes.allSatisfy { (48...57).contains($0) }
    }
    private static func safeID(_ value: String) -> Bool {
        positiveID(value, digits: 16) && (UInt64(value) ?? UInt64.max) <= 9_007_199_254_740_991
    }
    private static func validateName(_ value: String) throws {
        guard value.utf8.count <= 8192 else { throw MusicServiceError.limit }
        guard !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else {
            throw MusicServiceError.invalidRequest
        }
    }

    // Explicit descriptor keys preserve the reference home-snapshot action
    // shape without persisting arbitrary parameters or native URLs/DSL.
    private enum CodingKeys: String, CodingKey {
        case type, provider, mode, submode, entry, ruleId, tabTagId, sourceName
        case sourceType, sourceIds, branch, ids
    }
    public init(from decoder: Decoder) throws {
        let data = try decoder.container(keyedBy: CodingKeys.self)
        switch try data.decode(String.self, forKey: .type) {
        case "fm":
            let provider = try data.decodeIfPresent(String.self, forKey: .provider) ?? ""
            if provider == "rule" {
                self = .ruleFM(ruleID: try data.decode(String.self, forKey: .ruleId),
                               tabTagID: try data.decode(String.self, forKey: .tabTagId),
                               sourceName: try data.decodeIfPresent(String.self, forKey: .sourceName) ?? "")
            } else {
                guard provider.isEmpty else {
                    throw DecodingError.dataCorruptedError(forKey: .provider, in: data, debugDescription: "Unknown FM provider")
                }
                self = .fm(mode: try data.decode(FMMode.self, forKey: .mode),
                           submode: try data.decode(String.self, forKey: .submode),
                           entry: try data.decodeIfPresent(FMEntry.self, forKey: .entry))
            }
        case "similar":
            guard try data.decode(String.self, forKey: .sourceType) == "song" else {
                throw DecodingError.dataCorruptedError(forKey: .sourceType, in: data, debugDescription: "Expected song seeds")
            }
            self = .similar(sourceIDs: try Self.decodeIDs(data, key: .sourceIds, maximum: Self.maximumSimilarSeeds),
                            sourceName: try data.decodeIfPresent(String.self, forKey: .sourceName) ?? "")
        case "daily": self = .daily(branch: try data.decode(DailyBranch.self, forKey: .branch))
        case "heartbeat":
            // Old snapshots may have playlistId/resourceId. Never retain them.
            self = .heartbeat
        case "song-collection":
            self = .songCollection(ids: try Self.decodeIDs(data, key: .ids, maximum: nil))
        default:
            throw DecodingError.dataCorruptedError(forKey: .type, in: data, debugDescription: "Unknown home action")
        }
        try validate()
    }
    private static func decodeIDs(_ data: KeyedDecodingContainer<CodingKeys>, key: CodingKeys,
                                  maximum: Int?) throws -> [String] {
        var array = try data.nestedUnkeyedContainer(forKey: key), result: [String] = []
        if let maximum, let count = array.count, count > maximum { throw MusicServiceError.limit }
        while !array.isAtEnd {
            if let maximum, result.count >= maximum { throw MusicServiceError.limit }
            result.append(try array.decode(String.self))
        }
        return result
    }
    public func encode(to encoder: Encoder) throws {
        try validate()
        var data = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .fm(let mode, let submode, let entry):
            try data.encode("fm", forKey: .type)
            try data.encode(mode, forKey: .mode)
            try data.encode(submode, forKey: .submode)
            try data.encodeIfPresent(entry, forKey: .entry)
        case .ruleFM(let ruleID, let tabTagID, let sourceName):
            try data.encode("fm", forKey: .type)
            try data.encode("rule", forKey: .provider)
            try data.encode(ruleID, forKey: .ruleId)
            try data.encode(tabTagID, forKey: .tabTagId)
            try data.encode(sourceName, forKey: .sourceName)
        case .similar(let sourceIDs, let sourceName):
            try data.encode("similar", forKey: .type)
            try data.encode("song", forKey: .sourceType)
            try data.encode(sourceIDs, forKey: .sourceIds)
            if !sourceName.isEmpty { try data.encode(sourceName, forKey: .sourceName) }
        case .daily(let branch):
            try data.encode("daily", forKey: .type)
            try data.encode(branch, forKey: .branch)
        case .heartbeat: try data.encode("heartbeat", forKey: .type)
        case .songCollection(let ids):
            try data.encode("song-collection", forKey: .type)
            try data.encode(ids, forKey: .ids)
        }
    }
}
