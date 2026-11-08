import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor OwnershipDiscoveryCache: MusicSourceDiscoveryCaching {
    nonisolated var persistsRecords: Bool { false }
    let gate: MusicNativeCandidateDiscoveryBarrier
    init(_ gate: MusicNativeCandidateDiscoveryBarrier) { self.gate = gate }
    func entries(fingerprint: String, duration: Double) -> [String: MusicSourceDiscoveryEntry] { [:] }
    func record(fingerprint: String, duration: Double, platform: String, checkedAt: Double,
                sources: [MusicSourceDiscoverySource]) {}
    func excluded(fingerprint: String, duration: Double) async -> Set<String> {
        await gate.enter(); return []
    }
    func exclude(fingerprint: String, duration: Double, sourceKey: String) {}
    func persist(retaining recordings: [MusicSourceDiscoveryRecording]) {}
}

final class MusicSourceSessionOwnershipTests: XCTestCase {
    func testConcurrentFirstPlaybackAndInspectionShareResultsAndURLRequest() async throws {
        let gate = MusicNativeCandidateDiscoveryBarrier(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, sourceDiscovery: OwnershipDiscoveryCache(gate))
        let playback = Task { try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1) }
        await gate.started(1)
        let inspection = Task { try await service.inspectSources(for: .init(kind: .queue, id: "entry-1")) }
        await gate.started(2)
        let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
        await gate.release()
        let session = try await playback.value, page = try await inspection.value
        let first = try await session.match(intent: .quality("flac"))
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
        await service.close()
    }

    func testConfigurationChangeDuringFirstCreationCannotPublishOldSession() async throws {
        try await interruptedCreation(.configuration)
    }
    func testQueueChangeDuringFirstCreationCannotPublishOldSession() async throws {
        try await interruptedCreation(.queue)
    }
    func testCloseDuringFirstCreationCannotPublishOldSession() async throws {
        try await interruptedCreation(.close)
    }
    func testSettingsChangeDuringFirstCreationCannotPublishOldSession() async throws {
        try await interruptedCreation(.settings)
    }
    private enum Interruption { case configuration, queue, close, settings }
    private func interruptedCreation(_ interruption: Interruption) async throws {
        let gate = MusicNativeCandidateDiscoveryBarrier(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, sourceDiscovery: OwnershipDiscoveryCache(gate))
        let task = Task { try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1) }
        await gate.started(1)
        switch interruption {
        case .configuration: await service.sourceConfigurationChanged()
        case .queue: _ = try await service.changeQueue(.clear, expectedRevision: 1)
        case .close: await service.close()
        case .settings: _ = try await service.updateSettings(.init(ifRevision: 0, requestQuality: "128k"))
        }
        await gate.release()
        do { _ = try await task.value; XCTFail("Invalidated creation returned a session") }
        catch {
            let failure = MusicServiceError.sanitized(error)
            switch interruption {
            case .configuration: XCTAssertEqual(failure, .staleResult)
            case .queue: XCTAssertEqual(failure, .notFound)
            case .close: XCTAssertEqual(failure, .closed)
            case .settings: XCTAssertEqual(failure, .staleRevision)
            }
        }
        let calls = await scripts.calls(); XCTAssertTrue(calls.isEmpty)
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
private final class OwnershipClosingDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    let base: NativeMusicMemoryDocument
    private let lock = NSLock()
    private var reads = 0
    private var closeOnRead: Int?
    private var lifetime: MusicNativeCandidateDiscoveryLifetime?
    private var closures = 0
    init(_ base: NativeMusicMemoryDocument) { self.base = base }
    func arm(_ lifetime: MusicNativeCandidateDiscoveryLifetime, onRead: Int?) {
        lock.lock(); defer { lock.unlock() }
        reads = 0; closures = 0; self.lifetime = lifetime; closeOnRead = onRead
    }
    func read() throws -> Data? {
        lock.lock()
        reads += 1
        if reads == closeOnRead { lifetime?.close(); closures += 1 }
        lock.unlock()
        return try base.read()
    }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        try base.compareAndSwap(expected: expected, replacement: replacement)
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
        let service = NativeMusicService(document: document, scripts: scripts, qq: NativeMusicQQFixture(),
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
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
