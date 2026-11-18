import Foundation
import XCTest
import Combine
@testable import MeowFilmFeature

@MainActor
final class FilmPlayerPreferencesTests: XCTestCase {
    func testReloadAndSameValueDoNotPublishDuplicatePreferences() {
        isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            var values: [FilmPlayerPreferences] = []
            let observation = store.$preferences.dropFirst().sink { values.append($0) }
            defer { observation.cancel() }
            store.reload()
            XCTAssertTrue(store.set(\.holdRate, 2))
            store.reload()
            XCTAssertTrue(values.isEmpty)
            XCTAssertTrue(store.set(\.seekStep, 30))
            store.reload()
            XCTAssertEqual(values.count, 1)
        }
    }

    private func isolated(_ body: (UserDefaults) throws -> Void) rethrows {
        let suite = "meowfilm.tests.film-player." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        try body(defaults)
    }

    func testAllControlDefaultsWithoutWritingPreferences() {
        isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            let value = store.preferences
            XCTAssertEqual(value.engine, .ksPlayer)
            XCTAssertEqual(value.defaultRate, 1)
            XCTAssertEqual(value.holdRate, 2)
            XCTAssertEqual(value.seekStep, 15)
            XCTAssertTrue(value.showsControlCenter)
            XCTAssertTrue(value.recognizesChapters)
            XCTAssertEqual(value.singleTapCenter, .toggleControls)
            XCTAssertEqual(value.singleTapSides, .toggleControls)
            XCTAssertEqual(value.doubleTapCenter, .playPause)
            XCTAssertEqual(value.doubleTapSides, .playPause)
            XCTAssertTrue(value.swipeVolume)
            XCTAssertTrue(value.swipeBrightness)
            XCTAssertTrue(value.swipeSeek)
            XCTAssertTrue(value.swipeEpisodes)
            XCTAssertTrue(value.hapticHold)
            XCTAssertTrue(value.hapticBrightness)
            XCTAssertTrue(value.hapticVolume)
            XCTAssertTrue(value.hapticSeek)
            XCTAssertNil(store.persistenceError)
            XCTAssertNil(defaults.object(forKey: FilmPlayerPreferencesStore.preferencesKey))
        }
    }

    func testChoicesIntersectTheSelectedEngineCapability() {
        XCTAssertEqual(FilmPlayerPreferences.defaultRates(for: .ksPlayer), [1, 1.25, 1.5, 2, 3])
        XCTAssertEqual(FilmPlayerPreferences.defaultRates(for: .aether), [1, 1.25, 1.5, 2])
        XCTAssertEqual(FilmPlayerPreferences.holdRates(for: .ksPlayer), [2, 3, 4, 6, 8])
        XCTAssertEqual(FilmPlayerPreferences.holdRates(for: .aether), [2])
        XCTAssertEqual(FilmPlayerPreferences.seekSteps, [5, 10, 15, 20, 30, 60])
    }

    func testEveryControlFieldPersistsAndReopens() throws {
        try isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertTrue(store.set(\.defaultRate, 1.25))
            XCTAssertTrue(store.set(\.holdRate, 8))
            XCTAssertTrue(store.set(\.seekStep, 60))
            XCTAssertTrue(store.set(\.singleTapCenter, .none))
            XCTAssertTrue(store.set(\.singleTapSides, .seek))
            XCTAssertTrue(store.set(\.doubleTapCenter, .none))
            XCTAssertTrue(store.set(\.doubleTapSides, .seek))
            let switches: [WritableKeyPath<FilmPlayerPreferences, Bool>] = [
                \.showsControlCenter, \.recognizesChapters, \.swipeVolume, \.swipeBrightness,
                \.swipeSeek, \.swipeEpisodes, \.hapticHold, \.hapticBrightness, \.hapticVolume, \.hapticSeek,
            ]
            for keyPath in switches { XCTAssertTrue(store.set(keyPath, false)) }
            let saved = store.preferences
            let reopened = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertEqual(reopened.preferences, saved)
            let bytes = try XCTUnwrap(defaults.data(forKey: FilmPlayerPreferencesStore.preferencesKey))
            XCTAssertEqual(try JSONDecoder().decode(FilmPlayerPreferences.self, from: bytes), saved)
            XCTAssertNil(reopened.persistenceError)
        }
    }

    func testSwitchingToAetherPersistsBothCorrectionsAndDoesNotReviveHighRates() {
        isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            for holdRate in [3.0, 4, 6, 8] {
                XCTAssertTrue(store.set(\.engine, .ksPlayer))
                XCTAssertTrue(store.set(\.defaultRate, 3))
                XCTAssertTrue(store.set(\.holdRate, holdRate))
                XCTAssertTrue(store.set(\.engine, .aether))
                XCTAssertEqual(store.preferences.defaultRate, 2)
                XCTAssertEqual(store.preferences.holdRate, 2)
                let reopened = FilmPlayerPreferencesStore(defaults: defaults)
                XCTAssertEqual(reopened.preferences.engine, .aether)
                XCTAssertEqual(reopened.preferences.defaultRate, 2)
                XCTAssertEqual(reopened.preferences.holdRate, 2)
                XCTAssertTrue(reopened.set(\.engine, .ksPlayer))
                XCTAssertEqual(reopened.preferences.defaultRate, 2)
                XCTAssertEqual(reopened.preferences.holdRate, 2)
            }
        }
    }

    func testSwitchingEnginePreservesAlreadySupportedRatesAndOtherControls() {
        isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            for rate in [1.0, 1.25, 1.5, 2] {
                XCTAssertTrue(store.set(\.engine, .ksPlayer))
                XCTAssertTrue(store.set(\.defaultRate, rate))
                XCTAssertTrue(store.set(\.seekStep, 30))
                XCTAssertTrue(store.set(\.showsControlCenter, false))
                XCTAssertTrue(store.set(\.engine, .aether))
                XCTAssertEqual(store.preferences.defaultRate, rate)
                XCTAssertEqual(store.preferences.seekStep, 30)
                XCTAssertFalse(store.preferences.showsControlCenter)
            }
        }
    }

    func testOldHighRateCommandsCannotBypassAetherLimit() {
        isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertTrue(store.set(\.engine, .aether))
            XCTAssertTrue(store.set(\.defaultRate, 3))
            XCTAssertTrue(store.set(\.holdRate, 8))
            XCTAssertEqual(store.preferences.defaultRate, 2)
            XCTAssertEqual(store.preferences.holdRate, 2)
            XCTAssertEqual(FilmPlayerPreferencesStore(defaults: defaults).preferences, store.preferences)
        }
    }

    func testReloadNormalizesValidSavedAetherRatesAndPersistsTheCorrection() throws {
        try isolated { defaults in
            var value = FilmPlayerPreferences()
            value.engine = .aether; value.defaultRate = 3; value.holdRate = 6
            defaults.set(try JSONEncoder().encode(value), forKey: FilmPlayerPreferencesStore.preferencesKey)
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertNil(store.persistenceError)
            XCTAssertEqual(store.preferences.defaultRate, 2)
            XCTAssertEqual(store.preferences.holdRate, 2)
            let bytes = try XCTUnwrap(defaults.data(forKey: FilmPlayerPreferencesStore.preferencesKey))
            XCTAssertEqual(try JSONDecoder().decode(FilmPlayerPreferences.self, from: bytes), store.preferences)
        }
    }

    func testInvalidProductChoicesDoNotChangeSavedOrPublishedPreferences() throws {
        try isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertTrue(store.set(\.seekStep, 20))
            let before = store.preferences
            let bytes = try XCTUnwrap(defaults.data(forKey: FilmPlayerPreferencesStore.preferencesKey))
            for rate in [0.0, -1, 2.5, 4, .infinity, .nan] {
                XCTAssertFalse(store.set(\.defaultRate, rate))
                XCTAssertEqual(store.preferences, before)
                XCTAssertNotNil(store.persistenceError)
                XCTAssertEqual(defaults.data(forKey: FilmPlayerPreferencesStore.preferencesKey), bytes)
            }
            for rate in [1.0, 1.25, 5, 7, 9, .infinity, .nan] {
                XCTAssertFalse(store.set(\.holdRate, rate))
                XCTAssertEqual(store.preferences, before)
            }
            XCTAssertFalse(store.set(\.seekStep, 0))
            XCTAssertFalse(store.set(\.seekStep, 25))
            XCTAssertFalse(store.set(\.singleTapCenter, .seek))
            XCTAssertFalse(store.set(\.doubleTapCenter, .seek))
            XCTAssertFalse(store.set(\.doubleTapCenter, .toggleControls))
            XCTAssertFalse(store.set(\.doubleTapSides, .toggleControls))
            XCTAssertEqual(defaults.data(forKey: FilmPlayerPreferencesStore.preferencesKey), bytes)
            store.reload()
            XCTAssertNil(store.persistenceError)
            XCTAssertEqual(store.preferences, before)
        }
    }

    func testEveryAllowedTapActionCanBeSaved() {
        isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            for action in FilmPlayerTapAction.allCases {
                XCTAssertTrue(store.set(\.singleTapSides, action))
                XCTAssertEqual(store.preferences.singleTapSides, action)
                if action != .seek {
                    XCTAssertTrue(store.set(\.singleTapCenter, action))
                    XCTAssertEqual(store.preferences.singleTapCenter, action)
                }
                if action != .toggleControls {
                    XCTAssertTrue(store.set(\.doubleTapSides, action))
                    XCTAssertEqual(store.preferences.doubleTapSides, action)
                    if action != .seek {
                        XCTAssertTrue(store.set(\.doubleTapCenter, action))
                        XCTAssertEqual(store.preferences.doubleTapCenter, action)
                    }
                }
            }
        }
    }

    func testCorruptStorageIsReportedAndNeverOverwrittenByEdits() throws {
        try isolated { defaults in
            let store = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertTrue(store.set(\.seekStep, 30))
            let before = store.preferences
            var invalid = FilmPlayerPreferences()
            invalid.seekStep = 25
            let corruptValues: [Any] = [
                "not data", Data("not json".utf8), Data("{}".utf8), Data("[]".utf8),
                try JSONEncoder().encode(invalid),
            ]
            for corrupt in corruptValues {
                defaults.set(corrupt, forKey: FilmPlayerPreferencesStore.preferencesKey)
                store.reload()
                XCTAssertNotNil(store.persistenceError)
                XCTAssertEqual(store.preferences, before)
                XCTAssertFalse(store.set(\.engine, .aether))
                XCTAssertEqual(defaults.object(forKey: FilmPlayerPreferencesStore.preferencesKey) as? NSObject,
                               corrupt as? NSObject)
                let reopened = FilmPlayerPreferencesStore(defaults: defaults)
                XCTAssertNotNil(reopened.persistenceError)
                XCTAssertFalse(reopened.set(\.defaultRate, 2))
            }
        }
    }

    func testFieldEditsRebaseWithoutLosingAnUnrelatedOwnersEdit() {
        isolated { defaults in
            let first = FilmPlayerPreferencesStore(defaults: defaults)
            let second = FilmPlayerPreferencesStore(defaults: defaults)
            XCTAssertTrue(first.set(\.showsControlCenter, false))
            XCTAssertTrue(second.set(\.seekStep, 30))
            XCTAssertTrue(first.set(\.holdRate, 8))
            XCTAssertTrue(second.set(\.engine, .aether))
            first.reload()
            XCTAssertFalse(first.preferences.showsControlCenter)
            XCTAssertEqual(first.preferences.seekStep, 30)
            XCTAssertEqual(first.preferences.engine, .aether)
            XCTAssertEqual(first.preferences.holdRate, 2)
        }
    }

    func testSuitesAndUnrelatedSettingsRemainIsolated() {
        isolated { firstDefaults in
            isolated { secondDefaults in
                firstDefaults.set("preserved", forKey: "film.unrelated")
                let first = FilmPlayerPreferencesStore(defaults: firstDefaults)
                let second = FilmPlayerPreferencesStore(defaults: secondDefaults)
                XCTAssertTrue(first.set(\.engine, .aether))
                XCTAssertTrue(first.set(\.seekStep, 60))
                second.reload()
                XCTAssertEqual(second.preferences, FilmPlayerPreferences())
                XCTAssertNil(secondDefaults.object(forKey: FilmPlayerPreferencesStore.preferencesKey))
                XCTAssertEqual(firstDefaults.string(forKey: "film.unrelated"), "preserved")
                firstDefaults.removeObject(forKey: FilmPlayerPreferencesStore.preferencesKey)
                first.reload()
                XCTAssertEqual(first.preferences, FilmPlayerPreferences())
                XCTAssertNil(first.persistenceError)
            }
        }
    }
}
