import Foundation
import MeowAccounts
import MeowMusicFeature

struct MusicAccountImportBatch: Sendable {
    struct Item: Sendable {
        let entry: MusicAccountTransferEntry
        let baseline: AccountReplacementLease?
        let epoch: UUID?
    }
    let epoch: UUID
    let items: [Item]
}
