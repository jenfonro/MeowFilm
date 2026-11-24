// SPDX-License-Identifier: GPL-3.0-only
// Dual-platform reciprocal-rank fusion matching Windows MusicRankFusion.cs.
import Foundation

public struct MusicSearchSong: Equatable, Sendable, Identifiable {
    public let id: String
    public let track: MusicRemoteTrack
    /// Each variant retains the real reference registered by its native owner.
    public let variants: [MusicRemoteTrack]
    public let matchedPlatforms: [String]
    public let score: Double
}

public enum MusicSearchFusion {
    public struct SongBatch: Sendable {
        public let platform: MusicPublicBrowsePlatform
        public let items: [MusicRemoteTrack]
        public let ranks: [String: Double]
        public init(platform: MusicPublicBrowsePlatform, items: [MusicRemoteTrack], ranks: [String: Double] = [:]) {
            self.platform = platform; self.items = items; self.ranks = ranks
        }
    }
    public struct PlaylistBatch: Sendable {
        public let platform: MusicPublicBrowsePlatform
        public let items: [MusicPublicBrowseCollection]
        public let ranks: [String: Double]
        public init(platform: MusicPublicBrowsePlatform, items: [MusicPublicBrowseCollection], ranks: [String: Double] = [:]) {
            self.platform = platform; self.items = items; self.ranks = ranks
        }
    }

    public static func sourceIdentity(_ track: MusicRemoteTrack, platform: MusicPublicBrowsePlatform) -> String {
        track.sources.first(where: { $0.platform == platform.rawValue })?.sourceKey ?? track.songId
    }

    public static func songs(_ batches: [SongBatch],
                             platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicSearchSong] {
        fuse(batches, displayed: [], platformOrder: platformOrder)
    }

    public static func appendSongs(displayed: [MusicSearchSong], adding batches: [SongBatch],
                                   platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicSearchSong] {
        fuse(batches, displayed: displayed, platformOrder: platformOrder)
    }

    /// Dual 32-bit FNV-1a hash matching Windows `NativeMusicSong.Hash` over UTF-16 code units.
    static func fnvHash(_ value: String) -> String {
        var a: UInt32 = 2_166_136_261
        var b: UInt32 = 3_339_675_911
        for unit in value.utf16 {
            let code = UInt32(unit)
            a = (a ^ code) &* 16_777_619
            b = (b ^ code) &* 2_246_822_519
        }
        return String(format: "%08x%08x", a, b)
    }

    private static func ordinalLess(_ lhs: String, _ rhs: String) -> Bool {
        lhs.utf16.lexicographicallyPrecedes(rhs.utf16)
    }

    /// Platform results have already been adopted by the sole repository.
    /// Retained (`displayed`) rows are locked verbatim in position and identity;
    /// new rows are ranked by RRF score -> platform priority -> FNV hash(sourceKeys) -> songId.
    private static func fuse(_ batches: [SongBatch], displayed: [MusicSearchSong],
                             platformOrder: [MusicPublicBrowsePlatform]) -> [MusicSearchSong] {
        struct Candidate {
            let platform: String
            let priority: Int
            let rank: Double
            let sourceKey: String
            let track: MusicRemoteTrack
        }
        struct Group {
            let id: String
            let platform: String
            let priority: Int
            var variants: [MusicRemoteTrack]
            var ranks: [String: Double]
        }

        let order = Dictionary(platformOrder.enumerated().map { ($0.element.rawValue, $0.offset) }, uniquingKeysWith: min)
        let fallbackPriority = platformOrder.count
        func platformPriority(_ platform: String) -> Int {
            order[platform] ?? fallbackPriority
        }
        func trackPriority(_ track: MusicRemoteTrack) -> Int {
            track.sources.map { platformPriority($0.platform) }.min() ?? fallbackPriority
        }

        let retainedIDs = Set(displayed.map(\.id))
        var candidates: [Candidate] = []
        for batch in batches {
            let platform = batch.platform.rawValue
            let priority = platformPriority(platform)
            for (offset, track) in batch.items.enumerated() {
                if retainedIDs.contains(track.songId) { continue }
                let sourceKey = sourceIdentity(track, platform: batch.platform)
                let rank = max(1, batch.ranks[sourceKey] ?? Double(offset) + 1)
                candidates.append(Candidate(platform: platform, priority: priority, rank: rank,
                                            sourceKey: sourceKey, track: track))
            }
        }
        candidates.sort { lhs, rhs in
            if lhs.priority != rhs.priority { return lhs.priority < rhs.priority }
            if lhs.rank != rhs.rank { return lhs.rank < rhs.rank }
            let leftKey = lhs.track.songId + ":" + lhs.sourceKey
            let rightKey = rhs.track.songId + ":" + rhs.sourceKey
            if leftKey != rightKey { return ordinalLess(leftKey, rightKey) }
            return ordinalLess(lhs.track.ref.id, rhs.track.ref.id)
        }

        var groups: [Group] = []
        var positions: [String: Int] = [:]
        for candidate in candidates {
            let songID = candidate.track.songId
            let index: Int
            if let existing = positions[songID] {
                index = existing
            } else {
                index = groups.count
                positions[songID] = index
                groups.append(Group(id: songID, platform: candidate.platform, priority: candidate.priority,
                                    variants: [], ranks: [:]))
            }
            if !groups[index].variants.contains(candidate.track) {
                groups[index].variants.append(candidate.track)
            }
            let existingRank = groups[index].ranks[candidate.platform] ?? candidate.rank
            groups[index].ranks[candidate.platform] = min(existingRank, candidate.rank)
        }

        struct Ranked {
            let song: MusicSearchSong
            let priority: Int
            let tie: String
        }
        let ranked = groups.map { group -> Ranked in
            let variants = group.variants.sorted { lhs, rhs in
                let a = trackPriority(lhs), b = trackPriority(rhs)
                if a != b { return a < b }
                let leftKey = lhs.sources.first?.sourceKey ?? lhs.songId
                let rightKey = rhs.sources.first?.sourceKey ?? rhs.songId
                return ordinalLess(leftKey, rightKey)
            }
            let representative = variants[0]
            let platforms = Set(variants.flatMap { $0.sources.map(\.platform) } + Array(group.ranks.keys)).sorted()
            let score = group.ranks.values.reduce(0.0) { $0 + 1.0 / (5.0 + $1) }
            let tieInput: String
            let sourceKeys = representative.sources.map(\.sourceKey).sorted(by: ordinalLess)
            if !sourceKeys.isEmpty {
                tieInput = sourceKeys.joined(separator: "\n")
            } else {
                tieInput = group.id
            }
            let song = MusicSearchSong(id: group.id, track: representative, variants: variants,
                                       matchedPlatforms: platforms, score: score)
            return Ranked(song: song, priority: group.priority, tie: fnvHash(tieInput))
        }

        let added = ranked.sorted { lhs, rhs in
            if lhs.song.score != rhs.song.score { return lhs.song.score > rhs.song.score }
            if lhs.priority != rhs.priority { return lhs.priority < rhs.priority }
            if lhs.tie != rhs.tie { return ordinalLess(lhs.tie, rhs.tie) }
            return ordinalLess(lhs.song.id, rhs.song.id)
        }.map(\.song)

        return displayed + added
    }

