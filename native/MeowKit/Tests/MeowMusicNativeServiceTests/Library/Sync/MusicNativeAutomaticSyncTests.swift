import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

// All accounts/documents are synthetic. The fixture transport rejects network.
@MainActor final class MusicNativeAutomaticSyncTests: XCTestCase {
    private func enable(_ service: NativeMusicService, favorites: Bool = false,
                        playlists: Bool = true) async throws -> MusicLibrarySyncState {
        let saved = try await service.automaticLibrarySyncPreferences()
        let result = try await service.saveAutomaticLibrarySync(
            .init(revision: saved.revision, selections: [.init(platform: .netease, favorites: favorites, playlists: playlists)]),
            requestID: UUID(), authorization: .init())
        return try await result.synchronization.value.get()
    }
    private func run(_ service: NativeMusicService, _ reason: MusicAutomaticLibrarySyncRequest.Reason = .localMutation) async throws -> MusicLibrarySyncState {
        try await service.synchronizeAutomaticLibrary(.init(reason: reason, authorization: .init()))
    }
    private func playlist(_ service: NativeMusicService, _ id: String) async throws -> MusicCollection {
        let rows = try await service.collections()
        return try XCTUnwrap(rows.first { $0.id == id })
    }

    func testLongOpaqueAccountIDSurvivesAutomaticBindingSyncAndCurrentColdRead() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let accountID = "opaque-" + String(repeating: "u", count: 2300) + "-" + platform.rawValue
            let scope = "synthetic-install:local-account"
            XCTAssertGreaterThan(accountID.utf8.count, 2048)
            let peer = NativeSyncPeer(platform: platform, rows: [NativeSyncFixture.collection()])
            await peer.switchAccount(uid: accountID, scope: scope)
            let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
            let initial = try await service.automaticLibrarySyncPreferences()
            let selected = MusicLibrarySyncSelection(platform: platform, playlists: true)
            let saved = try await service.saveAutomaticLibrarySync(
                .init(revision: initial.revision, selections: [selected]), requestID: UUID(), authorization: .init())
            _ = try await saved.synchronization.value.get()
            let document = try NativeSyncFixture.document(store), ledger = try MusicNativeSyncLedger.load(document.library)
            XCTAssertEqual(ledger.automatic.bindings[platform.rawValue]?.accountID, accountID)
            XCTAssertEqual(ledger.automatic.bindings[platform.rawValue]?.scope, scope)
            XCTAssertEqual(document.library["library"]["accounts"][platform.rawValue].string, accountID)
            let bindings = document.library["library"]["collections"].array.flatMap { $0["bindings"].array }
            XCTAssertEqual(bindings.count, 1); XCTAssertEqual(bindings.first?["accountId"].string, accountID)
            XCTAssertEqual(bindings.first?["platform"].string, platform.rawValue)
            XCTAssertEqual(bindings.first?["id"].string, "501")
            let beforeCold = await peer.counts()
            await service.close()
            let restored = try await NativeMusicFixture.service(store, accounts: peer)
            let preferences = try await restored.automaticLibrarySyncPreferences()
            XCTAssertEqual(preferences, saved.preferences)
            _ = try await run(restored, .foreground)
            let current = try NativeSyncFixture.document(store), coldLedger = try MusicNativeSyncLedger.load(current.library)
            XCTAssertEqual(coldLedger.automatic.bindings[platform.rawValue]?.accountID, accountID)
            XCTAssertEqual(current.library["library"]["accounts"][platform.rawValue].string, accountID)
            let coldBindings = current.library["library"]["collections"].array.flatMap { $0["bindings"].array }
            XCTAssertEqual(coldBindings.map { $0["accountId"].string }, [accountID])
            let afterCold = await peer.counts()
            XCTAssertEqual(afterCold.0, beforeCold.0 + 1); XCTAssertEqual(afterCold.1, beforeCold.1 + 1)
            await peer.switchAccount(uid: accountID + "-other", scope: scope)
            do { _ = try await run(restored, .foreground); XCTFail("A replacement account inherited the old automatic binding") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            let afterReplacement = try NativeSyncFixture.document(store)
            for field in ["accounts", "collections", "networkReferences", "songs"] {
                XCTAssertEqual(afterReplacement.library["library"][field], current.library["library"][field])
            }
            XCTAssertEqual(try MusicNativeSyncLedger.load(afterReplacement.library).automatic.bindings[platform.rawValue]?.accountID, accountID)
            let counts = await peer.counts(), mutations = await peer.calls()
            XCTAssertEqual(counts.1, afterCold.1); XCTAssertTrue(mutations.isEmpty)
            await restored.close()
        }
    }

    func testNewAndCurrentManualLedgerDefaultsNeverAuthorizeAutomaticNetwork() async throws {
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection()])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        let initial = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(initial, .init())
        _ = try await run(service, .foreground)
        let unopened = await peer.counts()
        XCTAssertEqual(unopened.0, 0); XCTAssertEqual(unopened.1, 0)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let manual = try NativeSyncFixture.document(store).library["sync"]["automatic"]
        XCTAssertTrue(manual.isObject); XCTAssertEqual(manual["selections"], .array([]))
        let before = await peer.counts()
        let defaults = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(defaults, .init())
        _ = try await run(service, .foreground)
        _ = try await run(service, .interval)
        let after = await peer.counts()
        XCTAssertEqual(before.0, after.0); XCTAssertEqual(before.1, after.1)
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testStoredLedgerWithoutAutomaticIsRejectedWithoutAccountCallsOrWrites() async throws {
        let valid = try NativeSyncFixture.store()
        var document = try NativeSyncFixture.document(valid), ledger = try MusicNativeSyncLedger().json()
        ledger.remove("automatic"); document.library["sync"] = ledger
        let bytes = try document.encode(changed: ["library"]), store = NativeMusicTestLibrary(bytes)
        let peer = NativeSyncPeer(), service = try await NativeMusicFixture.service(store, accounts: peer)
        do { _ = try await service.automaticLibrarySyncPreferences(); XCTFail("Missing current settings") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        do { _ = try await run(service, .foreground); XCTFail("Ledger missing automatic must not authorize a round") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        do { _ = try await service.synchronizeLibrary(NativeSyncFixture.request()); XCTFail("Ledger missing automatic must not authorize manual sync") }
        catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
        let counts = await peer.counts(), writes = await peer.calls()
        XCTAssertEqual(counts.0, 0); XCTAssertEqual(counts.1, 0); XCTAssertTrue(writes.isEmpty)
        await service.close()
    }

    func testSaveRunsOnceAndAllOffSurvivesColdRestoreWithoutAccountCalls() async throws {
        let a = NativeSyncFixture.member("101"), peer = NativeSyncPeer()
        let store = try NativeSyncFixture.store([a]), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Local", refs: try await NativeSyncFixture.refs([a], service: service))
        _ = try await enable(service)
        var writes = await peer.calls()
        XCTAssertEqual(writes.map(\.action), [.create, .add])
        _ = try await run(service) // Buffered event for the already settled edit.
        writes = await peer.calls(); XCTAssertEqual(writes.count, 2)
        let saved = try await service.automaticLibrarySyncPreferences(), before = await peer.counts()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: saved.revision, selections: []),
            requestID: UUID(), authorization: .init())
        let off = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(off.selections, []); XCTAssertEqual(off.revision, saved.revision + 1)
        await service.close()
        let restored = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await run(restored, .foreground)
        let after = await peer.counts()
        XCTAssertEqual(after.0, before.0); XCTAssertEqual(after.1, before.1)
        await restored.close()
    }

    func testLocalSaveReturnsWhileItsImmediateSynchronizationIsBlocked() async throws {
        let gate = NativeMusicGate(), peer = NativeSyncPeer()
        await peer.configure(gate: gate)
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        let permission = MusicQueueAuthorization(), returned = expectation(description: "local CAS receipt")
        let saving = Task {
            let receipt = try await service.saveAutomaticLibrarySync(
                .init(selections: [.init(platform: .netease, playlists: true)]),
                requestID: UUID(), authorization: permission)
            returned.fulfill()
            return receipt
        }
        await gate.started()
        await fulfillment(of: [returned], timeout: 2)
        let preferences = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(preferences.revision, 1); XCTAssertTrue(permission.isActive)
        XCTAssertEqual(preferences.selections, [.init(platform: .netease, playlists: true)])
        let counts = await peer.counts()
        XCTAssertEqual(counts.1, 1, "Saving still starts one real immediate synchronization")
        await gate.release()
        let receipt = try await saving.value
        let result = try await receipt.synchronization.value.get()
        XCTAssertEqual(result.phase, .synced); XCTAssertFalse(permission.isActive)
        await service.close()
    }

    func testNewLocalSavesSupersedeBlockedSettingsSyncWithoutQueuingOldIntents() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Local", refs: [])
        await peer.configure(gate: gate)
        let firstPermission = MusicQueueAuthorization()
        let first = try await service.saveAutomaticLibrarySync(
            .init(selections: [.init(platform: .netease, playlists: true)]),
            requestID: UUID(), authorization: firstPermission)
        await gate.started()
        let before = await peer.counts(), secondPermission = MusicQueueAuthorization()
        let second = try await service.saveAutomaticLibrarySync(
            .init(revision: first.preferences.revision, selections: [.init(platform: .netease, favorites: true)]),
            requestID: UUID(), authorization: secondPermission)
        let off = try await service.saveAutomaticLibrarySync(.init(revision: second.preferences.revision),
            requestID: UUID(), authorization: .init())
        let checkpoint = store.snapshot().0, waiting = await peer.counts()
        XCTAssertFalse(firstPermission.isActive); XCTAssertFalse(secondPermission.isActive)
        XCTAssertEqual(off.preferences.revision, 3); XCTAssertTrue(off.preferences.selections.isEmpty)
        XCTAssertEqual(waiting.0, before.0); XCTAssertEqual(waiting.1, before.1)
        await gate.release()
        if case .success = await first.synchronization.value { XCTFail("Retired round succeeded") }
        if case .success = await second.synchronization.value { XCTFail("Superseded intent ran later") }
        _ = try await off.synchronization.value.get()
        let after = await peer.counts(), writes = await peer.calls()
        XCTAssertEqual(after.0, before.0); XCTAssertEqual(after.1, before.1)
        XCTAssertTrue(writes.isEmpty); XCTAssertEqual(store.snapshot().0, checkpoint)
        await service.close()
    }

    func testCancellingReturnedSynchronizationRevokesDispatchAndDrainsItsLatePull() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Local", refs: [])
        await peer.configure(gate: gate)
        let permission = MusicQueueAuthorization()
        let receipt = try await service.saveAutomaticLibrarySync(
            .init(selections: [.init(platform: .netease, playlists: true)]),
            requestID: UUID(), authorization: permission)
        await gate.started()
        receipt.synchronization.cancel()
        XCTAssertFalse(permission.isActive)
        let checkpoint = store.snapshot().0
        await gate.release()
        if case .success = await receipt.synchronization.value { XCTFail("Cancelled business result") }
        let writes = await peer.calls()
        XCTAssertTrue(writes.isEmpty); XCTAssertEqual(store.snapshot().0, checkpoint)
        let preferences = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(preferences, receipt.preferences, "Business cancellation cannot roll back local settings")
        await service.close()
    }

    func testProtectionRevokesAcceptedSettingsSyncBeforeActorDrainCompletes() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        await peer.configure(gate: gate)
        let permission = MusicQueueAuthorization()
        let receipt = try await service.saveAutomaticLibrarySync(
            .init(selections: [.init(platform: .netease, playlists: true)]),
            requestID: UUID(), authorization: permission)
        await gate.started()
        let retiring = expectation(description: "scope retirement entered before its drain")
        let events = await service.automaticLibrarySyncEvents()
        let observation = Task { for await _ in events {}; retiring.fulfill() }
        service.revokeAccess(generation: 1)
        XCTAssertFalse(permission.isActive, "The synchronous protection fence cannot wait for actor reentry")
        var drained = false
        let transition = Task { try await service.setAccess(available: false, generation: 2); drained = true }
        let checkpoint = store.snapshot().0
        await fulfillment(of: [retiring], timeout: 2)
        XCTAssertFalse(drained)
        await gate.release()
        try await transition.value; await observation.value
        if case .success = await receipt.synchronization.value { XCTFail("Revoked scope succeeded") }
        XCTAssertTrue(drained); XCTAssertEqual(store.snapshot().0, checkpoint)
        let writes = await peer.calls(); XCTAssertTrue(writes.isEmpty)
        await service.close()
    }

    func testCloseCancelsAndDrainsSettingsAccountBindingBeforeAnyEngineRunExists() async throws {
        let store = try NativeSyncFixture.store(), peer = AutomaticFailurePeer(), gate = NativeMusicGate()
        await peer.blockBinding(gate)
        let service = try await NativeMusicFixture.service(store, accounts: peer), permission = MusicQueueAuthorization()
        let receipt = try await service.saveAutomaticLibrarySync(
            .init(selections: [.init(platform: .netease, playlists: true)]),
            requestID: UUID(), authorization: permission)
        await gate.started()
        let checkpoint = store.snapshot().0, revoked = expectation(description: "close revoked accepted business")
        let stream = permission.revocations()
        let observation = Task { for await _ in stream { revoked.fulfill(); return } }
        var closed = false
        let closing = Task { await service.close(); closed = true }
        await fulfillment(of: [revoked], timeout: 2)
        XCTAssertFalse(closed, "Cancellation is not proof the non-cooperative binding physically drained")
        await gate.release(); await closing.value; await observation.value
        if case .success = await receipt.synchronization.value { XCTFail("Closed binding succeeded") }
        let writes = await peer.calls()
        XCTAssertTrue(closed); XCTAssertTrue(writes.isEmpty); XCTAssertEqual(store.snapshot().0, checkpoint)
    }

    func testCreateMembersFavoritesPureOrderAndDeleteProduceFreshBoundPlans() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let service = try await NativeMusicFixture.service(NativeSyncFixture.store([a, b]), accounts: peer)
        let refs = try await NativeSyncFixture.refs([a, b], service: service)
        _ = try await enable(service, favorites: true)
        let created = try await service.createPlaylist(name: "Local", refs: [refs[0]])
        _ = try await run(service)
        var local = try await playlist(service, created.id)
        _ = try await service.editPlaylist(local, name: nil, refs: refs, order: nil)
        _ = try await run(service)
        local = try await playlist(service, created.id)
        let beforeOrder = await peer.calls()
        _ = try await service.editPlaylist(local, name: nil, refs: nil, order: refs.reversed().map(\.id))
        _ = try await run(service)
        let afterOrder = await peer.calls()
        XCTAssertEqual(afterOrder.dropFirst(beforeOrder.count).map(\.action), [.order])
        let favorites = try await playlist(service, "favorites")
        _ = try await service.editPlaylist(favorites, name: nil, refs: [refs[0]], order: nil)
        _ = try await run(service)
        local = try await playlist(service, created.id)
        try await service.removePlaylist(local)
        _ = try await run(service)
        let actions = await peer.calls().map(\.action)
        XCTAssertEqual(actions, [.create, .add, .add, .order, .add, .delete])
        await service.close()
    }

    func testAllOffFencesNonCooperativeOldPullBeforeItCanPush() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await enable(service)
        _ = try await service.createPlaylist(name: "Local", refs: [])
        let gate = NativeMusicGate(); await peer.configure(gate: gate)
        let task = Task { try await self.run(service) }; await gate.started()
        let preferences = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: preferences.revision),
            requestID: UUID(), authorization: .init())
        let checkpoint = store.snapshot().0
        await gate.release()
        do { _ = try await task.value; XCTFail("Cancelled round must not commit") } catch {}
        XCTAssertEqual(store.snapshot().0, checkpoint)
        let writes = await peer.calls(); XCTAssertTrue(writes.isEmpty)
        let saved = try await service.automaticLibrarySyncPreferences()
        XCTAssertTrue(saved.selections.isEmpty)
        await service.close()
    }

    func testLostCreateIsReadOnlyRecoveryAcrossIntervalAndRestartNotReplay() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Uncertain", refs: [])
        await peer.configure(fault: .lostCreateResponse)
        _ = try await enable(service)
        _ = try await run(service, .interval)
        var writes = await peer.calls(); XCTAssertEqual(writes.map(\.action), [.create])
        XCTAssertEqual(try NativeSyncFixture.document(store).library["sync"]["fences"].array.count, 1)
        await service.close()
        let restored = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await run(restored, .foreground)
        writes = await peer.calls(); XCTAssertEqual(writes.map(\.action), [.create])
        await restored.close()
    }

    func testUnknownIdentityAndOfflineSettingsRemainSavedButCannotBlindlyRetryPush() async throws {
        let peer = AutomaticFailurePeer(), store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Local", refs: [])
        await peer.configure(known: false, offline: true)
        do { _ = try await enable(service); XCTFail("Unknown account") } catch {}
        let saved = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(saved.selections, [.init(platform: .netease, playlists: true)])
        await peer.configure(known: true, offline: false)
        let before = await peer.beginCount()
        do { _ = try await run(service, .foreground); XCTFail("Timer must not bind a new account") } catch {}
        let after = await peer.beginCount()
        XCTAssertEqual(before, after)
        await peer.configure(known: true, offline: true)
        do { _ = try await run(service, .manual); XCTFail("Offline") } catch {}
        // A buffered old mutation is not an immediate retry of the failed run.
        _ = try await run(service)
        let writes = await peer.calls(); XCTAssertTrue(writes.isEmpty)
        let stillSaved = try await service.automaticLibrarySyncPreferences()
        XCTAssertEqual(stillSaved.selections, saved.selections)
        await service.close()
    }

    func testPureOrderRefusesChangedRemoteMembershipBeforeAnyWrite() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102"), c = NativeSyncFixture.member("103")
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(members: [a, b])])
        let service = try await NativeMusicFixture.service(NativeSyncFixture.store(), accounts: peer)
        _ = try await enable(service)
        let rows = try await service.collections(), local = try XCTUnwrap(rows.first { $0.kind == "playlist" })
        let tracks = try await service.tracks(in: local)
        _ = try await service.editPlaylist(local, name: nil, refs: nil, order: tracks.reversed().map(\.ref.id))
        await peer.replace([NativeSyncFixture.collection(members: [a, b, c], revision: "2")])
        do { _ = try await run(service); XCTFail("Membership changed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testPureOrderRejectsAmbiguousTargetOrBrokenConsumedMapBeforeAnyWrite() async throws {
        for duplicate in [false, true] {
            let members = [NativeSyncFixture.member("101"), NativeSyncFixture.member("102")]
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(members: members)])
            let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
            _ = try await enable(service)
            let rows = try await service.collections(), local = try XCTUnwrap(rows.first { $0.kind == "playlist" })
            let tracks = try await service.tracks(in: local)
            _ = try await service.editPlaylist(local, name: nil, refs: nil, order: tracks.reversed().map(\.ref.id))
            var document = try NativeSyncFixture.document(store)
            var collections = document.library["library"]["collections"].array
            let index = try XCTUnwrap(collections.firstIndex { $0["id"].string == local.id })
            var bindings = collections[index]["bindings"].array
            if duplicate { bindings.append(bindings[0]) }
            else { bindings[0]["map"] = .obj() }
            collections[index]["bindings"] = .array(bindings)
            document.library["library"]["collections"] = .array(collections)
            store.replace(try document.encode(changed: ["library"]))
            do { _ = try await run(service); XCTFail("Unsafe order target was accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
            await service.close()
        }
    }
}

