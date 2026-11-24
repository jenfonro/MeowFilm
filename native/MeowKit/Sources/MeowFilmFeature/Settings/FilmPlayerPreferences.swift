import Foundation
import Combine
import MeowContracts
import MeowDebug
import MeowStorage

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
    public var engine: PlaybackVideoEngine = .native
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
        guard Self.defaultRates(for: .native).contains(defaultRate),
              Self.holdRates(for: .native).contains(holdRate),
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
    @Published public private(set) var preferences = FilmPlayerPreferences()
    @Published public private(set) var persistenceError: String?
    private var database: SQLiteDatabase?
    private var epoch = UUID()
    private var closed = false

    /// An unbound owner is ephemeral. The product binds its one database before
    /// publishing settings or admitting playback.
    public init(database: SQLiteDatabase? = nil) {
        self.database = database
    }
    public func bind(database: SQLiteDatabase) async {
        guard self.database == nil, !closed else { return }
        self.database = database
        await reload()
    }

    public func reload() async {
        guard !closed, let database else { return }
        let ticket = UUID(); epoch = ticket
        do {
            let next = try await database.read { try FilmPlayerPreferencesRepository.load($0) }
            guard epoch == ticket, !closed else { return }
            if preferences != next { preferences = next }; persistenceError = nil
        } catch {
            if epoch == ticket, !closed { persistenceError = "播放器偏好读取失败" }
        }
    }

    @discardableResult
    public func set<Value>(_ keyPath: WritableKeyPath<FilmPlayerPreferences, Value>, _ value: Value) async -> Bool {
        guard !closed else { return false }
        let ticket = UUID(); epoch = ticket
        do {
            let next: FilmPlayerPreferences
            if let database {
                next = try await database.transaction { db in
                    var updated = try FilmPlayerPreferencesRepository.load(db)
                    updated[keyPath: keyPath] = value
                    try updated.normalize()
                    try FilmPlayerPreferencesRepository.save(updated, db)
                    return updated
                }
            } else {
                var local = preferences; local[keyPath: keyPath] = value; try local.normalize(); next = local
            }
            if epoch == ticket, !closed { if preferences != next { preferences = next }; persistenceError = nil }
            return true
        } catch {
            if epoch == ticket, !closed { persistenceError = "播放器偏好保存失败" }
            return false
        }
    }
    public func close() async {
        closed = true; epoch = UUID()
        do { try await database?.flush() }
        catch { DebugRuntime.logError(.storage, sub: "film.player-preferences", event: "flush.error", error: error) }
    }
}
