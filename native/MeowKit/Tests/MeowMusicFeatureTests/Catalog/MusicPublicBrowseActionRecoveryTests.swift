import XCTest
import MeowMusicFeature

private actor BrowseActionGate {
    private var waiting = false, released = false
    private var starts: [CheckedContinuation<Void, Never>] = [], ends: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        waiting = true; starts.forEach { $0.resume() }; starts.removeAll()
        if !released { await withCheckedContinuation { ends.append($0) } }
    }
    func started() async { if !waiting { await withCheckedContinuation { starts.append($0) } } }
    func release() { released = true; ends.forEach { $0.resume() }; ends.removeAll() }
}
private actor BrowseActionService: MusicPublicBrowsing, MusicPublicBrowseQueueLoading {
    // QQ playlists retain repeated recordings; WY playlist IDs are unique.
    static let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "41", title: "Synthetic")
    var opens = 0
    private var readPage: (@Sendable (String?) async throws -> MusicBrowsePage)?
    func usePageReader(_ read: @escaping @Sendable (String?) async throws -> MusicBrowsePage) { readPage = read }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail {
        guard let readPage else { throw MusicServiceError.invalidResponse }
        let page = try await readPage(cursor)
        let members = try page.items.map { track -> MusicPublicBrowseMember in
            guard let position = Int(track.ref.id.split(separator: ":").last ?? "") else {
                throw MusicServiceError.invalidResponse
            }
            return .init(nativeID: track.sources[0].nativeId, sourcePosition: position, track: track)
        }
        return .init(collection: collection, members: members, total: 900,
                     cursor: page.cursor, snapshot: "synthetic-snapshot")
    }
    func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) -> MusicRemoteQueueSource {
        opens += 1
        return .init(context: .init(kind: .playlist, id: Self.collection.id, title: "Synthetic", platform: "tx"),
                     loadPage: { _ in .init(items: []) })
    }
    func publicQueueTracks(_ request: MusicPublicBrowseQueueRequest) -> [MusicNativeTrackData] { [] }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) throws -> MusicPublicBrowsePage { throw MusicServiceError.unsupported }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) throws -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }
}
@MainActor private final class BrowseActionPages {
    let sizes: [Int]
    var cursors: [String?] = []
    var failureCursor: String?
    var mutation = 0
    var gate: BrowseActionGate?
    init(_ sizes: [Int]) { self.sizes = sizes }
    func page(cursor: String?) async throws -> MusicBrowsePage {
        cursors.append(cursor)
        let visit = cursors.count, page = cursor.flatMap(Int.init) ?? 0
        guard sizes.indices.contains(page) else { throw MusicServiceError.invalidRequest }
        if let gate { await gate.wait() }
        if let failureCursor, cursor == failureCursor { throw MusicServiceError.unavailable }
        let offset = sizes.prefix(page).reduce(0, +)
        let rows = (offset..<(offset + sizes[page])).map { index -> MusicRemoteTrack in
            // The same recording at two positions must retain its occurrence.
            let id = index == 9 ? 0 : index
            let song = id == 0 ? "song:\u{e9}" : "song:\(id)"
            return .init(ref: .init(kind: .handle, id: "\(visit):row:\(index)"),
                songId: mutation == 1 ? "song:e\u{301}" : song, title: "Synthetic \(index)",
                sources: [.init(sourceKey: mutation == 2 ? "wrong-key" : "tx:\(id)",
                                platform: mutation == 3 ? "wy" : "tx",
                                nativeId: mutation == 4 ? "wrong-id" : String(id))])
        }
        return .init(items: rows, cursor: page + 1 < sizes.count ? String(page + 1) : nil)
    }
}

