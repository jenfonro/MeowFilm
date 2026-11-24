import Foundation
import MeowDebug
import MeowMusicFeature

struct MusicNativeResolvedSource: Sendable {
    let option: MusicPlaybackOption
    let quality: String
    let requestedQuality: String
    let audio: MusicNativeAudio
    let resolvedAt: Date
    var id: String { option.id + ":" + String(option.generation) + ":" + quality }
    var attemptKey: String { option.attemptKey(quality: requestedQuality) }
    var choice: MusicResolvedSource {
        .init(id: id, selection: option.selection(quality: quality), title: option.title, delivery: audio.delivery)
    }
}

struct MusicNativePreparedSource: Sendable {
    let seed: MusicSourceInspectionSeed
    let result: MusicNativeResolvedSource
    let candidate: MusicPlaybackCandidate
    var cache: MusicNativeAudioCacheAttempt?
    var armed = false
}

struct MusicNativePreparedCacheSource: Sendable {
    let playback: MusicCachedPlayback
    let songID: String
    let cache: MusicNativeAudioCacheAttempt
    let queueRevision: Int
    let settingsRevision: Int
    var armed = false
}

extension NativeMusicService: MusicSourcePlaybackServing {
    public func sourceConfigurationChanged() async {
        let previous = await sourceSessionStore.invalidateConfiguration()
        for id in preparedSources.filter({ !$0.value.armed }).keys { discardSource(attemptID: id) }
        for id in preparedCacheSources.filter({ !$0.value.armed }).keys { discardSource(attemptID: id) }
        await previous?.close()
    }

