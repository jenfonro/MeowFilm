import Foundation

/// Local cache identity. Neither this value nor its fence
/// authorizes login, platform HTTP, membership, quality selection or playback.
/// The owner derives scope from the configured identity and the persisted
/// non-secret credential revision, never from credential bytes or a platform UID.
/// scope survives process restart; revision/fence do not. Do not persist the fence.
/// Recheck with the owner after suspended work, then use authorization.perform
/// for the final synchronous cache read/write/publication.
public struct MusicNativeCacheScope: Sendable, Equatable {
    public let scope: String
    public let revision: UUID
    public let authorization: MusicQueueAuthorization

    public init(scope: String, revision: UUID = UUID(),
                authorization: MusicQueueAuthorization = MusicQueueAuthorization()) {
        self.scope = scope; self.revision = revision; self.authorization = authorization
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.scope == rhs.scope && lhs.revision == rhs.revision && lhs.authorization === rhs.authorization
    }
}

/// Combines all configured platforms' locally stored revisions/absence markers.
/// Reads local storage only, including cold records still requiring validation.
/// Such a record remains unvalidated. A pending imported credential is rejected;
/// a preview cannot supply a record. No provider/capabilities request is made.
/// Protection, logout/replacement and close revoke issued fences synchronously
/// before the owner's first suspension. External writes are checked on every
/// use and through the injected coordinator's per-identity change stream.
public protocol MusicNativeCacheScopeProviding: Sendable {
    func nativeCacheScope() async throws -> MusicNativeCacheScope
    func checkNativeCacheScope(_ scope: MusicNativeCacheScope) async throws
}

/// Optional product-injected lifecycle callback on NativeMusicAccountService.
/// The owner synchronously revokes affected fences before enqueueing callbacks,
/// serializes callbacks, and denies new aggregate scopes until they finish.
/// No secrets, account IDs or URLs are carried. The callback must not call back
/// into the same account owner (its lifecycle barrier would deadlock).
public enum MusicNativeCacheLifecycleEvent: Sendable, Equatable {
    /// Logout/login replacement, credential revision change
    /// or observed external replacement. Clear official complete files only.
    case accountChanged
    /// Protection generation changed. Cancel pending work, retain disk bytes.
    case accessChanged
    /// Owner shuts down. Cancel pending work, retain disk bytes across restart.
    case closed
}

/// A media failure is not an account failure. The caller reports the exact
/// failed candidate and may refresh once per playback attempt, not in a loop.
/// The owner rechecks the live account/quality grant, conditionally evicts that
/// URL, and makes at most one provider request. No source/account substitution.
public protocol MusicNativeAccountURLRefreshing: Sendable {
    func nativeRefreshAudio(platform: MusicAccountPlatform, nativeID: String,
                            selection: MusicPlaybackSelection, rejectedURL: URL) async throws -> MusicNativeAudio
}
