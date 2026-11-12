import Foundation
import MeowMusicFeature

struct MusicNativeCandidateDiscoverySeed: Sendable {
    let occurrenceID: String
    let song: MJ
    let state: MusicPlaybackState
    let epoch: UUID
    let lifetime: MusicNativeCandidateDiscoveryLifetime
    var cacheRecovery: MusicNativeCacheRecoveryPin? = nil
}

/// Queued callbacks and owner admission share the same close fence.
final class MusicNativeCandidateDiscoveryLifetime: @unchecked Sendable {
    private let lock = NSRecursiveLock()
    private var closed = false
    func close() { lock.lock(); closed = true; lock.unlock() }
    func perform<T>(_ operation: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        if closed { throw MusicServiceError.closed }
        return try operation()
    }
    func check() throws { try perform {} }
}

extension NativeMusicService: MusicCandidateDiscoveryServing {
    /// Current business decisions filter additions, never the owner's existing sources.
    func filteringRecordingSourceAdditions(_ song: MJ, existing: [MJ], playback: MJ) -> MJ {
        let descriptor = MusicModel.descriptor(song)
        let excluded = Set(playback["recordingSourceExclusions"].array.filter {
            $0["fingerprint"].string == descriptor.fingerprint &&
                descriptor.isCompatible(with: .init(title: "", artist: "", duration: $0["duration"].number), sameNative: true)
        }.map { $0["sourceKey"].string })
        guard !excluded.isEmpty else { return song }
        let retained = Set(existing.map { $0["sourceKey"].string })
        var rows = song["sources"].array.filter {
            retained.contains($0["sourceKey"].string) || !excluded.contains($0["sourceKey"].string)
        }
        if let primary = song["sources"].array.first?["sourceKey"],
           !rows.contains(where: { $0["sourceKey"] == primary }),
           let original = existing.first?["sourceKey"],
           let index = rows.firstIndex(where: { $0["sourceKey"] == original }) {
            rows.insert(rows.remove(at: index), at: 0)
        }
        var filtered = song
        filtered["sources"] = .array(MusicModel.orderedSources(rows))
        return filtered
    }

