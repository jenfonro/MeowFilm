import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor MusicNativePublicBrowseQQFixture: MusicPublicBrowseQQProviding {
    let count: Int
    var reads = 0
    let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "42", title: "Synthetic remote list")
    init(count: Int) { self.count = count }
    func search(query: String, page: Int) -> MusicNativeTrackPage { .init(items: []) }
    func lyrics(mid: String) -> MusicNativeLyrics { .init(text: "") }
    func publicBrowsePlaylists(query: String, page: Int) -> MusicPublicBrowseQQSearch {
        .init(items: [collection], total: 1, nextPage: nil)
    }
    func publicBrowsePlaylist(id: String) -> MusicPublicBrowseNativeCollection {
        reads += 1
        // Deliberate repeated recordings; occurrences must survive intact.
        return .init(collection: collection, tracks: (0..<count).map { NativeMusicFixture.track("MID\($0 % 2)") },
                     snapshot: String(repeating: "a", count: 64))
    }
}
private actor MusicNativePublicBrowseBlockedHTTP: MusicNativeHTTPTransport {
    let gate: NativeMusicGate
    init(gate: NativeMusicGate) { self.gate = gate }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        await gate.wait() // Intentionally non-cooperative injected transport.
        return .init(status: 200, data: Data(#"{"code":200,"result":{"playlistCount":0,"playlists":[]}}"#.utf8))
    }
}
final class MusicNativePublicBrowseQueueTests: XCTestCase {
    func testExplicitQueueActionLoadsOneSnapshotWithoutWritingAndKeepsOver600RepeatedOccurrences() async throws {
        let qq = MusicNativePublicBrowseQQFixture(count: 650), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, qq: qq)
        let collection = await qq.collection
        let request = MusicPublicBrowseQueueRequest(collection: collection, snapshot: String(repeating: "a", count: 64))
        let source = try await service.openPublicQueue(request)
        let initialReads = await qq.reads
        XCTAssertEqual(initialReads, 1); XCTAssertEqual(store.snapshot().2, 0)
        let first = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(first.items.count, 30)
        let state = try await service.replaceRemoteQueue(source, start: .init(index: 0, track: first.items[0]),
            expectedRevision: 0, intent: .init())
        XCTAssertEqual(state.total, 650)
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.count, 650); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 650)
        XCTAssertEqual(queue.entries[0].track.songId, queue.entries[2].track.songId)
        let reads = await qq.reads; XCTAssertEqual(reads, 1)
        await source.close()
    }
    func testPublicSourceProtectionRevokeIsSynchronousAndDoesNotReplaceExistingQueue() async throws {
        let qq = MusicNativePublicBrowseQQFixture(count: 3), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, qq: qq)
        let source = try await service.openPublicQueue(.init(collection: await qq.collection, snapshot: String(repeating: "a", count: 64)))
        let first = try await service.remoteQueuePage(source, cursor: nil), before = store.snapshot().0
        service.revokeAccess(generation: 2)
        XCTAssertFalse(source.authorization.isActive)
        do {
            _ = try await service.replaceRemoteQueue(source, start: .init(index: 0, track: first.items[0]), expectedRevision: 0, intent: .init())
            XCTFail("revoked source")
        } catch {}
        XCTAssertEqual(store.snapshot().0, before)
        await source.close()
    }
    func testMismatchedSnapshotFailsBeforeQueueWriter() async throws {
        let qq = MusicNativePublicBrowseQQFixture(count: 1), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, qq: qq)
        do {
            _ = try await service.openPublicQueue(.init(collection: await qq.collection, snapshot: String(repeating: "b", count: 64)))
            XCTFail("mismatched snapshot")
        }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertEqual(store.snapshot().2, 0)
        let retained = await service.publicQueueSources.count
        XCTAssertEqual(retained, 0, "A failed open must not publish source authority")
        await service.close()
    }
    func testPublicSourceCountIsBoundedAndCloseRefreshAndExpiryReleaseRegistrations() async throws {
        let qq = MusicNativePublicBrowseQQFixture(count: 1), clock = NativeMusicFixtureClock()
        let service = try await NativeMusicFixture.service(qq: qq, clock: { clock.now() })
        let collection = await qq.collection
        let request = MusicPublicBrowseQueueRequest(collection: collection, snapshot: String(repeating: "a", count: 64))
        var sources: [MusicRemoteQueueSource] = []
        for _ in 0..<32 { sources.append(try await service.openPublicQueue(request)) }
        do { _ = try await service.openPublicQueue(request); XCTFail("Unbounded public sources") }
        catch { XCTAssertEqual(error as? MusicServiceError, .busy) }
        await sources[0].close()
        sources.append(try await service.openPublicQueue(request))
        let full = await service.publicQueueSources.count; XCTAssertEqual(full, 32)
        clock.advance(601)
        let fresh = try await service.openPublicQueue(request)
        XCTAssertTrue(sources.allSatisfy { !$0.authorization.isActive })
        let afterExpiry = await service.publicQueueSources.count; XCTAssertEqual(afterExpiry, 1)
        await service.refreshPublicCollection(collection)
        XCTAssertFalse(fresh.authorization.isActive)
        let afterRefresh = await service.publicQueueSources.count; XCTAssertEqual(afterRefresh, 0)
        for source in sources { await source.close() }
        await fresh.close(); await service.close()
    }
    func testCancelledNonCooperativeSearchCannotPublishAndKeepsPhysicalBudgetUntilSettled() async throws {
        let gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(http: MusicNativePublicBrowseBlockedHTTP(gate: gate))
        let task = Task { try await service.publicPlaylists(platform: .netease, query: "synthetic", cursor: nil) }
        await gate.started(); task.cancel()
        do { _ = try await task.value; XCTFail("late search") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let active = await service.operations.count
        XCTAssertEqual(active, 1)
        await gate.release()
        await service.close()
    }
}
