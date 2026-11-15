import Foundation
import MeowMusicFeature
import MeowStorage

/// Disposable exact-recording metadata. No URL, source credentials, account
/// UID or trial offsets. A cache hit still requires a live local-scope fence.
struct MusicAccountChorusCache {
    struct Key: Codable, Equatable {
        let scope: String
        let platform: String
        let nativeID: String
        let duration: Double
    }
    struct Entry: Codable { let key: Key; let point: MusicChorusPoint; let expires: Date }
    private let store: DocumentStore?
    private var loaded = false
    private var entries: [Entry] = []
    init(store: DocumentStore?) { self.store = store }
    mutating func value(_ key: Key, actualDuration: Double, at now: Date) -> MusicChorusPoint? {
        load(at: now)
        return entries.first { $0.key == key &&
            $0.point.fits(recordingDuration: key.duration, actualDuration: actualDuration) }?.point
    }
    mutating func put(_ point: MusicChorusPoint, key: Key, at now: Date) {
        load(at: now)
        entries.removeAll { $0.key == key }
        entries.append(.init(key: key, point: point, expires: now.addingTimeInterval(30 * 86_400)))
        entries = Array(entries.suffix(100))
        if let data = try? JSONEncoder().encode(entries), data.count <= 128 * 1024 {
            try? store?.write(data, key: "music.platform-chorus")
        }
    }
    private mutating func load(at now: Date) {
        if !loaded {
            loaded = true
            if let data = try? store?.read(key: "music.platform-chorus"), data.count <= 128 * 1024,
               let values = try? JSONDecoder().decode([Entry].self, from: data), values.count <= 100 {
                entries = values
            }
        }
        entries.removeAll { $0.expires <= now || $0.expires > now.addingTimeInterval(30 * 86_400) ||
            !$0.point.fits(recordingDuration: $0.key.duration, actualDuration: $0.key.duration) }
    }
}
