import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicAccountNodeSyncTests: XCTestCase {
    func testCollectionPreflightRetainsRevisionReadonlyFavoritesAndOccurrenceBoundaries() throws {
        let first = MusicLibrarySyncMember(nativeID: "101", occurrenceID: "first", track: Data())
        let second = MusicLibrarySyncMember(nativeID: "101", occurrenceID: "second", track: Data())
        let collection = MusicLibrarySyncCollection(id: "501", name: "自有", kind: .playlist, writable: true,
            canRename: true, canDelete: true, canOrder: true, members: [first, second], revision: "current")
        let readonly = MusicLibrarySyncCollection(id: "502", name: "只读", kind: .playlist, writable: false,
            canRename: false, canDelete: false, canOrder: false, members: [], revision: "readonly")
        let favorites = MusicLibrarySyncCollection(id: "503", name: "喜欢", kind: .favorites, writable: true,
            canRename: false, canDelete: false, canOrder: true, members: [first], revision: "favorites")
        let snapshot = MusicLibrarySyncSnapshot(accountID: "101",
            collections: [collection, readonly, favorites], revision: "directory")
        let bad: [(MusicLibrarySyncMutation, MusicServiceError)] = [
            (.init(action: .create, name: "新建", directoryRevision: "obsolete"), .revisionConflict),
            (.init(action: .delete, collectionID: readonly.id, before: readonly), .revisionConflict),
            (.init(action: .delete, collectionID: favorites.id, before: favorites), .favoritesProtected),
            (.init(action: .rename, collectionID: favorites.id, name: "改名", before: favorites), .favoritesProtected),
            (.init(action: .add, collectionID: collection.id, before: collection, members: [first]), .revisionConflict),
            (.init(action: .remove, collectionID: collection.id, before: collection, members: [first, first]), .revisionConflict),
            (.init(action: .order, collectionID: collection.id, before: collection, members: [first, first]), .revisionConflict)
        ]
        for (mutation, expected) in bad {
            XCTAssertThrowsError(try MusicAccountSyncPreflight.collection(mutation, snapshot: snapshot)) {
                XCTAssertEqual($0 as? MusicServiceError, expected)
            }
        }
        XCTAssertNoThrow(try MusicAccountSyncPreflight.collection(.init(action: .order,
            collectionID: collection.id, before: collection, members: [second, first]), snapshot: snapshot))
        XCTAssertNoThrow(try MusicAccountSyncPreflight.collection(.init(action: .create,
            name: String(repeating: "字", count: 201), directoryRevision: "directory"), snapshot: snapshot),
            "A guessed name-length rule is not a transaction boundary")
    }

    func testKugouRemoveExpandsSelectedRecordingToAllFreshOccurrencesWithoutRetry() async throws {
        let f = try MusicAccountSyncRuntimeFixture(platform: .kugou), session = try await f.begin()
        let snapshot = try await f.owner.readLibrarySync(session, kinds: [.playlist])
        let collection = try XCTUnwrap(snapshot.collections.first)
        XCTAssertEqual(collection.members.map(\.nativeID), ["101", "101", "102"])
        let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: .remove,
            collectionID: collection.id, before: collection, members: [collection.members[0]]))
        if case .acknowledged = result {} else { XCTFail("The selected recording should be removed") }
        let requests = await f.peer.recorded(), writes = requests.filter { $0.operation == "sync.write" }
        XCTAssertEqual(writes.count, 1)
        let write = try XCTUnwrap(writes.first)
        XCTAssertEqual(write.parameters[music: "members"].musicArray.map { $0[music: "key"] },
            [.string("member-0"), .string("member-1")])
        guard case .object(let remote) = write.parameters[music: "remote"] else {
            XCTFail("The actual locator is required"); try await f.close(); return
        }
        XCTAssertNil(remote["members"], "Fresh member songs are not consumed by the removal recipe")
        XCTAssertEqual(requests.filter { $0.operation == "sync.read" }.count, 2,
            "The write uses one fresh collection, not a second snapshot proof")
        try await f.close()
    }

    func testIdentifierOnlyMutationsIgnoreUnconsumedTrackDataAndDoNotTransmitSongs() async throws {
        let cases: [(MusicAccountPlatform, MusicLibrarySyncMutation.Action)] = [
            (.netease, .order), (.netease, .add), (.netease, .remove),
            (.qq, .add), (.qq, .remove), (.kugou, .order), (.kugou, .remove)
        ]
        for (platform, action) in cases {
            let f = try MusicAccountSyncRuntimeFixture(platform: platform), session = try await f.begin()
            let snapshot = try await f.owner.readLibrarySync(session, kinds: [.playlist])
            let collection = try XCTUnwrap(snapshot.collections.first)
            let bad = Data("not JSON".utf8)
            let members: [MusicLibrarySyncMember]
            switch action {
            case .order:
                members = collection.members.reversed().map {
                    .init(nativeID: $0.nativeID, occurrenceID: $0.occurrenceID, track: bad)
                }
            case .add: members = [.init(nativeID: "103", occurrenceID: "new", track: Data())]
            default:
                members = [.init(nativeID: collection.members[0].nativeID,
                    occurrenceID: collection.members[0].occurrenceID, track: bad)]
            }
            let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: action,
                collectionID: collection.id, before: collection, members: members))
            if case .acknowledged = result {} else { XCTFail("Only consumed materials may affect this write") }
            let requests = await f.peer.recorded(), writes = requests.filter { $0.operation == "sync.write" }
            XCTAssertEqual(writes.count, 1)
            let write = try XCTUnwrap(writes.first)
            guard case .object(let fields) = write.parameters,
                  case .object(let remote)? = fields["remote"] else {
                XCTFail("The actual write parameters are required"); try await f.close(); continue
            }
            XCTAssertNil(fields["songs"])
            XCTAssertNil(remote["members"])
            if platform == .kugou {
                let expected = action == .remove ? Array(collection.members.prefix(2)) : members
                XCTAssertEqual(write.parameters[music: "members"].musicArray.map { $0[music: "key"].musicText },
                    expected.map(\.occurrenceID))
                XCTAssertNil(fields["ids"])
            } else {
                XCTAssertEqual(write.parameters[music: "ids"].musicArray.map(\.musicText), members.map(\.nativeID))
                XCTAssertNil(fields["members"])
            }
            XCTAssertEqual(requests.filter { $0.operation == "sync.read" }.count, 2)
            try await f.close()
        }
    }

    func testKugouAddConsumesItsSourceHintsAndRejectsUnusableTrackBeforeWrite() async throws {
        let f = try MusicAccountSyncRuntimeFixture(platform: .kugou), session = try await f.begin()
        let snapshot = try await f.owner.readLibrarySync(session, kinds: [.playlist])
        let collection = try XCTUnwrap(snapshot.collections.first)
        let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: .add,
            collectionID: collection.id, before: collection,
            members: [.init(nativeID: "103", occurrenceID: "new", track: Data("not JSON".utf8))]))
        if case .notDispatched(let error) = result { XCTAssertEqual(error, .invalidRequest) }
        else { XCTFail("Consumed source material must be decodable before dispatch") }
        let failed = await f.peer.recorded()
        XCTAssertFalse(failed.contains { $0.operation == "sync.write" })
        let track = try MusicAccountFixture.track("103", platform: "kg").data
        let valid = try await f.owner.writeLibrarySync(session, mutation: .init(action: .add,
            collectionID: collection.id, before: collection,
            members: [.init(nativeID: "103", occurrenceID: "new", track: track)]))
        if case .acknowledged = valid {} else { XCTFail("Actual source hints should dispatch") }
        let requests = await f.peer.recorded(), writes = requests.filter { $0.operation == "sync.write" }
        XCTAssertEqual(writes.count, 1)
        XCTAssertEqual(writes.first?.parameters[music: "songs"][music: "103"],
            try RuntimeJSON.decode(track))
        try await f.close()
    }

    func testQQOrderRemainsUnsupportedWithoutDispatchingOrEmulatingAnotherMutation() async throws {
        let f = try MusicAccountSyncRuntimeFixture(platform: .qq), session = try await f.begin()
        let snapshot = try await f.owner.readLibrarySync(session, kinds: [.playlist])
        let collection = try XCTUnwrap(snapshot.collections.first)
        let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: .order,
            collectionID: collection.id, before: collection, members: Array(collection.members.reversed())))
        if case .notDispatched(let error) = result { XCTAssertEqual(error, .unsupported) }
        else { XCTFail("Unsupported order cannot become another write") }
        let requests = await f.peer.recorded()
        XCTAssertFalse(requests.contains { $0.operation == "sync.write" })
        try await f.close()
    }

    func testKugouPublicLocatorOwnedByCurrentAccountCannotBecomeSubscription() async throws {
        let f = try MusicAccountSyncRuntimeFixture(platform: .kugou), session = try await f.begin()
        let snapshot = try await f.owner.readLibrarySubscriptions(session)
        await f.peer.setTargetCreator(snapshot.accountID)
        let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
            collectionID: "public-global-id", name: "自有公共歌单", directoryRevision: snapshot.revision))
        if case .notDispatched(let error) = result { XCTAssertEqual(error, .revisionConflict) }
        else { XCTFail("An owned public locator is not another account's subscription") }
        let requests = await f.peer.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "sync.target" }.count, 1)
        XCTAssertFalse(requests.contains { $0.operation == "sync.prepare" || $0.operation == "sync.write" })
        try await f.close()
    }

    func testDirectoryRevisionIsStableAcrossReadKindsAndMemberProjectionWhileOccurrencesRemainIndependent() async throws {
        let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
        let full = try await f.owner.readLibrarySync(session, kinds: [.playlist, .favorites])
        let dedicated = try await f.owner.readLibrarySubscriptions(session)
        let favorites = try await f.owner.readLibrarySync(session, kinds: [.favorites])
        XCTAssertEqual(full.revision, dedicated.revision)
        XCTAssertEqual(full.revision, favorites.revision)
        XCTAssertTrue(dedicated.collections.isEmpty)
        XCTAssertEqual(Set(dedicated.ownedCollectionIDs), ["501", "503"])
        let collection = try XCTUnwrap(full.collections.first)
        XCTAssertEqual(collection.members.map(\.nativeID), ["101", "101", "102"])
        XCTAssertEqual(Set(collection.members.map(\.occurrenceID)).count, 3)
        let again = try await f.owner.readLibrarySync(session, kinds: [.playlist])
        XCTAssertEqual(again.collections.first?.revision, collection.revision,
            "Object dictionary enumeration cannot manufacture a write conflict")
        await f.peer.renameSpecial("真实更新后的特殊自有歌单")
        let updated = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertNotEqual(updated.revision, dedicated.revision)
        let requests = await f.peer.recorded()
        XCTAssertFalse(requests.contains { $0.operation == "sync.prepare" || $0.operation == "sync.write" })
        try await f.close()
    }

    func testOwnedChangedAndWrongRelationshipFailBeforeObtainingTokenOrDispatchingWrite() async throws {
        let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
        let directory = try await f.owner.readLibrarySubscriptions(session)
        for mutation: MusicLibrarySyncMutation in [
            .init(action: .subscribe, collectionID: "501", name: "自有", directoryRevision: directory.revision),
            .init(action: .subscribe, collectionID: "503", name: "特殊自有", directoryRevision: directory.revision),
            .init(action: .subscribe, collectionID: "900", name: "远端", directoryRevision: "obsolete"),
            .init(action: .unsubscribe, collectionID: "502", directoryRevision: directory.revision,
                beforeSubscription: .init(remoteID: "502", relationID: "other", name: "错误关系"))
        ] {
            let result = try await f.owner.writeLibrarySync(session, mutation: mutation)
            if case .notDispatched(let error) = result { XCTAssertEqual(error, .revisionConflict) }
            else { XCTFail("A failed real transaction preflight cannot dispatch") }
        }
        let requests = await f.peer.recorded()
        XCTAssertFalse(requests.contains { ["sync.prepare", "sync.target", "sync.write"].contains($0.operation) })
        XCTAssertTrue(requests.filter { $0.operation == "sync.read" }.allSatisfy {
            $0.parameters[music: "subscriptionsOnly"] == .bool(true)
        })
        try await f.close()
    }

    func testSubscribeAndUnsubscribePassOpaquePreparationOnlyAfterRealDirectoryPreflight() async throws {
        let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
        let before = f.store.peek(f.identity)
        let directory = try await f.owner.readLibrarySubscriptions(session)
        let subscribed = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
            collectionID: "900", name: "远端", directoryRevision: directory.revision))
        if case .acknowledged = subscribed {} else { XCTFail("Valid relationship should dispatch") }
        let unsubscribed = try await f.owner.writeLibrarySync(session, mutation: .init(action: .unsubscribe,
            collectionID: "502", directoryRevision: directory.revision,
            beforeSubscription: directory.subscriptions.first))
        if case .acknowledged = unsubscribed {} else { XCTFail("Current subscription should dispatch") }
        let requests = await f.peer.recorded()
        let writes = requests.filter { $0.operation == "sync.write" }
        XCTAssertEqual(writes.map { $0.parameters[music: "action"] }, [.string("subscribe"), .string("unsubscribe")])
        XCTAssertEqual(writes.map { $0.parameters[music: "id"] }, [.string("900"), .string("502")])
        XCTAssertTrue(writes.allSatisfy { !$0.parameters[music: "preparationId"].musicText.isEmpty })
        XCTAssertEqual(requests.filter { $0.operation == "sync.prepare" }.count, 2)
        XCTAssertEqual(writes.last?.parameters[music: "remote"][music: "relationId"], .string("relation-502"))
        XCTAssertTrue(requests.filter { $0.operation == "sync.read" }.allSatisfy {
            $0.parameters[music: "subscriptionsOnly"] == .bool(true)
        })
        XCTAssertEqual(f.store.peek(f.identity), before, "Remote writes cannot republish or replace secure credentials")
        try await f.close()
    }

    func testPreparationFailureAndRevocationCannotSendAndAreNeverAutomaticallyRetried() async throws {
        for failure in ["NETWORK_ERROR", "TIMEOUT", "CANCELLED", "INVALID_RESPONSE"] {
            let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
            let directory = try await f.owner.readLibrarySubscriptions(session)
            await f.peer.fail("sync.prepare", code: failure)
            let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                collectionID: "900", name: "远端", directoryRevision: directory.revision))
            if case .notDispatched(let error) = result {
                XCTAssertEqual(error, MusicPlatformClient.error(RuntimeHTTPError.remote(failure)))
            } else { XCTFail("Failed preparation is not a sent mutation") }
            let requests = await f.peer.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "sync.prepare" }.count, 1)
            XCTAssertFalse(requests.contains { $0.operation == "sync.write" })
            try await f.close()
        }
        for event in ["logout", "credential-revision", "end-sync", "cancel"] {
            let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
            let directory = try await f.owner.readLibrarySubscriptions(session), gate = MusicAccountTestGate()
            await f.peer.hold("sync.prepare", gate: gate)
            let pending = Task { try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                collectionID: "900", name: "远端", directoryRevision: directory.revision)) }
            await gate.started()
            switch event {
            case "logout": try await f.owner.logout(platform: .netease)
            case "credential-revision":
                try f.store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: f.identity)
            case "end-sync": await f.owner.endLibrarySync(session)
            default: pending.cancel()
            }
            await gate.release()
            let result = try await pending.value
            if case .notDispatched = result {} else { XCTFail("Retired preparation cannot send: \(event)") }
            let requests = await f.peer.recorded()
            XCTAssertFalse(requests.contains { $0.operation == "sync.write" })
            try await f.close()
        }
    }

    func testReentrantDirectoryReadCannotReplaceTheRelationshipThatPassedWritePreflight() async throws {
        let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
        let directory = try await f.owner.readLibrarySubscriptions(session)
        let original = try XCTUnwrap(directory.subscriptions.first)
        let entered = expectation(description: "unsubscribe preparation after fresh preflight")
        let gate = MusicAccountTestGate { entered.fulfill() }
        defer { Task { await gate.release() } }
        await f.peer.hold("sync.prepare", gate: gate)
        let pending = Task { try await f.owner.writeLibrarySync(session, mutation: .init(action: .unsubscribe,
            collectionID: original.remoteID, directoryRevision: directory.revision, beforeSubscription: original)) }
        await fulfillment(of: [entered], timeout: 3)
        await f.peer.setRelationID("relation-from-another-read")
        let concurrent = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertEqual(concurrent.subscriptions.first?.relationID, "relation-from-another-read")
        await gate.release()
        let result = try await pending.value
        if case .acknowledged = result {} else { XCTFail("The write must settle from its own prepared facts") }
        let requests = await f.peer.recorded(), writes = requests.filter { $0.operation == "sync.write" }
        XCTAssertEqual(writes.count, 1)
        let write = try XCTUnwrap(writes.first)
        XCTAssertEqual(write.parameters[music: "remote"][music: "relationId"], .string(original.relationID))
        XCTAssertEqual(write.parameters[music: "remote"][music: "locator"][music: "listId"], .string("502"))
        XCTAssertEqual(requests.filter { $0.operation == "sync.read" }.count, 3,
            "Only the initial directory, this write's fresh preflight and the concurrent read are performed")
        XCTAssertEqual(requests.filter { $0.operation == "sync.prepare" }.count, 1)
        try await f.close()
    }

    func testDispatchedRuntimeWriteKeepsLateCanonicalReceiptsAndNeverReplaysUnknown() async throws {
        for status in ["confirmed", "rejected", "notSent", "unknown"] {
            let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
            let directory = try await f.owner.readLibrarySubscriptions(session), gate = MusicAccountTestGate()
            await f.peer.writeReceipt(.object(["status": .string(status), "errorCode": .string("UPSTREAM_HTTP")]))
            await f.peer.hold("sync.write", gate: gate)
            let pending = Task { try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                collectionID: "900", name: "远端", directoryRevision: directory.revision)) }
            await gate.started()
            pending.cancel()
            await gate.release()
            do {
                let result = try await pending.value
                switch result {
                case .acknowledged: XCTAssertEqual(status, "confirmed")
                case .rejected(let error): XCTAssertEqual(status, "rejected"); XCTAssertEqual(error, .upstreamFailed)
                case .notDispatched(let error):
                    XCTAssertEqual(status, "notSent", "Only an actual no-upstream-send receipt can prove no remote mutation")
                    XCTAssertEqual(error, .upstreamFailed)
                }
            } catch {
                XCTAssertEqual(status, "unknown")
                XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed)
            }
            let requests = await f.peer.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "sync.write" }.count, 1)
            try await f.close()
        }
    }
}
