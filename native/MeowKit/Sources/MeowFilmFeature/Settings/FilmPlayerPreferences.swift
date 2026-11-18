import Foundation
import Combine
import MeowContracts

public enum FilmPlayerTapAction: String, Codable, Sendable, CaseIterable {
    case none, toggleControls, playPause, seek

    public var title: String {
        switch self {
        case .none: return "无操作"
        case .toggleControls: return "显示/隐藏播放栏"
        case .playPause: return "播放/暂停"
        case .seek: return "快进/快退"
        }
    }
}

/// Device-local playback controls, separate from Node-owned matching preferences.
public struct FilmPlayerPreferences: Codable, Equatable, Sendable {
    public var engine: PlaybackVideoEngine = .ksPlayer
    public var defaultRate: Double = 1
    public var holdRate: Double = 2
    public var seekStep: Double = 15
    public var showsControlCenter = true
    public var recognizesChapters = true
    public var singleTapCenter: FilmPlayerTapAction = .toggleControls
    public var singleTapSides: FilmPlayerTapAction = .toggleControls
    public var doubleTapCenter: FilmPlayerTapAction = .playPause
    public var doubleTapSides: FilmPlayerTapAction = .playPause
    public var swipeVolume = true
    public var swipeBrightness = true
    public var swipeSeek = true
    public var swipeEpisodes = true
    public var hapticHold = true
    public var hapticBrightness = true
    public var hapticVolume = true
    public var hapticSeek = true

    public init() {}

    public static func defaultRates(for engine: PlaybackVideoEngine) -> [Double] {
        [1, 1.25, 1.5, 2, 3].filter { $0 <= engine.maximumRate }
    }

    public static func holdRates(for engine: PlaybackVideoEngine) -> [Double] {
        [2, 3, 4, 6, 8].filter { $0 <= engine.maximumRate }
    }

    public static let seekSteps: [Double] = [5, 10, 15, 20, 30, 60]

    /// Validate product choices before applying the selected engine's rate limit.
    /// Switching back restores choices, not the former high-rate values.
    public mutating func normalize() throws {
        guard Self.defaultRates(for: .ksPlayer).contains(defaultRate),
              Self.holdRates(for: .ksPlayer).contains(holdRate),
              Self.seekSteps.contains(seekStep),
              singleTapCenter != .seek,
              doubleTapCenter == .none || doubleTapCenter == .playPause,
              doubleTapSides != .toggleControls else {
            throw FilmPlayerPreferencesError.invalidPreferences
        }
        defaultRate = min(defaultRate, engine.maximumRate)
        holdRate = min(holdRate, engine.maximumRate)
    }

    func hasSameGestureSettings(as other: Self) -> Bool {
        holdRate == other.holdRate && seekStep == other.seekStep &&
            singleTapCenter == other.singleTapCenter && singleTapSides == other.singleTapSides &&
            doubleTapCenter == other.doubleTapCenter && doubleTapSides == other.doubleTapSides &&
            swipeVolume == other.swipeVolume && swipeBrightness == other.swipeBrightness &&
            swipeSeek == other.swipeSeek && swipeEpisodes == other.swipeEpisodes &&
            hapticHold == other.hapticHold && hapticBrightness == other.hapticBrightness &&
            hapticVolume == other.hapticVolume && hapticSeek == other.hapticSeek
    }
}

private enum FilmPlayerPreferencesError: LocalizedError {
    case invalidPreferences
    var errorDescription: String? { "播放器偏好无效" }
}

/// App composition shares this owner with settings and the film player.
@MainActor
public final class FilmPlayerPreferencesStore: ObservableObject {
    public static let preferencesKey = "film.player.preferences.v1"
    @Published public private(set) var preferences = FilmPlayerPreferences()
    @Published public private(set) var persistenceError: String?
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        reload()
    }

    public func reload() {
        persistenceError = nil
        guard let raw = defaults.object(forKey: Self.preferencesKey) else {
            if preferences != .init() { preferences = .init() }
            return
        }
        guard let data = raw as? Data,
              var next = try? JSONDecoder().decode(FilmPlayerPreferences.self, from: data) else {
            persistenceError = "播放器偏好读取失败"
            return
        }
        let saved = next
        do { try next.normalize() }
        catch {
            persistenceError = "播放器偏好读取失败"
            return
        }
        if next != saved { _ = save(next) }
        else if preferences != next { preferences = next }
    }

    @discardableResult
    public func set<Value>(_ keyPath: WritableKeyPath<FilmPlayerPreferences, Value>, _ value: Value) -> Bool {
        // Rebase the field edit without overwriting another owner's unrelated edit.
        reload()
        guard persistenceError == nil else { return false }
        var next = preferences
        next[keyPath: keyPath] = value
        do { try next.normalize() }
        catch {
            persistenceError = error.localizedDescription
            return false
        }
        return save(next)
    }

    @discardableResult
    private func save(_ next: FilmPlayerPreferences) -> Bool {
        do {
            let data = try JSONEncoder().encode(next)
            defaults.set(data, forKey: Self.preferencesKey)
            if preferences != next { preferences = next }
            persistenceError = nil
            return true
        } catch {
            persistenceError = "播放器偏好保存失败"
            return false
        }
    }
}
