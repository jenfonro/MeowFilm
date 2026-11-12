import Foundation
import MeowContracts
import MeowMusicFeature

/// In-memory media for selection tests; real byte/EOF semantics are exercised by
/// NativeMusicAudioFileCacheTests, not by the selection fixtures.
final class MusicTestByteSource: MusicAudioFilePlayback, @unchecked Sendable {
    let id = UUID()
    var progress: PlaybackCacheProgress { .init(received: 32, length: 32) }
    func information() async throws -> PlaybackByteInformation { .init(length: 32, mime: "audio/wav") }
    func read(offset: Int64, count: Int) async throws -> Data { Data(repeating: 0, count: min(count, max(0, 32 - Int(offset)))) }
    func updates() -> AsyncStream<PlaybackCacheProgress> { AsyncStream { $0.yield(progress); $0.finish() } }
    func confirmPlayback(at date: Date) {}
    func result() async -> MusicAudioFileCacheVersion? { nil }
    func retire() {}
}
