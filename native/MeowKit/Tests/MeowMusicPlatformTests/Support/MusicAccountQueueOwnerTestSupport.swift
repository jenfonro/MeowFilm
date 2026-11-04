import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

// Synthetic owner fixtures only: reuse the existing store, credential provider
// and non-cooperative gate. No Keychain, URLSession, reference code or real account.
final class MusicAccountQueueOwnerGate: @unchecked Sendable {
    let entered: XCTestExpectation
    let departed: XCTestExpectation
    private let gate = MusicAccountTestGate()
    init(_ label: String) {
        entered = XCTestExpectation(description: label + " entered")
        departed = XCTestExpectation(description: label + " departed")
    }
    func wait() async { entered.fulfill(); await gate.wait(); departed.fulfill() }
    func release() async { await gate.release() }
}

/// A bounded XCTest wait reads the captured outcome; a failed expectation must
/// not be followed by an unbounded await of Task.value or gate.started().
@MainActor final class MusicAccountQueueOwnerCall<Value: Sendable> {
    let finished: XCTestExpectation
    private(set) var result: Result<Value, Error>?
    private var task: Task<Void, Never>?
    init(_ label: String, operation: @escaping @Sendable () async throws -> Value) {
        finished = XCTestExpectation(description: label)
        task = Task { [weak self] in
            let result: Result<Value, Error>
            do { result = .success(try await operation()) }
            catch { result = .failure(error) }
            guard let self else { return }
            self.result = result; self.finished.fulfill()
        }
    }
    func cancel() { task?.cancel() }
    func value(file: StaticString = #filePath, line: UInt = #line) throws -> Value {
        try XCTUnwrap(result, file: file, line: line).get()
    }
}

actor MusicAccountQueueOwnerProvider: MusicAccountProvider {
    struct AccountCalls: Equatable, Sendable {
        var verifies = 0, refreshes = 0, logins = 0, polls = 0
    }
    struct Read: Equatable, Sendable {
        let operation: String
        let argument: String
        let page: Int
        let credential: AccountCredential
    }
    private struct RecommendationPage: Sendable {
        let count: Int
        let nextPage: Int?
    }
    let platform: MusicAccountPlatform
    private let account = MusicUpdateProvider()
    private var logins = 0, polls = 0
    private var reads: [Read] = []
    private var recommendationPages: [Int: RecommendationPage] = [:]
    private var businessGate: MusicAccountQueueOwnerGate?
    private var transitionGate: (String, MusicAccountQueueOwnerGate)?
    init(platform: MusicAccountPlatform) { self.platform = platform }
    func holdBusiness(_ gate: MusicAccountQueueOwnerGate) { businessGate = gate }
    func holdTransition(_ name: String, _ gate: MusicAccountQueueOwnerGate) { transitionGate = (name, gate) }
    func recommendationPage(_ page: Int, count: Int, nextPage: Int?) {
        recommendationPages[page] = .init(count: count, nextPage: nextPage)
    }
    func recorded() -> [Read] { reads }
    func accountCalls() async -> AccountCalls {
        let calls = await account.counts()
        return .init(verifies: calls.verifies, refreshes: calls.refreshes, logins: logins, polls: polls)
    }
    private func wait(_ name: String) async {
        if let held = transitionGate, held.0 == name {
            transitionGate = nil
            await held.1.wait()
        }
    }
    func nativeID(_ credential: AccountCredential) async throws -> String { try await account.nativeID(credential) }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        await wait("validate")
        return try await account.verify(credential)
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified {
        await wait("refresh")
        return try await account.refresh(credential)
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        logins += 1; await wait("login")
        return .init(id: id, platform: platform, image: Data([0]), expiresAt: .distantFuture)
    }
    func pollQR(id: UUID) -> MusicQQLoginResult {
        polls += 1
        return .init(progress: .waiting, verified: nil)
    }
    func cancelQR(_ id: UUID) {}
    func revoke() async { await wait("revoke") }
    private func rows(operation: String, argument: String, page: Int, count: Int,
                      credential: AccountCredential) async throws -> [JSONValue] {
        reads.append(.init(operation: operation, argument: argument, page: page, credential: credential))
        let batch = reads.count, uid = try await nativeID(credential)
        let result: [JSONValue] = (0..<count).map { index in
            .object([
                "songmid": .string("\(platform.rawValue)B\(batch)P\(page)I\(index)"),
                "name": .string("Synthetic \(uid) \(argument) \(batch)"),
                "singer": .string("Synthetic"), "album": .string("Synthetic"),
                "duration": .number(180), "types": .array([.string("128k")])
            ])
        }
        if let gate = businessGate {
            businessGate = nil
            await gate.wait() // Intentionally returns valid rows after cancellation.
        }
        return result
    }
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation {
        let continuation = kind == "radar" && page == 1
        let shape = recommendationPages[page] ?? .init(count: continuation ? 30 : 35,
                                                      nextPage: continuation ? 2 : nil)
        let value = try await rows(operation: "recommendations", argument: kind, page: page,
                                   count: shape.count, credential: credential)
        return .init(rows: value, nextPage: shape.nextPage)
    }
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue] {
        try await rows(operation: "tracks", argument: collectionID, page: 1, count: 65, credential: credential)
    }
    func collectionRows(credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func capabilities(mid: String, credential: AccountCredential) throws -> [String] { throw MusicServiceError.unsupported }
    func resolve(mid: String, quality: String, credential: AccountCredential) throws -> JSONValue {
        throw MusicServiceError.unsupported
    }
}

struct MusicAccountQueueOwnerFixture: Sendable {
    let identity: AccountIdentity
    let otherIdentity: AccountIdentity
    let store = MusicUpdateStore()
    let provider = MusicAccountQueueOwnerProvider(platform: .qq)
    let otherProvider = MusicAccountQueueOwnerProvider(platform: .netease)
    let clock = MusicAccountTestClock()
    let accounts: AccountSessionCoordinator
    let owner: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity()
        otherIdentity = try MusicAccountFixture.identity("wy")
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        store.seed(try MusicUpdateProvider.credential("other", uid: "99"), for: otherIdentity)
        accounts = .init(store: store)
        owner = .init(accounts: accounts, scopes: [.qq: identity, .netease: otherIdentity],
                      providers: [.qq: provider, .netease: otherProvider], clock: { [clock] in clock.now() })
    }
    func enable() async throws { _ = try await owner.setAccess(available: true, generation: 1) }
    static func rows(_ value: MusicNativeTrackPage) throws -> [JSONValue] {
        try value.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }
    }
}
