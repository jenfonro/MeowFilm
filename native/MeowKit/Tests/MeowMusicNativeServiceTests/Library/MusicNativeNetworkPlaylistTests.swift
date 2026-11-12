import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeNetworkPlaylistTests: XCTestCase {
    func testIdentityUsesPlatformAndRemoteIDNotTitleAndSurvivesReopening() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let a = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "Same"), intent: .init())
        let b = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "502", title: "Same"), intent: .init())
        let c = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "501", title: "Same"), intent: .init())
        XCTAssertEqual(Set([a.id, b.id, c.id]).count, 3)
        let before = store.snapshot()
        let duplicate = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "New metadata"), intent: .init())
        XCTAssertEqual(duplicate, a); XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await service.close()
        let reopened = try await NativeMusicFixture.service(store)
        let collections = try await reopened.collections(), references = collections.filter { $0.networkReference != nil }
        XCTAssertEqual(references, [a, b, c])
        XCTAssertEqual(collections.filter(\.canEdit).map(\.id), ["favorites"])
        XCTAssertEqual(references.compactMap(\.remoteCollection).map(\.nativeID), ["501", "502", "501"])
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await reopened.close()
    }

    func testNetworkReferenceRejectsMembersRenameOrderAndRemoteDeletion() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let reference = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "Remote"), intent: .init())
        let before = store.snapshot()
        do { _ = try await service.tracks(in: reference); XCTFail("local members") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        for mutation in 0..<3 {
            do {
                _ = try await service.editPlaylist(reference, name: mutation == 0 ? "Renamed" : nil,
                    refs: mutation == 1 ? [] : nil, order: mutation == 2 ? [] : nil)
                XCTFail("network content mutation")
            } catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        }
        do { try await service.removePlaylist(reference); XCTFail("author's playlist must not be deleted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await service.close()
    }

    func testLocalCancellationUsesReferenceRevisionAndCASAndCanBeSavedAgain() async throws {
        for platform in [MusicPublicBrowsePlatform.netease, .qq, .kugou] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let collection = MusicPublicBrowseCollection(platform: platform, nativeID: "501", title: "Remote")
            let saved = try await service.saveNetworkPlaylist(collection, intent: .init())
            let before = store.snapshot()
            let stale = MusicCollection(id: saved.id, title: saved.title, kind: saved.kind, revision: saved.revision + 1,
                                        networkReference: saved.networkReference)
            do { try await service.removeNetworkPlaylist(stale, intent: .init()); XCTFail("stale cancellation") }
            catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
            store.fail(.storageConflict)
            do { try await service.removeNetworkPlaylist(saved, intent: .init()); XCTFail("failed CAS") }
            catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            store.fail(nil)
            try await service.removeNetworkPlaylist(saved, intent: .init())
            let cancelled = try await service.collections()
            XCTAssertFalse(cancelled.contains { $0.id == saved.id })
            XCTAssertTrue(try NativeSyncFixture.document(store).library["library"]["networkReferences"].array.isEmpty)
            let restored = try await service.saveNetworkPlaylist(collection, intent: .init())
            XCTAssertEqual(restored.id, saved.id); XCTAssertGreaterThan(restored.revision, saved.revision)
            await service.close()
            let reopened = try await NativeMusicFixture.service(store)
            let checkpoint = store.snapshot()
            do { try await reopened.removeNetworkPlaylist(saved, intent: .init()); XCTFail("old handle after resaving") }
            catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
            XCTAssertEqual(store.snapshot().0, checkpoint.0); XCTAssertEqual(store.snapshot().2, checkpoint.2)
            let current = try NativeSyncFixture.document(store).library["library"]
            XCTAssertEqual(current["networkReferences"].array.count, 1)
            XCTAssertEqual(current["collections"].array.count, 1); XCTAssertTrue(current["songs"].fields.isEmpty)
            let visible = try await reopened.collections().filter { $0.networkReference != nil }
            XCTAssertEqual(visible, [restored])
            await reopened.close()
        }
    }

    func testKuwoAndMiguRemainLocalAcrossSaveCancelAndResave() async throws {
        let store = NativeMusicMemoryDocument(), accounts = NativeMusicAccountFixture()
        let service = try await NativeMusicFixture.service(store, accounts: accounts)
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let collection = MusicPublicBrowseCollection(platform: platform, nativeID: "501", title: platform.title)
            let saved = try await service.saveNetworkPlaylist(collection, intent: .init())
            try await service.removeNetworkPlaylist(saved, intent: .init())
            _ = try await service.saveNetworkPlaylist(collection, intent: .init())
        }
        let document = try NativeSyncFixture.document(store)
        let references = document.library["library"]["networkReferences"].array
        XCTAssertEqual(references.count, 2)
        XCTAssertTrue(references.allSatisfy { $0["present"].bool && !$0["pending"].bool && $0["relation"] == .null })
        let calls = await accounts.events()
        XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testKuwoAndMiguCancellationReleasesReferencesAcrossReopening() async throws {
        let store = NativeMusicMemoryDocument(), accounts = NativeMusicAccountFixture()
        var service = try await NativeMusicFixture.service(store, accounts: accounts)
        for nativeID in ["501", "502"] {
            var saved: [MusicCollection] = []
            for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
                let reference = try await service.saveNetworkPlaylist(
                    .init(platform: platform, nativeID: nativeID, title: "Same"), intent: .init())
                let before = store.snapshot()
                let duplicate = try await service.saveNetworkPlaylist(
                    .init(platform: platform, nativeID: nativeID, title: "New metadata"), intent: .init())
                XCTAssertEqual(duplicate, reference)
                XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
                saved.append(reference)
            }
            XCTAssertEqual(Set(saved.map(\.id)).count, 2)
            await service.close()
            service = try await NativeMusicFixture.service(store, accounts: accounts)
            let reopened = try await service.collections().filter { $0.networkReference != nil }
            XCTAssertEqual(reopened, saved)
            for (index, reference) in saved.enumerated() {
                try await service.removeNetworkPlaylist(reference, intent: .init())
                let remaining = try await service.collections().filter { $0.networkReference != nil }
                XCTAssertEqual(remaining, Array(saved.dropFirst(index + 1)))
                let persisted = try NativeSyncFixture.document(store).library["library"]["networkReferences"].array
                XCTAssertEqual(persisted.map { $0["id"].string }, remaining.map(\.id))
            }
            await service.close()
            service = try await NativeMusicFixture.service(store, accounts: accounts)
            let cancelled = try await service.collections().filter { $0.networkReference != nil }
            XCTAssertTrue(cancelled.isEmpty)
            let library = try NativeSyncFixture.document(store).library["library"]
            XCTAssertTrue(library["networkReferences"].array.isEmpty)
            XCTAssertEqual(library["collections"].array.count, 1); XCTAssertTrue(library["songs"].fields.isEmpty)
        }
        let calls = await accounts.events()
        XCTAssertTrue(calls.isEmpty)
        await service.close()
    }

    func testKuwoAndMiguCancellationPreservesRevisionAndCASAcrossResaving() async throws {
        for platform in [MusicPublicBrowsePlatform.kuwo, .migu] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let collection = MusicPublicBrowseCollection(platform: platform, nativeID: "501", title: "Remote")
            let saved = try await service.saveNetworkPlaylist(collection, intent: .init())
            let before = store.snapshot()
            let stale = MusicCollection(id: saved.id, title: saved.title, kind: saved.kind, revision: saved.revision + 1,
                                        networkReference: saved.networkReference)
            do { try await service.removeNetworkPlaylist(stale, intent: .init()); XCTFail("stale cancellation") }
            catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
            store.fail(.storageConflict)
            do { try await service.removeNetworkPlaylist(saved, intent: .init()); XCTFail("failed CAS") }
            catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            let retained = try await service.collections().filter { $0.networkReference != nil }
            XCTAssertEqual(retained, [saved])
            store.fail(nil)
            try await service.removeNetworkPlaylist(saved, intent: .init())
            XCTAssertTrue(try NativeSyncFixture.document(store).library["library"]["networkReferences"].array.isEmpty)
            let restored = try await service.saveNetworkPlaylist(collection, intent: .init())
            XCTAssertEqual(restored.id, saved.id); XCTAssertGreaterThan(restored.revision, saved.revision)
            await service.close()
            let reopened = try await NativeMusicFixture.service(store)
            let checkpoint = store.snapshot()
            do { try await reopened.removeNetworkPlaylist(saved, intent: .init()); XCTFail("old handle after resaving") }
            catch { XCTAssertEqual(error as? MusicServiceError, .revisionConflict) }
            XCTAssertEqual(store.snapshot().0, checkpoint.0); XCTAssertEqual(store.snapshot().2, checkpoint.2)
            let current = try await reopened.collections().filter { $0.networkReference != nil }
            XCTAssertEqual(current, [restored])
            try await reopened.removeNetworkPlaylist(restored, intent: .init())
            XCTAssertTrue(try NativeSyncFixture.document(store).library["library"]["networkReferences"].array.isEmpty)
            await reopened.close()
        }
    }

    func testCurrentNetworkStorageRejectsMembersBadIdentityAndCorruptRelationshipWithoutRewrite() async throws {
        let valid = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(valid)
        _ = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "Remote"), intent: .init())
        let current = try NativeSyncFixture.document(valid)
        await service.close()
        for corruption in 0..<5 {
            var document = current, rows = document.library["library"]["networkReferences"].array
            switch corruption {
            case 0: rows[0]["members"] = .array([])
            case 1: rows[0]["remoteID"] = .string("playlist:1")
            case 2: rows.append(rows[0])
            case 3: rows[0]["relation"] = .obj([("accountId", .string("unbound-account")), ("baseline", .bool(true)), ("relationID", .string("501"))])
            default:
                document.library["library"]["collections"] = .array(document.library["library"]["collections"].array +
                    [MusicModel.freshCollection(rows[0]["id"].string, "Managed identity collision")])
            }
            document.library["library"]["networkReferences"] = .array(rows)
            let bytes = try document.encode(changed: ["library"]), store = NativeMusicMemoryDocument(bytes)
            let reopened = try await NativeMusicFixture.service(store)
            do { _ = try await reopened.collections(); XCTFail("corrupt network storage") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(store.snapshot().0, bytes); XCTAssertEqual(store.snapshot().2, 0)
            await reopened.close()
        }
    }

    func testUnsupportedLibraryVersionsAreRejectedWithoutMigrationOrClearingOtherAreas() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        _ = try await MusicAccountQueueFixture.seed(service)
        _ = try await service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "501", title: "Remote"), intent: .init())
        let current = try NativeSyncFixture.document(store)
        await service.close()
        for version in [1, 3] {
            var document = current
            document.library["schema"] = .n(version); document.library["library"]["version"] = .n(version)
            let bytes = try document.encode(changed: ["library"]), rejected = NativeMusicMemoryDocument(bytes)
            let reopened = try await NativeMusicFixture.service(rejected)
            do { _ = try await reopened.collections(); XCTFail("unsupported library version") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(rejected.snapshot().0, bytes); XCTAssertEqual(rejected.snapshot().2, 0)
            await reopened.close()
        }
    }
}
