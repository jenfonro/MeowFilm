import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor OwnershipScripts: MusicScriptResolving {
    let base = NativeMusicScriptFixture()
    let gate: MusicNativeCandidateDiscoveryBarrier
    private var listed = 0, lookedUp: [String] = [], active = 0
    init(_ gate: MusicNativeCandidateDiscoveryBarrier) { self.gate = gate }
    func sources() async throws -> [MusicScriptSource] {
        listed += 1; return await base.sources()
    }
    func source(installationID: String) async throws -> MusicScriptSource {
        lookedUp.append(installationID); active += 1
        defer { active -= 1 }
        let snapshot = try await base.source(installationID: installationID)
        await gate.enter()
        return snapshot
    }
    func resolve(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeAudio> {
        try await base.resolve(request: request)
    }
    func lyrics(request: MusicScriptRequest) async throws -> MusicScriptResult<MusicNativeLyrics> {
        await base.lyrics(request: request)
    }
    func calls() async -> [MusicScriptRequest] { await base.calls() }
    func inventory() -> (listed: Int, lookedUp: [String], active: Int) { (listed, lookedUp, active) }
}

final class MusicSourceSessionOwnershipTests: XCTestCase {
    private func selection(_ service: NativeMusicService) async throws -> MusicPlaybackSelection {
        let queue = try await service.queue()
        let current = try XCTUnwrap(queue.entries.first { $0.id == queue.currentId })
        let source = try XCTUnwrap(current.track.sources.first)
        return .init(installationId: NativeMusicFixture.installation, generation: 1,
                     siteId: source.platform, sourceKey: source.sourceKey, quality: "flac")
    }
    func testConcurrentFirstPlaybackAndInspectionShareResultsAndURLRequest() async throws {
        let gate = MusicNativeCandidateDiscoveryBarrier()
        let scripts = OwnershipScripts(gate)
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let documentBefore = try await service.read(), storedBefore = store.snapshot()
        let selected = try await selection(service)
        let playback = Task { try await session.match(intent: .exact(selected)) }
        await gate.started(1)
        let page = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let retiredPage = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        await retiredPage.close()
        do { _ = try await retiredPage.loadMore(refresh: false); XCTFail("A retired inspection lease cannot load") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
        let held = await scripts.inventory()
        XCTAssertEqual(held.listed, 0); XCTAssertEqual(held.lookedUp, [NativeMusicFixture.installation])
        XCTAssertEqual(held.active, 1)
        await gate.release()
        let first = try await playback.value
        var updates = await page.updates().makeAsyncIterator()
        let view = try await updates.next()
        let row = try XCTUnwrap(view?.sources.first)
        XCTAssertEqual(row.selection, first.selection)
        let reused = try await session.match(quality: nil, sourceID: row.id)
        XCTAssertEqual(reused.candidate.url, first.candidate.url)
        let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1)
        await page.close()
        let afterClose = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let retained = try await afterClose.match(intent: .exact(row.selection))
        XCTAssertEqual(retained.candidate.url, first.candidate.url, "Closing a page lease keeps the shared session")
        let finalCalls = await scripts.calls(); XCTAssertEqual(finalCalls.count, 1)
        let completed = await scripts.inventory()
        XCTAssertEqual(completed.listed, 0); XCTAssertEqual(Set(completed.lookedUp), [NativeMusicFixture.installation])
        XCTAssertEqual(completed.active, 0)
        let documentAfter = try await service.read()
        XCTAssertEqual(documentAfter.queue, documentBefore.queue)
        XCTAssertEqual(documentAfter.playback, documentBefore.playback)
        XCTAssertEqual(documentAfter.history, documentBefore.history)
        XCTAssertEqual(store.snapshot().0, storedBefore.0); XCTAssertEqual(store.snapshot().2, storedBefore.2)
        await service.close()
    }

    func testConfigurationChangeDuringFirstMatchCannotPublishOldResult() async throws {
        try await interruptedFirstMatch(.configuration)
    }
    func testQueueChangeDuringFirstMatchCannotPublishOldResult() async throws {
        try await interruptedFirstMatch(.queue)
    }
    func testCloseDuringFirstMatchCannotPublishOldResult() async throws {
        try await interruptedFirstMatch(.close)
    }
    func testSettingsChangeDuringFirstMatchCannotPublishOldResult() async throws {
        try await interruptedFirstMatch(.settings)
    }
    private enum Interruption { case configuration, queue, close, settings }
    private func interruptedFirstMatch(_ interruption: Interruption) async throws {
        let gate = MusicNativeCandidateDiscoveryBarrier()
        let scripts = OwnershipScripts(gate)
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let selected = try await selection(service)
        let task = Task { try await session.match(intent: .exact(selected)) }
        await gate.started(1)
        var closing: Task<Void, Never>?
        var completed: XCTestExpectation?
        switch interruption {
        case .configuration:
            let premature = expectation(description: "configuration waits for physical inventory")
            premature.isInverted = true
            let finished = expectation(description: "configuration finished after inventory")
            completed = finished
            closing = Task {
                await service.sourceConfigurationChanged(); premature.fulfill(); finished.fulfill()
            }
            while await service.currentSourceSession != nil { await Task.yield() }
            await fulfillment(of: [premature], timeout: 0.05)
        case .queue: _ = try await service.changeQueue(.clear, expectedRevision: 1)
        case .close:
            let premature = expectation(description: "close waits for physical inventory")
            premature.isInverted = true
            let finished = expectation(description: "close finished after inventory")
            completed = finished
            closing = Task {
                await service.close(); premature.fulfill(); finished.fulfill()
            }
            while await service.available { await Task.yield() }
            await fulfillment(of: [premature], timeout: 0.05)
        case .settings: _ = try await service.updateSettings(.init(ifRevision: 0, requestQuality: "128k"))
        }
        let held = await scripts.inventory(), changed = store.snapshot()
        XCTAssertEqual(held.active, 1)
        XCTAssertEqual(held.lookedUp, [NativeMusicFixture.installation])
        await gate.release()
        if let completed { await fulfillment(of: [completed], timeout: 3) }
        await closing?.value
        do { _ = try await task.value; XCTFail("Invalidated first match published a result") }
        catch {
            let failure = MusicServiceError.sanitized(error)
            switch interruption {
            case .configuration: XCTAssertEqual(failure, .cancelled)
            case .queue: XCTAssertEqual(failure, .notFound)
            case .close: XCTAssertEqual(failure, .cancelled)
            case .settings: XCTAssertEqual(failure, .staleRevision)
            }
        }
        let calls = await scripts.calls(); XCTAssertTrue(calls.isEmpty)
        let finished = await scripts.inventory(); XCTAssertEqual(finished.active, 0)
        XCTAssertEqual(store.snapshot().0, changed.0); XCTAssertEqual(store.snapshot().2, changed.2)
        switch interruption {
        case .configuration, .close:
            do { _ = try await session.match(intent: .exact(selected)); XCTFail("A closed session cannot start another match") }
            catch { XCTAssertEqual(MusicServiceError.sanitized(error), .closed) }
        case .queue, .settings: break
        }
        if case .configuration = interruption {
            let fresh = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let result = try await fresh.match(intent: .quality("flac"))
            XCTAssertEqual(result.selection.quality, "flac")
        }
        await service.close()
    }

    func testClosedOrConfigurationInvalidatedExistingSessionIsNotReused() async throws {
        for configuration in [false, true] {
            let scripts = NativeMusicScriptFixture()
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
            let old = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            if configuration { await service.sourceConfigurationChanged() }
            else { await old.close() }
            let fresh = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            do { _ = try await old.match(intent: .quality("flac")); XCTFail("Old session revived") }
            catch { XCTAssertEqual(MusicServiceError.sanitized(error), .closed) }
            let result = try await fresh.match(intent: .quality("flac"))
            XCTAssertEqual(result.selection.quality, "flac")
            let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1)
            await service.close()
        }
    }

    func testQueueRevisionChangeReplacesExistingSessionWithoutRevivingOldOne() async throws {
        let scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let old = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let changed = try await service.changeQueue(.reorder(["entry-1"]), expectedRevision: 1)
        XCTAssertGreaterThan(changed.revision, 1)
        let fresh = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: changed.revision)
        do { _ = try await old.match(intent: .quality("flac")); XCTFail("Queue-invalidated session revived") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .closed) }
        let result = try await fresh.match(intent: .quality("flac"))
        XCTAssertEqual(result.selection.quality, "flac")
        let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1)
        await service.close()
    }
}

