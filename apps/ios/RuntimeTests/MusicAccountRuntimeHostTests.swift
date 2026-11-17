import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService
import MeowRuntime
@testable import Meowfilm

/// App publication and native consumers with synthetic canonical runtime replies.
/// This does not replace the separately required real-upstream acceptance.
@MainActor
final class MusicAccountRuntimeHostTests: XCTestCase {
    func testAppMusicUsesGuestThenSavedAccountThenGuestWithoutPublishingALateAccountHome() async throws {
        let fixture = try MusicAccountHostFixture()
        let held = MusicAccountHostGate()
        addTeardownBlock { await held.release(); await fixture.close() }
        await fixture.start()
        XCTAssertNotNil(fixture.assembly.music)
        XCTAssertFalse(fixture.assembly.startupFailed)
        let guest = try await fixture.service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(guest.sections.first?.title, "guest")
        XCTAssertNil(fixture.store.peek(fixture.identity))

        let challenge = try await fixture.owner.beginLogin(platform: .netease)
        let progress = try await fixture.owner.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed)
        XCTAssertNotNil(fixture.store.peek(fixture.identity))
        let member = try await fixture.service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(member.sections.first?.title, "member")
        XCTAssertEqual(fixture.store.events().filter { $0 == "write" || $0 == "publish:member" },
                       ["write", "publish:member"], "The real local save must precede runtime publication")

        await fixture.peer.holdNextHome(held)
        let late = Task { try await fixture.service.homePage(cursor: nil, refresh: true) }
        defer { late.cancel() }
        await fulfillment(of: [held.entered], timeout: 3)
        try await fixture.owner.logout(platform: .netease)
        XCTAssertNil(fixture.store.peek(fixture.identity))
        let signedOut = try await fixture.service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(signedOut.sections.first?.title, "guest")
        await held.release()
        do { _ = try await late.value; XCTFail("A retired account page became current after logout") }
        catch {
            XCTAssertTrue([MusicServiceError.cancelled, .staleResult].contains(error as? MusicServiceError ?? .unavailable))
        }
        let current = try await fixture.service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(current.sections.first?.title, "guest")
        let calls = await fixture.peer.operations()
        XCTAssertEqual(calls.filter { $0 == "qr.create" }.count, 1)
        XCTAssertEqual(calls.filter { $0 == "qr.poll" }.count, 1)
        XCTAssertFalse(calls.contains("account.verify"), "Ordinary home reads cannot add an authentication round trip")
        XCTAssertFalse(calls.contains("account.profile"))
        let writes = fixture.store.events().filter { $0 == "write" }
        XCTAssertEqual(writes.count, 1)
    }

    func testAppAccountSaveFailureNeverPublishesTheAuthenticatedCandidate() async throws {
        let fixture = try MusicAccountHostFixture(writeFailure: .accessDenied)
        addTeardownBlock { await fixture.close() }
        await fixture.start()
        let guest = try await fixture.service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(guest.sections.first?.title, "guest")
        let challenge = try await fixture.owner.beginLogin(platform: .netease)
        do {
            _ = try await fixture.owner.checkLogin(challengeID: challenge.id)
            XCTFail("An upstream candidate is not a durable login receipt")
        } catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertNil(fixture.store.peek(fixture.identity))
        XCTAssertFalse(fixture.store.events().contains("publish:member"))
        let statuses = try await fixture.owner.statuses()
        XCTAssertNotEqual(statuses.first?.phase, .signedIn)
        let calls = await fixture.peer.operations()
        XCTAssertEqual(calls.filter { $0 == "qr.poll" }.count, 1)
        XCTAssertFalse(calls.contains("account.verify"))
    }

    func testClosingTheAppAccountGateIsTerminalWithoutClosingItsBorrowedRuntime() async throws {
        let fixture = try MusicAccountHostFixture()
        addTeardownBlock { await fixture.close() }
        await fixture.start()
        _ = try await fixture.service.homePage(cursor: nil, refresh: true)
        await fixture.service.close()
        let report = await fixture.gate.close()
        XCTAssertTrue(report.cleanupComplete)
        XCTAssertEqual(report.inFlight, 0)
        await fixture.gate.setAvailable(true)
        let repeated = await fixture.gate.close()
        XCTAssertEqual(repeated, report)
        let statuses = try await fixture.owner.statuses()
        XCTAssertTrue(statuses.allSatisfy { $0.phase == .unavailable })
        XCTAssertFalse(fixture.runtime.runtime.isClosed, "The gate borrows, rather than owns, the App runtime")
        try await fixture.platform.close()
        XCTAssertFalse(fixture.runtime.runtime.isClosed, "Platform drain cannot close another module's transport")
        do {
            _ = try await fixture.platform.request(platform: "wy", operation: "qr.create", candidate: .null)
            XCTFail("App platform admission reopened after close")
        } catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        await fixture.runtime.runtime.close()
        XCTAssertTrue(fixture.runtime.runtime.isClosed)
    }
}

@MainActor
private final class MusicAccountHostFixture {
    let assembly = MeowfilmAssembly()
    let identity: AccountIdentity
    let store: MusicAccountHostStore
    let peer: MusicAccountHostPeer
    let runtime: MusicHostPlatformRuntime
    let platform: MusicPlatformClient
    let owner: NativeMusicAccountService
    let gate: MusicAccountAccessGate
    let service: NativeMusicService