    public func beginCandidateDiscovery(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicCandidateDiscoverySession {
        try candidateDiscoverySession(occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }

    func candidateDiscoverySession(occurrenceID: String, expectedQueueRevision: Int,
        timing: MusicCandidateDiscoveryTiming = .init(),
        cacheRecovery: MusicNativeCacheRecoveryPin? = nil) throws -> MusicNativeCandidateDiscoverySession {
        let d = try read()
        try musicRequire(d.queue["revision"].int == expectedQueueRevision, .staleRevision)
        try musicRequire(d.queue["currentId"].string == occurrenceID, .targetChanged)
        let seed = MusicNativeCandidateDiscoverySeed(occurrenceID: occurrenceID,
            song: try song(.init(kind: .queue, id: occurrenceID), d), state: stateDTO(d), epoch: epoch,
            lifetime: MusicNativeCandidateDiscoveryLifetime(), cacheRecovery: cacheRecovery)
        return MusicNativeCandidateDiscoverySession(owner: self, seed: seed, timing: timing)
    }

    @discardableResult
    func candidateCheck(_ seed: MusicNativeCandidateDiscoverySeed) throws -> MusicDocument {
        try seed.lifetime.check()
        try check(seed.epoch)
        if let recovery = seed.cacheRecovery { try checkCachedPlaybackRecovery(recovery) }
        let d = try read()
        try musicRequire(d.queue["currentId"].string == seed.occurrenceID, .targetChanged)
        try musicRequire(d.queue["revision"].int == seed.state.revision &&
            d.playback["revision"].int == seed.state.settingsRevision, .staleRevision)
        try musicRequire(try song(.init(kind: .queue, id: seed.occurrenceID), d) == seed.song, .targetChanged)
        return d
    }

    func candidateSearch(platform: String, query: String, label: String,
                         seed: MusicNativeCandidateDiscoverySeed) async throws -> MusicNativeTrackPage {
        try candidateCheck(seed)
        try requirePlatform(platform)
        let request = beginSourceDiscoveryObservation(platform: platform, query: query)
        let port = publicPlatforms
        // Discovery reads native rows without registering display handles.
        do {
            let page = try await work(label: label) {
                try await port.search(platform: platform, query: query, page: 1, limit: 30)
            }
            try candidateCheck(seed)
            try musicRequire(page.total >= 0 &&
                page.items.allSatisfy { $0.platform == platform }, .upstreamInvalid)
            await recordSourceDiscovery(seed.song, platform: platform, page: page,
                                        observation: finishSourceDiscoveryObservation(request))
            try candidateCheck(seed)
            return page
        } catch {
            if (try? candidateCheck(seed)) != nil {
                let failure = MusicServiceError.sanitized(error)
                if failure != .cancelled {
                    await recordSourceDiscovery(seed.song, platform: platform, rows: [],
                        observation: finishSourceDiscoveryObservation(request, failure: failure))
                }
            }
            throw error
        }
    }

    func candidateCancelWork(_ label: String) { cancelWork(label) }

    /// Automatic discovery searches only platforms whose rows can become a
    /// playback candidate now: official accounts that could answer, plus enabled,
    /// available script sites declaring musicUrl. Local state and the script list
    /// only; no account restore or platform request.
    func candidateDiscoveryScope(seed: MusicNativeCandidateDiscoverySeed, label: String) async throws -> Set<String> {
        try candidateCheck(seed)
        var result = Set<String>()
        if let accounts {
            let platforms: Set<MusicAccountPlatform>
            if let provider = accounts as? any MusicNativeCandidateScopeProviding {
                platforms = await provider.candidatePlatforms()
            } else { platforms = Set(MusicAccountPlatform.allCases) }
            let playback = try candidateCheck(seed).playback
            result.formUnion(platforms.map(\.rawValue).filter { Self.officialEnabled($0, playback) })
        }
        if let scripts {
            do {
                let sources = try await work(label: label) { try await scripts.sources() }
                for source in sources where source.isCandidate {
                    for capability in source.capabilities where capability.actions.contains("musicUrl") {
                        result.insert(capability.siteID)
                    }
                }
            } catch {
                // Declaration failures are not recording-content answers.
                // Only recoverable source failures may narrow the scope.
                let failure = MusicServiceError.sanitized(error)
                guard MusicCandidateDiscoveryFailures.mayTryAnother(failure) else { throw failure }
            }
        }
        try candidateCheck(seed)
        return result
    }
}

/// Scoped orchestration only. Durable writes and all media attempts stay on owner.
actor MusicNativeCandidateDiscoverySession: MusicCandidateDiscoverySession {
    let owner: NativeMusicService
    let timing: MusicCandidateDiscoveryTiming
    let label = "candidate:" + UUID().uuidString.lowercased()
    let seed: MusicNativeCandidateDiscoverySeed
    var staged: MJ
    var searchIssues: [MusicCandidateDiscoveryIssue] = []
    var searched = false, discovering = false, resolving = false, closed = false
    /// Platforms whose known targets failed in this session, by failure kind.
    var contentFailed = Set<String>(), otherFailed = Set<String>()
    var ownedAttempt: String?
    var resolvingID: String?
    var resolutionTask: Task<MusicPlaybackCandidate, Error>?

    init(owner: NativeMusicService, seed: MusicNativeCandidateDiscoverySeed,
         timing: MusicCandidateDiscoveryTiming) {
        self.owner = owner; self.seed = seed; staged = seed.song; self.timing = timing
    }
    func check() throws {
        if closed { throw MusicServiceError.closed }
        if Task.isCancelled { throw MusicServiceError.cancelled }
    }
    func snapshot(discover: Bool, force: Bool) async throws -> MusicCandidateDiscoverySnapshot {
        try check()
        try await owner.candidateCheck(seed)
        try check()
        let cache = try await owner.sourceDiscoveryState(seed.song)
        let cached = cache.current(window: MusicSourceDiscoveryWindow.playback)
        staged = await owner.mergeSourceDiscoveryCache(staged, entries: cached)
        try check(); try await owner.candidateCheck(seed); try check()
        if discover, force || !searched {
            guard !discovering else { throw MusicServiceError.busy }
            discovering = true
            defer { discovering = false }
            // Refresh keeps every enabled platform; automatic playback only its target platforms.
            let scope: Set<String>?
            if force { scope = nil }
            else {
                scope = try await owner.candidateDiscoveryScope(seed: seed, label: label)
                try check(); try await owner.candidateCheck(seed); try check()
            }
            let captured = seed, owner = owner, label = label, timing = timing
            let query = String((seed.song["title"].string + " " + seed.song["artist"].string).prefix(200))
            let contentFailed = contentFailed, otherFailed = otherFailed
            // A platform whose known sources all received content answers in this
            // session is searched again only when its check is missing or 7+ days old.
            func recheck(_ platform: String) -> Bool {
                contentFailed.contains(platform) && !otherFailed.contains(platform) &&
                    (cache.checks[platform].map { cache.at - $0.checkedAt >= MusicSourceDiscoveryWindow.recheck } ?? true)
            }
            let platforms = MusicNativeCandidateDiscoveryMerge.searchable(staged)
                ? MusicNativeSearchProtocol.platforms.filter {
                    owner.platformEnabled($0) && scope?.contains($0) != false &&
                        (MusicNativeCandidateDiscoveryMerge.needsPlatform($0, song: staged, force: force, cache: cached[$0]) &&
                            (force || !cache.hasCurrentCheck($0, window: MusicSourceDiscoveryWindow.playback)) ||
                            recheck($0))
                } : []
            let results = try await withThrowingTaskGroup(of: MusicNativeCandidateDiscoveryObservation.self) { group in
                for platform in platforms {
                    group.addTask {
                        do {
                            let page = try await MusicCandidateDiscoveryDeadline.run(seconds: 12, timing: timing) {
                                try await owner.candidateSearch(platform: platform, query: query, label: label, seed: captured)
                            }
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
                for try await result in group { values.append(result) }
                return values
            }
            try check(); try await owner.candidateCheck(seed); try check()
            // An empty scope (or no enabled platform needing discovery) is not a
            // completed search. Keep durable five-platform evidence unchanged;
            // never manufacture empty successes for disabled platforms.
            searchIssues = []; searched = false
            if !results.isEmpty {
                let merged = try MusicNativeCandidateDiscoveryMerge.merge(staged, observations: results, excluded: cache.excluded)
                staged = merged.song; searchIssues = merged.issues
                searched = results.contains(where: { $0.page != nil })
            }
        }
        let song = staged, captured = seed
        let options = try await owner.candidateOptions(song: song, seed: captured, label: label, timing: timing)
        try check(); try await owner.candidateCheck(seed); try check()
        return .init(track: try MusicModel.track(song, .init(kind: .queue, id: seed.occurrenceID)), state: seed.state,
            options: options.rows, issues: (discover ? searchIssues : []) + options.issues,
            scriptState: options.scriptState, searched: discover && searched)
    }
    func resolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String) async throws -> MusicPlaybackCandidate {
        try check()
        guard !resolving, !discovering else { throw MusicServiceError.busy }
        resolving = true; ownedAttempt = attemptID; resolvingID = attemptID
        let owner = owner, captured = seed, recording = staged, label = label
        let task = Task { try await owner.candidateResolve(target, attemptID: attemptID, seed: captured, recording: recording, label: label) }
        resolutionTask = task
        defer {
            if resolvingID == attemptID { resolving = false; resolvingID = nil; resolutionTask = nil }
        }
        do {
            let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            try check()
            return result
        } catch {
            let platform = target.selection.siteId
            if !(error is MusicSourceDeclarationFailure),
               MusicCandidateDiscoveryFailures.isContentFailure(MusicServiceError.sanitized(error)) {
                contentFailed.insert(platform)
            } else { otherFailed.insert(platform) }
            await owner.cancelCandidateAttempt(attemptID)
            if ownedAttempt == attemptID { ownedAttempt = nil }
            throw error
        }
    }
    func cancel(attemptID: String) async {
        if resolvingID == attemptID {
            resolutionTask?.cancel(); resolutionTask = nil; resolvingID = nil; resolving = false
        }
        if ownedAttempt == attemptID { ownedAttempt = nil }
        await owner.cancelCandidateAttempt(attemptID)
    }
    func close(retainingAttemptID: String?) async {
        closed = true
        seed.lifetime.close()
        resolutionTask?.cancel(); resolutionTask = nil; resolvingID = nil; resolving = false
        let cancel = ownedAttempt == retainingAttemptID ? nil : ownedAttempt
        ownedAttempt = nil
        await owner.candidateCancelWork(label)
        if let cancel { await owner.cancelCandidateAttempt(cancel) }
    }
}
