// SPDX-License-Identifier: Apache-2.0
// Native selection semantics from music-core/song-identity + audio-quality.
import Foundation

public struct MusicCandidateDiscoveryPolicy: MusicCandidateDiscoverySelecting {
    public init() {}
    private static let groups = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    private static let spatial = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]
    private static let levels = ["128k": "standard", "192k": "high", "320k": "high", "flac": "lossless",
        "flac24bit": "hires", "master": "master", "jyeffect": "enhanced", "dolby": "spatial",
        "sky": "spatial", "vivid": "spatial"]

    public func plan(_ snapshot: MusicCandidateDiscoverySnapshot,
                     preferred: MusicCandidateDiscoveryTarget? = nil) throws -> [MusicCandidateDiscoveryTarget] {
        guard snapshot.track.sources.count <= 16 else { throw MusicServiceError.limit }
        let state = snapshot.state
        let order = state.qualityOrder + Self.groups.filter { !state.qualityOrder.contains($0) }
        let spatialOrder = state.spatialOrder + Self.spatial.filter { !state.spatialOrder.contains($0) }
        func priority(_ target: MusicCandidateDiscoveryTarget) -> Int? {
            guard let group = Self.levels[target.quality], state.qualityEnabled.contains(group),
                  let index = order.firstIndex(of: group) else { return nil }
            if group == "spatial" && !state.spatialEnabled.contains(target.quality) { return nil }
            return index * 32 + (group == "spatial" ? spatialOrder.firstIndex(of: target.quality) ?? 16 :
                target.quality == "192k" ? 1 : 0)
        }
        let keys = Set(snapshot.track.sources.map(\.sourceKey))
        var candidates: [MusicCandidateDiscoveryTarget] = []
        for option in snapshot.options where keys.contains(option.playback.sourceKey) {
            guard option.playback.qualities.count <= 10,
                  let source = snapshot.track.sources.first(where: { $0.sourceKey == option.playback.sourceKey }),
                  source.platform == option.playback.siteId else { throw MusicServiceError.invalidResponse }
            for quality in option.playback.qualities {
                let target = option.target(quality: quality)
                if priority(target) != nil && !candidates.contains(where: { $0.sameRequest(as: target) }) {
                    candidates.append(target)
                }
            }
        }
        candidates.sort {
            let left = priority($0)!, right = priority($1)!
            if left != right { return left < right }
            let lo = $0.selection.installationId.hasPrefix("official:")
            let ro = $1.selection.installationId.hasPrefix("official:")
            if lo != ro { return lo }
            if $0.selection.sourceKey != $1.selection.sourceKey { return $0.selection.sourceKey < $1.selection.sourceKey }
            if $0.selection.installationId != $1.selection.installationId { return $0.selection.installationId < $1.selection.installationId }
            return $0.quality < $1.quality
        }
        // Scout each delivery provider's best tier before digging deeper into a
        // fast provider. Within an equal priority, rotate platforms and editions.
        var scouts: [MusicCandidateDiscoveryTarget] = [], deeper: [MusicCandidateDiscoveryTarget] = []
        for candidate in candidates {
            if scouts.contains(where: { $0.selection.installationId == candidate.selection.installationId &&
                $0.selection.sourceKey == candidate.selection.sourceKey }) { deeper.append(candidate) }
            else { scouts.append(candidate) }
        }
        func fair(_ values: [MusicCandidateDiscoveryTarget]) -> [MusicCandidateDiscoveryTarget] {
            var result: [MusicCandidateDiscoveryTarget] = []
            for tier in Set(values.compactMap(priority)).sorted() {
                for official in [true, false] {
                    let matching = values.filter {
                        priority($0) == tier && $0.selection.installationId.hasPrefix("official:") == official
                    }
                    var peers = Dictionary(grouping: matching, by: { $0.selection.siteId })
                    while peers.values.contains(where: { !$0.isEmpty }) {
                        for platform in peers.keys.sorted() {
                            guard var rows = peers[platform], !rows.isEmpty else { continue }
                            result.append(rows.removeFirst()); peers[platform] = rows
                        }
                    }
                }
            }
            return result
        }
        var plan = fair(scouts) + fair(deeper)
        if let preferred, let index = plan.firstIndex(where: { $0.sameRequest(as: preferred) }) {
            plan.insert(plan.remove(at: index), at: 0)
        }
        return plan
    }
}
