// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus source-resolver.js + browse-settings.js openSources, native ports.
import Foundation
import MeowMusicFeature

struct MusicSourceInspectionSeed: Sendable {
    let ref: MusicReference
    let song: MJ
    let state: MusicPlaybackState
    let playback: MJ
    let epoch: UUID
    let lifetime: MusicNativeCandidateDiscoveryLifetime
}

struct MusicSourceInspectionResult: Sendable {
    let audio: MusicNativeAudio
    let option: MusicPlaybackOption
}

extension NativeMusicService: MusicResolvedSourcesServing {
    public func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession {
        let d = try read()
        if reference.kind == .queue, d.queue["currentId"].string == reference.id {
            let shared = try await sourceSession(occurrenceID: reference.id, expectedQueueRevision: d.queue["revision"].int)
            return MusicSourceInspectionLease(session: shared)
        }
        let seed = MusicSourceInspectionSeed(ref: reference, song: try song(reference, d),
            state: stateDTO(d), playback: d.playback, epoch: epoch, lifetime: MusicNativeCandidateDiscoveryLifetime())
        return MusicNativeSourceInspection(owner: self, seed: seed, clock: now, cached: [])
    }

    func inspectionCheck(_ seed: MusicSourceInspectionSeed) throws {
        try seed.lifetime.check(); try check(seed.epoch)
        let d = try read()
        try musicRequire(d.playback["revision"].int == seed.state.settingsRevision, .staleRevision)
        try musicRequire(try song(seed.ref, d) == seed.song, .targetChanged)
        if seed.ref.kind == .queue {
            try musicRequire(d.queue["revision"].int == seed.state.revision &&
                (seed.state.currentId != seed.ref.id || d.queue["currentId"].string == seed.ref.id), .staleRevision)
        }
    }

    func inspectionDiscover(_ seed: MusicSourceInspectionSeed, label: String,
                            force: Bool, contentFailed: Set<String> = [], recheckOnly: Bool = false)
        async throws -> (song: MJ, searched: Set<String>) {
        try inspectionCheck(seed)
        let state = try await sourceDiscoveryState(seed.song)
        try inspectionCheck(seed)
        let cache = state.current(window: MusicSourceDiscoveryWindow.playback)
        let staged = mergeSourceDiscoveryCache(seed.song, entries: cache)
        let query = String((seed.song["title"].string + " " + seed.song["artist"].string).prefix(200))
        let scope: Set<String>
        if seed.ref.kind == .queue, seed.ref.id == seed.state.currentId {
            scope = try await candidateDiscoveryScope(seed: .init(occurrenceID: seed.ref.id, song: seed.song,
                state: seed.state, epoch: seed.epoch, lifetime: seed.lifetime), label: label)
        } else { scope = Set(MusicNativeSearchProtocol.platforms) }
        let platforms = MusicNativeSearchProtocol.platforms.filter {
            platformEnabled($0) && (force || scope.contains($0)) &&
                ((!recheckOnly && MusicNativeCandidateDiscoveryMerge.needsPlatform($0, song: staged, force: force, cache: cache[$0]) &&
                    (force || !state.hasCurrentCheck($0, window: MusicSourceDiscoveryWindow.playback))) ||
                    contentFailed.contains($0) && (state.checks[$0].map {
                        state.at - $0.checkedAt >= MusicSourceDiscoveryWindow.recheck
                    } ?? true))
        }
        let observations = try await withThrowingTaskGroup(of: MusicNativeCandidateDiscoveryObservation.self) { group in
            for platform in platforms {
                group.addTask {
                    do {
                        let page = try await self.inspectionSearch(seed, platform: platform, query: query, label: label)
                        return .init(platform: platform, page: page, failure: nil)
                    } catch {
                        let failure = MusicServiceError.sanitized(error)
                        if [.cancelled, .closed, .accessUnavailable, .staleAccess, .staleRevision, .targetChanged,
                            .storageFailed, .corruptDocument].contains(failure) { throw failure }
                        return .init(platform: platform, page: nil, failure: failure)
                    }
                }
            }
            var values: [MusicNativeCandidateDiscoveryObservation] = []
            for try await value in group { values.append(value) }
            return values
        }
        try inspectionCheck(seed)
        let merged = try MusicNativeCandidateDiscoveryMerge.merge(staged, observations: observations,
                                                                  excluded: state.excluded).song
        return (merged, Set(platforms))
    }