    public func sourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicSourcePlaybackSession {
        try await concreteSourceSession(occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }

    func concreteSourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicNativeSourceInspection {
        let d = try read()
        try musicRequire(d.queue.currentID == occurrenceID && d.queue.revision == expectedQueueRevision, .staleRevision)
        let revision = await sourceSessionStore.currentRevision()
        let ref = MusicReference(kind: .queue, id: occurrenceID), song = try song(ref, d)
        let seed = MusicSourceInspectionSeed(ref: ref, song: song, state: stateDTO(d), playback: d.playback,
            epoch: epoch, lifetime: MusicSourceSessionLifetime())
        var excluded: Set<String>?
        while true {
            try inspectionCheck(seed)
            let rev1 = await sourceSessionStore.currentRevision()
            try musicRequire(rev1 == revision, .staleResult)
            if let current = await sourceSessionStore.current() {
                let valid = await current.session.isCurrent()
                try inspectionCheck(seed)
                let rev2 = await sourceSessionStore.currentRevision()
                try musicRequire(rev2 == revision, .staleResult)
                guard await sourceSessionStore.current()?.session === current.session else { continue }
                if current.occurrence == occurrenceID, valid {
                    try current.session.lifetime.check()
                    return current.session
                }
                await sourceSessionStore.clearCurrent(ifMatching: current.session)
                await current.session.close()
                continue
            }
            if let excluded {
                let cachedAll = await sourceSessionStore.cachedResults(songID: song.songID)
                try inspectionCheck(seed)
                let rev3 = await sourceSessionStore.currentRevision()
                try musicRequire(rev3 == revision, .staleResult)
                let cached = try cachedAll.filter { value in
                    guard !excluded.contains(value.option.sourceKey) else { return false }
                    return try !sourceExcluded(song, provider: value.option.installationId, platform: value.option.siteId)
                }
                let session = MusicNativeSourceInspection(owner: self, seed: seed, cached: cached)
                let outcome = await sourceSessionStore.installIfEmpty(
                    occurrence: occurrenceID, session: session, expectedRevision: revision
                )
                if outcome.installed {
                    return session
                }
                await session.close()
                if let existing = outcome.existing, existing.occurrence == occurrenceID {
                    let valid = await existing.session.isCurrent()
                    try inspectionCheck(seed)
                    let rev4 = await sourceSessionStore.currentRevision()
                    try musicRequire(rev4 == revision, .staleResult)
                    if valid {
                        try existing.session.lifetime.check()
                        return existing.session
                    }
                }
                continue
            }
            excluded = try recordingSourceExclusions(song)
        }
    }

    func rememberSource(_ result: MusicNativeResolvedSource, song: MusicSongRecord) async {
        await sourceSessionStore.remember(result, songID: song.songID)
    }

    func cachedSourceAllowed(_ value: MusicNativeResolvedSource, seed: MusicSourceInspectionSeed) async throws -> Bool {
        let excluded = try recordingSourceExclusions(seed.song)
        try inspectionCheck(seed)
        return try !excluded.contains(value.option.sourceKey) &&
            !sourceExcluded(seed.song, provider: value.option.installationId, platform: value.option.siteId) &&
            value.audio.playbackAuthorization?.isActive != false &&
            (value.audio.expiresAt.map { $0 > now() } ?? (now().timeIntervalSince(value.resolvedAt) < 300)) &&
            (!value.option.installationId.hasPrefix("official:") || Self.officialEnabled(value.option.siteId, seed.playback))
    }

    func forgetSource(_ selection: MusicPlaybackSelection, songID: String) async {
        await sourceSessionStore.forget(selection: selection, songID: songID)
    }

    func prepareSource(_ value: MusicNativeResolvedSource, seed: MusicSourceInspectionSeed)
        async throws -> MusicPreparedSource {
        try inspectionCheck(seed)
        let song = try currentSongMetadata(seed.song)
        try admitSource(song, selection: value.choice.selection)
        try value.audio.playbackAuthorization?.check()
        try musicRequire(value.audio.expiresAt.map { $0 > now() } ?? true, .invalidMediaCandidate)
        try validateDelivery(value.audio, song: song, playback: seed.playback, automaticQuality: false)
        let id = UUID().uuidString.lowercased(), audio = value.audio
        let authorization = audio.playbackAuthorization.map { MusicQueueAuthorization(parent: $0) } ?? MusicQueueAuthorization()
        var cache: MusicNativeAudioCacheAttempt?
        do {
            cache = try await prepareAudioCache(audio: audio, song: song, selection: value.choice.selection,
                attemptID: id, playbackAuthorization: authorization, preparing: true)
            try inspectionCheck(seed)
            let song = try currentSongMetadata(seed.song)
            try validateDelivery(audio, song: song, playback: seed.playback, automaticQuality: false)
            let candidate = MusicPlaybackCandidate(attemptId: id, occurrenceId: seed.ref.id, resourceId: "music:" + id,
                url: audio.url, headers: audio.headers, title: song.title,
                startPosition: try read().playback.positionSeconds, requestedQuality: value.quality,
                resolvedSelection: value.choice.selection,
                expiresAt: audio.expiresAt.map { $0.timeIntervalSince1970 * 1000 }, delivery: audio.delivery,
                playbackAuthorization: authorization, byteSource: cache?.source)
            _ = try candidate.resource()
            preparedSources[id] = .init(seed: seed, result: value, candidate: candidate, cache: cache)
            observeAudioCacheCapture(cache)
            return .init(candidate: candidate, selection: value.choice.selection)
        } catch {
            authorization.revoke(); cache?.source?.retire()
            if let cache { cache.authorization.revoke(); access.remove(cache.accessRegistration) }
            throw error
        }
    }

    public func activateSource(attemptID: String) async throws {
        if let value = preparedCacheSources[attemptID] {
            let d = try read(), song = try song(.init(kind: .queue, id: value.playback.candidate.occurrenceId), d)
            try await admitRecordingSource(song, sourceKey: value.playback.selection.sourceKey)
            try musicRequire(preparedCacheSources[attemptID] != nil, .staleResult)
            try checkPreparedCache(value); preparedCacheSources[attemptID]?.armed = true; return
        }
        guard let value = preparedSources[attemptID] else { throw MusicServiceError.staleResult }
        try await admitRecordingSource(value.seed.song, sourceKey: value.result.option.sourceKey)
        try musicRequire(preparedSources[attemptID] != nil, .staleResult)
        try inspectionCheck(value.seed)
        let song = try currentSongMetadata(value.seed.song)
        try admitSource(song, selection: value.result.choice.selection)
        try value.candidate.playbackAuthorization?.check()
        try musicRequire(value.result.audio.expiresAt.map { $0 > now() } ?? true, .invalidMediaCandidate)
        try validateDelivery(value.result.audio, song: song, playback: value.seed.playback, automaticQuality: false)
        preparedSources[attemptID]?.armed = true
    }

    func commitPreparedSource(_ fact: MusicPlaybackFact) throws {
        if let value = preparedCacheSources[fact.attemptId], value.armed {
            let candidate = value.playback.candidate, selection = value.playback.selection
            try value.cache.authorization.check()
            let d = try activeSourceState(occurrenceID: candidate.occurrenceId,
                                          songID: value.songID, selection: selection)
            try musicRequire(fact.resourceId == candidate.resourceId && fact.occurrenceId == candidate.occurrenceId, .staleResult)
            let song = try repositorySong(value.songID, d)
            let isSeamlessHandoff = attempt?.occurrence == candidate.occurrenceId &&
                attempt?.ended == false && attempt?.ready == true &&
                playbackFeedbackAdmission?.retiringReason == nil
            replaceFeedbackAttempt(attemptID: fact.attemptId, occurrenceID: candidate.occurrenceId,
                                   song: song, sourceKey: selection.sourceKey, authorization: value.cache.authorization,
                                   preservingListen: isSeamlessHandoff)
            retireAudioCacheAttempt()
            cancelWork("playback"); spent.insert(fact.attemptId)
            attempt = .init(id: fact.attemptId, occurrence: candidate.occurrenceId, sourceKey: selection.sourceKey,
                            selection: selection, ready: true, authorization: value.cache.authorization)
            audioCacheAttempt = value.cache; preparedCacheSources[fact.attemptId] = nil
            return
        }
        guard let value = preparedSources[fact.attemptId], value.armed else { return }
        try check(value.seed.epoch)
        let selection = value.result.choice.selection
        let d = try activeSourceState(occurrenceID: value.seed.ref.id,
                                      songID: value.seed.song.songID, selection: selection)
        let song = try repositorySong(value.seed.song.songID, d)
        try musicRequire(fact.occurrenceId == value.seed.ref.id && fact.resourceId == value.candidate.resourceId, .staleResult)
        try value.candidate.playbackAuthorization?.check()
        let isSeamlessHandoff = attempt?.occurrence == value.seed.ref.id &&
            attempt?.ended == false && attempt?.ready == true &&
            playbackFeedbackAdmission?.retiringReason == nil
        replaceFeedbackAttempt(attemptID: fact.attemptId, occurrenceID: value.seed.ref.id,
                               song: song, sourceKey: value.result.option.sourceKey,
                               authorization: value.candidate.playbackAuthorization,
                               preservingListen: isSeamlessHandoff)
        retireAudioCacheAttempt()
        cancelWork("playback"); spent.insert(fact.attemptId)
        attempt = .init(id: fact.attemptId, occurrence: value.seed.ref.id, sourceKey: value.result.option.sourceKey,
            selection: selection, authorization: value.candidate.playbackAuthorization)
        preparedSources[fact.attemptId] = nil
        if selection.installationId.hasPrefix("official:"), platformEnabled(selection.siteId),
           Self.officialEnabled(selection.siteId, d.playback) {
            try pinPlaybackRecovery(candidate: value.candidate, song: song, state: d)
        }
        attempt?.ready = true
        audioCacheAttempt = value.cache
    }

    private func activeSourceState(occurrenceID: String, songID: String,
                                   selection: MusicPlaybackSelection) throws -> MusicDomainState {
        let d = try read()
        try musicRequire(d.queue.currentID == occurrenceID, .targetChanged)
        let song = try song(.init(kind: .queue, id: occurrenceID), d)
        try musicRequire(song.songID == songID, .targetChanged)
        let (source, _) = try target(song, selection.sourceKey)
        try musicRequire(source.platform == selection.siteId, .targetChanged)
        try admitSource(song, selection: selection)
        return d
    }

    public func discardSource(attemptID: String) {
        if let value = preparedSources.removeValue(forKey: attemptID) {
            value.candidate.playbackAuthorization?.revoke()
            value.cache?.source?.retire()
            if let cache = value.cache, cache.captureCompleted {
                cache.authorization.revoke(); access.remove(cache.accessRegistration)
            }
        }
        if let value = preparedCacheSources.removeValue(forKey: attemptID) {
            value.cache.readLease?.release()
            value.cache.authorization.revoke(); access.remove(value.cache.accessRegistration)
        }
    }

    func clearPreparedSources() {
        for value in preparedSources.values {
            value.candidate.playbackAuthorization?.revoke()
            value.cache?.source?.retire()
            if let cache = value.cache, cache.captureCompleted {
                cache.authorization.revoke(); access.remove(cache.accessRegistration)
            }
        }
        preparedSources = [:]
        for value in preparedCacheSources.values {
            value.cache.readLease?.release()
            value.cache.authorization.revoke(); access.remove(value.cache.accessRegistration)
        }
        preparedCacheSources = [:]
    }

    private func checkPreparedCache(_ value: MusicNativePreparedCacheSource) throws {
        let d = try read(), candidate = value.playback.candidate
        try musicRequire(d.queue.currentID == candidate.occurrenceId &&
            d.queue.revision == value.queueRevision && d.playback.revision == value.settingsRevision, .staleRevision)
        try value.cache.authorization.check()
        let song = try song(.init(kind: .queue, id: candidate.occurrenceId), d)
        try admitSource(song, selection: value.playback.selection)
        try musicRequire(MusicMediaDuration.matches(expected: song.effectiveDuration, actual: candidate.delivery?.duration),
                         .invalidMediaCandidate)
    }

    func prepareCachedSource(seed: MusicSourceInspectionSeed, quality: String,
                             selection: MusicPlaybackSelection? = nil) async throws -> MusicPreparedSource? {
        try inspectionCheck(seed)
        let hit = try await cachedPlayback(attemptID: UUID().uuidString.lowercased(), occurrenceID: seed.ref.id,
                                          selection: selection, requestedQuality: quality, preparing: true)
        do { try inspectionCheck(seed) }
        catch { if let hit { discardSource(attemptID: hit.candidate.attemptId) }; throw error }
        return hit.map { .init(candidate: $0.candidate, selection: $0.selection) }
    }
}

actor MusicSourceInspectionLease: MusicSourceInspectionSession {
    let session: any MusicSourcePlaybackSession
    private var closed = false
    init(session: any MusicSourcePlaybackSession) { self.session = session }
    private func check() throws { if closed { throw MusicServiceError.closed } }
    func updates() async -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> {
        guard !closed else { return AsyncThrowingStream { $0.finish(throwing: MusicServiceError.closed) } }
        return await session.updates()
    }
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot {
        try check(); return try await session.loadMore(refresh: refresh)
    }
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection {
        try check(); return try await session.adopt(sourceID: sourceID, occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) async throws {
        try check(); try await session.reject(sourceKey: sourceKey, occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }
    func close() async { closed = true; await session.cancelInspection() }
}

actor MusicNativeSourceInspection: MusicSourcePlaybackSession {
    private static let maxConcurrentProbes = 3
    private let owner: NativeMusicService
    nonisolated let lifetime: MusicSourceSessionLifetime
    private var seed: MusicSourceInspectionSeed
    private var staged: MusicSongRecord
    private var results: [MusicNativeResolvedSource]
    private var files: [MusicResolvedSource] = []
    private var excludedProviders: [String: Set<String>] = [:]
    private var rejectedSourceKeys = Set<String>()
    private var options: [MusicPlaybackOption]?
    private var optionRead: Task<[MusicPlaybackOption], Error>?
    private var discovered = false
    private var checkedDiscoveryPlatforms = Set<String>()
    private var attempted = Set<String>()
    private var contentFailed = Set<String>(), otherFailed = Set<String>()
    private var mediaFailed = Set<String>()
    private var invalidMediaURLs = Set<URL>()
    private var closed = false, matching = false
    private var closing: Task<Void, Never>?
    private var pageTasks: [UUID: Task<Void, Never>] = [:]
    private var busyProviders = Set<String>()
    private var authorizations: [ObjectIdentifier: Task<Void, Never>] = [:]
    private var revision = UUID()
    private let label = "source-session:" + UUID().uuidString
    private var observers: [UUID: AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>.Continuation] = [:]

    init(owner: NativeMusicService, seed: MusicSourceInspectionSeed,
         cached: [MusicNativeResolvedSource]) {
        self.owner = owner; self.seed = seed; self.lifetime = seed.lifetime
        staged = seed.song; results = cached
        attempted = Set(cached.map(\.attemptKey))
    }

    func isCurrent() async -> Bool {
        guard !closed else { return false }
        return (try? await owner.inspectionCheck(seed)) != nil
    }

    private func check() async throws {
        try Task.checkCancellation(); try seed.lifetime.check()
        guard !closed else { throw MusicServiceError.closed }
        try await owner.inspectionCheck(seed)
    }

    private var qualities: [String] { MusicSourceQualityPolicy.inspectionQualities }
    private var pendingContentFailures: Set<String> {
        contentFailed.subtracting(otherFailed).subtracting(checkedDiscoveryPlatforms)
    }

    private func unattempted(_ option: MusicPlaybackOption, quality: String) -> Bool {
        !rejectedSourceKeys.contains(option.sourceKey) &&
            option.qualities.contains(quality) && !attempted.contains(option.attemptKey(quality: quality)) &&
            !mediaFailed.contains(mediaKey(option.selection(quality: quality)))
    }

    private func snapshot() -> MusicResolvedSourcesSnapshot {
        .init(sources: files.filter { !providerExcluded($0.selection) && !rejectedSourceKeys.contains($0.selection.sourceKey) } +
              results.filter { $0.audio.playbackAuthorization?.isActive != false }.map(\.choice),
              hasMore: !closed && (options == nil || !discovered || !pendingContentFailures.isEmpty || qualities.contains { quality in
                  options!.contains { unattempted($0, quality: quality) }
              }),
              isLoading: !pageTasks.isEmpty || matching)
    }

    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> {
        for result in results { observeAuthorization(result.audio.playbackAuthorization) }
        return AsyncThrowingStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            let id = UUID()
            guard !closed else { continuation.finish(throwing: MusicServiceError.closed); return }
            observers[id] = continuation; continuation.yield(snapshot())
            continuation.onTermination = { [weak self] _ in Task { await self?.removeObserver(id) } }
        }
    }

    private func removeObserver(_ id: UUID) { observers[id] = nil }
    private func publish() { let value = snapshot(); for observer in observers.values { observer.yield(value) } }

    private func readOptions(discover: Bool = false, force: Bool = false) async throws -> [MusicPlaybackOption] {
        try await check()
        if let optionRead { return try await optionRead.value }
        if !discover, let options {
            let merged = try await owner.currentSongMetadata(staged)
            try await check()
            staged = withoutRejectedRecordings(merged)
            return options
        }
        let task = Task { try await self.collectOptions(discover: discover, force: force) }
        optionRead = task
        defer { optionRead = nil }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }

    private func collectOptions(discover: Bool, force: Bool) async throws -> [MusicPlaybackOption] {
        if discover, !discovered || force || !pendingContentFailures.isEmpty {
            let failures = pendingContentFailures
            let discovery = try await owner.inspectionDiscover(seed, label: label, force: force,
                contentFailed: failures, recheckOnly: discovered && !force)
            staged = withoutRejectedRecordings(discovery.song)
            checkedDiscoveryPlatforms.formUnion(failures.union(discovery.searched))
            discovered = true; options = nil
        }
        let merged = try await owner.currentSongMetadata(staged)
        try await check()
        staged = withoutRejectedRecordings(merged)
        if let options { return options }
        let collector = MusicSourceOptionCollector()
        try await owner.inspectionOptions(seed, staged: staged, label: label) { await collector.append($0) }
        try await check()
        let collected = await collector.values
        let values = collected.filter { !rejectedSourceKeys.contains($0.sourceKey) }
        options = values
        return values
    }

    private func withoutRejectedRecordings(_ song: MusicSongRecord) -> MusicSongRecord {
        guard !rejectedSourceKeys.isEmpty else { return song }
        var value = song
        value.sources = song.sources.filter { !rejectedSourceKeys.contains($0.sourceKey) }
        return value
    }

    private func providerExcluded(_ selection: MusicPlaybackSelection) -> Bool {
        excludedProviders[selection.siteId]?.contains(
            MusicSourceExclusion.providerID(selection.installationId, platform: selection.siteId)) == true
    }

    private func remember(_ value: MusicNativeResolvedSource) async throws {
        await owner.rememberSource(value, song: staged)
        try Task.checkCancellation(); try lifetime.check()
        try musicRequire(!providerExcluded(value.choice.selection), .sourceUnavailable)
        try musicRequire(!rejectedSourceKeys.contains(value.option.sourceKey), .sourceUnavailable)
        observeAuthorization(value.audio.playbackAuthorization)
        results.removeAll { $0.id == value.id }; results.append(value)
        attempted.insert(value.attemptKey)
        publish()
    }

    private func cached(quality: String, sourceID: String? = nil) async throws -> MusicNativeResolvedSource? {
        for value in results where value.quality == quality && (sourceID == nil || value.id == sourceID) {
            if mediaFailed.contains(mediaKey(value.choice.selection)) { continue }
            if invalidMediaURLs.contains(value.audio.url) { continue }
            guard staged.sources.contains(where: { $0.sourceKey == value.option.sourceKey }),
                  try await owner.cachedSourceAllowed(value, seed: seed) else {
                attempted.remove(value.attemptKey); continue
            }
            if let measured = value.audio.delivery?.quality, measured != quality { continue }
            if !value.option.installationId.hasPrefix("official:") {
                guard try await owner.inspectionCachedScriptCurrent(value.option) else {
                    attempted.remove(value.attemptKey); options = nil; continue
                }
            }
            return value
        }
        return nil
    }

    private func resolve(_ option: MusicPlaybackOption, quality: String,
                         automaticQuality: Bool = false) async throws -> MusicNativeResolvedSource {
        let execution = try await MusicOperationDeadline.run(seconds: 8.5) { [owner, seed, staged, label] in
            try await owner.inspectionResolve(seed, staged: staged, option: option, quality: quality,
                                              label: label, automaticQuality: automaticQuality)
        }
        try await check()
        return .init(option: execution.option, quality: execution.audio.delivery?.quality ?? quality, requestedQuality: quality,
                     audio: execution.audio, resolvedAt: Date())
    }

    private func explore(quality: String, firstOnly: Bool,
                         automaticQuality: Bool = false) async throws -> MusicNativeResolvedSource? {
        let values = try await readOptions()
        let jobs = values.filter { unattempted($0, quality: quality) &&
            !busyProviders.contains($0.installationId + ":" + $0.siteId) }
        let providers = Dictionary(grouping: jobs, by: { $0.installationId + ":" + $0.siteId })
        let orderedProviders = providers.sorted { lhs, rhs in
            let lo = lhs.key.hasPrefix("official:"), ro = rhs.key.hasPrefix("official:")
            if lo != ro { return lo }
            return lhs.key < rhs.key
        }
        let own = revision
        DebugRuntime.log(.musicPlayback, sub: "sourceSession", event: "explore.start", level: .debug, data: .object([
            "songId": .string(staged.songID),
            "quality": .string(quality),
            "providerCount": .int(orderedProviders.count),
            "firstOnly": .bool(firstOnly)
        ]))
        var iterator = orderedProviders.makeIterator()
        return try await withThrowingTaskGroup(of: MusicNativeResolvedSource?.self) { group in
            var inFlight = 0
            while inFlight < Self.maxConcurrentProbes, let (key, options) = iterator.next() {
                busyProviders.insert(key)
                inFlight += 1
                group.addTask { try await self.exploreProvider(key, options: options, quality: quality,
                    firstOnly: firstOnly, automaticQuality: automaticQuality) }
            }
            var winner: MusicNativeResolvedSource?
            while let value = try await group.next() {
                inFlight -= 1
                try await check()
                guard revision == own else { throw MusicServiceError.cancelled }
                if let value, winner == nil { winner = value }
                if winner != nil, firstOnly {
                    DebugRuntime.log(.musicPlayback, sub: "sourceSession", event: "explore.winner", level: .info, data: .object([
                        "songId": .string(staged.songID),
                        "quality": .string(winner!.quality),
                        "provider": .string(winner!.option.installationId),
                        "platform": .string(winner!.option.siteId)
                    ]))
                    group.cancelAll()
                    return winner
                }
                if let (key, options) = iterator.next() {
                    busyProviders.insert(key)
                    inFlight += 1
                    group.addTask { try await self.exploreProvider(key, options: options, quality: quality,
                        firstOnly: firstOnly, automaticQuality: automaticQuality) }
                }
            }
            return winner
        }
    }

    private func exploreProvider(_ key: String, options: [MusicPlaybackOption], quality: String,
                                 firstOnly: Bool, automaticQuality: Bool) async throws -> MusicNativeResolvedSource? {
        defer { busyProviders.remove(key) }
        var winner: MusicNativeResolvedSource?
        for option in options {
            try await check()
            do {
                let value = try await resolve(option, quality: quality, automaticQuality: automaticQuality)
                try await remember(value)
                attempted.insert(option.attemptKey(quality: quality))
                if value.quality == quality {
                    if winner == nil { winner = value }
                    if firstOnly { return winner }
                }
            } catch {
                if error is MusicSourceDeclarationFailure { throw error }
                let failure = MusicServiceError.sanitized(error)
                DebugRuntime.log(.musicPlayback, sub: "sourceSession", event: "probe.failed", level: .debug, data: .object([
                    "songId": .string(staged.songID),
                    "provider": .string(option.installationId),
                    "platform": .string(option.siteId),
                    "quality": .string(quality),
                    "error": .string(failure.rawValue)
                ]))
                guard MusicSourceFailureClassifier.mayTryAnother(failure) ||
                    MusicSourceFailureClassifier.isContentFailure(failure) else { throw failure }
                attempted.insert(option.attemptKey(quality: quality))
                if MusicSourceFailureClassifier.isContentFailure(failure) { contentFailed.insert(option.siteId) }
                else { otherFailed.insert(option.siteId) }
            }
        }
        return winner
    }

    func match(quality: String?, sourceID: String?) async throws -> MusicPreparedSource {
        if let sourceID {
            guard let selection = files.first(where: { $0.id == sourceID })?.selection ??
                results.first(where: { $0.id == sourceID })?.choice.selection else { throw MusicServiceError.notFound }
            return try await match(intent: .exact(selection))
        }
        return try await match(intent: quality.map(MusicSourceIntent.quality) ?? .automatic)
    }

    private func mediaKey(_ selection: MusicPlaybackSelection) -> String {
        selection.installationId + ":" + selection.siteId + ":" + selection.sourceKey + ":" + selection.quality
    }

    func beginSelection() {
        options = nil; attempted = Set(results.map(\.attemptKey)); mediaFailed = []
    }

    func rejectMedia(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) async {
        invalidMediaURLs.insert(candidate.url)
        if candidate.isCachedAudio {
            await owner.discardCachedPlayback(attemptID: candidate.attemptId)
            files.removeAll { mediaKey($0.selection) == mediaKey(selection) }
        } else {
            mediaFailed.insert(mediaKey(selection))
            results.removeAll { $0.choice.selection == selection }
            await owner.forgetSource(selection, songID: staged.songID)
        }
        await owner.discardSource(attemptID: candidate.attemptId)
        publish()
    }

    private func cachedFile(quality: String, selection: MusicPlaybackSelection? = nil) async throws -> MusicPreparedSource? {
        guard let file = try await owner.prepareCachedSource(seed: seed, quality: quality, selection: selection) else { return nil }
        if invalidMediaURLs.contains(file.candidate.url) {
            await owner.discardSource(attemptID: file.candidate.attemptId)
            return nil
        }
        return file
    }

    func match(intent: MusicSourceIntent) async throws -> MusicPreparedSource {
        do { return try await matchSource(intent: intent) }
        catch let declaration as MusicSourceDeclarationFailure { throw declaration.failure }
    }

    private func matchSource(intent: MusicSourceIntent) async throws -> MusicPreparedSource {
        try await check()
        guard !matching else { throw MusicServiceError.busy }
        matching = true; publish()
        defer { matching = false; publish() }
        let pages = Array(pageTasks.values)
        for task in pages { task.cancel() }; for task in pages { await task.value }
        try await check()
        while true {
            let merged = try await owner.currentSongMetadata(staged)
            try await check()
            staged = withoutRejectedRecordings(merged)
            let winner: MusicNativeResolvedSource
            if case .exact(let selected) = intent {
                guard qualities.contains(selected.quality) else { throw MusicServiceError.qualityDisabled }
                if let file = try await cachedFile(quality: selected.quality, selection: selected) {
                    DebugRuntime.log(.musicPlayback, sub: "sourceSession", event: "match.fileCacheHit", level: .info, data: .object([
                        "songId": .string(staged.songID), "quality": .string(selected.quality), "mode": .string("exact")
                    ]))
                    return file
                }
                guard !mediaFailed.contains(mediaKey(selected)) else { throw MusicServiceError.sourceUnavailable }
                let row = results.first { mediaKey($0.choice.selection) == mediaKey(selected) }
                if let row, let hit = try await cached(quality: selected.quality, sourceID: row.id) { winner = hit }
                else {
                    let option = try await owner.inspectionExactOption(seed, staged: staged, selection: selected, label: label)
                    try await check()
                    winner = try await resolve(option, quality: selected.quality)
                    try await remember(winner)
                }
                guard winner.audio.delivery?.quality == nil || winner.audio.delivery?.quality == selected.quality else {
                    throw MusicServiceError.sourceUnavailable
                }
            } else {
                let quality: String?
                if case .quality(let value) = intent { quality = value } else { quality = nil }
                if let quality, !qualities.contains(quality) { throw MusicServiceError.qualityDisabled }
                let automaticQuality = quality == nil
                let requested = quality.map { [$0] } ?? MusicSourceQualityPolicy.ordered(seed.state, automatic: true)
                var found: MusicNativeResolvedSource?
                for tier in requested {
                    if let file = try await cachedFile(quality: tier) {
                        let id = "cache:" + mediaKey(file.selection)
                        files.removeAll { $0.id == id }
                        files.append(.init(id: id, selection: file.selection, title: "本机缓存", delivery: file.candidate.delivery))
                        publish()
                        DebugRuntime.log(.musicPlayback, sub: "sourceSession", event: "match.fileCacheHit", level: .info, data: .object([
                            "songId": .string(staged.songID), "quality": .string(tier), "mode": .string(automaticQuality ? "automatic" : "quality")
                        ]))
                        return file
                    }
                    if let hit = try await cached(quality: tier) { found = hit; break }
                    if let value = try await explore(quality: tier, firstOnly: true, automaticQuality: automaticQuality) { found = value; break }
                    if !discovered || !pendingContentFailures.isEmpty {
                        _ = try await readOptions(discover: true)
                        if let value = try await explore(quality: tier, firstOnly: true, automaticQuality: automaticQuality) { found = value; break }
                    }
                }
                guard let found else { throw MusicServiceError.sourceUnavailable }
                winner = found
            }
            try await owner.admitRecordingSource(seed.song, sourceKey: winner.option.sourceKey)
            try await check()
            do {
                return try await owner.prepareSource(winner, seed: seed)
            } catch {
                try await check()
                guard MusicServiceError.sanitized(error) == .invalidMediaCandidate else { throw error }
                invalidMediaURLs.insert(winner.audio.url)
                mediaFailed.insert(mediaKey(winner.choice.selection))
                results.removeAll { $0.choice.selection == winner.choice.selection }
                await owner.forgetSource(winner.choice.selection, songID: staged.songID)
                publish()
                if case .exact = intent { throw error }
            }
        }
    }

    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot {
        try await check()
        guard !matching else { return snapshot() }
        if refresh {
            let pages = Array(pageTasks.values)
            for task in pages { task.cancel() }; for task in pages { await task.value }
            options = nil; discovered = false; checkedDiscoveryPlatforms = []; attempted = []
        }
        let id = UUID()
        pageTasks[id] = Task { [weak self] in await self?.loadPage(id, refresh: refresh) }
        publish(); return snapshot()
    }

    private func loadPage(_ id: UUID, refresh: Bool) async {
        do {
            let values = try await readOptions(discover: true, force: refresh)
            let next = qualities.first { quality in
                values.contains { unattempted($0, quality: quality) &&
                    !busyProviders.contains($0.installationId + ":" + $0.siteId) }
            }
            if let next {
                _ = try await explore(quality: next, firstOnly: false)
            }
            pageFinished(id, error: nil)
        } catch { pageFinished(id, error: error) }
    }

    private func pageFinished(_ id: UUID, error: Error?) {
        pageTasks[id] = nil
        if let error, !Task.isCancelled {
            for observer in observers.values { observer.finish(throwing: MusicServiceError.sanitized(error)) }
            observers = [:]
        }
        publish()
    }

    func exclusionsChanged(songID: String, provider: String, platform: String, excluded: Bool) {
        guard seed.song.songID == songID else { return }
        func matches(_ selection: MusicPlaybackSelection) -> Bool {
            selection.siteId == platform && MusicSourceExclusion.providerID(selection.installationId, platform: platform) == provider
        }
        if excluded {
            excludedProviders[platform, default: []].insert(provider)
            results.removeAll { matches($0.choice.selection) }
            files.removeAll { matches($0.selection) }
        } else {
            excludedProviders[platform]?.remove(provider)
        }
        options = nil; attempted = Set(results.map(\.attemptKey)); mediaFailed = []
        publish()
    }

    func cancelInspection() {
        for task in pageTasks.values { task.cancel() }
    }

    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection {
        try await check()
        guard seed.ref.id == occurrenceID, seed.state.revision == expectedQueueRevision else { throw MusicServiceError.targetChanged }
        guard let value = results.first(where: { $0.id == sourceID }) else { throw MusicServiceError.staleResult }
        try value.audio.playbackAuthorization?.check()
        try await owner.inspectionAdopt(seed, staged: staged, option: value.option, quality: value.quality,
            occurrenceID: occurrenceID, expectedRevision: expectedQueueRevision, label: label)
        return value.choice.selection
    }

    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) async throws {
        try await check()
        seed = try await owner.inspectionReject(seed, sourceKey: sourceKey, occurrenceID: occurrenceID,
                                                expectedRevision: expectedQueueRevision)
        recordingRejected(sourceKey: sourceKey)
    }

    func recordingRestored(songID: String, sourceKey: String) {
        guard seed.song.songID == songID else { return }
        rejectedSourceKeys.remove(sourceKey); options = nil
        attempted = Set(results.map(\.attemptKey)); mediaFailed = []
        publish()
    }

    func recordingExcluded(songID: String, sourceKey: String) {
        guard seed.song.songID == songID else { return }
        recordingRejected(sourceKey: sourceKey)
    }

    func recordingRejected(sourceKey: String) {
        rejectedSourceKeys.insert(sourceKey)
        staged = withoutRejectedRecordings(staged)
        results.removeAll { $0.option.sourceKey == sourceKey }; options = nil
        files.removeAll { $0.selection.sourceKey == sourceKey }
        publish()
    }

    func close() async {
        if closing == nil {
            let optionTask = optionRead; optionTask?.cancel(); optionRead = nil
            let physicalPages = Array(pageTasks.values), owner = owner, label = label
            closed = true; seed.lifetime.close(); revision = UUID(); cancelInspection()
            for task in authorizations.values { task.cancel() }; authorizations = [:]
            for observer in observers.values { observer.finish() }; observers = [:]
            closing = Task {
                await owner.cancelSourceWork(label)
                _ = await optionTask?.result
                for task in physicalPages { await task.value }
            }
        }
        await closing?.value
    }

    private func observeAuthorization(_ authorization: MusicQueueAuthorization?) {
        guard let authorization else { return }
        let id = ObjectIdentifier(authorization)
        guard authorizations[id] == nil else { return }
        let events = authorization.revocations()
        authorizations[id] = Task { [weak self] in
            for await _ in events {
                guard !Task.isCancelled, let self else { return }
                await authorizationRevoked(); return
            }
        }
    }

    private func authorizationRevoked() async {
        results = []
        for observer in observers.values {
            observer.yield(.init(sources: [], hasMore: false))
            observer.finish(throwing: MusicServiceError.staleAccess)
        }
        observers = [:]; await close()
    }

    deinit { optionRead?.cancel(); for task in pageTasks.values { task.cancel() }; for task in authorizations.values { task.cancel() } }
}

private actor MusicSourceOptionCollector {
    var values: [MusicPlaybackOption] = []
    func append(_ options: [MusicPlaybackOption]) { values += options }
}

private extension MusicPlaybackOption {
    func attemptKey(quality: String) -> String { id + ":" + String(generation) + ":" + quality }
}

extension NativeMusicService {
    func inspectionCachedScriptCurrent(_ option: MusicPlaybackOption) async throws -> Bool {
        guard let scripts else { return false }
        let live: MusicScriptSource
        do { live = try await work { try await scripts.source(installationID: option.installationId) } }
        catch {
            if MusicSourceFailureClassifier.mayTryAnother(MusicServiceError.sanitized(error)) { return false }
            throw error
        }
        return live.isCandidate && live.state == .ready && live.generation == option.generation &&
            live.identity == option.selection(quality: "").scriptIdentity
    }
}
