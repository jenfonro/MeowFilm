import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private final class ActionRecoveryClock: @unchecked Sendable {
    private let lock = NSLock()
    private var date = NativeMusicFixture.date
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return date }
    func advance() { lock.lock(); defer { lock.unlock() }; date.addTimeInterval(1) }
}

final class MusicNativeRemoteReferenceRecoveryTests: XCTestCase {
    func testDisplayedPermanentReferenceSurvivesLaterPagesAndOwnerRecreation() async throws {
        let clock = ActionRecoveryClock()
        let document = NativeMusicTestLibrary()
        let service = NativeMusicService(database: document, platformClient: try NativeMusicFixture.client(),
            clock: { clock.now() })
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
        XCTAssertEqual(old.ref.kind, .library)
        XCTAssertNotNil(UUID(uuidString: old.songId))
        let saved = try await service.createPlaylist(name: "Retained", refs: [old.ref])
        clock.advance()
        let renewedPage = try await service.remoteQueuePage(source, cursor: nil)
        let renewed = try XCTUnwrap(renewedPage.items.first)
        XCTAssertEqual(renewed.ref.id, old.ref.id)
        XCTAssertEqual(renewed.songId, old.songId); XCTAssertEqual(renewed.sources, old.sources)
        let tracks = try await service.tracks(in: saved)
        XCTAssertEqual(tracks.map(\.songId), [old.songId])
        await source.close(); await service.close()
        let cold = try await NativeMusicFixture.service(document)
        let restored = try await cold.tracks(in: saved)
        XCTAssertEqual(restored.map(\.songId), [old.songId])
        await cold.close()
    }
}
