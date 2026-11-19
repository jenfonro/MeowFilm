import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
import MeowStorage
import MeowStorageTestSupport
@testable import MeowMusicNativeService
@testable import MeowMusicPlatform

@MainActor
final class MusicNativeFeedbackOutboxIntegrationTests: XCTestCase {
    private struct Fixture {
        let owner: NativeMusicAccountService
        let native: NativeMusicService
        let settingsDocument: NativeMusicTestLibrary
        let outbox: SQLiteDatabase
        let peer: NativeFeedbackOfflinePeer
        let clock: NativeMusicFixtureClock
    }
    private func fixture(heldUpload: (gate: NativeMusicGate, started: XCTestExpectation)? = nil) async throws -> Fixture {
        let outbox = try storageTestDatabase(self)
        let identity = AccountIdentity(source: try SourceAddress(installationID: UUID(), siteKey: "wy"), accountID: UUID())
        let bytes = Data(#"{"platform":"wy","userId":"42","cookie":"MUSIC_U=synthetic-account; __csrf=synthetic-csrf"}"#.utf8)
        let credentials = NativeFeedbackCredentials(try .init(data: bytes)), peer = NativeFeedbackOfflinePeer()
        let clock = NativeMusicFixtureClock()
        let transport = try MusicPlatformFixture { try await peer.respond($0) }
        let client = transport.makeClient()
        let owner: NativeMusicAccountService
        if let heldUpload {
            let provider = NativeFeedbackHeldUploadProvider(
                node: MusicNodeAccountProvider(platform: .netease, client: client, clock: { clock.now() }),
                peer: peer, gate: heldUpload.gate, started: heldUpload.started)
            owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials), scopes: [.netease: identity],
                providers: [.netease: provider], database: outbox, clock: { clock.now() })
        } else {
            owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials), scopes: [.netease: identity],
                platformClient: client, database: outbox, clock: { clock.now() })
        }
        _ = try await owner.setAccess(available: true, generation: 1)
        let settingsDocument = NativeMusicTestLibrary()
        let native = NativeMusicService(database: settingsDocument, accounts: owner, platformClient: client,
            clock: { clock.now() }, monotonicClock: { clock.now().timeIntervalSince(NativeMusicFixture.date) })
        addTeardownBlock {
            if let heldUpload { await heldUpload.gate.release() }
            await native.close(); _ = await owner.close()
            try await client.close(); await transport.runtime.close()
        }
        try await native.setAccess(available: true, generation: 1)
        _ = try await native.setPlaybackFeedback(platform: .netease, enabled: true, expectedRevision: 0)
        _ = try await owner.validate(platform: .netease)
        await owner.flushPendingPlaybackFeedback()
        return .init(owner: owner, native: native, settingsDocument: settingsDocument, outbox: outbox, peer: peer, clock: clock)
    }
    private func feedbackData(_ database: SQLiteDatabase) throws -> Data? {
        guard let data = try storageTestRowData(database, "SELECT * FROM ios_music_feedback_outbox ORDER BY occurredAt,id",
            booleans: ["heartbeat", "startConfirmed"]) else { return nil }
        return try JSONSerialization.data(withJSONObject: ["entries": JSONSerialization.jsonObject(with: data)], options: [.sortedKeys])
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
        XCTAssertNotNil(try feedbackData(f.outbox))
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
        let data = try XCTUnwrap(feedbackData(f.outbox))
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
        XCTAssertEqual(try feedbackData(f.outbox), data)
        let after = await f.peer.uploadCount()
        XCTAssertEqual(after, uploads, "Closing freezes the local pair without starting another network attempt")
    }
    func testNativeCloseWaitsForIndependentOutboxUploadAfterControllerRegistrationHasFinished() async throws {
        let gate = NativeMusicGate(), entered = expectation(description: "independent outbox upload dispatched")
        let f = try await fixture(heldUpload: (gate, entered))
        let controller = await f.native.playbackFeedback
        let settings = try await f.native.playbackFeedbackSettings(), own = await f.native.epoch
        controller.consume(sample(1, at: 0, position: 0, kind: .playing),
            settings: settings, targets: [.init(platform: .netease, nativeID: "101")], accounts: f.owner) { platform, generation in
                try await f.native.checkFeedbackAuthorization(epoch: own, platform: platform, generation: generation)
            }
        await fulfillment(of: [entered], timeout: 3)
        // The lease reported registered locally; this flush does not own the
        // account outbox's independent network worker.
        await controller.flush()
        let dispatched = await f.peer.uploadCount(); XCTAssertEqual(dispatched, 1)
        let nativeBefore = f.settingsDocument.snapshot()
        let firstReturned = expectation(description: "close waits for independent account upload")
        let secondReturned = expectation(description: "repeated close shares account drain")
        firstReturned.isInverted = true; secondReturned.isInverted = true
        let first = Task { await f.native.close(); firstReturned.fulfill() }
        let second = Task { await f.native.close(); secondReturned.fulfill() }
        await fulfillment(of: [firstReturned, secondReturned], timeout: 0.05)
        let frozen = try feedbackData(f.outbox)
        await gate.release()
        await first.value; await second.value
        await f.owner.flushPendingPlaybackFeedback()
        let uploads = await f.peer.uploadCount()
        XCTAssertEqual(uploads, 1, "a cancelled dispatched upload cannot be replayed")
        XCTAssertEqual(try feedbackData(f.outbox), frozen,
                       "the late receipt cannot recreate or overwrite the retired outbox")
        XCTAssertEqual(f.settingsDocument.snapshot().0, nativeBefore.0)
        XCTAssertEqual(f.settingsDocument.snapshot().2, nativeBefore.2)
        _ = await f.owner.close()
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
            XCTAssertNil(try feedbackData(f.outbox), change)
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
private actor NativeFeedbackOfflinePeer: MusicPlatformFixtureResponding {
    private var uploads = 0
    func uploadCount() -> Int { uploads }
    func heldUpload(_ gate: NativeMusicGate, started: XCTestExpectation) async -> MusicPlaybackFeedbackReceipt {
        uploads += 1
        started.fulfill()
        await gate.wait() // Deliberately ignores cancellation after dispatch.
        return .confirmed
    }
    func respond(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        switch request.operation {
        case "feedback.upload":
            uploads += 1
            return .object(["status": .string("notSent"), "errorCode": .string("NETWORK_ERROR")])
        case "account.verify":
            return .object(["userId": .string("42"),
                "cookie": .string("MUSIC_U=synthetic-account; __csrf=synthetic-csrf"),
                "profile": .object(["displayName": .string("Synthetic")])])
        case "feedback.release": return .object(["released": .bool(true)])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }
}

private struct NativeFeedbackHeldUploadProvider: MusicAccountFeedbackProvider,
    MusicNeteasePlaybackLogUploading, MusicAccountSessionPublishing {
    let node: MusicNodeAccountProvider
    let peer: NativeFeedbackOfflinePeer
    let gate: NativeMusicGate
    let started: XCTestExpectation
    func uploadPlaybackLog(_ events: Data, authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        return await peer.heldUpload(gate, started: started)
    }
    func publish(_ credential: AccountCredential, scope: String, generation: Int, authorization: MusicQueueAuthorization) async throws {
        try await node.publish(credential, scope: scope, generation: generation, authorization: authorization)
    }
    func feedback(_ report: MusicPlaybackFeedbackReport, session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await node.feedback(report, session: session, guard: authorization)
    }
    func releaseFeedback(_ session: MusicAccountFeedbackNativeSession) async { await node.releaseFeedback(session) }
    func createQR(id: UUID) async throws -> MusicLoginChallenge { try await node.createQR(id: id) }
    func pollQR(id: UUID) async throws -> MusicAccountLoginResult { try await node.pollQR(id: id) }
    func cancelQR(_ id: UUID) async { await node.cancelQR(id) }
    func revoke() async { await node.revoke() }
    func nativeID(_ credential: AccountCredential) async throws -> String { try await node.nativeID(credential) }
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified { try await node.verify(credential) }
    func refresh(_ credential: AccountCredential) async throws -> MusicAccountVerified { try await node.refresh(credential) }
    func recommendations(kind: String, page: Int) async throws -> MusicAccountRecommendation {
        try await node.recommendations(kind: kind, page: page)
    }
    func collectionRows() async throws -> [RuntimeValue] { try await node.collectionRows() }
    func tracks(collectionID: String) async throws -> [RuntimeValue] { try await node.tracks(collectionID: collectionID) }
    func capabilities(mid: String) async throws -> [String] { try await node.capabilities(mid: mid) }
    func resolve(mid: String, quality: String) async throws -> RuntimeValue { try await node.resolve(mid: mid, quality: quality) }
}
