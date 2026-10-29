// SPDX-License-Identifier: GPL-3.0-only
// Native DTO projection of b900c259 song-identity.js / rank-fusion.js.
import Foundation

public struct MusicSearchSong: Sendable, Identifiable {
    public let id: String
    public let track: MusicRemoteTrack
    /// Keep the real references. Never attach another source to a handle which
    /// the native owner registered for a different track.
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
        track.sources.first(where: { $0.platform == platform.rawValue })?.sourceKey
            ?? track.sources.first?.sourceKey ?? track.songId
    }
    public static func songs(_ batches: [SongBatch]) -> [MusicSearchSong] {
        struct Row { let track: MusicRemoteTrack; let platform: String; let rank: Int; let key: String }
        struct Group { let id: String; var rows: [Row] }
        let rows = batches.flatMap { batch in
            batch.items.enumerated().map { index, track in
                let key = sourceIdentity(track, platform: batch.platform)
                return Row(track: track, platform: batch.platform.rawValue,
                           rank: max(1, batch.ranks[key] ?? (index + 1)), key: key)
            }
        }.sorted {
            let lhs = $0.track.songId + ":" + $0.key, rhs = $1.track.songId + ":" + $1.key
            if lhs != rhs { return lhs.utf16.lexicographicallyPrecedes(rhs.utf16) }
            return descriptorKey($0.track) < descriptorKey($1.track)
        }
        var groups: [Group] = []
        var bySong: [String: [Int]] = [:]
        for row in rows {
            let candidates = bySong[row.track.songId] ?? []
            if let index = candidates.first(where: { index in
                groups[index].rows.allSatisfy { sameRecording($0.track, row.track) }
            }) {
                groups[index].rows.append(row)
            } else {
                let id = candidates.isEmpty ? row.track.songId
                    : row.track.songId + ":" + hash(json([row.key, descriptorKey(row.track)]))
                bySong[row.track.songId, default: []].append(groups.count)
                groups.append(.init(id: id, rows: [row]))
            }
        }
        return groups.map { group in
            var ranks: [String: Int] = [:]
            var seen = Set<MusicReference>()
            let variants = group.rows.map(\.track).filter { seen.insert($0.ref).inserted }
            for row in group.rows { ranks[row.platform] = min(ranks[row.platform] ?? Int.max, row.rank) }
            let platforms = ranks.keys.sorted()
            let score = platforms.reduce(0.0) { $0 + 1.0 / Double(5 + (ranks[$1] ?? 1)) }
            return MusicSearchSong(id: group.id, track: variants[0], variants: variants,
                                   matchedPlatforms: platforms, score: score)
        }.sorted {
            if $0.score != $1.score { return $0.score > $1.score }
            let lhs = hash($0.id), rhs = hash($1.id)
            return lhs == rhs ? $0.id < $1.id : lhs < rhs
        }
    }
    public static func playlists(_ batches: [PlaylistBatch]) -> [MusicPublicBrowseCollection] {
        var values: [(value: MusicPublicBrowseCollection, score: Double, tie: String)] = []
        var seen = Set<String>()
        for batch in batches {
            for (index, item) in batch.items.enumerated() where seen.insert(item.id).inserted {
                // Same name on different platforms is not evidence of the same playlist.
                let rank = max(1, batch.ranks[item.id] ?? (index + 1))
                values.append((item, 1.0 / Double(5 + rank), hash(json([batch.platform.rawValue, item.nativeID]))))
            }
        }
        return values.sorted {
            if $0.score != $1.score { return $0.score > $1.score }
            return $0.tie == $1.tie ? $0.value.id < $1.value.id : $0.tie < $1.tie
        }.map(\.value)
    }

    public static func sameRecording(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack) -> Bool {
        let a = descriptor(lhs), b = descriptor(rhs)
        let duration = !(a.duration > 0 && b.duration > 0)
            || abs(a.duration - b.duration) <= max(3, min(a.duration, b.duration) * 0.02)
        let sameNative = lhs.sources.contains { l in rhs.sources.contains { r in
            !l.sourceKey.isEmpty && l.sourceKey.utf8.elementsEqual(r.sourceKey.utf8)
        }}
        if sameNative {
            return (!known(a.artist) || !known(b.artist) || a.artist == b.artist)
                && (!known(a.title) || !known(b.title) || a.version == b.version) && duration
        }
        return known(a.title) && known(a.artist) && a.title == b.title && a.artist == b.artist
            && a.version == b.version && a.duration > 0 && b.duration > 0 && duration
    }
    public static func isFavorite(_ row: MusicSearchSong, members: [MusicRemoteTrack]) -> Bool {
        members.contains { matchesFavorite($0, row: row) }
    }
    /// More restrictive than cross-platform display fusion: a handle renewal
    /// must retain both the recording identity and the exact native source.
    static func sameSearchTarget(_ lhs: MusicRemoteTrack, _ rhs: MusicRemoteTrack,
                                 platform: MusicPublicBrowsePlatform) -> Bool {
        guard lhs.songId.utf8.elementsEqual(rhs.songId.utf8), sameRecording(lhs, rhs) else { return false }
        return lhs.sources.contains { a in
            a.platform == platform.rawValue && !a.sourceKey.isEmpty && rhs.sources.contains { b in
                a == b
            }
        }
    }
    public static func matchesFavorite(_ member: MusicRemoteTrack, row: MusicSearchSong) -> Bool {
        if member.songId.utf8.elementsEqual(row.id.utf8) { return true }
        return row.variants.contains { track in
            member.sources.contains { source in
                track.sources.contains { $0.sourceKey.utf8.elementsEqual(source.sourceKey.utf8) }
            } && sameRecording(member, track)
        }
    }
    static func hash(_ value: String) -> String {
        var a: UInt32 = 2166136261, b: UInt32 = 3339675911
        for unit in value.utf16 { a = (a ^ UInt32(unit)) &* 16777619; b = (b ^ UInt32(unit)) &* 2246822519 }
        return String(format: "%08x%08x", a, b)
    }
    private static func normalized(_ value: String) -> String {
        value.precomposedStringWithCompatibilityMapping.lowercased()
            .replacingOccurrences(of: "[\\u200b-\\u200d\\ufeff]", with: "", options: .regularExpression)
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
    private static func known(_ value: String) -> Bool {
        !value.isEmpty && !value.hasPrefix("未知") && !value.hasPrefix("unknown")
    }
    private static func descriptor(_ track: MusicRemoteTrack) -> (title: String, artist: String, version: String, duration: Double) {
        var title = normalized(track.title)
        let artists = normalized(track.artist).replacingOccurrences(
            of: "\\s*(?:、|;|\\s/\\s|\\s&\\s|\\sfeat\\.?\\s|\\sfeaturing\\s)\\s*", with: "\u{1f}", options: .regularExpression)
        let artist = artists.components(separatedBy: "\u{1f}").filter { !$0.isEmpty }
            .sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) }.joined(separator: " & ")
        let tags = [("harmony", "和声伴奏|和聲伴奏"), ("instrumental", "伴奏|instrumental|karaoke"),
                    ("live", "\\blive\\b|现场|現場"), ("remix", "remix|\\bdj\\b"),
                    ("remaster", "remaster|重制|重製"), ("acoustic", "acoustic|不插电|不插電"),
                    ("cover", "翻唱|\\bcover\\b")]
        let version = tags.filter { title.range(of: $0.1, options: .regularExpression) != nil }.map(\.0).joined(separator: ",")
        if let range = title.range(of: "\\s*[（(]([^()（）]+)[)）]\\s*$", options: .regularExpression) {
            let suffix = String(title[range]).trimmingCharacters(in: .whitespaces)
            let inner = normalized(String(suffix.dropFirst().dropLast()))
            let prefix = normalized(String(title[..<range.lowerBound]))
            if inner == prefix { title = prefix }
        }
        title = title.replacingOccurrences(of: "\\s*\\(\\s*", with: "(", options: .regularExpression)
            .replacingOccurrences(of: "\\s*\\)\\s*", with: ")", options: .regularExpression)
            .trimmingCharacters(in: .whitespaces)
        return (title, artist, version, track.duration.isFinite ? track.duration : 0)
    }
    private static func descriptorKey(_ track: MusicRemoteTrack) -> String {
        let d = descriptor(track)
        return json([d.title, d.artist, d.version, String(d.duration)])
    }
    private static func json(_ values: [String]) -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        return (try? encoder.encode(values)).flatMap { String(data: $0, encoding: .utf8) } ?? ""
    }
}
