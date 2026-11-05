import Foundation
import MeowMusicFeature

extension NativeMusicService: MusicImportHistoryProviding {
    public func importHistory() async throws -> MusicImportHistorySnapshot {
        let document = try read(), receipt = document.library["receipt"]
        if receipt == .null {
            let rolledBack = document.library["rolledBack"]
            if rolledBack == .null { return .empty }
            try musicRequire(musicHex(rolledBack.string), .corruptDocument)
            return .rolledBack(batchID: rolledBack.string)
        }
        let unchanged = receipt["afterHash"].string == document.library["library"].stable.digest &&
            receipt["appliedRevision"] == document.library["revision"]
        return .imported(batchID: receipt["batchId"].string,
                         appliedAt: Date(timeIntervalSince1970: receipt["appliedAt"].number / 1000),
                         canRollback: unchanged)
    }

    public func rollbackImportedLibrary(_ authorization: MusicImportRollbackAuthorization) async throws {
        try check()
        try musicRequire(musicHex(authorization.batchID), .invalidRequest)
        try authorization.perform { try performImportRollback(batchId: authorization.batchID) }
    }
}
