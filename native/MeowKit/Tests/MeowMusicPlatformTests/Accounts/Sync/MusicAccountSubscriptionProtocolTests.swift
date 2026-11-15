import Foundation
import XCTest
import MeowMusicFeature
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicAccountSubscriptionProtocolTests: XCTestCase {
    func testCanonicalDirectoryKeepsOwnedSpecialListsAndNeverReadsMembersForSubscriptions() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try MusicAccountSyncRuntimeFixture(platform: platform), session = try await f.begin()
            let full = try await f.owner.readLibrarySync(session, kinds: [.playlist])
            let dedicated = try await f.owner.readLibrarySubscriptions(session)
            XCTAssertEqual(full.collections.map(\.id), ["501"])
            XCTAssertEqual(dedicated.subscriptions, full.subscriptions)
            XCTAssertTrue(dedicated.collections.isEmpty)
            XCTAssertEqual(Set(dedicated.ownedCollectionIDs), ["501", "503"])
            XCTAssertEqual(dedicated.revision, full.revision)
            let requests = await f.peer.recorded()
            let reads = requests.filter { $0.operation == "sync.read" }
            XCTAssertEqual(reads.count, 2)
            XCTAssertEqual(reads.last?.parameters[music: "subscriptionsOnly"], .bool(true))
            XCTAssertFalse(requests.contains { ["sync.prepare", "sync.target", "sync.write", "playlist.complete"].contains($0.operation) })
            try await f.close()
        }
    }

    func testCurrentCanonicalRelationshipAndLocatorReachWritesWithoutReinterpretingPlatformIDs() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try MusicAccountSyncRuntimeFixture(platform: platform), session = try await f.begin()
            let snapshot = try await f.owner.readLibrarySubscriptions(session)
            let before = try XCTUnwrap(snapshot.subscriptions.first)
            let subscribed = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                collectionID: "900", name: String(repeating: "T", count: 201), directoryRevision: snapshot.revision))
            let unsubscribed = try await f.owner.writeLibrarySync(session, mutation: .init(action: .unsubscribe,
                collectionID: before.remoteID, directoryRevision: snapshot.revision, beforeSubscription: before))
            if case .acknowledged = subscribed {} else { XCTFail("A valid remote relationship must dispatch") }
            if case .acknowledged = unsubscribed {} else { XCTFail("The account relationship must dispatch") }
            let requests = await f.peer.recorded(), writes = requests.filter { $0.operation == "sync.write" }
            XCTAssertEqual(writes.map { $0.parameters[music: "action"] }, [.string("subscribe"), .string("unsubscribe")])
            XCTAssertEqual(writes.map { $0.parameters[music: "id"] }, [.string("900"), .string("502")])
            XCTAssertEqual(writes.first?.parameters[music: "remote"][music: "locator"][music: "creatorId"], .string("202"))
            XCTAssertEqual(writes.last?.parameters[music: "remote"][music: "relationId"], .string("relation-502"))
            XCTAssertEqual(writes.last?.parameters[music: "remote"][music: "locator"][music: "listId"], .string("502"))
            XCTAssertTrue(requests.filter { $0.operation == "sync.read" }.allSatisfy {
                $0.parameters[music: "subscriptionsOnly"] == .bool(true)
            })
            XCTAssertEqual(requests.filter { $0.operation == "sync.prepare" }.count, platform == .netease ? 2 : 0)
            try await f.close()
        }
    }

    func testOwnedWrongRelationshipAndTargetFailureDoNotDispatchOrAcknowledge() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try MusicAccountSyncRuntimeFixture(platform: platform), session = try await f.begin()
            let snapshot = try await f.owner.readLibrarySubscriptions(session)
            for mutation: MusicLibrarySyncMutation in [
                .init(action: .subscribe, collectionID: "501", name: "自有", directoryRevision: snapshot.revision),
                .init(action: .subscribe, collectionID: "503", name: "自有特殊列表", directoryRevision: snapshot.revision),
                .init(action: .unsubscribe, collectionID: "502", directoryRevision: snapshot.revision,
                    beforeSubscription: .init(remoteID: "502", relationID: "author-not-account", name: "错误关系"))
            ] {
                let result = try await f.owner.writeLibrarySync(session, mutation: mutation)
                if case .notDispatched(let error) = result { XCTAssertEqual(error, .revisionConflict) }
                else { XCTFail("A wrong local write proof cannot dispatch") }
            }
            await f.peer.fail("sync.target", code: "INVALID_RESPONSE")
            let failedTarget = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                collectionID: "900", name: "真实目标", directoryRevision: snapshot.revision))
            if case .notDispatched(let error) = failedTarget { XCTAssertEqual(error, .upstreamInvalid) }
            else { XCTFail("Missing required target material cannot acknowledge a write") }
            let requests = await f.peer.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "sync.target" }.count, 1)
            XCTAssertFalse(requests.contains { $0.operation == "sync.prepare" || $0.operation == "sync.write" })
            try await f.close()
        }
    }

    func testExplicitRemoteRejectionNeverBecomesSuccessAndIsNotAutomaticallyRetried() async throws {
        let f = try MusicAccountSyncRuntimeFixture(platform: .qq), session = try await f.begin()
        let snapshot = try await f.owner.readLibrarySubscriptions(session)
        await f.peer.writeReceipt(.object(["status": .string("rejected"), "errorCode": .string("UPSTREAM_ERROR")]))
        let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
            collectionID: "900", name: "远端", directoryRevision: snapshot.revision))
        if case .rejected(let error) = result { XCTAssertEqual(error, .requestRejected) }
        else { XCTFail("An actual rejected target is not an acknowledged subscription") }
        let requests = await f.peer.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "sync.write" }.count, 1)
        try await f.close()
    }

    func testDirectoryReadbackUsesCurrentFactsAndSpecialChangesDoNotCreateASecondReadGate() async throws {
        let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
        let original = try await f.owner.readLibrarySubscriptions(session)
        await f.peer.renameSpecial("更新后的特殊列表")
        let updated = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertEqual(updated.collections, original.collections)
        XCTAssertEqual(updated.subscriptions, original.subscriptions)
        XCTAssertNotEqual(updated.revision, original.revision)
        await f.peer.setSubscribed(false)
        let removed = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertTrue(removed.subscriptions.isEmpty)
        await f.peer.setSubscribed(true)
        let restored = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertEqual(restored.subscriptions, updated.subscriptions)
        let requests = await f.peer.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "sync.read" }.count, 4)
        XCTAssertFalse(requests.contains { $0.operation == "sync.prepare" || $0.operation == "sync.write" })
        try await f.close()
    }
}
