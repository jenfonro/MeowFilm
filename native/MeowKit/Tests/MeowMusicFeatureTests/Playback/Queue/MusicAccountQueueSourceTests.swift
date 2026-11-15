import Foundation
import XCTest
import MeowMusicFeature

private actor MusicAccountQueueSourceFixture {
    var calls = 0
    func read(_ cursor: String?) -> MusicNativeTrackPage {
        calls += 1
        return .init(items: [.init(platform: "wy", data: Data("{}".utf8))], cursor: cursor == nil ? "next" : nil)
    }
    func count() -> Int { calls }
}

private actor MusicAccountQueueLongFixture {
    private(set) var calls: [String?] = []
    private let pages: [MusicNativeTrackPage]
    init(_ pages: [MusicNativeTrackPage]) { self.pages = pages }
    func read(_ cursor: String?) throws -> MusicNativeTrackPage {
        calls.append(cursor)
        let index = cursor.flatMap(Int.init) ?? 0
        guard pages.indices.contains(index) else { throw MusicServiceError.staleResult }
        return pages[index]
    }
}

/// The feature owns no song documents. This synthetic repository is its actual
/// port peer; the native integration tests exercise the real persistent owner.
private actor MusicQueueReferenceFixture {
    var songs: [String: MusicNativeTrackData] = [:]
    private(set) var adopted = 0
    var adoptionFailure: MusicServiceError?
    var projectionFailure: MusicServiceError?
    func configure(adoption: MusicServiceError? = nil, projection: MusicServiceError? = nil) {
        adoptionFailure = adoption; projectionFailure = projection
    }
    func adopt(_ rows: [MusicNativeTrackData], intent: MusicQueueAuthorization) throws -> [MusicQueueTrackReference] {
        try intent.check()
        if let adoptionFailure { throw adoptionFailure }
        adopted += rows.count
        return rows.map { row in
            let id = UUID().uuidString
            songs[id] = row
            return .init(songID: id, occurrenceID: UUID().uuidString, platform: row.platform,
                sourceKey: id, sourcePosition: row.sourcePosition, byteCount: row.data.count)
        }
    }
    func project(_ rows: [MusicQueueTrackReference]) throws -> [MusicNativeTrackData] {
        if let projectionFailure { throw projectionFailure }
        return try rows.map {
            guard let value = songs[$0.songID] else { throw MusicServiceError.notFound }
            return value
        }
    }
    func update(_ id: String, data: Data) {
        songs[id] = .init(platform: "wy", data: data)
    }
}
private func makeQueueSource(context: MusicRemoteQueueContext, authorization: MusicQueueAuthorization = .init(),
    loadPage: @escaping @Sendable (String?) async throws -> MusicNativeTrackPage,
    close: @escaping @Sendable () async -> Void = {},
    dailyStyle: MusicDailyStyleEditing? = nil,
    repository: MusicQueueReferenceFixture = MusicQueueReferenceFixture()) async -> MusicRemoteQueueSource {
    let source = MusicRemoteQueueSource(context: context, authorization: authorization, loadPage: loadPage,
        close: close, dailyStyle: dailyStyle)
    await source.bindSongRepository(adopt: { try await repository.adopt($0, intent: $1) },
        project: { try await repository.project($0) })
    return source
}

