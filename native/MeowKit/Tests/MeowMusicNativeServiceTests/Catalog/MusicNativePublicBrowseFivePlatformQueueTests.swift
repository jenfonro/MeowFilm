import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseFivePlatformQueueTests: XCTestCase {
    func testBothWholeQueuesRetainOver600OccurrencesAndCASRejectsWithoutChangingOldQueue() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = MusicNativePublicBrowseFiveHTTP(platform, total: 650, repeats: true), store = NativeMusicMemoryDocument()
            let service = try await NativeMusicFixture.service(store, http: http)
            let old = try await MusicAccountQueueFixture.seed(service), before = store.snapshot()
            let detail = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: detail.collection, snapshot: detail.snapshot))
            XCTAssertEqual(store.snapshot().0, before.0)
            do { _ = try await service.remoteQueuePage(source, cursor: "630"); XCTFail("unobserved cursor") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            var page = try await service.remoteQueuePage(source, cursor: nil)
            while let cursor = page.cursor { page = try await service.remoteQueuePage(source, cursor: cursor) }
            XCTAssertEqual(page.items.count, 20)
            let start = MusicRemoteQueueStart(index: 647, track: page.items[17])
            let wrong = MusicRemoteQueueStart(index: 647, songID: "not-this-recording", sourceKeys: start.sourceKeys)
            do { _ = try await service.replaceRemoteQueue(source, start: wrong, expectedRevision: old.revision, intent: .init()); XCTFail("wrong row") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            store.fail(.storageConflict)
            do { _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: .init()); XCTFail("CAS conflict") }
            catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            store.fail(nil)
            let result = try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: .init())
            let queue = try await service.queue()
            XCTAssertEqual(queue.entries.count, 650); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 650)
            XCTAssertEqual(result.currentId, queue.entries[647].id)
            XCTAssertEqual(queue.entries[0].track.songId, queue.entries[2].track.songId)
            XCTAssertEqual(store.snapshot().2, before.2 + 1, "one atomic writer, no page commits")
            let document = try MusicDocument(store.snapshot().0, runID: "synthetic")
            XCTAssertEqual(document.queue["source"]["sourceId"].string, detail.collection.id)
            XCTAssertEqual(document.queue["source"]["platform"].string, platform.rawValue)
            let calls = await http.calls()
            XCTAssertEqual(calls.count, platform == .kuwo ? 2 : 28, "detail and explicit queue actions share one double-verified snapshot")
            source.revoke(); await source.close(); await service.close()
        }
    }

    func testWholeQueueKeepsMoreThanFormerTwoThousandOccurrences() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = MusicNativePublicBrowseFiveHTTP(platform, total: 2001), store = NativeMusicMemoryDocument()
            let service = try await NativeMusicFixture.service(store, http: http)
            let detail = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: detail.collection, snapshot: detail.snapshot))
            let rows = try await source.collectionTracks(intent: .init())
            XCTAssertEqual(rows.count, 2001)
            let last = try await service.remoteQueuePage(source, cursor: "1980")
            let selected = try XCTUnwrap(last.items.last)
            let state = try await service.replaceRemoteQueue(source, start: .init(index: 2000, track: selected),
                                                             expectedRevision: 0, intent: .init())
            let queue = try await service.queue()
            XCTAssertEqual(state.total, 2001); XCTAssertEqual(queue.entries.count, 2001)
            XCTAssertEqual(state.currentId, queue.entries.last?.id)
            XCTAssertEqual(queue.entries.last?.track.songId, selected.songId)
            XCTAssertEqual(Set(queue.entries.map(\.id)).count, 2001)
            XCTAssertEqual(store.snapshot().2, 1)
            await source.close(); await service.close()
        }
    }

    func testQueueOpenRejectsLaterStableButDifferentSnapshotWithoutMutatingExistingQueue() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = MusicNativePublicBrowseFiveHTTP(platform, fault: "tail", changeFrom: 3)
            let store = NativeMusicMemoryDocument(), clock = NativeMusicFixtureClock()
            let service = try await NativeMusicFixture.service(store, http: http, clock: { clock.now() })
            _ = try await MusicAccountQueueFixture.seed(service)
            let before = store.snapshot()
            let first = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            clock.advance(46) // Expire the shared 45-second snapshot, not the page's 10-minute cursor.
            do {
                _ = try await service.openPublicQueue(.init(collection: first.collection, snapshot: first.snapshot))
                XCTFail("stale snapshot")
            }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            let retained = await service.publicQueueSources.count; XCTAssertEqual(retained, 0)
            await service.close()
        }
    }

    func testCancelledOrRevokedNonCooperativeDetailStopsBeforeSecondRequestAndCannotPublish() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            for revoke in [false, true] {
                let http = MusicNativePublicBrowseFiveHTTP(platform), store = NativeMusicMemoryDocument(), gate = NativeMusicGate()
                await http.hold(call: 1, gate: gate)
                let service = try await NativeMusicFixture.service(store, http: http)
                let task = Task { try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil) }
                await gate.started()
                if revoke { service.revokeAccess(generation: 2) } else { task.cancel() }
                do { _ = try await task.value; XCTFail("late authority") } catch {}
                // The single-flight cache may release this logical caller
                // immediately. Count the physical transport, not its waiter.
                let active = await http.activeTransferCount()
                XCTAssertEqual(active, 1, "physical work remains until the non-cooperative transport settles")
                await gate.release()
                await http.waitUntilIdle()
                await service.close()
                let calls = await http.calls(); XCTAssertEqual(calls.count, 1)
                let remaining = await http.activeTransferCount(); XCTAssertEqual(remaining, 0)
                XCTAssertEqual(store.snapshot().2, 0)
            }
        }
    }

    func testPartialDetailFailureIsNotRetriedUntilUserRetriesSameRequest() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let total = platform == .kuwo ? 1001 : 51
            let http = MusicNativePublicBrowseFiveHTTP(platform, total: total), store = NativeMusicMemoryDocument()
            await http.fail(call: 2)
            let service = try await NativeMusicFixture.service(store, http: http)
            let collection = MusicNativePublicBrowseFiveFixture.collection(platform)
            do { _ = try await service.publicDetail(collection, cursor: nil); XCTFail("partial detail") }
            catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
            let failedCalls = await http.calls(); XCTAssertEqual(failedCalls.count, 2)
            XCTAssertEqual(store.snapshot().2, 0)
            let retried = try await service.publicDetail(collection, cursor: nil)
            XCTAssertEqual(retried.total, total); XCTAssertEqual(retried.tracks.count, 30)
            XCTAssertEqual(retried.collection.platform, platform); XCTAssertEqual(store.snapshot().2, 0)
            let calls = await http.calls()
            XCTAssertEqual(calls.count, platform == .kuwo ? 6 : 8)
            await service.close()
        }
    }

    func testProtectionRevokesOpenedNewPlatformSourceSynchronouslyBeforeCAS() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let http = MusicNativePublicBrowseFiveHTTP(platform), store = NativeMusicMemoryDocument()
            let service = try await NativeMusicFixture.service(store, http: http)
            let detail = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: detail.collection, snapshot: detail.snapshot))
            let page = try await service.remoteQueuePage(source, cursor: nil)
            let before = store.snapshot()
            service.revokeAccess(generation: 2)
            XCTAssertFalse(source.authorization.isActive)
            do {
                _ = try await service.replaceRemoteQueue(source, start: .init(index: 0, track: page.items[0]),
                    expectedRevision: 0, intent: .init()); XCTFail("revoked source")
            } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            await source.close(); await service.close()
        }
    }
}