    private func inspectionSearch(_ seed: MusicSourceInspectionSeed, platform: String,
                                  query: String, label: String) async throws -> MusicNativeTrackPage {
        try inspectionCheck(seed); try requirePlatform(platform)
        let port = publicPlatforms
        let request = beginSourceDiscoveryObservation(platform: platform, query: query)
        do {
            let value = try await MusicCandidateDiscoveryDeadline.run(seconds: 12, timing: .init()) {
                try await self.work(label: label) {
                    try await port.search(platform: platform, query: query, page: 1, limit: 30)
                }
            }
            try inspectionCheck(seed)
            try musicRequire(value.total >= 0 && value.items.allSatisfy({ $0.platform == platform }), .upstreamInvalid)
            let completed = finishSourceDiscoveryObservation(request)
            await recordSourceDiscovery(seed.song, platform: platform, page: value, observation: completed)
            try inspectionCheck(seed)
            return value
        } catch {
            try inspectionCheck(seed)
            let failure = MusicServiceError.sanitized(error)
            if ![.cancelled, .closed, .accessUnavailable, .staleAccess, .staleRevision, .targetChanged,
                 .storageFailed, .corruptDocument].contains(failure) {
                let completed = finishSourceDiscoveryObservation(request, failure: failure)
                await recordSourceDiscovery(seed.song, platform: platform, rows: [], observation: completed)
                try inspectionCheck(seed)
            }
            throw failure
        }
    }

