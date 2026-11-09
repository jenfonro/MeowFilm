import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func validateDelivery(_ audio: MusicNativeAudio, song: MJ, playback: MJ, automaticQuality: Bool = true) throws {
        if let duration = audio.duration {
            let expected = song["duration"].number
            try musicRequire(MusicMediaDuration.matches(expected: expected, actual: duration), .invalidMediaCandidate)
        }
        if automaticQuality, let delivery = audio.delivery {
            // An upstream downgrade to a disabled tier is bad media, not a
            // change to the requested tier's admission. Other peers may work.
            try musicRequire(!Self.officialQualities([delivery.quality], playback).isEmpty, .invalidMediaCandidate)
        }
    }
}