/// A synchronous document port lets the test close the real shared lifetime
/// inside a selected owner check, with no sleep or production scheduling hook.
private final class OwnershipClosingDocument: NativeMusicTestLibraryWrapping, @unchecked Sendable {
    let base: NativeMusicTestLibrary
    var backingLibrary: NativeMusicTestLibrary { base }
    private let lock = NSLock()
    private var reads = 0
    private var closeOnRead: Int?
    private var lifetime: MusicNativeCandidateDiscoveryLifetime?
    private var closures = 0
    init(_ base: NativeMusicTestLibrary) { self.base = base }
    func arm(_ lifetime: MusicNativeCandidateDiscoveryLifetime, onRead: Int?) {
        lock.lock(); defer { lock.unlock() }
        reads = 0; closures = 0; self.lifetime = lifetime; closeOnRead = onRead
    }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot {
        lock.lock()
        reads += 1
        if reads == closeOnRead { lifetime?.close(); closures += 1 }
        lock.unlock()
        return try base.readSnapshot()
    }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void, _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        return try base.withTransaction(commit: commit, body)
    }
    func closureCount() -> Int { lock.lock(); defer { lock.unlock() }; return closures }
}

extension MusicSourceSessionOwnershipTests {
    func testExistingSessionClosedDuringIsCurrentOwnerCheckCannotBeReturned() async throws {
        try await existingSessionClosure(onRead: 3)
    }
    func testExistingSessionClosedAfterIsCurrentBeforeOwnerReturnCannotBeReturned() async throws {
        try await existingSessionClosure(onRead: 4)
    }
    func testExistingSessionWithLiveLifetimeIsReused() async throws {
        try await existingSessionClosure(onRead: nil)
    }
    private func existingSessionClosure(onRead: Int?) async throws {
        let document = OwnershipClosingDocument(try MusicNativeCandidateDiscoveryFixture.store())
        let scripts = NativeMusicScriptFixture()
        let service = NativeMusicService(database: document, scripts: scripts, platformClient: try NativeMusicFixture.client(),
            clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let initial = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let old = try XCTUnwrap(initial as? MusicNativeSourceInspection)
        // Public sourceSession reads: initial document, new request check,
        // old isCurrent's owner check, then new request's final owner check.
        // Closing at #3 exercises the await inside isCurrent; #4 exercises the
        // subsequent hop back to the owner. Both use the old seed's real fence.
        document.arm(old.lifetime, onRead: onRead)
        do {
            let returned = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            XCTAssertNil(onRead, "A closed old lifetime cannot pass the owner's final return fence")
            XCTAssertTrue((returned as? MusicNativeSourceInspection) === old)
        } catch {
            XCTAssertNotNil(onRead)
            XCTAssertEqual(error as? MusicServiceError, .closed)
        }
        XCTAssertEqual(document.closureCount(), onRead == nil ? 0 : 1)
        let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
        if onRead != nil {
            do { _ = try await old.match(intent: .quality("flac")); XCTFail("Closed lifetime revived") }
            catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        }
        let fresh = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        XCTAssertEqual((fresh as? MusicNativeSourceInspection) === old, onRead == nil)
        let result = try await fresh.match(intent: .quality("flac"))
        XCTAssertEqual(result.selection.quality, "flac")
        let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1)
        await service.close()
    }
}
