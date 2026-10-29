import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private final class ActionRecoveryClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = NativeMusicFixture.date
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance() { lock.lock(); defer { lock.unlock() }; date.addTimeInterval(1) }
}

final class MusicNativeRemoteActionHandleRecoveryTests: XCTestCase {
    func testEvictedDisplayHandleCanBeReissuedFromSameBoundedSourceAndSaved() async throws {
        let clock = ActionRecoveryClock()
        let service = NativeMusicService(document: NativeMusicMemoryDocument(), qq: NativeMusicQQFixture(),
            transport: NativeMusicNoNetwork(), clock: { clock.now() })
        try await service.setAccess(available: true, generation: 1)
        let source = MusicRemoteQueueSource(context: .init(kind: .playlist, id: "synthetic-660",
            title: "Synthetic", platform: "tx"), loadPage: { cursor in
                let start = cursor.flatMap(Int.init) ?? 0, end = min(start + 30, 660)
                return .init(items: (start..<end).map { NativeMusicFixture.track("Synthetic\($0)") },
                             cursor: end < 660 ? String(end) : nil)
            })
        let first = try await service.remoteQueuePage(source, cursor: nil)
        let old = try XCTUnwrap(first.items.first)
        var cursor = first.cursor
        while let next = cursor {
            clock.advance()
            cursor = try await service.remoteQueuePage(source, cursor: next).cursor
        }
        do { _ = try await service.createPlaylist(name: "Expired", refs: [old.ref]); XCTFail("old handle did not expire") }
        catch { XCTAssertEqual(error as? MusicServiceError, .handleExpired) }
        clock.advance()
        let renewedPage = try await service.remoteQueuePage(source, cursor: nil)
        let renewed = try XCTUnwrap(renewedPage.items.first)
        XCTAssertNotEqual(renewed.ref, old.ref)
        XCTAssertEqual(renewed.songId, old.songId); XCTAssertEqual(renewed.sources, old.sources)
        let saved = try await service.createPlaylist(name: "Recovered", refs: [renewed.ref])
        let tracks = try await service.tracks(in: saved)
        XCTAssertEqual(tracks.map(\.songId), [old.songId])
        await source.close(); await service.close()
    }
}