@MainActor final class MusicPublicBrowseActionRecoveryTests: XCTestCase {
    private func model(_ service: BrowseActionService, _ pages: BrowseActionPages) async -> MusicPublicBrowseViewModel {
        await service.usePageReader { cursor in try await pages.page(cursor: cursor) }
        let model = MusicPublicBrowseViewModel(service: service, request: .detail(BrowseActionService.collection),
            queuePage: { _, cursor in try await pages.page(cursor: cursor) })
        model.setAccess(available: true, generation: UUID()); model.appear(); await model.refresh()
        return model
    }
    func testOver600VisibleRowsRenewOnlySelectedPageWithoutReopeningOrMutatingDisplay() async throws {
        let service = BrowseActionService(), pages = BrowseActionPages(Array(repeating: 30, count: 22))
        let model = await model(service, pages)
        for _ in 1..<22 { await model.appendNext() }
        let rows = model.tracks
        XCTAssertEqual(rows.count, 660)
        let selected = try XCTUnwrap(rows.first)
        let renewed = try await model.refreshedActionTrack(selected)
        XCTAssertNotEqual(renewed.ref, selected.ref)
        XCTAssertEqual(renewed.songId, selected.songId)
        XCTAssertEqual(pages.cursors.count, 23); XCTAssertNil(pages.cursors.last ?? nil)
        XCTAssertEqual(model.tracks, rows); XCTAssertEqual(model.pageNumber, 22)
        XCTAssertEqual(model.trackOffset, 0); XCTAssertNil(model.nextCursor)
        let opens = await service.opens
        XCTAssertEqual(opens, 0, "Reading/renewing visible pages never opens a complete playback queue")
        model.disappear()
    }
    func testVariablePageLengthsAndDuplicateSongsRecoverExactGlobalOccurrence() async throws {
        let service = BrowseActionService(), pages = BrowseActionPages([7, 19, 5])
        let model = await model(service, pages)
        await model.appendNext(); await model.appendNext()
        let selected = try XCTUnwrap(model.tracks.indices.contains(9) ? model.tracks[9] : nil)
        XCTAssertEqual(model.queueIndex(of: selected), 9)
        let renewed = try await model.refreshedActionTrack(selected)
        XCTAssertEqual(pages.cursors.last ?? nil, "1")
        XCTAssertTrue(renewed.ref.id.hasSuffix("row:9"))
        XCTAssertEqual(model.queueIndex(of: selected), 9)
        model.disappear()
    }
    func testRenewalPreservesFailedAppendCursorAndAtomicRetryState() async throws {
        let service = BrowseActionService(), pages = BrowseActionPages([7, 19, 5])
        let model = await model(service, pages)
        await model.appendNext()
        pages.failureCursor = "2"; await model.appendNext()
        let before = model.tracks
        XCTAssertEqual(model.error, .unavailable); XCTAssertEqual(model.pageNumber, 2)
        _ = try await model.refreshedActionTrack(XCTUnwrap(before.first))
        XCTAssertEqual(model.tracks, before); XCTAssertEqual(model.error, .unavailable)
        XCTAssertEqual(model.nextCursor, "2"); XCTAssertEqual(model.pageNumber, 2)
        pages.failureCursor = nil; await model.retry()
        XCTAssertEqual(pages.cursors, [nil, "1", "2", nil, "2"])
        XCTAssertEqual(model.tracks.count, 31); XCTAssertEqual(model.pageNumber, 3)
        XCTAssertNil(model.error)
        model.disappear()
    }
    func testRenewalRejectsChangedUTF8SongKeyPlatformAndNativeIdentity() async throws {
        let service = BrowseActionService(), pages = BrowseActionPages([3])
        let model = await model(service, pages), selected = try XCTUnwrap(model.tracks.first)
        for mutation in 1...4 {
            pages.mutation = mutation
            do { _ = try await model.refreshedActionTrack(selected); XCTFail("changed identity accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        }
        XCTAssertEqual(model.tracks.first, selected)
        model.disappear()
    }
    func testSuspendedRenewalCannotSurviveDepartureGenerationRevocationOrCancellation() async throws {
        for mode in 0..<4 {
            let service = BrowseActionService(), pages = BrowseActionPages([3]), gate = BrowseActionGate()
            let model = await model(service, pages), selected = try XCTUnwrap(model.tracks.first)
            if mode == 2 {
                _ = try await model.playbackSource()
                XCTAssertNotNil(model.remoteSource, "Revocation requires an explicitly opened source")
            }
            pages.gate = gate
            let action = Task { try await model.refreshedActionTrack(selected) }
            await gate.started()
            switch mode {
            case 0: model.disappear()
            case 1: model.setAccess(available: true, generation: UUID())
            case 2: model.remoteSource?.revoke()
            default: action.cancel()
            }
            await gate.release()
            do { _ = try await action.value; XCTFail("stale renewal accepted") }
            catch {
                XCTAssertTrue([MusicServiceError.staleAccess, .staleResult, .cancelled].contains(MusicServiceError.sanitized(error)))
            }
            model.disappear()
        }
    }
}
