import Foundation

/// Product-owned account session coordinator backed by atomic SQLite credential persistence.
public actor AccountSessionCoordinator {
    private struct Entry {
        var generation: UUID
        var phase: AccountPhase
        var failure: AccountFailure?
        var record: CredentialRecord?
        var credentialRevision: UUID? { record?.revision }
    }

    private struct Observer: Sendable {
        let identity: AccountIdentity
        let continuation: AsyncStream<AccountSnapshot>.Continuation
    }

    private let store: MusicSQLiteCredentialStore
    private var entries: [AccountIdentity: Entry] = [:]
    private var observers: [UUID: Observer] = [:]

    public init(store: MusicSQLiteCredentialStore) {
        self.store = store
    }

    public func prepareReplacement(for identity: AccountIdentity) throws -> AccountReplacementLease {
        let state = snapshot(for: identity)
        guard state.phase != .signingIn, state.phase != .removalPending else {
            throw AccountSessionError.invalidState
        }
        let record: CredentialRecord?
        if let entry = entries[identity], entry.phase == .signedIn || entry.phase == .requiresValidation {
            record = entry.record
        } else {
            do { record = try store.read(for: identity) }
            catch { throw AccountSessionError.storage(.sanitized(error)) }
        }
        if state.phase == .invalidated || state.phase == .failed {
            guard record == nil else { throw AccountSessionError.invalidState }
        }
        if let entry = entries[identity], entry.phase != .signedOut,
           record?.revision != entry.credentialRevision {
            throw AccountSessionError.credentialsUnavailable
        }
        return AccountReplacementLease(snapshot: state, record: record)
    }

    public func replaceVerified(_ lease: AccountReplacementLease,
                                credential: AccountCredential) throws -> AccountSessionToken {
        try lease.consume {
            let identity = lease.snapshot.identity
            guard snapshot(for: identity) == lease.snapshot else { throw AccountSessionError.staleSession }
            let replacement = CredentialRecord(credential: credential)
            do {
                try store.write(replacement, for: identity)
            } catch {
                throw AccountSessionError.storage(.sanitized(error))
            }
            let generation = UUID()
            entries[identity] = Entry(generation: generation, phase: .signedIn, failure: nil, record: replacement)
            publish(identity)
            return AccountSessionToken(identity: identity, generation: generation)
        }
    }

    public func replacementIsCurrent(_ lease: AccountReplacementLease) throws -> Bool {
        try lease.check {
            snapshot(for: lease.snapshot.identity) == lease.snapshot
        }
    }
    public func replacementsAreCurrent(_ leases: [AccountReplacementLease]) throws -> Bool {
        try leases.allSatisfy { try replacementIsCurrent($0) }
    }

    public func readOffline(for identity: AccountIdentity) throws -> AccountCredential? {
        let state = snapshot(for: identity)
        guard state.phase != .signingIn, state.phase != .removalPending else {
            throw AccountSessionError.invalidState
        }
        if let entry = entries[identity], entry.phase == .signedIn || entry.phase == .requiresValidation {
            return entry.record?.credential
        }
        do {
            return try store.read(for: identity)?.credential
        } catch {
            throw AccountSessionError.storage(.sanitized(error))
        }
    }

    @discardableResult
    public func replaceOffline(for identity: AccountIdentity, credential: AccountCredential?) throws -> AccountSessionToken? {
        let state = snapshot(for: identity)
        guard state.phase != .signingIn else {
            throw AccountSessionError.invalidState
        }
        guard let credential else {
            try clear(identity, phase: .signedOut, failure: nil)
            return nil
        }
        let replacement = CredentialRecord(credential: credential)
        do {
            try store.write(replacement, for: identity)
        } catch {
            let failure = CredentialStoreError.sanitized(error)
            entries[identity] = Entry(generation: UUID(), phase: .failed, failure: .storage(failure), record: nil)
            publish(identity)
            throw AccountSessionError.storage(failure)
        }
        let generation = UUID()
        entries[identity] = Entry(generation: generation, phase: .requiresValidation, failure: nil, record: replacement)
        publish(identity)
        return AccountSessionToken(identity: identity, generation: generation)
    }

    public func snapshot(for identity: AccountIdentity) -> AccountSnapshot {
        guard let entry = entries[identity] else {
            return AccountSnapshot(identity: identity, generation: nil, phase: .signedOut)
        }
        return AccountSnapshot(identity: identity, generation: entry.generation,
                               phase: entry.phase, failure: entry.failure)
    }

    public func updates(for identity: AccountIdentity) -> AsyncStream<AccountSnapshot> {
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            observers[id] = Observer(identity: identity, continuation: continuation)
            continuation.yield(snapshot(for: identity))
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeObserver(id) }
            }
        }
    }

    public func beginLogin(for identity: AccountIdentity) throws -> AccountSessionToken {
        let generation = try clear(identity, phase: .signingIn, failure: nil)
        return AccountSessionToken(identity: identity, generation: generation)
    }

    @discardableResult
    public func completeLogin(
        _ token: AccountSessionToken, credential: AccountCredential
    ) throws -> AccountSnapshot {
        try require(token, phases: [.signingIn])
        let record = CredentialRecord(revision: token.generation, credential: credential)
        do {
            try store.write(record, for: token.identity)
        } catch {
            let failure = CredentialStoreError.sanitized(error)
            entries[token.identity] = Entry(generation: UUID(), phase: .failed, failure: .storage(failure), record: nil)
            publish(token.identity)
            throw AccountSessionError.storage(failure)
        }
        entries[token.identity]?.phase = .signedIn
        entries[token.identity]?.record = record
        entries[token.identity]?.failure = nil
        publish(token.identity)
        return snapshot(for: token.identity)
    }

    @discardableResult
    public func failLogin(
        _ token: AccountSessionToken, reason: AccountLoginFailure
    ) throws -> AccountSnapshot {
        try require(token, phases: [.signingIn])
        entries[token.identity] = Entry(generation: UUID(), phase: .failed, failure: .login(reason), record: nil)
        publish(token.identity)
        return snapshot(for: token.identity)
    }

    public func restore(for identity: AccountIdentity) throws -> AccountSessionToken? {
        if let entry = entries[identity] {
            guard entry.phase == .signedOut ||
                    (entry.phase == .failed && isStorageFailure(entry.failure)) else {
                throw AccountSessionError.invalidState
            }
        }
        let generation = UUID()
        entries[identity] = Entry(generation: generation, phase: .signedOut, failure: nil, record: nil)
        do {
            guard let record = try store.read(for: identity) else {
                publish(identity)
                return nil
            }
            entries[identity] = Entry(generation: generation, phase: .requiresValidation, failure: nil, record: record)
            publish(identity)
            return AccountSessionToken(identity: identity, generation: generation)
        } catch {
            let failure = CredentialStoreError.sanitized(error)
            entries[identity]?.phase = .failed
            entries[identity]?.failure = .storage(failure)
            publish(identity)
            throw AccountSessionError.storage(failure)
        }
    }

    public func credential(for token: AccountSessionToken) throws -> AccountCredential {
        try require(token, phases: [.signedIn])
        return try currentRecord(token).credential
    }

    public func credentialUpdateLease(
        for token: AccountSessionToken
    ) throws -> AccountCredentialUpdateLease {
        try require(token, phases: [.signedIn, .requiresValidation])
        let record = try currentRecord(token)
        guard let entry = entries[token.identity] else {
            throw AccountSessionError.staleSession
        }
        return AccountCredentialUpdateLease(token: token, phase: entry.phase, record: record)
    }

    @discardableResult
    public func commitCredentialUpdate(
        _ lease: AccountCredentialUpdateLease,
        credential: AccountCredential,
        validation: AccountCredentialUpdateValidation,
        preservingUnchangedRevision: Bool = false
    ) throws -> AccountSnapshot {
        try lease.withCommitAuthorization {
            let token = lease.token
            try require(token, phases: [.signedIn, .requiresValidation])
            guard entries[token.identity]?.phase == lease.phase else {
                throw AccountSessionError.invalidState
            }
            if validation == .preserveSignedIn, lease.phase != .signedIn {
                throw AccountSessionError.invalidState
            }
            guard entries[token.identity]?.credentialRevision == lease.revision.value else {
                throw AccountSessionError.storage(.conflict)
            }
            let observed = try currentRecord(token)
            if preservingUnchangedRevision, credential == observed.credential {
                entries[token.identity]?.phase = .signedIn
                entries[token.identity]?.failure = nil
                publish(token.identity)
                return snapshot(for: token.identity)
            }
            let replacement = CredentialRecord(credential: credential)
            do {
                try store.write(replacement, for: token.identity)
            } catch {
                let failure = CredentialStoreError.sanitized(error)
                entries[token.identity] = Entry(generation: UUID(), phase: .failed, failure: .storage(failure), record: nil)
                publish(token.identity)
                throw AccountSessionError.storage(failure)
            }
            entries[token.identity]?.phase = .signedIn
            entries[token.identity]?.record = replacement
            entries[token.identity]?.failure = nil
            publish(token.identity)
            return snapshot(for: token.identity)
        }
    }

    @discardableResult
    public func logout(identity: AccountIdentity) throws -> AccountSnapshot {
        try clear(identity, phase: .signedOut, failure: nil)
        return snapshot(for: identity)
    }

    @discardableResult
    public func logout(_ token: AccountSessionToken) throws -> AccountSnapshot {
        try require(token)
        return try logout(identity: token.identity)
    }

    @discardableResult
    public func invalidate(
        _ token: AccountSessionToken, reason: AccountInvalidationReason
    ) throws -> AccountSnapshot {
        try require(token, phases: [.signedIn, .requiresValidation])
        try clear(token.identity, phase: .invalidated, failure: .invalidated(reason))
        return snapshot(for: token.identity)
    }

    private func currentRecord(_ token: AccountSessionToken) throws -> CredentialRecord {
        guard let record = entries[token.identity]?.record else {
            throw AccountSessionError.credentialsUnavailable
        }
        return record
    }

    @discardableResult
    private func clear(_ identity: AccountIdentity, phase: AccountPhase, failure: AccountFailure?) throws -> UUID {
        let generation = UUID()
        entries[identity] = Entry(generation: generation, phase: phase, failure: failure, record: nil)
        do {
            try store.remove(for: identity)
        } catch {
            let failure = CredentialStoreError.sanitized(error)
            entries[identity]?.phase = .removalPending
            entries[identity]?.failure = .storage(failure)
            publish(identity)
            throw AccountSessionError.storage(failure)
        }
        publish(identity)
        return generation
    }

    private func require(_ token: AccountSessionToken, phases: [AccountPhase]? = nil) throws {
        guard let entry = entries[token.identity], entry.generation == token.generation else {
            throw AccountSessionError.staleSession
        }
        if let phases, !phases.contains(entry.phase) {
            throw AccountSessionError.invalidState
        }
    }

    private func isStorageFailure(_ failure: AccountFailure?) -> Bool {
        if case .storage? = failure { return true }
        return false
    }

    private func publish(_ identity: AccountIdentity) {
        let value = snapshot(for: identity)
        for observer in observers.values where observer.identity == identity {
            observer.continuation.yield(value)
        }
    }

    private func removeObserver(_ id: UUID) {
        observers.removeValue(forKey: id)
    }

    deinit {
        for observer in observers.values {
            observer.continuation.finish()
        }
    }
}
