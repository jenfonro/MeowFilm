import Foundation
import MeowMusicFeature
import MeowRuntime

struct MusicNativePublicBrowseDetailPage: Sendable {
    let collection: MusicPublicBrowseCollection
    let tracks: [MusicNativeTrackData]
    let sourcePositions: [Int?]
    let total: Int
    let continuation: RuntimeValue?
    let consumed: Int
    var notice: String? = nil
}
