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
        try receipt.requireFields(["batchId", "sourceId", "legacyOwnerId", "appliedAt", "appliedRevision", "afterHash"],
                                  required: ["batchId", "sourceId", "legacyOwnerId", "appliedAt", "appliedRevision", "afterHash"])
        try musicRequire(musicHex(receipt["batchId"].string) && musicHex(receipt["afterHash"].string) &&
            receipt["appliedRevision"].isInteger && receipt["appliedRevision"].int > 0 &&
            receipt["appliedAt"].isNumber && receipt["appliedAt"].number.isFinite &&
            receipt["appliedAt"].number >= 0, .corruptDocument)
        _ = try musicText(receipt["sourceId"].string)
        _ = try musicText(receipt["legacyOwnerId"].string)
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
