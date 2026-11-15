import XCTest
@testable import MeowMusicFeature

private actor OccurrenceBrowseService: MusicPublicBrowsing, MusicPublicBrowseQueueLoading {
    static let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "41", title: "Synthetic")
    private(set) var cursors: [String?] = []
    private(set) var queueCursors: [String?] = []
    private(set) var opened = 0
    private var expanded = false, changed = false, omitEarlierMember = false, omitSelectedMember = false
    private var withoutSourcePositions = false
    private let onClose: @Sendable () -> Void
    init(onClose: @escaping @Sendable () -> Void = {}) { self.onClose = onClose }
    func expandSources(_ value: Bool) { expanded = value }
    func changeSelectedIdentity() { changed = true }
    func removeEarlierMember() { omitEarlierMember = true }
    func removeSelectedMember() { omitSelectedMember = true }
    func omitSourcePositions() { withoutSourcePositions = true }
    static func track(_ index: Int, changed: Bool = false, expanded: Bool = false,
                      includesPosition: Bool = true) -> MusicRemoteTrack {
        let id = changed ? "999" : [1, 29, 30, 31].contains(index) ? "7" : String(index + 100)
        return .init(ref: .init(kind: .library, id: "tx:" + id, occurrence: "member:\(index)"),
            songId: "tx:" + id, title: [2, 28, 31].contains(index) ? "needle" : "Song",
            artist: "Artist", sources: [.init(sourceKey: "tx:" + id, platform: "tx", nativeId: id)] +
                (expanded ? [.init(sourceKey: "wy:123", platform: "wy", nativeId: "123")] : []),
            sourcePosition: includesPosition ? index : nil)
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) -> MusicPublicBrowseDetail {
        cursors.append(cursor)
        let positions = cursor == nil ? Array(0..<30) : [30, 31, 32]
        return .init(collection: collection, members: positions.map {
            let track = Self.track($0, changed: changed && $0 == 31, expanded: expanded,
                                   includesPosition: !withoutSourcePositions)
            return MusicPublicBrowseMember(nativeID: track.sources[0].nativeId,
                                           sourcePosition: withoutSourcePositions ? nil : $0, track: track)
        }, total: 33, cursor: cursor == nil ? "30" : nil, consumed: positions.count)
    }
    func queuePage(_ cursor: String?) -> MusicBrowsePage {
        queueCursors.append(cursor)
        let positions = (cursor == nil ? Array(0..<30) : omitEarlierMember ? [31, 32] : [30, 31, 32])
            .filter { !omitSelectedMember || $0 != 31 }
        return .init(items: positions.map { Self.track($0, changed: changed && $0 == 31, expanded: expanded,
                                                      includesPosition: !withoutSourcePositions) },
                     cursor: cursor == nil ? "30" : nil)
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
            queuePage: { _, cursor in await service.queuePage(cursor) })
        model.setAccess(available: true, generation: UUID()); model.appear()
        return model
    }
    func testRepeatedSongsKeepDistinctOccurrenceReferencesWithoutActionRenewal() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.appendNext()
        let repeated = model.tracks.filter { $0.songId == "tx:7" }
        XCTAssertEqual(repeated.count, 4)
        XCTAssertEqual(Set(repeated.map(\.ref)).count, 4)
        let selected = try XCTUnwrap(repeated.last), intent = try XCTUnwrap(model.makeActionIntent())
        var ref: MusicReference?
        await model.perform(intent: intent) { ref = selected.ref }
        XCTAssertEqual(ref?.id, "tx:7"); XCTAssertEqual(ref?.occurrence, "member:31")
        let cursors = await service.cursors, opened = await service.opened
        XCTAssertEqual(cursors, [nil, "30"]); XCTAssertEqual(opened, 0)
        XCTAssertEqual(model.tracks[31].ref, selected.ref)
        model.disappear()
    }
    func testCompleteQueueAndFilteredIndexCountEveryPrecedingOccurrence() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.first { $0.sourcePosition == 31 })
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31, "Repeated songs at 1/29/30 are separate queue occurrences")
        let filtered = try await model.filteredQueueIndex(source: source, index: index, query: "needle")
        XCTAssertEqual(filtered, 2)
        XCTAssertEqual(model.pageNumber, 2)
        model.disappear(); await source.close()
    }
    func testNilSourcePositionUsesLoadedPagePrefixAndCachedNavigationForRepeatedSong() async throws {
        let service = OccurrenceBrowseService()
        await service.omitSourcePositions()
        let model = model(service)
        await model.refresh(); await model.next()
        XCTAssertEqual(model.pageNumber, 2); XCTAssertEqual(model.tracks.count, 3)
        let selected = try XCTUnwrap(model.tracks.first { $0.ref.occurrence == "member:31" })
        XCTAssertNil(selected.sourcePosition)
        let before = await service.opened; XCTAssertEqual(before, 0)
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31)
        var queueCursors = await service.queueCursors; XCTAssertEqual(queueCursors, [nil, "30"])
        let filtered = try await model.filteredQueueIndex(source: source, index: index, query: "needle")
        XCTAssertEqual(filtered, 2)
        await model.previous()
        let earlier = try XCTUnwrap(model.tracks.first { $0.ref.occurrence == "member:29" })
        XCTAssertEqual(earlier.songId, selected.songId); XCTAssertNotEqual(earlier.ref, selected.ref)
        let (_, earlierIndex) = try await model.playbackSource(for: earlier)
        XCTAssertEqual(earlierIndex, 29)
        await model.next()
        XCTAssertEqual(model.tracks.first { $0.ref.occurrence == "member:31" }?.ref, selected.ref)
        let (sameSource, repeatedIndex) = try await model.playbackSource(for: selected)
        XCTAssertEqual(sameSource.id, source.id); XCTAssertEqual(repeatedIndex, 31)
        let cursors = await service.cursors, opened = await service.opened
        XCTAssertEqual(cursors, [nil, "30"]); XCTAssertEqual(opened, 1)
        queueCursors = await service.queueCursors
        XCTAssertEqual(queueCursors, [nil, "30", nil, "30", nil, nil, "30"])
        model.disappear(); await source.close()
    }
    func testNilSourcePositionDoesNotCountTheAppendedPrefixTwice() async throws {
        let service = OccurrenceBrowseService()
        await service.omitSourcePositions()
        let model = model(service)
        await model.refresh(); await model.appendNext()
        XCTAssertEqual(model.tracks.count, 33)
        let repeated = model.tracks.filter { $0.songId == "tx:7" }
        XCTAssertEqual(Set(repeated.map(\.ref)).count, 4)
        XCTAssertTrue(repeated.allSatisfy { $0.ref.kind == .library && $0.ref.id == $0.songId && $0.sourcePosition == nil })
        let selected = try XCTUnwrap(repeated.last)
        let before = await service.opened; XCTAssertEqual(before, 0)
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31); XCTAssertEqual(model.tracks[31].ref, selected.ref)
        let cursors = await service.cursors, queueCursors = await service.queueCursors
        XCTAssertEqual(cursors, [nil, "30"]); XCTAssertEqual(queueCursors, [nil, "30"])
        model.disappear(); await source.close()
    }
    func testSourceProjectionChangesDoNotInvalidateTheSelectedOccurrenceOrFiltering() async throws {
        let service = OccurrenceBrowseService()
        await service.expandSources(true)
        let model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.first { $0.sourcePosition == 31 })
        XCTAssertEqual(selected.sources.count, 2)
        await service.expandSources(false)
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31)
        let filtered = try await model.filteredQueueIndex(source: source, index: index, query: "needle")
        XCTAssertEqual(filtered, 2)
        XCTAssertEqual(model.tracks.first { $0.sourcePosition == 31 }?.ref, selected.ref)
        let cursors = await service.cursors; XCTAssertEqual(cursors, [nil, "30"])
        model.disappear(); await source.close()
    }
    func testMissingRepeatedOccurrenceDoesNotRetargetAnotherEqualSong() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.first { $0.sourcePosition == 31 })
        await service.removeSelectedMember()
        do { _ = try await model.playbackSource(for: selected); XCTFail("Missing occurrence was substituted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(model.tracks.first { $0.sourcePosition == 31 }?.ref, selected.ref)
        let cursors = await service.cursors; XCTAssertEqual(cursors, [nil, "30"])
        let source = model.remoteSource
        model.disappear(); await source?.close()
    }
    func testCurrentSourcePositionDoesNotRevalidateOldSongIdentity() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.first { $0.sourcePosition == 31 })
        await service.changeSelectedIdentity()
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31)
        XCTAssertEqual(model.tracks.first { $0.sourcePosition == 31 }?.ref, selected.ref)
        let cursors = await service.cursors, queueCursors = await service.queueCursors
        XCTAssertEqual(cursors, [nil, "30"]); XCTAssertEqual(queueCursors, [nil, "30"])
        model.disappear(); await source.close()
    }
    func testSourcePositionFindsTheCompactQueueIndexAfterAnEarlierMemberDisappears() async throws {
        let service = OccurrenceBrowseService(), model = model(service)
        await model.refresh(); await model.next()
        let selected = try XCTUnwrap(model.tracks.last)
        XCTAssertEqual(selected.sourcePosition, 32)
        await service.removeEarlierMember()
        let (source, index) = try await model.playbackSource(for: selected)
        XCTAssertEqual(index, 31)
        XCTAssertEqual(selected.songId, "tx:132")
        XCTAssertEqual(model.tracks.last?.ref, selected.ref)
        model.disappear(); await source.close()
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
        await source.close()
    }
}
