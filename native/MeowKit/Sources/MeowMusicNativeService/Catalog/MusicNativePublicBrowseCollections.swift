import Foundation
import MeowMusicFeature

struct MusicNativePublicBrowseDetailPage: Sendable {
    let collection: MusicPublicBrowseCollection
    let tracks: [MusicNativeTrackData]
    let sourcePositions: [Int?]
    let total: Int
    let continuation: MJ?
    let consumed: Int
    var notice: String? = nil
}
