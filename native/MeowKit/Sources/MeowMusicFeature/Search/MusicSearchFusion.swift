// SPDX-License-Identifier: GPL-3.0-only
// Native DTO projection of iWebPlus rank-fusion.js.
import Foundation

public struct MusicSearchSong: Sendable, Identifiable {
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
        public let ranks: [String: Int]
        public init(platform: MusicPublicBrowsePlatform, items: [MusicRemoteTrack], ranks: [String: Int] = [:]) {
            self.platform = platform; self.items = items; self.ranks = ranks
        }
    }
    public struct PlaylistBatch: Sendable {
        public let platform: MusicPublicBrowsePlatform
        public let items: [MusicPublicBrowseCollection]
        public let ranks: [String: Int]
        public init(platform: MusicPublicBrowsePlatform, items: [MusicPublicBrowseCollection], ranks: [String: Int] = [:]) {
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
    /// First and later rounds use the same all-pairs grouping. Existing groups
    /// retain identity, score and position; only new groups are ranked/appended.
    private static func fuse(_ batches: [SongBatch], displayed: [MusicSearchSong],
                             platformOrder: [MusicPublicBrowsePlatform]) -> [MusicSearchSong] {
        struct Row {
            let track: MusicRemoteTrack
            let evidence: MusicRecordingIdentity
            let platform: String
            let rank: Int
            let key: String
        }
        struct Group {
            let id: String
            var variants: [MusicRemoteTrack]
            var evidence: [MusicRecordingIdentity]
            var ranks: [String: Int]
            let displayedScore: Double?
        }
        enum IdentityKey: Hashable {
            case fingerprint(String)
            case source(Data)
        }
        func keys(_ track: MusicRemoteTrack) -> Set<IdentityKey> {
            let fingerprint = MusicRecordingIdentity(track).fingerprint
            var values = Set(track.sources.map { IdentityKey.source(Data($0.sourceKey.utf8)) })
            if !fingerprint.isEmpty { values.insert(.fingerprint(fingerprint)) }
            return values
        }
        let order = Dictionary(platformOrder.enumerated().map { ($0.element.rawValue, $0.offset) },
                               uniquingKeysWith: min)
        func priority(_ track: MusicRemoteTrack) -> Int {
            track.sources.map { order[$0.platform] ?? Int.max }.min() ?? Int.max
        }
        var groups = displayed.map {
            Group(id: $0.id, variants: $0.variants, evidence: $0.variants.map(MusicRecordingIdentity.init),
                  ranks: [:], displayedScore: $0.score)
        }
        var byIdentity: [IdentityKey: Set<Int>] = [:], occupied = Set(groups.map(\.id))
        for (index, group) in groups.enumerated() {
            for track in group.variants { for key in keys(track) { byIdentity[key, default: []].insert(index) } }
        }
        let rows = batches.flatMap { batch in
            batch.items.enumerated().map { index, track in
                let key = sourceIdentity(track, platform: batch.platform)
                return Row(track: track, evidence: .init(track), platform: batch.platform.rawValue,
                           rank: max(1, batch.ranks[key] ?? index + 1), key: key)
            }
        }.sorted {
            let lhs = $0.track.songId + ":" + $0.key, rhs = $1.track.songId + ":" + $1.key
            if lhs != rhs { return lhs.utf16.lexicographicallyPrecedes(rhs.utf16) }
            if $0.evidence.descriptorKey != $1.evidence.descriptorKey {
                return $0.evidence.descriptorKey < $1.evidence.descriptorKey
            }
            return $0.rank < $1.rank
        }
        for row in rows {
            let rowKeys = keys(row.track)
            let candidates = rowKeys.reduce(into: Set<Int>()) { $0.formUnion(byIdentity[$1] ?? []) }.sorted()
            let matching = candidates.first { index in
                groups[index].evidence.allSatisfy { $0.isCompatible(with: row.evidence) }
            }
            let index: Int
            if let matching { index = matching }
            else {
                var id = row.track.songId
                if occupied.contains(id) {
                    id += ":" + MusicRecordingIdentity.hash(row.key + ":" + row.evidence.descriptorKey)
                    var suffix = 1
                    let base = id
                    while occupied.contains(id) { id = base + ":\(suffix)"; suffix += 1 }
                }
                occupied.insert(id)
                index = groups.count
                groups.append(.init(id: id, variants: [], evidence: [], ranks: [:], displayedScore: nil))
            }
            for key in rowKeys { byIdentity[key, default: []].insert(index) }
            if !groups[index].variants.contains(row.track) {
                groups[index].variants.append(row.track)
                groups[index].evidence.append(row.evidence)
            }
            groups[index].ranks[row.platform] = min(groups[index].ranks[row.platform] ?? Int.max, row.rank)
        }
        let result = groups.map { group -> MusicSearchSong in
            let variants = group.variants.sorted {
                let a = priority($0), b = priority($1)
                if a != b { return a < b }
                let lhs = $0.sources.first?.sourceKey ?? $0.songId, rhs = $1.sources.first?.sourceKey ?? $1.songId
                return lhs.utf16.lexicographicallyPrecedes(rhs.utf16)
            }
            let platforms = Set(variants.flatMap { $0.sources.map(\.platform) } + Array(group.ranks.keys)).sorted()
            let score = group.displayedScore ?? group.ranks.values.reduce(0.0) { $0 + 1.0 / Double(5 + $1) }
            return .init(id: group.id, track: variants[0], variants: variants,
                         matchedPlatforms: platforms, score: score)
        }
        let added = result.dropFirst(displayed.count).sorted {
            if $0.score != $1.score { return $0.score > $1.score }
            let a = priority($0.track), b = priority($1.track)
            if a != b { return a < b }
            let lhs = MusicRecordingIdentity.hash($0.id), rhs = MusicRecordingIdentity.hash($1.id)
            return lhs == rhs ? $0.id < $1.id : lhs < rhs
        }
        return Array(result.prefix(displayed.count)) + added
    }
    public static func playlists(_ batches: [PlaylistBatch],
                                 platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicPublicBrowseCollection] {
        let order = Dictionary(platformOrder.enumerated().map { ($0.element, $0.offset) }, uniquingKeysWith: min)
        var values: [(value: MusicPublicBrowseCollection, rank: Int)] = [], seen = Set<String>()
        for batch in batches {
            for (index, item) in batch.items.enumerated() where seen.insert(item.id).inserted {
                values.append((item, max(1, batch.ranks[item.id] ?? index + 1)))
            }
        }
        return values.sorted {
            if $0.rank != $1.rank { return $0.rank < $1.rank }
            let a = order[$0.value.platform] ?? Int.max, b = order[$1.value.platform] ?? Int.max
            if a != b { return a < b }
            return $0.value.id < $1.value.id
        }.map(\.value)
    }
    public static func appendPlaylists(displayed: [MusicPublicBrowseCollection], adding batches: [PlaylistBatch],
                                       platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicPublicBrowseCollection] {
        var seen = Set(displayed.map(\.id))
        return displayed + playlists(batches, platformOrder: platformOrder).filter { seen.insert($0.id).inserted }
    }
    public static func sameRecording(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack) -> Bool {
        MusicRecordingIdentity(lhs).isCompatible(with: .init(rhs))
    }
    static func sameSearchTarget(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack,
                                 platform: MusicPublicBrowsePlatform) -> Bool {
        guard sameRecording(lhs, rhs) else { return false }
        return lhs.sources.contains { a in
            a.platform == platform.rawValue && !a.sourceKey.isEmpty && rhs.sources.contains { a == $0 }
        }
    }
    public static func isFavorite(_ row: MusicSearchSong, members: [MusicRemoteTrack]) -> Bool {
        members.contains { matchesFavorite($0, row: row) }
    }
    public static func matchesFavorite(_ member: MusicRemoteTrack, row: MusicSearchSong) -> Bool {
        return row.variants.contains { track in
            (member.songId.utf8.elementsEqual(track.songId.utf8) || member.sources.contains { source in
                track.sources.contains { $0.sourceKey.utf8.elementsEqual(source.sourceKey.utf8) }
            }) && sameRecording(member, track)
        }
    }
}
