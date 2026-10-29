import Foundation

/// Transport volume belongs to a product's playback owner, not an account,
/// media URL or presentation. Playback speed deliberately stays session-only.
@MainActor public protocol PlaybackVolumeStoring: AnyObject {
    func volume(for owner: String) -> Double
    func saveVolume(_ volume: Double, for owner: String)
}

/// Product-injected, owner-scoped native volume preferences.
@MainActor public final class PlaybackVolumePreferences: PlaybackVolumeStoring {
    public static let storageKey = "playback.volume"
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    public func volume(for owner: String) -> Double {
        guard let values = defaults.dictionary(forKey: Self.storageKey),
              let number = values[owner] as? NSNumber,
              String(cString: number.objCType) != "c" else { return 1 }
        let value = number.doubleValue
        return value.isFinite && (0...1).contains(value) ? value : 1
    }

    public func saveVolume(_ volume: Double, for owner: String) {
        guard !owner.isEmpty, owner.utf8.count <= 128, volume.isFinite, (0...1).contains(volume) else { return }
        var values = defaults.dictionary(forKey: Self.storageKey) ?? [:]
        values[owner] = volume
        defaults.set(values, forKey: Self.storageKey)
    }
}
