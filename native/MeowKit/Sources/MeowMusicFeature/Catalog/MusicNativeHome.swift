import Foundation

/// A revocable display/cache scope, not an account ID or a credential.
/// Persist scope with a home snapshot, but obtain a fresh binding before using
/// it. revision is owner-issued and changes on access/account invalidation.
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

/// Stored-snapshot display scope, separate from the live request binding.
/// Acquisition/checking MUST NOT verify an account or contact a provider.
/// A local unvalidated record may select its previously saved snapshot but
/// cannot set loggedIn, mint a request binding, or authorize a displayed action.
/// Guest display also has an owner-issued, revocable scope.
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
        guard !cursor.isEmpty, cursor.utf8.count <= 20,
              cursor.utf8.allSatisfy({ (48...57).contains($0) }),
              let number = UInt64(cursor), number <= 9_007_199_254_740_991,
              (blockCodeOrderList?.utf8.count ?? 0) <= 16_000,
              loadedPositionCodes.count <= 300,
              loadedPositionCodes.allSatisfy({ !$0.isEmpty && $0.utf8.count <= 200 &&
                  !$0.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) }),
              try JSONEncoder().encode(loadedPositionCodes).count <= 16_000 else {
            throw MusicServiceError.invalidRequest
        }
        if let blockCodeOrderList,
           blockCodeOrderList.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) {
            throw MusicServiceError.invalidRequest
        }
        self.cursor = cursor
        self.blockCodeOrderList = blockCodeOrderList
        self.loadedPositionCodes = loadedPositionCodes
    }
}

public enum MusicNativeHomeRequest: Sendable, Equatable {
    case home(continuation: MusicNativeHomeContinuation? = nil)
    case featured(continuation: MusicNativeHomeContinuation? = nil)
    /// nil is the first request; otherwise an exact 1...18 digit timestamp.
    case artistActivity(before: String? = nil)
    /// Account-bound VIP recommendations; the owner checks current membership.
    /// A display result is not playback or membership authorization.
    case vipSongs
    /// 1...100 exact positive decimal IDs, each at most 18 digits.
    case songMetadata(ids: [String])
    case playlistMetadata(ids: [String])
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
    /// failed/busy reload; only an observed scope change retires a persisted
    /// snapshot. A delayed notification does not invalidate a newer binding.
    /// Cancellation removes that subscription; close finishes all subscriptions.
    /// No global notification center is involved.
    func nativeHomeChanges() async -> AsyncStream<Void>
}
