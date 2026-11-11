import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor final class MusicNativeSubscriptionSyncTests: XCTestCase {
    private func enable(_ service: NativeMusicService, platforms: [MusicAccountPlatform] = [.netease]) async throws -> MusicLibrarySyncState {
        let saved = try await service.automaticLibrarySyncPreferences()
        return try await service.saveAutomaticLibrarySync(.init(revision: saved.revision,
            selections: platforms.map { .init(platform: $0, playlists: true) }), requestID: UUID(), authorization: .init())
    }
    private func run(_ service: NativeMusicService, reason: MusicAutomaticLibrarySyncRequest.Reason = .localMutation) async throws -> MusicLibrarySyncState {
        try await service.synchronizeAutomaticLibrary(.init(reason: reason, authorization: .init()))
    }
    private func references(_ service: NativeMusicService) async throws -> [MusicCollection] {
        try await service.collections().filter { $0.networkReference != nil }
    }

    func testFirstEnableImportsOfficialRelationshipsAndExternalChangesWithoutUnsubscribingAnEmptyLibrary() async throws {
        let peer = NativeSyncPeer(subscriptions: [.init(remoteID: "501", relationID: "501", name: "Remote", count: 8)])
        let service = try await NativeMusicFixture.service(accounts: peer)
        _ = try await enable(service)
        var saved = try await references(service), calls = await peer.calls()
        XCTAssertEqual(saved.map(\.title), ["Remote"]); XCTAssertEqual(saved.first?.count, 8); XCTAssertTrue(calls.isEmpty)
        await peer.replaceSubscriptions([.init(remoteID: "501", relationID: "501", name: "Updated", count: 9)])
        _ = try await run(service, reason: .interval)
        saved = try await references(service)
        XCTAssertEqual(saved.map(\.title), ["Updated"]); XCTAssertEqual(saved.first?.count, 9)
        await peer.replaceSubscriptions([])
        _ = try await run(service, reason: .interval)
        saved = try await references(service); calls = await peer.calls()
        XCTAssertTrue(saved.isEmpty); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testSameNameOwnedPlaylistAndSubscriptionsNeverShareMembersOrBindings() async throws {
        let owned = NativeSyncFixture.collection("501", name: "Same", members: [NativeSyncFixture.member("101")])
        let peer = NativeSyncPeer(rows: [owned], subscriptions: [
            .init(remoteID: "502", relationID: "502", name: "Same"), .init(remoteID: "503", relationID: "503", name: "Same")
        ])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let collections = try await service.collections()
        let managed = try XCTUnwrap(collections.first { $0.kind == "playlist" })
        let members = try await service.tracks(in: managed), network = collections.filter { $0.networkReference != nil }
        XCTAssertEqual(members.count, 1); XCTAssertEqual(network.count, 2)
        XCTAssertTrue(managed.canEdit); XCTAssertTrue(network.allSatisfy { !$0.canEdit })
        let library = try NativeSyncFixture.document(store).library["library"]
        XCTAssertEqual(library["songs"].fields.count, 1)
        XCTAssertEqual(library["collections"].array.filter { $0["kind"].string == "playlist" }.count, 1)
        XCTAssertEqual(Set(network.compactMap { $0.networkReference?.remoteID }), ["502", "503"])
        let calls = await peer.calls()
        XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testNetworkMutationReadsOnlyItsPlatformDirectoryAndNeverRunsOtherContentSync() async throws {
        let wy = NativeSyncPeer(), tx = NativeSyncPeer(platform: .qq)
        let peers = MultiPlatformSyncPeer([.netease: wy, .qq: tx])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peers)
        _ = try await enable(service, platforms: [.netease, .qq])
        await wy.replace([NativeSyncFixture.collection("701", name: "Unrelated WY", members: [NativeSyncFixture.member("101")])])
        await tx.replace([NativeSyncFixture.collection("702", name: "Unrelated QQ", members: [NativeSyncFixture.member("B", platform: .qq)])])
        let wyBefore = await wy.counts(), txBefore = await tx.counts()
        _ = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "900", title: "Network"), intent: .init())
        let result = try await run(service)
        XCTAssertEqual(result.phase, .synced)
        let wyAfter = await wy.counts(), txAfter = await tx.counts(), directoryReads = await tx.relationshipReadCount()
        XCTAssertEqual(wyAfter.0, wyBefore.0); XCTAssertEqual(wyAfter.1, wyBefore.1)
        XCTAssertEqual(txAfter.1, txBefore.1); XCTAssertEqual(directoryReads, 2)
        let wyWrites = await wy.calls(), txWrites = await tx.calls()
        XCTAssertTrue(wyWrites.isEmpty); XCTAssertEqual(txWrites.map(\.action), [.subscribe])
        XCTAssertEqual(txWrites.first?.collectionID, "900"); XCTAssertTrue(txWrites.first?.members.isEmpty == true)
        let library = try NativeSyncFixture.document(store).library["library"]
        XCTAssertEqual(library["collections"].array.count, 1); XCTAssertTrue(library["songs"].fields.isEmpty)
        await service.close()
    }

    func testClosedPlatformSwitchOnlyChangesLocalReferenceThenEnableUsesExplicitCancelIntent() async throws {
        let peer = NativeSyncPeer(subscriptions: [.init(remoteID: "501", relationID: "501", name: "Remote")])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        let values = try await references(service), saved = try XCTUnwrap(values.first)
        let preferences = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: preferences.revision), requestID: UUID(), authorization: .init())
        let before = await peer.counts()
        try await service.removeNetworkPlaylist(saved, intent: .init())
        _ = try await run(service, reason: .foreground)
        let after = await peer.counts(), noWrites = await peer.calls(), hidden = try await references(service)
        XCTAssertEqual(before.0, after.0); XCTAssertEqual(before.1, after.1); XCTAssertTrue(noWrites.isEmpty); XCTAssertTrue(hidden.isEmpty)
        _ = try await enable(service)
        let calls = await peer.calls(), remote = await peer.subscriptionContents()
        XCTAssertEqual(calls.map(\.action), [.unsubscribe]); XCTAssertTrue(remote.isEmpty)
        XCTAssertFalse(calls.contains { $0.action == .delete })
        XCTAssertEqual(calls.first?.beforeSubscription?.relationID, "501")
        await service.close()
    }

    func testLocalReferencesBeforeFirstEnableAreMergedInsteadOfDroppedOrCopied() async throws {
        let peer = NativeSyncPeer(subscriptions: [.init(remoteID: "502", relationID: "502", name: "Official")])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Local reference"), intent: .init())
        _ = try await run(service, reason: .foreground)
        let unopened = await peer.counts()
        XCTAssertEqual(unopened.0, 0); XCTAssertEqual(unopened.1, 0)
        _ = try await enable(service)
        let saved = try await references(service), calls = await peer.calls()
        XCTAssertEqual(Set(saved.compactMap { $0.networkReference?.remoteID }), ["501", "502"])
        XCTAssertEqual(calls.map(\.action), [.subscribe]); XCTAssertEqual(calls.first?.collectionID, "501")
        let library = try NativeSyncFixture.document(store).library["library"]
        XCTAssertTrue(library["songs"].fields.isEmpty); XCTAssertEqual(library["collections"].array.count, 1)
        await service.close()
    }

    func testUnboundCancellationDoesNotUnsubscribeFirstAccountOrReviveOldHandle() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let peer = NativeSyncPeer(platform: platform, subscriptions: [
                .init(remoteID: "501", relationID: platform == .kugou ? "1:501" : "501", name: "Remote")
            ])
            let store = try NativeSyncFixture.store()
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            let saved = try await service.saveNetworkPlaylist(
                .init(platform: .init(accountPlatform: platform), nativeID: "501", title: "Remote"), intent: .init())
            try await service.removeNetworkPlaylist(saved, intent: .init())
            await service.close()
            let reopened = try await NativeMusicFixture.service(store, accounts: peer)
            _ = try await enable(reopened, platforms: [platform])
            let imported = try await references(reopened)
            XCTAssertEqual(imported.count, 1)
            let current = try XCTUnwrap(imported.first)
            XCTAssertEqual(current.id, saved.id); XCTAssertGreaterThan(current.revision, saved.revision)
            let checkpoint = store.snapshot()
            do { try await reopened.removeNetworkPlaylist(saved, intent: .init()); XCTFail("old unbound handle") }
            catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
            XCTAssertEqual(store.snapshot().0, checkpoint.0); XCTAssertEqual(store.snapshot().2, checkpoint.2)
            let writes = await peer.calls(), remote = await peer.subscriptionContents()
            XCTAssertTrue(writes.isEmpty); XCTAssertEqual(remote.map(\.remoteID), ["501"])
            await reopened.close()
        }
    }

    func testTimeoutAfterAppliedSubscribeAndUnsubscribeIsConfirmedByReadbackNotReplay() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        await peer.configure(fault: .appliedThenThrow)
        let subscribed = try await run(service)
        XCTAssertEqual(subscribed.phase, .synced)
        let values = try await references(service), saved = try XCTUnwrap(values.first)
        try await service.removeNetworkPlaylist(saved, intent: .init())
        let unsubscribed = try await run(service)
        XCTAssertEqual(unsubscribed.phase, .synced)
        let calls = await peer.calls(), ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(calls.map(\.action), [.subscribe, .unsubscribe]); XCTAssertTrue(ledger.fences.isEmpty)
        XCTAssertFalse(calls.contains { $0.action == .delete })
        await service.close()
    }

    func testUnknownSubscriptionResultSurvivesRestartAndOnlyFreshReadCanSettleIt() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        await peer.configure(fault: .acknowledgedWithoutChange)
        let unknown = try await run(service)
        XCTAssertEqual(unknown.phase, .partial); XCTAssertTrue(unknown.issues.contains { $0.code == "unconfirmed" })
        await service.close()
        await peer.configure()
        let reopened = try await NativeMusicFixture.service(store, accounts: peer)
        let blocked = try await run(reopened, reason: .foreground)
        XCTAssertEqual(blocked.phase, .blocked)
        var calls = await peer.calls()
        XCTAssertEqual(calls.map(\.action), [.subscribe])
        await peer.replaceSubscriptions([.init(remoteID: "501", relationID: "501", name: "Remote")])
        let settled = try await run(reopened, reason: .interval)
        XCTAssertEqual(settled.phase, .synced)
        calls = await peer.calls()
        XCTAssertEqual(calls.map(\.action), [.subscribe])
        let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertTrue(ledger.fences.isEmpty)
        await reopened.close()
    }

    func testNotDispatchedFailureDoesNotPretendSubscriptionSucceededAndFreshRunCanRetry() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        let local = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        await peer.configure(fault: .notDispatched)
        let failed = try await run(service)
        XCTAssertEqual(failed.phase, .partial)
        let visible = try await references(service), before = await peer.calls()
        XCTAssertTrue(visible.contains { $0.id == local.id }); XCTAssertTrue(before.isEmpty)
        XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library).fences.isEmpty)
        await peer.configure()
        let retried = try await run(service, reason: .interval), calls = await peer.calls()
        XCTAssertEqual(retried.phase, .synced); XCTAssertEqual(calls.map(\.action), [.subscribe])
        await service.close()
    }

    func testFailedLibraryCASPreventsAnySubscriptionDispatchAndPreservesPendingIdentity() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        await peer.configure(failDocument: store)
        do { _ = try await service.synchronizeLibrary(NativeSyncFixture.request()); XCTFail("failed CAS") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        let calls = await peer.calls(), reference = try NativeSyncFixture.document(store).library["library"]["networkReferences"].array.first!
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(reference["remoteID"].string, "501"); XCTAssertTrue(reference["pending"].bool)
        store.fail(nil); await peer.configure()
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let retried = await peer.calls()
        XCTAssertEqual(retried.map(\.action), [.subscribe])
        await service.close()
    }

    func testClosingSyncFencesAlreadyDispatchedLateSubscriptionReply() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        let gate = NativeMusicGate(); await peer.configure(writeGate: gate)
        let task = Task { try await self.run(service) }
        await gate.started()
        let preferences = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: preferences.revision), requestID: UUID(), authorization: .init())
        let checkpoint = store.snapshot().0
        await gate.release()
        do { _ = try await task.value; XCTFail("obsolete reply") } catch {}
        XCTAssertEqual(store.snapshot().0, checkpoint)
        let calls = await peer.calls(), ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(calls.map(\.action), [.subscribe]); XCTAssertEqual(ledger.fences.count, 1)
        XCTAssertTrue(ledger.automatic.selections.isEmpty)
        await service.close()
    }

    func testAccountSwitchRejectsOldDirectoryReplyBeforeAnyRelationshipWrite() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        let gate = NativeMusicGate(); await peer.configure(gate: gate)
        let task = Task { try await self.run(service) }
        await gate.started(); await peer.switchAccount()
        let before = try NativeSyncFixture.document(store).library["library"]["networkReferences"]
        await gate.release()
        do { _ = try await task.value; XCTFail("old account directory") } catch {}
        let calls = await peer.calls()
        XCTAssertTrue(calls.isEmpty)
        let library = try NativeSyncFixture.document(store).library["library"]
        XCTAssertEqual(library["accounts"]["wy"].string, "42")
        XCTAssertEqual(library["networkReferences"].array.first?["relation"]["accountId"].string ?? "", "")
        XCTAssertEqual(library["networkReferences"], before)
        await service.close()
    }

    func testUnrelatedEnabledPlatformIsNotTriggeredByALocalOnlyReference() async throws {
        let peer = NativeSyncPeer(), service = try await NativeMusicFixture.service(accounts: peer)
        _ = try await enable(service)
        let before = await peer.counts()
        _ = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "Local QQ"), intent: .init())
        _ = try await run(service)
        let after = await peer.counts(), calls = await peer.calls()
        XCTAssertEqual(before.0, after.0); XCTAssertEqual(before.1, after.1); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testFavoritesOnlySwitchDoesNotAuthorizeNetworkSubscriptionOrCancellation() async throws {
        let favorite = NativeSyncFixture.collection("301", name: "Favorites", kind: .favorites)
        let peer = NativeSyncPeer(rows: [favorite]), service = try await NativeMusicFixture.service(accounts: peer)
        let preferences = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: preferences.revision,
            selections: [.init(platform: .netease, favorites: true)]), requestID: UUID(), authorization: .init())
        let before = await peer.counts()
        let saved = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Local"), intent: .init())
        _ = try await run(service)
        try await service.removeNetworkPlaylist(saved, intent: .init())
        _ = try await run(service)
        let after = await peer.counts(), calls = await peer.calls(), visible = try await references(service)
        XCTAssertEqual(after.0, before.0); XCTAssertEqual(after.1, before.1)
        XCTAssertTrue(calls.isEmpty); XCTAssertTrue(visible.isEmpty)
        await service.close()
    }

    func testCoalescedNetworkIntentsStillUseOnlyDirectoryReadsOnEachBelongingPlatform() async throws {
        let wy = NativeSyncPeer(), tx = NativeSyncPeer(platform: .qq)
        let peers = MultiPlatformSyncPeer([.netease: wy, .qq: tx])
        let service = try await NativeMusicFixture.service(accounts: peers)
        _ = try await enable(service, platforms: [.netease, .qq])
        let wyBefore = await wy.counts(), txBefore = await tx.counts()
        _ = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "Same"), intent: .init())
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Same"), intent: .init())
        _ = try await run(service)
        let wyAfter = await wy.counts(), txAfter = await tx.counts()
        let wyReads = await wy.relationshipReadCount(), txReads = await tx.relationshipReadCount()
        XCTAssertEqual(wyAfter.1, wyBefore.1); XCTAssertEqual(txAfter.1, txBefore.1)
        XCTAssertEqual(wyReads, 2); XCTAssertEqual(txReads, 2)
        let wyWrites = await wy.calls(), txWrites = await tx.calls(), saved = try await references(service)
        XCTAssertEqual(wyWrites.map(\.action), [.subscribe]); XCTAssertEqual(txWrites.map(\.action), [.subscribe])
        XCTAssertEqual(wyWrites.first?.collectionID, "501"); XCTAssertEqual(txWrites.first?.collectionID, "501")
        XCTAssertEqual(saved.count, 2)
        await service.close()
    }

    func testMixedManagedAndNetworkIntentsPreserveContentSyncWithoutCopyingTheReference() async throws {
        for networkFirst in [false, true] {
            let wy = NativeSyncPeer(), tx = NativeSyncPeer(platform: .qq)
            let peers = MultiPlatformSyncPeer([.netease: wy, .qq: tx])
            let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peers)
            _ = try await enable(service, platforms: [.netease, .qq])
            let reference = MusicPublicBrowseCollection(platform: .qq, nativeID: "900", title: "Network")
            if networkFirst { _ = try await service.saveNetworkPlaylist(reference, intent: .init()) }
            let managed = try await service.createPlaylist(name: "Managed", refs: [])
            if !networkFirst { _ = try await service.saveNetworkPlaylist(reference, intent: .init()) }
            let result = try await run(service), wyWrites = await wy.calls(), txWrites = await tx.calls()
            XCTAssertEqual(result.phase, .synced)
            XCTAssertEqual(wyWrites.map(\.action), [.create])
            XCTAssertEqual(txWrites.map(\.action), [.create, .subscribe])
            XCTAssertEqual(wyWrites.first?.name, "Managed"); XCTAssertEqual(txWrites.first?.name, "Managed")
            XCTAssertEqual(txWrites.last?.collectionID, "900"); XCTAssertTrue(txWrites.last?.members.isEmpty == true)
            let library = try NativeSyncFixture.document(store).library["library"]
            XCTAssertEqual(library["collections"].array.count, 2); XCTAssertTrue(library["songs"].fields.isEmpty)
            XCTAssertEqual(library["collections"].array.first { $0["id"].string == managed.id }?["bindings"].array.count, 2)
            XCTAssertEqual(library["networkReferences"].array.count, 1)
            XCTAssertEqual(library["networkReferences"].array.first?["remoteID"].string, "900")
            await service.close()
        }
    }

    func testFailedReadbackKeepsFenceAndRestoredRoundConfirmsWithoutAnotherWrite() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        await peer.configure(fault: .failedReadback)
        do { _ = try await run(service); XCTFail("readback failure") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library).fences.count, 1)
        await service.close()
        let reopened = try await NativeMusicFixture.service(store, accounts: peer)
        let recovered = try await run(reopened, reason: .foreground), calls = await peer.calls()
        XCTAssertEqual(recovered.phase, .synced); XCTAssertEqual(calls.map(\.action), [.subscribe])
        XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library).fences.isEmpty)
        await reopened.close()
    }

    func testAccountSwitchCannotConfirmLateWriteIntoAnotherAccountBaseline() async throws {
        let peer = NativeSyncPeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Remote"), intent: .init())
        let gate = NativeMusicGate(); await peer.configure(writeGate: gate)
        let task = Task { try await self.run(service) }
        await gate.started(); await peer.switchAccount(); await gate.release()
        do { _ = try await task.value; XCTFail("old account acknowledgement") } catch {}
        let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        let reference = try NativeSyncFixture.document(store).library["library"]["networkReferences"].array.first!
        XCTAssertEqual(ledger.fences.count, 1); XCTAssertEqual(ledger.fences.first?.accountID, "42")
        XCTAssertEqual(reference["relation"]["accountId"].string, "42")
        XCTAssertFalse(reference["relation"]["baseline"].bool); XCTAssertTrue(reference["pending"].bool)
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.action), [.subscribe])
        await service.close()
    }
}