    private static func jsonQuoted(_ value: String) -> String {
        var result = "\""
        for c in value.unicodeScalars {
            switch c.value {
            case 34: result += "\\\""
            case 92: result += "\\\\"
            case 8: result += "\\b"
            case 9: result += "\\t"
            case 10: result += "\\n"
            case 12: result += "\\f"
            case 13: result += "\\r"
            case 0..<32: result += String(format: "\\u%04x", c.value)
            default: result.unicodeScalars.append(c)
            }
        }
        return result + "\""
    }

    private static func playlistTieKey(_ item: MusicPublicBrowseCollection) -> String {
        "[" + jsonQuoted(item.platform.rawValue) + "," + jsonQuoted(item.nativeID) + "]"
    }

    public static func playlists(_ batches: [PlaylistBatch],
                                 platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicPublicBrowseCollection] {
        let order = Dictionary(platformOrder.enumerated().map { ($0.element, $0.offset) }, uniquingKeysWith: min)
        let fallbackPriority = platformOrder.count
        struct RankedPlaylist {
            let value: MusicPublicBrowseCollection
            let score: Double
            let priority: Int
            let tie: String
            let key: String
        }
        var grouped: [String: (value: MusicPublicBrowseCollection, rank: Double)] = [:]
        var keys: [String] = []
        for batch in batches {
            for (index, item) in batch.items.enumerated() {
                let rank = max(1, batch.ranks[item.id] ?? Double(index) + 1)
                if let existing = grouped[item.id] {
                    if rank < existing.rank {
                        grouped[item.id] = (existing.value, rank)
                    }
                } else {
                    grouped[item.id] = (item, rank)
                    keys.append(item.id)
                }
            }
        }
        let ranked: [RankedPlaylist] = keys.compactMap { id in
            guard let entry = grouped[id] else { return nil }
            let key = playlistTieKey(entry.value)
            return RankedPlaylist(
                value: entry.value,
                score: 1.0 / (5.0 + entry.rank),
                priority: order[entry.value.platform] ?? fallbackPriority,
                tie: fnvHash(key),
                key: key
            )
        }
        return ranked.sorted { lhs, rhs in
            if lhs.score != rhs.score { return lhs.score > rhs.score }
            if lhs.priority != rhs.priority { return lhs.priority < rhs.priority }
            if lhs.tie != rhs.tie { return ordinalLess(lhs.tie, rhs.tie) }
            if lhs.key != rhs.key { return ordinalLess(lhs.key, rhs.key) }
            return ordinalLess(lhs.value.id, rhs.value.id)
        }.map(\.value)
    }

    public static func appendPlaylists(displayed: [MusicPublicBrowseCollection], adding batches: [PlaylistBatch],
                                       platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicPublicBrowseCollection] {
        var seen = Set(displayed.map(\.id))
        return displayed + playlists(batches, platformOrder: platformOrder).filter { seen.insert($0.id).inserted }
    }

    public static func sameRecording(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack) -> Bool {
        lhs.songId.utf8.elementsEqual(rhs.songId.utf8)
    }

    public static func isFavorite(_ row: MusicSearchSong, members: [MusicRemoteTrack]) -> Bool {
        members.contains { matchesFavorite($0, row: row) }
    }

    public static func matchesFavorite(_ member: MusicRemoteTrack, row: MusicSearchSong) -> Bool {
        row.variants.contains { sameRecording(member, $0) }
    }
}
