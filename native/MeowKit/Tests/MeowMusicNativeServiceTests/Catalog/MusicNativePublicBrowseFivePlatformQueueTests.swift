import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseFivePlatformQueueTests: XCTestCase {
    func testBothWholeQueuesRetainOver600OccurrencesAndCASRejectsWithoutChangingOldQueue() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform, total: 650, repeats: true), store = NativeMusicTestLibrary()
            let service = try await NativeMusicFixture.service(store, platform: peer)
            let old = try await MusicAccountQueueFixture.seed(service)
            let detail = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: detail.collection))
            let unchanged = try await service.queue()
            XCTAssertEqual(unchanged.entries, old.entries); XCTAssertEqual(unchanged.revision, old.revision); XCTAssertEqual(unchanged.currentId, old.currentId)
            do { _ = try await service.remoteQueuePage(source, cursor: "630"); XCTFail("unobserved cursor") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            var page = try await service.remoteQueuePage(source, cursor: nil)
            while let cursor = page.cursor { page = try await service.remoteQueuePage(source, cursor: cursor) }
            XCTAssertEqual(page.items.count, 20)
            let before = store.snapshot(), start = MusicRemoteQueueStart(index: 647)
            do { _ = try await service.replaceRemoteQueue(source, start: .init(index: 650), expectedRevision: old.revision, intent: .init()); XCTFail("outside occurrence range") }
            catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
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
            XCTAssertEqual(store.snapshot().2, before.2 + 1, "one queue CAS after repository adoption")
            let document = try MusicDocument(store.snapshot().0, runID: "synthetic")
            XCTAssertEqual(document.queue["source"]["sourceId"].string, detail.collection.id)
            XCTAssertEqual(document.queue["source"]["platform"].string, platform.rawValue)
            XCTAssertTrue(document.queue["entries"].array.allSatisfy { $0["song"] == .null && !$0["songId"].string.isEmpty })
            let calls = await peer.calls()
            XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist.complete"], "no snapshot proof reread")
            source.revoke(); await source.close(); await service.close()
        }
    }

    func testWholeQueueKeepsMoreThanFormerTwoThousandOccurrences() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform, total: 2001), store = NativeMusicTestLibrary()
            let service = try await NativeMusicFixture.service(store, platform: peer)
            let detail = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: detail.collection))
            let rows = try await source.collectionTracks(intent: .init())
            XCTAssertEqual(rows.count, 2001)
            _ = try await service.remoteQueuePage(source, cursor: nil)
            let last = try await service.remoteQueuePage(source, cursor: "1980")
            let selected = try XCTUnwrap(last.items.last), before = store.snapshot()
            let state = try await service.replaceRemoteQueue(source, start: .init(index: 2000), expectedRevision: 0, intent: .init())
            let queue = try await service.queue()
            XCTAssertEqual(state.total, 2001); XCTAssertEqual(queue.entries.count, 2001)
            XCTAssertEqual(state.currentId, queue.entries.last?.id); XCTAssertEqual(queue.entries.last?.track.songId, selected.songId)
            XCTAssertEqual(Set(queue.entries.map(\.id)).count, 2001); XCTAssertEqual(store.snapshot().2, before.2 + 1)
            await source.close(); await service.close()
        }
    }

    func testQueueOpenConsumesCurrentChangedCollectionWithoutRecheckingOldDetail() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            for fault in ["tail", "reorder", "owner", "wrongID"] {
                let peer = MusicNativePublicBrowseFivePeer(platform, fault: fault), store = NativeMusicTestLibrary()
                let service = try await NativeMusicFixture.service(store, platform: peer)
                let old = try await MusicAccountQueueFixture.seed(service)
                let first = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
                let source = try await service.openPublicQueue(.init(collection: first.collection))
                let rows = try await source.collectionTracks(intent: .init())
                XCTAssertEqual(rows.count, 65)
                let queue = try await service.queue()
                XCTAssertEqual(queue.revision, old.revision); XCTAssertEqual(queue.currentId, old.currentId)
                if fault == "tail" { XCTAssertEqual(try MusicModel.sources(MusicModel.normalize(rows[64])).first?.nativeId, "999999") }
                if fault == "wrongID" { XCTAssertEqual(source.context.collection?.nativeID, "canonical-99") }
                let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist.complete"])
                await source.close(); await service.close()
            }
        }
    }

    func testCancelledOrRevokedNonCooperativeDetailCannotPublishAndPhysicalWorkerEnds() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            for revoke in [false, true] {
                let peer = MusicNativePublicBrowseFivePeer(platform), store = NativeMusicTestLibrary(), gate = NativeMusicGate()
                await peer.hold(call: 1, gate: gate)
                let service = try await NativeMusicFixture.service(store, platform: peer)
                let task = Task { try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil) }
                await gate.started()
                if revoke { service.revokeAccess(generation: 2) } else { task.cancel() }
                do { _ = try await task.value; XCTFail("late authority") } catch {}
                let active = await peer.activeTransferCount(); XCTAssertEqual(active, 1)
                await gate.release(); await peer.waitUntilIdle(); await service.close()
                let calls = await peer.calls(), remaining = await peer.activeTransferCount()
                XCTAssertEqual(calls.count, 1); XCTAssertEqual(remaining, 0); XCTAssertEqual(store.snapshot().2, 0)
            }
        }
    }

    func testLaterPageFailureDoesNotReplaceQueueOrAutomaticallyRetry() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform), store = NativeMusicTestLibrary()
            let service = try await NativeMusicFixture.service(store, platform: peer)
            let old = try await MusicAccountQueueFixture.seed(service)
            let first = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let before = store.snapshot(); await peer.fail(call: 2)
            do { _ = try await service.publicDetail(first.collection, cursor: first.cursor); XCTFail("failed page") }
            catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
            XCTAssertEqual(store.snapshot().0, before.0)
            let failed = await peer.calls(); XCTAssertEqual(failed.count, 2)
            let retried = try await service.publicDetail(first.collection, cursor: first.cursor)
            XCTAssertEqual(retried.tracks.count, 30)
            let queue = try await service.queue()
            XCTAssertEqual(queue.entries, old.entries); XCTAssertEqual(queue.revision, old.revision)
            let calls = await peer.calls(); XCTAssertEqual(calls.count, 3)
            await service.close()
        }
    }

    func testProtectionRevokesOpenedNewPlatformSourceSynchronouslyBeforeCAS() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform), store = NativeMusicTestLibrary()
            let service = try await NativeMusicFixture.service(store, platform: peer)
            let detail = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            let source = try await service.openPublicQueue(.init(collection: detail.collection))
            _ = try await service.remoteQueuePage(source, cursor: nil)
            let before = store.snapshot(); service.revokeAccess(generation: 2)
            XCTAssertFalse(source.authorization.isActive)
            do { _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init()); XCTFail("revoked source") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            await source.close(); await service.close()
        }
    }
}