    init(writeFailure: CredentialStoreError? = nil) throws {
        let identity = AccountIdentity(
            source: try SourceAddress(installationID: UUID(), siteKey: "wy"), accountID: UUID())
        let store = MusicAccountHostStore(writeFailure: writeFailure)
        let peer = MusicAccountHostPeer(store: store, identity: identity)
        let runtime = try MusicHostPlatformRuntime(control: { await peer.publish($0) }) {
            try await peer.respond($0)
        }
        let platform = runtime.client()
        let owner = NativeMusicAccountService(accounts: .init(store: store), scopes: [.netease: identity],
            platformClient: platform, clock: { MusicAccountHostPeer.now })
        self.identity = identity; self.store = store; self.peer = peer; self.runtime = runtime
        self.platform = platform; self.owner = owner
        gate = MusicAccountAccessGate(service: owner)
        service = NativeMusicService(database: MusicStartupLibrary(failure: nil), accounts: owner,
            platformClient: platform, clock: { MusicAccountHostPeer.now })
    }

    func start() async {
        await gate.setAvailable(true)
        await assembly.prepareMusic(service, prepareAccounts: { try await self.owner.prepareLocalAccounts() },
            makeModel: { MusicViewModel(service: self.service, playback: self.assembly.playback, accounts: self.owner) })
    }

    func close() async {
        assembly.shutdown()
        await assembly.waitForShutdown()
        _ = await gate.close()
        try? await platform.close()
        await runtime.runtime.close()
    }
}

/// Only the current secure-store port; no platform wire or credential migration.
private final class MusicAccountHostStore: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private let writeFailure: CredentialStoreError?
    private var records: [AccountIdentity: CredentialRecord] = [:]
    private var calls: [String] = []
    init(writeFailure: CredentialStoreError?) { self.writeFailure = writeFailure }
    func read(for identity: AccountIdentity) -> CredentialRecord? { peek(identity) }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }
        calls.append("write")
        if let writeFailure { throw writeFailure }
        records[identity] = record
    }
    func remove(for identity: AccountIdentity) {
        lock.lock(); defer { lock.unlock() }
        calls.append("remove"); records.removeValue(forKey: identity)
    }
    func peek(_ identity: AccountIdentity) -> CredentialRecord? {
        lock.lock(); defer { lock.unlock() }; return records[identity]
    }
    func publishedMember() { lock.lock(); calls.append("publish:member"); lock.unlock() }
    func events() -> [String] { lock.lock(); defer { lock.unlock() }; return calls }
}

private actor MusicAccountHostPeer {
    static let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let store: MusicAccountHostStore
    private let identity: AccountIdentity
    private var session = RuntimeValue.null
    private var calls: [String] = []
    private var nextHome: MusicAccountHostGate?
    init(store: MusicAccountHostStore, identity: AccountIdentity) { self.store = store; self.identity = identity }
    func operations() -> [String] { calls }
    func holdNextHome(_ gate: MusicAccountHostGate) { nextHome = gate }
    func publish(_ fields: RuntimeValue) -> RuntimeValue {
        if fields[music: "platform"] == .string("wy") {
            session = fields[music: "session"]
            if session != .null {
                XCTAssertNotNil(store.peek(identity), "Publication requires a completed local store write")
                store.publishedMember()
            }
        }
        return .object(["published": .bool(true)])
    }
    func respond(_ fields: RuntimeValue) async throws -> RuntimeValue {
        let operation = fields[music: "operation"].musicText
        calls.append(operation)
        switch operation {
        case "home":
            if session == .null { XCTAssertEqual(fields[music: "expectedSession"], .null) }
            else {
                XCTAssertEqual(fields[music: "expectedSession"][music: "scope"], session[music: "scope"])
                XCTAssertEqual(fields[music: "expectedSession"][music: "generation"], session[music: "generation"])
            }
            let title = session == .null ? "guest" : "member"
            let page: RuntimeValue = .object(["sections": .array([
                .object(["id": .string("current-home"), "title": .string(title), "items": .array([
                    .object(["id": .string("42"), "name": .string(title + " collection"),
                             "action": .object(["type": .string("playlist")])])
                ])])
            ]), "continuation": .null])
            let gate = nextHome; nextHome = nil
            await gate?.wait()
            return page
        case "qr.create":
            XCTAssertEqual(fields[music: "candidate"], .null)
            return .object(["url": .string("https://music.163.com/login?codekey=synthetic-app-host"),
                "expiresAt": .number(Self.now.addingTimeInterval(180).timeIntervalSince1970 * 1000)])
        case "qr.poll":
            return .object(["status": .string("authenticated"), "account": .object([
                "userId": .string("101"), "cookie": .string("MUSIC_U=synthetic-app-host")
            ])])
        case "qr.cancel": return .object(["cancelled": .bool(true)])
        default:
            XCTFail("Unexpected operation in the App publication fixture: " + operation)
            throw RuntimeHTTPError.remote("UNSUPPORTED")
        }
    }
}

private actor MusicAccountHostGate {
    nonisolated let entered = XCTestExpectation(description: "Old account home is physically held")
    private var released = false
    private var waiting: CheckedContinuation<Void, Never>?
    func wait() async {
        guard !released else { return }
        await withCheckedContinuation { waiting = $0; entered.fulfill() }
    }
    func release() { released = true; waiting?.resume(); waiting = nil }
}
