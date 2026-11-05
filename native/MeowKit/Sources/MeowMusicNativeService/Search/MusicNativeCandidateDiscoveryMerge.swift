// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus SongCatalog/aggregation identity rules, using native MusicModel.
import Foundation
import MeowMusicFeature

struct MusicNativeCandidateDiscoveryObservation: Sendable {
    let platform: String
    let page: MusicNativeTrackPage?
    let failure: MusicServiceError?
}

enum MusicNativeCandidateDiscoveryMerge {
    /// Only a durable recording identity can be matched across platforms.
    static func searchable(_ song: MJ) -> Bool {
        song["songId"].string.hasPrefix("song:") && !MusicModel.fingerprint(song).isEmpty
    }
    static func needsDiscovery(_ song: MJ, force: Bool,
                               cache: [String: MusicSourceDiscoveryEntry] = [:]) -> Bool {
        guard searchable(song) else { return false }
        return MusicNativeSearchProtocol.platforms.contains {
            needsPlatform($0, song: song, force: force, cache: cache[$0])
        }
    }
    static func needsPlatform(_ platform: String, song: MJ, force: Bool,
                              cache: MusicSourceDiscoveryEntry? = nil) -> Bool {
        guard MusicNativeSearchProtocol.platforms.contains(platform) else { return false }
        if force { return true }
        if cache != nil { return false }
        let rows = song["sources"].array.filter { $0["source"].string == platform }
        return rows.isEmpty || rows.contains { !metadataComplete($0) }
    }
    static func metadataComplete(_ source: MJ) -> Bool {
        let info = MusicModel.sourceData(source)
        func known(_ value: MJ) -> Bool {
            let text = MusicModel.norm(value.string)
            return !text.isEmpty && !text.hasPrefix("未知") && !text.hasPrefix("unknown")
        }
        let duration = MusicModel.nativeNumber(info["duration"])
        return known(info["name"]) && known(info["singer"]) && duration.isFinite && duration > 0
    }
    /// `excluded` source keys were confirmed as another recording and are never proposed.
    static func merge(_ seed: MJ, observations: [MusicNativeCandidateDiscoveryObservation],
                      excluded: Set<String> = [])
        throws -> (song: MJ, issues: [MusicCandidateDiscoveryIssue]) {
        var song = try MusicModel.cleanSong(seed)
        var issues: [MusicCandidateDiscoveryIssue] = []
        var buckets: [String: [MJ]] = [:]
        let platforms = MusicNativeSearchProtocol.platforms
        let attempted = Set(observations.map(\.platform))
        try musicRequire(observations.count <= platforms.count &&
            attempted.count == observations.count && attempted.isSubset(of: Set(platforms)) &&
            observations.allSatisfy { ($0.page == nil) != ($0.failure == nil) }, .invalidRequest)
        // Fixed order, independent of network completion order. No transitive
        // A~B~C clustering: every proposed source must match all accepted peers.
        for observation in observations.sorted(by: { $0.platform < $1.platform }) {
            let platform = observation.platform
            if let failure = observation.failure {
                issues.append(.init(stage: .search, platform: platform, failure: failure)); continue
            }
            guard let page = observation.page else { throw MusicServiceError.invalidResponse }
            try musicRequire(page.total >= 0 && page.items.allSatisfy { $0.platform == platform }, .invalidResponse)
            buckets[platform] = []
            for value in page.items {
                let proposed: MJ
                do {
                    let normalized = try MusicModel.normalize(value)
                    guard let row = normalized["sources"].array.first else { throw MusicServiceError.invalidResponse }
                    _ = try MusicModel.locator(MusicModel.sourceData(row))
                    proposed = row
                } catch {
                    issues.append(.init(stage: .identity, platform: platform, failure: .upstreamInvalid)); continue
                }
                let key = proposed["sourceKey"]
                if excluded.contains(key.string) { continue }
                var sources = song["sources"].array
                let existing = sources.firstIndex(where: { $0["sourceKey"] == key })
                let matches = MusicModel.compatible(seed, proposed) &&
                    seed["sources"].array.allSatisfy { MusicModel.compatible($0, proposed) }
                if !matches {
                    if let existing {
                        sources.remove(at: existing)
                        song["sources"] = .array(sources)
                        issues.append(.init(stage: .identity, platform: platform, failure: .nativeIDConflict))
                    }
                    continue
                }
                buckets[platform, default: []].append(proposed)
            }
        }
        // Do not let thirty album editions from the first completed/sorted
        // platform consume all sixteen durable source slots.
        let order = buckets.keys.sorted()
        var offsets: [String: Int] = [:], full = Set<String>(), hasRows = true
        while hasRows {
            hasRows = false
            for platform in order {
                let index = offsets[platform] ?? 0
                guard let rows = buckets[platform], index < rows.count else { continue }
                hasRows = true; offsets[platform] = index + 1
                let proposed = rows[index]
                guard song["sources"].array.allSatisfy({ MusicModel.compatible($0, proposed) }) else { continue }
                do { song = try MusicModel.mergeSources(song, adding: [proposed], preferIncoming: true) }
                catch {
                    if error as? MusicServiceError == .limit, full.insert(platform).inserted {
                        issues.append(.init(stage: .identity, platform: platform, failure: .limit))
                    } else if error as? MusicServiceError != .limit { throw error }
                }
            }
        }
        // Each merge above prefers the incoming row; keep the seed's primary
        // recording first (`sources[0]` defines the song).
        if let primary = seed["sources"].array.first?["sourceKey"],
           let index = song["sources"].array.firstIndex(where: { $0["sourceKey"] == primary }), index > 0 {
            var rows = song["sources"].array
            rows.insert(rows.remove(at: index), at: 0)
            song["sources"] = .array(rows)
        }
        return (try MusicModel.cleanSong(song), Array(issues.prefix(100)))
    }
}
