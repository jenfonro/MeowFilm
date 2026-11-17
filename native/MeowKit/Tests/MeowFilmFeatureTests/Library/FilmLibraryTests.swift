import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

@MainActor
final class FilmLibraryTests: XCTestCase {
    func testFavoritesSaveAndRestoreTheSuppliedTarget() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let target = FilmPlaybackTarget(id: "node-content", kind: .tv, title: "影片", metadataProvider: .tmdb, metadataID: "19")
        let item = FilmItem(sourceID: "tmdb", id: "19", title: "影片", kind: .tv,
                            businessID: "node-content", playbackTarget: target)
        let library = FilmLibrary(defaults: defaults)
        XCTAssertFalse(library.contains(item))
        XCTAssertTrue(try library.toggle(item))
        let restored = FilmLibrary(defaults: defaults)
        let record = try XCTUnwrap(restored.favorites.first)
        XCTAssertEqual(record.businessID, item.businessID)
        XCTAssertEqual(record.playbackTarget, target)
    }

    func testFavoritesPersistWithSourceAndItemAsSeparateIdentityFields() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("preserved", forKey: "unrelated-setting")
        let first = item(sourceID: "first", id: "100", title: "第一来源")
        let second = item(sourceID: "second", id: "100", title: "第二来源")
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

        let delimitedSource = item(sourceID: "a:b", id: "c", title: "不同身份一")
        let delimitedItem = item(sourceID: "a", id: "b:c", title: "不同身份二")
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
        let item = item(sourceID: "douban", id: "19", title: "影片", kind: .movie)
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
        let movie = item(sourceID: "tmdb", id: "19", title: "电影", kind: .movie)
        let tv = item(sourceID: "tmdb", id: "19", title: "剧集", kind: .tv)
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

    func testFavoriteActionRequiresTheSuppliedTargetToMatchItsBusinessIdentity() throws {
        let suite = "MeowFilmFeatureTests." + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let library = FilmLibrary(defaults: defaults)
        for target: FilmPlaybackTarget? in [nil, .init(id: "other", kind: .movie, title: "影片")] {
            let item = FilmItem(sourceID: "douban", id: "19", title: "影片", businessID: "selected", playbackTarget: target)
            XCTAssertThrowsError(try library.toggle(item)) { XCTAssertEqual($0 as? FilmLibraryError, .missingTarget) }
        }
        XCTAssertNil(defaults.object(forKey: FilmLibrary.storageKey))
        XCTAssertTrue(library.favorites.isEmpty)
    }

    private func item(sourceID: String, id: String, title: String, kind: FilmItemKind? = nil) -> FilmItem {
        let businessID = sourceID + ":" + (kind?.rawValue ?? "") + ":" + id
        let target = FilmPlaybackTarget(id: businessID, kind: kind == .tv ? .tv : .movie, title: title)
        return FilmItem(sourceID: sourceID, id: id, title: title, kind: kind, businessID: businessID, playbackTarget: target)
    }

    func testHealthyFavoritesSurviveUnreadableSiblingAndKeepExactMaterialWhenToggled() throws {
        let defaults = try XCTUnwrap(UserDefaults(suiteName: UUID().uuidString))
        defer { defaults.removeObject(forKey: FilmLibrary.storageKey) }
        let a = item(sourceID: "a", id: "1", title: "A"), b = item(sourceID: "b", id: "2", title: "B")
        let opaque = try RuntimeJSON.decode(Data(#"{"broken":true,"n":123456789012345678901234567890,"é":1,"e\u0301":2}"#.utf8))
        let healthy = try RuntimeJSON.decode(JSONEncoder().encode(a.identity))
        defaults.set(try RuntimeJSON.encode(.array([opaque, healthy])), forKey: FilmLibrary.storageKey)
        let library = FilmLibrary(defaults: defaults)
        XCTAssertNil(library.loadError); XCTAssertTrue(library.contains(a))
        XCTAssertTrue(try library.toggle(b)); XCTAssertFalse(try library.toggle(a))
        let raw = try RuntimeJSON.decode(XCTUnwrap(defaults.data(forKey: FilmLibrary.storageKey)))
        guard case .array(let rows) = raw else { return XCTFail("array") }
        XCTAssertEqual(rows.first, opaque)
        XCTAssertTrue(FilmLibrary(defaults: defaults).contains(b))
    }

}
