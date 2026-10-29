import Foundation
@testable import MeowAccounts

/// Synchronous synthetic storage. Effects are one-shot and never call back into an actor.
final class AccountCredentialUpdateTestStore: CredentialStore, @unchecked Sendable {
    enum WriteEffect {
        case normal
        case failBefore(Error)
        case persistThenFail(Error)
        case replaceThenFail(CredentialRecord, Error)
        case replaceThenReturn(CredentialRecord)
        case removeThenReturn
        case leaveUnchanged
        case failConfirmationRead(Error)
    }

    struct Calls: Equatable {
        var reads = 0
        var writes = 0
        var removes = 0
    }

    private let lock = NSLock()
    private var records: [AccountIdentity: CredentialRecord] = [:]
    private var counts = Calls()
    private var writeEffect: WriteEffect = .normal
    private var nextReadError: Error?

    var calls: Calls {
        lock.lock()
        defer { lock.unlock() }
        return counts
    }

    func arm(_ effect: WriteEffect) {
        lock.lock()
        defer { lock.unlock() }
        writeEffect = effect
    }

    func failNextRead(with error: Error) {
        lock.lock()
        defer { lock.unlock() }
        nextReadError = error
    }

    func seed(_ record: CredentialRecord?, for identity: AccountIdentity) {
        lock.lock()
        defer { lock.unlock() }
        records[identity] = record
    }

    func persisted(for identity: AccountIdentity) -> CredentialRecord? {
        lock.lock()
        defer { lock.unlock() }
        return records[identity]
    }

    func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock()
        defer { lock.unlock() }
        counts.reads += 1
        if let error = nextReadError {
            nextReadError = nil
            throw error
        }
        return records[identity]
    }

    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        counts.writes += 1
        let effect = writeEffect
        writeEffect = .normal
        switch effect {
        case .normal:
            records[identity] = record
        case .failBefore(let error):
            throw error
        case .persistThenFail(let error):
            records[identity] = record
            throw error
        case .replaceThenFail(let external, let error):
            records[identity] = external
            throw error
        case .replaceThenReturn(let external):
            records[identity] = external
        case .removeThenReturn:
            records[identity] = nil
        case .leaveUnchanged:
            break
        case .failConfirmationRead(let error):
            records[identity] = record
            nextReadError = error
        }
    }

    func remove(for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        counts.removes += 1
        records[identity] = nil
    }
}

struct AccountCredentialUpdateFixture: Sendable {
    let store: AccountCredentialUpdateTestStore
    let coordinator: AccountSessionCoordinator
    let identity: AccountIdentity
    let token: AccountSessionToken
    let original: AccountCredential

    static func signedIn() async throws -> AccountCredentialUpdateFixture {
        let store = AccountCredentialUpdateTestStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let original = try AccountFixtures.credential("update-original")
        let token = try await coordinator.beginLogin(for: identity)
        try await coordinator.completeLogin(token, credential: original)
        return Self(store: store, coordinator: coordinator, identity: identity,
                    token: token, original: original)
    }
}
