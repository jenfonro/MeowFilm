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

final class MusicAccountQueueSourceTests: XCTestCase {
    func testDailyStyleAcknowledgementFailureInvalidatesEveryOldPageWithoutReplayingWrite() async throws {
        let fixture = MusicStyleSourceFixture()
        let source = MusicRemoteQueueSource(
            context: .init(kind: .recommendation, id: "daily-style", title: "风格推荐", platform: "wy"),
            loadPage: { await fixture.page($0) },
            dailyStyle: .init(categories: { [] }, save: { try await fixture.save($0) }))
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

    func testDailyStyleSelectionLimitsMatchOriginalCategories() throws {
        let tags = (1...6).map { MusicDailyStyleTag(id: String($0), name: "合成\($0)") }
        let categories = [MusicDailyStyleCategory(id: "3000", name: "单选", tags: tags),
                          MusicDailyStyleCategory(id: "1000", name: "多选", tags: tags)]
        XCTAssertNoThrow(try MusicDailyStyleSelection(category: "3000", tags: [tags[0]]).validate(in: categories))
        XCTAssertThrowsError(try MusicDailyStyleSelection(category: "3000", tags: Array(tags.prefix(2))).validate(in: categories))
        XCTAssertNoThrow(try MusicDailyStyleSelection(category: "1000", tags: Array(tags.prefix(5))).validate(in: categories))
        XCTAssertThrowsError(try MusicDailyStyleSelection(category: "1000", tags: tags).validate(in: categories))
        XCTAssertThrowsError(try MusicDailyStyleSelection(category: "1000", tags: [tags[0], tags[0]]).validate(in: categories))
        XCTAssertThrowsError(try MusicDailyStyleSelection(category: "unknown", tags: [tags[0]]).validate(in: categories))
    }

    func testPassiveConstructionCachedPagesAndRevocationAlsoRejectCachedRead() async throws {
        let fixture = MusicAccountQueueSourceFixture(), authority = MusicQueueAuthorization()
        let source = MusicRemoteQueueSource(context: .init(kind: .playlist, id: "123", title: "Synthetic", platform: "wy"),
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
        let source = MusicRemoteQueueSource(context: .init(kind: .fm, id: "fm", title: "Synthetic FM", platform: "wy"),
            loadPage: { await fixture.read($0) })
        let before = await fixture.count(); XCTAssertEqual(before, 0)
        _ = try await source.completeTracks(intent: .init())
        let preview = await fixture.count(); XCTAssertEqual(preview, 2)
        _ = try await source.nextFMBatch(intent: .init())
        let continued = await fixture.count(); XCTAssertEqual(continued, 4)
        let ordinary = MusicRemoteQueueSource(context: .init(kind: .album, id: "123", title: "Album", platform: "wy"),
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
