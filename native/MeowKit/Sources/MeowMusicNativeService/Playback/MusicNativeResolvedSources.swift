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

extension NativeMusicService: MusicResolvedSourcesServing {
    public func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession {
        let d = try read()
        let seed = MusicSourceInspectionSeed(ref: reference, song: try song(reference, d),
            state: stateDTO(d), playback: d.playback, epoch: epoch, lifetime: MusicNativeCandidateDiscoveryLifetime())
        return MusicNativeSourceInspection(owner: self, seed: seed, clock: now)
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
                            force: Bool) async throws -> MJ {
        try inspectionCheck(seed)
        let cache = await sourceDiscoveryEntries(seed.song)
        try inspectionCheck(seed)
        let staged = mergeSourceDiscoveryCache(seed.song, entries: cache)
        guard MusicNativeCandidateDiscoveryMerge.needsDiscovery(staged, force: force, cache: cache) else {
            return staged
        }
        let query = String((seed.song["title"].string + " " + seed.song["artist"].string).prefix(200))
        let platforms = MusicNativeSearchProtocol.platforms.filter {
            platformEnabled($0) && MusicNativeCandidateDiscoveryMerge.needsPlatform($0, song: staged, force: force, cache: cache[$0])
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
        let merged = try MusicNativeCandidateDiscoveryMerge.merge(staged, observations: observations).song
        for observation in observations {
            guard let page = observation.page else { continue }
            await recordSourceDiscovery(staged, platform: observation.platform, page: page)
            try inspectionCheck(seed)
        }
        return merged
    }

    private func inspectionSearch(_ seed: MusicSourceInspectionSeed, platform: String,
                                  query: String, label: String) async throws -> MusicNativeTrackPage {
        try inspectionCheck(seed); try requirePlatform(platform)
        let port = publicPlatforms
        let value = try await MusicCandidateDiscoveryDeadline.run(seconds: 12, timing: .init()) {
            try await self.work(label: label) {
                try await port.search(platform: platform, query: query, page: 1, limit: 30)
            }
        }
        try inspectionCheck(seed)
        try musicRequire(value.total >= 0 && value.items.allSatisfy({ $0.platform == platform }), .upstreamInvalid)
        return value
    }

    func inspectionOptions(_ seed: MusicSourceInspectionSeed, staged: MJ, label: String,
                           receive: @escaping @Sendable ([MusicPlaybackOption]) async -> Void) async throws {
        try inspectionCheck(seed)
        let rows = try MusicModel.sources(staged).filter { platformEnabled($0.platform) }
        try await withThrowingTaskGroup(of: [MusicPlaybackOption].self) { group in
            for source in rows { group.addTask { try await self.inspectionOption(seed, source: source, label: label) } }
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
                let qualities = Self.officialQualities(value.qualities, seed.playback)
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
            if ![.authRequired, .sessionExpired, .verificationRequired, .unsupported, .sourceUnavailable, .unavailable,
                 .upstreamFailed, .upstreamInvalid, .timeout, .resolutionTimeout, .busy, .rateLimited].contains(failure) {
                throw failure
            }
            return []
        }
    }

    func inspectionResolve(_ seed: MusicSourceInspectionSeed, staged: MJ,
                           option: MusicPlaybackOption, quality: String, label: String) async throws -> MusicNativeAudio {
        try inspectionCheck(seed)
        let (source, _) = try target(staged, option.sourceKey)
        guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform) else {
            throw MusicServiceError.sourceUnavailable
        }
        try requirePlatform(source.platform)
        try candidateValidateOfficial(option, source: source)
        try musicRequire(option.qualities.contains(quality) && !Self.officialQualities([quality], seed.playback).isEmpty, .qualityDisabled)
        let selection = option.selection(quality: quality)
        let audio = try await work(label: label) {
            try await accounts.nativeResolve(platform: platform, nativeID: source.nativeId, selection: selection)
        }
        try inspectionCheck(seed)
        let valid = try media(audio)
        try validateDelivery(valid.delivery, song: staged, playback: seed.playback)
        // No attempt mutation, queue/history write, feedback, cache-body fill,
        // audio session or native playback admission on this inspection path.
        return valid
    }

