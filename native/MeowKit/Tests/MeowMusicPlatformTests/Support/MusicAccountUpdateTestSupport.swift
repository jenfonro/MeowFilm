import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

// Synthetic storage only. No Keychain, platform request or private Accounts API.
final class MusicUpdateReadGate: @unchecked Sendable {
    let entered: XCTestExpectation
    private let semaphore = DispatchSemaphore(value: 0)
    init(_ entered: XCTestExpectation) { self.entered = entered }
    func wait() throws {
        entered.fulfill()
        guard semaphore.wait(timeout: .now() + 5) == .success else { throw CredentialStoreError.unavailable }
    }
    func release() { semaphore.signal() }
}

final class MusicUpdateStore: CredentialStore, @unchecked Sendable {
    enum WriteFault: Equatable { case before, after, readback, mismatch, foreign, missing }
    private let lock = NSLock()
    private var records: [AccountIdentity: CredentialRecord] = [:]
    private var readFault: CredentialStoreError?
    private var writeFault: WriteFault?
    private var readGate: MusicUpdateReadGate?
    private var reads = 0, writes = 0, removes = 0
    func seed(_ credential: AccountCredential, for identity: AccountIdentity) {
        lock.lock(); defer { lock.unlock() }; records[identity] = .init(credential: credential)
    }
    func failRead(_ error: CredentialStoreError) { lock.lock(); readFault = error; lock.unlock() }
    func failWrite(_ error: WriteFault) { lock.lock(); writeFault = error; lock.unlock() }
    func holdRead(_ gate: MusicUpdateReadGate) { lock.lock(); readGate = gate; lock.unlock() }
    func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock(); reads += 1
        let error = readFault, gate = readGate, record = records[identity]
        readFault = nil; readGate = nil; lock.unlock()
        if let error { throw error }
        try gate?.wait()
        return record
    }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }; writes += 1
        let fault = writeFault; writeFault = nil
        if fault == .before { throw CredentialStoreError.backendFailure }
        records[identity] = record
        switch fault {
        case .some(.after): throw CredentialStoreError.backendFailure
        case .some(.readback): readFault = .interactionNotAllowed
        case .some(.mismatch): records[identity] = .init(credential: record.credential)
        case .some(.foreign): records[identity] = .init(credential: try MusicUpdateProvider.credential("external", uid: "99"))
        case .some(.missing): records.removeValue(forKey: identity)
        default: break
        }
    }
    func remove(for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }; removes += 1; records.removeValue(forKey: identity)
    }
    func peek(_ identity: AccountIdentity) -> CredentialRecord? {
        lock.lock(); defer { lock.unlock() }; return records[identity]
    }
    func counts() -> (reads: Int, writes: Int, removes: Int) {
        lock.lock(); defer { lock.unlock() }; return (reads, writes, removes)
    }
}

actor MusicUpdateProvider: MusicAccountProvider {
    static func credential(_ label: String = "old", uid: String = "42") throws -> AccountCredential {
        try .init(data: MusicQQWire.json(["uid": uid, "secret": "synthetic-" + label]))
    }
    private var replacement: AccountCredential?
    private var claimedID: String?
    private var hold: MusicAccountTestGate?
    private var capabilityHold: MusicAccountTestGate?
    private var verifies = 0, refreshes = 0, resolutions = 0
    private var inputs: [AccountCredential] = []
    func configure(_ replacement: AccountCredential?, claimedID: String? = nil, hold: MusicAccountTestGate? = nil) {
        self.replacement = replacement; self.claimedID = claimedID; self.hold = hold
    }
    func holdCapabilities(_ gate: MusicAccountTestGate) { capabilityHold = gate }
    func counts() -> (verifies: Int, refreshes: Int, resolutions: Int) { (verifies, refreshes, resolutions) }
    func received() -> [AccountCredential] { inputs }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicQQWire.string(MusicQQWire.decode(credential.dataRepresentation())["uid"])
    }
    private func update(_ credential: AccountCredential) async throws -> MusicQQVerified {
        inputs.append(credential)
        let value = replacement ?? credential, id = try claimedID ?? nativeID(value), gate = hold
        if let gate { await gate.wait() } // Non-cooperative on purpose.
        return .init(credential: value, nickname: "Synthetic", nativeID: id)
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified { verifies += 1; return try await update(credential) }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified { refreshes += 1; return try await update(credential) }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        .init(id: id, platform: .qq, image: Data([0]), expiresAt: Date().addingTimeInterval(180))
    }
    func pollQR(id: UUID) async throws -> MusicQQLoginResult { .init(progress: .waiting, verified: nil) }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation {
        .init(rows: [], nextPage: nil)
    }
    func collectionRows(credential: AccountCredential) async throws -> [JSONValue] { [] }
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue] { [] }
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String] {
        if let capabilityHold { await capabilityHold.wait() }
        return ["128k"]
    }
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue {
        resolutions += 1
        return .object(["url": .string("https://media.invalid/synthetic.mp3"), "quality": .string(quality),
                        "qualityVerified": .bool(false), "expiresAt": .null])
    }
}

struct MusicUpdateFixture {
    let identity = try! MusicAccountFixture.identity()
    let otherIdentity = try! MusicAccountFixture.identity("wy")
    let store = MusicUpdateStore()
    let provider = MusicUpdateProvider()
    let otherProvider = MusicUpdateProvider()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init() throws {
        accounts = .init(store: store)
        service = .init(accounts: accounts, scopes: [.qq: identity, .netease: otherIdentity],
                        providers: [.qq: provider, .netease: otherProvider])
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        store.seed(try MusicUpdateProvider.credential("other", uid: "99"), for: otherIdentity)
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}
