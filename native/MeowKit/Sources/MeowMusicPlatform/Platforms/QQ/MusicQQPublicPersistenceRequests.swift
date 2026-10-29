import Foundation
import MeowMusicFeature

extension QQMusicAccountAdapter {
    /// Same fixed track RPC and identity validation as the account path, but
    /// carries the anonymous admission explicitly through every HTTP stage.
    /// It never supplies credentials or calls the account service.
    func publicTrack(_ mid: String, access: UUID) async throws -> [String: Any] {
        _ = try MusicQQWire.id(mid, mid: true)
        let data = try await rpc("music.trackInfo.UniformRuleCtrl", "CgiGetTrackInfo",
            ["ctx": 0, "client": 1, "types": [0], "modify_stamp": [0], "mids": [mid]],
            publicAccess: access)
        let rows = try array(data["tracks"], maximum: 1)
        guard rows.count == 1, try MusicQQWire.object(rows[0])["mid"] as? String == mid else {
            throw MusicServiceError.targetChanged
        }
        _ = try song(rows[0])
        return try MusicQQWire.object(rows[0])
    }
}
