import Foundation
import XCTest
@testable import MeowFilmFeature

@MainActor
final class FilmLibraryTests: XCTestCase {
    func testFavoritesPersistWithSourceAndItemAsSeparateIdentityFields() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("preserved", forKey: "unrelated-setting")
        let first = FilmItem(sourceID: "first", id: "100", title: "第一来源")
        let second = FilmItem(sourceID: "second", id: "100", title: "第二来源")
        let library = FilmLibrary(defaults: defaults)
        XCTAssertFalse(library.contains(first))
        XCTAssertTrue(try library.toggle(first))
        XCTAssertTrue(library.contains(first))
        XCTAssertFalse(library.contains(second))

        let restored = FilmLibrary(defaults: defaults)
        XCTAssertTrue(restored.contains(first))
        XCTAssertFalse(restored.contains(second))
        XCTAssertTrue(try restored.toggle(second))
        XCTAssertFalse(try restored.toggle(first))

        let rebuilt = FilmLibrary(defaults: defaults)
        XCTAssertFalse(rebuilt.contains(first))
        XCTAssertTrue(rebuilt.contains(second))
        XCTAssertEqual(defaults.string(forKey: "unrelated-setting"), "preserved")

        let delimitedSource = FilmItem(sourceID: "a:b", id: "c", title: "不同身份一")
        let delimitedItem = FilmItem(sourceID: "a", id: "b:c", title: "不同身份二")
        XCTAssertTrue(try rebuilt.toggle(delimitedSource))
        XCTAssertFalse(rebuilt.contains(delimitedItem))
        XCTAssertTrue(try rebuilt.toggle(delimitedItem))
        XCTAssertEqual(FilmLibrary(defaults: defaults).favorites.count, 3)
    }

    func testUnreadableFavoriteDataDoesNotCrashOrEraseOtherSettings() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let bytes = Data("invalid saved data".utf8)
        defaults.set(bytes, forKey: FilmLibrary.storageKey)
        defaults.set("preserved", forKey: "unrelated-setting")
        let library = FilmLibrary(defaults: defaults)
        XCTAssertTrue(library.favorites.isEmpty)
        XCTAssertEqual(library.loadError, .corruptedData)
        XCTAssertEqual(library.loadError?.localizedDescription, "收藏数据已损坏，原数据已保留，无法更改收藏。")
        let item = FilmItem(sourceID: "first", id: "100", title: "影片")
        for _ in 0..<2 {
            XCTAssertThrowsError(try library.toggle(item)) { error in
                XCTAssertEqual(error as? FilmLibraryError, .corruptedData)
            }
            XCTAssertFalse(library.contains(item))
            XCTAssertTrue(library.favorites.isEmpty)
            XCTAssertEqual(defaults.data(forKey: FilmLibrary.storageKey), bytes)
        }
        let restored = FilmLibrary(defaults: defaults)
        XCTAssertEqual(restored.loadError, .corruptedData)
        XCTAssertFalse(restored.contains(item))
        XCTAssertEqual(defaults.string(forKey: "unrelated-setting"), "preserved")
    }

    func testMissingFavoriteDataIsAnEmptyHealthyLibrary() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let library = FilmLibrary(defaults: defaults)
        XCTAssertTrue(library.favorites.isEmpty)
        XCTAssertNil(library.loadError)
        XCTAssertNil(defaults.object(forKey: FilmLibrary.storageKey))
        let item = FilmItem(sourceID: "douban", id: "19", title: "影片", kind: .movie)
        XCTAssertTrue(try library.toggle(item))
        let restored = FilmLibrary(defaults: defaults)
        XCTAssertNil(restored.loadError)
        XCTAssertTrue(restored.contains(item))
        XCTAssertFalse(try restored.toggle(item))
        let empty = FilmLibrary(defaults: defaults)
        XCTAssertTrue(empty.favorites.isEmpty)
        XCTAssertNil(empty.loadError)
    }

    func testWrongStoredValueTypeIsReportedAndNeverReplaced() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("not saved data", forKey: FilmLibrary.storageKey)
        let library = FilmLibrary(defaults: defaults)
        XCTAssertEqual(library.loadError, .corruptedData)
        let item = FilmItem(sourceID: "douban", id: "19", title: "影片")
        XCTAssertThrowsError(try library.toggle(item)) { error in
            XCTAssertEqual(error as? FilmLibraryError, .corruptedData)
        }
        XCTAssertEqual(defaults.string(forKey: FilmLibrary.storageKey), "not saved data")
        XCTAssertFalse(library.contains(item))
    }

    func testTMDBMovieAndTVWithSameIDAreIndependentAcrossRestorationAndRemoval() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let movie = FilmItem(sourceID: "tmdb", id: "19", title: "电影", kind: .movie)
        let tv = FilmItem(sourceID: "tmdb", id: "19", title: "剧集", kind: .tv)
        let library = FilmLibrary(defaults: defaults)
        XCTAssertTrue(try library.toggle(movie))
        XCTAssertTrue(library.contains(movie))
        XCTAssertFalse(library.contains(tv))
        XCTAssertTrue(try library.toggle(tv))
        XCTAssertEqual(library.favorites.count, 2)
        let restored = FilmLibrary(defaults: defaults)
        XCTAssertNil(restored.loadError)
        XCTAssertTrue(restored.contains(movie))
        XCTAssertTrue(restored.contains(tv))
        XCTAssertFalse(try restored.toggle(movie))
        let rebuilt = FilmLibrary(defaults: defaults)
        XCTAssertFalse(rebuilt.contains(movie))
        XCTAssertTrue(rebuilt.contains(tv))
        XCTAssertFalse(try rebuilt.toggle(tv))
        XCTAssertTrue(FilmLibrary(defaults: defaults).favorites.isEmpty)
    }

    func testLegacyDoubanMatchesButUntypedTMDBIsNotGuessedOrMigrated() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let bytes = Data(#"[{"sourceID":"douban","itemID":"19"},{"sourceID":"tmdb","itemID":"19"}]"#.utf8)
        defaults.set(bytes, forKey: FilmLibrary.storageKey)
        let library = FilmLibrary(defaults: defaults)
        let legacy = FilmItem(sourceID: "tmdb", id: "19", title: "旧收藏")
        let movie = FilmItem(sourceID: "tmdb", id: "19", title: "电影", kind: .movie)
        let tv = FilmItem(sourceID: "tmdb", id: "19", title: "剧集", kind: .tv)
        let douban = FilmItem(sourceID: "douban", id: "19", title: "旧豆瓣", kind: .tv)
        XCTAssertNil(library.loadError)
        XCTAssertEqual(library.favorites.count, 2)
        XCTAssertTrue(library.contains(douban))
        XCTAssertTrue(library.contains(legacy))
        XCTAssertFalse(library.contains(movie))
        XCTAssertFalse(library.contains(tv))
        XCTAssertThrowsError(try library.toggle(legacy)) { error in
            XCTAssertEqual(error as? FilmLibraryError, .missingKind)
        }
        XCTAssertEqual(defaults.data(forKey: FilmLibrary.storageKey), bytes)
        XCTAssertTrue(try library.toggle(movie))
        XCTAssertTrue(try library.toggle(tv))
        let restored = FilmLibrary(defaults: defaults)
        XCTAssertNil(restored.loadError)
        XCTAssertEqual(restored.favorites, [legacy.identity, movie.identity, tv.identity, douban.identity])
        XCTAssertFalse(try restored.toggle(movie))
        XCTAssertFalse(try restored.toggle(douban))
        let rebuilt = FilmLibrary(defaults: defaults)
        XCTAssertEqual(rebuilt.favorites, [legacy.identity, tv.identity])
        XCTAssertFalse(rebuilt.contains(movie))
    }

    func testNewUntypedTMDBFavoriteFailsWithoutWritingOrClaimingSuccess() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let library = FilmLibrary(defaults: defaults)
        let item = FilmItem(sourceID: "tmdb", id: "19", title: "电影或剧集")
        XCTAssertThrowsError(try library.toggle(item)) { error in
            XCTAssertEqual(error as? FilmLibraryError, .missingKind)
        }
        XCTAssertNil(library.loadError)
        XCTAssertFalse(library.contains(item))
        XCTAssertTrue(library.favorites.isEmpty)
        XCTAssertNil(defaults.object(forKey: FilmLibrary.storageKey))
    }
}
