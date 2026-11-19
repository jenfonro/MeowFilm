import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
import Combine
@testable import MeowFilmFeature

@MainActor
final class FilmPlayerPreferencesTests: XCTestCase {
    func testReloadAndSameValueDoNotPublishDuplicatePreferences() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            var values: [FilmPlayerPreferences] = []
            let observation = store.$preferences.dropFirst().sink { values.append($0) }
            defer { observation.cancel() }
            await store.reload()
            let unchanged = await store.set(\.holdRate, 2); XCTAssertTrue(unchanged)
            await store.reload()
            XCTAssertTrue(values.isEmpty)
            let changed = await store.set(\.seekStep, 30); XCTAssertTrue(changed)
            await store.reload()
            XCTAssertEqual(values.count, 1)
        }
    }

    private func isolated(_ body: (SQLiteDatabase) async throws -> Void) async throws {
        try await body(storageTestDatabase(self))
    }
    private func makeStore(_ database: SQLiteDatabase) async -> FilmPlayerPreferencesStore {
        let store = FilmPlayerPreferencesStore(database: database)
        await store.reload()
        return store
    }
    private func rows(_ database: SQLiteDatabase) async throws -> [SQLiteRow] {
        try await database.snapshot { try $0.query("SELECT * FROM ios_film_player_preferences ORDER BY singleton") }
    }

    func testAllControlDefaultsWithoutWritingPreferences() async throws {
        try await isolated { database in
            let store = await makeStore(database)
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
            storageTestEqual(try await rows(database), [])
        }
    }

    func testChoicesIntersectTheSelectedEngineCapability() async throws {
        XCTAssertEqual(FilmPlayerPreferences.defaultRates(for: .ksPlayer), [1, 1.25, 1.5, 2, 3])
        XCTAssertEqual(FilmPlayerPreferences.defaultRates(for: .aether), [1, 1.25, 1.5, 2])
        XCTAssertEqual(FilmPlayerPreferences.holdRates(for: .ksPlayer), [2, 3, 4, 6, 8])
        XCTAssertEqual(FilmPlayerPreferences.holdRates(for: .aether), [2])
        XCTAssertEqual(FilmPlayerPreferences.seekSteps, [5, 10, 15, 20, 30, 60])
    }

    func testEveryControlFieldPersistsAndReopens() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            storageTestTrue(await store.set(\.defaultRate, 1.25))
            storageTestTrue(await store.set(\.holdRate, 8))
            storageTestTrue(await store.set(\.seekStep, 60))
            storageTestTrue(await store.set(\.singleTapCenter, .none))
            storageTestTrue(await store.set(\.singleTapSides, .seek))
            storageTestTrue(await store.set(\.doubleTapCenter, .none))
            storageTestTrue(await store.set(\.doubleTapSides, .seek))
            let switches: [WritableKeyPath<FilmPlayerPreferences, Bool>] = [
                \.showsControlCenter, \.recognizesChapters, \.swipeVolume, \.swipeBrightness,
                \.swipeSeek, \.swipeEpisodes, \.hapticHold, \.hapticBrightness, \.hapticVolume, \.hapticSeek,
            ]
            for keyPath in switches { storageTestTrue(await store.set(keyPath, false)) }
            let saved = store.preferences
            let reopened = await makeStore(database)
            XCTAssertEqual(reopened.preferences, saved)
            let bytes = try await rows(database)
            XCTAssertEqual(bytes.count, 1)
            XCTAssertEqual(try bytes[0].double("seekStep"), saved.seekStep)
            XCTAssertNil(reopened.persistenceError)
        }
    }

    func testSwitchingToAetherPersistsBothCorrectionsAndDoesNotReviveHighRates() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            for holdRate in [3.0, 4, 6, 8] {
                storageTestTrue(await store.set(\.engine, .ksPlayer))
                storageTestTrue(await store.set(\.defaultRate, 3))
                storageTestTrue(await store.set(\.holdRate, holdRate))
                storageTestTrue(await store.set(\.engine, .aether))
                XCTAssertEqual(store.preferences.defaultRate, 2)
                XCTAssertEqual(store.preferences.holdRate, 2)
                let reopened = await makeStore(database)
                XCTAssertEqual(reopened.preferences.engine, .aether)
                XCTAssertEqual(reopened.preferences.defaultRate, 2)
                XCTAssertEqual(reopened.preferences.holdRate, 2)
                storageTestTrue(await reopened.set(\.engine, .ksPlayer))
                XCTAssertEqual(reopened.preferences.defaultRate, 2)
                XCTAssertEqual(reopened.preferences.holdRate, 2)
            }
        }
    }

    func testSwitchingEnginePreservesAlreadySupportedRatesAndOtherControls() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            for rate in [1.0, 1.25, 1.5, 2] {
                storageTestTrue(await store.set(\.engine, .ksPlayer))
                storageTestTrue(await store.set(\.defaultRate, rate))
                storageTestTrue(await store.set(\.seekStep, 30))
                storageTestTrue(await store.set(\.showsControlCenter, false))
                storageTestTrue(await store.set(\.engine, .aether))
                XCTAssertEqual(store.preferences.defaultRate, rate)
                XCTAssertEqual(store.preferences.seekStep, 30)
                XCTAssertFalse(store.preferences.showsControlCenter)
            }
        }
    }

    func testOldHighRateCommandsCannotBypassAetherLimit() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            storageTestTrue(await store.set(\.engine, .aether))
            storageTestTrue(await store.set(\.defaultRate, 3))
            storageTestTrue(await store.set(\.holdRate, 8))
            XCTAssertEqual(store.preferences.defaultRate, 2)
            XCTAssertEqual(store.preferences.holdRate, 2)
            storageTestEqual(await makeStore(database).preferences, store.preferences)
        }
    }

    func testReloadProjectsCurrentEngineRateLimitsWithoutRewritingRows() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            storageTestTrue(await store.set(\.engine, .aether))
            _ = try await database.transaction { try $0.execute("UPDATE ios_film_player_preferences SET defaultRate=3,holdRate=6") }
            let before = try await rows(database)
            await store.reload()
            XCTAssertNil(store.persistenceError)
            XCTAssertEqual(store.preferences.defaultRate, 2)
            XCTAssertEqual(store.preferences.holdRate, 2)
            storageTestEqual(try await rows(database), before)
        }
    }

    func testInvalidProductChoicesDoNotChangeSavedOrPublishedPreferences() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            storageTestTrue(await store.set(\.seekStep, 20))
            let before = store.preferences
            let bytes = try await rows(database)
            for rate in [0.0, -1, 2.5, 4, .infinity, .nan] {
                storageTestFalse(await store.set(\.defaultRate, rate))
                XCTAssertEqual(store.preferences, before)
                XCTAssertNotNil(store.persistenceError)
                storageTestEqual(try await rows(database), bytes)
            }
            for rate in [1.0, 1.25, 5, 7, 9, .infinity, .nan] {
                storageTestFalse(await store.set(\.holdRate, rate))
                XCTAssertEqual(store.preferences, before)
            }
            storageTestFalse(await store.set(\.seekStep, 0))
            storageTestFalse(await store.set(\.seekStep, 25))
            storageTestFalse(await store.set(\.singleTapCenter, .seek))
            storageTestFalse(await store.set(\.doubleTapCenter, .seek))
            storageTestFalse(await store.set(\.doubleTapCenter, .toggleControls))
            storageTestFalse(await store.set(\.doubleTapSides, .toggleControls))
            storageTestEqual(try await rows(database), bytes)
            await store.reload()
            XCTAssertNil(store.persistenceError)
            XCTAssertEqual(store.preferences, before)
        }
    }

    func testEveryAllowedTapActionCanBeSaved() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            for action in FilmPlayerTapAction.allCases {
                storageTestTrue(await store.set(\.singleTapSides, action))
                XCTAssertEqual(store.preferences.singleTapSides, action)
                if action != .seek {
                    storageTestTrue(await store.set(\.singleTapCenter, action))
                    XCTAssertEqual(store.preferences.singleTapCenter, action)
                }
                if action != .toggleControls {
                    storageTestTrue(await store.set(\.doubleTapSides, action))
                    XCTAssertEqual(store.preferences.doubleTapSides, action)
                    if action != .seek {
                        storageTestTrue(await store.set(\.doubleTapCenter, action))
                        XCTAssertEqual(store.preferences.doubleTapCenter, action)
                    }
                }
            }
        }
    }

    func testCorruptStorageIsReportedAndNeverOverwrittenByEdits() async throws {
        try await isolated { database in
            let store = await makeStore(database)
            storageTestTrue(await store.set(\.seekStep, 30))
            let before = store.preferences
            for corrupt in ["seekStep=25", "engine='invalid'", "defaultRate=2.5"] {
                _ = try await database.transaction { try $0.execute("UPDATE ios_film_player_preferences SET " + corrupt) }
                let rowsBefore = try await rows(database)
                await store.reload()
                XCTAssertNotNil(store.persistenceError)
                XCTAssertEqual(store.preferences, before)
                storageTestFalse(await store.set(\.engine, .aether))
                storageTestEqual(try await rows(database), rowsBefore)
                let reopened = await makeStore(database)
                XCTAssertNotNil(reopened.persistenceError)
                storageTestFalse(await reopened.set(\.defaultRate, 2))
                _ = try await database.transaction { try $0.execute("UPDATE ios_film_player_preferences SET seekStep=30,engine='ksplayer',defaultRate=1") }
            }
        }
    }

    func testFieldEditsRebaseWithoutLosingAnUnrelatedOwnersEdit() async throws {
        try await isolated { database in
            let first = await makeStore(database)
            let second = await makeStore(database)
            storageTestTrue(await first.set(\.showsControlCenter, false))
            storageTestTrue(await second.set(\.seekStep, 30))
            storageTestTrue(await first.set(\.holdRate, 8))
            storageTestTrue(await second.set(\.engine, .aether))
            await first.reload()
            XCTAssertFalse(first.preferences.showsControlCenter)
            XCTAssertEqual(first.preferences.seekStep, 30)
            XCTAssertEqual(first.preferences.engine, .aether)
            XCTAssertEqual(first.preferences.holdRate, 2)
        }
    }

    func testDataRootsAndUnrelatedOwnerRowsRemainIsolated() async throws {
        let firstDatabase = try storageTestDatabase(self), secondDatabase = try storageTestDatabase(self)
        _ = try await firstDatabase.transaction { try $0.execute("INSERT INTO ios_search_history(text,used_at) VALUES('preserved',1)") }
        let first = await makeStore(firstDatabase), second = await makeStore(secondDatabase)
        storageTestTrue(await first.set(\.engine, .aether))
        storageTestTrue(await first.set(\.seekStep, 60))
        await second.reload()
        XCTAssertEqual(second.preferences, FilmPlayerPreferences())
        storageTestEqual(try await rows(secondDatabase), [])
        _ = try await firstDatabase.transaction { try $0.execute("DELETE FROM ios_film_player_preferences") }
        await first.reload()
        XCTAssertEqual(first.preferences, FilmPlayerPreferences())
        XCTAssertNil(first.persistenceError)
        let history = try await firstDatabase.snapshot { try $0.query("SELECT text FROM ios_search_history").map { try $0.text("text") } }
        XCTAssertEqual(history, ["preserved"])
    }
}
