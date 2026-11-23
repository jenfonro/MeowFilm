import Foundation
import MeowMusicFeature

struct MusicNativeCandidateDiscoveryOptions: Sendable {
    var rows: [MusicCandidateDiscoveryOption] = []
    var issues: [MusicCandidateDiscoveryIssue] = []
    var scriptState: MusicSourceAvailability.ScriptState = .notConfigured
}

extension NativeMusicService {
    func candidateOptions(song: MJ, seed: MusicNativeCandidateDiscoverySeed, label: String,
                          timing: MusicCandidateDiscoveryTiming) async throws -> MusicNativeCandidateDiscoveryOptions {
        try candidateCheck(seed)
        // A source confirmed as another recording is never offered for playback.
        let excluded = try recordingSourceExclusions(seed.song)
        let playback = try candidateCheck(seed).playback
        let rows = try MusicModel.sources(song).filter { platformEnabled($0.platform) && !excluded.contains($0.sourceKey) }
        // Do not probe accounts or a script host for disabled-only saved rows.
        // A disabled built-in source is skipped; scripts may still serve that platform.
        let platforms = (scripts != nil && !rows.isEmpty ? ["lx"] : []) +
            ["wy", "tx", "kg"].filter { platform in
                Self.officialEnabled(platform, playback) && rows.contains { $0.platform == platform }
            }
        let result = try await withThrowingTaskGroup(of: MusicNativeCandidateDiscoveryOptions.self) { group in
            for platform in platforms {
                group.addTask {
                    do {
                        return try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: timing) {
                            if platform == "lx" {
                                return try await self.candidateScriptOptions(song: song, seed: seed, label: label,
                                                                             excluded: excluded)
                            }
                            return try await self.candidateAccountOptions(rows: rows.filter { $0.platform == platform },
                                platform: platform, seed: seed, label: label)
                        }
                    } catch {
                        let failure = MusicServiceError.sanitized(error)
                        guard MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
                            failure.isAuthenticationFailure || [.verificationRequired, .unavailable].contains(failure) else { throw failure }
                        return .init(issues: [.init(stage: .capability, platform: platform, failure: failure)],
                            scriptState: platform == "lx" ? .unavailable(failure) : .notConfigured)
                    }
                }
            }
            var result = MusicNativeCandidateDiscoveryOptions()
            for try await value in group {
                result.rows += value.rows; result.issues += value.issues
                if value.scriptState != .notConfigured { result.scriptState = value.scriptState }
            }
            return result
        }
        try candidateCheck(seed)
        return result
    }

    private func candidateScriptOptions(song: MJ, seed: MusicNativeCandidateDiscoverySeed, label: String,
                                        excluded: Set<String>) async throws -> MusicNativeCandidateDiscoveryOptions {
        guard let scripts else { return .init() }
        try candidateCheck(seed)
        let snapshots = try await work(label: label) { try await scripts.sources() }
        try candidateCheck(seed)
        var result = MusicNativeCandidateDiscoveryOptions(scriptState: .ready)
        let prefs = try candidateCheck(seed).playback
        result.rows = try scriptOptions(song: song, playback: prefs, snapshots: snapshots, excluded: excluded)
        return result
    }

    private static let supportedScriptQualities: Set<String> = [
        "128k", "192k", "320k", "flac", "flac24bit", "master"
    ]

    /// Script/recording admission is shared; only automatic candidates apply
    /// the saved quality preferences.
    func scriptOptions(song: MJ, playback prefs: MJ, snapshots: [MusicScriptSource],
                       excluded: Set<String>, manual: Bool = false) throws -> [MusicCandidateDiscoveryOption] {
        try musicRequire(Set(snapshots.map(\.installationID)).count == snapshots.count, .invalidResponse)
        let candidates = snapshots.filter(\.isCandidate)
        guard !candidates.isEmpty else { return [] }
        let songSources = try MusicModel.sources(song).filter {
            platformEnabled($0.platform) && !excluded.contains($0.sourceKey)
        }
        guard !songSources.isEmpty else { return [] }
        let songID = song["songId"]
        let excludedPairs = Set(
            try exclusionRows(read().playback, key: "sourceExclusions")
                .filter { $0["songID"] == songID }
                .map { "\($0["provider"].string)\u{0}\($0["platform"].string)" }
        )
        var sourceRowsByKey: [String: MJ] = [:]
        for row in song["sources"].array {
            let key = row["sourceKey"].string
            if sourceRowsByKey[key] == nil { sourceRowsByKey[key] = row }
        }
        var rows: [MusicCandidateDiscoveryOption] = []
        for snapshot in candidates {
            for source in songSources {
                let pairKey = "\(MusicSourceExclusion.providerID(snapshot.installationID, platform: source.platform))\u{0}\(source.platform)"
                if excludedPairs.contains(pairKey) { continue }
                guard snapshot.capabilities.contains(where: { $0.siteID == source.platform && $0.actions.contains("musicUrl") }) else { continue }
                let capability = try validate(snapshot, site: source.platform)
                let row = sourceRowsByKey[source.sourceKey] ?? .null
                let declared = MusicModel.sourceData(row)["types"].array.map { $0["type"].string }
                let supported = capability.qualities.filter {
                    Self.supportedScriptQualities.contains($0) &&
                        (declared.isEmpty || declared.contains($0))
                }
                let qualities = manual ? supported : MusicModel.qualities(supported, prefs)
                if !qualities.isEmpty {
                    rows.append(.init(playback: .init(installationId: snapshot.installationID, generation: snapshot.generation,
                        siteId: source.platform, sourceKey: source.sourceKey, qualities: qualities,
                        title: capability.title, version: snapshot.version, contentHash: snapshot.contentHash,
                        revisionHash: snapshot.revisionHash),
                        revisionHash: snapshot.revisionHash))
                }
            }
        }
        return rows
    }

    private func candidateAccountOptions(rows: [MusicNativeSource], platform: String,
        seed: MusicNativeCandidateDiscoverySeed, label: String) async throws -> MusicNativeCandidateDiscoveryOptions {
        try requirePlatform(platform)
        guard let accounts, let platformValue = MusicAccountPlatform(rawValue: platform) else { return .init() }
        let prefs = try candidateCheck(seed).playback
        var result = MusicNativeCandidateDiscoveryOptions()
        for source in rows {
            do {
                let values = try await work(label: label) {
                    try await accounts.nativeOptions(platform: platformValue, nativeID: source.nativeId, sourceKey: source.sourceKey)
                }
                try candidateCheck(seed)
                for value in values {
                    let qualities = Self.officialQualities(value.qualities, prefs)
                    if !qualities.isEmpty {
                        result.rows.append(.init(playback: .init(installationId: value.installationId, generation: value.generation,
                            siteId: value.siteId, sourceKey: value.sourceKey, qualities: qualities,
                            title: value.title, version: value.version, contentHash: value.contentHash)))
                    }
                }
            } catch let error as MusicServiceError where error.isAuthenticationFailure || error == .unsupported {
                result.issues.append(.init(stage: .capability, platform: platform, failure: error))
            }
        }
        return result
    }

}