final class MusicAccountQueueSourceTests: XCTestCase {
    func testCachedPagesAndCompleteReadsProjectCurrentRepositoryWithoutReadOrAdoptionReplay() async throws {
        let fixture = MusicAccountQueueSourceFixture(), repository = MusicQueueReferenceFixture()
        let source = await makeQueueSource(context: .init(kind: .playlist, id: "current", title: "Current", platform: "wy"),
            loadPage: { await fixture.read($0) }, repository: repository)
        let initial = try await source.referencePage(cursor: nil)
        let saved = try XCTUnwrap(initial.items.first)
        await repository.update(saved.songID, data: Data("Current title, no old album or artwork".utf8))
        let projected = try await source.page(cursor: nil)
        let currentReferences = try await source.referencePage(cursor: nil)
        XCTAssertEqual(projected.items.first?.data, Data("Current title, no old album or artwork".utf8))
        XCTAssertEqual(currentReferences.items.map(\.songID), initial.items.map(\.songID))
        XCTAssertEqual(currentReferences.items.map(\.occurrenceID), initial.items.map(\.occurrenceID))
        let complete = try await source.completeTracks(intent: .init())
        XCTAssertEqual(complete.first?.data, projected.items.first?.data)
        let collected = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(collected.map(\.data), complete.map(\.data))
        let calls = await fixture.count(), adopted = await repository.adopted
        XCTAssertEqual(calls, 2); XCTAssertEqual(adopted, 2)
        await source.close()
    }

    func testDailyStyleReceiptSurvivesOptionalAdoptionAndProjectionFailuresWithoutRepeatingWrite() async throws {
        for failsAdoption in [false, true] {
            let repository = MusicQueueReferenceFixture(), writes = MusicAccountQueueSourceFixture()
            await repository.configure(adoption: failsAdoption ? .storageConflict : nil,
                projection: failsAdoption ? nil : .storageFailed)
            let resultPage = MusicNativeTrackPage(items: [.init(platform: "wy", data: Data("readback".utf8))])
            let source = await makeQueueSource(context: .init(kind: .recommendation, id: "style", title: "Style", platform: "wy"),
                loadPage: { _ in XCTFail("A receipt must not retry its write or read"); return .init(items: []) },
                dailyStyle: .init(scope: "account-a", categories: { [] }, save: { _ in
                    _ = await writes.read(nil)
                    return .init(page: resultPage, saveConfirmed: true, selectionChanged: true)
                }), repository: repository)
            let receipt = try await source.saveDailyStyle(.init(category: "style", tags: []))
            XCTAssertTrue(receipt.saveConfirmed); XCTAssertTrue(receipt.selectionChanged)
            XCTAssertEqual(receipt.page.items.map(\.data), resultPage.items.map(\.data))
            if !failsAdoption {
                do { _ = try await source.page(cursor: nil); XCTFail("Projection failure must remain a read failure") }
                catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
                XCTAssertTrue(receipt.saveConfirmed)
            }
            let count = await writes.count(); XCTAssertEqual(count, 1)
            await source.close()
        }
    }

