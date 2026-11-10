import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicNativeService
@testable import MeowMusicPlatform

@MainActor
final class MusicNativeFeedbackOutboxIntegrationTests: XCTestCase {
    private struct Fixture {
        let owner: NativeMusicAccountService
        let native: NativeMusicService
        let settingsDocument: NativeMusicMemoryDocument
        let outbox: DocumentStore
        let peer: NativeFeedbackOfflineHTTP
        let clock: NativeMusicFixtureClock
    }
    private func fixture() async throws -> Fixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let outbox = try DocumentStore(baseURL: root, namespace: "native-feedback")
        let identity = AccountIdentity(source: try SourceAddress(installationID: UUID(), siteKey: "wy"), accountID: UUID())
        let bytes = Data(#"{"version":1,"platform":"wy","accountID":"42","cookies":{"MUSIC_U":"synthetic-account","__csrf":"synthetic-csrf"},"device":{}}"#.utf8)
        let credentials = NativeFeedbackCredentials(try .init(data: bytes)), peer = NativeFeedbackOfflineHTTP()
        let clock = NativeMusicFixtureClock()
        let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials), scopes: [.netease: identity],
            transport: peer, urlCacheStore: outbox, clock: { clock.now() })
        _ = try await owner.setAccess(available: true, generation: 1)
        let settingsDocument = NativeMusicMemoryDocument()
        let native = try await NativeMusicFixture.service(settingsDocument, accounts: owner, clock: { clock.now() })
        _ = try await native.setPlaybackFeedback(platform: .netease, enabled: true, expectedRevision: 0)
        _ = try await owner.validate(platform: .netease)
        await owner.flushPendingPlaybackFeedback()
        return .init(owner: owner, native: native, settingsDocument: settingsDocument, outbox: outbox, peer: peer, clock: clock)
    }
    private func start(_ f: Fixture) async throws -> MusicNativeFeedbackController {
        let controller = await f.native.playbackFeedback
        let settings = try await f.native.playbackFeedbackSettings()
        let own = await f.native.epoch
        controller.consume(sample(1, at: 0, position: 0, kind: .playing),
            settings: settings, targets: [.init(platform: .netease, nativeID: "101")], accounts: f.owner) { platform, generation in
                try await f.native.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
            }
        await controller.flushRegistrations()
        await f.owner.flushPendingPlaybackFeedback()
        XCTAssertNotNil(try f.outbox.read(key: MusicNeteaseFeedbackOutbox.key))
        return controller
    }
    private func sample(_ sequence: UInt64, at: Double, position: Double,
                        kind: MusicPlaybackFeedbackSample.Kind = .progress) -> MusicPlaybackFeedbackSample {
        .init(attemptID: "synthetic", occurrenceID: "entry-1", resourceID: "music:synthetic",
            nativeAttemptID: NativeFeedbackFixture.nativeID, sequence: sequence, observedAt: at, kind: kind,
            position: position, duration: 0, rate: 2, running: true, hasPlayed: true,
            occurredAt: NativeMusicFixture.date.timeIntervalSince1970 + at)
    }
    func testActualNativeCloseDrainsRegisteredTerminalIntoAccountOwnerBeforeAccessRevocation() async throws {
        let f = try await fixture(), controller = try await start(f)
        let uploads = await f.peer.uploadCount()
        let settings = try await f.native.playbackFeedbackSettings(), own = await f.native.epoch
        f.clock.advance(4)
        controller.consume(sample(2, at: 4, position: 8),
            settings: settings, targets: [.init(platform: .netease, nativeID: "101")], accounts: f.owner) { platform, generation in
                try await f.native.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
            }
        // This is the same synchronous boundary called by Feature.prepareForClose.
        f.native.stopPlaybackFeedbackDispatchForClose()
        async let first: Void = f.native.prepareForClose()
        async let second: Void = f.native.prepareForClose()
        _ = await (first, second)
        let data = try XCTUnwrap(f.outbox.read(key: MusicNeteaseFeedbackOutbox.key))
        let document = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let rows = try XCTUnwrap(document["entries"] as? [[String: Any]])
        XCTAssertEqual(rows.map { $0["phase"] as? String }, ["start", "end"])
        XCTAssertEqual(rows.last?["reason"] as? String, "interrupt")
        XCTAssertEqual(rows.last?["occurredAt"] as? Double, 1_800_000_004)
        XCTAssertEqual(rows.last?["listened"] as? Double, 4)
        XCTAssertEqual(rows.last?["media"] as? Double, 8)
        // Product access retirement after prepare is normal close, not opt-out.
        f.native.revokeAccess(generation: 1)
        await f.native.close()
        _ = await f.owner.close()
        XCTAssertEqual(try f.outbox.read(key: MusicNeteaseFeedbackOutbox.key), data)
        let after = await f.peer.uploadCount()
        XCTAssertEqual(after, uploads, "Closing freezes the local pair without starting another network attempt")
    }
    func testFailedSettingsCASAndImmediateAccessFenceClearActualAccountOwnerPendingEvents() async throws {
        for change in ["stale", "storage", "access"] {
            let f = try await fixture()
            _ = try await start(f)
            if change == "access" { f.native.revokeAccess(generation: 2) }
            else {
                let revision = try await f.native.playbackFeedbackSettings().revision
                if change == "storage" { f.settingsDocument.fail(.storageConflict) }
                do {
                    _ = try await f.native.setPlaybackFeedback(platform: .netease, enabled: false,
                        expectedRevision: change == "stale" ? revision - 1 : revision)
                    XCTFail("The synthetic CAS must fail")
                } catch {
                    XCTAssertEqual(error as? MusicServiceError, change == "stale" ? .staleRevision : .storageConflict)
                }
                f.settingsDocument.fail(nil)
            }
            XCTAssertNil(try f.outbox.read(key: MusicNeteaseFeedbackOutbox.key), change)
            await f.native.close(); _ = await f.owner.close()
        }
    }
}

private final class NativeFeedbackCredentials: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var record: CredentialRecord?
    init(_ credential: AccountCredential) { record = .init(credential: credential) }
    func read(for identity: AccountIdentity) -> CredentialRecord? { lock.lock(); defer { lock.unlock() }; return record }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) { lock.lock(); self.record = record; lock.unlock() }
    func remove(for identity: AccountIdentity) { lock.lock(); record = nil; lock.unlock() }
}
private actor NativeFeedbackOfflineHTTP: MusicAccountHTTPTransport {
    private var uploads = 0
    func uploadCount() -> Int { uploads }
    func send(_ request: MusicAccountHTTPRequest) throws -> MusicAccountHTTPResponse {
        if request.url == MusicAccountFeedbackNeteaseWire.uploadURL {
            uploads += 1
            throw MusicAccountFeedbackNotSent.connectionUnavailable
        }
        guard request.url.path == "/weapi/w/nuser/account/get" else { throw MusicServiceError.invalidRequest }
        return .init(status: 200, data: Data(#"{"code":200,"profile":{"userId":42,"nickname":"Synthetic"},"account":{"id":42}}"#.utf8))
    }
}
