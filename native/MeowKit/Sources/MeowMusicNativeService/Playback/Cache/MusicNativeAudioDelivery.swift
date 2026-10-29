import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func validateDelivery(_ delivery: MusicAudioDelivery?, song: MJ, playback: MJ) throws {
        guard let delivery else { return }
        try musicRequire(!Self.officialQualities([delivery.quality], playback).isEmpty, .qualityDisabled)
        if let duration = delivery.duration {
            let expected = song["duration"].number
            try musicRequire(duration.isFinite && duration > 0 && (expected <= 0 ||
                abs(duration - expected) <= max(3, min(duration, expected) * 0.02)), .invalidMediaCandidate)
        }
    }
}
