import Foundation

/// A revocable live-request/cache scope, not an account ID or a credential.
/// This binding is never persisted in the last-success display snapshot.
/// revision is owner-issued and changes on access/account invalidation.
/// Final synchronous publication/persistence must use authorization.perform.
/// Scope survives cold restore, protection changes and validation when the
/// configured identity and persisted credential revision are unchanged. A real
/// replacement or logout changes scope; no credential bytes/hash form the scope.
/// An ordinary payload network failure changes neither scope nor login state.
public struct MusicNativeHomeBinding: Sendable, Equatable {
    public let scope: String
    public let revision: UUID
    public let loggedIn: Bool
    public let authorization: MusicQueueAuthorization

    public init(scope: String, revision: UUID, loggedIn: Bool,
                authorization: MusicQueueAuthorization = MusicQueueAuthorization()) {
        self.scope = scope; self.revision = revision; self.loggedIn = loggedIn
        self.authorization = authorization
    }
    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.scope == rhs.scope && lhs.revision == rhs.revision && lhs.loggedIn == rhs.loggedIn &&
            lhs.authorization === rhs.authorization
    }
}

/// Local account observation scope, separate from the live request binding.
/// Acquisition/checking MUST NOT verify an account or contact a provider.
/// A local unvalidated record cannot set loggedIn, mint a request binding, or
/// authorize a displayed action. Display snapshots are not scoped by this value.
/// Guest observation also has an owner-issued, revocable scope.
public protocol MusicNativeHomeDisplayProviding: MusicNativeHomeProviding {
    func nativeHomeDisplayScope() async throws -> MusicNativeCacheScope
    func checkNativeHomeDisplayScope(_ scope: MusicNativeCacheScope) async throws
}

/// Only the three continuation fields used by the reference home/featured API.
/// No URL, method, headers, arbitrary JSON object or account field is accepted.
public struct MusicNativeHomeContinuation: Sendable, Equatable {
    public let cursor: String
    public let blockCodeOrderList: String?
    public let loadedPositionCodes: [String]

    public init(cursor: String, blockCodeOrderList: String? = nil,
                loadedPositionCodes: [String] = []) throws {
        self.cursor = cursor
        self.blockCodeOrderList = blockCodeOrderList
        self.loadedPositionCodes = loadedPositionCodes
    }
}

public enum MusicNativeHomeRequest: Sendable, Equatable {
    case home(continuation: MusicNativeHomeContinuation? = nil)
    case featured(continuation: MusicNativeHomeContinuation? = nil)
    /// nil is the first request; otherwise the platform continuation timestamp.
    case artistActivity(before: String? = nil)
    /// Account-bound VIP recommendations.
    /// A display result is not playback or membership authorization.
    case vipSongs
    /// Node performs the platform's member-detail batches.
    case songMetadata(ids: [String])
    case playlistMetadata(ids: [String])
    /// Resolve one displayed seed from the current platform account's liked playlist.
    /// This is a read recipe, not a heartbeat recommendation or synchronization.
    case heartbeatSeed
}

/// Optional, injected capability of the existing native account owner.
/// Binding acquisition may restore/validate an existing account. Only an
/// authentication denial establishes a guest binding; storage, cancellation,
/// access, verification, busy and network errors are not anonymous success.
///
/// Guest home/featured and hydration use the existing anonymous browse provider.
/// This port rejects those requests with authRequired, while guest artistActivity returns
/// the reference's empty authentication-required capability payload.
///
/// Payload is bounded, credential-free upstream JSON, not an HTTP response or
/// executable source. Consumers normalize it and recheck binding before applying
/// results after any further suspended work (metadata, persistence, etc.).
public protocol MusicNativeHomeProviding: Sendable {
    func nativeHomeBinding() async throws -> MusicNativeHomeBinding
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) async throws -> Data
    func checkNativeHomeBinding(_ binding: MusicNativeHomeBinding) async throws
    /// Replays an initial invalidation; buffer 1 coalesces changes, not an
    /// audit log. Do not skip the first element: it can already be a real change.
    /// Reacquire current binding on every notification, including after a
    /// failed/busy reload. A scope change retires requests, not the persisted
    /// display snapshot. A delayed notification does not invalidate a newer binding.
    /// Cancellation removes that subscription; close finishes all subscriptions.
    /// No global notification center is involved.
    func nativeHomeChanges() async -> AsyncStream<Void>
}
