import Foundation
import MeowMusicFeature
import MeowJSON

enum MusicCredentialImportJSON {
    static func object(_ bytes: Data) throws -> [String: Any] {
        guard bytes.count <= MusicCredentialImportInput.maximumBytes else {
            throw MusicCredentialImportError.sizeLimit
        }
        do {
            return try JSONObject.decode(bytes)
        } catch { throw MusicCredentialImportError.invalidFormat }
    }
}
