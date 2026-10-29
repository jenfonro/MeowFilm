import Foundation
import MeowAccounts
import MeowMusicFeature

/// Account secrets stay inside the native adapter. The feature receives only
/// the revocable fence; the record revision is never a durable user identity.
struct MusicAccountQueueBinding: Sendable {
    let lease: MusicAccountQueueLease
    let credential: AccountCredentialUpdateLease
    let epoch: UUID
    let revision: Int
    let nativeID: String

    func revoke() { lease.revoke(); credential.revoke() }
}