    func testConcurrentCloseWaitsForTheSamePhysicalReleaseAndRejectsNewReads() async {
        let entered = expectation(description: "physical release entered")
        let gate = MusicQueueCloseGate(entered)
        let source = await makeQueueSource(
            context: .init(kind: .playlist, id: "held", title: "Held", platform: "wy"),
            loadPage: { _ in
                XCTFail("A closing source must not start a new read")
                return .init(items: [])
            }, close: { await gate.hold() })
        let firstFinished = expectation(description: "first close must wait")
        let secondFinished = expectation(description: "second close must wait")
        firstFinished.isInverted = true; secondFinished.isInverted = true
        let first = Task { await source.close(); firstFinished.fulfill() }
        await fulfillment(of: [entered], timeout: 1)
        let second = Task { await source.close(); secondFinished.fulfill() }
        do { _ = try await source.page(cursor: nil); XCTFail("A closing source accepted a read") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        await fulfillment(of: [firstFinished, secondFinished], timeout: 0.05)
        await gate.release()
        await first.value; await second.value
        await source.close()
        let releases = await gate.calls
        XCTAssertEqual(releases, 1)
    }

    func testCollectionReadConsumesAvailableMembersWithoutProvingTotalsOrCompleteness() async throws {
        let row = MusicNativeTrackData(platform: "wy", data: Data("{}".utf8))
        for mode in ["omitted", "partial", "changed", "complete"] {
            let source = await makeQueueSource(context: .init(kind: .playlist, id: "synthetic", title: "Synthetic", platform: "wy",
                unavailableCount: mode == "omitted" ? 1 : 0), loadPage: { cursor in
                    .init(items: [row], total: mode == "changed" && cursor != nil ? 3 : 2,
                          cursor: mode == "partial" || cursor != nil ? nil : "next")
                })
            let playable = try await source.completeTracks(intent: .init())
            XCTAssertEqual(playable.count, mode == "partial" ? 1 : 2)
            let all = try await source.collectionTracks(intent: .init())
            XCTAssertEqual(all.map(\.data), playable.map(\.data))
            XCTAssertEqual(all.count, mode == "partial" ? 1 : 2)
            await source.close()
        }
    }
    func testDailyStyleAcknowledgementFailureInvalidatesEveryOldPageWithoutReplayingWrite() async throws {
        let fixture = MusicStyleSourceFixture()
        let source = await makeQueueSource(
            context: .init(kind: .recommendation, id: "daily-style", title: "风格推荐", platform: "wy"),
            loadPage: { await fixture.page($0) },
            dailyStyle: .init(scope: "account-a", categories: { [] }, save: { try await fixture.save($0) }))
        let before = try await source.page(cursor: nil)
        _ = try await source.page(cursor: "next")
        XCTAssertEqual(String(decoding: before.items[0].data, as: UTF8.self), "0")
        do {
            _ = try await source.saveDailyStyle(.init(category: "3000", tags: [.init(id: "1", name: "合成")]))
            XCTFail("Lost readback was accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        do { _ = try await source.page(cursor: "next"); XCTFail("Stale continuation was reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let next = try await source.page(cursor: nil)
        XCTAssertEqual(String(decoding: next.items[0].data, as: UTF8.self), "1")
        let counts = await fixture.counts()
        XCTAssertEqual(counts.writes, 1); XCTAssertEqual(counts.reads, 3)
        await source.close()
    }

    func testDailyStylePreservesConfirmedReceiptWithoutRequiringSelectionOrCachingOverBudgetReadback() async throws {
        let selection = MusicDailyStyleSelection(category: "3000", tags: [.init(id: "1", name: "合成")])
        for oversized in [false, true] {
            let fixture = MusicAccountQueueSourceFixture()
            let row = MusicNativeTrackData(platform: "wy", data: Data(repeating: 1, count: oversized ? 64 * 1024 : 1))
            let page = MusicNativeTrackPage(items: Array(repeating: row, count: oversized ? 100 : 1),
                dailyStyle: oversized ? selection : nil)
            let source = await makeQueueSource(
                context: .init(kind: .recommendation, id: "daily-style", title: "风格推荐", platform: "wy"),
                loadPage: { await fixture.read($0) },
                dailyStyle: .init(scope: "account-a", categories: { [] }, save: { _ in
                    .init(page: page, saveConfirmed: true, selectionChanged: true)
                }))
            let result = try await source.saveDailyStyle(selection)
            XCTAssertTrue(result.saveConfirmed)
            XCTAssertTrue(result.selectionChanged)
            XCTAssertEqual(result.page.items.map(\.data), page.items.map(\.data))
            XCTAssertEqual(result.page.dailyStyle, oversized ? selection : nil)
            let fresh = try await source.page(cursor: nil)
            XCTAssertEqual(fresh.items.map(\.data), oversized ? [Data("{}".utf8)] : [Data([1])])
            let reads = await fixture.count()
            XCTAssertEqual(reads, oversized ? 1 : 0, "Only an in-budget readback becomes a cached first page")
            await source.close()
        }
    }

    func testDailyStyleSelectionAcceptsDynamicCategoryMembersAndRejectsInvalidSelections() throws {
        let tags = (1...12).map { MusicDailyStyleTag(id: String($0), name: "合成\($0)") }
        let other = MusicDailyStyleTag(id: "50", name: "另一类别成员")
        let categories = [MusicDailyStyleCategory(id: "3000", name: "服务分类甲", tags: tags),
                          MusicDailyStyleCategory(id: "1000", name: "服务分类乙", tags: tags),
                          MusicDailyStyleCategory(id: "dynamic-category", name: "动态类别", tags: tags),
                          MusicDailyStyleCategory(id: "other", name: "另一类别", tags: [other])]
        for category in ["3000", "1000", "dynamic-category"] {
            XCTAssertNoThrow(try MusicDailyStyleSelection(category: category, tags: [tags[0]]).validate(in: categories))
            XCTAssertNoThrow(try MusicDailyStyleSelection(category: category, tags: tags).validate(in: categories))
            XCTAssertThrowsError(try MusicDailyStyleSelection(category: category, tags: []).validate(in: categories))
            XCTAssertThrowsError(try MusicDailyStyleSelection(category: category, tags: [tags[0], tags[0]]).validate(in: categories))
            XCTAssertThrowsError(try MusicDailyStyleSelection(category: category, tags: [tags[0], other]).validate(in: categories))
        }
        XCTAssertThrowsError(try MusicDailyStyleSelection(category: "unknown", tags: [tags[0]]).validate(in: categories))
        XCTAssertThrowsError(try MusicDailyStyleSelection(category: "other", tags: [tags[0]]).validate(in: categories))
    }

    func testPassiveConstructionCachedPagesAndRevocationAlsoRejectCachedRead() async throws {
        let fixture = MusicAccountQueueSourceFixture(), authority = MusicQueueAuthorization()
        let source = await makeQueueSource(context: .init(kind: .playlist, id: "123", title: "Synthetic", platform: "wy"),
            authorization: authority, loadPage: { await fixture.read($0) })
        let before = await fixture.count(); XCTAssertEqual(before, 0)
        _ = try await source.page(cursor: nil)
        let complete = try await source.completeTracks(intent: .init())
        XCTAssertEqual(complete.count, 2)
        _ = try await source.page(cursor: nil)
        let reads = await fixture.count(); XCTAssertEqual(reads, 2)
        authority.revoke()
        do { _ = try await source.page(cursor: nil); XCTFail("cached reads retain account authority") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        let after = await fixture.count(); XCTAssertEqual(after, reads)
    }

    func testFMHasNoAutomaticRequestsAndFreshBatchDoesNotReusePreview() async throws {
        let fixture = MusicAccountQueueSourceFixture()
        let source = await makeQueueSource(context: .init(kind: .fm, id: "fm", title: "Synthetic FM", platform: "wy"),
            loadPage: { await fixture.read($0) })
        let before = await fixture.count(); XCTAssertEqual(before, 0)
        _ = try await source.completeTracks(intent: .init())
        let preview = await fixture.count(); XCTAssertEqual(preview, 2)
        _ = try await source.nextFMBatch(intent: .init())
        let continued = await fixture.count(); XCTAssertEqual(continued, 4)
        let ordinary = await makeQueueSource(context: .init(kind: .album, id: "123", title: "Album", platform: "wy"),
            loadPage: { await fixture.read($0) })
        do { _ = try await ordinary.nextFMBatch(intent: .init()); XCTFail("album is not FM") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        let final = await fixture.count(); XCTAssertEqual(final, continued)
    }

    func testRevocationNotificationAndCommitFenceAreSynchronousAndCredentialFree() async throws {
        let lease = MusicAccountQueueLease(platform: .netease)
        var events = lease.authorization.revocations().makeAsyncIterator()
        lease.revoke(); lease.revoke()
        let event = await events.next(); XCTAssertNotNil(event)
        XCTAssertThrowsError(try lease.authorization.perform { XCTFail("revoked commit"); return true })
        XCTAssertEqual(String(describing: lease), "MusicAccountQueueLease")
        XCTAssertEqual(Mirror(reflecting: lease).children.count, 0)
        XCTAssertEqual(Mirror(reflecting: lease.authorization).children.count, 0)
    }

    func testCompleteTracksKeepsMoreThanFormerTwoThousandSongs() async throws {
        let count = 2001
        let pages = (0..<((count + 29) / 30)).map { page -> MusicNativeTrackPage in
            let start = page * 30, end = min(start + 30, count)
            let items: [MusicNativeTrackData] = (start..<end).map {
                MusicNativeTrackData(platform: "wy", data: Data(String($0).utf8))
            }
            let cursor: String? = end < count ? String(page + 1) : nil
            return MusicNativeTrackPage(items: items, total: count, cursor: cursor)
        }
        let fixture = MusicAccountQueueLongFixture(pages)
        let source = await makeQueueSource(context: .init(kind: .playlist, id: "long", title: "Long", platform: "wy"),
            loadPage: { try await fixture.read($0) })
        let rows = try await source.completeTracks(intent: .init())
        XCTAssertEqual(rows.count, count)
        XCTAssertEqual(rows.first?.data, Data("0".utf8))
        XCTAssertEqual(rows.last?.data, Data("2000".utf8))
        let members = try await source.collectionTracks(intent: .init())
        XCTAssertEqual(members.map(\.data), rows.map(\.data))
        let calls = await fixture.calls
        XCTAssertEqual(calls.count, 67)
        await source.close()
    }

    func testCompleteTracksKeepsMoreThanFormerHundredPageLimit() async throws {
        let count = 101
        let pages: [MusicNativeTrackPage] = (0..<count).map { page -> MusicNativeTrackPage in
            let data = Data(String(page).utf8)
            let row = MusicNativeTrackData(platform: "wy", data: data)
            let cursor: String? = page + 1 < count ? String(page + 1) : nil
            let result = MusicNativeTrackPage(items: [row], total: count, cursor: cursor)
            return result
        }
        let fixture = MusicAccountQueueLongFixture(pages)
        let source = await makeQueueSource(context: .init(kind: .playlist, id: "pages", title: "Pages", platform: "wy"),
            loadPage: { try await fixture.read($0) })
        let rows = try await source.completeTracks(intent: .init())
        XCTAssertEqual(rows.count, count)
        let calls = await fixture.calls
        XCTAssertEqual(calls.count, count)
        await source.close()
    }

    func testCursorCycleStillFailsAndByteBudgetStillLimits() async throws {
        let cycle = await makeQueueSource(context: .init(kind: .playlist, id: "cycle", title: "Cycle", platform: "wy"),
            loadPage: { _ in .init(items: [.init(platform: "wy", data: Data([1]))], total: 1, cursor: "next") })
        do { _ = try await cycle.completeTracks(intent: .init()); XCTFail("cursor cycle") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        await cycle.close()

        let row = MusicNativeTrackData(platform: "wy", data: Data(repeating: 1, count: 64 * 1024))
        let oversized = await makeQueueSource(context: .init(kind: .playlist, id: "bytes", title: "Bytes", platform: "wy"),
            loadPage: { _ in .init(items: Array(repeating: row, count: 100), total: 100) })
        do { _ = try await oversized.completeTracks(intent: .init()); XCTFail("byte budget") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        await oversized.close()
    }

    func testLongCursorCycleAndCumulativeBytesStillFailWithoutCachingRejectedPage() async throws {
        let row = MusicNativeTrackData(platform: "wy", data: Data([1]))
        let cycle = await makeQueueSource(context: .init(kind: .playlist, id: "cycle", title: "Cycle", platform: "wy"),
            loadPage: { cursor in
                .init(items: [row], cursor: cursor == nil ? "a" : cursor == "a" ? "b" : "a")
            })
        do { _ = try await cycle.completeTracks(intent: .init()); XCTFail("multi-page cursor cycle") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        await cycle.close()

        let large = MusicNativeTrackData(platform: "wy", data: Data(repeating: 1, count: 64 * 1024))
        let pages = (0..<3).map { page in
            MusicNativeTrackPage(items: Array(repeating: large, count: 40),
                                 cursor: page < 2 ? String(page + 1) : nil)
        }
        let fixture = MusicAccountQueueLongFixture(pages)
        let source = await makeQueueSource(context: .init(kind: .playlist, id: "bytes", title: "Bytes", platform: "wy"),
            loadPage: { try await fixture.read($0) })
        for _ in 0..<2 {
            do { _ = try await source.completeTracks(intent: .init()); XCTFail("cumulative byte budget") }
            catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        }
        let calls = await fixture.calls
        XCTAssertEqual(calls, [nil, "1", "2", "2"])
        await source.close()
    }

    func testNextFMBatchKeepsMoreThanFormerTrackAndPageLimits() async throws {
        let count = 3030
        let pages = (0..<((count + 29) / 30)).map { page -> MusicNativeTrackPage in
            let start = page * 30, end = min(start + 30, count)
            let items: [MusicNativeTrackData] = (start..<end).map {
                MusicNativeTrackData(platform: "wy", data: Data(String($0).utf8))
            }
            let cursor: String? = end < count ? String(page + 1) : nil
            return MusicNativeTrackPage(items: items, total: count, cursor: cursor)
        }
        let fixture = MusicAccountQueueLongFixture(pages)
        let source = await makeQueueSource(context: .init(kind: .fm, id: "fm-long", title: "FM", platform: "wy"),
            loadPage: { try await fixture.read($0) })
        let rows = try await source.nextFMBatch(intent: .init())
        XCTAssertEqual(rows.count, count)
        XCTAssertEqual(rows.first?.data, Data("0".utf8))
        XCTAssertEqual(rows.last?.data, Data("3029".utf8))
        let calls = await fixture.calls
        XCTAssertEqual(calls.count, 101)
        await source.close()
    }

    func testFMBatchStillRejectsCursorCyclesAndCumulativeByteOverflow() async throws {
        let small = MusicNativeTrackData(platform: "wy", data: Data([1]))
        let cycle = await makeQueueSource(context: .init(kind: .fm, id: "cycle", title: "FM", platform: "wy"),
            loadPage: { cursor in
                .init(items: [small], cursor: cursor == nil ? "a" : cursor == "a" ? "b" : "a")
            })
        do { _ = try await cycle.nextFMBatch(intent: .init()); XCTFail("FM cursor cycle") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        await cycle.close()

        let large = MusicNativeTrackData(platform: "wy", data: Data(repeating: 1, count: 64 * 1024))
        let oversized = await makeQueueSource(context: .init(kind: .fm, id: "bytes", title: "FM", platform: "wy"),
            loadPage: { cursor in
                .init(items: Array(repeating: large, count: 60), cursor: cursor == nil ? "next" : nil)
            })
        do { _ = try await oversized.nextFMBatch(intent: .init()); XCTFail("FM byte budget") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        await oversized.close()
    }
}

private actor MusicQueueCloseGate {
    let entered: XCTestExpectation
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    private(set) var calls = 0
    init(_ entered: XCTestExpectation) { self.entered = entered }
    func hold() async {
        calls += 1; entered.fulfill()
        if !released { await withCheckedContinuation { continuation = $0 } }
    }
    func release() {
        released = true; continuation?.resume(); continuation = nil
    }
}

private actor MusicStyleSourceFixture {
    var revision = 0, reads = 0, writes = 0
    func page(_ cursor: String?) -> MusicNativeTrackPage {
        reads += 1
        return .init(items: [.init(platform: "wy", data: Data(String(revision).utf8))],
                     cursor: cursor == nil ? "next" : nil)
    }
    func save(_ selection: MusicDailyStyleSelection) throws -> MusicDailyStyleSave {
        writes += 1; revision += 1
        throw MusicServiceError.upstreamFailed
    }
    func counts() -> (reads: Int, writes: Int) { (reads, writes) }
}
