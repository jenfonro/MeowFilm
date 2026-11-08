import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor RecordingDiscoveryScripts: MusicScriptResolving {
    static let identity = MusicScriptIdentity(version: "1", contentHash: String(repeating: "a", count: 64),
        revisionHash: String(repeating: "a", count: 64))
    let qualities: [String]
    let providers: [String]
    var failures: [String: MusicServiceError] = [:]
    var requests: [MusicScriptRequest] = []
    private var held: (String, String, MusicNativeCandidateDiscoveryBarrier)?
    init(qualities: [String] = ["flac", "128k"], providers: [String] = ["recording-script"]) {
        self.qualities = qualities; self.providers = providers
    }
    func hold(provider: String, sourceKey: String, gate: MusicNativeCandidateDiscoveryBarrier) {
        held = (provider, sourceKey, gate)
    }
    func fail(_ key: String, with error: MusicServiceError) { failures[key] = error }
    func sources() -> [MusicScriptSource] { providers.map(snapshot) }
    func source(installationID: String) -> MusicScriptSource { snapshot(installationID) }
    private func snapshot(_ provider: String) -> MusicScriptSource {
        .init(installationID: provider, generation: 1, enabled: true, state: .ready, version: Self.identity.version,
              contentHash: Self.identity.contentHash, revisionHash: Self.identity.revisionHash,
              capabilities: [.init(siteID: "tx", title: "Recording", actions: ["musicUrl"], qualities: qualities)])
    }
    func resolve(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeAudio> {
        let executed = snapshot(request.installationID)
        requests.append(request)
        if let failure = failures[request.sourceKey] { throw failure }
        let value = MusicNativeAudio(url: URL(string: "https://media.invalid/recording/\(requests.count)")!,
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180))
        if let (provider, key, gate) = held, provider == request.installationID, key == request.sourceKey {
            held = nil; await gate.enter()
        }
        return .init(value: value, source: executed)
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func calls() -> [MusicScriptRequest] { requests }
}

final class MusicSourceRecordingDiscoveryTests: XCTestCase {
    private func selection(_ key: String, quality: String = "flac") -> MusicPlaybackSelection {
        .init(installationId: "recording-script", generation: 1, siteId: "tx", sourceKey: key, quality: quality,
              scriptIdentity: RecordingDiscoveryScripts.identity)
    }
    private func snapshot(_ session: any MusicSourcePlaybackSession) async throws -> MusicResolvedSourcesSnapshot {
        var iterator = await session.updates().makeAsyncIterator()
        let value = try await iterator.next()
        return try XCTUnwrap(value)
    }

    func testRecordingRejectionSurvivesLibraryRequeueWithoutProviderExclusion() async throws {
        for reject in [false, true] {
            let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "primary"))
            let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "peer"))
            let song = try MusicModel.mergeSources(primary, adding: peer["sources"].array)
            let primaryKey = primary["sources"].array[0]["sourceKey"].string
            let peerKey = peer["sources"].array[0]["sourceKey"].string
            let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
            let scripts = RecordingDiscoveryScripts()
            let service = try await NativeMusicFixture.service(store, scripts: scripts)
            let collections = try await service.collections()
            let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
            _ = try await service.editPlaylist(favorites, name: nil,
                refs: [.init(kind: .queue, id: "entry-1")], order: nil)
            let libraryBefore = try MusicDocument(store.read(), runID: "recording").library
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let rejected = try await session.match(intent: .exact(selection(peerKey)))
            await service.discardSource(attemptID: rejected.candidate.attemptId)
            let discovery = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let originalOptions = try await discovery.snapshot(discover: false, force: false)
            let oldTarget = try XCTUnwrap(originalOptions.options.first { $0.playback.sourceKey == peerKey })
                .target(quality: "flac")
            await discovery.close(retainingAttemptID: nil)
            let legal = try await session.match(intent: .exact(selection(primaryKey)))
            await service.discardSource(attemptID: legal.candidate.attemptId)
            if reject {
                try await session.reject(sourceKey: peerKey, occurrenceID: "entry-1", expectedQueueRevision: 1)
            }
            XCTAssertEqual(try MusicDocument(store.read(), runID: "recording").library, libraryBefore,
                           "Recording rejection must not rewrite the saved library")
            let state = try await service.state()
            let next = try await service.changeQueue(.replace([.init(kind: .library, id: song["songId"].string)], index: 0),
                                                      expectedRevision: state.revision)
            let occurrence = try XCTUnwrap(next.currentId)
            let fresh = try await service.sourceSession(occurrenceID: occurrence, expectedQueueRevision: next.revision)
            let visible = try await snapshot(fresh)
            XCTAssertEqual(visible.sources.contains { $0.selection.sourceKey == peerKey }, !reject)
            XCTAssertTrue(visible.sources.contains { $0.selection.sourceKey == primaryKey })
            let callsBefore = await scripts.calls()
            if reject {
                do {
                    _ = try await fresh.match(intent: .exact(selection(peerKey)))
                    XCTFail("An explicit historical selection must not bypass recording exclusion")
                } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .sourceUnavailable) }
                // The retained public resolver must enforce the same recording admission.
                let attempt = UUID().uuidString.lowercased()
                do {
                    _ = try await service.resolve(attemptId: attempt, occurrenceId: occurrence, selection: selection(peerKey))
                    XCTFail("Legacy resolution admitted an excluded recording")
                } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .sourceUnavailable) }
                do {
                    _ = try await service.record(.init(attemptId: attempt, occurrenceId: occurrence,
                        resourceId: "music:" + attempt, kind: .playing, position: 0, duration: 180))
                    XCTFail("Rejected resolution cannot own a playing fact")
                } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .staleResult) }
                let oldConsumer = try await service.beginCandidateDiscovery(occurrenceID: occurrence,
                                                                            expectedQueueRevision: next.revision)
                do {
                    _ = try await oldConsumer.resolve(oldTarget, attemptID: UUID().uuidString.lowercased())
                    XCTFail("A retained discovery target must not bypass recording exclusion")
                } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .sourceUnavailable) }
                await oldConsumer.close(retainingAttemptID: nil)
            } else {
                let reused = try await fresh.match(intent: .exact(selection(peerKey)))
                XCTAssertEqual(reused.candidate.url, rejected.candidate.url)
                await service.discardSource(attemptID: reused.candidate.attemptId)
            }
            let valid = try await fresh.match(intent: .quality("flac"))
            XCTAssertEqual(valid.selection.sourceKey, reject ? primaryKey : peerKey)
            if reject { XCTAssertNotEqual(valid.candidate.url, rejected.candidate.url) }
            let exclusions = try await service.sourceExclusions(), history = try await service.playedSongs()
            XCTAssertTrue(exclusions.isEmpty)
            XCTAssertTrue(history.isEmpty)
            if !reject {
                let callsAfter = await scripts.calls()
                XCTAssertEqual(callsAfter.count, callsBefore.count, "Unrejected URL history remains reusable")
            }
            await service.close()
        }
    }

    func testRecordingRejectionDoesNotBlockAnotherRecordingUsingTheSourceKey() async throws {
        let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "primary"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "peer"))
        let song = try MusicModel.mergeSources(primary, adding: peer["sources"].array)
        let key = peer["sources"].array[0]["sourceKey"].string
        let otherPrimary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "other", title: "Other recording"))
        let otherPeer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "peer", title: "Other recording"))
        let other = try MusicModel.mergeSources(otherPrimary, adding: otherPeer["sources"].array)
        XCTAssertNotEqual(song["songId"], other["songId"])
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        var document = try MusicDocument(store.read(), runID: "other-recording")
        document.queue["entries"] = .array(document.queue["entries"].array + [
            .obj([("id", .string("entry-2")), ("song", other)])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        let service = try await NativeMusicFixture.service(store, scripts: RecordingDiscoveryScripts())
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.match(intent: .exact(selection(key)))
        try await session.reject(sourceKey: key, occurrenceID: "entry-1", expectedQueueRevision: 1)
        let state = try await service.state()
        let selected = try await service.changeQueue(.select("entry-2"), expectedRevision: state.revision)
        let next = try await service.sourceSession(occurrenceID: "entry-2", expectedQueueRevision: selected.revision)
        let result = try await next.match(intent: .exact(selection(key)))
        XCTAssertEqual(result.selection.sourceKey, key)
        try await service.activateSource(attemptID: result.candidate.attemptId)
        let accepted = try await service.record(.init(attemptId: result.candidate.attemptId, occurrenceId: "entry-2",
            resourceId: result.candidate.resourceId, kind: .playing, position: 0, duration: 180))
        XCTAssertTrue(accepted)
        let exclusions = try await service.sourceExclusions()
        XCTAssertTrue(exclusions.isEmpty)
        await service.close()
    }

    func testLowerTierContentFailureRechecksEightDayMappingOnce() async throws {
        try await recheck(ageDays: 8, failure: .audioUnavailable, searches: 1)
    }
    func testLowerTierContentFailureKeepsThreeDayMapping() async throws {
        try await recheck(ageDays: 3, failure: .audioUnavailable, searches: 0)
    }
    func testLowerTierNetworkFailureDoesNotTriggerContentRecheck() async throws {
        try await recheck(ageDays: 8, failure: .networkOffline, searches: 0)
    }
    func testSuccessfulLowerTierMappingDoesNotRecheckByAge() async throws {
        try await recheck(ageDays: 8, failure: nil, searches: 0)
    }
    func testSharedSessionKeepsTwentyFourHourNegativeWindowAndPlatformIntersection() async throws {
        for hours in [23.0, 25.0] {
            let song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("kw", id: "123"))
            let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
            await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: "tx",
                checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000 - hours * 3_600_000, sources: [])
            let scripts = RecordingDiscoveryScripts(qualities: ["128k"])
            let qq = MusicNativeCandidateDiscoveryQQ(items: [MusicNativeCandidateDiscoveryFixture.track(id: "new")])
            let http = MusicNativeCandidateDiscoveryHTTP()
            let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: scripts, qq: qq, sourceDiscovery: cache, enabledPlatforms: [.qq, .kuwo, .netease],
                transport: http, clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            do {
                let result = try await session.match(intent: .automatic)
                XCTAssertEqual(hours, 25)
                XCTAssertEqual(result.selection.siteId, "tx")
                XCTAssertEqual(result.selection.quality, "128k")
            } catch {
                XCTAssertEqual(hours, 23)
                XCTAssertEqual(MusicServiceError.sanitized(error), .sourceUnavailable)
            }
            let calls = await qq.calls(), other = await http.calls()
            XCTAssertEqual(calls.count, hours == 25 ? 1 : 0)
            XCTAssertTrue(other.isEmpty, "Undeclared platforms must not acquire searches")
            await service.close()
        }
    }

    func testCancellationDuringConditionalRecheckCannotPublishReplacement() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song(), row = song["sources"].array[0]
        let scripts = RecordingDiscoveryScripts(qualities: ["128k"])
        await scripts.fail(row["sourceKey"].string, with: .audioUnavailable)
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let checkedAt = NativeMusicFixture.date.timeIntervalSince1970 * 1000 - 8 * 86_400_000
        await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: "tx",
            checkedAt: checkedAt, sources: [.init(sourceKey: row["sourceKey"].string, source: "tx",
                                                musicInfo: row["musicInfo"].json)])
        let gate = MusicNativeCandidateDiscoveryBarrier()
        let qq = MusicNativeCandidateDiscoveryQQ(barrier: gate,
            items: [MusicNativeCandidateDiscoveryFixture.track(id: "new")])
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, qq: qq, sourceDiscovery: cache, enabledPlatforms: [.qq],
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let task = Task { try await session.match(intent: .automatic) }
        await gate.started(1)
        task.cancel()
        await gate.release()
        do { _ = try await task.value; XCTFail("Cancelled recheck published a replacement") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .cancelled) }
        let calls = await scripts.calls(), searches = await qq.calls()
        XCTAssertEqual(calls.map(\.sourceKey), [row["sourceKey"].string])
        XCTAssertEqual(searches.count, 1)
        let entries = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertEqual(entries["tx"]?.checkedAt, checkedAt)
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty)
        await service.close()
    }
    private func recheck(ageDays: Double, failure: MusicServiceError?, searches: Int) async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song(), row = song["sources"].array[0]
        let key = row["sourceKey"].string, scripts = RecordingDiscoveryScripts(qualities: ["128k"])
        if let failure { await scripts.fail(key, with: failure) }
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: "tx",
            checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000 - ageDays * 86_400_000,
            sources: [.init(sourceKey: key, source: "tx", musicInfo: row["musicInfo"].json)])
        let replacement = MusicNativeCandidateDiscoveryFixture.track(id: "replacement")
        let replacementKey = try MusicModel.normalize(replacement)["sources"].array[0]["sourceKey"].string
        let qq = MusicNativeCandidateDiscoveryQQ(items: [replacement])
        let service = NativeMusicService(document: try MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, qq: qq, sourceDiscovery: cache, enabledPlatforms: [.qq],
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        do {
            let result = try await session.match(intent: .automatic)
            XCTAssertTrue(failure == nil || searches == 1)
            XCTAssertEqual(result.selection.quality, "128k")
            XCTAssertEqual(result.selection.sourceKey, searches == 1 ? replacementKey : key)
            await service.discardSource(attemptID: result.candidate.attemptId)
        } catch {
            XCTAssertNotNil(failure)
            XCTAssertEqual(searches, 0)
            XCTAssertEqual(MusicServiceError.sanitized(error), .sourceUnavailable)
        }
        let calls = await qq.calls(), resolutions = await scripts.calls()
        XCTAssertEqual(calls.count, searches)
        XCTAssertEqual(resolutions.map(\.sourceKey), searches == 1 ? [key, replacementKey] : [key])
        XCTAssertTrue(resolutions.allSatisfy { $0.siteID == "tx" && $0.quality == "128k" })
        // A later attempt/page cannot turn this condition into a search on every tier.
        await session.beginSelection()
        _ = try? await session.match(intent: .automatic)
        let repeated = await qq.calls()
        XCTAssertEqual(repeated.count, searches)
        let exclusions = try await service.sourceExclusions()
        XCTAssertTrue(exclusions.isEmpty)
        await service.close()
    }
}

