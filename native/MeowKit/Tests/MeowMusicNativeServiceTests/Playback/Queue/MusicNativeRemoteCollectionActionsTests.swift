import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeRemoteCollectionActionsTests: XCTestCase {
    func testSavePersistsOnlyRemoteIdentityWithoutReadingMembersOrChangingPlayback() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture(), accounts = NativeMusicAccountFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts)
        _ = try await MusicAccountQueueFixture.seed(service)
        let before = store.snapshot(), old = try MusicDocument(before.0, runID: "fixture")
        let pages = MusicRemoteCollectionActionsFixture.pages((0..<631).map { MusicRemoteCollectionActionsFixture.track("MID\($0)") })
        let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "501", title: "Remote", count: 631)
        let source = MusicRemoteCollectionActionsFixture.source(pages, collection: collection)
        let actions: any MusicRemoteCollectionServing = service
        let saved = try await actions.saveRemoteCollection(source: source, name: "Remote", intent: .init())
        XCTAssertEqual(saved.kind, "network"); XCTAssertEqual(saved.count, 631)
        XCTAssertEqual(saved.networkReference, .init(platform: .qq, remoteID: "501"))
        XCTAssertFalse(saved.canEdit); XCTAssertEqual(saved.remoteCollection, collection)
        XCTAssertEqual(store.snapshot().2, before.2 + 1)
        let current = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertEqual(current.queue, old.queue); XCTAssertEqual(current.playback, old.playback)
        XCTAssertEqual(current.history, old.history)
        XCTAssertEqual(current.library["library"]["collections"], old.library["library"]["collections"])
        XCTAssertEqual(current.library["library"]["songs"], old.library["library"]["songs"])
        XCTAssertEqual(current.library["library"]["nextId"], old.library["library"]["nextId"])
        XCTAssertEqual(Set(current.envelope["records"].fields.map(\.0)),
                       Set(old.envelope["records"].fields.map(\.0)).union([MusicModel.key("library")]))
        let reference = current.library["library"]["networkReferences"].array.first!
        XCTAssertEqual(reference["remoteID"].string, "501"); XCTAssertEqual(reference["platform"].string, "tx")
        XCTAssertFalse(reference.contains("order")); XCTAssertFalse(reference.contains("members"))
        XCTAssertTrue(current.library["library"]["accounts"].fields.isEmpty)
        let requests = await pages.events(), resolutions = await scripts.calls(), accountCalls = await accounts.events()
        XCTAssertTrue(requests.isEmpty); XCTAssertTrue(resolutions.isEmpty); XCTAssertTrue(accountCalls.isEmpty)
        await source.close()
    }

    func testRepeatedSaveIsIdempotentAndNeverCreatesManagedMembership() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages([
            MusicRemoteCollectionActionsFixture.track("B"), MusicRemoteCollectionActionsFixture.track("A")
        ])
        let source = MusicRemoteCollectionActionsFixture.source(pages)
        let first = try await service.saveRemoteCollection(source: source, name: "Remote", intent: .init())
        let before = store.snapshot()
        let second = try await service.saveRemoteCollection(source: source, name: "Different display title", intent: .init())
        XCTAssertEqual(first, second)
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let document = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertTrue(document.library["library"]["songs"].fields.isEmpty)
        XCTAssertEqual(document.library["library"]["collections"].array.count, 1)
        XCTAssertEqual(document.library["library"]["networkReferences"].array.count, 1)
        XCTAssertEqual(document.library["library"]["nextId"].int, 1)
        let requests = await pages.events()
        XCTAssertTrue(requests.isEmpty)
        await source.close()
    }

    func testUnavailableMembersDoNotBlockReferenceAndCASFailureLeavesNoPartialWrite() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        _ = try await MusicAccountQueueFixture.seed(service)
        let before = store.snapshot()
        let pages = MusicRemoteCollectionActionsFixture.pages((0..<31).map { MusicRemoteCollectionActionsFixture.track("MID\($0)") })
        await pages.fail("30")
        let source = MusicRemoteCollectionActionsFixture.source(pages)
        store.fail(.storageConflict)
        do { _ = try await service.saveRemoteCollection(source: source, name: "Remote", intent: .init()); XCTFail("failed CAS") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        store.fail(nil)
        let saved = try await service.saveRemoteCollection(source: source, name: "Remote", intent: .init())
        XCTAssertEqual(saved.networkReference?.remoteID, "501")
        let requests = await pages.events()
        XCTAssertTrue(requests.isEmpty)
        let current = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertEqual(current.library["library"]["nextId"].int, 1)
        await source.close()
    }

    func testSaveRejectsLateReplyAfterSourceIntentProtectionEpochOrTaskCancellation() async throws {
        for mode in 0..<5 {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let gate = NativeMusicGate(), probe = MusicRemoteCollectionActionsProbe(), intent = MusicQueueAuthorization()
            await probe.holdValidation(1, gate: gate)
            let pages = MusicRemoteCollectionActionsFixture.pages([])
            let source = MusicRemoteCollectionActionsFixture.source(pages, probe: probe)
            let task = Task { try await service.saveRemoteCollection(source: source, name: "Remote", intent: intent) }
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
            let requests = await pages.events()
            XCTAssertTrue(requests.isEmpty)
            await source.close(); await service.close()
        }
    }

    func testOwnerValidationAndIntentFencePreventSaveBeforeTheSingleCAS() async throws {
        for revokeIntent in [false, true] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let probe = MusicRemoteCollectionActionsProbe(), gate = NativeMusicGate(), intent = MusicQueueAuthorization()
            await probe.holdValidation(1, gate: gate)
            let source = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([]), probe: probe)
            let task = Task { try await service.saveRemoteCollection(source: source, name: "Remote", intent: intent) }
            await gate.started()
            if revokeIntent { intent.revoke() } else { await probe.fail(.targetChanged) }
            await gate.release()
            do { _ = try await task.value; XCTFail("final authority must win") }
            catch { XCTAssertEqual(error as? MusicServiceError, revokeIntent ? .staleAccess : .targetChanged) }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
            await source.close()
        }
    }

    func testMissingWrongKindOrMismatchedIdentityCannotBeSaved() async throws {
        let cases: [(MusicPublicBrowseCollection?, MusicServiceError)] = [
            (nil, .unsupported),
            (.init(platform: .qq, nativeID: "501", kind: .album, title: "Album"), .unsupported),
            (.init(platform: .netease, nativeID: "501", title: "Other platform"), .unsupported),
            (.init(platform: .qq, nativeID: "playlist:1", title: "Not a remote ID"), .invalidRequest)
        ]
        for (collection, expected) in cases {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let source = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([]), collection: collection)
            do { _ = try await service.saveRemoteCollection(source: source, name: "Remote", intent: .init()); XCTFail("invalid identity") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
            await source.close()
        }
    }

    func testMalformedOrMixedMembersAreNotImportedWhenSavingAReference() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let rows: [MusicNativeTrackData] = [
            .init(platform: "tx", data: Data("{}".utf8)),
            .init(platform: "wy", data: NativeMusicFixture.track().data)
        ]
        let pages = MusicRemoteCollectionActionsFixture.pages(rows), source = MusicRemoteCollectionActionsFixture.source(pages)
        _ = try await service.saveRemoteCollection(source: source, name: "Remote", intent: .init())
        let requests = await pages.events()
        XCTAssertTrue(requests.isEmpty)
        let document = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertTrue(document.library["library"]["songs"].fields.isEmpty)
        await source.close()
    }

    func testReferenceCanRepresentALargePlaylistWithoutReadingMembers() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages([])
        let source = MusicRemoteCollectionActionsFixture.source(pages,
            collection: .init(platform: .qq, nativeID: "501", title: "Large", count: 2001))
        let saved = try await service.saveRemoteCollection(source: source, name: "Large", intent: .init())
        XCTAssertEqual(saved.count, 2001)
        let requests = await pages.events()
        XCTAssertTrue(requests.isEmpty)
        let document = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertTrue(document.library["library"]["songs"].fields.isEmpty)
        await source.close()
    }

    func testReferenceDisplayTitleDoesNotUseTheManagedPlaylistNameLimit() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let title = String(repeating: "T", count: 201), pages = MusicRemoteCollectionActionsFixture.pages([])
        let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "501", title: title)
        let source = MusicRemoteCollectionActionsFixture.source(pages, collection: collection)
        let saved = try await service.saveRemoteCollection(source: source, name: title, intent: .init())
        let duplicate = try await service.saveNetworkPlaylist(collection, intent: .init())
        XCTAssertEqual(saved.title, title); XCTAssertEqual(saved, duplicate)
        let reads = await pages.events()
        XCTAssertTrue(reads.isEmpty); XCTAssertEqual(store.snapshot().2, 1)
        await source.close(); await service.close()
    }

    func testEmptyReferenceIsSavedButBlankNameFailsBeforeAnySourceValidation() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages([]), probe = MusicRemoteCollectionActionsProbe()
        let source = MusicRemoteCollectionActionsFixture.source(pages, probe: probe)
        do { _ = try await service.saveRemoteCollection(source: source, name: " \n ", intent: .init()); XCTFail("blank name") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        let reads = await pages.events(), validations = await probe.validationCount()
        XCTAssertTrue(reads.isEmpty); XCTAssertEqual(validations, 0); XCTAssertEqual(store.snapshot().2, 0)
        let saved = try await service.saveRemoteCollection(source: source, name: "Empty", intent: .init())
        XCTAssertEqual(saved.count, 0); XCTAssertEqual(store.snapshot().2, 1)
        await source.close()
    }
}