private actor AutomaticFailurePeer: MusicNativeAccountProviding, MusicNativeLibrarySyncProviding {
    private let base = NativeSyncPeer()
    private var known = true, offline = false, begins = 0
    private var bindingGate: NativeMusicGate?
    func blockBinding(_ gate: NativeMusicGate) { bindingGate = gate }
    func configure(known: Bool, offline: Bool) { self.known = known; self.offline = offline }
    func beginCount() -> Int { begins }
    func calls() async -> [MusicLibrarySyncMutation] { await base.calls() }
    func beginLibrarySync(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        begins += 1
        if let bindingGate { await bindingGate.wait() }
        guard known else { throw MusicServiceError.authRequired }
        return try await base.beginLibrarySync(platform: platform)
    }
    func checkLibrarySync(_ session: MusicLibrarySyncSession) async throws { try await base.checkLibrarySync(session) }
    func endLibrarySync(_ session: MusicLibrarySyncSession) async { await base.endLibrarySync(session) }
    func readLibrarySync(_ session: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        guard !offline else { throw MusicServiceError.upstreamFailed }
        return try await base.readLibrarySync(session, kinds: kinds)
    }
    func readLibrarySubscriptions(_ session: MusicLibrarySyncSession) async throws -> MusicLibrarySyncSnapshot {
        guard !offline else { throw MusicServiceError.upstreamFailed }
        return try await base.readLibrarySubscriptions(session)
    }
    func writeLibrarySync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        try await base.writeLibrarySync(session, mutation: mutation)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unavailable }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unavailable }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unavailable }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] { throw MusicServiceError.unavailable }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unavailable }
}