private actor RecordingPublicationCache: MusicSourceDiscoveryCaching {
    nonisolated var persistsRecords: Bool { false }
    let base = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
    private var entriesGate: MusicNativeCandidateDiscoveryBarrier?
    func holdEntries(_ gate: MusicNativeCandidateDiscoveryBarrier) { entriesGate = gate }
    func entries(fingerprint: String, duration: Double) async -> [String: MusicSourceDiscoveryEntry] {
        let value = await base.entries(fingerprint: fingerprint, duration: duration)
        if let gate = entriesGate { entriesGate = nil; await gate.enter() }
        return value // Deliberately return the old snapshot after public rejection.
    }
    func excluded(fingerprint: String, duration: Double) async -> Set<String> {
        await base.excluded(fingerprint: fingerprint, duration: duration)
    }
    func exclude(fingerprint: String, duration: Double, sourceKey: String) async {
        await base.exclude(fingerprint: fingerprint, duration: duration, sourceKey: sourceKey)
    }
    func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                sources: [MusicSourceDiscoverySource]) async {
        await base.record(fingerprint: fingerprint, duration: duration, platform: platform,
                          checkedAt: checkedAt, sources: sources)
    }
    func persist(retaining recordings: [MusicSourceDiscoveryRecording]) {}
}

// Only the final-window test injects the already-confirmed session notification.
// The public rejection/CAS/cache chain is exercised separately below.
private final class RecordingPublicationDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    let base: NativeMusicMemoryDocument
    private let lock = NSLock()
    private var remaining = 0, notifications = 0
    private var action: (@Sendable () throws -> Void)?
    init(_ base: NativeMusicMemoryDocument) { self.base = base }
    func arm(afterReads: Int, action: @escaping @Sendable () throws -> Void) {
        lock.lock(); defer { lock.unlock() }
        remaining = afterReads; self.action = action
    }
    func read() throws -> Data? {
        lock.lock()
        var notify: (@Sendable () throws -> Void)?
        if action != nil {
            remaining -= 1
            if remaining == 0 { notify = action; action = nil; notifications += 1 }
        }
        lock.unlock()
        try notify?()
        return try base.read()
    }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        try base.compareAndSwap(expected: expected, replacement: replacement)
    }
    func notificationCount() -> Int { lock.lock(); defer { lock.unlock() }; return notifications }
}

