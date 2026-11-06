import Foundation

/// Product-owned, not a singleton. External account adapters perform network login and
/// validation, then submit results with the original token. Never hand this whole object
/// or its store to untrusted scripts; their capability adapter must bind an explicit scope.
///
/// Storage calls are synchronous and there is NO await between checking a token, changing
/// credentials and publishing state. Actor serialization therefore prevents a delayed
/// callback from committing across a newer login/logout/invalidation in this coordinator.
/// This is not a cross-process transaction or remote-session revocation protocol.
public actor AccountSessionCoordinator {
    private struct Entry {
        var generation: UUID
        var phase: AccountPhase
        var failure: AccountFailure?
        var credentialRevision: UUID?
    }

    private struct Observer: Sendable {
        let identity: AccountIdentity
        let continuation: AsyncStream<AccountSnapshot>.Continuation
    }

    private let store: any CredentialStore
    private var entries: [AccountIdentity: Entry] = [:]
    private var observers: [UUID: Observer] = [:]

    public init(store: any CredentialStore) {
        self.store = store
    }

    public func snapshot(for identity: AccountIdentity) -> AccountSnapshot {
        guard let entry = entries[identity] else {
            return AccountSnapshot(identity: identity, generation: nil, phase: .signedOut)
        }
        return AccountSnapshot(identity: identity, generation: entry.generation,
                               phase: entry.phase, failure: entry.failure)
    }

    /// Per-identity state stream, not a global event bus. Slow consumers see the latest
    /// state (buffer 1), not a guaranteed audit trail. Cancellation releases the observer.
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

    /// An explicit new login revokes the old generation and removes its local credentials.
    /// If deletion fails, no token is issued and state is removalPending (fail closed).
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
            // Even an injected backend that wrote before throwing must not leave an
            // accepted session. Rotate first and attempt scoped cleanup once.
            do {
                try clear(token.identity, phase: .failed, failure: .storage(failure))
            } catch {
                // clear already published removalPending; preserve that stronger warning.
            }
            throw AccountSessionError.storage(failure)
        }
        entries[token.identity]?.phase = .signedIn
        entries[token.identity]?.credentialRevision = record.revision
        entries[token.identity]?.failure = nil
        publish(token.identity)
        return snapshot(for: token.identity)
    }

    @discardableResult
    public func failLogin(
        _ token: AccountSessionToken, reason: AccountLoginFailure
    ) throws -> AccountSnapshot {
        try require(token, phases: [.signingIn])
        entries[token.identity] = Entry(generation: UUID(), phase: .failed,
                                        failure: .login(reason), credentialRevision: nil)
        publish(token.identity)
        return snapshot(for: token.identity)
    }

    /// Loading bytes does not establish platform validity. Only a fresh/retryable idle
    /// scope can restore; this cannot overwrite an in-flight login or a live session.
    public func restore(for identity: AccountIdentity) throws -> AccountSessionToken? {
        if let entry = entries[identity] {
            guard entry.phase == .signedOut ||
                    (entry.phase == .failed && isStorageFailure(entry.failure)) else {
                throw AccountSessionError.invalidState
            }
        }
        let generation = UUID()
        entries[identity] = Entry(generation: generation, phase: .signedOut,
                                  failure: nil, credentialRevision: nil)
        do {
            guard let record = try store.read(for: identity) else {
                publish(identity)
                return nil
            }
            entries[identity] = Entry(generation: generation, phase: .requiresValidation,
                                      failure: nil, credentialRevision: record.revision)
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

    /// Explicit validation access, not a normal authenticated-business credential read.
    public func credentialForValidation(_ token: AccountSessionToken) throws -> AccountCredential {
        try require(token, phases: [.requiresValidation])
        return try checkedRecord(token).credential
    }

    /// Called only after the platform adapter actually validates the restored session.
    @discardableResult
    public func completeValidation(_ token: AccountSessionToken) throws -> AccountSnapshot {
        try require(token, phases: [.requiresValidation])
        _ = try checkedRecord(token)
        entries[token.identity]?.phase = .signedIn
        entries[token.identity]?.failure = nil
        publish(token.identity)
        return snapshot(for: token.identity)
    }

    public func credential(for token: AccountSessionToken) throws -> AccountCredential {
        try require(token, phases: [.signedIn])
        return try checkedRecord(token).credential
    }

    /// Capture before the adapter starts its asynchronous request. The returned bytes
    /// and opaque revision belong to this exact token and phase, never a newer login.
    /// Reading a requiresValidation credential here does not accept that session.
    public func credentialUpdateLease(
        for token: AccountSessionToken
    ) throws -> AccountCredentialUpdateLease {
        try require(token, phases: [.signedIn, .requiresValidation])
        let record = try checkedRecord(token)
        guard let entry = entries[token.identity] else {
            throw AccountSessionError.staleSession
        }
        return AccountCredentialUpdateLease(token: token, phase: entry.phase, record: record)
    }

    /// A bounded, single-use conditional replacement, not a new login or remote request.
    /// A successful update retains the session token and identity, but rotates the
    /// persistence revision even when the replacement bytes happen to be unchanged,
    /// unless preservingUnchangedRevision explicitly opts into a no-write verified
    /// read. That opt-in still consumes the lease and performs every binding,
    /// phase and observed-record check; changed credentials always rotate.
    /// The adapter must assert same-account validation explicitly to promote a restored
    /// session. External protection/epoch changes must synchronously revoke the lease.
    ///
    /// NO await in this operation: actor serialization and the lease's local permission
    /// lock cover all checks, at most one write, confirmation read, and state publication.
    /// This detects observed external changes but is not a store/cross-process CAS.
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
            switch validation {
            case .preserveSignedIn:
                guard lease.phase == .signedIn else { throw AccountSessionError.invalidState }
            case .verified:
                break
            }
            guard entries[token.identity]?.credentialRevision == lease.revision.value else {
                // A successful refresh has already advanced this same session. Do not
                // revoke it or let an older response overwrite its new credential.
                throw AccountSessionError.storage(.conflict)
            }
            let observed = try checkedRecord(token)
            guard observed == lease.originalRecord else {
                // Also reject an external writer that reused the revision with new bytes.
                revokeAccess(token.identity, failure: .credentialsChanged)
                throw AccountSessionError.credentialsUnavailable
            }

            if preservingUnchangedRevision, credential == observed.credential {
                entries[token.identity]?.phase = .signedIn
                entries[token.identity]?.failure = nil
                publish(token.identity)
                return snapshot(for: token.identity)
            }
            let replacement = CredentialRecord(credential: credential)
            do {
                try store.write(replacement, for: token.identity)
                guard try store.read(for: token.identity) == replacement else {
                    throw CredentialStoreError.conflict
                }
            } catch {
                let failure = CredentialStoreError.sanitized(error)
                // The backend may have persisted before throwing, or another writer may
                // now own this record. Revoke ALL old permits without deleting, retrying,
                // or guessing which bytes reached disk. Explicit restore must read and
                // validate the actual record using a fresh session generation.
                entries[token.identity] = Entry(generation: UUID(), phase: .failed,
                                                 failure: .storage(failure), credentialRevision: nil)
                publish(token.identity)
                throw AccountSessionError.storage(failure)
            }
            entries[token.identity]?.phase = .signedIn
            entries[token.identity]?.credentialRevision = replacement.revision
            entries[token.identity]?.failure = nil
            publish(token.identity)
            return snapshot(for: token.identity)
        }
    }

    /// Immediate local user intent. It does not contact/revoke a remote platform session.
    /// Repeating this method retries a removalPending deletion. Durable success is returned
    /// only after deletion succeeds; old callbacks are rejected even if deletion fails.
    /// Adapters handling asynchronous callbacks must use the token-bound overload below.
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

    private func checkedRecord(_ token: AccountSessionToken) throws -> CredentialRecord {
        let record: CredentialRecord?
        do {
            record = try store.read(for: token.identity)
        } catch {
            let failure = CredentialStoreError.sanitized(error)
            // A temporary Keychain lock/permission error denies access, not an automatic
            // network logout or a fallback to cached secret bytes.
            entries[token.identity]?.failure = .storage(failure)
            publish(token.identity)
            throw AccountSessionError.storage(failure)
        }
        guard let record else {
            revokeAccess(token.identity, failure: .credentialsMissing)
            throw AccountSessionError.credentialsUnavailable
        }
        guard record.revision == entries[token.identity]?.credentialRevision else {
            // Detect an unsupported external writer without deleting its replacement.
            revokeAccess(token.identity, failure: .credentialsChanged)
            throw AccountSessionError.credentialsUnavailable
        }
        if entries[token.identity]?.failure != nil {
            entries[token.identity]?.failure = nil
            publish(token.identity)
        }
        return record
    }

    @discardableResult
    private func clear(_ identity: AccountIdentity, phase: AccountPhase, failure: AccountFailure?) throws -> UUID {
        let generation = UUID()
        entries[identity] = Entry(generation: generation, phase: phase, failure: failure, credentialRevision: nil)
        do {
            try store.remove(for: identity)
        } catch {
            let failure = CredentialStoreError.sanitized(error)
            entries[identity]?.phase = .removalPending
            entries[identity]?.failure = .storage(failure)
            publish(identity)
            // The caller must not report a completed durable logout. A process restart
            // can still find the undeleted record; restore will require fresh validation.
            throw AccountSessionError.storage(failure)
        }
        publish(identity)
        return generation
    }

    private func revokeAccess(_ identity: AccountIdentity, failure: AccountFailure) {
        entries[identity] = Entry(generation: UUID(), phase: .invalidated,
                                  failure: failure, credentialRevision: nil)
        publish(identity)
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
