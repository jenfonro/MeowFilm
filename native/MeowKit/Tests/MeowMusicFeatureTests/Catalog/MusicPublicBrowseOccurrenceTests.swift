import XCTest
@testable import MeowMusicFeature

private actor OccurrenceBrowseService: MusicPublicBrowsing, MusicPublicBrowseQueueLoading {
    static let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "41", title: "Synthetic")
    private(set) var cursors: [String?] = []
    private(set) var opened = 0
    private var changed = false
    private var shiftsOnRenewal = false
    private let onClose: @Sendable () -> Void
    init(onClose: @escaping @Sendable () -> Void = {}) { self.onClose = onClose }
    func changeSelectedIdentity() { changed = true }
    func shiftSecondPageOnRenewal() { shiftsOnRenewal = true }
    static func track(_ index: Int, handle: String, changed: Bool = false) -> MusicRemoteTrack {
        let id = changed ? "999" : [1, 29, 30, 31].contains(index) ? "7" : String(index + 100)
        return .init(ref: .init(kind: .handle, id: handle + ":" + String(index)),
            songId: "tx:" + id, title: [2, 28, 31].contains(index) ? "needle" : "Song",
            artist: "Artist", sources: [.init(sourceKey: "tx:" + id, platform: "tx", nativeId: id)])
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) -> MusicPublicBrowseDetail {
        cursors.append(cursor)
        let positions: [Int]
        let consumed: Int
        if cursor == nil {
            positions = Array(0..<30); consumed = 30
        } else if shiftsOnRenewal {
            positions = cursors.count >= 3 ? [31, 32] : [30, 31, 32]
            consumed = 3
        } else {
            positions = [30, 31]; consumed = 2
        }
        return .init(collection: collection, members: positions.map {
            let track = Self.track($0, handle: "read-\(cursors.count)", changed: changed && $0 == 31)
            return MusicPublicBrowseMember(nativeID: track.sources[0].nativeId, sourcePosition: $0, track: track)
        }, total: shiftsOnRenewal ? 33 : 32, cursor: cursor == nil ? "30" : nil,
                     snapshot: String(repeating: "a", count: 64), consumed: consumed)
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) throws -> MusicPublicBrowsePage {
        throw MusicServiceError.unsupported
    }
    func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) -> [MusicNativeTrackData] { [] }
    func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) -> MusicRemoteQueueSource {
        opened += 1
        let onClose = onClose
        return .init(context: .init(kind: .playlist, id: Self.collection.id, title: "Synthetic", platform: "tx"),
                     loadPage: { _ in .init(items: []) }, close: { onClose() })
    }
}

@MainActor final class MusicPublicBrowseOccurrenceTests: XCTestCase {
    private func model(_ service: OccurrenceBrowseService) -> MusicPublicBrowseViewModel {
        let model = MusicPublicBrowseViewModel(service: service, request: .detail(OccurrenceBrowseService.collection),
            queuePage: { _, cursor in
                let start = cursor == nil ? 0 : 30, end = cursor == nil ? 30 : 32
                return .init(items: (start..<end).map { OccurrenceBrowseService.track($0, handle: "queue") },
                             cursor: cursor == nil ? "30" : nil)
            })
        model.setAccess(available: true, generation: UUID()); model.appear()
        return model
    }
    func testRenewalUsesExactRepeatedOccurrenceFromItsOriginalPage() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.last)
        XCTAssertEqual(model.queueIndex(of: selected), 31)
        let renewed = try await model.refreshedActionTrack(selected)
        XCTAssertEqual(renewed.ref.id, "read-3:31", "Must not renew occurrence 30 of the same song")
        XCTAssertEqual(renewed.songId, selected.songId)
        XCTAssertEqual(model.tracks.last?.ref, selected.ref, "Renewal must not replace displayed handles")
        let cursors = await service.cursors, opened = await service.opened
        XCTAssertEqual(cursors, [nil, "30", "30"]); XCTAssertEqual(opened, 0)
        model.disappear()
    }
    func testCompleteQueueAndFilteredIndexUseWholeSnapshotPrefixNotCurrentPage() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.last)
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31, "Repeated song at 1/29/30 is not the selected occurrence")
        let filtered = try await model.filteredQueueIndex(source: source, index: index, track: selected, query: "needle")
        XCTAssertEqual(filtered, 2, "Two matches precede this page")
        XCTAssertEqual(model.pageNumber, 2)
        model.disappear(); await source.close()
    }
    func testRenewalRejectsChangedOccurrenceWithoutFallingBackToAnotherEqualSong() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.last)
        await service.changeSelectedIdentity()
        do { _ = try await model.refreshedActionTrack(selected); XCTFail("Different occurrence accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(model.tracks.last?.ref, selected.ref)
        XCTAssertNil(model.remoteSource)
        model.disappear()
    }
    func testRenewalUsesSourcePositionWhenEarlierMemberBecomesUnavailable() async throws {
        let service = OccurrenceBrowseService()
        await service.shiftSecondPageOnRenewal()
        let model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.last)
        XCTAssertEqual(model.queueIndex(of: selected), 32)
        XCTAssertEqual(model.sections[0].items.last?.id, "32:tx:132")
        let renewed = try await model.refreshedActionTrack(selected)
        XCTAssertEqual(renewed.ref.id, "read-3:32")
        XCTAssertEqual(renewed.songId, selected.songId)
        model.disappear()
    }
    func testDefaultSourceReleaseRevokesSynchronouslyAndClosesExactlyOnce() async throws {
        let closed = expectation(description: "default source cleanup")
        let service = OccurrenceBrowseService(onClose: { closed.fulfill() }), model = model(service)
        await model.refresh()
        let (source, _) = try await model.playbackSource()
        model.disappear()
        XCTAssertFalse(source.authorization.isActive)
        XCTAssertNil(model.remoteSource)
        await fulfillment(of: [closed], timeout: 2)
        // The owner may also finish its cleanup; the source closes only once.
        await source.close()
    }
}