@MainActor private final class RecordingPublicationPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var stops = 0, handoffs = 0, resumes = 0
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        handoffs += 1
        return .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) { resumes += 1 }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

extension MusicSourceRecordingDiscoveryTests {
    private struct PublicationFixture {
        let service: NativeMusicService
        let scripts: RecordingDiscoveryScripts
        let cache: RecordingPublicationCache
        let document: RecordingPublicationDocument
        let session: any MusicSourcePlaybackSession
        let model: MusicViewModel
        let player: RecordingPublicationPlayer
        let song: MJ
        let primary: String, rejected: String, other: String
        let donor: MusicPlaybackCandidate
        let history: MJ
    }
    private func publicationSelection(_ provider: String, _ key: String) -> MusicPlaybackSelection {
        .init(installationId: provider, generation: 1, siteId: "tx", sourceKey: key, quality: "flac",
              scriptIdentity: RecordingDiscoveryScripts.identity)
    }
    @MainActor private func publicationFixture(adopted: Bool) async throws -> PublicationFixture {
        let primary = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "primary"))
        let rejected = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "rejected"))
        let other = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "other"))
        let peers = rejected["sources"].array + other["sources"].array
        let song = try (adopted ? MusicModel.mergeSources(primary, adding: peers) : primary)
        let document = RecordingPublicationDocument(try MusicNativeCandidateDiscoveryFixture.store(song: song))
        let scripts = RecordingDiscoveryScripts(qualities: ["flac"], providers: ["a", "b"])
        let cache = RecordingPublicationCache()
        await cache.record(fingerprint: MusicModel.fingerprint(song), duration: 180, platform: "tx",
            checkedAt: NativeMusicFixture.date.timeIntervalSince1970 * 1000,
            sources: peers.map { .init(sourceKey: $0["sourceKey"].string, source: "tx", musicInfo: $0["musicInfo"].json) })
        let service = NativeMusicService(document: document, scripts: scripts, qq: MusicNativeCandidateDiscoveryQQ(items: []),
            sourceDiscovery: cache, enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(),
            clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let player = RecordingPublicationPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load()
        await model.playbackMetadata.flush()
        let p = primary["sources"].array[0]["sourceKey"].string
        await model.choose(publicationSelection("a", p))
        let donor = try XCTUnwrap(model.candidate)
        model.onPlayback(.init(attemptId: donor.attemptId, occurrenceId: donor.occurrenceId,
            resourceId: donor.resourceId, kind: .playing, position: 23, duration: 180))
        await model.flushPlaybackFacts()
        await model.playbackMetadata.flush()
        XCTAssertEqual(model.phase, .playing); XCTAssertNil(model.error)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: model.state.revision)
        return .init(service: service, scripts: scripts, cache: cache, document: document, session: session,
            model: model, player: player, song: song, primary: p,
            rejected: rejected["sources"].array[0]["sourceKey"].string,
            other: other["sources"].array[0]["sourceKey"].string, donor: donor,
            history: try MusicDocument(document.read(), runID: "publication").history)
    }
    private func publicationUpdates(_ updates: AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>,
                                    queueChanged: Bool = false) async throws -> [MusicResolvedSourcesSnapshot] {
        try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            var values: [MusicResolvedSourcesSnapshot] = []
            do {
                for try await value in updates {
                    values.append(value)
                    if !value.isLoading { return values }
                }
            } catch {
                // A real queue CAS invalidates the older owner's pending read.
                // The session itself remains usable with its updated seed.
                guard queueChanged, error as? MusicServiceError == .targetChanged else { throw error }
                return values
            }
            throw MusicServiceError.closed
        }
    }
    private func publicationReady(_ fixture: PublicationFixture) async throws -> MusicResolvedSourcesSnapshot {
        let keys = Set([fixture.primary, fixture.rejected, fixture.other])
        let session = fixture.session
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates()
                where keys.isSubset(of: Set(value.sources.filter { $0.selection.installationId == "a" }.map(\.selection.sourceKey))) {
                return value
            }
            throw MusicServiceError.closed
        }
    }
    @MainActor private func assertPublicationDonor(_ f: PublicationFixture) async throws {
        XCTAssertEqual(f.player.resources.map(\.id), [f.donor.resourceId])
        XCTAssertEqual(f.player.stops, 0); XCTAssertEqual(f.player.handoffs, 0); XCTAssertEqual(f.player.resumes, 0)
        XCTAssertEqual(f.model.activeResourceID, f.donor.resourceId)
        XCTAssertEqual(f.model.activePlaybackSelection, publicationSelection("a", f.primary))
        XCTAssertEqual(f.model.phase, .playing); XCTAssertEqual(f.model.current?.id, "entry-1")
        XCTAssertTrue(f.donor.playbackAuthorization?.isActive == true)
        let accepted = try await f.service.record(.init(attemptId: f.donor.attemptId, occurrenceId: "entry-1",
            resourceId: f.donor.resourceId, kind: .progress, position: 24, duration: 180))
        XCTAssertTrue(accepted, "Recording rejection cannot retire the healthy primary's fact owner")
        XCTAssertEqual(try MusicDocument(f.document.read(), runID: "publication").history, f.history)
        let providers = try await f.service.sourceExclusions()
        XCTAssertTrue(providers.isEmpty, "A recording decision must never become a provider exclusion")
    }
    @MainActor private func closePublication(_ f: PublicationFixture) async {
        await f.model.close(); await f.service.close()
    }

    @MainActor func testPublicRecordingRejectionWithdrawsInFlightRowsAndKeepsOtherRecordings() async throws {
        for adopted in [false, true] { try await publicRecordingRejection(adopted: adopted, rejected: "r") }
    }
    @MainActor func testPublicOtherSourceKeyRejectionKeepsTheInFlightRecording() async throws {
        try await publicRecordingRejection(adopted: false, rejected: "s")
    }
    @MainActor func testUnrejectedInFlightRecordingStillPublishes() async throws {
        try await publicRecordingRejection(adopted: false, rejected: nil)
    }
    @MainActor private func publicRecordingRejection(adopted: Bool, rejected: String?) async throws {
        let f = try await publicationFixture(adopted: adopted), gate = MusicNativeCandidateDiscoveryBarrier()
        await f.scripts.hold(provider: "b", sourceKey: f.rejected, gate: gate)
        _ = try await f.session.loadMore(refresh: false)
        await gate.started(1)
        let ready = try await publicationReady(f)
        XCTAssertTrue(ready.isLoading)
        let key = rejected.map { $0 == "r" ? f.rejected : f.other }
        if let key {
            let row = try XCTUnwrap(ready.sources.first { $0.selection.sourceKey == key })
            await f.model.rejectInspectedSource(row, session: f.session)
            XCTAssertNil(f.model.error)
        }
        let queue = try await f.service.queue()
        XCTAssertEqual(queue.revision, adopted && key != nil ? 2 : 1)
        let excluded = await f.cache.excluded(fingerprint: MusicModel.fingerprint(f.song), duration: 180)
        XCTAssertEqual(excluded, key.map { Set([$0]) } ?? [])
        let updates = await f.session.updates()
        await gate.release()
        let observed = try await publicationUpdates(updates, queueChanged: adopted && key != nil)
        XCTAssertFalse(observed.isEmpty)
        for value in observed {
            XCTAssertTrue(value.sources.contains { $0.selection.sourceKey == f.primary })
            XCTAssertTrue(value.sources.contains { $0.selection.sourceKey == (key == f.other ? f.rejected : f.other) })
            if let key { XCTAssertFalse(value.sources.contains { $0.selection.sourceKey == key }) }
        }
        let calls = await f.scripts.calls()
        let lease = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        var iterator = await lease.updates().makeAsyncIterator()
        let value = try await iterator.next(), visible = try XCTUnwrap(value)
        let keys = Set(visible.sources.map(\.selection.sourceKey))
        XCTAssertEqual(keys, Set([f.primary, f.rejected, f.other]).subtracting(key.map { [$0] } ?? []))
        if key != f.rejected {
            XCTAssertTrue(visible.sources.contains { $0.selection == publicationSelection("b", f.rejected) })
        }
        let afterInspect = await f.scripts.calls()
        XCTAssertEqual(afterInspect.count, calls.count, "Inspect reuses the session, not a new lookup to hide the late result")
        try await assertPublicationDonor(f)
        await lease.close(); await closePublication(f)
    }

    @MainActor func testOldDiscoveryReceiptCannotReintroduceRejectedRecordingThroughStagedAdoption() async throws {
        let f = try await publicationFixture(adopted: false)
        _ = try await f.session.loadMore(refresh: false)
        let first = try await publicationUpdates(await f.session.updates())
        let ready = try XCTUnwrap(first.last)
        let rejected = try XCTUnwrap(ready.sources.first { $0.selection.sourceKey == f.rejected })
        let survivor = try XCTUnwrap(ready.sources.first { $0.selection == publicationSelection("a", f.other) })
        let gate = MusicNativeCandidateDiscoveryBarrier()
        await f.cache.holdEntries(gate)
        _ = try await f.session.loadMore(refresh: true)
        await gate.started(1)
        await f.model.rejectInspectedSource(rejected, session: f.session)
        XCTAssertNil(f.model.error)
        let unchanged = try await f.service.queue()
        XCTAssertEqual(unchanged.revision, 1, "Rejecting an unadopted mapping changes no queue revision")
        let updates = await f.session.updates()
        await gate.release()
        for value in try await publicationUpdates(updates) {
            XCTAssertFalse(value.sources.contains { $0.selection.sourceKey == f.rejected })
            XCTAssertTrue(value.sources.contains { $0.selection.sourceKey == f.primary })
            XCTAssertTrue(value.sources.contains { $0.selection.sourceKey == f.other })
        }
        let before = await f.scripts.calls()
        let prepared = try await f.session.match(intent: .exact(survivor.selection))
        XCTAssertEqual(prepared.selection, survivor.selection)
        let after = await f.scripts.calls()
        XCTAssertEqual(after.count, before.count, "The other discovered recording retains its usable URL")
        let queue = try await f.service.queue()
        XCTAssertEqual(Set(queue.entries[0].track.sources.map(\.sourceKey)), [f.primary, f.other],
                       "Choosing a lawful peer cannot adopt the rejected key from a late staged snapshot")
        await f.service.discardSource(attemptID: prepared.candidate.attemptId)
        try await assertPublicationDonor(f)
        await closePublication(f)
    }

    @MainActor func testRecordingNotificationAtFinalOwnerBoundaryFencesPublicationWithoutHidingOtherKeys() async throws {
        for rejectingTarget in [true, false] {
            let f = try await publicationFixture(adopted: true)
            // This injection counts synchronous document reads. Drain the
            // unrelated initial radio observer rather than relying on timing.
            let radioObservation = f.model.similarObservation
            f.model.stopSimilarObservation()
            await radioObservation?.value
            for key in [f.rejected, f.other] {
                let prepared = try await f.session.match(intent: .exact(publicationSelection("a", key)))
                await f.service.discardSource(attemptID: prepared.candidate.attemptId)
            }
            let gate = MusicNativeCandidateDiscoveryBarrier()
            await f.scripts.hold(provider: "b", sourceKey: f.rejected, gate: gate)
            let selected = publicationSelection("b", f.rejected)
            let pending = Task { try await f.session.match(intent: .exact(selected)) }
            await gate.started(1)
            let native = try XCTUnwrap(f.session as? MusicNativeSourceInspection)
            let rejectedKey = rejectingTarget ? f.rejected : f.other
            // After audio: external declaration check, final recording check,
            // provider admission, then the session's final owner check. This
            // injects only the confirmed notification, not a fabricated CAS.
            f.document.arm(afterReads: 4) {
                let completed = DispatchSemaphore(value: 0)
                Task.detached { await native.recordingRejected(sourceKey: rejectedKey); completed.signal() }
                guard completed.wait(timeout: .now() + 3) == .success else { throw MusicServiceError.timeout }
            }
            let updates = await f.session.updates()
            await gate.release()
            do {
                let prepared = try await pending.value
                XCTAssertFalse(rejectingTarget, "A confirmed recording rejection must fence the late result")
                XCTAssertEqual(prepared.selection, selected)
                await f.service.discardSource(attemptID: prepared.candidate.attemptId)
            } catch {
                XCTAssertTrue(rejectingTarget)
                XCTAssertEqual(error as? MusicServiceError, .sourceUnavailable)
            }
            XCTAssertEqual(f.document.notificationCount(), 1)
            // Subscribe after notification completion: the earlier stream may
            // correctly contain A/R from before the injected rejection.
            let latest = try await snapshot(f.session)
            XCTAssertFalse(latest.sources.contains { $0.selection.sourceKey == rejectedKey })
            XCTAssertTrue(latest.sources.contains { $0.selection.sourceKey == f.primary })
            XCTAssertTrue(latest.sources.contains { $0.selection.sourceKey == (rejectingTarget ? f.other : f.rejected) })
            XCTAssertEqual(latest.sources.contains { $0.selection == selected }, !rejectingTarget)
            for value in try await publicationUpdates(updates) where !value.isLoading {
                XCTAssertFalse(value.sources.contains { $0.selection.sourceKey == rejectedKey })
            }
            try await assertPublicationDonor(f)
            await closePublication(f)
        }
    }
}
