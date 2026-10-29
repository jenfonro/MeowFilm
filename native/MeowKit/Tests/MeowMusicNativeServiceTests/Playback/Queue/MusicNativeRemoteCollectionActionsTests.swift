import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeRemoteCollectionActionsTests: XCTestCase {
    func testSaveReadsAll631RowsWithoutHandlesAndCommitsOnlyOneUnboundLocalCollection() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture(), accounts = NativeMusicAccountFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts)
        _ = try await MusicAccountQueueFixture.seed(service)
        let before = store.snapshot(), old = try MusicDocument(before.0, runID: "fixture")
        let rows = (0..<631).map { MusicRemoteCollectionActionsFixture.track("MID\($0)") }
        let pages = MusicRemoteCollectionActionsFixture.pages(rows)
        let source = MusicRemoteCollectionActionsFixture.source(pages)
        let first = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(first.items.count, 30)
        let actions: any MusicRemoteCollectionServing = service
        let saved = try await actions.saveRemoteCollection(source: source, name: "  Local copy  ", intent: .init())
        let tracks = try await service.tracks(in: saved)
        XCTAssertEqual(saved, .init(id: "playlist:1", title: "Local copy", count: 631, revision: 1))
        XCTAssertEqual(tracks.compactMap { $0.sources.first?.nativeId }, (0..<631).map { "MID\($0)" })
        XCTAssertTrue(tracks.allSatisfy { $0.ref.kind == .library })
        XCTAssertEqual(store.snapshot().2, before.2 + 1)
        let current = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertEqual(current.queue, old.queue); XCTAssertEqual(current.playback, old.playback)
        XCTAssertEqual(current.history, old.history)
        XCTAssertEqual(Set(current.envelope["records"].fields.map(\.0)),
                       Set(old.envelope["records"].fields.map(\.0)).union([MusicModel.key("library")]))
        let local = current.library["library"]["collections"].array.last!
        XCTAssertTrue(local["bindings"].array.isEmpty); XCTAssertTrue(local["creating"].fields.isEmpty)
        XCTAssertTrue(current.library["library"]["accounts"].fields.isEmpty)
        XCTAssertEqual(local["intents"].fields.count, 631)
        XCTAssertTrue(local["intents"].fields.allSatisfy { $0.1["origin"].string == "local" && $0.1["pending"].bool })
        let requests = await pages.events(), resolutions = await scripts.calls(), accountCalls = await accounts.events()
        XCTAssertEqual(requests.count, 22); XCTAssertTrue(resolutions.isEmpty); XCTAssertTrue(accountCalls.isEmpty)
        await source.close()
    }

    func testSavingCopiesUsesExistingIdentityMergeAndDoesNotRebindOrDuplicateMembership() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let a = MusicRemoteCollectionActionsFixture.track("A"), b = MusicRemoteCollectionActionsFixture.track("B")
        let source = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([b, a, b, a]))
        let first = try await service.saveRemoteCollection(source: source, name: "Copy", intent: .init())
        let second = try await service.saveRemoteCollection(source: source, name: "Copy", intent: .init())
        XCTAssertEqual(first.count, 2); XCTAssertEqual(second.count, 2); XCTAssertNotEqual(first.id, second.id)
        let tracks = try await service.tracks(in: second)
        XCTAssertEqual(tracks.compactMap { $0.sources.first?.nativeId }, ["B", "A"])
        let document = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertEqual(document.library["library"]["songs"].fields.count, 2)
        XCTAssertEqual(document.library["library"]["nextId"].int, 3)
        XCTAssertTrue(document.library["library"]["collections"].array.allSatisfy { $0["bindings"].array.isEmpty })
        await source.close()
    }

    func testLatePageAndCASFailureKeepOriginalDocumentAndRetryDoesNotBurnACollectionID() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        _ = try await MusicAccountQueueFixture.seed(service)
        let before = store.snapshot()
        let pages = MusicRemoteCollectionActionsFixture.pages((0..<31).map { MusicRemoteCollectionActionsFixture.track("MID\($0)") })
        let source = MusicRemoteCollectionActionsFixture.source(pages)
        await pages.fail("30")
        do { _ = try await service.saveRemoteCollection(source: source, name: "Copy", intent: .init()); XCTFail("partial import") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await pages.fail(nil); store.fail(.storageConflict)
        do { _ = try await service.saveRemoteCollection(source: source, name: "Copy", intent: .init()); XCTFail("failed CAS") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        store.fail(nil)
        let saved = try await service.saveRemoteCollection(source: source, name: "Copy", intent: .init())
        XCTAssertEqual(saved.id, "playlist:1"); XCTAssertEqual(saved.count, 31)
        await source.close()
    }

    func testSaveRejectsLateReplyAfterSourceIntentProtectionEpochOrTaskCancellation() async throws {
        for mode in 0..<5 {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let gate = NativeMusicGate(), intent = MusicQueueAuthorization()
            let pages = MusicRemoteCollectionActionsFixture.pages(Array(repeating: NativeMusicFixture.track(), count: 31))
            await pages.hold("30", gate: gate)
            let source = MusicRemoteCollectionActionsFixture.source(pages)
            let task = Task { try await service.saveRemoteCollection(source: source, name: "Copy", intent: intent) }
            await gate.started()
            switch mode {
            case 0: source.revoke()
            case 1: intent.revoke()
            case 2: service.revokeAccess(generation: 2)
            case 3: try await service.setAccess(available: true, generation: 2)
            default: task.cancel()
            }
            await gate.release()
            do { _ = try await task.value; XCTFail("late save mode \(mode)") }
            catch {
                XCTAssertTrue([MusicServiceError.staleAccess, .cancelled, .accessUnavailable].contains(error as? MusicServiceError ?? .invalidResponse))
            }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
            await source.close(); await service.close()
        }
    }

    func testFinalOwnerValidationAndIntentFencePreventSaveAfterAllRowsArrive() async throws {
        for revokeIntent in [false, true] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let probe = MusicRemoteCollectionActionsProbe(), gate = NativeMusicGate(), intent = MusicQueueAuthorization()
            // One page validates before/after loading and at completeTracks'
            // end. The fourth call is the action's final owner revalidation.
            await probe.holdValidation(4, gate: gate)
            let source = MusicRemoteCollectionActionsFixture.source(
                MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track()]), probe: probe)
            let task = Task { try await service.saveRemoteCollection(source: source, name: "Copy", intent: intent) }
            await gate.started()
            if revokeIntent { intent.revoke() } else { await probe.fail(.targetChanged) }
            await gate.release()
            do { _ = try await task.value; XCTFail("final authority must win") }
            catch { XCTAssertEqual(error as? MusicServiceError, revokeIntent ? .staleAccess : .targetChanged) }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
            await source.close()
        }
    }

    func testSaveRejectsMalformedMixedPlatformAndOversizedSourcesWithoutPartialWrites() async throws {
        let cases: [([MusicNativeTrackData], MusicServiceError)] = [
            ([.init(platform: "tx", data: Data("{}".utf8))], .invalidResponse),
            ([NativeMusicFixture.track(), .init(platform: "wy", data: NativeMusicFixture.track().data)], .invalidResponse),
            (Array(repeating: NativeMusicFixture.track(), count: MusicRemoteQueueSource.maximumTracks + 1), .limit)
        ]
        for (rows, expected) in cases {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let source = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages(rows))
            do { _ = try await service.saveRemoteCollection(source: source, name: "Copy", intent: .init()); XCTFail("invalid snapshot") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
            await source.close()
        }
    }

    func testEmptyCollectionIsSavedButBlankNameFailsBeforeAnySourceRead() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages([])
        let source = MusicRemoteCollectionActionsFixture.source(pages)
        do { _ = try await service.saveRemoteCollection(source: source, name: " \n ", intent: .init()); XCTFail("blank name") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        let reads = await pages.events()
        XCTAssertTrue(reads.isEmpty); XCTAssertEqual(store.snapshot().2, 0)
        let saved = try await service.saveRemoteCollection(source: source, name: "Empty", intent: .init())
        XCTAssertEqual(saved.count, 0); XCTAssertEqual(store.snapshot().2, 1)
        await source.close()
    }
}
