import Foundation
import MeowContracts
import MeowStorage

enum FilmPlayerPreferencesRepository {
    private static let columns = [
        "engine", "default_rate", "hold_rate", "seek_step", "shows_control_center", "recognizes_chapters",
        "single_tap_center", "single_tap_sides", "double_tap_center", "double_tap_sides",
        "swipe_volume", "swipe_brightness", "swipe_seek", "swipe_episodes",
        "haptic_hold", "haptic_brightness", "haptic_volume", "haptic_seek",
    ]

    static func load(_ db: SQLiteConnection) throws -> FilmPlayerPreferences {
        guard let next: FilmPlayerPreferences = try db.queryOne(
            "SELECT engine, default_rate, hold_rate, seek_step, shows_control_center, recognizes_chapters, " +
            "single_tap_center, single_tap_sides, double_tap_center, double_tap_sides, " +
            "swipe_volume, swipe_brightness, swipe_seek, swipe_episodes, " +
            "haptic_hold, haptic_brightness, haptic_volume, haptic_seek " +
            "FROM ios_film_player_preferences WHERE singleton=1"
        , map: { cursor in
            guard let engine = PlaybackVideoEngine(rawValue: try cursor.text(0)),
                  let singleTapCenter = FilmPlayerTapAction(rawValue: try cursor.text(6)),
                  let singleTapSides = FilmPlayerTapAction(rawValue: try cursor.text(7)),
                  let doubleTapCenter = FilmPlayerTapAction(rawValue: try cursor.text(8)),
                  let doubleTapSides = FilmPlayerTapAction(rawValue: try cursor.text(9)) else {
                throw SQLiteStorageError.invalidValue
            }
            var prefs = FilmPlayerPreferences()
            prefs.engine = engine
            prefs.defaultRate = try cursor.double(1)
            prefs.holdRate = try cursor.double(2)
            prefs.seekStep = try cursor.double(3)
            prefs.showsControlCenter = try cursor.bool(4)
            prefs.recognizesChapters = try cursor.bool(5)
            prefs.singleTapCenter = singleTapCenter
            prefs.singleTapSides = singleTapSides
            prefs.doubleTapCenter = doubleTapCenter
            prefs.doubleTapSides = doubleTapSides
            prefs.swipeVolume = try cursor.bool(10)
            prefs.swipeBrightness = try cursor.bool(11)
            prefs.swipeSeek = try cursor.bool(12)
            prefs.swipeEpisodes = try cursor.bool(13)
            prefs.hapticHold = try cursor.bool(14)
            prefs.hapticBrightness = try cursor.bool(15)
            prefs.hapticVolume = try cursor.bool(16)
            prefs.hapticSeek = try cursor.bool(17)
            try prefs.normalize()
            return prefs
        }) else {
            return .init()
        }
        return next
    }

    static func save(_ value: FilmPlayerPreferences, _ db: SQLiteConnection) throws {
        let revision = try db.queryOne(
            "SELECT row_revision FROM ios_film_player_preferences WHERE singleton=1"
        ) { try $0.int64(0) } ?? 0
        guard revision < 9_007_199_254_740_991 else { throw SQLiteStorageError.conflict }
        let values: [SQLiteValue] = [
            .integer(1),
            .text(value.engine.rawValue),
            .real(value.defaultRate),
            .real(value.holdRate),
            .real(value.seekStep),
            .bool(value.showsControlCenter),
            .bool(value.recognizesChapters),
            .text(value.singleTapCenter.rawValue),
            .text(value.singleTapSides.rawValue),
            .text(value.doubleTapCenter.rawValue),
            .text(value.doubleTapSides.rawValue),
            .bool(value.swipeVolume),
            .bool(value.swipeBrightness),
            .bool(value.swipeSeek),
            .bool(value.swipeEpisodes),
            .bool(value.hapticHold),
            .bool(value.hapticBrightness),
            .bool(value.hapticVolume),
            .bool(value.hapticSeek),
            .integer(revision + 1),
        ]
        try db.upsert(
            "ios_film_player_preferences",
            columns: ["singleton"] + columns + ["row_revision"],
            values: values,
            key: ["singleton"]
        )
    }
}
