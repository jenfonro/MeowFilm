import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func validateDelivery(_ audio: MusicNativeAudio, song: MusicSongRecord, playback: MusicPlaybackSettingsState, automaticQuality: Bool = true) throws {
        if let duration = audio.duration {
            let expected = song.effectiveDuration
            try musicRequire(MusicMediaDuration.matches(expected: expected, actual: duration), .invalidMediaCandidate)
        }
        if automaticQuality, let delivery = audio.delivery {
            try musicRequire(!Self.officialQualities([delivery.quality], playback).isEmpty, .invalidMediaCandidate)
        }
    }
}
