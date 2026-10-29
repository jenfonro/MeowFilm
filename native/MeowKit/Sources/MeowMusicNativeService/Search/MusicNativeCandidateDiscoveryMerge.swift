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
    static func needsDiscovery(_ song: MJ, now: Date, force: Bool) -> Bool {
        guard song["_isOnlineObj"].bool, song["songId"].string.hasPrefix("song:") else { return false }
        if force { return true }
        let at = now.timeIntervalSince1970 * 1000
        if song["sourcesRetryAfter"].number > at { return false }
        return song["sourcesCheckedAt"].number <= 0 ||
            (song["nextCheckAt"].number > 0 && song["nextCheckAt"].number <= at) ||
            (song["partial"].bool && song["sourcesCheckedAt"].number <= at - 30 * 60 * 1000)
    }
    static func needsPlatform(_ platform: String, song: MJ, now: Date, force: Bool) -> Bool {
        if force { return true }
        let at = now.timeIntervalSince1970 * 1000
        // A completed five-platform check also records successful empty search
        // results; absence of a mapping is not a reason to search at every play.
        if song["partial"] == .bool(false), song["sourcesCheckedAt"].number > 0,
           song["nextCheckAt"].number > at { return false }
        let rows = song["sources"].array.filter { $0["source"].string == platform }
        guard !rows.isEmpty else { return true }
        return rows.contains {
            let due = $0["nextCheckAt"].number > 0 ? $0["nextCheckAt"].number : $0["observedAt"].number + 7 * 24 * 60 * 60 * 1000
            return MusicModel.fingerprint($0).isEmpty || $0["duration"].number <= 0 ||
                $0["observedAt"].number <= 0 || due <= at
        }
    }
    static func merge(_ seed: MJ, observations: [MusicNativeCandidateDiscoveryObservation], now: Date)
        throws -> (song: MJ, issues: [MusicCandidateDiscoveryIssue]) {
        var song = seed, sources = seed["sources"].array, quarantined = seed["quarantinedSources"].array
        var issues: [MusicCandidateDiscoveryIssue] = [], partial = false
        var buckets: [String: [MJ]] = [:]
        var observed: [String: Set<String>] = [:]
        let at = now.timeIntervalSince1970 * 1000
        let platforms = MusicNativeSearchProtocol.platforms
        let attempted = Set(observations.map(\.platform))
        try musicRequire(at.isFinite && at > 0 && observations.count <= platforms.count &&
            attempted.count == observations.count && attempted.isSubset(of: Set(platforms)) &&
            observations.allSatisfy { ($0.page == nil) != ($0.failure == nil) }, .invalidRequest)
        // Unobserved platforms can only count as covered if native evidence is
        // already fresh. Never turn omitted work into a successful empty page.
        partial = platforms.contains { !attempted.contains($0) && needsPlatform($0, song: seed, now: now, force: false) }
        // Fixed order, independent of network completion order. No transitive
        // A~B~C clustering: every proposed source must match all accepted peers.
        for observation in observations.sorted(by: { $0.platform < $1.platform }) {
            let platform = observation.platform
            if let failure = observation.failure {
                for index in sources.indices where sources[index]["source"].string == platform {
                    sources[index]["nextCheckAt"] = .number(at + 30 * 60 * 1000)
                }
                partial = true; issues.append(.init(stage: .search, platform: platform, failure: failure)); continue
            }
            guard let page = observation.page else { throw MusicServiceError.invalidResponse }
            try musicRequire(page.items.count <= MusicNativeSearchProtocol.maximumItems(platform) &&
                page.items.allSatisfy { $0.platform == platform }, .invalidResponse)
            buckets[platform] = []
            observed[platform] = []
            for value in page.items {
                let proposed: MJ
                do {
                    let normalized = try MusicModel.normalize(value)
                    guard let row = normalized["sources"].array.first else { throw MusicServiceError.invalidResponse }
                    _ = try MusicModel.locator(row["source_data"])
                    proposed = row
                } catch {
                    partial = true; issues.append(.init(stage: .identity, platform: platform, failure: .upstreamInvalid)); continue
                }
                let key = proposed["sourceKey"]
                if quarantined.contains(where: { $0["sourceKey"] == key }) { partial = true; continue }
                let existing = sources.firstIndex(where: { $0["sourceKey"] == key })
                let matches = MusicModel.compatible(seed, proposed) &&
                    seed["sources"].array.allSatisfy { MusicModel.compatible($0, proposed) }
                if !matches {
                    if let existing {
                        sources.remove(at: existing)
                        if quarantined.count < 16 { quarantined.append(proposed) }
                        partial = true
                        issues.append(.init(stage: .identity, platform: platform, failure: .nativeIDConflict))
                    }
                    continue
                }
                buckets[platform, default: []].append(proposed)
            }
        }
        // Do not let thirty album editions from the first completed/sorted
        // platform consume all sixteen durable source slots.
        while buckets.values.contains(where: { !$0.isEmpty }) {
            for platform in buckets.keys.sorted() {
                guard var rows = buckets[platform], !rows.isEmpty else { continue }
                let proposed = rows.removeFirst(); buckets[platform] = rows
                let key = proposed["sourceKey"]
                guard !quarantined.contains(where: { $0["sourceKey"] == key }),
                      sources.allSatisfy({ MusicModel.compatible($0, proposed) }) else { partial = true; continue }
                let existing = sources.firstIndex(where: { $0["sourceKey"] == key })
                observed[platform, default: []].insert(key.string)
                var row = proposed; row["observedAt"] = .number(at)
                row["nextCheckAt"] = .number(at + 7 * 24 * 60 * 60 * 1000)
                if let existing {
                    // Keep original evidence if an incomplete native-ID update
                    // would erase the duration/artist guard against future reuse.
                    for field in ["title", "artist", "album"] where row[field].string.isEmpty ||
                        MusicModel.norm(row[field].string).hasPrefix("未知") ||
                        MusicModel.norm(row[field].string).hasPrefix("unknown") { row[field] = sources[existing][field] }
                    if row["duration"].number <= 0 { row["duration"] = sources[existing]["duration"] }
                    sources[existing] = row
                } else if sources.count < 16 { sources.append(row) }
                else {
                    partial = true; issues.append(.init(stage: .identity, platform: platform, failure: .limit))
                }
            }
        }
        for platform in observed.keys {
            let accepted = observed[platform] ?? []
            if seed["sources"].array.contains(where: { $0["source"].string == platform && !accepted.contains($0["sourceKey"].string) }) {
                partial = true
                for index in sources.indices where sources[index]["source"].string == platform &&
                    !accepted.contains(sources[index]["sourceKey"].string) {
                    sources[index]["nextCheckAt"] = .number(at + 30 * 60 * 1000)
                }
            }
        }
        try musicRequire(seed["revision"].number < 9_007_199_254_740_991, .limit)
        song["sources"] = .array(sources.sorted { $0["sourceKey"].string < $1["sourceKey"].string })
        song["quarantinedSources"] = .array(quarantined)
        let changed = semantic(seed) != semantic(song)
        let priorStable = min(2, max(0, seed["stableCount"].int))
        var stable = changed ? 0 : priorStable
        let sevenDays = 7 * 24 * 60 * 60 * 1000.0
        var next = seed["nextCheckAt"].number > 0 ? seed["nextCheckAt"].number : at + sevenDays
        if changed { next = min(next, at + sevenDays) }
        if !partial {
            if attempted.count == platforms.count {
                // Match the fixed catalog's 7/15/30-day stable metadata policy.
                // Timestamps/revision counters are not semantic song changes.
                stable = !changed && seed["sourcesCheckedAt"].number > 0 ? min(2, priorStable + 1) : 0
                next = at + Double([7, 15, 30][stable]) * 24 * 60 * 60 * 1000
            } else {
                // Incremental coverage may finish a partial check, but cannot
                // extend the unqueried platforms' evidence or promote stability.
                let deadlines = sources.filter { !attempted.contains($0["source"].string) }.map {
                    $0["nextCheckAt"].number > 0 ? $0["nextCheckAt"].number : $0["observedAt"].number + sevenDays
                }
                next = min(at + sevenDays, deadlines.min() ?? at + sevenDays)
            }
        }
        song["revision"] = .n(max(1, seed["revision"].int + 1))
        song["sourcesCheckedAt"] = .number(at)
        song["partial"] = .bool(partial)
        song["sourcesRetryAfter"] = .number(partial ? at + 30 * 60 * 1000 : 0)
        song["stableCount"] = .n(stable)
        song["nextCheckAt"] = .number(next)
        return (try MusicModel.cleanSong(song), Array(issues.prefix(100)))
    }
    private static func semantic(_ song: MJ) -> MJ {
        let rows = song["sources"].array.sorted { $0["sourceKey"].string < $1["sourceKey"].string }.map { row -> MJ in
            let qualities = row["source_data"]["types"].array.map { $0["type"].string }.sorted()
            return .array([row["sourceKey"], row["title"], row["artist"], row["album"], row["duration"], .strings(qualities)])
        }
        return .array([song["title"], song["artist"], song["album"], song["duration"], .array(rows)])
    }
}