    func inspectionOptions(_ seed: MusicSourceInspectionSeed, staged: MJ, label: String,
                           receive: @escaping @Sendable ([MusicPlaybackOption]) async -> Void) async throws {
        try inspectionCheck(seed)
        let excluded = try recordingSourceExclusions(seed.song)
        try inspectionCheck(seed)
        let rows = try MusicModel.sources(staged).filter { platformEnabled($0.platform) && !excluded.contains($0.sourceKey) }
        try await withThrowingTaskGroup(of: [MusicPlaybackOption].self) { group in
            for source in rows where Self.officialEnabled(source.platform, seed.playback) {
                if try sourceExcluded(seed.song, provider: "official:" + source.platform, platform: source.platform) { continue }
                group.addTask { try await self.inspectionOption(seed, source: source, label: label) }
            }
            if scripts != nil, !rows.isEmpty {
                group.addTask { try await self.inspectionScriptOptions(seed, staged: staged, label: label, excluded: excluded) }
            }
            while let options = try await group.next() {
                try inspectionCheck(seed)
                await receive(options)
            }
        }
    }
    private func inspectionOption(_ seed: MusicSourceInspectionSeed, source: MusicNativeSource,
                                  label: String) async throws -> [MusicPlaybackOption] {
        try inspectionCheck(seed)
        guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform) else { return [] }
        do {
            let options = try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: .init()) {
                try await self.work(label: label) {
                    try await accounts.nativeOptions(platform: platform, nativeID: source.nativeId, sourceKey: source.sourceKey)
                }
            }
            try inspectionCheck(seed)
            try musicRequire(options.count <= 1, .invalidResponse)
            return try options.compactMap { value in
                try candidateValidateOfficial(value, source: source)
                let qualities = value.qualities
                if !qualities.isEmpty {
                    return .init(installationId: value.installationId, generation: value.generation,
                        siteId: value.siteId, sourceKey: value.sourceKey, qualities: qualities,
                        title: value.title, version: value.version, contentHash: value.contentHash)
                }
                return nil
            }
        } catch {
            try inspectionCheck(seed)
            let failure = MusicServiceError.sanitized(error)
            guard MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
                failure.isAuthenticationFailure || [.verificationRequired, .unavailable].contains(failure) else { throw failure }
            return []
        }
    }

    private func inspectionScriptOptions(_ seed: MusicSourceInspectionSeed, staged: MJ,
                                         label: String, excluded: Set<String>) async throws -> [MusicPlaybackOption] {
        try inspectionCheck(seed)
        guard let scripts else { return [] }
        do {
            let snapshots = try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: .init()) {
                try await self.work(label: label) { try await scripts.sources() }
            }
            try inspectionCheck(seed)
            return try scriptOptions(song: staged, playback: seed.playback, snapshots: snapshots,
                                     excluded: excluded, manual: true).map(\.playback)
        } catch {
            try inspectionCheck(seed)
            let failure = MusicServiceError.sanitized(error)
            guard failure == .unavailable || MusicCandidateDiscoveryFailures.mayTryAnother(failure) else { throw failure }
            return []
        }
    }

    /// A pinned result refreshes only its provider/recording. In particular,
    /// do not start other installed scripts or probe unrelated official accounts.
    func inspectionExactOption(_ seed: MusicSourceInspectionSeed, staged: MJ,
                               selection: MusicPlaybackSelection, label: String) async throws -> MusicPlaybackOption {
        try inspectionCheck(seed)
        let (source, _) = try target(staged, selection.sourceKey)
        try requirePlatform(source.platform)
        try musicRequire(source.platform == selection.siteId, .targetChanged)
        try admitSource(seed.song, selection: selection)
        let excluded = try recordingSourceExclusions(seed.song)
        try inspectionCheck(seed)
        try musicRequire(!excluded.contains(source.sourceKey), .sourceUnavailable)
        let values: [MusicPlaybackOption]
        if selection.installationId.hasPrefix("official:") {
            try musicRequire(Self.officialEnabled(source.platform, seed.playback), .sourceUnavailable)
            values = try await inspectionOption(seed, source: source, label: label)
        } else {
            guard let scripts else { throw MusicServiceError.sourceUnavailable }
            let live = try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: .init()) {
                try await self.work(label: label) { try await scripts.source(installationID: selection.installationId) }
            }
            try inspectionCheck(seed)
            values = try scriptOptions(song: staged, playback: seed.playback, snapshots: [live],
                                       excluded: excluded, manual: true).map(\.playback)
        }
        guard let value = values.first(where: {
            $0.installationId == selection.installationId && $0.siteId == selection.siteId &&
                $0.sourceKey == selection.sourceKey && $0.qualities.contains(selection.quality)
        }) else { throw MusicServiceError.sourceUnavailable }
        return value
    }

    /// The live script must still be the exact load the option was built from.
    private func inspectionScriptPin(_ live: MusicScriptSource, option: MusicPlaybackOption, quality: String) throws {
        try pinScript(live, selection: option.selection(quality: quality))
        _ = try validate(live, site: option.siteId, quality: quality)
    }

    func inspectionResolve(_ seed: MusicSourceInspectionSeed, staged: MJ,
                           option: MusicPlaybackOption, quality: String, label: String,
                           automaticQuality: Bool = false) async throws -> MusicSourceInspectionResult {
        try await admitRecordingSource(seed.song, sourceKey: option.sourceKey)
        try inspectionCheck(seed)
        let (source, info) = try target(staged, option.sourceKey)
        try requirePlatform(source.platform)
        try musicRequire(source.platform == option.siteId, .targetChanged)
        let selection = option.selection(quality: quality)
        var resolvedOption = option
        try admitSource(seed.song, selection: selection)
        let audio: MusicNativeAudio
        if option.installationId.hasPrefix("official:") {
            guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform),
                  Self.officialEnabled(source.platform, seed.playback) else {
                throw MusicServiceError.sourceUnavailable
            }
            try candidateValidateOfficial(option, source: source)
            let options = try await work(label: label) {
                try await accounts.nativeOptions(platform: platform, nativeID: source.nativeId, sourceKey: source.sourceKey)
            }
            try inspectionCheck(seed)
            guard let live = options.first, options.count == 1, live.installationId == option.installationId,
                  live.generation == option.generation, live.contentHash == option.contentHash,
                  live.version == option.version, live.qualities.contains(quality) else { throw MusicServiceError.targetChanged }
            try musicRequire(option.qualities.contains(quality), .sourceUnavailable)
            audio = try await work(label: label) {
                try await accounts.nativeResolve(platform: platform, nativeID: source.nativeId, selection: selection)
            }
        } else {
            guard let scripts else { throw MusicServiceError.sourceUnavailable }
            try musicRequire(option.qualities.contains(quality), .sourceUnavailable)
            let snapshot = try await sourceDeclaration(scripts, installationID: option.installationId, label: label)
            try inspectionCheck(seed)
            try inspectionScriptPin(snapshot, option: option, quality: quality)
            let request = MusicScriptRequest(source: snapshot,
                siteID: option.siteId, sourceKey: option.sourceKey, quality: quality, musicInfo: info.bytes)
            let execution = try await work(label: label) { try await scripts.resolve(request: request) }
            try executedScript(execution.source, from: snapshot)
            try inspectionCheck(seed)
            let live = try await sourceDeclaration(scripts, installationID: option.installationId, label: label)
            try sameSource(live, execution.source); try inspectionScriptPin(live, option: option, quality: quality)
            audio = execution.value
            resolvedOption = .init(installationId: live.installationID, generation: live.generation,
                siteId: option.siteId, sourceKey: option.sourceKey, qualities: option.qualities,
                title: option.title, version: live.version, contentHash: live.contentHash, revisionHash: live.revisionHash)
        }
        try await admitRecordingSource(seed.song, sourceKey: option.sourceKey)
        try inspectionCheck(seed)
        try admitSource(seed.song, selection: selection)
        let valid = try media(audio)
        try validateDelivery(valid, song: staged, playback: seed.playback, automaticQuality: automaticQuality)
        // No attempt mutation, queue/history write, feedback, cache-body fill,
        // audio session or native playback admission on this inspection path.
        return .init(audio: valid, option: resolvedOption)
    }

    func inspectionAdopt(_ seed: MusicSourceInspectionSeed, staged: MJ, option: MusicPlaybackOption,
                         quality: String, occurrenceID: String, expectedRevision: Int,
                         label: String) async throws {
        try inspectionCheck(seed)
        try musicRequire(seed.ref.kind == .queue && seed.ref.id == occurrenceID &&
            seed.state.revision == expectedRevision, .targetChanged)
        let (source, _) = try target(staged, option.sourceKey)
        if option.installationId.hasPrefix("official:") {
            guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform),
                  Self.officialEnabled(source.platform, seed.playback) else {
                throw MusicServiceError.sourceUnavailable
            }
            let live = try await work(label: label) {
                try await accounts.nativeOptions(platform: platform, nativeID: source.nativeId, sourceKey: source.sourceKey)
            }
            try inspectionCheck(seed)
            guard live.count == 1, let value = live.first, value.installationId == option.installationId,
                  value.generation == option.generation, value.contentHash == option.contentHash,
                  value.version == option.version, value.sourceKey == option.sourceKey,
                  value.siteId == option.siteId, value.qualities.contains(quality) else { throw MusicServiceError.targetChanged }
        } else {
            guard let scripts else { throw MusicServiceError.sourceUnavailable }
            try musicRequire(source.platform == option.siteId, .targetChanged)
            let live = try await work(label: label) { try await scripts.source(installationID: option.installationId) }
            try inspectionCheck(seed)
            try inspectionScriptPin(live, option: option, quality: quality)
        }
        try await admitRecordingSource(seed.song, sourceKey: option.sourceKey)
        try inspectionCheck(seed)
    }

    /// Records "this recording -> sourceKey" as a confirmed mismatch, then removes
    /// the source from the current occurrence through the existing queue CAS.
    /// Library songs are not rewritten; the primary source cannot be rejected.
    func inspectionReject(_ seed: MusicSourceInspectionSeed, sourceKey: String, occurrenceID: String,
                          expectedRevision: Int) async throws -> MusicSourceInspectionSeed {
        try inspectionCheck(seed)
        try musicRequire(seed.ref.kind == .queue && seed.ref.id == occurrenceID &&
            seed.state.revision == expectedRevision, .targetChanged)
        try musicRequire(seed.song["sources"].array.first?["sourceKey"].string != sourceKey, .invalidRequest)
        try await excludeRecordingSource(seed.song, sourceKey: sourceKey)
        try inspectionCheck(seed)
        let rows = seed.song["sources"].array
        guard rows.contains(where: { $0["sourceKey"].string == sourceKey }) else { return seed }
        return try seed.lifetime.perform { try transaction(["queue"]) { d in
            try musicRequire(d.queue["revision"].int == seed.state.revision &&
                d.playback["revision"].int == seed.state.settingsRevision, .staleRevision)
            guard d.queue["currentId"].string == occurrenceID,
                  let index = d.queue["entries"].array.firstIndex(where: { $0["id"].string == occurrenceID }),
                  try MusicModel.cleanSong(d.queue["entries"].array[index]["song"]) == seed.song else {
                throw MusicServiceError.targetChanged
            }
            var song = seed.song
            song["sources"] = .array(rows.filter { $0["sourceKey"].string != sourceKey })
            var entries = d.queue["entries"].array
            entries[index]["song"] = song
            d.queue["entries"] = .array(entries)
            try musicRequire(d.queue["revision"].int < 9_007_199_254_740_991, .limit)
            d.queue["revision"] = .n(d.queue["revision"].int + 1)
            return .init(ref: seed.ref, song: song, state: stateDTO(d), playback: seed.playback,
                         epoch: seed.epoch, lifetime: seed.lifetime)
        } }
    }
}

// Only the ambiguous permission code needs stage information. Other errors keep
// their existing classification; unknown errors are not wrapped as declarations.
extension NativeMusicService {
    func sourceDeclaration(_ scripts: any MusicScriptResolving, installationID: String,
                           label: String) async throws -> MusicScriptSource {
        do { return try await work(label: label) { try await scripts.source(installationID: installationID) } }
        catch MusicServiceError.permissionDenied {
            throw MusicSourceDeclarationFailure(failure: .permissionDenied)
        }
    }
}