    func inspectionAdopt(_ seed: MusicSourceInspectionSeed, staged: MJ, option: MusicPlaybackOption,
                         quality: String, occurrenceID: String, expectedRevision: Int,
                         label: String) async throws -> MusicSourceInspectionSeed {
        try inspectionCheck(seed)
        try musicRequire(seed.ref.kind == .queue && seed.ref.id == occurrenceID &&
            seed.state.revision == expectedRevision, .targetChanged)
        let (source, _) = try target(staged, option.sourceKey)
        guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform) else {
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
        let next = try candidateCommit(.init(occurrenceID: occurrenceID, song: seed.song,
            state: seed.state, epoch: seed.epoch, lifetime: seed.lifetime), staged: staged)
        return .init(ref: seed.ref, song: next.song, state: next.state, playback: seed.playback,
                     epoch: seed.epoch, lifetime: seed.lifetime)
    }
}

actor MusicNativeSourceInspection: MusicSourceInspectionSession {
    private struct Resolved {
        let choice: MusicResolvedSource
        let option: MusicPlaybackOption
        let audio: MusicNativeAudio
    }
    private let owner: NativeMusicService
    private let clock: @Sendable () -> Date
    private struct Job {
        let key: String
        let option: MusicPlaybackOption
        let quality: String
        let tier: Int
        let sequence: Int
        var provider: String { option.installationId + ":" + option.siteId }
    }
    private let id = UUID()
    private var revision = UUID()
    private var label: String { "sources:" + id.uuidString + ":" + revision.uuidString }
    private var seed: MusicSourceInspectionSeed
    private var staged: MJ
    private var pending: [Job] = []
    private var resolved: [Resolved] = []
    private var registered = Set<String>(), busy = Set<String>(), scouted = Set<String>()
    private var running: [String: Task<Void, Never>] = [:]
    private var preparation: Task<Void, Never>?, discovery: Task<Void, Never>?
    private var authorizations: [ObjectIdentifier: Task<Void, Never>] = [:]
    private var observers: [UUID: AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>.Continuation] = [:]
    private var frontier = 0, maximumDispatchedTier = 0
    private var automatic = true, scoutProviders = true
    private var prepared = false, adopting = false, closed = false

    init(owner: NativeMusicService, seed: MusicSourceInspectionSeed, clock: @escaping @Sendable () -> Date) {
        self.owner = owner; self.seed = seed; staged = seed.song; self.clock = clock
    }
    private func check() throws {
        try seed.lifetime.check()
        try Task.checkCancellation()
        guard !closed else { throw MusicServiceError.closed }
    }
    private func rank(_ quality: String) -> Int {
        NativeMusicService.officialQualities(
            ["128k", "192k", "320k", "flac", "flac24bit", "master", "jyeffect", "dolby", "sky", "vivid"],
            seed.playback).firstIndex(of: quality) ?? 100
    }
    private var order: [String] {
        let policy = MusicModel.policy(seed.playback)
        return policy.order.filter { policy.enabled.contains($0) }
    }
    private func category(_ quality: String) -> String {
        ["128k": "standard", "192k": "high", "320k": "high", "flac": "lossless",
         "flac24bit": "hires", "master": "master", "jyeffect": "enhanced",
         "dolby": "spatial", "sky": "spatial", "vivid": "spatial"][quality] ?? ""
    }
    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> {
        AsyncThrowingStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            guard !closed else { continuation.finish(throwing: MusicServiceError.closed); return }
            let id = UUID()
            observers[id] = continuation
            continuation.yield(snapshot())
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeObserver(id) }
            }
        }
    }
    private func removeObserver(_ id: UUID) { observers.removeValue(forKey: id) }
    private func snapshot() -> MusicResolvedSourcesSnapshot {
        resolved.removeAll { $0.audio.playbackAuthorization?.isActive == false ||
            ($0.audio.expiresAt.map { $0 <= clock() } ?? false) }
        let choices = resolved.enumerated().sorted {
            let a = rank($0.element.choice.delivery?.quality ?? ""), b = rank($1.element.choice.delivery?.quality ?? "")
            return a == b ? $0.offset < $1.offset : a < b
        }.map { $0.element.choice }
        return .init(sources: choices, hasMore: !closed && frontier < order.count - 1,
                     isLoading: !closed && (preparation != nil || discovery != nil || !running.isEmpty))
    }
    private func publish() {
        let value = snapshot()
        for continuation in observers.values { continuation.yield(value) }
    }
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot {
        try check()
        guard !adopting else { throw MusicServiceError.busy }
        try await owner.inspectionCheck(seed)
        try check()
        guard !adopting else { throw MusicServiceError.busy }
        if refresh {
            let oldLabel = label
            resetWork()
            stopAuthorizationObservers()
            resolved = []
            frontier = min(max(frontier, maximumDispatchedTier), max(0, order.count - 1))
            automatic = false; scoutProviders = false
            Task { await owner.candidateCancelWork(oldLabel) }
        } else if prepared {
            frontier = min(frontier + 1, max(0, order.count - 1))
        }
        if !prepared {
            prepared = true
            prepare(force: refresh)
        }
        pump(); publish()
        return snapshot()
    }
    private func prepare(force: Bool) {
        let own = revision, owner = owner, captured = seed, label = label
        staged = seed.song
        preparation = Task { [weak self] in
            do {
                try await owner.inspectionOptions(captured, staged: captured.song, label: label) { [weak self] options in
                    await self?.preparedOptions(options, song: nil, revision: own, completed: false)
                }
                await self?.preparedOptions([], song: nil, revision: own, completed: true)
            } catch { await self?.preparationFailed(error, discovery: false, revision: own) }
        }
        discovery = Task { [weak self] in
            do {
                let song = try await owner.inspectionDiscover(captured, label: label, force: force)
                if song == captured.song {
                    await self?.preparedOptions([], song: song, revision: own, completed: true)
                    return
                }
                try await owner.inspectionOptions(captured, staged: song, label: label) { [weak self] options in
                    await self?.preparedOptions(options, song: song, revision: own, completed: false)
                }
                await self?.preparedOptions([], song: song, revision: own, completed: true)
            } catch { await self?.preparationFailed(error, discovery: true, revision: own) }
        }
    }
    private func requestID(_ option: MusicPlaybackOption, _ quality: String) -> String {
        option.id + ":" + String(option.generation) + ":" + quality
    }
    private func preparedOptions(_ options: [MusicPlaybackOption], song: MJ?, revision own: UUID, completed: Bool) {
        guard !closed, own == revision, !Task.isCancelled else { return }
        if let song {
            staged = song
            if completed { discovery = nil }
            let keys = Set(song["sources"].array.map { $0["sourceKey"].string })
            resolved.removeAll { !keys.contains($0.choice.selection.sourceKey) }
            pending.removeAll { !keys.contains($0.option.sourceKey) }
        } else if completed { preparation = nil }
        for option in options {
            for quality in option.qualities {
                let key = requestID(option, quality)
                guard let tier = order.firstIndex(of: category(quality)), !registered.contains(key) else { continue }
                let sequence = registered.count
                registered.insert(key)
                pending.append(.init(key: key, option: option, quality: quality, tier: tier, sequence: sequence))
            }
        }
        pump(); publish()
    }
    private func preparationFailed(_ error: Error, discovery: Bool, revision own: UUID) {
        guard !closed, own == revision else { return }
        if discovery { self.discovery = nil } else { preparation = nil }
        if recoverable(error) { pump(); publish() } else { fail(error) }
    }
    private func recoverable(_ error: Error) -> Bool {
        let failure = MusicServiceError.sanitized(error)
        return MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
            failure.isAuthenticationFailure || [.verificationRequired, .qualityDisabled].contains(failure)
    }
    private func pump() {
        guard !closed, !adopting else { return }
        // Each exact provider has one current request; quality tiers prioritize
        // useful work without imposing a shared request quota.
        pending.sort { $0.tier == $1.tier ? $0.sequence < $1.sequence : $0.tier < $1.tier }
        while true {
            let ready = pending.indices.filter { !busy.contains(pending[$0].provider) }
            guard let index = ready.first(where: {
                pending[$0].tier <= frontier || scoutProviders && !scouted.contains(pending[$0].provider)
            }) else {
                if automatic, !ready.isEmpty, frontier < order.count - 1 { frontier += 1; continue }
                break
            }
            let job = pending.remove(at: index), own = revision, captured = seed, song = staged, label = label
            let owner = owner
            busy.insert(job.provider); scouted.insert(job.provider)
            maximumDispatchedTier = max(maximumDispatchedTier, job.tier)
            running[job.key] = Task { [weak self] in
                let result: Result<MusicNativeAudio, Error>
                do {
                    let audio = try await MusicCandidateDiscoveryDeadline.run(seconds: 8.5, timing: .init()) {
                        try await owner.inspectionResolve(captured, staged: song, option: job.option,
                                                          quality: job.quality, label: label)
                    }
                    result = .success(audio)
                } catch { result = .failure(error) }
                await self?.finished(job, result: result, revision: own)
            }
        }
    }
    private func finished(_ job: Job, result: Result<MusicNativeAudio, Error>, revision own: UUID) {
        guard !closed, own == revision else { return }
        running.removeValue(forKey: job.key); busy.remove(job.provider)
        switch result {
        case .success(let audio):
            guard audio.playbackAuthorization?.isActive != false else { fail(MusicServiceError.staleAccess); return }
            guard staged["sources"].array.contains(where: { $0["sourceKey"].string == job.option.sourceKey }) else {
                pump(); publish(); return
            }
            let id = job.option.id + ":" + (audio.delivery?.quality ?? "unknown")
            if !resolved.contains(where: { $0.choice.id == id }) {
                resolved.append(.init(choice: .init(id: id, selection: job.option.selection(quality: job.quality),
                    title: job.option.title, delivery: audio.delivery), option: job.option, audio: audio))
                observeAuthorization(audio.playbackAuthorization)
            }
            if let delivery = audio.delivery {
                let group = category(delivery.quality)
                if ["lossless", "hires", "master"].contains(group) ||
                    (["spatial", "enhanced"].contains(group) ? delivery.quality == job.quality : group == order.first) {
                    automatic = false
                }
            }
        case .failure(let error):
            if !recoverable(error) { fail(error); return }
        }
        pump(); publish()
    }
    private func resetWork() {
        revision = UUID()
        preparation?.cancel(); preparation = nil; discovery?.cancel(); discovery = nil
        for task in running.values { task.cancel() }
        running = [:]; pending = []; registered = []; busy = []; scouted = []; prepared = false
    }
    private func observeAuthorization(_ authorization: MusicQueueAuthorization?) {
        guard let authorization else { return }
        let id = ObjectIdentifier(authorization)
        guard authorizations[id] == nil else { return }
        let events = authorization.revocations()
        authorizations[id] = Task { [weak self] in
            for await _ in events {
                guard !Task.isCancelled else { return }
                await self?.authorizationRevoked()
                return
            }
        }
    }
    private func stopAuthorizationObservers() {
        for task in authorizations.values { task.cancel() }
        authorizations = [:]
    }
    private func authorizationRevoked() {
        guard !closed else { return }
        fail(MusicServiceError.staleAccess)
    }
    private func fail(_ error: Error) {
        // Cancelling our own revocation observer must not reclassify the
        // account failure as caller cancellation.
        let failure = MusicServiceError.sanitized(error)
        let oldLabel = label
        stopAuthorizationObservers()
        closed = true; seed.lifetime.close(); resetWork(); resolved = []
        for continuation in observers.values {
            continuation.yield(.init(sources: [], hasMore: false, isLoading: false))
            continuation.finish(throwing: failure)
        }
        observers = [:]
        Task { await owner.candidateCancelWork(oldLabel) }
    }
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection {
        try check()
        guard !adopting, let value = resolved.first(where: { $0.choice.id == sourceID }) else {
            throw MusicServiceError.staleResult
        }
        try value.audio.playbackAuthorization?.check()
        let oldLabel = label
        resetWork()
        adopting = true
        defer {
            adopting = false
            if !closed, !Task.isCancelled { prepared = true; prepare(force: false); pump(); publish() }
        }
        await owner.candidateCancelWork(oldLabel)
        try check()
        seed = try await owner.inspectionAdopt(seed, staged: staged, option: value.option,
            quality: value.choice.selection.quality, occurrenceID: occurrenceID,
            expectedRevision: expectedQueueRevision, label: label)
        try check()
        try value.audio.playbackAuthorization?.check()
        // The subsequent existing resolve reuses/revalidates the account URL
        // cache, or refreshes an expired entry. Inspection is not permission
        // to play a stale URL or switch to a different account.
        return value.choice.selection
    }
    func close() async {
        guard !closed else { return }
        let oldLabel = label
        stopAuthorizationObservers()
        closed = true; seed.lifetime.close(); resetWork(); resolved = []
        for continuation in observers.values { continuation.finish() }
        observers = [:]
        await owner.candidateCancelWork(oldLabel)
    }
    deinit {
        preparation?.cancel(); discovery?.cancel()
        for task in running.values { task.cancel() }
        for task in authorizations.values { task.cancel() }
    }
}
