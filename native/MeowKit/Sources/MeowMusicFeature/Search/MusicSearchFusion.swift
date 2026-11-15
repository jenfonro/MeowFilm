// SPDX-License-Identifier: GPL-3.0-only
// Native DTO projection of iWebPlus rank-fusion.js.
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
    /// Platform results have already been adopted by the sole repository.
    /// Display fusion groups permanent references; it never creates identities.
    private static func fuse(_ batches: [SongBatch], displayed: [MusicSearchSong],
                             platformOrder: [MusicPublicBrowsePlatform]) -> [MusicSearchSong] {
        struct Group {
            let id: String
            var variants: [MusicRemoteTrack]
            var ranks: [String: Double]
            let displayedScore: Double?
        }
        let order = Dictionary(platformOrder.enumerated().map { ($0.element.rawValue, $0.offset) }, uniquingKeysWith: min)
        func priority(_ track: MusicRemoteTrack) -> Int {
            track.sources.map { order[$0.platform] ?? Int.max }.min() ?? Int.max
        }
        var groups = displayed.map { Group(id: $0.id, variants: $0.variants, ranks: [:], displayedScore: $0.score) }
        var positions = Dictionary(groups.enumerated().map { ($0.element.id, $0.offset) }, uniquingKeysWith: min)
        for batch in batches {
            for (offset, track) in batch.items.enumerated() {
                let index: Int
                if let existing = positions[track.songId] { index = existing }
                else {
                    index = groups.count; positions[track.songId] = index
                    groups.append(.init(id: track.songId, variants: [], ranks: [:], displayedScore: nil))
                }
                if !groups[index].variants.contains(track) { groups[index].variants.append(track) }
                let key = sourceIdentity(track, platform: batch.platform)
                let rank = max(1, batch.ranks[key] ?? Double(offset) + 1)
                groups[index].ranks[batch.platform.rawValue] = min(groups[index].ranks[batch.platform.rawValue] ?? rank, rank)
            }
        }
        let result = groups.map { group -> MusicSearchSong in
            let variants = group.variants.sorted {
                let a = priority($0), b = priority($1)
                if a != b { return a < b }
                return ($0.sources.first?.sourceKey ?? $0.songId) < ($1.sources.first?.sourceKey ?? $1.songId)
            }
            let platforms = Set(variants.flatMap { $0.sources.map(\.platform) } + Array(group.ranks.keys)).sorted()
            let score = group.displayedScore ?? group.ranks.values.reduce(0.0) { $0 + 1.0 / (5 + $1) }
            return .init(id: group.id, track: variants[0], variants: variants, matchedPlatforms: platforms, score: score)
        }
        let added = result.dropFirst(displayed.count).sorted {
            if $0.score != $1.score { return $0.score > $1.score }
            let a = priority($0.track), b = priority($1.track)
            return a == b ? $0.id < $1.id : a < b
        }
        return Array(result.prefix(displayed.count)) + added
    }
    public static func playlists(_ batches: [PlaylistBatch],
                                 platformOrder: [MusicPublicBrowsePlatform] = MusicPublicBrowsePlatform.allCases) -> [MusicPublicBrowseCollection] {
        let order = Dictionary(platformOrder.enumerated().map { ($0.element, $0.offset) }, uniquingKeysWith: min)
        var values: [(value: MusicPublicBrowseCollection, rank: Double)] = [], seen = Set<String>()
        for batch in batches {
            for (index, item) in batch.items.enumerated() where seen.insert(item.id).inserted {
                values.append((item, max(1, batch.ranks[item.id] ?? Double(index) + 1)))
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
        lhs.songId.utf8.elementsEqual(rhs.songId.utf8)
    }
    public static func isFavorite(_ row: MusicSearchSong, members: [MusicRemoteTrack]) -> Bool {
        members.contains { matchesFavorite($0, row: row) }
    }
    public static func matchesFavorite(_ member: MusicRemoteTrack, row: MusicSearchSong) -> Bool {
        row.variants.contains { sameRecording(member, $0) }
    }
}
