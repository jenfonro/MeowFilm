// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// v1 data-only NetEase home actions. These are not URLs, playable tracks,
/// account grants, or queue sessions. Executors use the current platform or account scope before mutating playback.
public enum MusicHomeAction: Codable, Hashable, Sendable {
    public enum FMMode: String, Codable, Hashable, Sendable {
        case `default` = "DEFAULT", familiar = "FAMILIAR", explore = "EXPLORE"
        case puzzle = "PUZZLE_MODE_RCMD", scene = "SCENE_RCMD"
    }
    public enum FMEntry: String, Codable, Hashable, Sendable { case channel }
    public enum DailyBranch: String, Codable, Hashable, Sendable { case `default`, style }

    case fm(mode: FMMode, submode: String, entry: FMEntry?)
    /// The platform recipe consumes the supplied rule or tab selector.
    case ruleFM(ruleID: String, tabTagID: String, sourceName: String)
    /// Song or artist seeds retain their actual provider identity.
    case similar(sourceIDs: [String], sourceName: String)
    case similarArtist(sourceIDs: [String], sourceName: String)
    case daily(branch: DailyBranch)
    /// Deliberately carries no resource/playlist/song ID. The account owner
    /// resolves the current user's real liked playlist and seed at execution.
    case heartbeat
    case heartbeatSong(songID: String)
    /// A finite ordered collection, not a playlist ID or an endless FM stream.
    case songCollection(ids: [String])

    public var isSimilar: Bool {
        switch self { case .similar, .similarArtist: return true; default: return false }
    }

    public var isHeartbeat: Bool {
        switch self { case .heartbeat, .heartbeatSong: return true; default: return false }
    }

    // Explicit descriptor keys preserve the reference home-snapshot action
    // shape without persisting arbitrary parameters or native URLs/DSL.
    private enum CodingKeys: String, CodingKey {
        case type, provider, mode, submode, entry, ruleId, tabTagId, sourceName
        case sourceType, sourceIds, branch, ids, songId
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
            let type = try data.decode(String.self, forKey: .sourceType)
            let ids = try data.decode([String].self, forKey: .sourceIds)
            let name = try data.decodeIfPresent(String.self, forKey: .sourceName) ?? ""
            switch type {
            case "song": self = .similar(sourceIDs: ids, sourceName: name)
            case "artist": self = .similarArtist(sourceIDs: ids, sourceName: name)
            default: throw MusicServiceError.invalidRequest
            }
        case "daily": self = .daily(branch: try data.decode(DailyBranch.self, forKey: .branch))
        case "heartbeat":
            if let id = try data.decodeIfPresent(String.self, forKey: .songId) { self = .heartbeatSong(songID: id) }
            else { self = .heartbeat }
        case "song-collection":
            self = .songCollection(ids: try data.decode([String].self, forKey: .ids))
        default:
            throw DecodingError.dataCorruptedError(forKey: .type, in: data, debugDescription: "Unknown home action")
        }
    }
    public func encode(to encoder: Encoder) throws {
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
        case .similarArtist(let sourceIDs, let sourceName):
            try data.encode("similar", forKey: .type)
            try data.encode("artist", forKey: .sourceType)
            try data.encode(sourceIDs, forKey: .sourceIds)
            if !sourceName.isEmpty { try data.encode(sourceName, forKey: .sourceName) }
        case .heartbeatSong(let id):
            try data.encode("heartbeat", forKey: .type)
            try data.encode(id, forKey: .songId)
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
